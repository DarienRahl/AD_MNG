using AdMng.Core.Correspondence.Fields;
using AdMng.Core.Messaging.Images;
using AdMng.Presentation.Settings;
using AdMng.Presentation.UserMessages;

namespace AdMng.Presentation.Correspondence;

/// <summary>
/// The templates of Korespondencja (application singleton, design note 6.2): the built-ins (not stored) and the operator's
/// templates in the settings, with their images in the <see cref="MessageImageStore"/>; save, delete, export
/// (<c>{"format":"AdMng.CorrespondenceTemplates","version":1,...}</c> with the images in base64, at most
/// <see cref="MaxFileLength"/>) and import (new ids); the last draft (<see cref="CorrespondenceSettings.LastDraft"/>).
/// </summary>
public sealed class CorrespondenceTemplateLibrary
{
    private const string Track = "U5 T4 shell";

    public const string ExportFormat = "AdMng.CorrespondenceTemplates";

    public const int ExportVersion = 1;

    /// <summary>The largest export file (20 MiB).</summary>
    public const int MaxFileLength = 20 * 1024 * 1024;

    /// <summary>Id of the built-in "is the mailbox still needed?" template.</summary>
    public const string MailboxStillNeededId = "mailbox-still-needed";

    private readonly ISettingsStore _settings;

    public CorrespondenceTemplateLibrary(ISettingsStore settings, MessageImageStore? images = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Images = images;
    }

    /// <summary>A template or the last draft was saved, deleted or imported.</summary>
    public event EventHandler? Changed;

    /// <summary>The image store (null: templates are saved without images).</summary>
    public MessageImageStore? Images { get; }

    /// <summary>The built-in templates (new instances on each call; never stored).</summary>
    public static IReadOnlyList<CorrespondenceTemplate> BuiltIns => [MailboxStillNeeded()];

    /// <summary>The operator's templates (settings).</summary>
    public IReadOnlyList<CorrespondenceTemplate> Saved => _settings.Current.Correspondence.Templates;

    /// <summary>The built-ins, then the saved templates.</summary>
    public IReadOnlyList<CorrespondenceTemplate> All => [.. BuiltIns, .. Saved];

    public static bool IsBuiltIn(string? id) => string.Equals(id, MailboxStillNeededId, StringComparison.Ordinal);

    public CorrespondenceTemplate? Find(string? id) => throw new NotImplementedException(Track);

    /// <summary>Saves a template (a built-in or unknown id gets a new id) and the bytes of its images.</summary>
    public Task<CorrespondenceTemplate> SaveAsync(CorrespondenceTemplate template, IEnumerable<MessageImage>? images = null) =>
        throw new NotImplementedException(Track);

    public Task DeleteAsync(string id) => throw new NotImplementedException(Track);

    /// <summary>The images of a template by content id (missing or changed files are left out).</summary>
    public Task<IReadOnlyDictionary<string, MessageImage>> LoadImagesAsync(CorrespondenceTemplate template, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(Track);

    /// <summary>The export file of <paramref name="templates"/> (null: every saved template).</summary>
    /// <exception cref="IOException">The file would exceed <see cref="MaxFileLength"/>.</exception>
    public Task<string> ExportJsonAsync(IEnumerable<CorrespondenceTemplate>? templates = null) => throw new NotImplementedException(Track);

    /// <summary>Imports an export file with new ids (never overwrites).</summary>
    /// <exception cref="FormatException">Not a templates file.</exception>
    public Task<TemplateImportResult> ImportAsync(string json) => throw new NotImplementedException(Track);

    /// <summary>The last draft of the settings, or null.</summary>
    public CorrespondenceTemplate? LoadLastDraft() => _settings.Current.Correspondence.LastDraft;

    /// <summary>Saves the last draft (on close and before each send) and the bytes of its images.</summary>
    public Task SaveLastDraftAsync(CorrespondenceTemplate draft, IEnumerable<MessageImage>? images = null) =>
        throw new NotImplementedException(Track);

    public override string ToString() => nameof(CorrespondenceTemplateLibrary);

    /// <summary>Raises <see cref="Changed"/>.</summary>
    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>"Czy skrzynka pocztowa jest nadal potrzebna?" (design note 6.1).</summary>
    private static CorrespondenceTemplate MailboxStillNeeded() => new()
    {
        Id = MailboxStillNeededId,
        Name = "Czy skrzynka pocztowa jest nadal potrzebna?",
        Subject = "Czy skrzynka pocztowa jest nadal potrzebna?",
        BodyHtml =
            "<p>{{Zwrot osobowy}},</p>"
            + "<p>w ewidencji poczty jako osobę, do której przypisano skrzynkę, wskazano {{Pana/Panią}}:</p>"
            + "<div>{{lista}}</div>"
            + "<p>Prosimy o informację, czy skrzynka jest nadal potrzebna. W Outlooku wystarczy kliknąć przycisk nad treścią tej "
            + "wiadomości („Tak, nadal potrzebna” albo „Nie, można usunąć”); można też po prostu odpowiedzieć na tę wiadomość. "
            + "Jeżeli do {{Termin}} nie otrzymamy odpowiedzi, skrzynka zostanie zablokowana.</p>"
            + "<p>Z poważaniem</p>",
        CustomFields = [new CustomFieldDefinition("Termin", "")],
        ListItemTemplate = "{{Skrzynka}} ({{Adres skrzynki}}) – ostatnie logowanie: {{Ostatnie logowanie|brak danych}}",
        VotingEnabled = true,
        VotingOptions = CorrespondenceSettings.DefaultVotingOptions,
        GroupByAddress = true,
    };
}
