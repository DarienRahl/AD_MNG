using System.Collections.ObjectModel;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.PropertySheets;

/// <summary>How a value of an attribute is edited in the Attribute Editor (the WPF editor template switches on it).</summary>
public enum AttributeEditorKind
{
    /// <summary>One string value.</summary>
    Text,

    /// <summary>A list of string values.</summary>
    MultiText,

    /// <summary>32-bit integer (hexadecimal input "0x..." accepted).</summary>
    Integer,

    /// <summary>64-bit integer.</summary>
    LargeInteger,

    /// <summary>FILETIME date (accountExpires, pwdLastSet...): date/time or "never".</summary>
    FileTime,

    /// <summary>GeneralizedTime / UTC time.</summary>
    DateTime,

    /// <summary>TRUE / FALSE / not set.</summary>
    Boolean,

    /// <summary>One distinguished name (with object picker).</summary>
    Dn,

    /// <summary>A list of distinguished names (with object picker).</summary>
    MultiDn,

    /// <summary>Binary data - read-only hexadecimal view.</summary>
    Binary,
}

/// <summary>One attribute of the object in the Attribute Editor list.</summary>
public sealed partial class AttributeRowViewModel : ObservableObject
{
    private readonly FieldContext _context;

    internal AttributeRowViewModel(FieldContext context, string attribute, AttributeValues? values)
    {
        _context = context;
        Attribute = attribute;
        Values = values;
        var definition = context.Schema.FindAttribute(attribute);
        DisplayName = context.Label(attribute);
        IsMultiValued = definition is { IsSingleValued: false };
        IsBinary = context.Schema.IsBinary(attribute);
        SyntaxText = definition is null ? string.Empty : context.Localizer[DisplayNames.SyntaxKey(definition.Syntax)];
        EditorKind = KindOf(attribute, definition, IsBinary, IsMultiValued);
        ReadOnlyReason = IsBinary ? context.Localizer[L.AttributeEditor.BinaryReadOnly] : context.ReadOnlyReason(attribute);
    }

    /// <summary>LDAP display name (what ADUC's Attribute Editor lists).</summary>
    public string Attribute { get; }

    /// <summary>Friendly name (tooltip).</summary>
    public string DisplayName { get; }

    public string SyntaxText { get; }

    public bool IsMultiValued { get; }

    public bool IsBinary { get; }

    public AttributeEditorKind EditorKind { get; }

    /// <summary>Why the attribute cannot be edited (system attribute, permission, binary), or null.</summary>
    public string? ReadOnlyReason { get; }

    public bool IsReadOnly => ReadOnlyReason is not null;

    /// <summary>Values read from the domain controller (null = not set).</summary>
    public AttributeValues? Values { get; }

    public bool HasValue => PendingValues is { } pending ? pending.Count > 0 : Values is { Count: > 0 };

    /// <summary>Staged values (wire form) that replace the attribute on Apply; null = unchanged.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(ValueText), nameof(HasValue))]
    public partial IReadOnlyList<string>? PendingValues { get; internal set; }

    public bool IsDirty => PendingValues is not null;

    /// <summary>Formatted value(s) as ADUC shows them, "&lt;not set&gt;" when empty.</summary>
    public string ValueText
    {
        get
        {
            if (PendingValues is { } pending)
            {
                return pending.Count == 0
                    ? _context.Localizer[L.AttributeEditor.NotSet]
                    : string.Join("; ", pending.Select(v => _context.Formatter.FormatValue(Attribute, v, _context.Culture)));
            }

            return Values is { Count: > 0 }
                ? _context.Formatter.Format(Attribute, Values, _context.Culture)
                : _context.Localizer[L.AttributeEditor.NotSet];
        }
    }

    /// <summary>Values formatted for editing (one string per value).</summary>
    internal IReadOnlyList<string> EditableValues
    {
        get
        {
            if (PendingValues is { } pending)
            {
                return [.. pending.Select(v => ToEditable(v))];
            }

            return Values is null ? [] : [.. Values.Select(ToEditable)];
        }
    }

