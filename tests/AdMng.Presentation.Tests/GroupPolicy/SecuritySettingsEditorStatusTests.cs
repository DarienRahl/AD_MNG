using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.SecuritySettings;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.GroupPolicy;

/// <summary>
/// What the security settings editor says in its status line (track V, stage V2b-3; docs/dev/v-interface-renewal.md
/// "V2b-3 as built", <i>The two editors</i>): the number of defined and of changed settings as the summary; the read of the
/// three files as the operation of the document, with Stop; the plan of a save as the operation of the save; and - as the
/// state that lasts - that a file changed on SYSVOL under the edits, with Discard as its action. Which file cannot be read
/// and what the audit pages leave open are notes of the view: they say nothing to the line. What the editor edits and how
/// it saves is covered, unchanged, by <see cref="SecuritySettingsEditorTests"/>.
/// </summary>
public sealed class SecuritySettingsEditorStatusTests
{
    private const string AuditHeader = "Machine Name,Policy Target,Subcategory,Subcategory GUID,Inclusion Setting,Exclusion Setting,Setting Value";
    private const string GptTmpl = "Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf";
    private const string AuditCsv = "Machine/Microsoft/Windows NT/Audit/audit.csv";
    private const string GroupsXml = "Machine/Preferences/Groups/Groups.xml";

    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>"Lokacja Warszawa – Drukarki" with a small template (two rights), no audit policy and no GPP groups.</summary>
    private static async Task<GroupPolicyHarness> HarnessAsync(CultureInfo culture, ListLogger<PageStatusHolder>? logger = null)
    {
        var harness = logger is null ? await GroupPolicyHarness.CreateAsync(culture) : await GroupPolicyHarness.CreateAsync(culture, logger);
        harness.Sysvol.Put(harness.Sample.SitePrinters, GptTmpl, FakeSysvolFileReader.Utf16(FakeDirectoryBuilder.Template(
            "[System Access]", "MinimumPasswordLength = 12",
            "[Privilege Rights]", "SeDebugPrivilege = *S-1-5-32-544", "SeShutdownPrivilege = *S-1-5-32-544")));
        return harness;
    }

