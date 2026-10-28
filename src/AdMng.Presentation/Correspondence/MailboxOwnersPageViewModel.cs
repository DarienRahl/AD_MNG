using System.Collections.ObjectModel;
using AdMng.Core.Correspondence.Mailboxes;
using AdMng.Core.Directory;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Correspondence;

/// <summary>Where the mailbox owner query looks for mailboxes.</summary>
public enum MailboxScopeKind
{
    /// <summary>The objects selected when the window was opened.</summary>
    Selection,

    Domain,

    OrganizationalUnit,
}

/// <summary>Which rows of the "Skrzynki" tab are shown.</summary>
public enum MailboxRowFilter
{
    All,
    Ready,
    NeedsReview,
    NotFound,
    Excluded,
}

/// <summary>
/// "Skrzynki" (design note 6.4, U5c): choose mailbox accounts (scope, type, extra filter), parse the owner's name, find the
/// owner's account in the person base, review the matches and add the owners as recipients (one message per mailbox or
/// per owner). Reads use the window's session (rule 4); nothing is written (D10).
/// </summary>
public sealed partial class MailboxOwnersPageViewModel : ObservableObject
{
    /// <param name="services">The window's services.</param>
    /// <param name="correspondence">The Korespondencja services.</param>
    /// <param name="session">The window's session (<c>RequireSession</c> of the window: throws when it ended).</param>
    /// <param name="recipients">The recipients page the owners are added to.</param>
    /// <param name="message">The message page (grouping, the built-in template when the message is empty).</param>
    public MailboxOwnersPageViewModel(DialogServices services, CorrespondenceServices correspondence, Func<IDirectorySession> session,
        RecipientsPageViewModel recipients, MessagePageViewModel message)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Correspondence = correspondence ?? throw new ArgumentNullException(nameof(correspondence));
        Session = session ?? throw new ArgumentNullException(nameof(session));
        Recipients = recipients ?? throw new ArgumentNullException(nameof(recipients));
        Message = message ?? throw new ArgumentNullException(nameof(message));
        SelectionChoices = [.. Enum.GetValues<MailboxSelection>().Select(v => new Option<MailboxSelection>(v, v.ToString()))];
        NameSourceChoices = [.. Enum.GetValues<OwnerNameSource>().Select(v => new Option<OwnerNameSource>(v, v.ToString()))];
        FilterChoices = [.. Enum.GetValues<MailboxRowFilter>().Select(v => new Option<MailboxRowFilter>(v, v.ToString()))];
        Selection = SelectionChoices[0];
        NameSource = NameSourceChoices[0];
        Filter = FilterChoices[0];
    }

    /// <summary>"Skrzynki" rows were added as recipients (the window switches to the recipients tab).</summary>
    public event EventHandler? RecipientsAdded;

    [ObservableProperty]
    public partial MailboxScopeKind ScopeKind { get; set; } = MailboxScopeKind.Domain;

    /// <summary>The scope as shown ("contoso.test", "OU=…", "3 zaznaczone obiekty").</summary>
    [ObservableProperty]
    public partial string ScopeText { get; private set; } = string.Empty;

    public IReadOnlyList<Option<MailboxSelection>> SelectionChoices { get; }

    [ObservableProperty]
    public partial Option<MailboxSelection> Selection { get; set; }

    /// <summary>The schema has <c>msExchRecipientTypeDetails</c>; without it only AllAccounts is offered (with a hint).</summary>
    [ObservableProperty]
    public partial bool IsExchangeSchemaPresent { get; private set; } = true;

    /// <summary>An LDAP filter ANDed to the mailbox filter (validated).</summary>
    [ObservableProperty]
    public partial string ExtraFilter { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ExtraFilterError { get; private set; }

    public IReadOnlyList<Option<OwnerNameSource>> NameSourceChoices { get; }

    [ObservableProperty]
    public partial Option<OwnerNameSource> NameSource { get; set; }

    /// <summary>The attribute of <see cref="OwnerNameSource.Attribute"/>.</summary>
    [ObservableProperty]
    public partial string NameAttribute { get; set; } = string.Empty;

    /// <summary>Where the persons are (empty: the domain).</summary>
    [ObservableProperty]
    public partial string PersonBaseText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool EnabledOwnersOnly { get; set; } = true;

    [ObservableProperty]
    public partial bool GroupByOwner { get; set; } = true;

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    /// <summary>"Skrzynki: 120 · osoby: 4 210".</summary>
    [ObservableProperty]
    public partial string ProgressText { get; private set; } = string.Empty;

    /// <summary>Every mailbox row of the last run.</summary>
    public BulkObservableCollection<MailboxOwnerRowViewModel> Rows { get; } = [];

    /// <summary>The rows shown: <see cref="Rows"/> filtered by <see cref="Filter"/>.</summary>
    public BulkObservableCollection<MailboxOwnerRowViewModel> View { get; } = [];

    public IReadOnlyList<Option<MailboxRowFilter>> FilterChoices { get; }

    [ObservableProperty]
    public partial Option<MailboxRowFilter> Filter { get; set; }

    /// <summary>"Dopasowane: 90 · do sprawdzenia: 12 · nieznalezione: 8 · wykluczone: 2".</summary>
    [ObservableProperty]
    public partial string SummaryText { get; private set; } = string.Empty;

    /// <summary>The fields of mailbox rows (mailbox and owner definitions).</summary>
    public ObservableCollection<DirectoryFieldViewModel> MailboxFields { get; } = [];

    private DialogServices Services { get; }

    private CorrespondenceServices Correspondence { get; }

    private Func<IDirectorySession> Session { get; }

    private RecipientsPageViewModel Recipients { get; }

    private MessagePageViewModel Message { get; }

    /// <summary>The objects selected when the window was opened become the scope (<see cref="MailboxScopeKind.Selection"/>).</summary>
    public void UseSelection(IReadOnlyList<string> distinguishedNames)
    {
    }

    /// <summary>Takes the mailbox options of the settings.</summary>
    public void LoadFrom(CorrespondenceSettings settings)
    {
    }

    /// <summary>Writes the mailbox options into the settings.</summary>
    public void SaveTo(CorrespondenceSettings settings)
    {
    }

    /// <summary>Raises <see cref="RecipientsAdded"/>.</summary>
    private void OnRecipientsAdded() => RecipientsAdded?.Invoke(this, EventArgs.Empty);

    /// <summary>The container picker (an OU as the scope).</summary>
    [RelayCommand]
    private Task BrowseScopeAsync() => Task.CompletedTask;

    [RelayCommand]
    private Task BrowsePersonBaseAsync() => Task.CompletedTask;

    [RelayCommand]
    private void ClearPersonBase()
    {
    }

    /// <summary>Reads the mailboxes and the persons, builds the index, matches and streams the rows.</summary>
    [RelayCommand]
    private Task FindOwnersAsync() => Task.CompletedTask;

    [RelayCommand]
    private void Cancel()
    {
    }

    /// <summary>The object picker (users): the picked person becomes the owner (Manual).</summary>
    [RelayCommand]
    private Task PickOwnerAsync(MailboxOwnerRowViewModel? row) => Task.CompletedTask;

    /// <summary>A typed address becomes the owner's address (Manual).</summary>
    [RelayCommand]
    private Task TypeOwnerAddressAsync(MailboxOwnerRowViewModel? row) => Task.CompletedTask;

    [RelayCommand]
    private void Exclude(MailboxOwnerRowViewModel? row)
    {
    }

    [RelayCommand]
    private void Restore(MailboxOwnerRowViewModel? row)
    {
    }

    [RelayCommand]
    private void AddMailboxField()
    {
    }

    [RelayCommand]
    private void RemoveMailboxField(DirectoryFieldViewModel? field)
    {
    }

    [RelayCommand]
    private void ResetMailboxFields()
    {
    }

    /// <summary>
    /// Rows with an owner address and Include become recipient rows (replacing earlier "Skrzynki" rows); grouping follows
    /// <see cref="GroupByOwner"/>; the built-in template is loaded when the message is empty.
    /// </summary>
    [RelayCommand]
    private Task AddToRecipientsAsync() => Task.CompletedTask;
}
