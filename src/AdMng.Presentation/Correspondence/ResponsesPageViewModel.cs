using AdMng.Core.Correspondence.Outlook;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Correspondence;

/// <summary>A voting response: the first option (yes), the second (no), another text, or none.</summary>
public enum ResponseKind
{
    Yes,
    No,
    Other,
    None,
}

/// <summary>One recipient of a campaign and their vote (in memory only).</summary>
/// <param name="recipient">The recipient's address.</param>
/// <param name="keys">The keys of the message (mailbox addresses).</param>
/// <param name="response">The vote text, or "brak odpowiedzi".</param>
/// <param name="respondedAtText">When the vote came, or empty.</param>
/// <param name="kind">How the vote is classified.</param>
public sealed class ResponseRowViewModel(string recipient, IReadOnlyList<string> keys, string response, string respondedAtText, ResponseKind kind)
{
    public string Recipient { get; } = recipient ?? string.Empty;

    /// <summary>The mailboxes the message asked about.</summary>
    public IReadOnlyList<string> Keys { get; } = keys ?? [];

    public string KeysText => string.Join(", ", Keys);

    public string Response { get; } = response ?? string.Empty;

    public string RespondedAtText { get; } = respondedAtText ?? string.Empty;

    public ResponseKind Kind { get; } = kind;

    /// <summary>When the message was sent (the CSV export), or empty.</summary>
    public string SentAtText { get; init; } = string.Empty;

    public override string ToString() => nameof(ResponseRowViewModel);
}

/// <summary>
/// "5 · Odpowiedzi" (design note 6.4 and 7.5): the campaigns found in "Elementy wysłane" since a date, the votes of one
/// campaign mapped to the mailboxes it asked about, the summary and the CSV export. Needs a connected session.
/// </summary>
/// <remarks>Campaign ids exist only in Outlook items (nothing local). A vote is "yes" when it is the first voting option,
/// "no" when it is the second (the options of the message page, else of the settings, else the built-in ones); the rows and
/// the export are in memory or an operator export (rule 1).</remarks>
public sealed partial class ResponsesPageViewModel : ObservableObject
{
    /// <summary>The file name offered by the export.</summary>
    public const string ExportFileName = "odpowiedzi.csv";

    private readonly SerialCancellation _reads = new();

    public ResponsesPageViewModel(DialogServices services, CorrespondenceServices correspondence, OutlookConnectionViewModel outlook)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Correspondence = correspondence ?? throw new ArgumentNullException(nameof(correspondence));
        Outlook = outlook ?? throw new ArgumentNullException(nameof(outlook));
        Since = services.Time.GetLocalNow().Date.AddDays(-90);
        VotingOptions = services.Settings.Current.Correspondence.VotingOptions;
        Rows.CollectionChanged += (_, _) => ExportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Campaigns sent since this day (default 90 days back).</summary>
    [ObservableProperty]
    public partial DateTime Since { get; set; }

