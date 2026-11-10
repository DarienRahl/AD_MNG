using System.Collections.ObjectModel;
using System.ComponentModel;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Core.Security;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.PropertySheets;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.BulkCreate;

/// <summary>
/// Step 3 <em>Nazwy i opcje</em> of the bulk creation wizard (docs/dev/phase3-bulk-create.md 2.6, owner B3): the
/// <em>Numeracja</em> panel bound to the one counter, the naming rules with live samples, pattern errors and quick fixes,
/// <em>Wstaw pole</em>, the UPN suffix and mail domain, the account options (password mode, length, the shared password
/// boxes, must-change, enabled, expiry), group scope and type, the joiner of computers, the option groups, the advanced
/// options and the naming profiles. Outputs: <see cref="ToRules"/>, <see cref="ToOptions"/>, <see cref="ReadSharedPassword"/>
/// (UI thread only, reads and clears the boxes), <see cref="NeedsSharedPassword"/>, <see cref="Error"/>.
/// </summary>
/// <remarks>
/// The rules are compiled on the UI thread at every change (cheap) – errors and quick fixes appear at once – while the live
/// samples, which render every row to find the longest value, are computed on the thread pool (the newest change wins).
/// The first free number comes from <see cref="IBulkCreatePlanner.PreviewCounterAsync"/> on entering the step and after a
/// counter change. Nothing of the table, the samples or the shared password is stored: a saved profile holds patterns,
/// options, header names and portable DNs only (rule 1, rule 2).
/// </remarks>
public sealed partial class BulkRulesViewModel : ObservableObject, IDisposable
{
    private readonly BulkCreateProfileStore _store;
    private readonly ISchemaCatalog? _openedSchema;
    private ObjectKind _kind;
    private string? _joinerSid;
    private string? _joinerName;
    private bool _disposed;

    /// <param name="services">The dialog services of the window.</param>
    /// <param name="columns">Step 2 (mapped columns: precedence notes, <em>Wstaw pole</em>, samples).</param>
    /// <param name="planner">The planner (<see cref="IBulkCreatePlanner.PreviewCounterAsync"/> for the first free number).</param>
    /// <param name="session">The window's session, null once it ended.</param>
    /// <param name="containerDn">The target container (the container's default profile).</param>
    /// <param name="input">The wizard's current input (<see cref="BulkCreateViewModel.BuildInput"/>) for the counter preview.</param>
    /// <param name="lookups">The wizard's lookup cache (the preview is cached there).</param>
    /// <param name="kind">The kind of the created objects.</param>
    public BulkRulesViewModel(
        DialogServices services,
        BulkColumnsViewModel columns,
        IBulkCreatePlanner planner,
        Func<IDirectorySession?> session,
        Func<string?> containerDn,
        Func<BulkCreateInput?> input,
        Func<BulkCreateLookups> lookups,
        ObjectKind kind)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Columns = columns ?? throw new ArgumentNullException(nameof(columns));
        Planner = planner ?? throw new ArgumentNullException(nameof(planner));
        Session = session ?? throw new ArgumentNullException(nameof(session));
        ContainerDn = containerDn ?? throw new ArgumentNullException(nameof(containerDn));
        Input = input ?? throw new ArgumentNullException(nameof(input));
        Lookups = lookups ?? throw new ArgumentNullException(nameof(lookups));
        Kind = kind;
        var localizer = services.Localizer;
        PasswordModes = [.. Enum.GetValues<InitialPasswordMode>().Select(m => new Option<InitialPasswordMode>(m, localizer[BulkCreateTextKeys.InitialModeKey(m)]))];
        CounterModes = [.. Enum.GetValues<CounterMode>().Select(m => new Option<CounterMode>(m, localizer[BulkCreateTextKeys.CounterModeKey(m)]))];
        CollisionStrategies = [.. Enum.GetValues<CollisionStrategy>().Select(s => new Option<CollisionStrategy>(s, localizer[BulkCreateTextKeys.CollisionKey(s)]))];
        PhoneFormats = [.. Enum.GetValues<PhoneFormat>().Select(f => new Option<PhoneFormat>(f, localizer[BulkCreateTextKeys.PhonesKey(f)]))];
        GroupScopes = [.. new[] { GroupScope.DomainLocal, GroupScope.Global, GroupScope.Universal }
            .Select(s => new Option<GroupScope>(s, localizer[DisplayNames.GroupScopeKey(s)]))];
        _store = new BulkCreateProfileStore(services.Settings, localizer);
        var current = session();
        _openedSchema = current?.Schema;
        if (current is not null)
        {
            UpnSuffixes = [current.Info.DomainDnsName];
        }

