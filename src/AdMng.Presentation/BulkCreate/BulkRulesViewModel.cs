using System.Collections.ObjectModel;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Core.Security;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
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
/// Contract stub: the option lists are built and the members the wizard host calls (<see cref="InitializeAsync"/>,
/// <see cref="OnEnteredAsync"/>, <see cref="OnKindChanged"/>, <see cref="OnSessionEnded"/>, <see cref="Dispose"/>) do nothing;
/// the commands and the outputs throw until B3 writes them.
/// </remarks>
public sealed partial class BulkRulesViewModel : ObservableObject, IDisposable
{
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
    public ObjectKind Kind { get; private set; }

    // ---- Nazwy ------------------------------------------------------------------------------------------------

    /// <summary>The naming rules (attribute, pattern, sample, error).</summary>
    public ObservableCollection<AttributeRuleViewModel> Rules { get; } = [];

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

    /// <summary><em>Pierwszy wolny numer: kd261 (najwyższy istniejący: kd260)</em>, or <em>bez sprawdzenia w AD</em>.</summary>
    [ObservableProperty]
    public partial string? FirstFreeText { get; private set; }

    /// <summary>The forest's UPN suffixes and domain names (also <see cref="BulkCreateInput.UpnSuffixes"/>).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> UpnSuffixes { get; private set; } = [];

    [ObservableProperty]
    public partial string? UpnSuffix { get; set; }

    /// <summary><em>Domena adresów e-mail</em> (default: the UPN suffix).</summary>
    [ObservableProperty]
    public partial string? MailDomain { get; set; }

    // ---- Profiles ---------------------------------------------------------------------------------------------

    /// <summary><em>Profil nazewnictwa</em>: the built-ins of the kind and the saved profiles (id → name).</summary>
    public ObservableCollection<Option<string>> Profiles { get; } = [];

    [ObservableProperty]
    public partial Option<string>? Profile { get; set; }

    /// <summary>The rules or options differ from the selected profile (<em>(zmieniony)</em>; a kind change asks first).</summary>
    [ObservableProperty]
    public partial bool IsModified { get; private set; }

    // ---- Konto ------------------------------------------------------------------------------------------------

    public IReadOnlyList<Option<InitialPasswordMode>> PasswordModes { get; }

    [ObservableProperty]
    public partial InitialPasswordMode PasswordMode { get; set; } = InitialPasswordMode.Generate;

    /// <summary>The length of generated passwords (<see cref="PasswordGenerator.MinimumLength"/>–<see cref="PasswordGenerator.MaximumLength"/>).</summary>
    [ObservableProperty]
    public partial int GeneratedLength { get; set; } = PasswordGenerator.DefaultLength;

    /// <summary>The domain's <c>minPwdLength</c> once known (<em>minimum domeny: 7</em>).</summary>
    [ObservableProperty]
    public partial int? DomainMinimumLength { get; private set; }

    /// <summary>The shared password box (set by the view's <c>PasswordBox</c>; the password is never bound).</summary>
    public IPasswordInput? PasswordInput { get; set; }

    /// <summary>The confirmation box of the shared password.</summary>
    public IPasswordInput? ConfirmPasswordInput { get; set; }

    [ObservableProperty]
    public partial bool MustChangeAtLogon { get; set; } = true;

    [ObservableProperty]
    public partial bool Enabled { get; set; } = true;

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
    public partial DirectoryItem? Joiner { get; private set; }

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

    /// <summary>The shared password mode is chosen and the boxes are empty (<em>wpisz hasło ponownie</em> after a Back). Contract: false.</summary>
    public bool NeedsSharedPassword => false;

    [RelayCommand]
    private void AddRule() => throw new NotImplementedException("F3 B3: BulkRulesViewModel.AddRule (2.6).");

    [RelayCommand]
    private void RemoveRule(AttributeRuleViewModel? rule) => throw new NotImplementedException("F3 B3: BulkRulesViewModel.RemoveRule (2.6).");

    /// <summary><em>Wstaw pole</em>: inserts a token (<c>{givenName}</c>, <c>{$nr:201}</c>, a function …) into the focused rule.</summary>
    [RelayCommand]
    private void InsertToken(string? token) => throw new NotImplementedException("F3 B3: BulkRulesViewModel.InsertToken (2.6).");

    /// <summary>The quick fix of a literal ending in digits: <c>kd201</c> → <c>kd{$nr:201}</c>.</summary>
    [RelayCommand]
    private void ApplyCounterSuggestion(AttributeRuleViewModel? rule) => throw new NotImplementedException("F3 B3: BulkRulesViewModel.ApplyCounterSuggestion (2.6).");

    /// <summary>The preset <em>UPN = adres e-mail</em> (rule <c>{mail}</c>).</summary>
    [RelayCommand]
    private void UseMailAsUpn() => throw new NotImplementedException("F3 B3: BulkRulesViewModel.UseMailAsUpn (2.6).");

