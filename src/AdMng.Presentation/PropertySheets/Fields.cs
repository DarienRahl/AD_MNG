using System.Collections.ObjectModel;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.PropertySheets;

/// <summary>
/// Base of all editable values of a property page. A field knows the attribute(s) it writes, whether the operator
/// may change it (schema + effective permissions, with a localized reason), whether it differs from the value read
/// from the DC (<see cref="IsDirty"/>), live validation (<see cref="Error"/>) and the planner's finding after Apply
/// (<see cref="Issue"/>). It turns its pending change into declarative batch operations - the property sheet never
/// writes to the directory itself (CLAUDE.md rule 5).
/// </summary>
public abstract partial class PropertyFieldViewModel : ObservableObject
{
    private bool _loading;

    private protected PropertyFieldViewModel(FieldContext context, string attribute, string label)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Attribute = attribute ?? throw new ArgumentNullException(nameof(attribute));
        Label = label ?? throw new ArgumentNullException(nameof(label));
    }

    /// <summary>Raised after an edit, a load or a revert (the page recomputes HasChanges / HasErrors).</summary>
    public event EventHandler? StateChanged;

    /// <summary>LDAP name of the (main) attribute.</summary>
    public string Attribute { get; }

    /// <summary>Localized label.</summary>
    public string Label { get; }

    /// <summary>The value cannot be changed (system attribute, missing permission or informational field).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    public partial bool IsReadOnly { get; private set; }

    public bool IsEditable => !IsReadOnly;

    /// <summary>Localized reason for <see cref="IsReadOnly"/> (tooltip next to the lock icon); null for informational fields.</summary>
    [ObservableProperty]
    public partial string? ReadOnlyReason { get; private set; }

    /// <summary>Localized live validation error of the pending value (null when valid or unchanged).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    /// <summary>Localized finding of the change planner for this field after Apply (error or warning).</summary>
    [ObservableProperty]
    public partial string? Issue { get; private set; }

    /// <summary><see cref="Issue"/> is an error that blocks Apply.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial bool IsIssueError { get; private set; }

    /// <summary>The value differs from what was read from the domain controller.</summary>
    [ObservableProperty]
    public partial bool IsDirty { get; private set; }

    /// <summary>Validation or planning error: Apply is disabled.</summary>
    public bool HasError => Error is not null || IsIssueError;

    /// <summary>Always read-only (computed or informational values such as the computer role).</summary>
    public bool IsInformational { get; init; }

    internal FieldContext Context { get; }

    /// <summary>All attributes the field writes (planner findings for any of them are shown on the field).</summary>
    internal virtual IReadOnlyList<string> WrittenAttributes => [Attribute];

    /// <summary>Attributes the field needs to read.</summary>
    internal virtual IReadOnlyList<string> ReadAttributes => WrittenAttributes;

    protected ILocalizer Localizer => Context.Localizer;

    /// <summary>Discards the pending change.</summary>
    public void Revert()
    {
        _loading = true;
        try
        {
            RevertCore();
        }
        finally
        {
            _loading = false;
        }

        ClearIssue();
        Refresh();
    }

    /// <summary>Takes the values of a fresh read (the field becomes clean).</summary>
    internal void Load(DirectoryObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        _loading = true;
        try
        {
            LoadCore(obj);
        }
        finally
        {
            _loading = false;
        }

        ClearIssue();
        UpdateAccess();
        Refresh();
    }

    /// <summary>Second, asynchronous part of loading (resolving referenced objects). Never throws for directory errors.</summary>
    internal virtual Task LoadDetailsAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Operations that apply the pending change (empty when unchanged or read-only).</summary>
    internal IEnumerable<BatchOperation> GetOperations() => IsDirty && !IsReadOnly ? CreateOperations() : [];

    internal void SetIssue(string text, bool isError)
    {
        Issue = text;
        IsIssueError = isError;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void ClearIssue()
    {
        Issue = null;
        IsIssueError = false;
    }

    protected abstract void LoadCore(DirectoryObject obj);

    protected abstract void RevertCore();

    protected abstract bool ComputeIsDirty();

    protected abstract IEnumerable<BatchOperation> CreateOperations();

    /// <summary>Localized validation error of the pending value, or null.</summary>
    protected virtual string? Validate() => null;

    /// <summary>Called after <see cref="IsReadOnly"/> was re-evaluated (commands update their CanExecute).</summary>
    protected virtual void OnAccessChanged()
    {
    }

    /// <summary>To be called by derived fields after the operator changed the value.</summary>
    protected void OnEdited()
    {
        if (_loading)
        {
            return;
        }

        ClearIssue();
        Refresh();
    }

    private void Refresh()
    {
        if (_loading)
        {
            return;
        }

        IsDirty = ComputeIsDirty();
        Error = IsReadOnly || !IsDirty ? null : Validate();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateAccess()
    {
        if (IsInformational)
        {
            IsReadOnly = true;
            ReadOnlyReason = null;
        }
        else
        {
            var reason = WrittenAttributes.Select(Context.ReadOnlyReason).FirstOrDefault(r => r is not null);
            IsReadOnly = reason is not null;
            ReadOnlyReason = reason;
        }

        OnAccessChanged();
    }
}

/// <summary>A single-valued text value (ADUC text box), optionally with an "Other..." list of additional values.</summary>
public sealed partial class FieldViewModel : PropertyFieldViewModel
{
    private readonly Func<DirectoryObject, string>? _reader;

    internal FieldViewModel(FieldContext context, string attribute, string? label = null, Func<DirectoryObject, string>? reader = null)
        : base(context, attribute, label ?? context.Label(attribute))
    {
        _reader = reader;
        IsInformational = reader is not null;
        MaxLength = context.MaxLength(attribute);
    }

    /// <summary>Pending value (two-way).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QualifiedValue))]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand), nameof(CopyQualifiedCommand))]
    public partial string Value { get; set; } = string.Empty;

    /// <summary>Value as read from the domain controller.</summary>
    public string OriginalValue { get; private set; } = string.Empty;

    /// <summary>Maximum length from the schema (rangeUpper), 0 = unlimited.</summary>
    public int MaxLength { get; }

    /// <summary>Multi-line text (Notes).</summary>
    public bool IsMultiline { get; init; }

    /// <summary>Show a copy button (Object page, e-mail, phones, logon name, title...).</summary>
    public bool CanCopy { get; init; }

    /// <summary>
    /// "CONTOSO\" in front of a pre-Windows 2000 logon name (like ADUC's Account tab): a second copy button puts
    /// <see cref="QualifiedValue"/> on the clipboard. Null when the field has none (or the NetBIOS name is unknown).
    /// </summary>
    public string? QualifiedPrefix { get; init; }

    /// <summary>NETBIOS\value, or null without a prefix or a value.</summary>
    public string? QualifiedValue => QualifiedPrefix is null || string.IsNullOrWhiteSpace(Value) ? null : QualifiedPrefix + Value.Trim();

    /// <summary>"Other..." values (otherTelephone, url...) edited in a list; null when the field has none.</summary>
    public MultiValueFieldViewModel? Others { get; init; }

    internal override IReadOnlyList<string> ReadAttributes => [Attribute];

    /// <summary>Copies the value shown (also a pending, not yet applied one), confirmed with the field's label.</summary>
    [RelayCommand(CanExecute = nameof(HasValue))]
    private void Copy()
    {
        if (string.IsNullOrEmpty(Value))
        {
            return;
        }

        if (SecretAttributes.IsSecret(Attribute, Context.Schema))
        {
            // Never the case on the standard pages; a secret would only go the protected way (no history, cleared).
            ClipboardCopy.Secret(Context.Services.Clipboard, Context.Services.Notifications, Value, Localizer.Format(L.Copy.CopiedValue, Label));
            return;
        }

        ClipboardCopy.Value(Context.Services.Clipboard, Context.Services.Notifications, Localizer, Value, Label);
    }

    private bool HasValue() => !string.IsNullOrEmpty(Value);

    /// <summary>Copies NETBIOS\login (<see cref="QualifiedValue"/>).</summary>
    [RelayCommand(CanExecute = nameof(HasQualifiedValue))]
    private void CopyQualified() =>
        ClipboardCopy.Value(Context.Services.Clipboard, Context.Services.Notifications, Localizer, QualifiedValue, Localizer[L.Copy.DomainLogin]);

    private bool HasQualifiedValue() => QualifiedValue is not null;

    partial void OnValueChanged(string value) => OnEdited();

    protected override void LoadCore(DirectoryObject obj)
    {
        OriginalValue = _reader?.Invoke(obj) ?? Read(obj);
        Value = OriginalValue;
    }

    protected override void RevertCore() => Value = OriginalValue;

    protected override bool ComputeIsDirty() => !string.Equals(Value ?? string.Empty, OriginalValue, StringComparison.Ordinal);

    protected override string? Validate()
    {
        var text = (Value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return Context.Mandatory.Contains(Attribute) ? Localizer[L.Fields.Required] : null;
        }

        return Context.TryParse(Attribute, text, out _, out var error) ? null : error;
    }

    protected override IEnumerable<BatchOperation> CreateOperations()
    {
        var text = (Value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return [new SetValuesOperation(Attribute, [])];
        }

        return Context.TryParse(Attribute, text, out var wire, out _) ? [new SetValuesOperation(Attribute, [wire])] : [];
    }

    private string Read(DirectoryObject obj)
    {
        if (!obj.TryGet(Attribute, out var values) || values.Count == 0)
        {
            return string.Empty;
        }

        return Context.IsTextAttribute(Attribute) && !Context.Schema.IsBinary(Attribute)
            ? values.FirstString ?? string.Empty
            : Context.Formatter.Format(Attribute, values, Context.Culture);
    }
}

/// <summary>A multi-valued text attribute edited as a list (ADUC "Other..." dialogs, url, otherTelephone...).</summary>
public sealed partial class MultiValueFieldViewModel : PropertyFieldViewModel
{
    internal MultiValueFieldViewModel(FieldContext context, string attribute, string? label = null)
        : base(context, attribute, label ?? context.Label(attribute))
    {
        Values.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Summary));
            OnEdited();
        };
    }

    /// <summary>Pending values in display order.</summary>
    public ObservableCollection<string> Values { get; } = [];

    public IReadOnlyList<string> OriginalValues { get; private set; } = [];

    /// <summary>Values joined with "; " for a one-line display.</summary>
    public string Summary => string.Join("; ", Values);

    /// <summary>Text of the value being added.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string NewValue { get; set; } = string.Empty;

    /// <summary>Localized error of <see cref="NewValue"/> after Add was attempted.</summary>
    [ObservableProperty]
    public partial string? NewValueError { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(EditCommand))]
    public partial string? SelectedValue { get; set; }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        var text = NewValue.Trim();
        if (!Context.TryParse(Attribute, text, out var wire, out var error))
        {
            NewValueError = error;
            return;
        }

        if (Values.Contains(wire, StringComparer.OrdinalIgnoreCase))
        {
            NewValueError = Localizer[L.Fields.DuplicateValue];
            return;
        }

        NewValueError = null;
        Values.Add(wire);
        NewValue = string.Empty;
    }

    private bool CanAdd() => IsEditable && !string.IsNullOrWhiteSpace(NewValue);

    [RelayCommand(CanExecute = nameof(CanChangeSelected))]
    private void Remove()
    {
        if (SelectedValue is { } selected)
        {
            Values.Remove(selected);
            SelectedValue = null;
        }
    }

    /// <summary>ADUC "Edit": moves the selected value into the input box.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeSelected))]
    private void Edit()
    {
        if (SelectedValue is { } selected)
        {
            NewValue = selected;
            Values.Remove(selected);
            SelectedValue = null;
        }
    }

    private bool CanChangeSelected() => IsEditable && SelectedValue is not null;

    protected override void OnAccessChanged()
    {
        AddCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
    }

    protected override void LoadCore(DirectoryObject obj)
    {
        OriginalValues = obj.GetStrings(Attribute);
        Replace(OriginalValues);
        NewValue = string.Empty;
        NewValueError = null;
    }

    protected override void RevertCore() => Replace(OriginalValues);

    protected override bool ComputeIsDirty() =>
        Values.Count != OriginalValues.Count || !Values.Order(StringComparer.Ordinal).SequenceEqual(OriginalValues.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    protected override string? Validate() =>
        Values.Count == 0 && Context.Mandatory.Contains(Attribute) ? Localizer[L.Fields.Required] : null;

    protected override IEnumerable<BatchOperation> CreateOperations() => [new SetValuesOperation(Attribute, [.. Values])];

    private void Replace(IEnumerable<string> values)
    {
        Values.Clear();
        foreach (var value in values)
        {
            Values.Add(value);
        }
    }
}

