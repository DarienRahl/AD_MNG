using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Formatting;
using AdMng.Core.Schema;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Tests.Dialogs;

/// <summary>The preview grid never shows passwords or values of secret attributes.</summary>
public class PreviewSecretTests
{
    [Fact]
    public void SecretAttributeValues_AreMaskedInThePreview()
    {
        var schema = new SchemaCatalog(
            [
                new AttributeDefinition { LdapName = "contoso-Pin", Syntax = AttributeSyntax.UnicodeString, IsConfidential = true },
                new AttributeDefinition { LdapName = Attr.Description, Syntax = AttributeSyntax.UnicodeString },
            ],
            []);
        var localizer = new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL"));
        var builder = new PreviewBuilder(localizer, new ErrorTranslator(localizer), new AttributeFormatter(schema), schema);
        var plan = new ChangePlan
        {
            Objects =
            [
                new ObjectPlan
                {
                    DistinguishedName = "CN=PC-Łódź,OU=Komputery,DC=contoso,DC=test",
                    DisplayName = "PC-Łódź",
                    Changes =
                    [
                        new PlannedChange
                        {
                            Action = ChangeAction.Modify,
                            Attributes =
                            [
                                new AttributeChange("ms-Mcs-AdmPwd", AttributeChangeKind.Replace, ["Stare!Hasło"], ["Nowe!Hasło"]),
                                new AttributeChange("contoso-Pin", AttributeChangeKind.Clear, ["1234"], []),
                                new AttributeChange(Attr.Description, AttributeChangeKind.Replace, ["stary"], ["nowy"]),
                            ],
                        },
                        new PlannedChange { Action = ChangeAction.ResetPassword, Password = "Zażółć!2026" },
                    ],
                },
            ],
        };

        var rows = Assert.Single(builder.Build(plan)).Rows;

        Assert.Equal([SecretAttributes.Mask, SecretAttributes.Mask, "stary", string.Empty], rows.Select(r => r.Before));
        Assert.Equal([SecretAttributes.Mask, string.Empty, "nowy", SecretAttributes.Mask], rows.Select(r => r.After));
        Assert.All(rows, r => Assert.DoesNotContain("Hasło", r.Before + r.After, StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => (r.Before + r.After).Contains("1234", StringComparison.Ordinal));
    }
}
