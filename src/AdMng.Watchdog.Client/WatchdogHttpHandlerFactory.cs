using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AdMng.Watchdog.Client;

/// <summary>
/// The HTTP handler of <see cref="WatchdogHttpClient"/> (spec 5.5): Negotiate credentials bound to the exact origin of
/// <see cref="WatchdogClientOptions.BaseAddress"/> (.NET has no intranet-zone logic – default credentials alone would
/// answer any host's Negotiate/NTLM challenge), no pre-authentication, no redirects, no cookies, no proxy, pooled
/// connections recycled every 5 minutes, TLS 1.2/1.3 with system validation or – when that fails – the optional
/// SHA-256 pin of the server certificate.
/// </summary>
public static class WatchdogHttpHandlerFactory
{
    /// <summary>The handler for <paramref name="options"/>.</summary>
    /// <exception cref="ArgumentException">The base address is not https (or loopback http with
    /// <see cref="WatchdogClientOptions.AllowInsecureLoopback"/>).</exception>
    public static SocketsHttpHandler Create(WatchdogClientOptions options)
    {
        Validate(options);
        var origin = Origin(options.BaseAddress);
        var pin = NormalizePin(options.PinnedCertificateSha256);
        return new SocketsHttpHandler
        {
            Credentials = new CredentialCache { { origin, "Negotiate", options.Credential ?? CredentialCache.DefaultNetworkCredentials } },
            PreAuthenticate = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            SslOptions =
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, certificate, _, errors) => IsTrusted(certificate, errors, pin),
            },
        };
    }

    /// <summary>Checks the base address (https, or http on loopback when explicitly allowed).</summary>
    /// <exception cref="ArgumentException">The address is not acceptable.</exception>
    public static void Validate(WatchdogClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var address = options.BaseAddress ?? throw new ArgumentException("A base address is required.", nameof(options));
        if (!address.IsAbsoluteUri || !string.IsNullOrEmpty(address.UserInfo) || !string.IsNullOrEmpty(address.Query) || !string.IsNullOrEmpty(address.Fragment))
        {
            throw new ArgumentException("The watchdog address must be an absolute https URL without user info, query or fragment.", nameof(options));
        }

        if (address.Scheme == Uri.UriSchemeHttps)
        {
            return;
        }

        if (address.Scheme == Uri.UriSchemeHttp && options.AllowInsecureLoopback && address.IsLoopback)
        {
            return;
        }

        throw new ArgumentException("The watchdog address must use https.", nameof(options));
    }

    /// <summary><c>scheme://host:port/</c> – the only prefix the credentials are offered to.</summary>
    public static Uri Origin(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return new Uri(address.GetLeftPart(UriPartial.Authority) + "/");
    }

    /// <summary>System validation, or the leaf certificate's SHA-256 equals the pin.</summary>
    internal static bool IsTrusted(X509Certificate? certificate, SslPolicyErrors errors, byte[]? pin)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        if (pin is null || certificate is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), pin);
    }

    private static byte[]? NormalizePin(string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            return null;
        }

        var hex = new string([.. pin.Where(char.IsAsciiHexDigit)]);
        return hex.Length == 64 ? Convert.FromHexString(hex) : throw new ArgumentException("The certificate pin must be a SHA-256 hash (64 hex digits).", nameof(pin));
    }
}
