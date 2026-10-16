using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Formatting.Webhooks;

/// <summary>
/// Standard Webhooks v1 signatures (HMAC-SHA256) of the generic webhook channel (section 14.5), the optional
/// GitHub-style <c>X-Hub-Signature-256</c>, signing-key parsing and secret generation.
/// </summary>
/// <remarks>
/// <para><c>webhook-signature: v1,&lt;base64 HMAC-SHA256(key, id + "." + timestamp + "." + body)&gt;</c>; during key rotation
/// a second, space-separated <c>v1,…</c> signature with the previous key follows. The key is the base64-decoded part of a
/// <c>whsec_…</c> secret (24–64 bytes). The transport computes the headers <b>per attempt</b> (fresh timestamp, stable
/// <c>webhook-id</c>) over the exact body bytes it sends.</para>
/// <para><c>X-Hub-Signature-256: sha256=&lt;hex HMAC-SHA256(secret, body)&gt;</c> uses the UTF-8 bytes of the whole secret text
/// as entered (what a GitHub-style receiver configured with the same secret computes).</para>
/// <para>Secrets and keys never appear in exceptions; key buffers are zeroed after use.</para>
/// </remarks>
public static class StandardWebhookSigner
{
    /// <summary>Prefix of Standard Webhooks signing secrets.</summary>
    public const string SecretPrefix = "whsec_";

    /// <summary>Smallest accepted key (decoded bytes).</summary>
    public const int MinKeyBytes = 24;

    /// <summary>Largest accepted key (decoded bytes).</summary>
    public const int MaxKeyBytes = 64;

    /// <summary>Key size of <see cref="GenerateSecret"/>.</summary>
    public const int GeneratedKeyBytes = 32;

    public const string IdHeader = "webhook-id";
    public const string TimestampHeader = "webhook-timestamp";
    public const string SignatureHeader = "webhook-signature";
    public const string GitHubSignatureHeader = "X-Hub-Signature-256";

    /// <summary>A new secret: <c>"whsec_" + Base64(32 random bytes)</c>.</summary>
    public static string GenerateSecret()
    {
        Span<byte> key = stackalloc byte[GeneratedKeyBytes];
        RandomNumberGenerator.Fill(key);
        try
        {
            return SecretPrefix + Convert.ToBase64String(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>True when <paramref name="secret"/> is <c>whsec_</c> + standard base64 (padding optional) of 24–64 bytes.</summary>
    public static bool IsValidSecret(string? secret)
    {
        if (!TryParseKey(secret, out var key))
        {
            return false;
        }

        CryptographicOperations.ZeroMemory(key);
        return true;
    }

    /// <summary>Decodes the HMAC key of a <c>whsec_</c> secret; false when the prefix, the base64 or the length (24–64
    /// bytes) is wrong. The caller should zero the returned buffer after use.</summary>
    public static bool TryParseKey(string? secret, [NotNullWhen(true)] out byte[]? key)
    {
        key = null;
        if (secret is null || !secret.StartsWith(SecretPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var encoded = secret.AsSpan(SecretPrefix.Length);
        if (encoded.IsEmpty || encoded.Length % 4 == 1 || encoded.Length > 4 * ((MaxKeyBytes + 2) / 3))
        {
            return false;
        }

        // Accept unpadded base64 (some generators drop the '=').
        var padding = (4 - (encoded.Length % 4)) % 4;
        Span<char> padded = stackalloc char[encoded.Length + padding];
        encoded.CopyTo(padded);
        padded[encoded.Length..].Fill('=');

        Span<byte> buffer = stackalloc byte[MaxKeyBytes + 3];
        try
        {
            if (!Convert.TryFromBase64Chars(padded, buffer, out var written) || written is < MinKeyBytes or > MaxKeyBytes)
            {
                return false;
            }

            key = buffer[..written].ToArray();
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    /// <summary>One Standard Webhooks signature: <c>v1,&lt;base64&gt;</c>.</summary>
    /// <param name="secret">A <c>whsec_</c> secret.</param>
    /// <param name="messageId"><c>webhook-id</c> (non-empty, without '.').</param>
    /// <param name="timestamp"><c>webhook-timestamp</c>: Unix seconds of this attempt.</param>
    /// <param name="body">The exact body bytes sent.</param>
    /// <exception cref="ArgumentException">The secret is not a valid <c>whsec_</c> secret, or the id is empty or contains
    /// '.' (the message text names neither).</exception>
    public static string Sign(SecretValue secret, string messageId, long timestamp, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ValidateMessage(messageId, timestamp);
        if (!TryParseKey(secret.Reveal(), out var key))
        {
            throw new ArgumentException("The signing secret is not a valid whsec_ secret (base64 of 24 to 64 bytes).", nameof(secret));
        }

        try
        {
            using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
            hmac.AppendData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{messageId}.{timestamp}.")));
            hmac.AppendData(body);
            return "v1," + Convert.ToBase64String(hmac.GetHashAndReset());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>The <c>webhook-signature</c> header value: <c>v1,&lt;sig&gt;</c>, followed by <c> v1,&lt;sig of the previous
    /// key&gt;</c> while <paramref name="previousSecret"/> is set (key rotation).</summary>
    /// <exception cref="ArgumentException">See <see cref="Sign"/>.</exception>
    public static string CreateSignatureHeader(SecretValue secret, SecretValue? previousSecret, string messageId, long timestamp, ReadOnlySpan<byte> body)
    {
        var current = Sign(secret, messageId, timestamp, body);
        return previousSecret is null ? current : current + " " + Sign(previousSecret, messageId, timestamp, body);
    }

    /// <summary>The <c>X-Hub-Signature-256</c> header value: <c>sha256=&lt;lower-case hex HMAC-SHA256(UTF-8 secret, body)&gt;</c>.</summary>
    public static string SignGitHub(SecretValue secret, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var key = Encoding.UTF8.GetBytes(secret.Reveal());
        try
        {
            return "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(key, body));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>True when one of the space-separated <c>v1,…</c> signatures of <paramref name="signatureHeader"/> matches
    /// (constant-time comparison; other versions are ignored). For receivers and test sinks; the timestamp tolerance is
    /// the receiver's decision.</summary>
    public static bool Verify(string? signatureHeader, SecretValue secret, string messageId, long timestamp, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (string.IsNullOrWhiteSpace(signatureHeader) || !IsValidSecret(secret.Reveal())
            || string.IsNullOrEmpty(messageId) || messageId.Contains('.', StringComparison.Ordinal) || timestamp < 0)
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Sign(secret, messageId, timestamp, body));
        var match = false;
        foreach (var candidate in signatureHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (candidate.StartsWith("v1,", StringComparison.Ordinal))
            {
                match |= CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(candidate));
            }
        }

        return match;
    }

    private static void ValidateMessage(string messageId, long timestamp)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        if (messageId.Contains('.', StringComparison.Ordinal))
        {
            throw new ArgumentException("The webhook id must not contain '.'.", nameof(messageId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(timestamp);
    }
}