    private static SecuritySettingsEditorViewModel Create(GroupPolicyHarness h) =>
        (SecuritySettingsEditorViewModel)h.Factory.Create(
            new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.SecuritySettings) { GpoId = h.Sample.SitePrinters, Name = "Lokacja Warszawa – Drukarki" });

    private static async Task<SecuritySettingsEditorViewModel> OpenAsync(GroupPolicyHarness h)
    {
        var editor = Create(h);
        await editor.LoadAsync();
        return editor;
    }

    private static UserRightRowViewModel Right(SecuritySettingsEditorViewModel editor, string constant) => editor.UserRights.Single(r => r.Constant == constant);

    /// <summary>Another administrator saved another template.</summary>
    private static void AnotherAdministratorSaved(GroupPolicyHarness h) =>
        h.Sysvol.Put(h.Sample.SitePrinters, GptTmpl, FakeSysvolFileReader.Utf16(FakeDirectoryBuilder.Template("[Privilege Rights]", "SeShutdownPrivilege = *S-1-5-32-545")));

    // ---- The summary ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "Security settings: ", "Changed settings: 1 (not saved)")]
    [InlineData("pl-PL", "Ustawienia zabezpieczeń: zdefiniowane ", "Zmienione ustawienia: 1 (niezapisane)")]
    public async Task TheLine_SaysHowManySettingsAreDefined_AndHowManyAreChanged_InBothLanguages(string culture, string summaryStart, string changed)
    {
        var h = await HarnessAsync(CultureInfo.GetCultureInfo(culture));
        using var editor = Create(h);
        Assert.IsAssignableFrom<IHasPageStatus>(editor);
        Assert.Equal((PageStatusKind.Idle, string.Empty), (editor.Status.Current.Kind, editor.Status.Current.Message));

        await editor.LoadAsync();

        // At rest: what was read - once.
        var summary = editor.LoadedText;
        Assert.StartsWith(summaryStart, summary, StringComparison.Ordinal);
        Assert.Equal((PageStatusKind.Idle, summary), (editor.Status.Current.Kind, editor.Status.Current.Message));
        Assert.Null(editor.Status.Lasting);
        Assert.Null(editor.Status.Current.Action);

        // An edit joins the summary with the number of changed settings.
        Right(editor, "SeBackupPrivilege").IsDefined = true;
        Assert.Equal(changed, editor.SelectionText);
        Assert.Equal((PageStatusKind.Idle, $"{summary} · {changed}"), (editor.Status.Current.Kind, editor.Status.Current.Message));
    }

    // ---- Reading ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "Reading “Security settings – Lokacja Warszawa – Drukarki”…", "Stopping…",
        "Reading “Security settings – Lokacja Warszawa – Drukarki” was stopped: the tab may not show everything. Press Refresh to read it again.", "Refresh")]
    [InlineData("pl-PL", "Wczytywanie „Ustawienia zabezpieczeń – Lokacja Warszawa – Drukarki”…", "Zatrzymywanie…",
        "Wczytywanie „Ustawienia zabezpieczeń – Lokacja Warszawa – Drukarki” zatrzymano: karta może nie pokazywać wszystkiego. Wybierz Odśwież, aby wczytać ją ponownie.", "Odśwież")]
    public async Task Reading_IsTheOperationOfTheDocument_AndStopReturnsControl_WithoutLosingTheEdits(
        string culture, string reading, string stopping, string stoppedText, string refreshLabel)
    {
        var h = await HarnessAsync(CultureInfo.GetCultureInfo(culture));
        using var editor = await OpenAsync(h);
        Right(editor, "SeBackupPrivilege").IsDefined = true;
        var seen = new StatusRecorder(editor.Status);

        // The first file of the GPO is read by a call the test holds.
        var hold = h.Sysvol.HoldNext("Read", path => path?.Contains("GptTmpl", StringComparison.OrdinalIgnoreCase) == true);
        var loading = editor.LoadAsync();
        await hold.Reached.WaitAsync(HeldDirectoryCall.Limit, Ct);

        Assert.True(editor.IsLoading);
        var working = editor.Status.Current;
        Assert.Equal((PageStatusKind.Working, reading), (working.Kind, working.Message));
        Assert.Equal("GroupPolicy.Status.Reading", working.Log.ToString());
        Assert.False(working.CanClose);
        var stop = Assert.IsType<PageStatusAction>(working.Action);
        Assert.Same(editor.Status.CancelCommand, stop.Command);

        // Stop: "Stopping…" at once, although the call to SYSVOL still has to return.
        stop.Command.Execute(stop.Parameter);
        Assert.True(editor.Status.IsCancelling);
        Assert.Equal((PageStatusKind.Working, stopping), (editor.Status.Current.Kind, editor.Status.Current.Message));
        hold.Release();
        await loading.WaitAsync(HeldDirectoryCall.Limit, Ct);

        // Stopped: the state of the document until it is read again - with Refresh. What was typed is not lost.
        Assert.False(editor.IsLoading);
        var stopped = editor.Status.Current;
        Assert.Equal((PageStatusKind.Attention, stoppedText), (stopped.Kind, stopped.Message));
        Assert.False(stopped.CanClose);
        Assert.Same(stopped, editor.Status.Lasting);
        var refresh = Assert.IsType<PageStatusAction>(stopped.Action);
        Assert.Equal(refreshLabel, refresh.Label);
        Assert.Same(editor.RefreshCommand, refresh.Command);
        Assert.True(editor.HasChanges);
        Assert.Equal([(PageStatusKind.Working, reading), (PageStatusKind.Working, stopping), (PageStatusKind.Attention, stoppedText)], seen.Plain);

        // Refresh reads the document on top of the edits: the summary, and "stopped" is gone with the read.
        await editor.RefreshCommand.ExecuteAsync(null);
        Assert.Null(editor.Status.Lasting);
        Assert.True(editor.HasChanges);
        Assert.Equal(PageStatusKind.Idle, editor.Status.Current.Kind);
        Assert.EndsWith(" · " + editor.SelectionText, editor.Status.Current.Message, StringComparison.Ordinal);
    }

    // ---- A file that changed under the edits ------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "The security settings of this GPO changed on {0} since the edits started. The save will be refused as a conflict: discard your changes to see the current settings.", "Discard changes")]
    [InlineData("pl-PL", "Ustawienia zabezpieczeń tego GPO zmieniły się na {0} od rozpoczęcia edycji. Zapis zostanie odrzucony jako konflikt: odrzuć swoje zmiany, aby zobaczyć aktualne ustawienia.", "Odrzuć zmiany")]
    public async Task AFileThatChangedUnderTheEdits_IsTheStateOfTheDocument_WithDiscardAsItsAction(string culture, string text, string discardLabel)
    {
        var h = await HarnessAsync(CultureInfo.GetCultureInfo(culture));
        using var editor = await OpenAsync(h);
        Right(editor, "SeBackupPrivilege").IsDefined = true;
        var seen = new StatusRecorder(editor.Status);
        AnotherAdministratorSaved(h);

        await editor.LoadAsync();

        // The state of the document: it says what happened, what it means for the save and what to do - and it lasts.
        var expected = string.Format(CultureInfo.InvariantCulture, text, h.Session.Info.Server);
        Assert.Equal(expected, editor.ConflictNote);
        var conflict = editor.Status.Current;
        Assert.Equal((PageStatusKind.Attention, expected), (conflict.Kind, conflict.Message));
        Assert.Equal("SecSettings.ConflictNote", conflict.Log.ToString());
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
        Assert.False(Right(editor, "SeBackupPrivilege").IsDefined);
        Assert.True(Right(editor, "SeShutdownPrivilege").IsDefined);
        Assert.Equal(editor.LoadedText, editor.Status.Current.Message);
    }

    [Fact]
    public async Task TheConflict_IsSaidInTheNewLanguageAtOnce_NotWhenTheReadIsThrough()
    {
        var h = await HarnessAsync(English);
        using var editor = await OpenAsync(h);
        Right(editor, "SeBackupPrivilege").IsDefined = true;
        AnotherAdministratorSaved(h);
        await editor.LoadAsync();
        Assert.StartsWith("The security settings of this GPO changed on ", editor.Status.Current.Message, StringComparison.Ordinal);

        // The language changes and the document reads itself again: while that read runs the state is already Polish.
        var hold = h.Sysvol.HoldNext("Read");
        h.B2.Localizer.SetCulture(Polish);
        await hold.Reached.WaitAsync(HeldDirectoryCall.Limit, Ct);
        Assert.StartsWith("Ustawienia zabezpieczeń tego GPO zmieniły się na ", editor.Status.Lasting!.Message, StringComparison.Ordinal);
        Assert.Equal("Odrzuć zmiany", editor.Status.Lasting.Action!.Label);
        Assert.Equal(PageStatusKind.Working, editor.Status.Current.Kind); // the read still has the line

        hold.Release();
        await B2Harness.WaitUntilAsync(() => !editor.IsLoading && editor.Status.Current.Kind == PageStatusKind.Attention);
        Assert.StartsWith("Ustawienia zabezpieczeń tego GPO zmieniły się na ", editor.Status.Current.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutASession_TheLineSaysThat_BeforeAnythingElseOfTheEditor()
    {
        var h = await HarnessAsync(English);
        using var editor = await OpenAsync(h);
        Right(editor, "SeBackupPrivilege").IsDefined = true;
        AnotherAdministratorSaved(h);
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
    public async Task AFileThatCannotBeRead_IsANoteOfTheView_NotStatus()
    {
        var h = await HarnessAsync(English);
        h.Sysvol.PutText(h.Sample.SitePrinters, GroupsXml, "<Groups><niedomknięty>");
        h.Sysvol.Fail("Read", SysvolWriteError.AccessDenied, "audit.csv");

        using var editor = await OpenAsync(h);

        // The pages of the other files are there, with the sentence next to them; the line keeps the summary.
        Assert.Contains(GroupsXml, editor.FileNote, StringComparison.Ordinal);
        Assert.Contains(AuditCsv, editor.FileNote, StringComparison.Ordinal);
        Assert.False(editor.CanEditGroups);
        Assert.True(editor.CanEditTemplate);
        Assert.Null(editor.Status.Lasting);
        Assert.Equal((PageStatusKind.Idle, editor.LoadedText), (editor.Status.Current.Kind, editor.Status.Current.Message));
        Assert.DoesNotContain(GroupsXml, editor.Status.Current.Message, StringComparison.Ordinal);
        Assert.Null(editor.ConflictNote);
    }

    [Fact]
    public async Task WhatTheAuditPagesLeaveOpen_IsANoteOfThePage_NotStatus()
    {
        var h = await HarnessAsync(English);
        h.Sysvol.Put(h.Sample.SitePrinters, GptTmpl, FakeSysvolFileReader.Utf16(FakeDirectoryBuilder.Template("[Event Audit]", "AuditLogonEvents = 3")));
        h.Sysvol.PutText(h.Sample.SitePrinters, AuditCsv, AuditHeader + "\r\n,System,Audit Logon,{0cce9215-69ae-11d9-bed3-505054503030},Success and Failure,,3\r\n");

        using var editor = await OpenAsync(h);

        // Both kinds of audit are configured and the subcategories are not forced: a sentence about the pages.
        Assert.NotNull(editor.AuditNote);
        Assert.Null(editor.Status.Lasting);
        Assert.Equal((PageStatusKind.Idle, editor.LoadedText), (editor.Status.Current.Kind, editor.Status.Current.Message));
        Assert.DoesNotContain(editor.AuditNote, editor.Status.Current.Message, StringComparison.Ordinal);
    }

    // ---- Saving ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en-US", "Security settings of GPO {0}: preparing the changes…", "Reading")]
    [InlineData("pl-PL", "Ustawienia zabezpieczeń GPO {0}: przygotowywanie zmian…", "Wczytywanie")]
    public async Task ASave_SaysThatItsPlanIsBeingMade_ThenThePreviewHasTheWord_AndTheDocumentIsReadAgain(string culture, string preparing, string reading)
    {
        var h = await HarnessAsync(CultureInfo.GetCultureInfo(culture));
        using var editor = await OpenAsync(h);
        Right(editor, "SeBackupPrivilege").IsDefined = true;
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
        Right(editor, "SeBackupPrivilege").IsDefined = true;
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
        Assert.True(Right(editor, "SeBackupPrivilege").IsDefined);
        var failed = editor.Status.Current;
        Assert.Equal(PageStatusKind.Error, failed.Kind);
        Assert.StartsWith($"Security settings of GPO {editor.GpoName} – nothing was changed: ", failed.Message, StringComparison.Ordinal);
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
        Assert.Contains("SeBackupPrivilege", GpoText.DecodeUnicodeText(h.Sysvol.Get(h.Sample.SitePrinters, GptTmpl)!), StringComparison.Ordinal);
        Assert.Empty(h.B2.Dialogs.Errors);
    }

    // ---- The log copy -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheLogCopies_CarryKeysAndCodes_NeverANameOrAServerText()
    {
        var logger = new ListLogger<PageStatusHolder>();
        var h = await HarnessAsync(English, logger);
        using var editor = await OpenAsync(h);
        Right(editor, "SeBackupPrivilege").IsDefined = true;
        AnotherAdministratorSaved(h);
        await editor.LoadAsync(); // the conflict

        // A save that cannot be planned: the code of the failure, not its text.
        h.Session.BeforeOperation = _ => throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "serwer dc01.contoso.test nie odpowiada");
        await editor.SaveCommand.ExecuteAsync(null);

        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains(messages, m => m.Contains("GroupPolicy.Status.Reading", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("SecSettings.ConflictNote", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("GroupPolicy.Status.NotChanged", StringComparison.Ordinal) && m.Contains("[ServerUnavailable]", StringComparison.Ordinal));
        Assert.DoesNotContain("Lokacja Warszawa", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Security settings", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("contoso", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nie odpowiada", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("changed on", logger.AllText, StringComparison.Ordinal);
    }
}