    /// <summary>"28.10.2026 · Temat · 95 wiad.".</summary>
    [ObservableProperty]
    public partial IReadOnlyList<Option<OutlookCampaign>> Campaigns { get; private set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadResponsesCommand))]
    public partial Option<OutlookCampaign>? SelectedCampaign { get; set; }

    public BulkObservableCollection<ResponseRowViewModel> Rows { get; } = [];

    /// <summary>"Odpowiedzi: 40 z 95 · Tak: 30 · Nie: 10".</summary>
    [ObservableProperty]
    public partial string SummaryText { get; private set; } = string.Empty;

    /// <summary>Outlook is being read.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCampaignsCommand), nameof(LoadResponsesCommand))]
    public partial bool IsLoading { get; private set; }

    /// <summary>The voting buttons whose first option is "yes" and second "no" ("Tak;Nie"); the window sets the options of
    /// the message page when the tab is entered.</summary>
    [ObservableProperty]
    public partial string VotingOptions { get; set; }

    private DialogServices Services { get; }

    private CorrespondenceServices Correspondence { get; }

    private OutlookConnectionViewModel Outlook { get; }

    private ILocalizer Localizer => Services.Localizer;

    /// <summary>The kind of a vote: the first option is yes, the second no (ignoring case and spaces); empty is none.</summary>
    internal static ResponseKind KindOf(string? response, string? votingOptions)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return ResponseKind.None;
        }

        var answer = response.Trim();
        foreach (var options in new[] { votingOptions, CorrespondenceSettings.DefaultVotingOptions })
        {
            var parts = (options ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 0 && string.Equals(parts[0], answer, StringComparison.CurrentCultureIgnoreCase))
            {
                return ResponseKind.Yes;
            }

            if (parts.Length > 1 && string.Equals(parts[1], answer, StringComparison.CurrentCultureIgnoreCase))
            {
                return ResponseKind.No;
            }
        }

        return ResponseKind.Other;
    }

    [RelayCommand(CanExecute = nameof(CanLoadCampaigns))]
    private async Task LoadCampaignsAsync()
    {
        var token = _reads.Next();
        IsLoading = true;
        SummaryText = Localizer[L.CorrespondenceResponses.Searching];
        try
        {
            var session = await Outlook.EnsureConnectedAsync(token).ConfigureAwait(true);
            if (session is null)
            {
                SummaryText = Localizer.Format(L.CorrespondenceSend.OutlookUnavailable, Outlook.FailureText ?? Outlook.StateText);
                return;
            }

            var day = DateTime.SpecifyKind(Since.Date, DateTimeKind.Unspecified);
            var since = new DateTimeOffset(day, Services.Time.LocalTimeZone.GetUtcOffset(day));
            var campaigns = await session.FindCampaignsAsync(since, token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return;
            }

            Campaigns = [.. campaigns.Select(c => new Option<OutlookCampaign>(c, Localizer.Format(L.CorrespondenceResponses.Campaign,
                LocalDate(c.FirstSent), c.Subject, c.Messages)))];
            SelectedCampaign = Campaigns.FirstOrDefault();
            Rows.Clear();
            SummaryText = Campaigns.Count == 0
                ? Localizer.Format(L.CorrespondenceResponses.NoCampaigns, Since.Date.ToString("d", Localizer.Culture))
                : Localizer.Format(L.CorrespondenceResponses.CampaignsFound, Campaigns.Count);
        }
        catch (OutlookException ex)
        {
            Outlook.ReportFailure(ex.Failure);
            SummaryText = Localizer.Format(L.CorrespondenceResponses.ReadFailed, CorrespondenceText.FailureText(Localizer, ex.Failure));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer read replaced this one.
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    private bool CanLoadCampaigns() => !IsLoading && Correspondence.Outlook is not null;

    [RelayCommand(CanExecute = nameof(CanLoadResponses))]
    private async Task LoadResponsesAsync()
    {
        if (SelectedCampaign?.Value is not { } campaign)
        {
            return;
        }

        var token = _reads.Next();
        IsLoading = true;
        SummaryText = Localizer[L.CorrespondenceResponses.Reading];
        try
        {
            var session = await Outlook.EnsureConnectedAsync(token).ConfigureAwait(true);
            if (session is null)
            {
                SummaryText = Localizer.Format(L.CorrespondenceSend.OutlookUnavailable, Outlook.FailureText ?? Outlook.StateText);
                return;
            }

            var responses = await session.ReadResponsesAsync(campaign.Id, token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return;
            }

            var options = VotingOptions;
            var rows = responses.Select(r => new ResponseRowViewModel(
                r.Recipient,
                r.Keys,
                string.IsNullOrWhiteSpace(r.Response) ? Localizer[L.CorrespondenceResponses.NoResponse] : r.Response.Trim(),
                r.RespondedAt is { } at ? LocalText(at) : string.Empty,
                KindOf(r.Response, options))
            {
                SentAtText = LocalText(r.SentAt),
            }).ToList();
            Rows.ReplaceAll(rows);
            Summarize(rows);
        }
        catch (OutlookException ex)
        {
            Outlook.ReportFailure(ex.Failure);
            SummaryText = Localizer.Format(L.CorrespondenceResponses.ReadFailed, CorrespondenceText.FailureText(Localizer, ex.Failure));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer read replaced this one.
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    private bool CanLoadResponses() => !IsLoading && SelectedCampaign is not null && Correspondence.Outlook is not null;

    /// <summary>CSV (formulas protected).</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        var title = Localizer[L.CorrespondenceResponses.ExportTitle];
        var path = await Services.Files.SaveFileAsync(title, ExportFileName, [new FileFilter(Localizer[L.CorrespondenceResponses.CsvFilter], "*.csv")]).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        IReadOnlyList<string> headers =
        [
            Localizer[L.CorrespondenceApp.ResponsesColumnRecipient],
            Localizer[L.CorrespondenceApp.ResponsesColumnMailboxes],
            Localizer[L.CorrespondenceApp.ResponsesColumnResponse],
            Localizer[L.CorrespondenceResponses.ColumnKind],
            Localizer[L.CorrespondenceApp.ResponsesColumnRespondedAt],
            Localizer[L.CorrespondenceResponses.ColumnSentAt],
        ];
        var rows = Rows.Select(r => (IReadOnlyList<string>)
            [r.Recipient, r.KeysText, r.Response, Localizer[CorrespondenceText.ResponseKindKey(r.Kind)], r.RespondedAtText, r.SentAtText]).ToList();
        var delimiter = CsvFiles.ListSeparator(Services.Regional.Culture);
        try
        {
            await CsvFiles.WriteAsync(path, writer => Correspondence.Csv.Write(writer, headers, rows, delimiter, protectFormulas: true)).ConfigureAwait(true);
            Services.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.CorrespondenceResponses.Exported, path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Services.Dialogs.ShowErrorAsync(title, Localizer.Format(L.CorrespondenceSend.SaveFailed, Services.Errors.Translate(ex))).ConfigureAwait(true);
        }
    }

    private bool CanExport() => Rows.Count > 0;

    private void Summarize(IReadOnlyList<ResponseRowViewModel> rows)
    {
        var answered = rows.Count(r => r.Kind != ResponseKind.None);
        var yes = rows.Count(r => r.Kind == ResponseKind.Yes);
        var no = rows.Count(r => r.Kind == ResponseKind.No);
        var other = rows.Count(r => r.Kind == ResponseKind.Other);
        SummaryText = other > 0
            ? Localizer.Format(L.CorrespondenceResponses.SummaryOther, answered, rows.Count, yes, no, other)
            : Localizer.Format(L.CorrespondenceResponses.Summary, answered, rows.Count, yes, no);
    }

    private string LocalDate(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Services.Time.LocalTimeZone).ToString("d", Localizer.Culture);

    private string LocalText(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Services.Time.LocalTimeZone).ToString("g", Localizer.Culture);
}