    [RelayCommand]
    private Task SaveProfileAsync() => throw new NotImplementedException("F3 B3: BulkRulesViewModel.SaveProfile (2.6).");

    [RelayCommand]
    private Task SaveProfileAsAsync() => throw new NotImplementedException("F3 B3: BulkRulesViewModel.SaveProfileAs (2.6).");

    [RelayCommand]
    private Task DeleteProfileAsync() => throw new NotImplementedException("F3 B3: BulkRulesViewModel.DeleteProfile (2.6).");

    /// <summary><em>Domyślny dla tego kontenera</em>.</summary>
    [RelayCommand]
    private Task SetDefaultForContainerAsync() => throw new NotImplementedException("F3 B3: BulkRulesViewModel.SetDefaultForContainer (2.6).");

    [RelayCommand]
    private Task AddGroupsAsync() => throw new NotImplementedException("F3 B3: BulkRulesViewModel.AddGroups (2.6).");

    [RelayCommand]
    private void RemoveGroup(DirectoryItem? group) => throw new NotImplementedException("F3 B3: BulkRulesViewModel.RemoveGroup (2.6).");

    [RelayCommand]
    private Task PickJoinerAsync() => throw new NotImplementedException("F3 B3: BulkRulesViewModel.PickJoiner (2.6).");

    /// <summary>The naming rules and options of the run (input of the planner).</summary>
    public BulkCreateRules ToRules() => throw new NotImplementedException("F3 B3: BulkRulesViewModel.ToRules (2.6).");

    /// <summary>The account options of the run (the shared password is never part of them).</summary>
    public BulkAccountOptions ToOptions() => throw new NotImplementedException("F3 B3: BulkRulesViewModel.ToOptions (2.6).");

    /// <summary>
    /// Reads and clears the shared password boxes (UI thread only; B4 calls it at plan time): null without the shared mode
    /// or with empty boxes; a confirmation mismatch sets <see cref="Error"/> (<c>L.Batch.PasswordMismatch</c>) and returns null.
    /// </summary>
    public string? ReadSharedPassword() => throw new NotImplementedException("F3 B3: BulkRulesViewModel.ReadSharedPassword (2.6).");

    /// <summary>Reads the UPN suffixes and selects the profile (container default, last used, <em>Standardowy</em>). Contract: nothing.</summary>
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>The step is entered: the counter preview (<see cref="IBulkCreatePlanner.PreviewCounterAsync"/>). Contract: nothing.</summary>
    public Task OnEnteredAsync() => Task.CompletedTask;

    /// <summary>The kind changed (the wizard asked before when <see cref="IsModified"/>): the kind's default profile. Contract: records the kind.</summary>
    public void OnKindChanged(ObjectKind kind)
    {
        Kind = kind;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The window's session ended (no more previews). Contract: nothing.</summary>
    public void OnSessionEnded()
    {
    }

    /// <summary>Clears the password boxes and unsubscribes (B3). Contract: nothing to undo.</summary>
    public void Dispose()
    {
    }

    public override string ToString() => $"Bulk create rules: {Kind}, {Rules.Count} rules, {PasswordMode}";
}

/// <summary>One naming rule of step 3 (2.6).</summary>
public sealed partial class AttributeRuleViewModel : ObservableObject
{
    /// <param name="attribute">The LDAP display name (for the UPN: the prefix pattern).</param>
    /// <param name="attributeText">The localized label with the LDAP name (<em>Nazwa logowania (sAMAccountName)</em>).</param>
    internal AttributeRuleViewModel(string attribute, string attributeText)
    {
        Attribute = attribute ?? throw new ArgumentNullException(nameof(attribute));
        AttributeText = attributeText ?? throw new ArgumentNullException(nameof(attributeText));
    }

    [ObservableProperty]
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
    public partial string? ColumnNote { get; set; }

    /// <summary>The live sample (three rows plus the longest, <em>(zmieniono znaki)</em> / <em>(skrócono)</em>).</summary>
    [ObservableProperty]
    public partial string Sample { get; set; } = string.Empty;

    /// <summary>The localized pattern problem with its position and token.</summary>
    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>The did-you-mean or the counter quick fix (<em>Numerować kolejno od 201?</em>).</summary>
    [ObservableProperty]
    public partial string? Suggestion { get; set; }

    /// <summary>The rule uses the counter.</summary>
    [ObservableProperty]
    public partial bool IsCounter { get; set; }

    /// <summary>The rule as the planner takes it.</summary>
    public AttributeRule ToRule() => new(Attribute, Pattern, OverridesColumn);

    /// <summary>The attribute and the flags – a pattern may hold literal names.</summary>
    public override string ToString() => $"Rule {Attribute} (counter={IsCounter}, overrides={OverridesColumn})";
}
