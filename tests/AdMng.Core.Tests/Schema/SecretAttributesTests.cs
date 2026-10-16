using AdMng.Core.Schema;

namespace AdMng.Core.Tests.Schema;

public class SecretAttributesTests
{
    private static readonly SchemaCatalog Schema = new(
        [
            new AttributeDefinition { LdapName = "contoso-Pin", Syntax = AttributeSyntax.UnicodeString, IsConfidential = true },
            new AttributeDefinition { LdapName = "department", Syntax = AttributeSyntax.UnicodeString },
        ],
        []);

    [Theory]
    [InlineData("unicodePwd")]
    [InlineData("UNICODEPWD")]
    [InlineData("userPassword")]
    [InlineData("ms-Mcs-AdmPwd")]
    [InlineData("msLAPS-Password")]
    [InlineData("msLAPS-EncryptedPassword")]
    [InlineData("msFVE-RecoveryPassword")]
    [InlineData("unicodePwd;binary")]
    public void WellKnownSecrets_AreSecretWithoutASchema(string attribute)
    {
        Assert.True(SecretAttributes.IsSecret(attribute));
        Assert.True(SecretAttributes.IsSecret(attribute, Schema));
    }

    [Theory]
    [InlineData("department")]
    [InlineData("ms-Mcs-AdmPwdExpirationTime")]
    [InlineData("msLAPS-PasswordExpirationTime")]
    [InlineData("pwdLastSet")]
    public void OrdinaryAttributes_AreNotSecret(string attribute)
    {
        Assert.False(SecretAttributes.IsSecret(attribute));
        Assert.False(SecretAttributes.IsSecret(attribute, Schema));
    }

    [Fact]
    public void CoversDrsrSecretSet()
    {
        // MS-DRSR 4.1.10.3.11 (secret attributes): replicated encrypted, never readable over LDAP in clear - yet the
        // schema does not mark most of them confidential, so the name list must cover all of them.
        string[] drsrSecrets =
        [
            "unicodePwd", "dBCSPwd", "ntPwdHistory", "lmPwdHistory", "supplementalCredentials", "priorValue",
            "currentValue", "trustAuthOutgoing", "trustAuthIncoming", "initialAuthOutgoing", "initialAuthIncoming",
            "msDS-ExecuteScriptPassword",
        ];

        foreach (var attribute in drsrSecrets)
        {
            Assert.True(SecretAttributes.IsSecret(attribute), attribute);
            Assert.True(SecretAttributes.IsSecret(attribute.ToUpperInvariant(), Schema), attribute);
            Assert.Contains(attribute, SecretAttributes.WellKnown, StringComparer.OrdinalIgnoreCase);
        }

        Assert.True(SecretAttributes.IsSecret("msSFU30Password"));
    }

    [Fact]
    public void ConfidentialSchemaAttributes_AreSecret()
    {
        Assert.True(SecretAttributes.IsSecret("contoso-Pin", Schema));
        Assert.True(SecretAttributes.IsSecret("CONTOSO-PIN;range=0-*", Schema));
        Assert.False(SecretAttributes.IsSecret("contoso-Pin"));
    }
}
