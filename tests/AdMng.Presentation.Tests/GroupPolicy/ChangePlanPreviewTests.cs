using System.Globalization;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Tests.GroupPolicy;

/// <summary>The change plan preview window (design note 3.3): SYSVOL rows, differences, PowerShell, execution, Undo.</summary>
public sealed class ChangePlanPreviewTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public async Task CopyPlan_ShowsFileVersionAndFolderRows_AndNoContentInPowerShell()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        var plan = await CopyPlanAsync(h);
        using var preview = new ChangePlanPreviewViewModel(h.Ui, h.B2.Engines, new ChangePlanPreviewRequest(plan, "Kopiowanie „Stacje robocze – Zasady haseł”"));

        Assert.True(preview.IsPreviewStep);
        Assert.Contains(preview.Rows, r => r.Attribute == SecurityTemplate.RelativePath && r.Before == "(brak)" && r.After.EndsWith(" B", StringComparison.Ordinal));
        Assert.Contains(preview.Rows, r => r.Attribute == "Wersja GPO" && r.Before == "komputer 0, użytkownik 0" && r.After == "komputer 1, użytkownik 0");
        Assert.Contains(preview.Rows, r => r.Attribute == "Folder GPO (SYSVOL)");
        Assert.NotNull(preview.SelectedRow?.Sysvol);
        preview.SelectedRow = preview.Rows.Single(r => r.Attribute == SecurityTemplate.RelativePath);
        await Dialogs.B2Harness.WaitUntilAsync(() => preview.DiffLines.Any(l => l.Text.Contains("MinimumPasswordLength", StringComparison.Ordinal)));
        Assert.Contains("MinimumPasswordLength", string.Join("\n", preview.DiffLines.Select(l => l.Text)), StringComparison.Ordinal);

        Assert.Contains("Copy-GPO", preview.PowerShellScript, StringComparison.Ordinal);
        Assert.Contains("#Requires -Modules GroupPolicy", preview.PowerShellScript, StringComparison.Ordinal);
        Assert.DoesNotContain("MinimumPasswordLength", preview.PowerShellScript, StringComparison.Ordinal);
        Assert.Contains(h.Session.Info.Server, preview.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_ThenUndo_RemovesTheCopyAgain()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        var plan = await CopyPlanAsync(h);
        var copyId = plan.Objects.SelectMany(o => o.Changes).First(c => c.Action == ChangeAction.CreateGpoFolder).Sysvol!.GpoId;
        using var preview = new ChangePlanPreviewViewModel(h.Ui, h.B2.Engines, new ChangePlanPreviewRequest(plan, "Kopia"));

        await preview.ExecuteCommand.ExecuteAsync(null);

        Assert.True(preview.IsResultsStep);
        Assert.False(preview.ResultHasFailures);
        Assert.True(preview.ChangedDirectory);
        Assert.True(h.Sysvol.FolderExists(copyId));
        Assert.True(preview.UndoCommand.CanExecute(null));

        preview.UndoCommand.Execute(null);
        Assert.True(preview.IsRollbackPreview);
        Assert.True(preview.IsPreviewStep);
        await preview.ExecuteCommand.ExecuteAsync(null);

        Assert.False(preview.ResultHasFailures);
        Assert.False(h.Session.Exists(h.GpoDn(copyId)));
        Assert.False(h.Sysvol.FolderExists(copyId));
    }

    [Fact]
    public async Task TypedConfirmation_IsAsked_AndRefusalExecutesNothing()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        var plan = await h.Planner.PlanDeleteAsync(h.Session, h.Sample.SitePrinters, CancellationToken.None);
        using var preview = new ChangePlanPreviewViewModel(h.Ui, h.B2.Engines, new ChangePlanPreviewRequest(plan, "Usunięcie")
        {
            ConfirmationText = "Lokacja Warszawa – Drukarki",
            ConfirmationMessage = "Usunąć z domeny contoso.test?",
        });
        h.B2.Dialogs.TypedConfirmAnswers.Enqueue(false);

        await preview.ExecuteCommand.ExecuteAsync(null);

        var typed = Assert.Single(h.B2.Dialogs.TypedConfirmations);
        Assert.Equal("Lokacja Warszawa – Drukarki", typed.Expected);
        Assert.True(preview.IsPreviewStep);
        Assert.True(h.Session.Exists(h.GpoDn(h.Sample.SitePrinters)));
    }

    [Fact]
    public async Task FailedSysvolWrite_IsReported_AndTheGpoStaysAsBefore()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        var plan = await CopyPlanAsync(h);
        var copyId = plan.Objects.SelectMany(o => o.Changes).First(c => c.Action == ChangeAction.CreateGpoFolder).Sysvol!.GpoId;
        h.Sysvol.Fail("Replace", SysvolWriteError.AccessDenied);
        using var preview = new ChangePlanPreviewViewModel(h.Ui, h.B2.Engines, new ChangePlanPreviewRequest(plan, "Kopia"));

        await preview.ExecuteCommand.ExecuteAsync(null);

        Assert.True(preview.ResultHasFailures);
        Assert.Contains(preview.ResultRows, r => !string.IsNullOrEmpty(r.Message));
        Assert.DoesNotContain(h.Sysvol.Files(copyId), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("pl-PL", "cofnięto")]
    [InlineData("en-US", "reverted")]
    public void TranslateFailure_NamesRevertedAndInconsistentGpos(string culture, string reverted)
    {
        var localizer = new ResxLocalizer(CultureInfo.GetCultureInfo(culture));
        var errors = new ErrorTranslator(localizer);

        Assert.Contains(reverted, ChangePlanPreviewViewModel.TranslateFailure(localizer, errors, DirectoryErrorKind.AccessDenied, GpoTransaction.RevertedMessage + " (x)"), StringComparison.Ordinal);
        Assert.Contains(culture == "pl-PL" ? "niespójne" : "inconsistent",
            ChangePlanPreviewViewModel.TranslateFailure(localizer, errors, DirectoryErrorKind.Unknown, GpoTransaction.InconsistentMessage), StringComparison.Ordinal);
        Assert.Contains("SYSVOL", ChangePlanPreviewViewModel.TranslateFailure(localizer, errors, DirectoryErrorKind.AccessDenied, BatchExecutor.SysvolUnavailableMessage), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PdcNote_IsShownAboveTheRows()
    {
        var h = await GroupPolicyHarness.CreateAsync(Polish);
        var configuration = h.Session.Info.ConfigurationNamingContext;
        var servers = $"CN=Servers,CN=Warszawa,CN=Sites,{configuration}";
        h.Session.Seed(servers, new Dictionary<string, object?> { [Core.ActiveDirectory.Attr.ObjectClass] = new[] { "top", "serversContainer" } });
        var server = $"CN=DC02,{servers}";
        h.Session.Seed(server, new Dictionary<string, object?> { [Core.ActiveDirectory.Attr.ObjectClass] = new[] { "top", "server" }, [Core.ActiveDirectory.Attr.DnsHostName] = "dc02.contoso.test" });
        h.Session.SeedAttributes(h.DomainDn, new Dictionary<string, object?> { ["fSMORoleOwner"] = $"CN=NTDS Settings,{server}" });

        Assert.True(await h.Actions.SetLinkOptionsAsync(h.Company.ComputersOu, h.Sample.WorkstationPasswords, "Stacje robocze – Zasady haseł", true, null));

        var note = Assert.Single(h.Windows.Previews[^1].Notes);
        Assert.Contains("dc02.contoso.test", note, StringComparison.Ordinal);
    }

    private static async Task<ChangePlan> CopyPlanAsync(GroupPolicyHarness h)
    {
        await using var sysvol = await h.Sysvol.OpenForWriteAsync(h.Session, CancellationToken.None);
        return await h.Planner.PlanCopyAsync(h.Session, sysvol, h.Sample.WorkstationPasswords, "Kopia – Zasady haseł", copyPermissions: false, CancellationToken.None);
    }
}
