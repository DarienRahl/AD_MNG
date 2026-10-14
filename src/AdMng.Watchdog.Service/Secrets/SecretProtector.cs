using System.Security.Cryptography;
using System.Text;
using AdMng.Watchdog.Core.Model;
using Microsoft.AspNetCore.DataProtection;

namespace AdMng.Watchdog.Service.Secrets;

/// <summary>
/// ASP.NET Core Data Protection for stored channel secrets (spec 18): purpose <see cref="Purpose"/>, keys in
/// <c>&lt;data&gt;/keys</c> protected with DPAPI-NG to the gMSA on Windows (<c>DataProtectionSetup</c>). Plaintext
/// buffers are zeroed after use; values only ever leave as <see cref="SecretValue"/>.
/// </summary>
public sealed class SecretProtector
{
    /// <summary>The Data Protection purpose string of stored secrets.</summary>
    public const string Purpose = "AdMng.Watchdog.Secrets.v1";

    private readonly IDataProtector _protector;

    public SecretProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _protector = provider.CreateProtector(Purpose);
    }

    /// <summary>Encrypts and authenticates the secret.</summary>
    public byte[] Protect(SecretValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var plain = Encoding.UTF8.GetBytes(value.Reveal());
        try
        {
            return _protector.Protect(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Decrypts a stored secret.</summary>
    /// <exception cref="SecretUnreadableException">The key ring cannot decrypt it (gMSA recreated, keys lost, tampering).</exception>
    public SecretValue Unprotect(string name, byte[] protectedValue)
    {
        ArgumentNullException.ThrowIfNull(protectedValue);
        byte[] plain;
        try
        {
            plain = _protector.Unprotect(protectedValue);
        }
        catch (CryptographicException ex)
        {
            throw new SecretUnreadableException(name, ex);
        }

        try
        {
            return new SecretValue(Encoding.UTF8.GetString(plain));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }
}
