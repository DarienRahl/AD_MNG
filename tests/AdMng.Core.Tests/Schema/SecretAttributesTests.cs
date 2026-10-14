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
    public void ConfidentialSchemaAttributes_AreSecret()
    {
        Assert.True(SecretAttributes.IsSecret("contoso-Pin", Schema));
        Assert.True(SecretAttributes.IsSecret("CONTOSO-PIN;range=0-*", Schema));
        Assert.False(SecretAttributes.IsSecret("contoso-Pin"));
    }
}
