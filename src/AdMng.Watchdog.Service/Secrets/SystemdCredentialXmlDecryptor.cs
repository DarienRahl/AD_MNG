using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace AdMng.Watchdog.Service.Secrets;

/// <summary>
/// Decrypts the key elements written by <see cref="SystemdCredentialXmlEncryptor"/> (stage B design 10). Constructed by
/// Data Protection through its <see cref="IServiceProvider"/> constructor; the key comes from the registered
/// <see cref="DataProtectionKeySource"/>. A key element written with another credential (different <c>kid</c>), a missing
/// key or a tampered element throws <see cref="CryptographicException"/> – the key ring then cannot unprotect the stored
/// secrets, which the start-up checks report as <c>secret.unreadable</c> (the documented <c>reset-secrets-key</c> path).
/// The exception messages never contain key material or plaintext.
/// </summary>
public sealed class SystemdCredentialXmlDecryptor : IXmlDecryptor
{
    private readonly DataProtectionKeySource? _keys;

    /// <summary>The constructor Data Protection's activator uses.</summary>
    public SystemdCredentialXmlDecryptor(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _keys = services.GetService<DataProtectionKeySource>();
    }

    /// <summary>With an explicit key source (tests).</summary>
    public SystemdCredentialXmlDecryptor(DataProtectionKeySource keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _keys = keys;
    }

    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);
        var ns = SystemdCredentialXmlEncryptor.Namespace;
        if (encryptedElement.Name != ns + "encryptedKey"
            || !string.Equals((string?)encryptedElement.Attribute("alg"), SystemdCredentialXmlEncryptor.Algorithm, StringComparison.Ordinal))
        {
            throw new CryptographicException("The key element is not an AdMng A256GCM element.");
        }

        var key = _keys?.Key ?? throw new CryptographicException("The Data Protection key credential (dp-key) is not available.");
        var keyId = (string?)encryptedElement.Attribute("kid");
        if (!string.Equals(keyId, key.KeyId, StringComparison.Ordinal))
        {
            throw new CryptographicException("The key element was encrypted with another dp-key credential.");
        }

        byte[] nonce, ciphertext, tag;
        try
        {
            nonce = Convert.FromBase64String((string?)encryptedElement.Element(ns + "nonce") ?? string.Empty);
            ciphertext = Convert.FromBase64String((string?)encryptedElement.Element(ns + "ciphertext") ?? string.Empty);
            tag = Convert.FromBase64String((string?)encryptedElement.Element(ns + "tag") ?? string.Empty);
        }
        catch (FormatException)
        {
            throw new CryptographicException("The key element is malformed.");
        }

        if (nonce.Length != SystemdCredentialXmlEncryptor.NonceLength || tag.Length != SystemdCredentialXmlEncryptor.TagLength)
        {
            throw new CryptographicException("The key element is malformed.");
        }

        var plaintext = new byte[ciphertext.Length];
        try
        {
            using (var aes = new AesGcm(key.Key, SystemdCredentialXmlEncryptor.TagLength))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext, SystemdCredentialXmlEncryptor.AssociatedData(key.KeyId));
            }

            return XElement.Parse(Encoding.UTF8.GetString(plaintext));
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new CryptographicException("The key element failed authentication (tampered or another credential).");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
