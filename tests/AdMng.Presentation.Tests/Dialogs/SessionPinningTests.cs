using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Model;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Localization;
using AdMng.Presentation.PropertySheets;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Dialogs;

/// <summary>
/// Windows stay pinned to the session they were opened in: after a disconnect and a new connection (here: another
/// identity on the same domain) they refuse to read and write instead of acting with the new identity.
/// </summary>
public class SessionPinningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The same sample company, bound as another (more privileged) identity.</summary>
    private static FakeDirectoryBuilder Admin(B2Harness harness)
    {
        var info = FakeDirectorySession.CreateInfo() with { BoundIdentity = @"CONTOSO\admin.domeny" };
        var admin = new FakeDirectoryBuilder(timeProvider: harness.Time, info: info);
        admin.AddSampleCompany();
        return admin;
    }

    private static FieldViewModel Text(PropertySheetViewModel sheet, string page, string attribute) =>
        Assert.Single(Assert.IsType<FieldsPageViewModel>(Assert.Single(sheet.Pages, p => p.Key == page)).Fields.OfType<FieldViewModel>(), f => f.Attribute == attribute);

    [Fact]
    public async Task PropertySheet_OfAnEndedSession_NeverAppliesWithTheNextIdentity()
    {
        var harness = await B2Harness.CreateAsync();
        using var sheet = harness.Factory.CreatePropertySheet(harness.Company.JanKowalski);
        await sheet.InitializeAsync();
        Text(sheet, L.Pages.Organization, Attr.Title).Value = "Kierownik działu IT";
        Assert.True(sheet.ApplyCommand.CanExecute(null));
        var admin = Admin(harness);

        await harness.ReconnectAsync(admin.Session);

        var ended = harness.Localizer[L.WindowSession.Ended];
        Assert.True(sheet.IsSessionEnded);
        Assert.Equal(ended, sheet.SessionEndedMessage);
        Assert.Equal(ended, sheet.LoadError);
        Assert.True(sheet.HasChanges);
        Assert.False(sheet.ApplyCommand.CanExecute(null));
        Assert.False(sheet.RefreshCommand.CanExecute(null));

        // Even when invoked directly, nothing is planned or written - neither as the new identity nor in the old session.
        Assert.False(await sheet.ApplyChangesAsync());
        await sheet.LoadAsync();
        Assert.Equal(ended, sheet.ApplyError);
        Assert.Empty(admin.Session.Operations);
        Assert.Empty(harness.Session.Writes);
        Assert.Equal(["Administrator systemów"], admin.Session.GetValues(harness.Company.JanKowalski, Attr.Title));

        // Reopened, the sheet works in the current session.
        using var reopened = harness.Factory.CreatePropertySheet(harness.Company.JanKowalski);
        await reopened.InitializeAsync();
        Assert.False(reopened.IsSessionEnded);
        Text(reopened, L.Pages.Organization, Attr.Title).Value = "Kierownik działu IT";
        Assert.True(await reopened.ApplyChangesAsync());
        Assert.Equal("Kierownik działu IT", admin.Session.GetValues(harness.Company.JanKowalski, Attr.Title).Single());
    }

    [Fact]
    public async Task BatchEditor_OfAnEndedSession_DoesNotPlanOrExecute()
    {
        var harness = await B2Harness.CreateAsync();
        using var editor = harness.Factory.CreateBatchEditor(new BatchEditorRequest([harness.Company.JanKowalski])
        {
            Operations = [new SetAttributeOperation(Attr.Title, "Konsultant")],
        });
        await editor.InitializeAsync();
        await editor.PreviewCommand.ExecuteAsync(null);
        Assert.True(editor.ExecuteCommand.CanExecute(null));
        var admin = Admin(harness);

        await harness.ReconnectAsync(admin.Session);

        var ended = harness.Localizer[L.WindowSession.Ended];
        Assert.True(editor.IsSessionEnded);
        Assert.Equal(ended, editor.Error);
        Assert.False(editor.ExecuteCommand.CanExecute(null));

        await editor.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal(BatchEditorStep.Preview, editor.Step);
        Assert.Equal(ended, editor.Error);
        Assert.Empty(admin.Session.Writes);
        Assert.Empty(harness.Session.Writes);
        Assert.Empty(harness.Activities.Activities);

        editor.BackToEditCommand.Execute(null);
        Assert.False(editor.PreviewCommand.CanExecute(null));
    }

    [Fact]
    public async Task Coordinator_RefusesToExecuteInASessionThatIsNoLongerCurrent()
    {
        var harness = await B2Harness.CreateAsync();
        var old = harness.Sessions.Current!;
        var plan = await harness.Planner.PlanAsync(old, new BatchRequest([harness.Company.JanKowalski], [new SetAttributeOperation(Attr.Title, "X")]), null, null, Ct);
        var admin = Admin(harness);
        await harness.ReconnectAsync(admin.Session);

        await Assert.ThrowsAsync<SessionEndedException>(() => harness.Coordinator.ExecutePlanAsync(plan, "Tytuł", old, cancellationToken: Ct));

        Assert.Empty(admin.Session.Writes);
        Assert.Empty(harness.Activities.Activities);
        var result = await harness.Coordinator.ExecutePlanAsync(
            await harness.Planner.PlanAsync(admin.Session, new BatchRequest([harness.Company.JanKowalski], [new SetAttributeOperation(Attr.Title, "X")]), null, null, Ct),
            "Tytuł",
            admin.Session,
            cancellationToken: Ct);
        Assert.Equal(1, result.SucceededCount);
    }

    [Fact]
    public async Task NewObjectAndResetPassword_OfAnEndedSession_DoNotWrite()
    {
        var harness = await B2Harness.CreateAsync();
        using var dialog = harness.Factory.CreateNewObject(ObjectKind.Group, harness.Company.GroupsOu);
        dialog.Name = "GG_Nowa";
        using var reset = harness.Factory.CreateResetPassword([harness.Company.JanKowalski]);
        await reset.InitializeAsync();
        var admin = Admin(harness);

        await harness.ReconnectAsync(admin.Session);

        var ended = harness.Localizer[L.WindowSession.Ended];
        Assert.False(dialog.CreateCommand.CanExecute(null));
        Assert.False(reset.ResetCommand.CanExecute(null));
        await dialog.CreateCommand.ExecuteAsync(null);
        await reset.ResetCommand.ExecuteAsync(null);
        Assert.Equal(ended, dialog.Error);
        Assert.Equal(ended, reset.Error);
        Assert.Null(dialog.CreatedDn);
        Assert.Empty(admin.Session.Writes);
    }

    [Fact]
    public async Task RecycleBinFindAndSessionLog_OfAnEndedSession_DoNotUseTheNextSession()
    {
        var harness = await B2Harness.CreateAsync();
        await harness.Session.DeleteAsync(harness.Company.JanKowalski, subtree: false, Ct);
        using var bin = harness.Factory.CreateRecycleBin();
        await bin.InitializeAsync();
        bin.SelectedItem = Assert.Single(bin.Items);
        Assert.True(bin.RestoreCommand.CanExecute(null));
        using var find = harness.Factory.CreateFind(null);
        find.NameText = "lukasz";
        await find.FindNowCommand.ExecuteAsync(null);
        Assert.Equal("Łukasz Wiśniewski", Assert.Single(find.Results.Items).Name);
        using var log = harness.Factory.CreateSessionLog();
        var admin = Admin(harness);
        await admin.Session.DeleteAsync(harness.Company.LukaszWisniewski, subtree: false, Ct);

        await harness.ReconnectAsync(admin.Session);
        await bin.Loading;

        var ended = harness.Localizer[L.WindowSession.Ended];
        Assert.False(bin.IsConnected);
        Assert.Equal(ended, bin.Error);
        Assert.Equal(ended, bin.BannerText);
        Assert.Empty(bin.Items);
        Assert.False(bin.RestoreCommand.CanExecute(null));
        Assert.False(bin.RefreshCommand.CanExecute(null));
        Assert.False(find.FindNowCommand.CanExecute(null));
        await find.FindNowCommand.ExecuteAsync(null);
        await find.Results.RefreshCommand.ExecuteAsync(null);
        Assert.Empty(find.Results.Items);
        Assert.False(find.OpenCommand.CanExecute(null));
        Assert.Equal(ended, find.SessionEndedMessage);
        Assert.False(log.OpenCommand.CanExecute(null));
        Assert.DoesNotContain(admin.Session.Operations, o => o.Kind is FakeOperationKind.Search or FakeOperationKind.VirtualListPage);
        Assert.Empty(harness.Dialogs.PropertiesShown);

        // The Recycle Bin opened again lists the deleted objects of the current session.
        using var reopened = harness.Factory.CreateRecycleBin();
        await reopened.InitializeAsync();
        Assert.Equal("Łukasz Wiśniewski", Assert.Single(reopened.Items).Name);
    }

    [Fact]
    public async Task Disconnect_WhileABatchRuns_CancelsItBeforeClosingTheSession_SoItsResultsStayCompleteForUndo()
    {
        var harness = await B2Harness.CreateAsync();
        await harness.Settings.UpdateAsync(s => s.BatchMaxParallelism = 1, Ct);
        using var shell = harness.CreateShell();
        await shell.InitializeAsync();
        var company = harness.Company;
        using var editor = harness.Factory.CreateBatchEditor(new BatchEditorRequest([company.JanKowalski, company.LukaszWisniewski, company.PawelLewandowski])
        {
            Operations = [new SetAttributeOperation(Attr.Title, "Konsultant")],
        });
        await editor.InitializeAsync();
        await editor.PreviewCommand.ExecuteAsync(null);
        using var writing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        harness.Session.BeforeOperation = operation =>
        {
            if (operation.IsWrite)
            {
                writing.Set();
                release.Wait(Ct);
            }
        };

        var execution = editor.ExecuteCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => writing.IsSet);
        harness.Dialogs.ConfirmAnswers.Enqueue(true);
        var disconnect = shell.DisconnectCommand.ExecuteAsync(null);
        Task finished;
        try
        {
            // The running batch is cancelled first; the session stays open while its write is in flight.
            Assert.Contains($"confirm:{harness.Localizer[L.Shell.DisconnectWhileRunning]}", harness.Dialogs.Shown);
            Assert.True(editor.CurrentActivity!.IsCancelling);
            Assert.False(harness.Session.IsDisposed);
            Assert.False(disconnect.IsCompleted);
        }
        finally
        {
            release.Set();
            finished = Task.WhenAll(execution, disconnect);
        }

        await finished;
        Assert.True(harness.Session.IsDisposed);
        Assert.False(shell.IsConnected);
        var result = editor.Result!;
        Assert.True(result.WasCancelled);
        Assert.Equal(
            [ObjectResultStatus.Succeeded, ObjectResultStatus.Cancelled, ObjectResultStatus.Cancelled],
            result.Objects.Select(o => o.Status));
        Assert.Equal("Konsultant", harness.Value(company.JanKowalski, Attr.Title));
        Assert.Equal("Programista", harness.Value(company.LukaszWisniewski, Attr.Title));

        // The write that was in flight is in the results, so its undo plan exists (to review or export).
        Assert.True(editor.UndoCommand.CanExecute(null));
        editor.UndoCommand.Execute(null);
        Assert.True(editor.IsRollbackPreview);
        Assert.Equal(company.JanKowalski, Assert.Single(editor.PreviewObjects).Plan.DistinguishedName);
    }

    [Fact]
    public async Task WindowsThatDoNotUseTheSession_DoNotEnd()
    {
        var harness = await B2Harness.CreateAsync();
        using var history = harness.Factory.CreatePowerShellHistory();
        using var settings = harness.Factory.CreateSettings();

        await harness.ReconnectAsync(Admin(harness).Session);

        Assert.False(history.IsSessionEnded);
        Assert.False(settings.IsSessionEnded);
    }
}