/// <summary>A check box (account option, "must change password", "unlock account"...).</summary>
public partial class CheckFieldViewModel : PropertyFieldViewModel
{
    private readonly Func<DirectoryObject, bool> _read;
    private readonly Func<bool, IEnumerable<BatchOperation>> _write;
    private readonly Func<DirectoryObject, bool>? _visible;

    internal CheckFieldViewModel(
        FieldContext context,
        string attribute,
        string label,
        Func<DirectoryObject, bool> read,
        Func<bool, IEnumerable<BatchOperation>> write,
        Func<DirectoryObject, bool>? visible = null)
        : base(context, attribute, label)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _visible = visible;
    }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    public bool OriginalChecked { get; private set; }

    /// <summary>False hides the check box (e.g. "Unlock account" when the account is not locked).</summary>
    [ObservableProperty]
    public partial bool IsVisible { get; private set; } = true;

    partial void OnIsCheckedChanged(bool value) => OnEdited();

    protected override void LoadCore(DirectoryObject obj)
    {
        OriginalChecked = _read(obj);
        IsChecked = OriginalChecked;
        IsVisible = _visible?.Invoke(obj) ?? true;
    }

    protected override void RevertCore() => IsChecked = OriginalChecked;

    protected override bool ComputeIsDirty() => IsChecked != OriginalChecked;

    protected override IEnumerable<BatchOperation> CreateOperations() => _write(IsChecked);
}

