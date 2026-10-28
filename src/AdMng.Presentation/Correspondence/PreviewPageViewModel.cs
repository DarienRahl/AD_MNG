using AdMng.Core.Correspondence.Composition;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Settings;
using AdMng.Presentation.UserMessages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Correspondence;

/// <summary>
/// "3 · Podgląd" (design note 6.4 and 7.3): the message as the chosen recipient sees it – MailerWPiA's card (Od/Do/Temat/
/// Załączniki, body, 700 / 380 px / full) – with the warnings (unknown and empty fields, compatibility, size), "Podgląd z
/// Outlooka", "Otwórz w przeglądarce" and "Zapisz jako .html". Previews stay in memory (D5).
/// </summary>
public sealed partial class PreviewPageViewModel : ObservableObject
{
    public PreviewPageViewModel(DialogServices services, CorrespondenceServices correspondence, RecipientsPageViewModel recipients,
        MessagePageViewModel message, OutlookConnectionViewModel outlook)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Correspondence = correspondence ?? throw new ArgumentNullException(nameof(correspondence));
        Recipients = recipients ?? throw new ArgumentNullException(nameof(recipients));
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Outlook = outlook ?? throw new ArgumentNullException(nameof(outlook));
        WidthChoices = [.. Enum.GetValues<CorrespondencePreviewWidth>().Select(v => new Option<CorrespondencePreviewWidth>(v, v.ToString()))];
        Width = WidthChoices[0];
    }

    /// <summary>The first 500 messages: "Name &lt;email&gt;".</summary>
    [ObservableProperty]
    public partial IReadOnlyList<Option<CorrespondenceUnit>> PreviewRecipients { get; private set; } = [];

    [ObservableProperty]
    public partial Option<CorrespondenceUnit>? SelectedRecipient { get; set; }

    public IReadOnlyList<Option<CorrespondencePreviewWidth>> WidthChoices { get; }

    [ObservableProperty]
    public partial Option<CorrespondencePreviewWidth> Width { get; set; }

    /// <summary>The card shown by the preview control (HTML and images from memory).</summary>
    [ObservableProperty]
    public partial HtmlPreviewContent Preview { get; private set; } = HtmlPreviewContent.Empty;

    /// <summary>"obrazków osadzonych: 3 • pola: zwrot, Miejscowość".</summary>
    [ObservableProperty]
    public partial string InfoText { get; private set; } = string.Empty;

    /// <summary>Unknown fields (with the available ones), empty fields over the send list, lint findings, size.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    public partial IReadOnlyList<string> Warnings { get; private set; } = [];

    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>The preview was built by Outlook ("Podgląd z Outlooka").</summary>
    [ObservableProperty]
    public partial bool IsFromOutlook { get; private set; }

    /// <summary>"Wygląda dobrze – …" when there is no warning.</summary>
    [ObservableProperty]
    public partial string OkText { get; private set; } = string.Empty;

    private DialogServices Services { get; }

    private CorrespondenceServices Correspondence { get; }

    private RecipientsPageViewModel Recipients { get; }

    private MessagePageViewModel Message { get; }

    private OutlookConnectionViewModel Outlook { get; }

    /// <summary>Takes the preview width of the settings.</summary>
    public void LoadFrom(CorrespondenceSettings settings)
    {
    }

    /// <summary>Writes the preview width into the settings.</summary>
    public void SaveTo(CorrespondenceSettings settings)
    {
    }

    /// <summary>Rebuilds the preview (also on tab entry and recipient or width change; debounced 200 ms).</summary>
    [RelayCommand]
    private Task RefreshAsync() => Task.CompletedTask;

    /// <summary>"Podgląd z Outlooka": the session renders the message (images read into memory).</summary>
    [RelayCommand]
    private Task PreviewViaOutlookAsync() => Task.CompletedTask;

    [RelayCommand]
    private Task OpenInBrowserAsync() => Task.CompletedTask;

    /// <summary>"Zapisz jako .html": <c>podglad_wiadomosci.html</c> and the image files next to it in a chosen folder.</summary>
    [RelayCommand]
    private Task SaveHtmlAsync() => Task.CompletedTask;
}
