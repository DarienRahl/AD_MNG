using System.ComponentModel;
using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Core.Csv;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Dialogs;

/// <summary>
/// The status line of the session log (track V, stage V2b; docs/dev/v-interface-renewal.md "V2b-2 as built"): at rest
/// how many entries the list shows - the text the footer had - and, as the state that lasts, that the session of the
/// window ended. The window starts no operation of its own; its export still answers with a toast. How entries are
/// listed, filtered, copied and exported is covered, unchanged, by <see cref="SessionLogAndHistoryTests"/>.
/// </summary>
public sealed class SessionLogStatusTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheLine_SaysHowManyEntriesTheListShows_AndFollowsTheFilterAndTheLanguage()
    {
        var harness = await B2Harness.CreateAsync();
        using var log = harness.Factory.CreateSessionLog();
        Assert.IsAssignableFrom<IHasPageStatus>(log);

        // At rest: the summary - nothing to close, nothing lasting, no operation.
        Assert.Equal(PageStatusKind.Idle, log.Status.Current.Kind);
        Assert.Equal("Entries: 0 of 0", log.Status.Current.Message);
        Assert.Equal(log.CountText, log.Status.Current.Message);
        Assert.Null(log.Status.Lasting);
        Assert.False(log.Status.IsWorking);
        Assert.False(log.Status.CloseCommand.CanExecute(null));
        var seen = Record(log.Status);

        // A change made while the window is open: the summary follows, and so it does the filter.
        await ChangeTitlesAsync(harness);
        Assert.Equal("Entries: 2 of 2", log.Status.Current.Message);
        log.FilterText = "lukasz";
        Assert.Equal("Entries: 1 of 2", log.Status.Current.Message);
        log.ShowFailuresOnly = true;
        Assert.Equal("Entries: 0 of 2", log.Status.Current.Message);
        log.ShowFailuresOnly = false;

        // The language of the moment.
        harness.Localizer.SetCulture(Polish);
        Assert.Equal("Wpisy: 1 z 2", log.Status.Current.Message);
        Assert.Equal(log.CountText, log.Status.Current.Message);

        // Only summaries were said.
        Assert.All(seen, said => Assert.Equal(PageStatusKind.Idle, said.Kind));
        Assert.NotEmpty(seen);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("pl-PL")]
    public async Task TheEndOfTheSession_IsTheStateOfTheWindowThatLasts_AndTheEntriesStay(string culture)
    {
        var harness = await B2Harness.CreateAsync(CultureInfo.GetCultureInfo(culture));
        using var log = harness.Factory.CreateSessionLog();
        await ChangeTitlesAsync(harness);
        log.SelectedEntry = log.Entries[0];
        Assert.True(log.OpenCommand.CanExecute(null));
        var seen = Record(log.Status);

        await harness.Sessions.DisconnectAsync();

        // An error that cannot be closed: the window belongs to a session that is over.
        Assert.True(log.IsSessionEnded);
        var ended = log.Status.Current;
        Assert.Equal(PageStatusKind.Error, ended.Kind);
        Assert.Equal(harness.Localizer[L.WindowSession.Ended], ended.Message);
        Assert.Equal("WindowSession.Ended", ended.Log.ToString());
        Assert.Null(ended.Action);
        Assert.False(ended.CanClose);
        Assert.False(log.Status.CloseCommand.CanExecute(null));
        Assert.Same(log.Status.Lasting, ended);
        Assert.Equal([(PageStatusKind.Error, ended.Message)], seen);
        Assert.False(log.OpenCommand.CanExecute(null));

        // The entries stay, can be filtered and copied; the summary is kept behind the state.
        Assert.Equal(2, log.Entries.Count);
        log.FilterText = "lukasz";
        Assert.Single(log.Entries);
        Assert.Equal(log.CountText, log.Status.Idle.Message);
        Assert.Same(ended, log.Status.Current);
        Assert.True(log.CopyEntryCommand.CanExecute(log.Entries[0]));

        // Said again in the language of the moment.
        var other = culture == "pl-PL" ? CultureInfo.GetCultureInfo("en-US") : Polish;
        harness.Localizer.SetCulture(other);
        Assert.Equal(PageStatusKind.Error, log.Status.Current.Kind);
        Assert.Equal(harness.Localizer[L.WindowSession.Ended], log.Status.Current.Message);
        Assert.NotEqual(ended.Message, log.Status.Current.Message);
        Assert.False(log.Status.Current.CanClose);
    }

    [Fact]
    public async Task AWindowOpenedWithoutASession_ListsTheLog_AndHasNoStateThatLasts()
    {
        var harness = await B2Harness.CreateAsync();
        await ChangeTitlesAsync(harness);
        await harness.Sessions.DisconnectAsync();
        using var log = harness.Factory.CreateSessionLog();

        // The log is the window's content, not the directory: nothing is wrong with a window that lists it offline.
        Assert.False(log.IsSessionEnded);
        Assert.Null(log.Status.Lasting);
        Assert.Equal(PageStatusKind.Idle, log.Status.Current.Kind);
        Assert.Equal("Entries: 2 of 2", log.Status.Current.Message);
    }

    [Fact]
    public async Task TheExport_StillAnswersWithAToast_AndTheLineKeepsItsSummary()
    {
        var harness = await B2Harness.CreateAsync();
        using var log = harness.Factory.CreateSessionLog();
        await ChangeTitlesAsync(harness);
        harness.Notifications.Clear();
        var seen = Record(log.Status);
        var path = Path.Combine(Path.GetTempPath(), $"admng-test-{Guid.NewGuid():N}.csv");
        try
        {
            harness.Files.NextSavePath = path;
            await log.ExportCommand.ExecuteAsync(null);
            Assert.Equal(3, (await File.ReadAllLinesAsync(path, Ct)).Length);
        }
        finally
        {
            File.Delete(path);
        }

        var (severity, _) = Assert.Single(harness.Notifications.Items);
        Assert.Equal(NotificationSeverity.Success, severity);
        Assert.Empty(seen);
        Assert.Equal("Entries: 2 of 2", log.Status.Current.Message);
    }

    [Fact]
    public async Task TheLogCopy_IsAKey_NeverAnObjectAValueOrAnOperator()
    {
        var harness = await B2Harness.CreateAsync();
        var logger = new ListLogger<PageStatusHolder>();
        var lists = new ObjectListServices(harness.Sessions, harness.Localizer, harness.Dispatcher, harness.Settings, harness.Errors, harness.Changes, harness.Time, logger);
        var services = new DialogServices(
            harness.Sessions, harness.Localizer, harness.Dispatcher, harness.Settings, harness.Errors, harness.Changes, harness.Time, harness.Dialogs, harness.Files,
            harness.Clipboard, harness.Notifications, harness.Coordinator, harness.Activities, lists, harness.Regional);
        using var log = new SessionLogViewModel(services, harness.AuditLog, new AuditLogExporter(new CsvCodec()));
        await ChangeTitlesAsync(harness);
        log.FilterText = "Analityk";
        Assert.Equal("Entries: 2 of 2", log.Status.Current.Message);

        // The summary is not logged at all.
        Assert.Empty(logger.Entries);

        await harness.Sessions.DisconnectAsync();
        var message = Assert.Single(logger.Entries).Message;
        Assert.Contains("WindowSession.Ended", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Kowalski", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Analityk", logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("contoso", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Entries", logger.AllText, StringComparison.Ordinal);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>Two entries: the title of Jan Kowalski and of Łukasz Wiśniewski becomes "Analityk".</summary>
    private static Task ChangeTitlesAsync(B2Harness harness) => harness.Coordinator.ApplyAsync(
        new ChangeRequest([harness.Company.JanKowalski, harness.Company.LukaszWisniewski], [new SetAttributeOperation(Attr.Title, "Analityk")], "test"), Ct);

    private static List<(PageStatusKind Kind, string Message)> Record(PageStatusHolder status)
    {
        var seen = new List<(PageStatusKind Kind, string Message)>();
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName == nameof(PageStatusHolder.Current))
            {
                lock (seen)
                {
                    seen.Add((status.Current.Kind, status.Current.Message));
                }
            }
        };
        status.PropertyChanged += handler;
        return seen;
    }
}
