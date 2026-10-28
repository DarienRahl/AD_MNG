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

/// <summary>
/// One row of the recipient list (design note 6.4): whether it is sent, address, name, source, status, the journal mark
/// ("wysłano wcześniej"), validity, exclusion, its fields by column, and – for AD rows – the DN, for mailbox rows the
/// mailbox keys. In memory only (rule 1).
/// </summary>
public sealed partial class CorrespondenceRecipientViewModel : ObservableObject
{
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

    /// <summary>The address is in the journal ("już wysłane").</summary>
    [ObservableProperty]
    public partial bool IsAlreadySent { get; set; }

    /// <summary>The cells of the row by column name.</summary>
    public IReadOnlyDictionary<string, string> Fields { get; }

    /// <summary>The object of an AD row, else null.</summary>
    public string? DistinguishedName { get; }

    /// <summary>The mailbox addresses (or DNs) of a mailbox row.</summary>
    public IReadOnlyList<string> MailboxKeys { get; }

    /// <summary>The cell of <paramref name="column"/> ("" when the row has none) – the grid's dynamic columns.</summary>
    public string this[string column] => Fields.TryGetValue(column, out var value) ? value : string.Empty;

    public override string ToString() => nameof(CorrespondenceRecipientViewModel);
}
