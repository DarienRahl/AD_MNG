using System.Collections.ObjectModel;
using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.PropertySheets;

/// <summary>A country/region choice for the picker (localized name).</summary>
/// <param name="Country">The country, or null for "(none)".</param>
/// <param name="Text">Localized name shown in the list.</param>
public sealed record CountryOption(CountryInfo? Country, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// Country/region picker that writes c (alpha-2), co (name) and countryCode (numeric) together, as ADUC does.
/// co is written in the UI language (ADUC behaviour: Polish ADUC writes "Polska").
/// </summary>
public sealed partial class CountryFieldViewModel : PropertyFieldViewModel
{
    private readonly IReadOnlyList<CountryOption> _standardOptions;
    private CountryOption? _original;

    internal CountryFieldViewModel(FieldContext context)
        : base(context, Attr.Country, context.Localizer[L.Labels.Country])
    {
        var comparer = StringComparer.Create(context.Culture, ignoreCase: true);
        _standardOptions =
        [
            new CountryOption(null, context.Localizer[L.Fields.NoCountry]),
            .. Countries.All
                .Select(c => new CountryOption(c, context.Localizer[DisplayNames.CountryKey(c.Code)]))
                .OrderBy(o => o.Text, comparer),
        ];
        Options = _standardOptions;
    }

    public IReadOnlyList<CountryOption> Options { get; private set; }

    [ObservableProperty]
    public partial CountryOption? SelectedOption { get; set; }

    internal override IReadOnlyList<string> WrittenAttributes => [Attr.Country, Attr.CountryName, Attr.CountryCode];

    partial void OnSelectedOptionChanged(CountryOption? value) => OnEdited();

    protected override void LoadCore(DirectoryObject obj)
    {
        var code = obj.GetString(Attr.Country);
        var country = Countries.FindByCode(code) ?? (string.IsNullOrEmpty(code) ? Countries.FindByNumericCode(obj.GetInt32(Attr.CountryCode)) : null);
        CountryOption option;
        IReadOnlyList<CountryOption> options = _standardOptions;
        if (country is not null)
        {
            option = _standardOptions.First(o => o.Country == country);
        }
        else if (!string.IsNullOrWhiteSpace(code))
        {
            // A code outside ISO 3166-1 (hand-edited data): keep it selectable so the value is not lost
            // (one extra entry per load, not one more on every reload).
            option = new CountryOption(new CountryInfo(code, obj.GetInt32(Attr.CountryCode) ?? 0), obj.GetString(Attr.CountryName) ?? code);
            options = [.. _standardOptions, option];
        }
        else
        {
            option = _standardOptions[0];
        }

        if (!ReferenceEquals(options, Options))
        {
            Options = options;
            OnPropertyChanged(nameof(Options));
        }

        _original = option;
        SelectedOption = option;
    }

    protected override void RevertCore() => SelectedOption = _original;

    protected override bool ComputeIsDirty() => !Equals(SelectedOption?.Country, _original?.Country);

    protected override IEnumerable<BatchOperation> CreateOperations()
    {
        if (SelectedOption?.Country is not { } country)
        {
            return
            [
                new SetValuesOperation(Attr.Country, []),
                new SetValuesOperation(Attr.CountryName, []),
                new SetValuesOperation(Attr.CountryCode, ["0"]),
            ];
        }

        return
        [
            new SetValuesOperation(Attr.Country, [country.Code]),
            new SetValuesOperation(Attr.CountryName, [SelectedOption.Text]),
            new SetValuesOperation(Attr.CountryCode, [country.NumericCode.ToString(CultureInfo.InvariantCulture)]),
        ];
    }
}

/// <summary>
/// Home folder (ADUC Profile page): either a local path (homeDirectory, no homeDrive) or a drive letter connected
/// to a UNC path (homeDrive + homeDirectory).
/// </summary>
public sealed partial class HomeFolderFieldViewModel : PropertyFieldViewModel
{
    private const string DefaultDrive = "Z:";

    private bool _originalUseDrive;
    private string _originalLocalPath = string.Empty;
    private string _originalDrive = DefaultDrive;
    private string _originalNetworkPath = string.Empty;

    internal HomeFolderFieldViewModel(FieldContext context)
        : base(context, Attr.HomeDirectory, context.Localizer[L.Labels.HomeFolder])
    {
    }

    /// <summary>Drive letters C: to Z:.</summary>
    public IReadOnlyList<string> Drives { get; } = [.. Enumerable.Range('C', 24).Select(c => $"{(char)c}:")];

    /// <summary>False = local path, true = connect a drive letter to a network path.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseLocalPath))]
    public partial bool UseDrive { get; set; }

    public bool UseLocalPath
    {
        get => !UseDrive;
        set => UseDrive = !value;
    }

    [ObservableProperty]
    public partial string LocalPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Drive { get; set; } = DefaultDrive;

    [ObservableProperty]
    public partial string NetworkPath { get; set; } = string.Empty;

    internal override IReadOnlyList<string> WrittenAttributes => [Attr.HomeDirectory, Attr.HomeDrive];

    partial void OnUseDriveChanged(bool value) => OnEdited();

    partial void OnLocalPathChanged(string value) => OnEdited();

    partial void OnDriveChanged(string value) => OnEdited();

    partial void OnNetworkPathChanged(string value) => OnEdited();

    protected override void LoadCore(DirectoryObject obj)
    {
        var directory = obj.GetString(Attr.HomeDirectory) ?? string.Empty;
        var drive = obj.GetString(Attr.HomeDrive);
        _originalUseDrive = !string.IsNullOrEmpty(drive);
        _originalDrive = _originalUseDrive ? NormalizeDrive(drive!) : DefaultDrive;
        _originalNetworkPath = _originalUseDrive ? directory : string.Empty;
        _originalLocalPath = _originalUseDrive ? string.Empty : directory;
        RevertCore();
    }

    protected override void RevertCore()
    {
        LocalPath = _originalLocalPath;
        Drive = _originalDrive;
        NetworkPath = _originalNetworkPath;
        UseDrive = _originalUseDrive;
    }

    protected override bool ComputeIsDirty() =>
        UseDrive != _originalUseDrive
        || (UseDrive
            ? !string.Equals(Drive, _originalDrive, StringComparison.OrdinalIgnoreCase) || !string.Equals(NetworkPath, _originalNetworkPath, StringComparison.Ordinal)
            : !string.Equals(LocalPath, _originalLocalPath, StringComparison.Ordinal));

    protected override string? Validate()
    {
        if (!UseDrive)
        {
            return LocalPath.Trim().Length > 0 && !Context.TryParse(Attr.HomeDirectory, LocalPath.Trim(), out _, out var localError) ? localError : null;
        }

        var path = NetworkPath.Trim();
        if (path.Length == 0 || !path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return Localizer[L.Fields.UncPathRequired];
        }

        return Context.TryParse(Attr.HomeDirectory, path, out _, out var error) ? null : error;
    }

    protected override IEnumerable<BatchOperation> CreateOperations()
    {
        if (UseDrive)
        {
            return
            [
                new SetValuesOperation(Attr.HomeDrive, [NormalizeDrive(Drive)]),
                new SetValuesOperation(Attr.HomeDirectory, [NetworkPath.Trim()]),
            ];
        }

        var local = LocalPath.Trim();
        return
        [
            new SetValuesOperation(Attr.HomeDrive, []),
            new SetValuesOperation(Attr.HomeDirectory, local.Length == 0 ? [] : [local]),
        ];
    }

    private static string NormalizeDrive(string drive)
    {
        var letter = drive.Trim().TrimEnd(':');
        return letter.Length == 1 ? letter.ToUpperInvariant() + ":" : drive.Trim();
    }
}

/// <summary>User logon name: UPN prefix + suffix chosen from the domain and the forest's additional UPN suffixes.</summary>
public sealed partial class UpnFieldViewModel : PropertyFieldViewModel
{
    private string _originalPrefix = string.Empty;
    private string _originalSuffix = string.Empty;

    internal UpnFieldViewModel(FieldContext context)
        : base(context, Attr.UserPrincipalName, context.Localizer[L.Labels.UserLogonName])
    {
    }

    [ObservableProperty]
    public partial string Prefix { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Suffix { get; set; } = string.Empty;

    /// <summary>"@contoso.test" style suffixes (without the '@').</summary>
    public ObservableCollection<string> Suffixes { get; } = [];

    /// <summary>The complete UPN as it will be written ("" when the prefix is empty).</summary>
    public string FullName => Prefix.Trim().Length == 0 ? string.Empty : $"{Prefix.Trim()}@{Suffix}";

    /// <summary>Copies the UPN as it will be written (prefix@suffix), confirmed with the field's label.</summary>
    [RelayCommand(CanExecute = nameof(HasFullName))]
    private void Copy() =>
        ClipboardCopy.Value(Context.Services.Clipboard, Context.Services.Notifications, Localizer, FullName, Label);

    private bool HasFullName() => FullName.Length > 0;

    partial void OnPrefixChanged(string value)
    {
        OnPropertyChanged(nameof(FullName));
        CopyCommand.NotifyCanExecuteChanged();
        OnEdited();
    }

    partial void OnSuffixChanged(string value)
    {
        OnPropertyChanged(nameof(FullName));
        CopyCommand.NotifyCanExecuteChanged();
        OnEdited();
    }

    protected override void LoadCore(DirectoryObject obj)
    {
        var upn = obj.GetString(Attr.UserPrincipalName) ?? string.Empty;
        var at = upn.LastIndexOf('@');
        _originalPrefix = at < 0 ? upn : upn[..at];
        _originalSuffix = at < 0 ? Context.Session.Info.DomainDnsName : upn[(at + 1)..];
        AddSuffix(Context.Session.Info.DomainDnsName);
        AddSuffix(_originalSuffix);
        RevertCore();
    }

    /// <summary>Reads the forest's additional UPN suffixes (uPNSuffixes of CN=Partitions).</summary>
    internal override async Task LoadDetailsAsync(CancellationToken cancellationToken)
    {
        var suffixes = await ReadUpnSuffixesAsync(Context.Session, cancellationToken).ConfigureAwait(true);
        foreach (var suffix in suffixes)
        {
            AddSuffix(suffix);
        }
    }

    /// <summary>Domain DNS name, root domain DNS name and the forest's uPNSuffixes (best effort, never throws).</summary>
    internal static async Task<IReadOnlyList<string>> ReadUpnSuffixesAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        var result = new List<string> { session.Info.DomainDnsName };
        if (session.Info.RootDomainNamingContext is { } root && Core.Ldap.DistinguishedName.TryParse(root, out var rootDn) && !rootDn.IsRoot)
        {
            result.Add(rootDn.DnsDomain);
        }

        try
        {
            var partitions = $"CN=Partitions,{session.Info.ConfigurationNamingContext}";
            var obj = await Task.Run(() => session.GetAsync(partitions, [UpnSuffixesAttribute], cancellationToken), cancellationToken).ConfigureAwait(true);
            if (obj is not null)
            {
                result.AddRange(obj.GetStrings(UpnSuffixesAttribute));
            }
        }
        catch (Exception ex) when (ex is DirectoryException or OperationCanceledException)
        {
            // Additional suffixes are optional.
        }

        return [.. result.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    internal const string UpnSuffixesAttribute = "uPNSuffixes";

    protected override void RevertCore()
    {
        Prefix = _originalPrefix;
        Suffix = _originalSuffix;
    }

    protected override bool ComputeIsDirty() =>
        !string.Equals(Prefix, _originalPrefix, StringComparison.Ordinal) || !string.Equals(Suffix, _originalSuffix, StringComparison.OrdinalIgnoreCase);

    protected override string? Validate()
    {
        var prefix = Prefix.Trim();
        if (prefix.Length == 0)
        {
            return null;
        }

        if (!AccountNames.IsValidUpnPrefix(prefix) || string.IsNullOrWhiteSpace(Suffix))
        {
            return Localizer[L.Fields.InvalidUpn];
        }

        return Context.TryParse(Attribute, FullName, out _, out var error) ? null : error;
    }

    protected override IEnumerable<BatchOperation> CreateOperations() =>
        [new SetValuesOperation(Attribute, FullName.Length == 0 ? [] : [FullName])];

    private void AddSuffix(string suffix)
    {
        if (!string.IsNullOrWhiteSpace(suffix) && !Suffixes.Contains(suffix, StringComparer.OrdinalIgnoreCase))
        {
            Suffixes.Add(suffix);
        }
    }
}

/// <summary>"Log On To..." (userWorkstations): all computers, or a list of NetBIOS computer names.</summary>
public sealed partial class WorkstationsFieldViewModel : PropertyFieldViewModel
{
    private IReadOnlyList<string> _original = [];

    internal WorkstationsFieldViewModel(FieldContext context)
        : base(context, Attr.UserWorkstations, context.Localizer[L.Labels.LogOnTo])
    {
        Computers.CollectionChanged += (_, _) => OnEdited();
    }

    /// <summary>True = the user can log on to all computers (attribute cleared).</summary>
    [ObservableProperty]
    public partial bool AllComputers { get; set; } = true;

    public ObservableCollection<string> Computers { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string NewComputer { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    public partial string? SelectedComputer { get; set; }

    [ObservableProperty]
    public partial string? NewComputerError { get; private set; }

    partial void OnAllComputersChanged(bool value) => OnEdited();

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        var name = NewComputer.Trim().TrimEnd('$');
        if (name.Length is 0 or > Infrastructure.AccountNames.MaxComputerNameLength || name.Any(c => c == ',' || Infrastructure.AccountNames.IsInvalidSamCharacter(c) || char.IsWhiteSpace(c)))
        {
            NewComputerError = Localizer[L.Fields.InvalidComputerName];
            return;
        }

        NewComputerError = null;
        if (!Computers.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            Computers.Add(name);
        }

        NewComputer = string.Empty;
    }

    private bool CanAdd() => IsEditable && !string.IsNullOrWhiteSpace(NewComputer);

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove()
    {
        if (SelectedComputer is { } selected)
        {
            Computers.Remove(selected);
        }
    }

    private bool CanRemove() => IsEditable && SelectedComputer is not null;

    protected override void OnAccessChanged()
    {
        AddCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
    }

    protected override void LoadCore(DirectoryObject obj)
    {
        _original = (obj.GetString(Attr.UserWorkstations) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        RevertCore();
    }

    protected override void RevertCore()
    {
        Computers.Clear();
        foreach (var computer in _original)
        {
            Computers.Add(computer);
        }

        AllComputers = _original.Count == 0;
    }

    protected override bool ComputeIsDirty() =>
        !Current().SequenceEqual(_original, StringComparer.OrdinalIgnoreCase);

    protected override string? Validate() => !AllComputers && Computers.Count == 0 ? Localizer[L.Fields.ComputerRequired] : null;

    protected override IEnumerable<BatchOperation> CreateOperations()
    {
        var current = Current();
        return [new SetValuesOperation(Attribute, current.Count == 0 ? [] : [string.Join(',', current)])];
    }

    private List<string> Current() => AllComputers ? [] : [.. Computers];
}

/// <summary>A group scope offered on the group's General page (disabled when ADUC does not allow the conversion).</summary>
public sealed record GroupScopeOption(GroupScope Scope, string Text, bool IsEnabled);

/// <summary>
/// Group scope and type (groupType) with ADUC's conversion rules: Global and Domain local groups can become
/// Universal, Universal groups can become either; Global and Domain local cannot be converted into each other
/// directly. Builtin local groups cannot change.
/// </summary>
public sealed partial class GroupTypeFieldViewModel : PropertyFieldViewModel
{
    private GroupScope _originalScope = GroupScope.Global;
    private bool _originalSecurity = true;

    internal GroupTypeFieldViewModel(FieldContext context)
        : base(context, Attr.GroupType, context.Label(Attr.GroupType))
    {
    }

    public IReadOnlyList<GroupScopeOption> ScopeOptions { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGlobal), nameof(IsDomainLocal), nameof(IsUniversal))]
    public partial GroupScope Scope { get; set; } = GroupScope.Global;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDistribution))]
    public partial bool IsSecurity { get; set; } = true;

    /// <summary>Radio-button helpers.</summary>
    public bool IsGlobal
    {
        get => Scope == GroupScope.Global;
        set
        {
            if (value)
            {
                Scope = GroupScope.Global;
            }
        }
    }

    public bool IsDomainLocal
    {
        get => Scope == GroupScope.DomainLocal;
        set
        {
            if (value)
            {
                Scope = GroupScope.DomainLocal;
            }
        }
    }

    public bool IsUniversal
    {
        get => Scope == GroupScope.Universal;
        set
        {
            if (value)
            {
                Scope = GroupScope.Universal;
            }
        }
    }

    public bool IsDistribution
    {
        get => !IsSecurity;
        set => IsSecurity = !value;
    }

    /// <summary>Whether the original scope may be converted to <paramref name="scope"/> (ADUC rules).</summary>
    public static bool CanConvert(GroupScope from, GroupScope scope) => from == scope || (from, scope) switch
    {
        (GroupScope.Global, GroupScope.Universal) => true,
        (GroupScope.DomainLocal, GroupScope.Universal) => true,
        (GroupScope.Universal, GroupScope.Global or GroupScope.DomainLocal) => true,
        _ => false,
    };

    partial void OnScopeChanged(GroupScope value) => OnEdited();

    partial void OnIsSecurityChanged(bool value) => OnEdited();

    protected override void LoadCore(DirectoryObject obj)
    {
        var raw = obj.GetInt32(Attr.GroupType) ?? GroupTypes.Compose(GroupScope.Global, security: true);
        _originalScope = GroupTypes.GetScope(raw);
        _originalSecurity = GroupTypes.IsSecurity(raw);
        ScopeOptions =
        [
            .. new[] { GroupScope.DomainLocal, GroupScope.Global, GroupScope.Universal }
                .Select(s => new GroupScopeOption(s, Localizer.GroupScopeName(s), _originalScope != GroupScope.BuiltinLocal && CanConvert(_originalScope, s))),
        ];
        OnPropertyChanged(nameof(ScopeOptions));
        RevertCore();
    }

    protected override void RevertCore()
    {
        Scope = _originalScope;
        IsSecurity = _originalSecurity;
    }

    protected override bool ComputeIsDirty() => Scope != _originalScope || IsSecurity != _originalSecurity;

    protected override string? Validate() =>
        CanConvert(_originalScope, Scope) ? null : Localizer[L.Fields.InvalidScopeChange];

    protected override IEnumerable<BatchOperation> CreateOperations() =>
        Scope is GroupScope.Unknown
            ? []
            : [new SetValuesOperation(Attribute, [GroupTypes.Compose(Scope, IsSecurity).ToString(CultureInfo.InvariantCulture)])];
}