/// <summary>One userAccountControl option (ADUC "Account options" list).</summary>
public sealed class FlagFieldViewModel : CheckFieldViewModel
{
    internal FlagFieldViewModel(FieldContext context, UserAccountControl flag, string? label = null)
        : base(
            context,
            Attr.UserAccountControl,
            label ?? context.Localizer.UacFlagName(flag),
            obj => ((UserAccountControl)(obj.GetInt32(Attr.UserAccountControl) ?? 0)).HasFlag(flag),
            value => flag == UserAccountControl.AccountDisable
                ? [new SetAccountEnabledOperation(!value)]
                : [new SetAccountFlagOperation(flag, value)])
    {
        Flag = flag;
    }

    public UserAccountControl Flag { get; }
}

/// <summary>"Account expires: Never / End of [date]" (accountExpires).</summary>
/// <remarks>
/// Like ADUC, "end of" a day is stored as the following local midnight converted to UTC; a stored value at local
/// midnight is shown as the previous day.
/// </remarks>
public sealed partial class ExpiryFieldViewModel : PropertyFieldViewModel
{
    internal ExpiryFieldViewModel(FieldContext context, string? label = null)
        : base(context, Attr.AccountExpires, label ?? context.Localizer[L.Labels.AccountExpires])
    {
    }