    private static AttributeEditorKind KindOf(string attribute, AttributeDefinition? definition, bool binary, bool multi)
    {
        if (binary)
        {
            return AttributeEditorKind.Binary;
        }

        if (Attr.FileTimeAttributes.Contains(attribute))
        {
            return AttributeEditorKind.FileTime;
        }

        return definition?.Syntax switch
        {
            AttributeSyntax.Boolean => AttributeEditorKind.Boolean,
            AttributeSyntax.Integer or AttributeSyntax.Enumeration when !multi => AttributeEditorKind.Integer,
            AttributeSyntax.LargeInteger when !multi => AttributeEditorKind.LargeInteger,
            AttributeSyntax.GeneralizedTime or AttributeSyntax.UtcTime when !multi => AttributeEditorKind.DateTime,
            AttributeSyntax.DistinguishedName => multi ? AttributeEditorKind.MultiDn : AttributeEditorKind.Dn,
            _ => multi ? AttributeEditorKind.MultiText : AttributeEditorKind.Text,
        };
    }

    private string ToEditable(object value)
    {
        if (value is byte[] bytes)
        {
            return _context.Formatter.FormatValue(Attribute, bytes, _context.Culture);
        }

        var text = (string)value;
        return EditorKind is AttributeEditorKind.FileTime or AttributeEditorKind.DateTime
            ? _context.Formatter.FormatValue(Attribute, text, _context.Culture)
            : text;
    }
}

/// <summary>
/// The value editor of the Attribute Editor (ADUC "String Attribute Editor", "Multi-valued String Editor"...). It is
/// shown by the view while <see cref="AttributeEditorPageViewModel.Editor"/> is set (inline panel or overlay); OK
/// stages the values on the row, nothing is written before the sheet's Apply.
/// </summary>
public sealed partial class AttributeValueEditorViewModel : ObservableObject
{
    private readonly FieldContext _context;
    private readonly Action<AttributeValueEditorViewModel, IReadOnlyList<string>?> _close;

    /// <summary>The text the single-value editor was opened with (formatted dates do not round-trip exactly).</summary>
    private readonly string _shownValue = string.Empty;

    internal AttributeValueEditorViewModel(FieldContext context, AttributeRowViewModel row, Action<AttributeValueEditorViewModel, IReadOnlyList<string>?> close)
    {
        _context = context;
        _close = close;
        Row = row;
        var values = row.EditableValues;
        switch (row.EditorKind)
        {
            case AttributeEditorKind.MultiText or AttributeEditorKind.MultiDn:
                foreach (var value in values)
                {
                    Values.Add(value);
                }

                break;
            case AttributeEditorKind.Boolean:
                BooleanValue = values.Count == 0 ? null : string.Equals(values[0], "TRUE", StringComparison.OrdinalIgnoreCase);
                break;
            case AttributeEditorKind.Binary:
                Value = string.Join(Environment.NewLine, values);
                break;
            default:
                Value = values.Count > 0 ? values[0] : string.Empty;
                _shownValue = Value;
                break;
        }
    }

    public AttributeRowViewModel Row { get; }

    public AttributeEditorKind Kind => Row.EditorKind;

    /// <summary>"Attribute: department".</summary>
    public string Title => _context.Localizer.Format(L.AttributeEditor.EditorTitle, Row.Attribute);

    public bool IsReadOnly => Row.IsReadOnly;

    /// <summary>Single value (text, number, date, DN, hexadecimal view).</summary>
    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    /// <summary>Boolean editor: true, false or null (not set).</summary>
    [ObservableProperty]
    public partial bool? BooleanValue { get; set; }

