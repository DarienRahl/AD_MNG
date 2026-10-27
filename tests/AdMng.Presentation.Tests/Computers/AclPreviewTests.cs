using System.Globalization;
using AdMng.Core.Batch;
using AdMng.Core.Formatting;
using AdMng.Core.Schema;
using AdMng.Core.Security;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Computers;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Tests.Computers;

/// <summary>The preview of <see cref="ChangeAction.ModifySecurity"/>: one row per ACE with the trustee and the rights.</summary>
public class AclPreviewTests
{
    private const string Sid = "S-1-5-21-3623811015-3361044348-30300820-1201";

    private static PreviewBuilder Builder(string culture)
    {
        var localizer = new ResxLocalizer(CultureInfo.GetCultureInfo(culture));
        return new PreviewBuilder(localizer, new ErrorTranslator(localizer), new AttributeFormatter(SchemaCatalog.Empty), SchemaCatalog.Empty);
    }

    private static ChangePlan Plan() => new()
    {
        Objects =
        [
            new ObjectPlan
            {
                DistinguishedName = "CN=WAW-PC003,OU=Komputery,DC=contoso,DC=test",
                DisplayName = "WAW-PC003",
                Changes =
                [
                    new PlannedChange
                    {
                        Action = ChangeAction.ModifySecurity,
                        Acl = new AclChange(
                            [
                                AceSpec.Allow(Sid, 0x100, ExtendedRightIds.ResetPassword, "CONTOSO\\Pomoc techniczna – Łódź"),
                                AceSpec.Allow(Sid, 0x30, ExtendedRightIds.AccountRestrictions, "CONTOSO\\Pomoc techniczna – Łódź"),
                            ],
                            [new AceSpec { Qualifier = AceQualifier.Deny, Mask = 0x30, TrusteeSid = Sid, ObjectType = new Guid("bf967950-0de6-11d0-a285-00aa003049e2") }]),
                    },
                ],
            },
        ],
    };

    [Fact]
    public void Rows_NameTheTrusteeAndTheRights_InPolish()
    {
        var rows = Assert.Single(Builder("pl-PL").Build(Plan())).Rows;

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal("Zmień uprawnienia", r.Action));
        Assert.Equal([Sid, "CONTOSO\\Pomoc techniczna – Łódź", "CONTOSO\\Pomoc techniczna – Łódź"], rows.Select(r => r.Attribute));
        Assert.Equal("usunięte: Odmawiaj – odczyt bf967950-0de6-11d0-a285-00aa003049e2, zapis bf967950-0de6-11d0-a285-00aa003049e2", rows[0].Before);
        Assert.Equal("dodane: Zezwalaj – resetowanie hasła", rows[1].After);
        Assert.Equal("dodane: Zezwalaj – odczyt i zapis ograniczeń konta", rows[2].After);
        Assert.All(rows, r => Assert.True(r.IsChange));
    }

    [Fact]
    public void Rows_AreLocalizedInEnglish()
    {
        var rows = Assert.Single(Builder("en-US").Build(Plan())).Rows;

        Assert.Equal("Change permissions", rows[0].Action);
        Assert.Equal("added: Allow – reset password", rows[1].After);
    }

    [Fact]
    public void AceText_DescribesGenericRightsAndInheritance()
    {
        var localizer = new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL"));

        Assert.Equal("Zezwalaj – pełna kontrola", AceText.Describe(new AceSpec { Qualifier = AceQualifier.Allow, Mask = 0xF01FF, TrusteeSid = Sid }, localizer));
        Assert.Equal(
            "Zezwalaj – odczyt (także dla obiektów podrzędnych)",
            AceText.Describe(new AceSpec { Qualifier = AceQualifier.Allow, Mask = 0x20094, TrusteeSid = Sid, Flags = 0x02 }, localizer));
        Assert.Equal(
            "Zezwalaj – zastosowanie zasad grupy",
            AceText.Describe(AceSpec.Allow(Sid, 0x100, ExtendedRightIds.ApplyGroupPolicy), localizer));
        Assert.Equal(
            "Odmawiaj – usuwanie, prawa 0x01000000",
            AceText.Describe(new AceSpec { Qualifier = AceQualifier.Deny, Mask = 0x01010000, TrusteeSid = Sid }, localizer));
    }
}
