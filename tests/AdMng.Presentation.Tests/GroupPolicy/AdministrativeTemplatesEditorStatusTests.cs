using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.GroupPolicy;

/// <summary>
/// What the administrative templates editor says in its status line (track V, stage V2b-3; docs/dev/v-interface-renewal.md
/// "V2b-3 as built", <i>The two editors</i>): the number of settings and of the changed ones as the summary; the read of the
/// templates and of the two Registry.pol files as the operation of the document, with the stage that takes the time and
/// with Stop; the plan of a save as the operation of the save; and - as the state that lasts - that a Registry.pol changed
/// on SYSVOL under the edits, with Discard as its action. Where the templates come from and which Registry.pol cannot be
/// read are notes of the view: they say nothing to the line. What the editor edits and how it saves is covered, unchanged,
/// by <see cref="AdministrativeTemplatesEditorTests"/>.
/// </summary>
public sealed class AdministrativeTemplatesEditorStatusTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<GroupPolicyHarness> HarnessAsync(CultureInfo culture, bool centralStore = true, ListLogger<PageStatusHolder>? logger = null)
    {
        var harness = logger is null ? await GroupPolicyHarness.CreateAsync(culture) : await GroupPolicyHarness.CreateAsync(culture, logger);
        if (centralStore)
        {
            harness.AddCentralStore();
        }

        return harness;
    }

    private static AdministrativeTemplatesEditorViewModel Create(GroupPolicyHarness h) =>
        (AdministrativeTemplatesEditorViewModel)h.Factory.Create(
            new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.AdministrativeTemplates) { GpoId = h.Sample.SitePrinters, Name = "Lokacja Warszawa – Drukarki" });

    private static async Task<AdministrativeTemplatesEditorViewModel> OpenAsync(GroupPolicyHarness h)
    {
        var editor = Create(h);
        await editor.LoadAsync();
        return editor;
    }

    /// <summary>"Turn off the preview pane" / "Wyłącz okienko podglądu" of the computer configuration, selected.</summary>
    private static PolicyRowViewModel SelectPreviewPane(AdministrativeTemplatesEditorViewModel editor, string name)
    {
        editor.SelectedNode = editor.Roots.Single(r => r.Scope == PolicyClass.Machine).Children.Single(c => c.Name == "System");
        var row = editor.Policies.Single(p => p.Name == name);
        editor.SelectedPolicy = row;
        return row;
    }

    // ---- The summary ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "Turn off the preview pane", "Changed settings: 1 (not saved)")]
    [InlineData("pl-PL", "Wyłącz okienko podglądu", "Zmienione ustawienia: 1 (niezapisane)")]
    public async Task TheLine_SaysHowManySettingsThereAre_AndHowManyAreChanged_InBothLanguages(string culture, string policy, string changed)
    {
        var h = await HarnessAsync(CultureInfo.GetCultureInfo(culture));
        using var editor = Create(h);
        Assert.IsAssignableFrom<IHasPageStatus>(editor);
        Assert.Equal((PageStatusKind.Idle, string.Empty), (editor.Status.Current.Kind, editor.Status.Current.Message));

        await editor.LoadAsync();

        // At rest: what was read - once, the numbers of the settings and of the configured ones.
        var summary = editor.LoadedText;
        Assert.Contains(culture == "pl-PL" ? "Ustawienia: " : " settings, ", summary, StringComparison.Ordinal);
        Assert.Equal((PageStatusKind.Idle, summary), (editor.Status.Current.Kind, editor.Status.Current.Message));
        Assert.Null(editor.Status.Lasting);
        Assert.Null(editor.Status.Current.Action);

        // An edit joins the summary with the number of changed settings; the numbers follow the edit.
        SelectPreviewPane(editor, policy);
        editor.Editor!.IsEnabled = true;
        Assert.Equal(changed, editor.SelectionText);
        Assert.NotEqual(summary, editor.LoadedText); // one setting more is configured
        Assert.Equal((PageStatusKind.Idle, $"{editor.LoadedText} · {changed}"), (editor.Status.Current.Kind, editor.Status.Current.Message));
    }

    // ---- Reading ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "Reading “Administrative templates – Lokacja Warszawa – Drukarki”…", "Reading the ADMX and ADML files…", "Stopping…",
        "Reading “Administrative templates – Lokacja Warszawa – Drukarki” was stopped: the tab may not show everything. Press Refresh to read it again.", "Refresh")]
    [InlineData("pl-PL", "Wczytywanie „Szablony administracyjne – Lokacja Warszawa – Drukarki”…", "Wczytywanie plików ADMX i ADML…", "Zatrzymywanie…",
        "Wczytywanie „Szablony administracyjne – Lokacja Warszawa – Drukarki” zatrzymano: karta może nie pokazywać wszystkiego. Wybierz Odśwież, aby wczytać ją ponownie.", "Odśwież")]
    public async Task Reading_IsTheOperationOfTheDocument_WithTheStageThatTakesTheTime_AndStopReturnsControl(
        string culture, string reading, string stage, string stopping, string stoppedText, string refreshLabel)
    {
        var h = await HarnessAsync(CultureInfo.GetCultureInfo(culture));
        using var editor = Create(h);
        var seen = new StatusRecorder(editor.Status);

        // The templates of the central store are listed by a call the test holds: the read stands at its stage.
        var hold = h.Sysvol.HoldNext("ListDefinitions");
        var loading = editor.LoadAsync();
        await hold.Reached.WaitAsync(HeldDirectoryCall.Limit, Ct);

        Assert.True(editor.IsLoading);
        var working = editor.Status.Current;
        Assert.Equal((PageStatusKind.Working, stage), (working.Kind, working.Message));
        Assert.Equal("GroupPolicy.Status.Reading", working.Log.ToString()); // the stage is not a new message
        Assert.False(working.CanClose);
        var stop = Assert.IsType<PageStatusAction>(working.Action);
        Assert.Same(editor.Status.CancelCommand, stop.Command);

        // Stop: "Stopping…" at once, although the call to SYSVOL still has to return.
        stop.Command.Execute(stop.Parameter);
        Assert.True(editor.Status.IsCancelling);
        Assert.Equal((PageStatusKind.Working, stopping), (editor.Status.Current.Kind, editor.Status.Current.Message));
        Assert.True(editor.IsLoading);
        hold.Release();
        await loading.WaitAsync(HeldDirectoryCall.Limit, Ct);

        // Stopped: the state of the document until it is read again - with Refresh, and it cannot be closed.
        Assert.False(editor.IsLoading);
        var stopped = editor.Status.Current;
        Assert.Equal((PageStatusKind.Attention, stoppedText), (stopped.Kind, stopped.Message));
        Assert.False(stopped.CanClose);
        Assert.Same(stopped, editor.Status.Lasting);
        var refresh = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal(refreshLabel, refresh.Label);
        Assert.Same(editor.RefreshCommand, refresh.Command);
        Assert.Equal(
            [(PageStatusKind.Working, reading), (PageStatusKind.Working, stage), (PageStatusKind.Working, stopping), (PageStatusKind.Attention, stoppedText)],
            seen.Plain);

        // Refresh reads the document: the summary, the tree, and "stopped" is gone with the read.
        await editor.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(2, editor.Roots.Count);
        Assert.Null(editor.Status.Lasting);
        Assert.Equal((PageStatusKind.Idle, editor.LoadedText), (editor.Status.Current.Kind, editor.Status.Current.Message));
    }

    [Fact]
    public async Task AStoppedRead_KeepsTheEdits_AndTheRefreshOfTheLineReadsAgainOnTopOfThem()
    {
        var h = await HarnessAsync(English);
        using var editor = await OpenAsync(h);
        SelectPreviewPane(editor, "Turn off the preview pane");
        editor.Editor!.IsEnabled = true;
        Assert.True(editor.HasChanges);

        var hold = h.Sysvol.HoldNext("ListDefinitions");
        h.Definitions.Invalidate(); // the templates are read again
        var loading = editor.LoadAsync();
        await hold.Reached.WaitAsync(HeldDirectoryCall.Limit, Ct);
        editor.Status.CancelCommand.Execute(null);
        hold.Release();
        await loading.WaitAsync(HeldDirectoryCall.Limit, Ct);

        Assert.Equal(PageStatusKind.Attention, editor.Status.Current.Kind);
        Assert.True(editor.HasChanges); // what was typed is not lost by a read that was stopped
        Assert.Equal(1, editor.ChangedCount);

        await editor.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(PageStatusKind.Idle, editor.Status.Current.Kind);
        Assert.True(editor.HasChanges);
        Assert.Equal($"{editor.LoadedText} · Changed settings: 1 (not saved)", editor.Status.Current.Message);
    }

    // ---- A Registry.pol that changed under the edits ----------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "Turn off the preview pane", "The settings of this GPO changed on {0} since you started editing. Saving will be refused as a conflict: discard your changes to see the current settings.", "Discard changes")]
    [InlineData("pl-PL", "Wyłącz okienko podglądu", "Ustawienia tego GPO zmieniły się na {0} od rozpoczęcia edycji. Zapis zostanie odrzucony jako konflikt: odrzuć swoje zmiany, aby zobaczyć aktualne ustawienia.", "Odrzuć zmiany")]
    public async Task AFileThatChangedUnderTheEdits_IsTheStateOfTheDocument_WithDiscardAsItsAction(string culture, string policy, string text, string discardLabel)
    {
        var h = await HarnessAsync(CultureInfo.GetCultureInfo(culture));
        using var editor = await OpenAsync(h);
        var row = SelectPreviewPane(editor, policy);
        editor.Editor!.IsEnabled = true;
        var summary = editor.Status.Current.Message;
        var seen = new StatusRecorder(editor.Status);
        var other = new RegistryPolicyDocument();
        PolicyEngine.Write(row.Definition.Policy, PolicySetting.Disabled, other);
        h.Sysvol.Put(h.Sample.SitePrinters, "Machine/Registry.pol", other.ToBytes()); // another administrator saved

        await editor.LoadAsync();

        // The state of the document: it says what happened, what it means for the save and what to do - and it lasts.
        var expected = string.Format(CultureInfo.InvariantCulture, text, h.Session.Info.Server);
        Assert.Equal(expected, editor.ConflictNote);
        var conflict = editor.Status.Current;
        Assert.Equal((PageStatusKind.Attention, expected), (conflict.Kind, conflict.Message));
        Assert.Equal("AdmTemplates.ConflictNote", conflict.Log.ToString());
        Assert.False(conflict.CanClose);
        Assert.Same(conflict, editor.Status.Lasting);
        var discard = Assert.IsType<PageStatusAction>(conflict.Action);
        Assert.Equal(discardLabel, discard.Label);
        Assert.Same(editor.DiscardCommand, discard.Command);
        Assert.True(editor.HasChanges); // the edits are kept
        Assert.Equal(PageStatusKind.Working, seen.Plain[0].Kind);
        Assert.Equal(PageStatusKind.Attention, seen.Plain[^1].Kind);
        Assert.DoesNotContain(seen.Plain, s => s.Kind is PageStatusKind.Idle or PageStatusKind.Error);

        // Discard, from the line, asks first; then the current file is shown, the conflict is gone and the summary is back.
        h.B2.Dialogs.ConfirmAnswers.Enqueue(true);
        discard.Command.Execute(discard.Parameter);
        await B2Harness.WaitUntilAsync(() => !editor.IsLoading && editor.Status.Current.Kind == PageStatusKind.Idle);
        Assert.Null(editor.ConflictNote);
        Assert.Null(editor.Status.Lasting);
        Assert.False(editor.HasChanges);
        Assert.Equal(PolicyState.Disabled, editor.Policies.Single(p => p.Definition.Id == row.Definition.Id).State);
        Assert.NotEqual(string.Empty, editor.Status.Current.Message);
        Assert.NotEqual(expected, editor.Status.Current.Message);
        Assert.NotEqual(string.Empty, summary);
    }

    [Fact]
    public async Task TheConflict_IsSaidInTheNewLanguageAtOnce_NotWhenTheReadIsThrough()
    {
        var h = await HarnessAsync(English);
        using var editor = await OpenAsync(h);
        var row = SelectPreviewPane(editor, "Turn off the preview pane");
        editor.Editor!.IsEnabled = true;
        var other = new RegistryPolicyDocument();
        PolicyEngine.Write(row.Definition.Policy, PolicySetting.Disabled, other);
        h.Sysvol.Put(h.Sample.SitePrinters, "Machine/Registry.pol", other.ToBytes());
        await editor.LoadAsync();
        Assert.StartsWith("The settings of this GPO changed on ", editor.Status.Current.Message, StringComparison.Ordinal);

        // The language changes and the document reads itself again: while that read runs the state is already Polish.
        var hold = h.Sysvol.HoldNext("ListDefinitions");
        h.B2.Localizer.SetCulture(Polish);
        await hold.Reached.WaitAsync(HeldDirectoryCall.Limit, Ct);
        Assert.StartsWith("Ustawienia tego GPO zmieniły się na ", editor.Status.Lasting!.Message, StringComparison.Ordinal);
        Assert.Equal("Odrzuć zmiany", editor.Status.Lasting.Action!.Label);
        Assert.Equal(PageStatusKind.Working, editor.Status.Current.Kind); // the read still has the line

        hold.Release();
        await B2Harness.WaitUntilAsync(() => !editor.IsLoading && editor.Status.Current.Kind == PageStatusKind.Attention);
        Assert.StartsWith("Ustawienia tego GPO zmieniły się na ", editor.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutASession_TheLineSaysThat_BeforeAnythingElseOfTheEditor()
    {
        var h = await HarnessAsync(English);
        using var editor = await OpenAsync(h);
        var row = SelectPreviewPane(editor, "Turn off the preview pane");
        editor.Editor!.IsEnabled = true;
        var other = new RegistryPolicyDocument();
        PolicyEngine.Write(row.Definition.Policy, PolicySetting.Disabled, other);
        h.Sysvol.Put(h.Sample.SitePrinters, "Machine/Registry.pol", other.ToBytes());
        await editor.LoadAsync();
        Assert.Equal(PageStatusKind.Attention, editor.Status.Current.Kind);

        await h.B2.Sessions.DisconnectAsync();
        await B2Harness.WaitUntilAsync(() => editor.Status.Current.Kind == PageStatusKind.Error);

        // What every document has comes first; the conflict is still true behind it.
        Assert.Equal("Not connected to a domain.", editor.Status.Current.Message);
        Assert.NotNull(editor.ConflictNote);
    }

    // ---- The notes ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WhereTheTemplatesComeFrom_AndAFileThatCannotBeRead_AreNotesOfTheView_NotStatus()
    {
        var h = await HarnessAsync(English, centralStore: false);
        h.Sysvol.Put(h.Sample.SitePrinters, "User/Registry.pol", [1, 2, 3, 4, 5, 6, 7, 8, 9]);
        using var editor = await OpenAsync(h);

        // Neither says anything to the line: the content is there, with these sentences next to it.
        Assert.True(editor.IsCentralStoreMissing);
        Assert.Contains("No ADMX templates", editor.DefinitionsNote, StringComparison.Ordinal);
        Assert.Contains("User/Registry.pol", editor.FileNote, StringComparison.Ordinal);
        Assert.Null(editor.Status.Lasting);
        Assert.Equal((PageStatusKind.Idle, editor.LoadedText), (editor.Status.Current.Kind, editor.Status.Current.Message));
        Assert.DoesNotContain("User/Registry.pol", editor.Status.Current.Message, StringComparison.Ordinal);
        Assert.Null(editor.ConflictNote);
    }

    // ---- Saving ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "Turn off the preview pane", "Administrative templates of {0}: preparing the changes…", "Reading")]
    [InlineData("pl-PL", "Wyłącz okienko podglądu", "Szablony administracyjne GPO {0}: przygotowywanie zmian…", "Wczytywanie")]
    public async Task ASave_SaysThatItsPlanIsBeingMade_ThenThePreviewHasTheWord_AndTheDocumentIsReadAgain(string culture, string policy, string preparing, string reading)
    {
        var h = await HarnessAsync(CultureInfo.GetCultureInfo(culture));
        using var editor = await OpenAsync(h);
        SelectPreviewPane(editor, policy);
        editor.Editor!.IsEnabled = true;
        (PageStatusKind Kind, bool Working, bool Saving)? duringPreview = null;
        h.Windows.BeforePreview = _ =>
        {
            duringPreview = (editor.Status.Current.Kind, editor.Status.IsWorking, editor.IsSaving);
            return Task.CompletedTask;
        };
        var seen = new StatusRecorder(editor.Status);

        await editor.SaveCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => !editor.IsLoading && !editor.HasChanges && editor.Status.Current.Kind == PageStatusKind.Idle);

        // While the preview window is open the line is the document's again: the window says its own status.
        Assert.Equal((PageStatusKind.Idle, false, true), duringPreview);
        Assert.Single(h.Windows.Previews);
        Assert.Empty(h.B2.Dialogs.Errors);
        Assert.False(editor.IsSaving);

        // Preparing (with Stop), the summary while the preview is open, the read after the save, the summary.
        var said = seen.Plain;
        Assert.Equal((PageStatusKind.Working, string.Format(CultureInfo.InvariantCulture, preparing, editor.GpoName)), said[0]);
        Assert.Equal(PageStatusKind.Idle, said[1].Kind);
        Assert.Contains(said.Skip(2), s => s.Kind == PageStatusKind.Working && s.Message.StartsWith(reading, StringComparison.Ordinal));
        Assert.Equal(PageStatusKind.Idle, said[^1].Kind);
        Assert.DoesNotContain(said, s => s.Kind is PageStatusKind.Attention or PageStatusKind.Error or PageStatusKind.Done);
    }

    [Fact]
    public async Task ASaveThatCannotBePlanned_SaysWhyInTheLine_KeepsTheEdits_AndRetryMakesThePlanOfTheSameSave()
    {
        var h = await HarnessAsync(English);
        using var editor = await OpenAsync(h);
        SelectPreviewPane(editor, "Turn off the preview pane");
        editor.Editor!.IsEnabled = true;
        var fail = 1;
        h.Session.BeforeOperation = _ =>
        {
            if (Volatile.Read(ref fail) == 1)
            {
                throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "the domain controller does not answer (test)");
            }
        };

        await editor.SaveCommand.ExecuteAsync(null);

        // The editor is as it was - editable, with its edits - and the line says what was not done, why and what to do.
        Assert.Empty(h.Windows.Previews);
        Assert.Empty(h.B2.Dialogs.Errors);
        Assert.False(editor.IsSaving);
        Assert.True(editor.HasChanges);
        var failed = editor.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.StartsWith($"Administrative templates of {editor.GpoName} – nothing was changed: ", failed.Message, StringComparison.Ordinal);
        Assert.EndsWith(" Press Retry to try again. (ServerUnavailable)", failed.Message, StringComparison.Ordinal);
        Assert.Equal("GroupPolicy.Status.NotChanged [ServerUnavailable]", failed.Log.ToString());
        Assert.True(failed.CanClose);
        Assert.Null(editor.Status.Lasting);
        var retry = Assert.IsType<PageStatusAction>(failed.Action);
        Assert.Equal("Retry", retry.Label);

        // The domain controller answers again: Retry leads to the preview, and the save is made.
        Volatile.Write(ref fail, 0);
        retry.Command.Execute(retry.Parameter);
        await B2Harness.WaitUntilAsync(() => h.Windows.Previews.Count == 1 && !editor.IsLoading && !editor.HasChanges);
        var written = RegistryPolicyDocument.Parse(h.Sysvol.Get(h.Sample.SitePrinters, "Machine/Registry.pol"));
        Assert.Equal(PolicyState.Enabled, PolicyEngine.Read(editor.Policies.Single(p => p.Name == "Turn off the preview pane").Definition.Policy, written.View).State);
        Assert.Empty(h.B2.Dialogs.Errors);
    }

    // ---- The log copy -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheLogCopies_CarryKeysAndCodes_NeverANameOrAServerText()
    {
        var logger = new ListLogger<PageStatusHolder>();
        var h = await HarnessAsync(English, logger: logger);
        using var editor = await OpenAsync(h);
        var row = SelectPreviewPane(editor, "Turn off the preview pane");
        editor.Editor!.IsEnabled = true;
        var other = new RegistryPolicyDocument();
        PolicyEngine.Write(row.Definition.Policy, PolicySetting.Disabled, other);
        h.Sysvol.Put(h.Sample.SitePrinters, "Machine/Registry.pol", other.ToBytes());
        await editor.LoadAsync(); // the conflict

        // A save that cannot be planned: the code of the failure, not its text.
        h.Session.BeforeOperation = _ => throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "serwer dc01.contoso.test nie odpowiada");
        await editor.SaveCommand.ExecuteAsync(null);

        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains(messages, m => m.Contains("GroupPolicy.Status.Reading", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("AdmTemplates.ConflictNote", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("GroupPolicy.Status.NotChanged", StringComparison.Ordinal) && m.Contains("[ServerUnavailable]", StringComparison.Ordinal));
        Assert.DoesNotContain("Lokacja Warszawa", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Administrative templates", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("ADMX", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("contoso", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nie odpowiada", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("changed on", logger.AllText, StringComparison.Ordinal);
    }
}
