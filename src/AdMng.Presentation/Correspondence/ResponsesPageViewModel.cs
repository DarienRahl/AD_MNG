using AdMng.Core.Correspondence.Outlook;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
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

    public override string ToString() => nameof(ResponseRowViewModel);
}

/// <summary>
/// "5 · Odpowiedzi" (design note 6.4 and 7.5): the campaigns found in "Elementy wysłane" since a date, the votes of one
/// campaign mapped to the mailboxes it asked about, the summary and the CSV export. Needs a connected session.
/// </summary>
public sealed partial class ResponsesPageViewModel : ObservableObject
{
    public ResponsesPageViewModel(DialogServices services, CorrespondenceServices correspondence, OutlookConnectionViewModel outlook)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Correspondence = correspondence ?? throw new ArgumentNullException(nameof(correspondence));
        Outlook = outlook ?? throw new ArgumentNullException(nameof(outlook));
        Since = services.Time.GetLocalNow().Date.AddDays(-90);
    }

    /// <summary>Campaigns sent since this day (default 90 days back).</summary>
    [ObservableProperty]
    public partial DateTime Since { get; set; }

    /// <summary>"28.10.2026 · Temat · 95 wiad.".</summary>
    [ObservableProperty]
    public partial IReadOnlyList<Option<OutlookCampaign>> Campaigns { get; private set; } = [];

    [ObservableProperty]
    public partial Option<OutlookCampaign>? SelectedCampaign { get; set; }

    public BulkObservableCollection<ResponseRowViewModel> Rows { get; } = [];

    /// <summary>"Odpowiedzi: 40 z 95 · Tak: 30 · Nie: 10".</summary>
    [ObservableProperty]
    public partial string SummaryText { get; private set; } = string.Empty;

    private DialogServices Services { get; }

    private CorrespondenceServices Correspondence { get; }

    private OutlookConnectionViewModel Outlook { get; }

    [RelayCommand]
    private Task LoadCampaignsAsync() => Task.CompletedTask;

    [RelayCommand]
    private Task LoadResponsesAsync() => Task.CompletedTask;

    /// <summary>CSV (formulas protected).</summary>
    [RelayCommand]
    private Task ExportAsync() => Task.CompletedTask;
}
