using AdMng.Core.Correspondence.Fields;
using AdMng.Presentation.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.Correspondence;

/// <summary>What happened to a recipient row in this window (localized by <c>Correspondence.Status.{kind}</c>).</summary>
public enum RecipientStatusKind
{
    None,
    Sent,
    Draft,
    Test,
    Failed,
    Skipped,
}

/// <summary>Where a recipient row came from (the shown <see cref="CorrespondenceRecipientViewModel.Source"/> is localized).</summary>
public enum RecipientOrigin
{
    /// <summary>A file (dialog, Downloads, drag and drop).</summary>
    File,

    /// <summary>The clipboard.</summary>
    Clipboard,

    /// <summary>Typed into the prompt.</summary>
    Manual,

    /// <summary>An object of the directory (selection, picker, group members).</summary>
    Directory,

    /// <summary>The owner of mailboxes ("Skrzynki"): replaced as a whole by the next "add to recipients".</summary>
    Mailboxes,
}

/// <summary>
/// One row of the recipient list (design note 6.4): whether it is sent, address, name, source, status, the journal mark
/// ("wysłano wcześniej"), validity, exclusion, its fields by column, and – for AD rows – the DN, for mailbox rows the
/// mailbox keys. In memory only (rule 1).
/// </summary>
public sealed partial class CorrespondenceRecipientViewModel : ObservableObject
{
    private string? _searchKey;

    /// <param name="email">The address (normalized).</param>
    /// <param name="name">The name (may be empty).</param>
    /// <param name="source">Where the row came from (file name, "Schowek", "AD", "Skrzynki" …).</param>
    /// <param name="fields">The cells of the row by column name.</param>
    /// <param name="isValid">The address is valid.</param>
    /// <param name="distinguishedName">The object of an AD row, else null.</param>
    /// <param name="mailboxKeys">The mailbox addresses (or DNs) of a mailbox row, else empty.</param>
    public CorrespondenceRecipientViewModel(string email, string name, string source, IReadOnlyDictionary<string, string> fields,
        bool isValid = true, string? distinguishedName = null, IReadOnlyList<string>? mailboxKeys = null)
    {
        Email = email ?? throw new ArgumentNullException(nameof(email));
        Name = name ?? string.Empty;
        Source = source ?? string.Empty;
        Fields = fields ?? throw new ArgumentNullException(nameof(fields));
        IsValid = isValid;
        DistinguishedName = distinguishedName;
        MailboxKeys = mailboxKeys ?? [];
    }

    /// <summary>"Wyślij": the row is sent.</summary>
    [ObservableProperty]
    public partial bool Include { get; set; } = true;

    public string Email { get; }

    public string Name { get; }

    public string Source { get; }

    /// <summary>Where the row came from (<see cref="Source"/> is its localized text or the file name).</summary>
    public RecipientOrigin Origin { get; init; } = RecipientOrigin.File;

    /// <summary>The localized status ("wysłano", "błąd – …").</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial RecipientStatusKind StatusKind { get; set; }

    /// <summary>The journal entry: "28.10.2026 · Temat…", or null.</summary>
    [ObservableProperty]
    public partial string? PreviouslySentText { get; set; }

    public bool IsValid { get; }

    /// <summary>The address is on the "Nie wysyłać" list.</summary>
    [ObservableProperty]
    public partial bool IsExcluded { get; set; }

    /// <summary>
    /// The address is in the journal ("już wysłane") while "Pomijaj już wysłane" is on: the row is skipped (and dimmed).
    /// <see cref="PreviouslySentText"/> shows the entry either way.
    /// </summary>
    [ObservableProperty]
    public partial bool IsAlreadySent { get; set; }

    /// <summary>The cells of the row by column name.</summary>
    public IReadOnlyDictionary<string, string> Fields { get; }

    /// <summary>The object of an AD row, else null.</summary>
    public string? DistinguishedName { get; }

    /// <summary>The mailbox addresses (or DNs) of a mailbox row.</summary>
    public IReadOnlyList<string> MailboxKeys { get; }

    /// <summary>
    /// The cell of <paramref name="column"/> ("" when the row has none) – the grid's dynamic columns. A column named alike
    /// (<see cref="FieldKey"/>: case, diacritics, spaces, '-' and '_' ignored) matches too.
    /// </summary>
    public string this[string column]
    {
        get
        {
            if (column is null)
            {
                return string.Empty;
            }

            if (Fields.TryGetValue(column, out var value))
            {
                return value ?? string.Empty;
            }

            var key = FieldKey.Of(column);
            foreach (var (name, cell) in Fields)
            {
                if (string.Equals(FieldKey.Of(name), key, StringComparison.Ordinal))
                {
                    return cell ?? string.Empty;
                }
            }

            return string.Empty;
        }
    }

    /// <summary>The text the filter box searches (address, name, source, field values), folded once.</summary>
    internal string SearchKey => _searchKey ??= TextFolding.Fold(string.Join('\u001F', new[] { Email, Name, Source }.Concat(Fields.Values)));

    /// <summary>The address has a journal entry (kept to recompute <see cref="IsAlreadySent"/> when the option changes).</summary>
    internal bool IsInJournal { get; set; }

    /// <summary>Sets <see cref="Status"/> and <see cref="StatusKind"/> together.</summary>
    public void SetStatus(RecipientStatusKind kind, string text)
    {
        StatusKind = kind;
        Status = text ?? string.Empty;
    }

    public override string ToString() => nameof(CorrespondenceRecipientViewModel);
}
