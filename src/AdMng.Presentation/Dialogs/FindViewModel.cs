using System.Collections.ObjectModel;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Dialogs;

/// <summary>What the Find dialog searches for (ADUC "Find:" list).</summary>
public enum FindKind
{
    UsersContactsGroups,
    Users,
    Groups,
    Contacts,
    Computers,
    OrganizationalUnits,

    /// <summary>Raw LDAP filter typed by the operator.</summary>
    Custom,
}

/// <summary>Comparison of an advanced Find condition.</summary>
public enum FindOperator
{
    StartsWith,
    EndsWith,
    Contains,
    Is,
    IsNot,
    Present,
    NotPresent,
}

/// <summary>One "Field / Condition / Value" row of the Find dialog's advanced tab.</summary>
public sealed partial class FindConditionViewModel : ObservableObject
{
    internal FindConditionViewModel(IReadOnlyList<Option<string>> fields, IReadOnlyList<Option<FindOperator>> operators)
    {
        Fields = fields;
        Operators = operators;
        Field = fields.FirstOrDefault();
        Operator = operators[0];
    }

    /// <summary>Raised when the condition changed (the dialog regenerates the filter).</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<Option<string>> Fields { get; }

    public IReadOnlyList<Option<FindOperator>> Operators { get; }

    [ObservableProperty]
    public partial Option<string>? Field { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsValue))]
    public partial Option<FindOperator> Operator { get; set; }

    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    /// <summary>False for "present" / "not present".</summary>
    public bool NeedsValue => Operator.Value is not (FindOperator.Present or FindOperator.NotPresent);

    /// <summary>The LDAP filter of the condition (escaped), or null when incomplete.</summary>
    public string? ToFilter()
    {
        if (Field?.Value is not { Length: > 0 } attribute)
        {
            return null;
        }

        var value = Value.Trim();
        if (NeedsValue && value.Length == 0)
        {
            return null;
        }

        return Operator.Value switch
        {
            FindOperator.StartsWith => LdapFilter.StartsWith(attribute, value),
            FindOperator.EndsWith => LdapFilter.EndsWith(attribute, value),
            FindOperator.Contains => LdapFilter.Contains(attribute, value),
            FindOperator.Is => LdapFilter.Equal(attribute, value),
            FindOperator.IsNot => LdapFilter.Not(LdapFilter.Equal(attribute, value)),
            FindOperator.Present => LdapFilter.Present(attribute),
            _ => LdapFilter.NotPresent(attribute),
        };
    }

    partial void OnFieldChanged(Option<string>? value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnOperatorChanged(Option<FindOperator> value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnValueChanged(string value) => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// ADUC "Find Users, Contacts, and Groups" (modeless): object kind, scope (a container or the entire domain), name
/// (ambiguous name resolution) and advanced field/condition/value rows. The generated LDAP filter is shown and can
/// be edited as raw LDAP (validated). Results are an <see cref="ObjectListViewModel"/> (sorting, columns, streaming).
/// </summary>
public sealed partial class FindViewModel : WindowViewModelBase
{
    private static readonly string[] CommonFields =
    [
        Attr.Name, Attr.DisplayName, Attr.GivenName, Attr.Sn, Attr.SamAccountName, Attr.UserPrincipalName, Attr.Mail,
        Attr.Description, Attr.Title, Attr.Department, Attr.Company, Attr.Office, Attr.City, Attr.TelephoneNumber,
        Attr.EmployeeId, Attr.Manager, Attr.DnsHostName, Attr.OperatingSystem, Attr.Location,
    ];

    private bool _settingFilter;

    public FindViewModel(DialogServices services, string? startDn)
        : base(services)
    {
        Kinds = [.. Enum.GetValues<FindKind>().Select(k => new Option<FindKind>(k, Localizer[DisplayNames.FindKindKey(k)]))];
        Kind = Kinds[0];
        Operators = [.. Enum.GetValues<FindOperator>().Select(o => new Option<FindOperator>(o, Localizer[DisplayNames.FindOperatorKey(o)]))];
        var schema = services.Sessions.Current?.Schema;
        Fields = [.. CommonFields.Select(a => new Option<string>(a, Localizer.AttributeName(a, schema)))];
        SearchBaseDn = startDn;
        Results = services.Lists.CreateList();
        Conditions.CollectionChanged += (_, _) => RegenerateFilter();
        RegenerateFilter();
    }

    public override string WindowTitle => Localizer.Format(L.Find.WindowTitle, Kind.Text);

    public IReadOnlyList<Option<FindKind>> Kinds { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(IsCustom))]
    public partial Option<FindKind> Kind { get; set; }

    public bool IsCustom => Kind.Value == FindKind.Custom;

    /// <summary>Where to search: a container DN, or null for the entire domain.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchBaseText))]
    public partial string? SearchBaseDn { get; set; }

    public string SearchBaseText => SearchBaseDn is null ? Localizer[L.Csv.EntireDomain] : AddressResolver.ToDisplayPath(SearchBaseDn);

    /// <summary>Include sub-containers (always true for the entire domain).</summary>
    [ObservableProperty]
    public partial bool IncludeSubcontainers { get; set; } = true;

    /// <summary>Name (ambiguous name resolution: name, display name, first/last name, logon name, e-mail).</summary>
    [ObservableProperty]
    public partial string NameText { get; set; } = string.Empty;

    public ObservableCollection<FindConditionViewModel> Conditions { get; } = [];

    public IReadOnlyList<Option<string>> Fields { get; }

    public IReadOnlyList<Option<FindOperator>> Operators { get; }

    /// <summary>The LDAP filter that will be used (generated, or typed when <see cref="IsRawFilter"/>).</summary>
    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    /// <summary>The operator edited the filter by hand; kind, name and conditions are ignored until reset.</summary>
    [ObservableProperty]
    public partial bool IsRawFilter { get; private set; }

    /// <summary>Localized syntax error of <see cref="FilterText"/>.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindNowCommand))]
    public partial string? FilterError { get; private set; }

    /// <summary>Results (an object list: sort, columns, export through the shell's list features).</summary>
    public ObjectListViewModel Results { get; }

    /// <summary>A search ran (the results pane is shown).</summary>
    [ObservableProperty]
    public partial bool HasSearched { get; private set; }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var session = Services.Sessions.Current;
        if (session is null)
        {
            return;
        }

        var picked = await Services.Dialogs.PickContainerAsync(session, SearchBaseDn).ConfigureAwait(true);
        if (picked is not null)
        {
            SearchBaseDn = picked;
        }
    }

    [RelayCommand]
    private void SearchEntireDomain() => SearchBaseDn = null;

    [RelayCommand]
    private void AddCondition()
    {
        var condition = new FindConditionViewModel(Fields, Operators);
        condition.Changed += OnConditionChanged;
        Conditions.Add(condition);
    }

    [RelayCommand]
    private void RemoveCondition(FindConditionViewModel? condition)
    {
        if (condition is not null && Conditions.Remove(condition))
        {
            condition.Changed -= OnConditionChanged;
        }
    }

    /// <summary>Throws away the hand-edited filter and regenerates it from kind, name and conditions.</summary>
    [RelayCommand]
    private void ResetFilter()
    {
        IsRawFilter = false;
        RegenerateFilter();
    }

    [RelayCommand(CanExecute = nameof(CanFindNow))]
    private async Task FindNowAsync()
    {
        var session = Services.Sessions.Current;
        if (session is null || FilterError is not null)
        {
            return;
        }

        var baseDn = SearchBaseDn ?? session.Info.DefaultNamingContext;
        var scope = SearchBaseDn is null || IncludeSubcontainers ? SearchScope.Subtree : SearchScope.OneLevel;
        var location = ListLocation.ForQuery(Localizer[L.Find.ResultsTitle], baseDn, scope, LdapFilter.Wrap(FilterText));
        HasSearched = true;
        await Results.NavigateAsync(location).ConfigureAwait(true);
    }

    private bool CanFindNow() => FilterError is null && FilterText.Trim().Length > 0;

    [RelayCommand]
    private void Stop() => Results.CancelLoad();

    /// <summary>ADUC "Clear All".</summary>
    [RelayCommand]
    private void ClearAll()
    {
        Results.CancelLoad();
        NameText = string.Empty;
        foreach (var condition in Conditions)
        {
            condition.Changed -= OnConditionChanged;
        }

        Conditions.Clear();
        IsRawFilter = false;
        Kind = Kinds[0];
        RegenerateFilter();
    }

    /// <summary>Opens the properties of the selected result (double click / Enter).</summary>
    [RelayCommand]
    private Task OpenAsync(ObjectRowViewModel? row)
    {
        row ??= Results.SelectedItem;
        return row is null ? Task.CompletedTask : Services.Dialogs.ShowPropertiesAsync(row.Dn);
    }

    partial void OnKindChanged(Option<FindKind> oldValue, Option<FindKind> newValue)
    {
        if (newValue.Value == FindKind.Custom)
        {
            IsRawFilter = true;
        }
        else if (oldValue is { Value: FindKind.Custom })
        {
            // Leaving "Custom search" for a predefined kind: the filter follows kind, name and conditions again.
            IsRawFilter = false;
        }

        RegenerateFilter();
    }

    partial void OnNameTextChanged(string value) => RegenerateFilter();

    partial void OnFilterTextChanged(string value)
    {
        if (!_settingFilter)
        {
            IsRawFilter = true;
        }

        FilterError = value.Trim().Length == 0 || LdapFilter.Validate(LdapFilter.Wrap(value.Trim())) is null ? null : Localizer[L.Views.InvalidFilter];
        FindNowCommand.NotifyCanExecuteChanged();
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(SearchBaseText));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var condition in Conditions)
            {
                condition.Changed -= OnConditionChanged;
            }

            Results.Dispose();
        }
    }

    private void OnConditionChanged(object? sender, EventArgs e) => RegenerateFilter();

    /// <summary>Builds the filter from kind, name and conditions (all escaped through <see cref="LdapFilter"/>).</summary>
    private void RegenerateFilter()
    {
        if (IsRawFilter)
        {
            return;
        }

        var parts = new List<string?> { KindFilter(Kind.Value) };
        var name = NameText.Trim();
        if (name.Length > 0)
        {
            parts.Add(LdapFilter.Anr(name));
        }

        parts.AddRange(Conditions.Select(c => c.ToFilter()));
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        var filter = present.Count == 0 ? ListLocation.AllObjectsFilter : LdapFilter.And(present);
        _settingFilter = true;
        try
        {
            FilterText = filter;
        }
        finally
        {
            _settingFilter = false;
        }
    }

    private static string? KindFilter(FindKind kind) => kind switch
    {
        FindKind.UsersContactsGroups => LdapFilter.Or(ObjectKind.User.ToLdapFilter(), ObjectKind.Contact.ToLdapFilter(), ObjectKind.Group.ToLdapFilter()),
        FindKind.Users => ObjectKind.User.ToLdapFilter(),
        FindKind.Groups => ObjectKind.Group.ToLdapFilter(),
        FindKind.Contacts => ObjectKind.Contact.ToLdapFilter(),
        FindKind.Computers => ObjectKind.Computer.ToLdapFilter(),
        FindKind.OrganizationalUnits => ObjectKind.OrganizationalUnit.ToLdapFilter(),
        _ => null,
    };
}