/// <summary>Kerberos AES options of the account (msDS-SupportedEncryptionTypes bits 0x8 and 0x10).</summary>
public sealed partial class EncryptionTypesFieldViewModel : PropertyFieldViewModel
{
    public const int Aes128Bit = 0x8;
    public const int Aes256Bit = 0x10;

    private int _original;

    internal EncryptionTypesFieldViewModel(FieldContext context)
        : base(context, Attr.SupportedEncryptionTypes, context.Localizer[L.Labels.KerberosAes])
    {
    }

    [ObservableProperty]
    public partial bool Aes128 { get; set; }

    [ObservableProperty]
    public partial bool Aes256 { get; set; }

    public string Aes128Label => Localizer[L.Labels.Aes128];

    public string Aes256Label => Localizer[L.Labels.Aes256];

    partial void OnAes128Changed(bool value) => OnEdited();

    partial void OnAes256Changed(bool value) => OnEdited();

    protected override void LoadCore(DirectoryObject obj)
    {
        _original = obj.GetInt32(Attribute) ?? 0;
        RevertCore();
    }

    protected override void RevertCore()
    {
        Aes128 = (_original & Aes128Bit) != 0;
        Aes256 = (_original & Aes256Bit) != 0;
    }

    protected override bool ComputeIsDirty() => Compute() != _original;

    protected override IEnumerable<BatchOperation> CreateOperations()
    {
        var value = Compute();
        return [new SetValuesOperation(Attribute, value == 0 ? [] : [value.ToString(CultureInfo.InvariantCulture)])];
    }

    private int Compute()
    {
        var value = _original & ~(Aes128Bit | Aes256Bit);
        if (Aes128)
        {
            value |= Aes128Bit;
        }

        if (Aes256)
        {
            value |= Aes256Bit;
        }

        return value;
    }
}