    /// <summary>Multi-valued editors.</summary>
    public ObservableCollection<string> Values { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddValueCommand))]
    public partial string NewValue { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveValueCommand))]
    public partial string? SelectedValue { get; set; }

    /// <summary>Localized validation error.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Hint for the expected input (date format, TRUE/FALSE, "never"...).</summary>
    public string Hint => Kind switch
    {
        AttributeEditorKind.FileTime => _context.Localizer[L.AttributeEditor.FileTimeHint],
        AttributeEditorKind.DateTime => _context.Localizer[L.AttributeEditor.DateTimeHint],
        AttributeEditorKind.Integer or AttributeEditorKind.LargeInteger => _context.Localizer[L.AttributeEditor.IntegerHint],
        AttributeEditorKind.Dn or AttributeEditorKind.MultiDn => _context.Localizer[L.AttributeEditor.DnHint],
        _ => string.Empty,
    };

    [RelayCommand(CanExecute = nameof(CanAddValue))]
    private void AddValue()
    {
        if (!TryParse(NewValue.Trim(), out var wire))
        {
            return;
        }

        if (Values.Contains(wire, StringComparer.OrdinalIgnoreCase))
        {
            Error = _context.Localizer[L.Fields.DuplicateValue];
            return;
        }

        Error = null;
        Values.Add(wire);
        NewValue = string.Empty;
    }

    private bool CanAddValue() => !IsReadOnly && !string.IsNullOrWhiteSpace(NewValue);

    [RelayCommand(CanExecute = nameof(CanRemoveValue))]
    private void RemoveValue()
    {
        if (SelectedValue is { } selected)
        {
            Values.Remove(selected);
        }
    }

    private bool CanRemoveValue() => !IsReadOnly && SelectedValue is not null;

    /// <summary>Picks a DN with the object picker (DN editors).</summary>
    [RelayCommand]
    private async Task BrowseAsync()
    {
        var multi = Kind == AttributeEditorKind.MultiDn;
        var picked = await _context.Services.Dialogs.PickObjectsAsync(
            _context.Session,
            [ObjectKind.User, ObjectKind.Group, ObjectKind.Computer, ObjectKind.Contact, ObjectKind.OrganizationalUnit, ObjectKind.Container],
            multi).ConfigureAwait(true);
        if (picked.Count == 0)
        {
            return;
        }

        if (multi)
        {
            foreach (var dn in picked.Where(dn => !Values.Contains(dn, StringComparer.OrdinalIgnoreCase)))
            {
                Values.Add(dn);
            }
        }
        else
        {
            Value = picked[0];
        }
    }

    /// <summary>Stages "not set" (the attribute will be cleared).</summary>
    [RelayCommand]
    private void Clear()
    {
        if (!IsReadOnly)
        {
            _close(this, []);
        }
    }

    [RelayCommand]
    private void Ok()
    {
        if (IsReadOnly)
        {
            _close(this, null);
            return;
        }

        List<string> wire = [];
        switch (Kind)
        {
            case AttributeEditorKind.MultiText or AttributeEditorKind.MultiDn:
                wire.AddRange(Values);
                break;
            case AttributeEditorKind.Boolean:
                if (BooleanValue is { } flag)
                {
                    wire.Add(flag ? "TRUE" : "FALSE");
                }

                break;
            default:
                var text = Value.Trim();
                if (string.Equals(text, _shownValue.Trim(), StringComparison.Ordinal))
                {
                    // Not edited: dates are shown in whole seconds of local time (and 0 / "never" as text), so parsing
                    // the shown text back would stage a different value (AD rejects most lockoutTime/pwdLastSet values).
                    Error = null;
                    _close(this, null);
                    return;
                }

                if (text.Length > 0)
                {
                    if (!TryParse(text, out var parsed))
                    {
                        return;
                    }

                    wire.Add(NeverAsRead(parsed));
                }

                break;
        }

        if (wire.Count == 0 && _context.Mandatory.Contains(Row.Attribute))
        {
            Error = _context.Localizer[L.Fields.Required];
            return;
        }

        Error = null;
        _close(this, wire);
    }

    [RelayCommand]
    private void Cancel() => _close(this, null);

    /// <summary>
    /// "never" typed for a FILETIME that already means never keeps the value read from the DC: 0 and
    /// <see cref="long.MaxValue"/> both show as "never", but parsing "never" yields only one of them.
    /// </summary>
    private string NeverAsRead(string wire)
    {
        if (Kind == AttributeEditorKind.FileTime
            && IsNever(wire)
            && Row.Values?.Strings is [var original]
            && IsNever(original))
        {
            return original;
        }

        return wire;

        static bool IsNever(string value) => AdConvert.ParseInt64(value) is 0 or AdConvert.FileTimeNever;
    }

    private bool TryParse(string text, out string wire)
    {
        if (_context.TryParse(Row.Attribute, text, out wire, out var error))
        {
            Error = null;
            return true;
        }

        Error = error;
        return false;
    }
}

