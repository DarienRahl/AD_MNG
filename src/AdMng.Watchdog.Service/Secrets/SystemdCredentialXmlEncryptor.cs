using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace AdMng.Watchdog.Service.Secrets;

/// <summary>
/// Encrypts the Data Protection key elements on Linux (stage B design 10): AES-256-GCM with the
/// <see cref="DataProtectionMasterKey"/> from the systemd credential, a random 96-bit nonce per element and the key id as
/// associated data. Output: <c>&lt;encryptedKey xmlns="urn:admng:dp" alg="A256GCM" kid="…"&gt;</c> with base64
/// <c>nonce</c>, <c>ciphertext</c> and <c>tag</c>; <see cref="SystemdCredentialXmlDecryptor"/> reverses it.
/// </summary>
public sealed class SystemdCredentialXmlEncryptor : IXmlEncryptor
{
    /// <summary>The XML namespace of the encrypted element.</summary>
    public static readonly XNamespace Namespace = "urn:admng:dp";

    /// <summary>The <c>alg</c> value.</summary>
    public const string Algorithm = "A256GCM";

    /// <summary>GCM nonce length in bytes.</summary>
    public const int NonceLength = 12;

    /// <summary>GCM tag length in bytes.</summary>
    public const int TagLength = 16;

    private readonly DataProtectionMasterKey _key;

    public SystemdCredentialXmlEncryptor(DataProtectionMasterKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _key = key;
    }

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);
        var plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        try
        {
            using var aes = new AesGcm(_key.Key, TagLength);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(_key.KeyId));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        var element = new XElement(
            Namespace + "encryptedKey",
            new XAttribute("alg", Algorithm),
            new XAttribute("kid", _key.KeyId),
            new XElement(Namespace + "nonce", Convert.ToBase64String(nonce)),
            new XElement(Namespace + "ciphertext", Convert.ToBase64String(ciphertext)),
            new XElement(Namespace + "tag", Convert.ToBase64String(tag)));
        return new EncryptedXmlInfo(element, typeof(SystemdCredentialXmlDecryptor));
    }

    /// <summary>Binds the ciphertext to the purpose, the algorithm and the key id.</summary>
    internal static byte[] AssociatedData(string keyId) => Encoding.ASCII.GetBytes("AdMng.Watchdog.DataProtection|" + Algorithm + "|" + keyId);
}
