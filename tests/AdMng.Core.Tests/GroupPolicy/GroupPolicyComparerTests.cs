using AdMng.Core.GroupPolicy.Comparison;
using AdMng.Core.GroupPolicy.Management;

namespace AdMng.Core.Tests.GroupPolicy;

public class GroupPolicyComparerTests : GroupPolicyTestBed
{
    private async Task<GpoComparisonSide> SideAsync(Guid id)
    {
        await using var sysvol = await OpenSysvolAsync();
        var gpo = (await Reader.ReadPolicyAsync(Session, id, Ct))!;
        return new GpoComparisonSide(gpo, await GroupPolicyReader.ReadFolderAsync(sysvol, id, Ct), await Reader.ReadLinksOfAsync(Session, id, Ct));
    }

    [Fact]
    public async Task Compare_ShowsCoreSettingsPermissionsLinksAndFiles()
    {
        var rows = GroupPolicyComparer.Compare(await SideAsync(Sample.WorkstationPasswords), await SideAsync(Sample.ServerSecurity));

        Assert.Contains(rows, r => r.Key == GroupPolicyComparer.StatusKey && r.State == GpoComparisonState.Different && r.ValueB == "UserSettingsDisabled");
        Assert.Contains(rows, r => r.Key == GroupPolicyComparer.WmiFilterKey && r.State == GpoComparisonState.OnlyInA);
        Assert.Contains(rows, r => r.Section == GpoComparisonSection.Permissions && r.State == GpoComparisonState.OnlyInA
            && r.Key == Builder.SidOf(Company.LukaszWisniewski) && r.ValueA == "Edit");
        Assert.Contains(rows, r => r.Section == GpoComparisonSection.Links && r.Key == Company.ComputersOu && r.State == GpoComparisonState.OnlyInA);
        Assert.Contains(rows, r => r.Section == GpoComparisonSection.Settings && r.Key.Contains("MinimumPasswordLength", StringComparison.Ordinal)
            && r.State == GpoComparisonState.OnlyInA && r.ValueA == "12");
        Assert.Contains(rows, r => r.Section == GpoComparisonSection.Files && r.Key == "GPO.cmt" && r.State == GpoComparisonState.OnlyInA);
        Assert.Contains(rows, r => r.Section == GpoComparisonSection.Files && r.Key.EndsWith("GptTmpl.inf", StringComparison.Ordinal) && r.State == GpoComparisonState.Different);
    }

    [Fact]
    public async Task Compare_OfAGpoWithItself_IsAllSame()
    {
        var side = await SideAsync(Sample.WorkstationPasswords);

        Assert.All(GroupPolicyComparer.Compare(side, side), r => Assert.Equal(GpoComparisonState.Same, r.State));
    }

    [Fact]
    public async Task Compare_MasksSecretSettings()
    {
        Sysvol.PutText(Sample.ServerSecurity, "Machine/Preferences/Groups/Groups.xml",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><Groups clsid=\"{3125E937-EB16-4b4c-9934-544FC6D24D26}\"><User clsid=\"{DF5F1855-51E5-4d24-8B1A-D9BDE98BA1D1}\" name=\"admin.lokalny\" uid=\"{1}\"><Properties action=\"U\" userName=\"admin.lokalny\" cpassword=\"j1Uyj3Vx8TY9LtLZil2uAuZkFQA/4latT76ZwgdHdhw\"/></User></Groups>");

        var rows = GroupPolicyComparer.Compare(await SideAsync(Sample.WorkstationPasswords), await SideAsync(Sample.ServerSecurity));

        var secret = Assert.Single(rows, r => r.Key.EndsWith("cpassword", StringComparison.OrdinalIgnoreCase));
        Assert.True(secret.Masked);
        Assert.Equal(GroupPolicyComparer.MaskedValue, secret.ValueB);
        Assert.DoesNotContain(rows, r => (r.ValueB ?? string.Empty).Contains("j1Uyj3", StringComparison.Ordinal) && r.Section == GpoComparisonSection.Settings);
    }
}
