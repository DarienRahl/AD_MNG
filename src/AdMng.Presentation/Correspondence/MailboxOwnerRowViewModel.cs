using AdMng.Core.Correspondence.Mailboxes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.Correspondence;

/// <summary>How the operator treated a mailbox row (on top of the matcher's <see cref="OwnerMatchStatus"/>).</summary>
public enum MailboxOwnerRowState
{
    /// <summary>The matcher's result.</summary>
    Automatic,

    /// <summary>The operator picked a candidate, a user or typed an address.</summary>
    Manual,

    Excluded,
}

/// <summary>The colour of a row's status.</summary>
public enum MailboxRowStatusKind
{
    Ok,
    Warning,
    Error,
}

/// <summary>A candidate owner in the row's combo box: "Jan Kowalski – IT, jan.kowalski@…", delegate mark and level.</summary>
public sealed record CandidateOption(PersonCandidate Person, string Text, bool IsDelegate, OwnerMatchLevel Level)
{
    public override string ToString() => nameof(CandidateOption);
}

/// <summary>
/// One mailbox of the "Skrzynki" tab (design note 6.4): the mailbox, the owner's name as parsed, the match status (plus
/// Manual / Excluded), the candidates and the chosen owner. In memory only (rule 1).
/// </summary>
public sealed partial class MailboxOwnerRowViewModel : ObservableObject
{
    /// <param name="mailbox">The mailbox account.</param>
    /// <param name="location">Where the mailbox is (its container).</param>
    /// <param name="typeText">The localized mailbox type.</param>
    /// <param name="ownerName">The owner's name as parsed, or null.</param>
    /// <param name="match">The matcher's result.</param>
    /// <param name="candidates">The candidates as options.</param>
    public MailboxOwnerRowViewModel(MailboxEntry mailbox, string location, string typeText, OwnerName? ownerName, OwnerMatch match,
        IReadOnlyList<CandidateOption> candidates)
    {
        Mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
        Match = match ?? throw new ArgumentNullException(nameof(match));
        MailboxLocation = location ?? string.Empty;
        MailboxTypeText = typeText ?? string.Empty;
        OwnerName = ownerName;
        Candidates = candidates ?? [];
        Status = match.Status;
    }

    public MailboxEntry Mailbox { get; }

    /// <summary>The matcher's result.</summary>
    public OwnerMatch Match { get; }

    /// <summary>The owner's name as parsed, or null.</summary>
    public OwnerName? OwnerName { get; }

    /// <summary>"Wyślij": the owner of this row becomes a recipient.</summary>
    [ObservableProperty]
    public partial bool Include { get; set; } = true;

    public string MailboxName => Mailbox.Name;

    public string MailboxAddress => Mailbox.Address ?? string.Empty;

    public string MailboxLocation { get; }

    public string MailboxTypeText { get; }

    /// <summary>The owner's name as parsed ("Jan Kowalski"), or empty.</summary>
    public string OwnerNameText => OwnerName?.Text ?? string.Empty;

    [ObservableProperty]
    public partial OwnerMatchStatus Status { get; private set; }

    /// <summary>Manual / Excluded on top of <see cref="Status"/>.</summary>
    [ObservableProperty]
    public partial MailboxOwnerRowState RowState { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial MailboxRowStatusKind StatusKind { get; set; }

    public IReadOnlyList<CandidateOption> Candidates { get; }

    /// <summary>The chosen candidate (two-way; one that differs from the matcher's best makes the row Manual).</summary>
    [ObservableProperty]
    public partial CandidateOption? SelectedCandidate { get; set; }

    [ObservableProperty]
    public partial string OwnerDisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OwnerAddress { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OwnerDepartment { get; set; } = string.Empty;

    /// <summary>Why: level, delegate, disabled.</summary>
    [ObservableProperty]
    public partial string DetailText { get; set; } = string.Empty;

    public bool HasCandidates => Candidates.Count > 0;

    /// <summary>The row needs the operator's decision (ambiguous, probable, not found, disabled or no address).</summary>
    public bool NeedsReview => false;

    public override string ToString() => nameof(MailboxOwnerRowViewModel);
}