    [ObservableProperty]
    public partial bool IsNever { get; set; } = true;

    /// <summary>Last day the account may be used (local calendar date, time part ignored).</summary>
    [ObservableProperty]
    public partial DateTime? EndOfDate { get; set; }

    public bool OriginalIsNever { get; private set; } = true;

    public DateTime? OriginalEndOfDate { get; private set; }

    private TimeZoneInfo TimeZone => Context.Services.Time.LocalTimeZone;

    /// <summary>The expiry instant written for an "end of" date (next local midnight).</summary>
    internal DateTimeOffset ExpiryFor(DateTime endOfDate)
    {
        var midnight = DateTime.SpecifyKind(endOfDate.Date.AddDays(1), DateTimeKind.Unspecified);
        return new DateTimeOffset(midnight, TimeZone.GetUtcOffset(midnight));
    }

    partial void OnIsNeverChanged(bool value)
    {
        if (!value && EndOfDate is null)
        {
            EndOfDate = TimeZoneInfo.ConvertTime(Context.Services.Time.GetUtcNow(), TimeZone).Date;
        }

        OnEdited();
    }

    partial void OnEndOfDateChanged(DateTime? value) => OnEdited();

    protected override void LoadCore(DirectoryObject obj)
    {
        var expires = obj.GetFileTime(Attr.AccountExpires);
        if (expires is null)
        {
            OriginalIsNever = true;
            OriginalEndOfDate = null;
        }
        else
        {
            var local = TimeZoneInfo.ConvertTime(expires.Value, TimeZone);
            OriginalIsNever = false;
            OriginalEndOfDate = local.TimeOfDay == TimeSpan.Zero ? local.Date.AddDays(-1) : local.Date;
        }

        EndOfDate = OriginalEndOfDate;
        IsNever = OriginalIsNever;
    }