        AttributeOptions = BulkColumnsViewModel.OptionsFor(_openedSchema, localizer, kind);
        RefreshProfileList();
        ApplyProfile(_store.BuiltIns(kind)[0], warn: false);
        Columns.Changed += OnColumnsChanged;
        Columns.PropertyChanged += OnColumnsPropertyChanged;
        localizer.CultureChanged += OnCultureChanged;
    }

    /// <summary>A rule or an option changed (the wizard re-evaluates <em>Dalej</em> and the check step).</summary>
    public event EventHandler? Changed;

    internal DialogServices Services { get; }

    internal BulkColumnsViewModel Columns { get; }

    internal IBulkCreatePlanner Planner { get; }

    internal Func<IDirectorySession?> Session { get; }

    internal Func<string?> ContainerDn { get; }

    internal Func<BulkCreateInput?> Input { get; }

    internal Func<BulkCreateLookups> Lookups { get; }

    /// <summary>The kind of the created objects (<see cref="OnKindChanged"/>).</summary>
    public ObjectKind Kind
    {
        get => _kind;
        private set
        {
            if (SetProperty(ref _kind, value))
            {
                OnPropertyChanged(nameof(IsUserKind));
                OnPropertyChanged(nameof(IsGroupKind));
                OnPropertyChanged(nameof(IsComputerKind));
                OnPropertyChanged(nameof(HasAccountOptions));
                OnPropertyChanged(nameof(CanChooseCollisions));
                OnPropertyChanged(nameof(HasUpnRule));
            }
        }
    }

    public bool IsUserKind => Kind is ObjectKind.User or ObjectKind.InetOrgPerson;

    public bool IsGroupKind => Kind == ObjectKind.Group;

    public bool IsComputerKind => Kind == ObjectKind.Computer;

    /// <summary>The <em>Konto</em> section has something to show (users, groups, computers).</summary>
    public bool HasAccountOptions => Kind != ObjectKind.Contact;

    /// <summary>The attributes a rule can set (as the column step offers them, <c>cn</c> included).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AttributeOption> AttributeOptions { get; private set; } = [];

    // ---- Nazwy ------------------------------------------------------------------------------------------------

    /// <summary>The naming rules (attribute, pattern, sample, error).</summary>
    public ObservableCollection<AttributeRuleViewModel> Rules { get; } = [];

    /// <summary>The rule whose pattern box had the focus last (<em>Wstaw pole</em> inserts there).</summary>
    public AttributeRuleViewModel? ActiveRule { get; set; }

    /// <summary><em>Prefiks</em> of the <em>Numeracja</em> panel.</summary>
    [ObservableProperty]
    public partial string CounterPrefix { get; set; } = string.Empty;

    /// <summary><em>Pierwszy numer</em>.</summary>
    [ObservableProperty]
    public partial int CounterStart { get; set; } = 1;

    /// <summary><em>Liczba cyfr (zera z przodu)</em>.</summary>
    [ObservableProperty]
    public partial int CounterWidth { get; set; }

    /// <summary><em>Krok</em>.</summary>
    [ObservableProperty]
    public partial int CounterStep { get; set; } = 1;

    /// <summary>A rule uses the counter (the <em>Numeracja</em> panel is shown).</summary>
    [ObservableProperty]
    public partial bool HasCounter { get; private set; }

    /// <summary>The prefix is the literal start of the counter rule (<c>kd</c> of <c>kd{$nr:201}</c>) and can be edited here.</summary>
    [ObservableProperty]
    public partial bool CanEditPrefix { get; private set; }

    /// <summary><em>Pierwszy wolny numer: kd261 (najwyższy istniejący: kd260)</em>, or <em>bez sprawdzenia w AD</em>.</summary>
    [ObservableProperty]
    public partial string? FirstFreeText { get; private set; }

    /// <summary>The forest's UPN suffixes and domain names (also <see cref="BulkCreateInput.UpnSuffixes"/>).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> UpnSuffixes { get; private set; } = [];

    [ObservableProperty]
    public partial string? UpnSuffix { get; set; }

    /// <summary>The UPN rule renders a whole address (<c>{mail}</c>): the suffix comes <em>z wzorca</em>.</summary>
    [ObservableProperty]
    public partial bool IsUpnFromPattern { get; private set; }

    /// <summary><em>Domena adresów e-mail</em> (default: the UPN suffix).</summary>
    [ObservableProperty]
    public partial string? MailDomain { get; set; }

    /// <summary>The mail domain is <c>.local</c>, <c>.lan</c> or <c>.internal</c> (it receives no mail from the internet).</summary>
    [ObservableProperty]
    public partial string? MailDomainWarning { get; private set; }

    /// <summary>The fields, tokens and functions <em>Wstaw pole</em> offers, by their Polish labels.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<BulkTokenOption> InsertableFields { get; private set; } = [];

    // ---- Profiles ---------------------------------------------------------------------------------------------

    /// <summary><em>Profil nazewnictwa</em>: the built-ins of the kind and the saved profiles (id → name).</summary>
    public ObservableCollection<Option<string>> Profiles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBuiltInProfile))]
    [NotifyCanExecuteChangedFor(nameof(DeleteProfileCommand))]
    public partial Option<string>? Profile { get; set; }

    /// <summary>The rules or options differ from the selected profile (<em>(zmieniony)</em>; a kind change asks first).</summary>
    [ObservableProperty]
    public partial bool IsModified { get; private set; }

    // ---- Konto ------------------------------------------------------------------------------------------------

    public IReadOnlyList<Option<InitialPasswordMode>> PasswordModes { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGenerate), nameof(IsSharedPassword), nameof(CanEnable), nameof(NeedsSharedPassword), nameof(SharedPasswordNote))]
    public partial InitialPasswordMode PasswordMode { get; set; } = InitialPasswordMode.Generate;

    public bool IsGenerate => PasswordMode == InitialPasswordMode.Generate;

    public bool IsSharedPassword => PasswordMode == InitialPasswordMode.SameForAll;

    /// <summary>The length of generated passwords (<see cref="PasswordGenerator.MinimumLength"/>–<see cref="PasswordGenerator.MaximumLength"/>).</summary>
    [ObservableProperty]
    public partial int GeneratedLength { get; set; } = PasswordGenerator.DefaultLength;

    public int MinimumLength => PasswordGenerator.MinimumLength;

    public int MaximumLength => PasswordGenerator.MaximumLength;

    /// <summary>The domain's <c>minPwdLength</c> once known (<em>minimum domeny: 7</em>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DomainMinimumText))]
    public partial int? DomainMinimumLength { get; private set; }

    /// <summary><em>minimum domeny: 7</em>, or null while unknown.</summary>
    public string? DomainMinimumText => DomainMinimumLength is { } minimum ? Services.Localizer.Format(L.BulkCreate.DomainMinimum, minimum) : null;

    /// <summary>The shared password box (set by the view's <c>PasswordBox</c>; the password is never bound).</summary>
    public IPasswordInput? PasswordInput { get; set; }

    /// <summary>The confirmation box of the shared password.</summary>
    public IPasswordInput? ConfirmPasswordInput { get; set; }

    /// <summary>The localized problem of the shared password (mismatch at plan time).</summary>
    [ObservableProperty]
    public partial string? PasswordError { get; private set; }

    [ObservableProperty]
    public partial bool MustChangeAtLogon { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SharedPasswordNote))]
    public partial bool Enabled { get; set; } = true;

    /// <summary>The accounts can be enabled (not without a password).</summary>
    public bool CanEnable => PasswordMode != InitialPasswordMode.None;

    /// <summary><em>Wszyscy będą znać hasło każdego konta do pierwszej zmiany</em> (one shared password, enabled accounts).</summary>
    public string? SharedPasswordNote => IsSharedPassword && Enabled ? Services.Localizer[L.BulkCreate.SharedPasswordEnabledNote] : null;

    [ObservableProperty]
    public partial bool HasExpiry { get; set; }

    /// <summary>The last day of the accounts (the end of that local day).</summary>
    [ObservableProperty]
    public partial DateTime? ExpiryDate { get; set; }

    /// <summary>The scopes of created groups.</summary>
    public IReadOnlyList<Option<GroupScope>> GroupScopes { get; }

    [ObservableProperty]
    public partial GroupScope GroupScope { get; set; } = GroupScope.Global;

    [ObservableProperty]
    public partial bool IsSecurityGroup { get; set; } = true;

    /// <summary>The principal allowed to join created computers (per run only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(JoinerText))]
    public partial DirectoryItem? Joiner { get; private set; }

    /// <summary>The joiner's name, or <em>(nie wybrano)</em>.</summary>
    public string JoinerText => _joinerName ?? Services.Localizer[L.BulkCreate.NoJoiner];

    // ---- Grupy ------------------------------------------------------------------------------------------------

    /// <summary>The groups every created object is added to.</summary>
    public ObservableCollection<DirectoryItem> Groups { get; } = [];

    // ---- Zaawansowane -----------------------------------------------------------------------------------------

    public IReadOnlyList<Option<CounterMode>> CounterModes { get; }

    [ObservableProperty]
    public partial CounterMode CounterMode { get; set; } = CounterMode.FillGaps;

    /// <summary><em>Nie używaj ponownie numerów z Kosza AD</em> (<see cref="BulkCreateRules.ReuseRecycleBinNumbers"/> inverted).</summary>
    [ObservableProperty]
    public partial bool AvoidRecycleBinNumbers { get; set; } = true;

    public IReadOnlyList<Option<CollisionStrategy>> CollisionStrategies { get; }

    [ObservableProperty]
    public partial CollisionStrategy Collisions { get; set; } = CollisionStrategy.AppendNumber;

    /// <summary>Computers always mark a taken name as an error (2.4).</summary>
    public bool CanChooseCollisions => Kind != ObjectKind.Computer;

    [ObservableProperty]
    public partial bool SanitizeIdentifiers { get; set; } = true;

    [ObservableProperty]
    public partial bool TruncateNames { get; set; } = true;

    [ObservableProperty]
    public partial bool AddPrimarySmtp { get; set; }

    public IReadOnlyList<Option<PhoneFormat>> PhoneFormats { get; }

    [ObservableProperty]
    public partial PhoneFormat Phones { get; set; } = PhoneFormat.AsIs;

    /// <summary><em>Szukaj istniejących kont tej osoby</em>.</summary>
    [ObservableProperty]
    public partial bool MatchExisting { get; set; } = true;

    /// <summary>The one-line summary of the collapsed <em>Zaawansowane</em> section.</summary>
    [ObservableProperty]
    public partial string AdvancedSummary { get; private set; } = string.Empty;

    /// <summary>The localized problem of the rules or options (null when the step can be left).</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>The shared password mode is chosen and the boxes are empty (<em>wpisz hasło ponownie</em> after a Back).</summary>
    public bool NeedsSharedPassword => IsUserKind && IsSharedPassword && PasswordInput?.GetPassword() is not { Length: > 0 };

    [RelayCommand]
    private void AddRule()
    {
        var rule = new AttributeRuleViewModel(string.Empty, string.Empty) { Owner = this };
        Rules.Add(rule);
        ActiveRule = rule;
        OnRulesEdited();
    }

    [RelayCommand]
    private void RemoveRule(AttributeRuleViewModel? rule)
    {
        if (rule is null || rule.IsCore || !Rules.Remove(rule))
        {
            return;
        }

        if (ReferenceEquals(ActiveRule, rule))
        {
            ActiveRule = null;
        }

        OnRulesEdited();
    }

    /// <summary><em>Wstaw pole</em>: inserts a token (<c>{givenName}</c>, <c>{$nr:201}</c>, a function …) into the focused rule.</summary>
    [RelayCommand]
    private void InsertToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        var rule = ActiveRule ?? Rules.FirstOrDefault(r => string.Equals(r.Attribute, Attr.SamAccountName, StringComparison.OrdinalIgnoreCase)) ?? Rules.FirstOrDefault();
        if (rule is null)
        {
            return;
        }

        var (pattern, caret) = PatternEditing.Insert(rule.Pattern, Math.Clamp(rule.CaretIndex, 0, rule.Pattern.Length), token);
        rule.Pattern = pattern;
        rule.CaretIndex = caret;
        ActiveRule = rule;
    }

    /// <summary>The quick fix of a literal ending in digits: <c>kd201</c> → <c>kd{$nr:201}</c>.</summary>
    [RelayCommand]
    private void ApplyCounterSuggestion(AttributeRuleViewModel? rule)
    {
        if (rule?.CounterFix is { } fix)
        {
            rule.Pattern = fix;
        }
    }

    /// <summary>The preset <em>UPN = adres e-mail</em> (rule <c>{mail}</c>).</summary>
    [RelayCommand]
    private void UseMailAsUpn()
    {
        var rule = Rules.FirstOrDefault(r => string.Equals(r.Attribute, Attr.UserPrincipalName, StringComparison.OrdinalIgnoreCase));
        if (rule is null)
        {
            rule = new AttributeRuleViewModel(Attr.UserPrincipalName, RuleLabel(Attr.UserPrincipalName)) { Owner = this, IsCore = true };
            Rules.Add(rule);
        }

        rule.Pattern = "{" + Attr.Mail + "}";
    }

    [RelayCommand]
    private async Task AddGroupsAsync()
    {
        if (Session() is not { } session)
        {
            return;
        }

        var picked = await Services.Dialogs.PickObjectsAsync(session, [ObjectKind.Group], multiSelect: true).ConfigureAwait(true);
        var added = picked.Where(dn => !Groups.Any(g => DistinguishedName.AreEqual(g.Dn, dn))).ToList();
        if (added.Count == 0 || _disposed)
        {
            return;
        }

        foreach (var dn in added)
        {
            Groups.Add(DirectoryItem.Unresolved(dn, Services.Localizer) with { IsResolved = true });
        }

        OnOptionEdited();
        await ResolveGroupsAsync(session, added).ConfigureAwait(true);
    }

    [RelayCommand]
    private void RemoveGroup(DirectoryItem? group)
    {
        if (group is not null && Groups.Remove(group))
        {
            OnOptionEdited();
        }
    }

    [RelayCommand]
    private async Task PickJoinerAsync()
    {
        if (Session() is not { } session)
        {
            return;
        }

        var picked = await Services.Dialogs.PickObjectsAsync(session, [ObjectKind.User, ObjectKind.Group], multiSelect: false).ConfigureAwait(true);
        if (picked.Count == 0 || _disposed)
        {
            return;
        }

        try
        {
            var principal = await Task.Run(() => session.GetAsync(picked[0], [.. DirectoryItem.Attributes], CancellationToken.None)).ConfigureAwait(true);
            if (principal?.ObjectSid is not { } sid)
            {
                OptionsError = Services.Localizer[L.BulkCreate.JoinerNotFound];
                return;
            }

            _joinerSid = sid;
            _joinerName = principal.GetString(Attr.SamAccountName) is { Length: > 0 } login
                ? $"{session.Info.DomainNetBiosName ?? session.Info.DomainDnsName}\\{login}"
                : principal.Name;
            Joiner = DirectoryItem.From(principal, Services.Localizer);
            OptionsError = null;
        }
        catch (Exception ex) when (ex is DirectoryException or InvalidOperationException or ObjectDisposedException)
        {
            OptionsError = Services.Errors.Translate(ex);
        }

        OnOptionEdited();
    }

    [RelayCommand]
    private void ClearJoiner()
    {
        _joinerSid = null;
        _joinerName = null;
        Joiner = null;
        OnOptionEdited();
    }

    /// <summary>The naming rules and options of the run (input of the planner).</summary>
    public BulkCreateRules ToRules() => new()
    {
        Kind = Kind,
        Attributes = [.. Rules
            .Where(r => r.Attribute.Trim().Length > 0 && r.Pattern.Trim().Length > 0)
            .Select(r => new AttributeRule(r.Attribute.Trim(), r.Pattern, r.OverridesColumn))],
        UpnSuffix = string.IsNullOrWhiteSpace(UpnSuffix) ? null : UpnSuffix.Trim(),
        MailDomain = string.IsNullOrWhiteSpace(MailDomain) ? null : MailDomain.Trim(),
        CounterMode = CounterMode,
        ReuseRecycleBinNumbers = !AvoidRecycleBinNumbers,
        Collisions = Kind == ObjectKind.Computer ? CollisionStrategy.MarkError : Collisions,
        SanitizeIdentifiers = SanitizeIdentifiers,
        TruncateNames = TruncateNames,
        AddPrimarySmtp = AddPrimarySmtp,
        Phones = Phones,
    };

    /// <summary>The account options of the run (the shared password is never part of them).</summary>
    public BulkAccountOptions ToOptions() => new()
    {
        PasswordMode = IsUserKind ? PasswordMode : InitialPasswordMode.None,
        GeneratedLength = GeneratedLength,
        MustChangeAtLogon = IsUserKind && MustChangeAtLogon,
        Enabled = !IsUserKind || (Enabled && PasswordMode != InitialPasswordMode.None),
        ExpiresAt = IsUserKind && HasExpiry && ExpiryDate is { } date ? EndOfDay(date) : null,
        GroupDns = [.. Groups.Select(g => g.Dn)],
        GroupScope = GroupScope,
        SecurityGroup = IsSecurityGroup,
        JoinerSid = IsComputerKind ? _joinerSid : null,
        JoinerName = IsComputerKind ? _joinerName : null,
        MatchAttributes = MatchExisting ? null : [],
    };

    /// <summary>
    /// Reads and clears the shared password boxes (UI thread only; B4 calls it at plan time): null without the shared mode
    /// or with empty boxes; a confirmation mismatch sets <see cref="Error"/> (<c>L.Batch.PasswordMismatch</c>) and returns null.
    /// </summary>
    public string? ReadSharedPassword()
    {
        if (!IsUserKind || !IsSharedPassword)
        {
            return null;
        }

        var password = PasswordInput?.GetPassword() ?? string.Empty;
        if (password.Length == 0)
        {
            return null;
        }

        var confirm = ConfirmPasswordInput?.GetPassword() ?? string.Empty;
        if (!string.Equals(password, confirm, StringComparison.Ordinal))
        {
            PasswordError = Services.Localizer[L.Batch.PasswordMismatch];
            Revalidate(raise: true);
            return null;
        }

        ClearSharedPassword();
        return password;
    }

    /// <summary>Clears the shared password boxes (after the plan was built, on close).</summary>
    public void ClearSharedPassword()
    {
        PasswordInput?.Clear();
        ConfirmPasswordInput?.Clear();
        NotifyPasswordChanged();
    }

    /// <summary>Call from the view when the operator typed in a password box (the boxes raise no change notification).</summary>
    public void NotifyPasswordChanged()
    {
        if (PasswordError is not null)
        {
            PasswordError = null;
            Revalidate(raise: true);
        }

        OnPropertyChanged(nameof(NeedsSharedPassword));
    }

    /// <summary>Reads the UPN suffixes and the domain minimum and selects the profile (container default, last used, <em>Standardowy</em>).</summary>
    public async Task InitializeAsync()
    {
        var session = Session();
        if (session is not null)
        {
            try
            {
                UpnSuffixes = await UpnFieldViewModel.ReadUpnSuffixesAsync(session, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is DirectoryException or InvalidOperationException or ObjectDisposedException)
            {
                // The window's domain stays the only suffix.
            }

            DomainMinimumLength = await ReadDomainMinimumAsync(session).ConfigureAwait(true);
        }

        if (_disposed)
        {
            return;
        }

        var selected = _store.Select(Kind, ContainerDn(), session?.Info.DefaultNamingContext);
        _selectedFor = ContainerDn();
        ApplyProfile(selected, warn: true);
    }

    /// <summary>The step is entered: the container's default profile (if the container changed), the counter preview and the samples.</summary>
    public async Task OnEnteredAsync()
    {
        var container = ContainerDn();
        if (!IsModified && !DistinguishedName.AreEqual(container, _selectedFor)
            && Session()?.Info.DefaultNamingContext is { } namingContext
            && _store.DefaultFor(Kind, container, namingContext) is { } byContainer)
        {
            ApplyProfile(byContainer, warn: true);
        }

        _selectedFor = container;
        RefreshProfileList();

        // The container may have changed in step 1: the default mark and the button follow it.
        RefreshProfileState();
        Recompile();
        RefreshFields();
        await RefreshPreviewAsync().ConfigureAwait(true);
    }

    /// <summary>The kind changed (the wizard asked before when <see cref="IsModified"/>): the kind's default profile.</summary>
    public void OnKindChanged(ObjectKind kind)
    {
        Kind = kind;
        AttributeOptions = BulkColumnsViewModel.OptionsFor(Session()?.Schema ?? _openedSchema, Services.Localizer, kind);
        RefreshProfileList();
        var selected = _store.Select(kind, ContainerDn(), Session()?.Info.DefaultNamingContext);
        ApplyProfile(selected, warn: true);
    }

    /// <summary>The window's session ended (no more previews): the first free number is no longer checked.</summary>
    public void OnSessionEnded()
    {
        _preview.Cancel();
        _firstFree = null;
        _highest = null;
        RefreshFirstFreeText(checkedInDirectory: false);
    }

    /// <summary>Clears the password boxes and unsubscribes.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _preview.Cancel();
        _samples.Cancel();
        Columns.Changed -= OnColumnsChanged;
        Columns.PropertyChanged -= OnColumnsPropertyChanged;
        Services.Localizer.CultureChanged -= OnCultureChanged;
        PasswordInput?.Clear();
        ConfirmPasswordInput?.Clear();
        PasswordInput = null;
        ConfirmPasswordInput = null;
        foreach (var rule in Rules)
        {
            rule.Sample = string.Empty;
        }

        Changed = null;
    }

    public override string ToString() => $"Bulk create rules: {Kind}, {Rules.Count} rules, {PasswordMode}";

    // ---- Options ----------------------------------------------------------------------------------------------

    /// <summary>A problem of an option command (joiner not found, profile list full …).</summary>
    [ObservableProperty]
    public partial string? OptionsError { get; private set; }

    partial void OnPasswordModeChanged(InitialPasswordMode value)
    {
        if (value == InitialPasswordMode.None)
        {
            Enabled = false;
        }

        OnOptionEdited();
    }

    partial void OnGeneratedLengthChanged(int value) => OnOptionEdited();

    partial void OnMustChangeAtLogonChanged(bool value) => OnOptionEdited();

    partial void OnEnabledChanged(bool value) => OnOptionEdited();

    partial void OnHasExpiryChanged(bool value)
    {
        if (value && ExpiryDate is null)
        {
            ExpiryDate = TimeZoneInfo.ConvertTime(Services.Time.GetUtcNow(), Services.Time.LocalTimeZone).Date.AddMonths(3);
        }

        OnOptionEdited();
    }

    partial void OnExpiryDateChanged(DateTime? value) => OnOptionEdited();

    partial void OnGroupScopeChanged(GroupScope value) => OnOptionEdited();

    partial void OnIsSecurityGroupChanged(bool value) => OnOptionEdited();

    partial void OnCounterModeChanged(CounterMode value)
    {
        OnOptionEdited();
        if (!IsApplying && !_disposed)
        {
            RefreshPreviewIfCounterChanged();
        }
    }

    partial void OnAvoidRecycleBinNumbersChanged(bool value)
    {
        OnOptionEdited();
        if (!IsApplying && !_disposed)
        {
            RefreshPreviewIfCounterChanged();
        }
    }

    partial void OnCollisionsChanged(CollisionStrategy value) => OnOptionEdited();

    partial void OnSanitizeIdentifiersChanged(bool value) => OnRulesEdited();

    partial void OnTruncateNamesChanged(bool value) => OnRulesEdited();

    partial void OnAddPrimarySmtpChanged(bool value) => OnOptionEdited();

    partial void OnPhonesChanged(PhoneFormat value)
    {
        Columns.InternationalPhones = value == PhoneFormat.International;
        OnRulesEdited();
    }

    partial void OnMatchExistingChanged(bool value) => OnOptionEdited();

    /// <summary>An account option changed: validation, the summary, <em>(zmieniony)</em> and the wizard.</summary>
    private void OnOptionEdited()
    {
        if (IsApplying || _disposed)
        {
            return;
        }

        Revalidate(raise: true);
    }

    /// <summary>The validation of the options (a rule problem comes first).</summary>
    private string? OptionsProblem()
    {
        var localizer = Services.Localizer;
        if (IsUserKind)
        {
            if (IsGenerate && GeneratedLength is < PasswordGenerator.MinimumLength or > PasswordGenerator.MaximumLength)
            {
                return localizer.Format(L.Batch.PasswordLength, PasswordGenerator.MinimumLength, PasswordGenerator.MaximumLength);
            }

            if (IsGenerate && DomainMinimumLength is { } minimum && GeneratedLength < minimum)
            {
                return localizer.Format(L.BulkCreate.LengthBelowDomain, minimum);
            }

            if (HasExpiry && ExpiryDate is null)
            {
                return localizer[L.BulkCreate.ExpiryDateRequired];
            }

            if (HasExpiry && ExpiryDate is { } date && EndOfDay(date) <= Services.Time.GetUtcNow())
            {
                return localizer[L.BulkCreate.ExpiryInPast];
            }

            if (!IsUpnFromPattern && string.IsNullOrWhiteSpace(UpnSuffix)
                && Rules.Any(r => string.Equals(r.Attribute, Attr.UserPrincipalName, StringComparison.OrdinalIgnoreCase) && r.Pattern.Trim().Length > 0))
            {
                return localizer[L.BulkCreate.ChooseUpnSuffix];
            }
        }

        return null;
    }

    /// <summary>The end of the chosen local day (the next local midnight), as the account expiry of ADUC.</summary>
    private DateTimeOffset EndOfDay(DateTime date)
    {
        var midnight = DateTime.SpecifyKind(date.Date.AddDays(1), DateTimeKind.Unspecified);
        return new DateTimeOffset(midnight, Services.Time.LocalTimeZone.GetUtcOffset(midnight));
    }

    private static async Task<int?> ReadDomainMinimumAsync(IDirectorySession session)
    {
        try
        {
            var domain = await Task.Run(() => session.GetAsync(session.Info.DefaultNamingContext, ["minPwdLength"], CancellationToken.None)).ConfigureAwait(true);
            return domain?.GetString("minPwdLength") is { } text && int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minimum)
                ? minimum
                : null;
        }
        catch (Exception ex) when (ex is DirectoryException or InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }
    }

    private async Task ResolveGroupsAsync(IDirectorySession session, IReadOnlyList<string> dns)
    {
        foreach (var dn in dns)
        {
            try
            {
                var group = await Task.Run(() => session.GetAsync(dn, [.. DirectoryItem.Attributes], CancellationToken.None)).ConfigureAwait(true);
                var index = Groups.ToList().FindIndex(g => DistinguishedName.AreEqual(g.Dn, dn));
                if (group is not null && index >= 0 && !_disposed)
                {
                    Groups[index] = DirectoryItem.From(group, Services.Localizer);
                }
            }
            catch (Exception ex) when (ex is DirectoryException or InvalidOperationException or ObjectDisposedException)
            {
                // The RDN value stays the name.
            }
        }
    }

    private void OnColumnsChanged(object? sender, EventArgs e)
    {
        if (_disposed || IsApplying)
        {
            return;
        }

        Recompile();
        RefreshFields();
        RefreshSamples();
    }

    private void OnColumnsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BulkColumnsViewModel.InternationalPhones))
        {
            var phones = Columns.InternationalPhones ? PhoneFormat.International : PhoneFormat.AsIs;
            if (Phones != phones)
            {
                Phones = phones;
            }
        }
        else if (e.PropertyName == nameof(BulkColumnsViewModel.AttributeOptions) && Columns.Kind == Kind && Columns.AttributeOptions.Count > 0)
        {
            AttributeOptions = Columns.AttributeOptions;
        }
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        foreach (var rule in Rules)
        {
            rule.AttributeText = RuleLabel(rule.Attribute);
        }

        OnPropertyChanged(nameof(DomainMinimumText));
        OnPropertyChanged(nameof(JoinerText));
        OnPropertyChanged(nameof(SharedPasswordNote));
        OnPropertyChanged(nameof(NoCounterHint));
        OnPropertyChanged(nameof(HomeFoldersNote));
        OnPropertyChanged(nameof(ModifiedText));
        RefreshProfileList();
        Recompile();
        RefreshFields();
        RefreshFirstFreeText(_previewChecked);
    }
}

