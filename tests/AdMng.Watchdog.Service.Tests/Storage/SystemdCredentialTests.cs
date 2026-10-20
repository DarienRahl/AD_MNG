using System.Security.Cryptography;
using System.Xml.Linq;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Secrets;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Watchdog.Service.Tests.Storage;

/// <summary>The Linux key ring encryption with the systemd credential <c>dp-key</c> (stage B design 10).</summary>
public sealed class SystemdCredentialTests
{
    private static readonly XElement Secret = new("masterKey", new XElement("value", "Zażółć gęślą jaźń – sekret 2026"));

    [Fact]
    public void Credential_IsRead_FromTheCredentialsDirectory()
    {
        using var directory = new TestDataDirectory();
        var bytes = RandomNumberGenerator.GetBytes(DataProtectionMasterKey.KeyLength);
        File.WriteAllBytes(directory.File(DataProtectionMasterKey.CredentialName), bytes);

        using var key = DataProtectionMasterKey.FromCredentialsDirectory(directory.Path);

        Assert.NotNull(key);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)[..8]), key.KeyId);
        Assert.DoesNotContain(Convert.ToHexString(bytes), key.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(DataProtectionMasterKey.FromCredentialsDirectory(null));
        Assert.Null(DataProtectionMasterKey.FromCredentialsDirectory(directory.File("missing")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void Credential_OfTheWrongLength_IsInvalid(int length)
    {
        using var directory = new TestDataDirectory();
        File.WriteAllBytes(directory.File(DataProtectionMasterKey.CredentialName), new byte[length]);
        var source = new DataProtectionKeySource(() => DataProtectionMasterKey.FromCredentialsDirectory(directory.Path));

        Assert.Throws<InvalidDataException>(() => DataProtectionMasterKey.FromCredentialsDirectory(directory.Path));
        Assert.Null(source.Key);
        Assert.Equal("invalid", source.Problem);
        Assert.Equal("missing", DataProtectionKeySource.None().Problem);
    }

    [Fact]
    public void Encryptor_WritesAesGcm_WithoutThePlaintext_AndTheDecryptorRestoresIt()
    {
        using var key = NewKey();
        var encrypted = new SystemdCredentialXmlEncryptor(key).Encrypt(Secret);

        var element = encrypted.EncryptedElement;
        Assert.Equal(SystemdCredentialXmlEncryptor.Namespace + "encryptedKey", element.Name);
        Assert.Equal("A256GCM", (string?)element.Attribute("alg"));
        Assert.Equal(key.KeyId, (string?)element.Attribute("kid"));
        Assert.Equal(12, Convert.FromBase64String(element.Element(SystemdCredentialXmlEncryptor.Namespace + "nonce")!.Value).Length);
        Assert.DoesNotContain("sekret", element.ToString(), StringComparison.Ordinal);
        Assert.Equal(typeof(SystemdCredentialXmlDecryptor), encrypted.DecryptorType);

        var decrypted = new SystemdCredentialXmlDecryptor(DataProtectionKeySource.FromKey(key)).Decrypt(element);

        Assert.Equal(Secret.ToString(), decrypted.ToString());
    }

    [Fact]
    public void Decryptor_RefusesAnotherCredential_AndATamperedElement()
    {
        using var key = NewKey();
        using var other = NewKey();
        var element = new SystemdCredentialXmlEncryptor(key).Encrypt(Secret).EncryptedElement;
        var tampered = new XElement(element);
        var ciphertext = tampered.Element(SystemdCredentialXmlEncryptor.Namespace + "ciphertext")!;
        var bytes = Convert.FromBase64String(ciphertext.Value);
        bytes[0] ^= 0x01;
        ciphertext.Value = Convert.ToBase64String(bytes);
        var forged = new XElement(element);
        forged.SetAttributeValue("kid", other.KeyId);

        Assert.Throws<CryptographicException>(() => new SystemdCredentialXmlDecryptor(DataProtectionKeySource.FromKey(other)).Decrypt(element));
        Assert.Throws<CryptographicException>(() => new SystemdCredentialXmlDecryptor(DataProtectionKeySource.FromKey(key)).Decrypt(tampered));
        Assert.Throws<CryptographicException>(() => new SystemdCredentialXmlDecryptor(DataProtectionKeySource.FromKey(other)).Decrypt(forged));
        Assert.Throws<CryptographicException>(() => new SystemdCredentialXmlDecryptor(DataProtectionKeySource.None()).Decrypt(element));
    }

    /// <summary>The real key ring: keys written by one service instance are read by the next one with the same credential
    /// (the decryptor is constructed by Data Protection), never stored in clear, and useless with another credential.</summary>
    [Fact]
    public void KeyRing_IsEncryptedWithTheCredential_AndSurvivesARestart()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "On Windows the key ring is protected with DPAPI-NG.");
        using var directory = new TestDataDirectory();
        var data = new DataDirectory(directory.Path);
        data.Ensure();
        var bytes = RandomNumberGenerator.GetBytes(DataProtectionMasterKey.KeyLength);

        string protectedText;
        using (var first = Provider(data, bytes))
        {
            protectedText = first.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("Hasło-Wąż-2026!");
        }

        var keyFile = Assert.Single(System.IO.Directory.GetFiles(data.KeysPath, "*.xml"));
        var xml = File.ReadAllText(keyFile);
        Assert.Contains("urn:admng:dp", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("<masterKey", xml, StringComparison.Ordinal);

        using (var second = Provider(data, bytes))
        {
            Assert.Equal("Hasło-Wąż-2026!", second.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(protectedText));
        }

        using var foreign = Provider(data, RandomNumberGenerator.GetBytes(DataProtectionMasterKey.KeyLength));
        Assert.ThrowsAny<CryptographicException>(() => foreign.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(protectedText));
    }

    [Fact]
    public void Variant_IsReadFromItsFile_WhenItIsAKnownValue()
    {
        using var directory = new TestDataDirectory();
        var file = directory.File("dp-key.variant");
        var source = new DataProtectionKeySource(() => null, file);

        Assert.Null(source.ReadVariant());
        File.WriteAllText(file, "tpm2+host\n");
        Assert.Equal(HostCredentialKeys.Tpm2Host, source.ReadVariant());
        File.WriteAllText(file, "cokolwiek");
        Assert.Null(source.ReadVariant());
    }

    private static DataProtectionMasterKey NewKey() => DataProtectionMasterKey.FromBytes(RandomNumberGenerator.GetBytes(DataProtectionMasterKey.KeyLength));

    private static ServiceProvider Provider(DataDirectory data, byte[] key)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(DataProtectionKeySource.FromKey(DataProtectionMasterKey.FromBytes(key)));
        DataProtectionSetup.Configure(services, data);
        return services.BuildServiceProvider();
    }
}