/// <summary>
/// ADUC Attribute Editor: every attribute allowed for the object's classes (plus constructed ones that were read),
/// with "only attributes with values" / "only writable attributes" filters. Loaded when the tab is first shown.
/// Edited values are staged and applied with the sheet's other changes; they are applied last, so they win over a
/// change of the same attribute on another tab.
/// </summary>
public sealed partial class AttributeEditorPageViewModel : PropertyPageViewModel
{
    /// <summary>Constructed attributes worth showing (they are not returned by "*").</summary>
    private static readonly string[] ConstructedAttributes =
    [
        Attr.CanonicalName, Attr.UserAccountControlComputed, Attr.PasswordExpiryComputed, Attr.MsDsPrincipalName,
    ];

    private readonly List<AttributeRowViewModel> _rows = [];
    private readonly SerialCancellation _loads = new();
    private string _dn = string.Empty;
    private IReadOnlyList<string> _objectClasses = [];
    private bool _activated;

    internal AttributeEditorPageViewModel(FieldContext context)
        : base(context, L.Pages.AttributeEditor, context.Localizer[L.Pages.AttributeEditor])
    {
    }

    /// <summary>Visible rows (filters applied), sorted by LDAP name like ADUC.</summary>
    public BulkObservableCollection<AttributeRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(CopyValueCommand))]
    public partial AttributeRowViewModel? SelectedRow { get; set; }

    [ObservableProperty]
    public partial bool ShowOnlyWithValues { get; set; }

    [ObservableProperty]
    public partial bool ShowOnlyWritable { get; set; }

    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    /// <summary>The value editor while editing (null otherwise).</summary>
    [ObservableProperty]
    public partial AttributeValueEditorViewModel? Editor { get; private set; }

    internal override IEnumerable<string> ReadAttributes => [];

    public override async Task ActivateAsync()
    {
        if (_activated)
        {
            return;
        }

        _activated = true;
        await ReloadAsync().ConfigureAwait(true);
    }

    public override void Revert()
    {
        foreach (var row in _rows)
        {
            row.PendingValues = null;
        }

        Editor = null;
        UpdateState();
    }

    internal override void Load(DirectoryObject obj)
    {
        // A fresh read of the object discards staged values (the sheet reloads after Apply and on Refresh).
        _dn = obj.DistinguishedName;
        _objectClasses = obj.GetStrings(Attr.ObjectClass);
        Editor = null;
        foreach (var row in _rows)
        {
            row.PendingValues = null;
        }

        UpdateState();
    }

    internal override Task LoadDetailsAsync(DirectoryObject obj, CancellationToken cancellationToken) =>
        _activated ? ReloadAsync() : Task.CompletedTask;

    /// <summary>Values the Attribute Editor showed for an attribute with staged values (null when nothing is staged).</summary>
    internal IReadOnlyList<string>? StagedBaseline(string attribute) =>
        _rows.FirstOrDefault(r => r.IsDirty && string.Equals(r.Attribute, attribute, StringComparison.OrdinalIgnoreCase)) is { } row
            ? row.Values?.Strings ?? []
            : null;

    internal override IEnumerable<BatchOperation> GetOperations() =>
        [.. _rows.Where(r => r.PendingValues is not null && !r.IsReadOnly).Select(r => new SetValuesOperation(r.Attribute, r.PendingValues!))];

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Edit(AttributeRowViewModel? row)
    {
        row ??= SelectedRow;
        if (row is not null)
        {
            Editor = new AttributeValueEditorViewModel(Context, row, CloseEditor);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyValue(AttributeRowViewModel? row)
    {
        row ??= SelectedRow;
        if (row is not { HasValue: true })
        {
            return;
        }

        // A LAPS password or another secret value: kept out of clipboard history and cleared after a while.
        if (SecretAttributes.IsSecret(row.Attribute, Context.Schema))
        {
            if (Context.Services.Clipboard.SetSecret(row.ValueText))
            {
                Context.Services.Notifications.Show(NotificationSeverity.Info, Context.Localizer[L.Fields.Copied]);
            }

            return;
        }

        Context.Services.Clipboard.SetText(row.ValueText);
        Context.Services.Notifications.Show(NotificationSeverity.Info, Context.Localizer[L.Fields.Copied]);
    }

    private bool HasSelection(AttributeRowViewModel? row) => (row ?? SelectedRow) is not null;

    [RelayCommand]
    private Task RefreshAsync() => ReloadAsync();

    partial void OnShowOnlyWithValuesChanged(bool value) => ApplyFilter();

    partial void OnShowOnlyWritableChanged(bool value) => ApplyFilter();

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    protected override bool ComputeHasChanges() => _rows.Any(r => r.IsDirty);

    protected override bool ComputeHasErrors() => false;

    private void CloseEditor(AttributeValueEditorViewModel editor, IReadOnlyList<string>? values)
    {
        if (values is not null && !editor.Row.IsReadOnly)
        {
            var original = editor.Row.Values?.Strings ?? [];
            editor.Row.PendingValues = values.Count == original.Count && values.Order(StringComparer.Ordinal).SequenceEqual(original.Order(StringComparer.Ordinal), StringComparer.Ordinal)
                ? null
                : values;
        }

        if (ReferenceEquals(Editor, editor))
        {
            Editor = null;
        }

        ApplyFilter();
        UpdateState();
    }

    private async Task ReloadAsync()
    {
        if (_dn.Length == 0)
        {
            return;
        }

        var token = _loads.Next();
        IsLoading = true;
        Error = null;
        try
        {
            var session = Context.Session;
            var dn = _dn;
            var obj = await Task.Run(
                async () =>
                {
                    var read = await session.GetAsync(dn, ["*", .. ConstructedAttributes], token).ConfigureAwait(false);
                    return read is null ? null : await CompleteRangedAsync(session, read, token).ConfigureAwait(false);
                },
                token).ConfigureAwait(true);
            if (obj is null)
            {
                Error = Context.Localizer[L.PropertySheet.NotFound];
                return;
            }

            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Context.HasSchema)
            {
                names.UnionWith(Context.Schema.GetAllowedAttributes(_objectClasses.Count > 0 ? _objectClasses : obj.GetStrings(Attr.ObjectClass)));
            }

            names.UnionWith(obj.Attributes.Keys);
            var pending = _rows.Where(r => r.IsDirty).ToDictionary(r => r.Attribute, r => r.PendingValues!, StringComparer.OrdinalIgnoreCase);
            _rows.Clear();
            foreach (var name in names)
            {
                var row = new AttributeRowViewModel(Context, name, obj.Attributes.GetValueOrDefault(name));
                if (pending.TryGetValue(name, out var staged) && !row.IsReadOnly)
                {
                    row.PendingValues = staged;
                }

                _rows.Add(row);
            }

            ApplyFilter();
            UpdateState();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Superseded by a newer load.
        }
        catch (DirectoryException ex)
        {
            Error = Context.Services.Errors.Translate(ex);
        }
        finally
        {
            if (_loads.IsCurrent(token))
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>Completes multi-valued attributes the server returned partially (more than 1500 values).</summary>
    private static async Task<DirectoryObject> CompleteRangedAsync(IDirectorySession session, DirectoryObject obj, CancellationToken cancellationToken)
    {
        List<AttributeValues>? completed = null;
        foreach (var values in obj.Attributes.Values)
        {
            if (!values.Name.Contains(";range=", StringComparison.OrdinalIgnoreCase) || values.Name.EndsWith("-*", StringComparison.Ordinal))
            {
                continue;
            }

            var baseName = values.Name[..values.Name.IndexOf(';', StringComparison.Ordinal)];
            if (session.Schema.IsBinary(baseName))
            {
                continue;
            }

            var all = new List<object>();
            await foreach (var value in session.GetAllValuesAsync(obj.DistinguishedName, baseName, cancellationToken).ConfigureAwait(false))
            {
                all.Add(value);
            }

            (completed ??= []).Add(new AttributeValues(baseName, all));
        }

        return completed is null ? obj : obj.With(completed);
    }

    private void ApplyFilter()
    {
        var selected = SelectedRow;
        Rows.ReplaceAll(_rows.Where(r =>
            (!ShowOnlyWithValues || r.HasValue)
            && (!ShowOnlyWritable || !r.IsReadOnly)
            && (FilterText.Length == 0 || r.Attribute.Contains(FilterText.Trim(), StringComparison.OrdinalIgnoreCase)
                || TextFolding.Contains(r.DisplayName, FilterText.Trim()))));
        SelectedRow = selected is not null && Rows.Contains(selected) ? selected : null;
    }
}