/// <summary>One naming rule of step 3 (2.6).</summary>
public sealed partial class AttributeRuleViewModel : ObservableObject
{
    private bool _syncingAttribute;

    /// <param name="attribute">The LDAP display name (for the UPN: the prefix pattern).</param>
    /// <param name="attributeText">The localized label with the LDAP name (<em>Nazwa logowania (sAMAccountName)</em>).</param>
    internal AttributeRuleViewModel(string attribute, string attributeText)
    {
        Attribute = attribute ?? throw new ArgumentNullException(nameof(attribute));
        AttributeText = attributeText ?? throw new ArgumentNullException(nameof(attributeText));
    }

    /// <summary>The step that owns the rule (recompiles on every change).</summary>
    internal BulkRulesViewModel? Owner { get; init; }

    /// <summary>One of the names every object gets (<c>cn</c>, <c>sAMAccountName</c>, the UPN …): the attribute is fixed and the rule cannot be removed.</summary>
    public bool IsCore { get; init; }

    /// <summary>The attribute can be chosen (rules added with <em>Dodaj regułę</em>).</summary>
    public bool CanEditAttribute => !IsCore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpn))]
    public partial string Attribute { get; set; }

    [ObservableProperty]
    public partial string AttributeText { get; set; }

    [ObservableProperty]
    public partial string Pattern { get; set; } = string.Empty;

    /// <summary><em>Reguła zastępuje kolumnę</em>.</summary>
    [ObservableProperty]
    public partial bool OverridesColumn { get; set; }

    /// <summary>A mapped column feeds the attribute too (<em>Kolumna „E-mail” ma pierwszeństwo…</em>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasColumnNote))]
    public partial string? ColumnNote { get; set; }

    public bool HasColumnNote => ColumnNote is not null;

    /// <summary>The live sample (three rows plus the longest, <em>(zmieniono znaki)</em> / <em>(skrócono)</em>).</summary>
    [ObservableProperty]
    public partial string Sample { get; set; } = string.Empty;

    /// <summary>The localized pattern problem with its position and token.</summary>
    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>The problem stops the step (an error-level pattern problem; warnings do not).</summary>
    [ObservableProperty]
    public partial bool IsError { get; set; }

    /// <summary>The did-you-mean or the counter quick fix (<em>Numerować kolejno od 201?</em>).</summary>
    [ObservableProperty]
    public partial string? Suggestion { get; set; }

    /// <summary>The pattern of the counter quick fix (<c>kd{$nr:201}</c>), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCounterFix))]
    public partial string? CounterFix { get; set; }

    public bool HasCounterFix => CounterFix is not null;

    /// <summary>The rule uses the counter.</summary>
    [ObservableProperty]
    public partial bool IsCounter { get; set; }

    /// <summary>The hint of an empty pattern (<em>bez reguły – wartość z kolumny</em>).</summary>
    [ObservableProperty]
    public partial string? EmptyHint { get; set; }

    /// <summary>The UPN rule: its pattern is the part before <c>@</c> (the suffix is chosen next to it).</summary>
    public bool IsUpn => string.Equals(Attribute, Core.ActiveDirectory.Attr.UserPrincipalName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The caret in the pattern box (set by the view; <em>Wstaw pole</em> inserts there).</summary>
    public int CaretIndex { get; set; }

    /// <summary>The rule as the planner takes it.</summary>
    public AttributeRule ToRule() => new(Attribute, Pattern, OverridesColumn);

    /// <summary>The attribute and the flags – a pattern may hold literal names.</summary>
    public override string ToString() => $"Rule {Attribute} (counter={IsCounter}, overrides={OverridesColumn})";

    partial void OnPatternChanged(string value)
    {
        CaretIndex = Math.Min(CaretIndex, value.Length);
        Owner?.OnRuleEdited(this);
    }

    partial void OnOverridesColumnChanged(bool value) => Owner?.OnRuleEdited(this);

    partial void OnAttributeChanged(string value)
    {
        if (!_syncingAttribute && Owner is not null)
        {
            _syncingAttribute = true;
            try
            {
                AttributeText = Owner.RuleLabel(value);
            }
            finally
            {
                _syncingAttribute = false;
            }
        }

        Owner?.OnRuleEdited(this);
    }

    partial void OnAttributeTextChanged(string value)
    {
        if (_syncingAttribute || Owner is null || IsCore)
        {
            return;
        }

        var resolved = Owner.ResolveAttribute(value) ?? string.Empty;
        if (!string.Equals(resolved, Attribute, StringComparison.Ordinal))
        {
            _syncingAttribute = true;
            try
            {
                Attribute = resolved;
            }
            finally
            {
                _syncingAttribute = false;
            }
        }
    }
}

