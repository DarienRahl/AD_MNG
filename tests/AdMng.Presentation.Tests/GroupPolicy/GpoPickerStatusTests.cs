using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.Localization;
using AdMng.Testing;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Tests.GroupPolicy;

/// <summary>
/// The status line of the GPO picker ("Link an existing GPO…", "Compare with…"; track V, stage V2b-3,
/// docs/dev/v-interface-renewal.md "V2b-3 as built"): the read of the GPOs when the dialog opens is its operation, with
/// Stop; the list is the result, so the line then says how many GPOs it shows (with the filter: how many of them); a read
/// that was stopped or failed leaves the list empty, which is the state of the dialog until Retry reads it again; and the
/// line says for good that the dialog has no session. How a GPO is chosen is covered, unchanged, by the existing picker
/// tests.
/// </summary>
public sealed class GpoPickerStatusTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("en-US", "Reading the group policy objects of contoso.test…", "Group policy objects: ")]
    [InlineData("pl-PL", "Wczytywanie obiektów zasad grupy domeny contoso.test…", "Obiekty zasad grupy: ")]
    public async Task TheReadOfTheGpos_IsTheOperationOfTheDialog_WithStop_AndTheLineShowsTheCountAfterwards(string culture, string working, string found)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        var total = (await h.Services.Reader.ReadPoliciesAsync(h.Session, Ct)).Count;
        using var picker = NewPicker(h, exclude: []);
        Assert.IsAssignableFrom<IHasPageStatus>(picker);
        Assert.Same(PageStatus.Empty, picker.Status.Current);
        Assert.Null(picker.Status.Lasting);
        var seen = new StatusRecorder(picker.Status);

        using var held = new HeldDirectoryCall(h.Session);
        var opening = Task.Run(picker.InitializeAsync, Ct);
        await held.Reached.WaitAsync(Limit, Ct);

        // Working: what is read, with Stop; the dialog's own Cancel closes it at any time.
        Assert.True(picker.IsLoading);
        Assert.True(picker.Status.IsWorking);
        Assert.Equal(PageStatusKind.Working, picker.Status.Current.Kind);
        Assert.Equal(working, picker.Status.Current.Message);
        Assert.Equal("GpoPicker.Status.Reading", picker.Status.Current.Log.ToString());
        Assert.True(picker.Status.CancelCommand.CanExecute(null));
        Assert.True(picker.CancelCommand.CanExecute(null));
        Assert.Empty(picker.Items);

        held.Release();
        await opening.WaitAsync(Limit, Ct);

        // The list is the result: the line shows its count as the summary, and nothing else was said.
        Assert.Equal([(PageStatusKind.Working, working), (PageStatusKind.Idle, found + total.ToString(CultureInfo.InvariantCulture))], seen.Plain);
        Assert.Equal(total, picker.Items.Count);
        Assert.False(picker.IsLoading);
        Assert.False(picker.Status.IsWorking);
        Assert.Null(picker.Error);
        Assert.Null(picker.Status.Lasting);
    }

    [Theory]
    [InlineData("en-US", " group policy objects shown")]
    [InlineData("pl-PL", "Pokazano obiekty zasad grupy: ")]
    public async Task TheFilter_NarrowsTheSummary_AndClearingItGivesTheCountBack(string culture, string marker)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var picker = NewPicker(h, exclude: [h.Sample.WorkstationPasswords]);
        await picker.InitializeAsync();
        var all = picker.Items.Count;
        var summary = picker.Status.Current.Message;
        Assert.DoesNotContain(marker, summary, StringComparison.Ordinal);

        picker.FilterText = "PULPIT ZDALNY";

        Assert.Single(picker.Items);
        Assert.Equal(PageStatusKind.Idle, picker.Status.Current.Kind);
        Assert.Contains(marker, picker.Status.Current.Message, StringComparison.Ordinal);
        Assert.Contains(culture == "en-US" ? $"1 of {all}" : $"1 z {all}", picker.Status.Current.Message, StringComparison.Ordinal);

        picker.FilterText = string.Empty;

        Assert.Equal(all, picker.Items.Count);
        Assert.Equal(summary, picker.Status.Current.Message);
    }

    [Theory]
    [InlineData("en-US", "Stopping…", "Reading the group policy objects was stopped: the list is empty. Press “Retry” to read them again.", "Retry")]
    [InlineData("pl-PL", "Zatrzymywanie…", "Wczytywanie obiektów zasad grupy zatrzymano: lista jest pusta. Wybierz „Ponów”, aby wczytać je ponownie.", "Ponów")]
    public async Task Stop_ReturnsControl_TheLineSaysTheListIsEmpty_AndRetryReadsItAgain(string culture, string stopping, string stopped, string retryLabel)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var picker = NewPicker(h, exclude: []);
        var seen = new StatusRecorder(picker.Status);
        using var held = new HeldDirectoryCall(h.Session);
        var opening = Task.Run(picker.InitializeAsync, Ct);
        await held.Reached.WaitAsync(Limit, Ct);

        // Stop answers at once: "Stopping…" on the page, and only then the read is asked to stop.
        picker.Status.CancelCommand.Execute(null);

        Assert.True(picker.Status.IsCancelling);
        Assert.Equal(stopping, picker.Status.Current.Message);
        Assert.False(picker.Status.CancelCommand.CanExecute(null));
        held.Release();
        await opening.WaitAsync(Limit, Ct);

        // The read ended as the state of the dialog: nothing was listed, and the line says what to do - an attention that
        // cannot be closed, with Retry.
        Assert.False(picker.IsLoading);
        Assert.Empty(picker.Items);
        Assert.Equal([PageStatusKind.Working, PageStatusKind.Working, PageStatusKind.Attention], seen.Plain.Select(s => s.Kind).ToList());
        var state = picker.Status.Current;
        Assert.Equal(stopped, state.Message);
        Assert.Equal("GpoPicker.Status.Stopped", state.Log.ToString());
        Assert.False(state.CanClose);
        Assert.Same(state, picker.Status.Lasting);
        var retry = Assert.IsType<PageStatusAction>(state.Action);
        Assert.Equal(retryLabel, retry.Label);

        // Retry reads the GPOs again: the list is there and the state is over.
        await ((IAsyncRelayCommand)retry.Command).ExecuteAsync(retry.Parameter);

        Assert.NotEmpty(picker.Items);
        Assert.Null(picker.Status.Lasting);
        Assert.Equal(PageStatusKind.Idle, picker.Status.Current.Kind);
        Assert.False(picker.IsLoading);
    }

    [Theory]
    [InlineData("en-US", "Could not read the group policy objects of contoso.test: ", " Press Retry to try again. (ServerUnavailable)", "Retry")]
    [InlineData("pl-PL", "Nie udało się wczytać obiektów zasad grupy domeny contoso.test: ", " Wybierz Ponów, aby spróbować ponownie. (ServerUnavailable)", "Ponów")]
    public async Task AReadThatFailed_IsTheStateOfTheDialog_WithTheReasonTheCodeAndRetry(string culture, string lead, string tail, string retryLabel)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var picker = NewPicker(h, exclude: []);
        h.Session.BeforeOperation = _ => throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "serwer dc01.contoso.test nie odpowiada: DSID-0315");
        var seen = new StatusRecorder(picker.Status);

        await picker.InitializeAsync();

        Assert.Equal([PageStatusKind.Working, PageStatusKind.Error], seen.Plain.Select(s => s.Kind).ToList());
        Assert.NotNull(picker.Error);
        var failed = picker.Status.Current;
        Assert.StartsWith(lead, failed.Message, StringComparison.Ordinal);
        Assert.EndsWith(tail, failed.Message, StringComparison.Ordinal);
        Assert.Contains(picker.Error, failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("DSID", failed.Message, StringComparison.Ordinal);
        Assert.Equal("GpoPicker.Status.ReadFailed [ServerUnavailable]", failed.Log.ToString());
        Assert.False(failed.CanClose);
        Assert.Same(failed, picker.Status.Lasting);
        Assert.False(picker.Status.CloseCommand.CanExecute(null));
        Assert.False(picker.IsLoading);
        Assert.Empty(picker.Items);
        var retry = Assert.IsType<PageStatusAction>(failed.Action);
        Assert.Equal(retryLabel, retry.Label);
        Assert.False(picker.OkCommand.CanExecute(null));

        // Retry, with a domain controller that answers: the list is there, the error is gone.
        h.Session.BeforeOperation = null;
        await ((IAsyncRelayCommand)retry.Command).ExecuteAsync(retry.Parameter);

        Assert.NotEmpty(picker.Items);
        Assert.Null(picker.Error);
        Assert.Null(picker.Status.Lasting);
        Assert.Equal(PageStatusKind.Idle, picker.Status.Current.Kind);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("pl-PL")]
    public async Task WithoutASession_TheLineSaysSoFromTheStart_AndNothingIsRead(string culture)
    {
        var h = await GroupPolicyHarness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        await h.B2.Sessions.DisconnectAsync();
        var before = h.Session.Operations.Count;
        using var picker = new GpoPickerViewModel(h.Ui, h.Services.Reader, "Połącz istniejący GPO", multiSelect: false, []);

        // Said before anything is pressed: an error that cannot be closed.
        var state = picker.Status.Current;
        Assert.Equal(PageStatusKind.Error, state.Kind);
        Assert.Equal(h.B2.Localizer[L.Errors.NotConnected], state.Message);
        Assert.Equal("Errors.NotConnected", state.Log.ToString());
        Assert.False(state.CanClose);
        Assert.Null(state.Action);
        var seen = new StatusRecorder(picker.Status);

        await picker.InitializeAsync();

        Assert.Empty(seen.Seen);
        Assert.NotNull(picker.Error);
        Assert.Empty(picker.Items);
        Assert.False(picker.IsLoading);
        Assert.Equal(before, h.Session.Operations.Count);
    }

    [Fact]
    public async Task TheEndOfTheSession_TakesTheLine_AlsoDuringTheRead_AndIsSaidInTheLanguageOfTheMoment()
    {
        // While the GPOs are read: the read ends without a word, the line says for good that the session ended.
        var h = await GroupPolicyHarness.CreateAsync();
        using (var reading = NewPicker(h, exclude: []))
        {
            using var held = new HeldDirectoryCall(h.Session);
            var opening = Task.Run(reading.InitializeAsync, Ct);
            await held.Reached.WaitAsync(Limit, Ct);
            Assert.True(reading.Status.IsWorking);
            var seen = new StatusRecorder(reading.Status);

            await h.B2.Sessions.DisconnectAsync();

            Assert.True(reading.IsSessionEnded);
            Assert.False(reading.Status.IsWorking);
            Assert.False(reading.IsLoading);
            Assert.Equal(PageStatusKind.Error, reading.Status.Current.Kind);
            Assert.Equal(h.B2.Localizer[L.WindowSession.Ended], reading.Status.Current.Message);
            Assert.False(reading.Status.Current.CanClose);

            held.Release();
            await opening.WaitAsync(Limit, Ct);

            // Whatever the read brought afterwards: one message, and it stays.
            Assert.Equal([(PageStatusKind.Error, h.B2.Localizer[L.WindowSession.Ended])], seen.Plain);
            Assert.Equal("WindowSession.Ended", reading.Status.Current.Log.ToString());
        }

        // At rest, and after a language switch.
        h = await GroupPolicyHarness.CreateAsync();
        using var picker = NewPicker(h, exclude: []);
        await picker.InitializeAsync();
        Assert.Equal(PageStatusKind.Idle, picker.Status.Current.Kind);
        await h.B2.Sessions.DisconnectAsync();
        var english = picker.Status.Current.Message;
        Assert.Equal(h.B2.Localizer[L.WindowSession.Ended], english);
        h.B2.Localizer.SetCulture(Polish);
        Assert.Equal(PageStatusKind.Error, picker.Status.Current.Kind);
        Assert.Equal(h.B2.Localizer[L.WindowSession.Ended], picker.Status.Current.Message);
        Assert.NotEqual(english, picker.Status.Current.Message);
    }

    [Fact]
    public async Task TheSummaryFollowsTheLanguage_AndTheLogCopiesCarryKeysAndCodes_NeverADomainOrAServerText()
    {
        var logger = new ListLogger<PageStatusHolder>();
        var h = await GroupPolicyHarness.CreateAsync(null, logger);
        using var picker = NewPicker(h, exclude: []);
        h.Session.BeforeOperation = _ => throw new DirectoryException(DirectoryErrorKind.AccessDenied, "00002098: SecErr: DSID-03150F94 for CN=Policies,CN=System,DC=contoso,DC=test");
        await picker.InitializeAsync();
        h.Session.BeforeOperation = null;
        Assert.Contains("contoso.test", picker.Status.Current.Message, StringComparison.Ordinal);
        await ((IAsyncRelayCommand)picker.Status.Current.Action!.Command).ExecuteAsync(null);
        var english = picker.Status.Current.Message;

        h.B2.Localizer.SetCulture(Polish);

        Assert.NotEqual(english, picker.Status.Current.Message);
        Assert.StartsWith("Obiekty zasad grupy: ", picker.Status.Current.Message, StringComparison.Ordinal);
        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains(messages, m => m.Contains("GpoPicker.Status.Reading", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("GpoPicker.Status.ReadFailed", StringComparison.Ordinal) && m.Contains("AccessDenied", StringComparison.Ordinal));
        Assert.DoesNotContain("contoso", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DSID", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Policies", logger.AllText, StringComparison.Ordinal);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    private static GpoPickerViewModel NewPicker(GroupPolicyHarness h, IEnumerable<Guid> exclude) =>
        new(h.Ui, h.Services.Reader, "Połącz istniejący GPO z Komputery", multiSelect: false, exclude);
}