    protected override void RevertCore()
    {
        EndOfDate = OriginalEndOfDate;
        IsNever = OriginalIsNever;
    }

    protected override bool ComputeIsDirty() =>
        IsNever != OriginalIsNever || (!IsNever && EndOfDate?.Date != OriginalEndOfDate?.Date);

    protected override string? Validate() => !IsNever && EndOfDate is null ? Localizer[L.Fields.DateRequired] : null;

    protected override IEnumerable<BatchOperation> CreateOperations() =>
        IsNever ? [new SetAccountExpirationOperation(null)]
        : EndOfDate is { } date ? [new SetAccountExpirationOperation(ExpiryFor(date))]
        : [];
}

/// <summary>
/// A reference to another object (manager, managedBy) with a picker, the resolved name and - for Managed By -
/// the manager's office and phone numbers.
/// </summary>
public sealed partial class DnFieldViewModel : PropertyFieldViewModel
{
    private static readonly string[] DetailAttributes =
    [
        Attr.Office, Attr.StreetAddress, Attr.City, Attr.State, Attr.CountryName, Attr.TelephoneNumber,
        Attr.FacsimileTelephoneNumber,
    ];

    private readonly IReadOnlyList<ObjectKind> _kinds;

    internal DnFieldViewModel(FieldContext context, string attribute, IReadOnlyList<ObjectKind> kinds, string? label = null, bool showDetails = false)
        : base(context, attribute, label ?? context.Label(attribute))
    {
        _kinds = kinds;
        ShowsDetails = showDetails;
    }

    /// <summary>DN of the referenced object (null = not set).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearCommand), nameof(OpenCommand), nameof(CopyCommand))]
    [NotifyPropertyChangedFor(nameof(HasValue))]
    public partial string? Value { get; private set; }

    public string? OriginalValue { get; private set; }

    public bool HasValue => Value is not null;

    /// <summary>Name of the referenced object (displayName or RDN).</summary>
    [ObservableProperty]
    public partial string DisplayName { get; private set; } = string.Empty;

    /// <summary>Folder of the referenced object.</summary>
    [ObservableProperty]
    public partial string Path { get; private set; } = string.Empty;

    /// <summary>Office, address and phone numbers of the referenced object (Managed By page).</summary>
    public ObservableCollection<SummaryField> Details { get; } = [];

    public bool ShowsDetails { get; }

