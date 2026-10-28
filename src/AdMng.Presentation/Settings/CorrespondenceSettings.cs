using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Correspondence.Fields;
using AdMng.Core.Correspondence.Mailboxes;
using AdMng.Core.Ldap;
using AdMng.Core.Messaging.Images;

namespace AdMng.Presentation.Settings;

/// <summary>How Korespondencja sends: one message per recipient (unit) or BCC chunks.</summary>
public enum CorrespondenceSendMode
{
    Individual,
    Bcc,
}

/// <summary>The width of the preview card: Desktop 700 px, Phone 380 px, Full.</summary>
public enum CorrespondencePreviewWidth
{
    Desktop,
    Phone,
    Full,
}

/// <summary>
/// Korespondencja (U5, design note docs/dev/u5-outlook-correspondence.md 6.1): the options of the window, the operator's
/// templates, the last draft, the definitions of AD fields and the mailbox owner options. Operator content and names,
/// LDAP filters and portable DNs only (rule 1): never recipients, addresses or directory values.
/// </summary>
public sealed class CorrespondenceSettings
{
    public const int MaxTemplates = 100;

    public const int MaxDelaySeconds = 300;

    public const int MaxJournalRetentionDays = 3650;

    /// <summary>The voting buttons of the "is the mailbox still needed" query.</summary>
    public const string DefaultVotingOptions = "Tak, nadal potrzebna;Nie, można usunąć";

    /// <summary>The newest file of the Downloads folder matching this is "Wczytaj z Pobranych".</summary>
    public const string DefaultDownloadsPattern = "*kontakty*.csv";

    public const string DefaultDeferTime = "08:00";

    public string Subject { get; set; } = "";

    public string Salutation { get; set; } = BuiltInFields.DefaultSalutation;

    /// <summary>"W imieniu" (SentOnBehalfOfName), or empty.</summary>
    public string OnBehalfOf { get; set; } = "";

    /// <summary>The chosen Outlook account (SMTP), applied after connect.</summary>
    public string AccountAddress { get; set; } = "";

    public string SignatureName { get; set; } = "";

    /// <summary>Seconds between messages (0–<see cref="MaxDelaySeconds"/>).</summary>
    public double DelaySeconds { get; set; } = 3;

    public CorrespondenceSendMode SendMode { get; set; } = CorrespondenceSendMode.Individual;

    /// <summary>Recipients of one BCC message (1–<see cref="CorrespondenceLimits.MaxBccPerMessage"/>).</summary>
    public int BccChunkSize { get; set; } = CorrespondenceLimits.MaxBccPerMessage;

    public bool ReadReceipt { get; set; }

    /// <summary>Rows whose address is in the journal ("już wysłane") are skipped.</summary>
    public bool SkipAlreadySent { get; set; } = true;

    /// <summary>One message per address with all its rows (<c>{{lista}}</c>).</summary>
    public bool GroupByAddress { get; set; }

    public bool VotingEnabled { get; set; }

    /// <summary>Voting buttons, semicolon separated.</summary>
    public string VotingOptions { get; set; } = DefaultVotingOptions;

    /// <summary>The time of day of deferred delivery ("H:mm").</summary>
    public string DeferTime { get; set; } = DefaultDeferTime;

    public CorrespondencePreviewWidth PreviewWidth { get; set; } = CorrespondencePreviewWidth.Desktop;

    public string DownloadsPattern { get; set; } = DefaultDownloadsPattern;

    /// <summary>Above this number of messages sending needs the number typed as confirmation.</summary>
    public int ConfirmationThreshold { get; set; } = 20;

    /// <summary>Days the journal keeps an entry (1–<see cref="MaxJournalRetentionDays"/>).</summary>
    public int JournalRetentionDays { get; set; } = 365;

    /// <summary>Autosaved on close and before each send.</summary>
    public CorrespondenceTemplate? LastDraft { get; set; }

    /// <summary>The operator's templates (built-ins are not stored).</summary>
    public List<CorrespondenceTemplate> Templates { get; set; } = [];

    /// <summary>The AD fields of recipients read from the directory; empty → <see cref="DirectoryFieldDefinition.Defaults"/>.</summary>
    public List<DirectoryFieldDefinition> DirectoryFields { get; set; } = [];

    public MailboxOwnerSettings Mailboxes { get; set; } = new();

