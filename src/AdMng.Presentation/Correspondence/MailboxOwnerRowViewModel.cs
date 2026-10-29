using System.Collections.ObjectModel;
using AdMng.Core.Correspondence.Import;
using AdMng.Core.Correspondence.Mailboxes;
using AdMng.Core.Model;
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
/// <remarks>
/// The matcher's best candidate is chosen automatically for <see cref="OwnerMatchStatus.Matched"/>,
/// <see cref="OwnerMatchStatus.MatchedByDelegate"/>, <see cref="OwnerMatchStatus.Probable"/> and
/// <see cref="OwnerMatchStatus.OwnerWithoutAddress"/> (no address: not sent until one is typed), and for
/// <see cref="OwnerMatchStatus.OwnerDisabled"/> only when disabled owners are allowed. Choosing another candidate (the combo
/// box), picking any user or typing an address makes the row <see cref="MailboxOwnerRowState.Manual"/>; choosing the
/// automatic candidate again returns it to <see cref="MailboxOwnerRowState.Automatic"/>. A cleared selection (a recycled row of
/// the virtualized grid) is ignored.
/// </remarks>
public sealed partial class MailboxOwnerRowViewModel : ObservableObject
{
    private readonly ObservableCollection<CandidateOption> _candidates;
    private bool _applying;
    private MailboxOwnerRowState _stateBeforeExclusion;
    private string? _typedAddress;

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
        _candidates = [.. candidates ?? []];
        Status = match.Status;
    }

    public MailboxEntry Mailbox { get; }

    /// <summary>The matcher's result.</summary>
    public OwnerMatch Match { get; }

    /// <summary>The owner's name as parsed, or null.</summary>
    public OwnerName? OwnerName { get; }

    /// <summary>"Wyślij": the owner of this row becomes a recipient.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
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
    [NotifyPropertyChangedFor(nameof(NeedsReview), nameof(IsReady))]
    public partial MailboxOwnerRowState RowState { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial MailboxRowStatusKind StatusKind { get; set; }

    /// <summary>The candidates (a user picked with "Wybierz właściciela…" is added at the top).</summary>
    public IReadOnlyList<CandidateOption> Candidates => _candidates;

    /// <summary>The chosen candidate (two-way; one that differs from the matcher's best makes the row Manual).</summary>
    [ObservableProperty]
    public partial CandidateOption? SelectedCandidate { get; set; }

    [ObservableProperty]
    public partial string OwnerDisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    public partial string OwnerAddress { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OwnerDepartment { get; set; } = string.Empty;

    /// <summary>Why: level, delegate, disabled.</summary>
    [ObservableProperty]
    public partial string DetailText { get; set; } = string.Empty;

    public bool HasCandidates => _candidates.Count > 0;

    /// <summary>The row needs the operator's decision (ambiguous, probable, not found, disabled or no address).</summary>
    public bool NeedsReview => RowState == MailboxOwnerRowState.Automatic
        && Status is not (OwnerMatchStatus.Matched or OwnerMatchStatus.MatchedByDelegate);

    /// <summary>The owner becomes a recipient: not excluded, included and with a valid address.</summary>
    public bool IsReady => RowState != MailboxOwnerRowState.Excluded && Include && EmailAddressText.IsValid(OwnerAddress);

    /// <summary>The person chosen as the owner (null: none, or only a typed address).</summary>
    public PersonCandidate? Owner => SelectedCandidate?.Person;

    /// <summary>The address was typed by the operator (it wins over the chosen person's address).</summary>
    public bool HasTypedAddress => _typedAddress is not null;

    /// <summary>The candidate the matcher chose automatically (see the remarks of the type), or null.</summary>
    internal CandidateOption? AutomaticCandidate { get; private set; }

    /// <summary>The mailbox account as read (with the attributes of the mailbox fields), for the fields of its recipient row.</summary>
    internal DirectoryObject? MailboxObject { get; init; }

    /// <summary>Chooses the matcher's best candidate without making the row Manual (none when <paramref name="choose"/> is false).</summary>
    internal void ApplyAutomatic(bool choose)
    {
        AutomaticCandidate = choose && Match.Best is { } best ? _candidates.FirstOrDefault(c => SamePerson(c.Person, best.Person)) : null;
        Apply(AutomaticCandidate);
    }

    /// <summary>
    /// "Wybierz właściciela…": the picked person becomes the owner (added to the candidates when missing). Picking the
    /// automatic candidate again returns the row to <see cref="MailboxOwnerRowState.Automatic"/>, as the combo box does.
    /// </summary>
    internal void ChooseManually(CandidateOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        var existing = _candidates.FirstOrDefault(c => SamePerson(c.Person, option.Person));
        if (existing is null)
        {
            _candidates.Insert(0, option);
            existing = option;
            OnPropertyChanged(nameof(HasCandidates));
        }

        var hadTypedAddress = _typedAddress is not null;
        _typedAddress = null;
        Apply(existing);
        if (hadTypedAddress)
        {
            OnPropertyChanged(nameof(HasTypedAddress));
        }

        SetState(ReferenceEquals(existing, AutomaticCandidate) ? MailboxOwnerRowState.Automatic : MailboxOwnerRowState.Manual);
    }

    /// <summary>"Wpisz adres właściciela…": the typed address is the owner's address.</summary>
    internal void TypeAddress(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        _typedAddress = address;
        OwnerAddress = address;
        SetState(MailboxOwnerRowState.Manual);
        OnPropertyChanged(nameof(HasTypedAddress));
    }

    internal void Exclude()
    {
        if (RowState != MailboxOwnerRowState.Excluded)
        {
            _stateBeforeExclusion = RowState;
            RowState = MailboxOwnerRowState.Excluded;
        }
    }

    internal void Restore()
    {
        if (RowState == MailboxOwnerRowState.Excluded)
        {
            RowState = _stateBeforeExclusion;
        }
    }

    public override string ToString() => nameof(MailboxOwnerRowViewModel);

    partial void OnSelectedCandidateChanged(CandidateOption? value)
    {
        if (_applying || value is null)
        {
            return;
        }

        _typedAddress = null;
        OnPropertyChanged(nameof(HasTypedAddress));
        ApplyOwner(value);
        SetState(ReferenceEquals(value, AutomaticCandidate) ? MailboxOwnerRowState.Automatic : MailboxOwnerRowState.Manual);
    }

    private static bool SamePerson(PersonCandidate a, PersonCandidate b) =>
        Core.Ldap.DistinguishedName.AreEqual(a.DistinguishedName, b.DistinguishedName);

    private void Apply(CandidateOption? option)
    {
        _applying = true;
        try
        {
            SelectedCandidate = option;
        }
        finally
        {
            _applying = false;
        }

        ApplyOwner(option);
        OnPropertyChanged(nameof(Owner));
    }

    private void ApplyOwner(CandidateOption? option)
    {
        OwnerDisplayName = option?.Person.DisplayName ?? string.Empty;
        OwnerDepartment = option?.Person.Department ?? string.Empty;
        OwnerAddress = _typedAddress ?? EmailAddressText.Normalize(option?.Person.Address);
        OnPropertyChanged(nameof(Owner));
    }

    /// <summary>Sets the operator's state; an excluded row keeps it for <see cref="Restore"/>.</summary>
    private void SetState(MailboxOwnerRowState state)
    {
        if (RowState == MailboxOwnerRowState.Excluded)
        {
            _stateBeforeExclusion = state;
        }
        else
        {
            RowState = state;
        }
    }
}