/// <summary>An entry of <em>Wstaw pole</em> (2.6): a field by its Polish label, a column, a special token or a function.</summary>
public sealed class BulkTokenOption
{
    internal BulkTokenOption(string group, string text, string token, string? description = null)
    {
        Group = group;
        Text = text;
        Token = token;
        Description = description;
    }

    /// <summary><em>Pola</em>, <em>Kolumny</em>, <em>Specjalne</em>, <em>Funkcje</em>.</summary>
    public string Group { get; }

    /// <summary>The label (<em>Imię</em>, <em>Kolejny numer</em>, <em>małe litery</em>).</summary>
    public string Text { get; }

    /// <summary>What is inserted (<c>{givenName}</c>, <c>{$nr:201}</c>, <c>|lower</c>).</summary>
    public string Token { get; }

    /// <summary>The longer explanation of a function, or null.</summary>
    public string? Description { get; }

    /// <summary>A function: inserted into the token at the caret.</summary>
    public bool IsFunction => Token.StartsWith('|');

    /// <summary>The menu text: <em>Imię → {givenName}</em>.</summary>
    public string MenuText => Description is null ? $"{Text}  →  {Token}" : $"{Token}  –  {Description}";

    /// <summary>The kind of entry only – a column name is sheet data.</summary>
    public override string ToString() => $"Token option ({(IsFunction ? "function" : "field")})";
}