    /// <summary>Repairs values of a hand-edited or older settings file.</summary>
    public void Normalize()
    {
        Subject = Subject ?? "";
        Salutation = string.IsNullOrWhiteSpace(Salutation) ? BuiltInFields.DefaultSalutation : Salutation.Trim();
        OnBehalfOf = OnBehalfOf?.Trim() ?? "";
        AccountAddress = AccountAddress?.Trim() ?? "";
        SignatureName = SignatureName?.Trim() ?? "";
        DelaySeconds = double.IsFinite(DelaySeconds) ? Math.Clamp(DelaySeconds, 0, MaxDelaySeconds) : 3;
        SendMode = Enum.IsDefined(SendMode) ? SendMode : CorrespondenceSendMode.Individual;
        BccChunkSize = Math.Clamp(BccChunkSize, 1, CorrespondenceLimits.MaxBccPerMessage);
        VotingOptions = string.IsNullOrWhiteSpace(VotingOptions) ? DefaultVotingOptions : VotingOptions.Trim();
        DeferTime = string.IsNullOrWhiteSpace(DeferTime) ? DefaultDeferTime : DeferTime.Trim();
        PreviewWidth = Enum.IsDefined(PreviewWidth) ? PreviewWidth : CorrespondencePreviewWidth.Desktop;
        DownloadsPattern = string.IsNullOrWhiteSpace(DownloadsPattern) ? DefaultDownloadsPattern : DownloadsPattern.Trim();
        ConfirmationThreshold = Math.Clamp(ConfirmationThreshold, 1, 100_000);
        JournalRetentionDays = Math.Clamp(JournalRetentionDays, 1, MaxJournalRetentionDays);
        if (LastDraft is not null)
        {
            NormalizeTemplate(LastDraft);
        }

        Templates = [.. (Templates ?? [])
            .Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Id) && !string.IsNullOrWhiteSpace(t.Name)
                && !Correspondence.CorrespondenceTemplateLibrary.IsBuiltIn(t.Id))
            .Select(NormalizeTemplate)
            .DistinctBy(t => t.Id, StringComparer.Ordinal)
            .Take(MaxTemplates)];
        DirectoryFields = DirectoryFieldDefinition.Normalize(DirectoryFields);
        Mailboxes ??= new();
        Mailboxes.Normalize();
    }

    private static CorrespondenceTemplate NormalizeTemplate(CorrespondenceTemplate template)
    {
        template.Name = template.Name?.Trim() ?? "";
        template.Subject ??= "";
        template.BodyHtml ??= "";
        template.ListItemTemplate ??= "";
        template.Images = [.. (template.Images ?? [])
            .Where(i => i is not null && i.IsValid)
            .DistinctBy(i => i.ContentId, StringComparer.OrdinalIgnoreCase)
            .Take(CorrespondenceLimits.MaxImagesPerMessage)];
        template.CustomFields = [.. (template.CustomFields ?? [])
            .Where(f => f is not null && !string.IsNullOrWhiteSpace(f.Name))
            .Select(f => new CustomFieldDefinition(f.Name.Trim(), f.Value ?? ""))
            .DistinctBy(f => f.Name, StringComparer.OrdinalIgnoreCase)];
        template.Salutation = string.IsNullOrWhiteSpace(template.Salutation) ? null : template.Salutation.Trim();
        template.VotingOptions = string.IsNullOrWhiteSpace(template.VotingOptions) ? null : template.VotingOptions.Trim();
        return template;
    }
}

/// <summary>
/// A saved message of Korespondencja (or the last draft): subject and body with <c>{{fields}}</c>, the images (bytes in the
/// <c>MessageImageStore</c>), custom fields, the list item template and the options a template may carry (null = keep
/// the window's).
/// </summary>
public sealed class CorrespondenceTemplate
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public string Subject { get; set; } = "";

    /// <summary>The sanitized subset with <c>cid:</c> images.</summary>
    public string BodyHtml { get; set; } = "";

    /// <summary>Metadata of the body images; the bytes are in <c>MessageImageStore</c>.</summary>
    public List<MessageTemplateImage> Images { get; set; } = [];

    public List<CustomFieldDefinition> CustomFields { get; set; } = [];

    public string ListItemTemplate { get; set; } = "";

    public string? Salutation { get; set; }

    public bool? VotingEnabled { get; set; }

    public string? VotingOptions { get; set; }

    public bool? GroupByAddress { get; set; }

    public override string ToString() => $"{nameof(CorrespondenceTemplate)} {Name}";
}

/// <summary>Whose value a <see cref="DirectoryFieldDefinition"/> is.</summary>
public enum DirectoryFieldTarget
{
    /// <summary>The recipient (AD rows) or the owner (mailbox rows).</summary>
    Person,

    /// <summary>The mailbox account (mailbox rows).</summary>
    Mailbox,
}

/// <summary>
/// A <c>{{field}}</c> of rows read from the directory: its value is a U1 template expression (decision D2) evaluated with
/// <c>UserMessageFields</c> for the person or the mailbox. Expressions are checked with <c>UserMessageFields.Engine.Parse</c>
/// and <c>UserMessageRules.IsForbiddenAttribute</c> (no secrets).
/// </summary>
public sealed record DirectoryFieldDefinition(string Name, string Expression, DirectoryFieldTarget Target = DirectoryFieldTarget.Person)
{
    /// <summary>The fields of a person (the recipient of AD rows, the owner of mailbox rows).</summary>
    public static IReadOnlyList<DirectoryFieldDefinition> Defaults { get; } =
    [
        new("Imię", "{givenName}"),
        new("Nazwisko", "{sn}"),
        new("Nazwa wyświetlana", "{displayName}"),
        new("Stanowisko", "{title}"),
        new("Dział", "{department}"),
        new("Login", "{sAMAccountName}"),
        new("Zwrot osobowy", "{$salutation}"),
        new("Tytuł", "{$title}"),
        new("Pan/Pani", "{$honorific}"),
        new("Pana/Panią", "{$honorific:biernik}"),
        new("Panu/Pani", "{$honorific:celownik}"),
        new("Szanowny/a", "{$byGender:Szanowny/Szanowna}"),
    ];