    /// <summary>Object kinds offered by the picker.</summary>
    public IReadOnlyList<ObjectKind> Kinds => _kinds;

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private async Task BrowseAsync()
    {
        var picked = await Context.Services.Dialogs.PickObjectsAsync(Context.Session, _kinds, multiSelect: false).ConfigureAwait(true);
        if (picked.Count == 0)
        {
            return;
        }

        Value = picked[0];
        DisplayName = RdnName(Value);
        OnEdited();
        await ResolveAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanClear))]
    private void Clear()
    {
        Value = null;
        DisplayName = string.Empty;
        Path = string.Empty;
        Details.Clear();
        OnEdited();
    }

    private bool CanClear() => IsEditable && Value is not null;

    [RelayCommand(CanExecute = nameof(HasValue))]
    private Task OpenAsync() => Value is { } dn ? Context.Services.Dialogs.ShowPropertiesAsync(dn) : Task.CompletedTask;

    /// <summary>Copies the referenced object's name (the manager's name goes into tickets and e-mails, not the DN).</summary>
    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void Copy() =>
        ClipboardCopy.Value(Context.Services.Clipboard, Context.Services.Notifications, Localizer, DisplayName, Label);

    private bool CanCopy() => Value is not null && !string.IsNullOrWhiteSpace(DisplayName);

    partial void OnDisplayNameChanged(string value) => CopyCommand.NotifyCanExecuteChanged();

    protected override void OnAccessChanged()
    {
        BrowseCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
    }

    protected override void LoadCore(DirectoryObject obj)
    {
        OriginalValue = obj.GetString(Attribute);
        Value = OriginalValue;
        DisplayName = RdnName(Value);
        Path = string.Empty;
        Details.Clear();
    }

    internal override Task LoadDetailsAsync(CancellationToken cancellationToken) => ResolveAsync(cancellationToken);

    protected override void RevertCore()
    {
        Value = OriginalValue;
        DisplayName = RdnName(Value);
        ResolveAsync(CancellationToken.None).Forget();
    }

    protected override bool ComputeIsDirty() =>
        Value is null ? OriginalValue is not null : OriginalValue is null || !DistinguishedName.AreEqual(Value, OriginalValue);

    protected override IEnumerable<BatchOperation> CreateOperations() =>
        [new SetValuesOperation(Attribute, Value is null ? [] : [Value])];

    private static string RdnName(string? dn) =>
        dn is not null && DistinguishedName.TryParse(dn, out var parsed) && !parsed.IsRoot ? parsed.Name : dn ?? string.Empty;

    private async Task ResolveAsync(CancellationToken cancellationToken)
    {
        var dn = Value;
        Details.Clear();
        if (dn is null)
        {
            Path = string.Empty;
            return;
        }

        var item = DirectoryItem.Unresolved(dn, Localizer);
        Path = item.Path;
        try
        {
            string[] attributes = ShowsDetails ? [.. DirectoryItem.Attributes, .. DetailAttributes] : [.. DirectoryItem.Attributes];
            var session = Context.Session;
            var obj = await Task.Run(() => session.GetAsync(dn, attributes, cancellationToken), cancellationToken).ConfigureAwait(true);
            if (!string.Equals(dn, Value, StringComparison.Ordinal))
            {
                return;
            }

            if (obj is null)
            {
                DisplayName = $"{item.Name} {Localizer[L.Fields.NotFound]}";
                return;
            }

            DisplayName = DirectoryItem.From(obj, Localizer).Name;
            if (ShowsDetails)
            {
                foreach (var attribute in DetailAttributes)
                {
                    Details.Add(new SummaryField(Context.Label(attribute), obj.GetString(attribute) ?? string.Empty));
                }
            }
        }
        catch (Exception ex) when (ex is Core.Directory.DirectoryException or OperationCanceledException)
        {
            // The name stays the RDN value; the reference itself is still shown and editable.
        }
    }
}

/// <summary>A read-only list of referenced objects (direct reports).</summary>
public sealed partial class ReferenceListFieldViewModel : PropertyFieldViewModel
{
    private IReadOnlyList<string> _dns = [];

    internal ReferenceListFieldViewModel(FieldContext context, string attribute, string? label = null)
        : base(context, attribute, label ?? context.Label(attribute))
    {
        IsInformational = true;
    }

    public BulkObservableCollection<DirectoryItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    public partial DirectoryItem? SelectedItem { get; set; }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private Task OpenAsync(DirectoryItem? item)
    {
        var target = item ?? SelectedItem;
        return target is null ? Task.CompletedTask : Context.Services.Dialogs.ShowPropertiesAsync(target.Dn);
    }

    private bool CanOpen(DirectoryItem? item) => (item ?? SelectedItem) is not null;

    protected override void LoadCore(DirectoryObject obj)
    {
        _dns = obj.GetStrings(Attribute);
        Items.ReplaceAll(_dns.Select(dn => DirectoryItem.Unresolved(dn, Localizer)));
    }

    internal override async Task LoadDetailsAsync(CancellationToken cancellationToken)
    {
        if (_dns.Count == 0)
        {
            return;
        }

        var dns = _dns;
        try
        {
            var session = Context.Session;
            var found = await Task.Run(() => DirectoryLookup.ReadManyAsync(session, dns, DirectoryItem.Attributes, cancellationToken), cancellationToken)
                .ConfigureAwait(true);
            if (!ReferenceEquals(dns, _dns))
            {
                return;
            }

            var comparer = StringComparer.Create(Context.Culture, ignoreCase: true);
            Items.ReplaceAll(dns
                .Select(dn => found.TryGetValue(DnKey(dn), out var obj) ? DirectoryItem.From(obj, Localizer) : DirectoryItem.Unresolved(dn, Localizer))
                .OrderBy(i => i.Name, comparer));
        }
        catch (Exception ex) when (ex is Core.Directory.DirectoryException or OperationCanceledException)
        {
            // Names stay unresolved (RDN values).
        }
    }

    protected override void RevertCore()
    {
    }

    protected override bool ComputeIsDirty() => false;

    protected override IEnumerable<BatchOperation> CreateOperations() => [];

    private static string DnKey(string dn) => Lists.DnKey.For(dn);
}
