using System.Collections.ObjectModel;
using AdMng.Core.Correspondence.Mailboxes;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Schema;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Session;
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
/// per owner). Reads use the window's session (rule 4); nothing is written (D10). Mailboxes, persons and matches exist in
/// memory only (rule 1); the settings keep the options, the extra filter and the person base as a portable DN.
/// </summary>
public sealed partial class MailboxOwnersPageViewModel : ObservableObject
{
    private readonly DirectoryFieldEditor _fields;
    private IReadOnlyList<string> _selection = [];
    private string? _scopeOu;
    private string _personBase = string.Empty;
    private MailboxSelection _preferredSelection = MailboxSelection.Shared;
    private bool _refreshingChoices;

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
        SelectionChoices = SelectionOptions(true);
        NameSourceChoices = [.. Enum.GetValues<OwnerNameSource>().Select(v => new Option<OwnerNameSource>(v, Localizer[CorrespondenceText.NameSourceKey(v)]))];
        FilterChoices = [.. Enum.GetValues<MailboxRowFilter>().Select(v => new Option<MailboxRowFilter>(v, Localizer[CorrespondenceText.FilterKey(v)]))];
        Selection = SelectionChoices[0];
        NameSource = NameSourceChoices[0];
        Filter = FilterChoices[0];
        _fields = new DirectoryFieldEditor(MailboxFields, correspondence.Fields, services.Localizer, TrySchema,
            DirectoryFieldDefinition.MailboxDefaults, DirectoryFieldTarget.Mailbox, L.CorrespondenceMailboxes.NewFieldName);
        _fields.Load(null);
        UpdateScopeText();
        UpdatePersonBaseText();
        UpdateSummary();
    }

    /// <summary>"Skrzynki" rows were added as recipients (the window switches to the recipients tab).</summary>
    public event EventHandler? RecipientsAdded;

    [ObservableProperty]
    public partial MailboxScopeKind ScopeKind { get; set; } = MailboxScopeKind.Domain;

    /// <summary>The scope as shown ("contoso.test", "contoso.test/Firma/Skrzynki", "Zaznaczone obiekty: 3").</summary>
    [ObservableProperty]
    public partial string ScopeText { get; private set; } = string.Empty;

    /// <summary>Every <see cref="MailboxSelection"/>; only AllAccounts when the schema has no Exchange attributes.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<Option<MailboxSelection>> SelectionChoices { get; private set; }

    [ObservableProperty]
    public partial Option<MailboxSelection> Selection { get; set; }

    /// <summary>The schema has <c>msExchRecipientTypeDetails</c>; without it only AllAccounts is offered (with a hint).</summary>
    [ObservableProperty]
    public partial bool IsExchangeSchemaPresent { get; private set; } = true;

    /// <summary>An LDAP filter ANDed to the mailbox filter (validated).</summary>
    [ObservableProperty]
    public partial string ExtraFilter { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindOwnersCommand))]
    public partial string? ExtraFilterError { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<Option<OwnerNameSource>> NameSourceChoices { get; private set; }

    [ObservableProperty]
    public partial Option<OwnerNameSource> NameSource { get; set; }

    /// <summary>The attribute of <see cref="OwnerNameSource.Attribute"/>.</summary>
    [ObservableProperty]
    public partial string NameAttribute { get; set; } = string.Empty;

    /// <summary>Where the persons are (empty: the domain).</summary>
    [ObservableProperty]
    public partial string PersonBaseText { get; private set; } = string.Empty;

    /// <summary>Disabled persons stay candidates but are never chosen automatically (<see cref="OwnerMatchStatus.OwnerDisabled"/>).</summary>
    [ObservableProperty]
    public partial bool EnabledOwnersOnly { get; set; } = true;

    [ObservableProperty]
    public partial bool GroupByOwner { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindOwnersCommand), nameof(CancelCommand), nameof(AddToRecipientsCommand))]
    public partial bool IsRunning { get; private set; }

    /// <summary>"Skrzynki: 120 · osoby: 4 210".</summary>
    [ObservableProperty]
    public partial string ProgressText { get; private set; } = string.Empty;

    /// <summary>Every mailbox row of the last run.</summary>
    public BulkObservableCollection<MailboxOwnerRowViewModel> Rows { get; } = [];

    /// <summary>The rows shown: <see cref="Rows"/> filtered by <see cref="Filter"/>.</summary>
    public BulkObservableCollection<MailboxOwnerRowViewModel> View { get; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<Option<MailboxRowFilter>> FilterChoices { get; private set; }

    [ObservableProperty]
    public partial Option<MailboxRowFilter> Filter { get; set; }

    /// <summary>"Dopasowane: 90 · do sprawdzenia: 12 · nieznalezione: 8 · wykluczone: 2".</summary>
    [ObservableProperty]
    public partial string SummaryText { get; private set; } = string.Empty;

    /// <summary>The fields of mailbox rows (mailbox and owner definitions).</summary>
    public ObservableCollection<DirectoryFieldViewModel> MailboxFields { get; } = [];

    /// <summary>The last outcome of an action ("Dodano do odbiorców: …"), for the status bar.</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    /// <summary>Why the last query or action failed (also shown in <see cref="ProgressText"/>), or null.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    private DialogServices Services { get; }

    private CorrespondenceServices Correspondence { get; }

    private Func<IDirectorySession> Session { get; }

    private RecipientsPageViewModel Recipients { get; }

    private MessagePageViewModel Message { get; }

    private ILocalizer Localizer => Services.Localizer;

    /// <summary>The selection the query uses: AllAccounts without the Exchange attributes.</summary>
    private MailboxSelection EffectiveSelection => IsExchangeSchemaPresent ? Selection.Value : MailboxSelection.AllAccounts;

    /// <summary>The objects selected when the window was opened become the scope (<see cref="MailboxScopeKind.Selection"/>).</summary>
    public void UseSelection(IReadOnlyList<string> distinguishedNames)
    {
        ArgumentNullException.ThrowIfNull(distinguishedNames);
        _selection = [.. distinguishedNames.Where(dn => !string.IsNullOrWhiteSpace(dn))];
        ScopeKind = MailboxScopeKind.Selection;
        UpdateScopeText();
    }

    /// <summary>Takes the mailbox options of the settings (and whether the session's schema has the Exchange attributes).</summary>
    public void LoadFrom(CorrespondenceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var options = settings.Mailboxes ?? new MailboxOwnerSettings();
        _preferredSelection = Enum.IsDefined(options.Selection) ? options.Selection : MailboxSelection.Shared;
        if (TrySession() is { } session)
        {
            RefreshSchema(session.Schema);
        }

        SelectPreferred();
        ExtraFilter = options.ExtraFilter ?? string.Empty;
        NameSource = NameSourceChoices.FirstOrDefault(c => c.Value == options.NameSource) ?? NameSourceChoices[0];
        NameAttribute = options.NameAttribute ?? string.Empty;
        _personBase = string.Equals(options.PersonBase?.Trim(), DistinguishedName.DomainPlaceholder, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : options.PersonBase?.Trim() ?? string.Empty;
        EnabledOwnersOnly = options.EnabledOwnersOnly;
        GroupByOwner = options.GroupByOwner;
        _fields.Load(options.Fields);
        UpdateScopeText();
        UpdatePersonBaseText();
    }

    /// <summary>Writes the mailbox options into the settings (the selection only when the schema offers it).</summary>
    public void SaveTo(CorrespondenceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var options = settings.Mailboxes ??= new MailboxOwnerSettings();
        options.Selection = _preferredSelection;
        options.ExtraFilter = ExtraFilterError is null ? ExtraFilter?.Trim() ?? string.Empty : string.Empty;
        options.NameSource = NameSource.Value;
        options.NameAttribute = NameAttribute?.Trim() ?? string.Empty;
        options.PersonBase = _personBase;
        options.EnabledOwnersOnly = EnabledOwnersOnly;
        options.GroupByOwner = GroupByOwner;
        options.Fields = _fields.ToSettings();
    }

    /// <summary>The window's language changed: the choices, scope texts, row texts and field errors follow.</summary>
    public void RefreshTexts()
    {
        _refreshingChoices = true;
        try
        {
            var selection = Selection.Value;
            var source = NameSource.Value;
            var filter = Filter.Value;
            SelectionChoices = SelectionOptions(IsExchangeSchemaPresent);
            NameSourceChoices = [.. Enum.GetValues<OwnerNameSource>().Select(v => new Option<OwnerNameSource>(v, Localizer[CorrespondenceText.NameSourceKey(v)]))];
            FilterChoices = [.. Enum.GetValues<MailboxRowFilter>().Select(v => new Option<MailboxRowFilter>(v, Localizer[CorrespondenceText.FilterKey(v)]))];
            Selection = SelectionChoices.FirstOrDefault(c => c.Value == selection) ?? SelectionChoices[0];
            NameSource = NameSourceChoices.First(c => c.Value == source);
            Filter = FilterChoices.First(c => c.Value == filter);
        }
        finally
        {
            _refreshingChoices = false;
        }

        ValidateExtraFilter();
        UpdateScopeText();
        UpdatePersonBaseText();
        foreach (var row in Rows)
        {
            UpdateRow(row);
        }

        UpdateSummary();
        _fields.Validate();
    }

    /// <summary>Raises <see cref="RecipientsAdded"/>.</summary>
    private void OnRecipientsAdded() => RecipientsAdded?.Invoke(this, EventArgs.Empty);

    private IReadOnlyList<Option<MailboxSelection>> SelectionOptions(bool exchange) =>
    [
        .. Enum.GetValues<MailboxSelection>()
            .Where(v => exchange || v == MailboxSelection.AllAccounts)
            .Select(v => new Option<MailboxSelection>(v, Localizer[CorrespondenceText.SelectionKey(v)])),
    ];

    /// <summary>Offers the choices the schema supports: without <c>msExchRecipientTypeDetails</c> only AllAccounts.</summary>
    private void RefreshSchema(ISchemaCatalog? schema)
    {
        var present = MailboxTypes.HasExchangeAttributes(schema);
        if (present == IsExchangeSchemaPresent && SelectionChoices.Count == (present ? Enum.GetValues<MailboxSelection>().Length : 1))
        {
            return;
        }

        IsExchangeSchemaPresent = present;
        _refreshingChoices = true;
        try
        {
            SelectionChoices = SelectionOptions(present);
        }
        finally
        {
            _refreshingChoices = false;
        }

        SelectPreferred();
    }

    private void SelectPreferred()
    {
        _refreshingChoices = true;
        try
        {
            Selection = SelectionChoices.FirstOrDefault(c => c.Value == _preferredSelection)
                ?? SelectionChoices.FirstOrDefault(c => c.Value == MailboxSelection.AllAccounts)
                ?? SelectionChoices[0];
        }
        finally
        {
            _refreshingChoices = false;
        }
    }

    private IDirectorySession? TrySession()
    {
        try
        {
            return Session();
        }
        catch (Exception ex) when (ex is SessionEndedException or InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }
    }

    private ISchemaCatalog? TrySchema() => TrySession()?.Schema;

    private void ValidateExtraFilter()
    {
        var filter = ExtraFilter?.Trim();
        ExtraFilterError = string.IsNullOrEmpty(filter) || LdapFilter.Validate(filter) is not { } problem
            ? null
            : Localizer.Format(L.CorrespondenceMailboxes.ExtraFilterInvalid, problem);
    }

    private void UpdateScopeText()
    {
        ScopeText = ScopeKind switch
        {
            MailboxScopeKind.Selection => _selection.Count > 0
                ? Localizer.Format(L.CorrespondenceMailboxes.ScopeSelection, _selection.Count)
                : Localizer[L.CorrespondenceMailboxes.ScopeSelectionNone],
            MailboxScopeKind.OrganizationalUnit => _scopeOu is null ? Localizer[L.CorrespondenceMailboxes.ScopeOuNone] : Canonical(_scopeOu),
            _ => TrySession()?.Info.DomainDnsName ?? string.Empty,
        };
    }

    private void UpdatePersonBaseText()
    {
        var info = TrySession()?.Info;
        PersonBaseText = _personBase.Length == 0
            ? Localizer.Format(L.CorrespondenceMailboxes.PersonBaseDomain, info?.DomainDnsName ?? string.Empty)
            : info is null ? _personBase : Canonical(DistinguishedName.FromPortable(_personBase, info.DefaultNamingContext));
    }

    /// <summary>The DN of the person base in the session's domain.</summary>
    private string PersonBaseDn(IDirectorySession session) =>
        _personBase.Length == 0 ? session.Info.DefaultNamingContext : DistinguishedName.FromPortable(_personBase, session.Info.DefaultNamingContext);

    private static string Canonical(string dn) => DistinguishedName.TryParse(dn, out var parsed) ? parsed.ToCanonicalName() : dn;

    partial void OnScopeKindChanged(MailboxScopeKind value) => UpdateScopeText();

    partial void OnSelectionChanged(Option<MailboxSelection> value)
    {
        if (!_refreshingChoices && value is not null && IsExchangeSchemaPresent)
        {
            _preferredSelection = value.Value;
        }
    }

    partial void OnExtraFilterChanged(string value) => ValidateExtraFilter();

    partial void OnFilterChanged(Option<MailboxRowFilter> value)
    {
        if (!_refreshingChoices)
        {
            RefreshView();
        }
    }

    /// <summary>The container picker (an OU as the scope).</summary>
    [RelayCommand]
    private async Task BrowseScopeAsync()
    {
        if (TrySession() is not { } session)
        {
            Fail(Localizer[L.WindowSession.Ended]);
            return;
        }

        if (await Services.Dialogs.PickContainerAsync(session, _scopeOu).ConfigureAwait(true) is { Length: > 0 } dn)
        {
            _scopeOu = dn;
            ScopeKind = MailboxScopeKind.OrganizationalUnit;
            UpdateScopeText();
        }
    }

    [RelayCommand]
    private async Task BrowsePersonBaseAsync()
    {
        if (TrySession() is not { } session)
        {
            Fail(Localizer[L.WindowSession.Ended]);
            return;
        }

        var current = _personBase.Length == 0 ? null : PersonBaseDn(session);
        if (await Services.Dialogs.PickContainerAsync(session, current).ConfigureAwait(true) is { Length: > 0 } dn)
        {
            var portable = DistinguishedName.ToPortable(dn, session.Info.DefaultNamingContext);
            _personBase = string.Equals(portable, DistinguishedName.DomainPlaceholder, StringComparison.OrdinalIgnoreCase) ? string.Empty : portable;
            UpdatePersonBaseText();
        }
    }

    [RelayCommand]
    private void ClearPersonBase()
    {
        _personBase = string.Empty;
        UpdatePersonBaseText();
    }

    [RelayCommand]
    private void AddMailboxField() => _fields.Add();

    [RelayCommand]
    private void RemoveMailboxField(DirectoryFieldViewModel? field) => _fields.Remove(field);

    [RelayCommand]
    private void ResetMailboxFields() => _fields.Reset();
}