    /// <summary>The fields of mailbox rows: the mailbox (target Mailbox), then the owner (target Person).</summary>
    public static IReadOnlyList<DirectoryFieldDefinition> MailboxDefaults { get; } =
    [
        new("Skrzynka", "{displayName}", DirectoryFieldTarget.Mailbox),
        new("Adres skrzynki", "{mail}", DirectoryFieldTarget.Mailbox),
        new("Opis skrzynki", "{description}", DirectoryFieldTarget.Mailbox),
        new("Jednostka", "{$parentName}", DirectoryFieldTarget.Mailbox),
        new("Ostatnie logowanie", "{$lastLogon:d MMMM yyyy}", DirectoryFieldTarget.Mailbox),
        new("Utworzona", "{$created:d MMMM yyyy}", DirectoryFieldTarget.Mailbox),
        new("Login skrzynki", "{sAMAccountName}", DirectoryFieldTarget.Mailbox),
        new("Imię", "{givenName}"),
        new("Nazwisko", "{sn}"),
        new("Nazwa wyświetlana", "{displayName}"),
        new("Stanowisko", "{title}"),
        new("Dział", "{department}"),
        new("Zwrot osobowy", "{$salutation}"),
        new("Pan/Pani", "{$honorific}"),
        new("Pana/Panią", "{$honorific:biernik}"),
        new("Panu/Pani", "{$honorific:celownik}"),
        new("Szanowny/a", "{$byGender:Szanowny/Szanowna}"),
    ];

    /// <summary>Definitions of a settings file without blanks, unknown targets and repeated names (of the same target).</summary>
    internal static List<DirectoryFieldDefinition> Normalize(IEnumerable<DirectoryFieldDefinition>? definitions) =>
    [
        .. (definitions ?? [])
            .Where(d => d is not null && !string.IsNullOrWhiteSpace(d.Name) && !string.IsNullOrWhiteSpace(d.Expression) && Enum.IsDefined(d.Target))
            .Select(d => d with { Name = d.Name.Trim(), Expression = d.Expression.Trim() })
            .DistinctBy(d => (d.Name.ToUpperInvariant(), d.Target)),
    ];
}

/// <summary>
/// The options of the mailbox owner query ("Skrzynki"): which accounts, an extra LDAP filter, where the owner's name is,
/// where the persons are (portable DN) and how rows become messages. Never mailboxes, owners or matches.
/// </summary>
public sealed class MailboxOwnerSettings
{
    public MailboxSelection Selection { get; set; } = MailboxSelection.Shared;

    /// <summary>A validated LDAP filter ANDed to the mailbox filter (empty = none).</summary>
    public string ExtraFilter { get; set; } = "";

    public OwnerNameSource NameSource { get; set; } = OwnerNameSource.GivenNameAndSurname;

    /// <summary>The attribute of <see cref="OwnerNameSource.Attribute"/>.</summary>
    public string NameAttribute { get; set; } = "";

    /// <summary>Where the persons are: a portable DN (<c>{domain}</c>); empty = the domain.</summary>
    public string PersonBase { get; set; } = "";

    /// <summary>Disabled persons stay candidates but never auto-match (<see cref="OwnerMatchStatus.OwnerDisabled"/>).</summary>
    public bool EnabledOwnersOnly { get; set; } = true;

    /// <summary>One message per owner listing all their mailboxes.</summary>
    public bool GroupByOwner { get; set; } = true;

    /// <summary>The fields of mailbox rows; empty → <see cref="DirectoryFieldDefinition.MailboxDefaults"/>.</summary>
    public List<DirectoryFieldDefinition> Fields { get; set; } = [];

    /// <summary>Repairs values of a hand-edited or older settings file.</summary>
    public void Normalize()
    {
        Selection = Enum.IsDefined(Selection) ? Selection : MailboxSelection.Shared;
        ExtraFilter = ExtraFilter?.Trim() ?? "";
        if (ExtraFilter.Length > 0 && LdapFilter.Validate(ExtraFilter) is not null)
        {
            ExtraFilter = "";
        }

        NameSource = Enum.IsDefined(NameSource) ? NameSource : OwnerNameSource.GivenNameAndSurname;
        NameAttribute = NameAttribute?.Trim() ?? "";
        PersonBase = PersonBase?.Trim() ?? "";
        Fields = DirectoryFieldDefinition.Normalize(Fields);
    }
}
