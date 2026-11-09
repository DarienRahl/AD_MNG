using System.Globalization;
using System.Resources;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Presentation.GroupPolicy.Copy;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Tests.GroupPolicy.Copy;

/// <summary>The K7 contract commit (docs/dev/k-computers-and-gpo.md 21.11): texts present, no directory values in ToString.</summary>
public sealed class GpoK7ContractTests
{
    private static readonly ResourceManager Resources = new(ResxLocalizer.ResourceBaseName, typeof(ResxLocalizer).Assembly);

    [Fact]
    public void RefusalTexts_ExistInBothLanguages()
    {
        var en = Resources.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        var pl = Resources.GetResourceSet(CultureInfo.GetCultureInfo("pl"), true, false)!;
        var keys = GpoUnitRefusalText.AllKeys().ToList();

        Assert.Equal(Enum.GetValues<GpoUnitRefusal>().Length, keys.Count);
        Assert.Equal([], keys.Where(k => en.GetString(k) is null));
        Assert.Equal([], keys.Where(k => pl.GetString(k) is null));
    }

    [Fact]
    public void ToString_CarriesNoDirectoryValues()
    {
        var gpo = Guid.Parse("0b6a3c1e-1d2f-4a7b-9c8d-123456789abc");
        var selection = new GpoUnitSelection(gpo, @"I|User|Drives|Drive|K:\\serwer\księgowość", "{UID}", "ścieżka");
        var item = new GpoCopyItem(selection, "Dyski (Księgowość)", "K:", "Preferencje", @"\\serwer\księgowość", GpoUnitRefusal.Secret);
        var unit = new GpoSettingUnit { Id = "K|x", BaseId = "K|x", Kind = GpoSettingUnitKind.RegistryValue, Scope = Core.GroupPolicy.Content.GpoScope.Computer };

        foreach (var text in new[] { selection.ToString(), item.ToString(), new GpoUnitRef(gpo, unit).ToString(), new GpoPrincipal("CONTOSO\\Łukasz", "S-1-5-21-1-2-3-1105", GpoPrincipalKind.Account).ToString() })
        {
            Assert.DoesNotContain("serwer", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Księgowość", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Łukasz", text, StringComparison.Ordinal);
            Assert.DoesNotContain("S-1-5-21", text, StringComparison.Ordinal);
        }

        Assert.Contains(SysvolPaths.FolderName(gpo), item.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
