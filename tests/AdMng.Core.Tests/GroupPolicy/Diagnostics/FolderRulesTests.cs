using System.Text;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Diagnostics;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Testing;
using static AdMng.Core.Tests.GroupPolicy.Diagnostics.DiagnosticsSnapshotBuilder;

namespace AdMng.Core.Tests.GroupPolicy.Diagnostics;

/// <summary>Versions, the SYSVOL folder and its files.</summary>
public sealed class FolderRulesTests
{
    private const string SecurityPair = "[{827D319E-6EAC-11D2-A4EA-00C04F79F83A}{803E14A0-B4FB-11D0-A0D0-00A0C90F574B}]";

    [Fact]
    public void VersionMismatch_OffersToAlign_WithBothVersions()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var behind = builder.Gpo("Użytkownicy – Mapowanie dysków", user: 2, computer: 0, gptVersion: (1, 0));
        var equal = builder.Gpo("Użytkownicy – Tapeta", user: 1, computer: 0);
        builder.Link(Ou("Pracownicy"), behind).Link(Ou("Pracownicy"), equal);

        var finding = Findings.Single(builder.Analyze(), GpoFindingCodes.VersionMismatch);

        Assert.Equal((behind, GpoFindingArea.Versions), (finding.GpoId, finding.Area));
        Assert.Equal(["2", "0", "1", "0"], finding.Arguments);
        Assert.Equal(new GpoQuickFix.AlignVersions(behind), finding.Fix);
    }

    [Fact]
    public void SysvolFolderMissing_QuickSaysGptIni_FullSaysFolder()
    {
        var quick = new DiagnosticsSnapshotBuilder { Depth = GpoDiagnosticsDepth.Quick };
        var gpo = quick.Gpo("Kraków – Bez folderu", gptStatus: SysvolFileStatus.NotFound);
        quick.Link(Ou("Kraków"), gpo);
        Assert.Equal(["GptIni"], Findings.Single(quick.Analyze(), GpoFindingCodes.SysvolFolderMissing).Arguments);

        var full = new DiagnosticsSnapshotBuilder();
        var missing = full.Gpo("Kraków – Bez folderu", gptStatus: SysvolFileStatus.NotFound);
        var denied = full.Gpo("Kraków – Bez dostępu", gptStatus: SysvolFileStatus.AccessDenied);
        full.Link(Ou("Kraków"), missing).Link(Ou("Kraków"), denied).FolderState(missing, GpoFolderReadStatus.NotFound).FolderState(denied, GpoFolderReadStatus.AccessDenied);

        var result = full.Analyze();

        var finding = Findings.Single(result, GpoFindingCodes.SysvolFolderMissing);
        Assert.Equal((missing, GpoFindingSeverity.High, "Folder"), (finding.GpoId, finding.Severity, finding.Arguments[0]));
        Assert.Equal([denied], result.FoldersNotRead);
    }

    [Fact]
    public void FolderSecurityMismatch_OffersTheRepair_NotForTheMappedAcl()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var wrong = builder.Gpo("Serwery – Uprawnienia folderu");
        var right = builder.Gpo("Serwery – Zgodne uprawnienia");
        builder.Link(Ou("Serwery"), wrong).Link(Ou("Serwery"), right)
            .Folder(wrong, [], GpoFolderAcl.Build([new GpoFolderAcl.FileAce("S-1-1-0", 0x03, GpoFolderAcl.ReadAndExecute)]))
            .Folder(right);

        var finding = Findings.Single(builder.Analyze(), GpoFindingCodes.FolderSecurityMismatch);

        Assert.Equal((wrong, new GpoQuickFix.RepairFolderSecurity(wrong)), (finding.GpoId!.Value, finding.Fix));
    }

    [Fact]
    public void ExtensionNamesMissing_FilesWhoseExtensionIsNotListed_OffersTheRepair()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var gpo = builder.Gpo("Stacje – Hasła i audyt", user: 1, computer: 3, machineExtensions: SecurityPair);
        var complete = builder.Gpo("Stacje – Tylko hasła", computer: 1, machineExtensions: SecurityPair);
        var emptyRegistry = builder.Gpo("Stacje – Pusty Registry.pol", computer: 1);
        builder.Link(Ou("Stacje"), gpo).Link(Ou("Stacje"), complete).Link(Ou("Stacje"), emptyRegistry)
            .Folder(gpo, Template("[System Access]", "MinimumPasswordLength = 12"), AuditCsv(), RegistryPol(true, (@"Software\Policies\Contoso", "Tapeta", 1)))
            .Folder(complete, Template("[System Access]", "MinimumPasswordLength = 12"))
            .Folder(emptyRegistry, RegistryPol(false));

        var finding = Findings.Single(builder.Analyze(), GpoFindingCodes.ExtensionNamesMissing);

        Assert.Equal(gpo, finding.GpoId);
        Assert.Equal("User/Registry.pol, Machine/Microsoft/Windows NT/Audit/audit.csv", finding.Arguments[0]);
        Assert.Equal(new GpoQuickFix.RepairExtensionNames(gpo), finding.Fix);
    }

    [Fact]
    public void UnreadableFile_AFileThatCannotBeParsed_AndABrokenGptIni()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var gpo = builder.Gpo("Pracownicy – Uszkodzona", user: 1);
        builder.Link(Ou("Pracownicy"), gpo)
            .Folder(gpo, ("User/Preferences/Drives/Drives.xml", Encoding.UTF8.GetBytes("<Drives><Drive>niedomknięty")), (GptIni.RelativePath, Encoding.ASCII.GetBytes("[General]\r\nVersion=dwa\r\n")));

        var findings = Findings.All(builder.Analyze(), GpoFindingCodes.UnreadableFile);

        Assert.Equal(["GPT.INI", "User/Preferences/Drives/Drives.xml"], findings.Select(f => f.Arguments[0]).Order(StringComparer.Ordinal));
        Assert.Contains(findings, f => f.SettingKey == "File/User/Preferences/Drives/Drives.xml");
    }

    [Fact]
    public void Leftovers_OfAnInterruptedSave()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var gpo = builder.Gpo("Sprzedaż – Przerwany zapis");
        var clean = builder.Gpo("Sprzedaż – Czysta");
        builder.Link(Ou("Sprzedaż"), gpo).Link(Ou("Sprzedaż"), clean)
            .Folder(gpo, [], null, ["Machine/Registry.pol.admng-1a2b3c.tmp", "GPT.INI.admng-1a2b3c.tmp"])
            .Folder(clean);

        var finding = Findings.Single(builder.Analyze(), GpoFindingCodes.Leftovers);

        Assert.Equal((gpo, "2", GpoFindingSeverity.Low), (finding.GpoId!.Value, finding.Arguments[0], finding.Severity));
    }

    [Fact]
    public void GppPassword_ACpasswordInPreferences_NotWhenRemoved()
    {
        var builder = new DiagnosticsSnapshotBuilder();
        var gpo = builder.Gpo("Stacje – Konto serwisowe");
        var removed = builder.Gpo("Stacje – Konto bez hasła");
        static string Item(string cpassword) => FakeDirectoryBuilder.Groups(
            "<User clsid=\"{DF5F1855-51E5-4d24-8B1A-D9BDE98BA1D1}\" name=\"serwis\" uid=\"{2B1C1D1E-0000-4000-8000-000000000001}\">"
            + $"<Properties action=\"U\" userName=\"serwis\" cpassword=\"{cpassword}\" changeLogon=\"0\"/></User>");
        builder.Link(Ou("Stacje"), gpo).Link(Ou("Stacje"), removed)
            .Folder(gpo, ("Machine/Preferences/Groups/Groups.xml", Encoding.UTF8.GetBytes(Item("j1Uyj3Vx8TY9LtLZil2uAuZkFQA/4latT76ZwgdHdhw"))))
            .Folder(removed, ("Machine/Preferences/Groups/Groups.xml", Encoding.UTF8.GetBytes(Item(string.Empty))));

        var finding = Findings.Single(builder.Analyze(), GpoFindingCodes.GppPassword);

        Assert.Equal((gpo, GpoFindingSeverity.Critical, "Machine/Preferences/Groups/Groups.xml"), (finding.GpoId!.Value, finding.Severity, finding.Arguments[0]));
        Assert.EndsWith("/cpassword", finding.SettingKey, StringComparison.Ordinal);
        Assert.DoesNotContain("j1Uyj3", string.Join(" ", finding.Arguments), StringComparison.Ordinal);
    }

    [Fact]
    public void Quick_FolderRulesAreNotEvaluated_AndSaySo()
    {
        var builder = new DiagnosticsSnapshotBuilder { Depth = GpoDiagnosticsDepth.Quick };
        var gpo = builder.Gpo("Stacje – Hasła");
        builder.Link(Ou("Stacje"), gpo).Folder(gpo, [], null, ["x.admng-1.tmp"]);

        var result = builder.Analyze();

        Findings.None(result, GpoFindingCodes.Leftovers);
        Assert.Equal([GpoRuleGroup.Folders, GpoRuleGroup.ObjectCounts, GpoRuleGroup.Principals, GpoRuleGroup.Conflicts], result.NotEvaluated);
    }

    [Fact]
    public void NoSysvol_VersionsAndFoldersAreNotEvaluated()
    {
        var builder = new DiagnosticsSnapshotBuilder { SysvolVersionsRead = false, FoldersRead = false };
        var gpo = builder.Gpo("Stacje – Hasła", gptVersion: (0, 9));
        builder.Link(Ou("Stacje"), gpo);

        var result = builder.Analyze();

        Findings.None(result, GpoFindingCodes.VersionMismatch);
        Assert.Equal([GpoRuleGroup.SysvolVersions, GpoRuleGroup.Folders, GpoRuleGroup.Conflicts], result.NotEvaluated);
    }
}
