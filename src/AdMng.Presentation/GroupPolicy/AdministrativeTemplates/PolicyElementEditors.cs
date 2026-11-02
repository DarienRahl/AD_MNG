using System.Collections.ObjectModel;
using System.Globalization;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy.AdministrativeTemplates;

/// <summary>
/// One control of a policy's options (the ADML presentation: text, check box, number, text box, drop-down list, lines,
/// list). An editor of an element tells its owner when its value changes (<see cref="ValueChanged"/>); static text has no
/// element.
/// </summary>
public abstract partial class PolicyElementEditorViewModel : ObservableObject
{
    protected PolicyElementEditorViewModel(PolicyElement? element, string label)
    {
        Element = element;
        Label = label ?? string.Empty;
    }

    /// <summary>The element edited (null: static text).</summary>
    public PolicyElement? Element { get; }

    public string Label { get; }

    /// <summary>The controls can be changed (the policy is enabled and the GPO can be written).</summary>
    [ObservableProperty]
    public partial bool IsEditable { get; set; }

    /// <summary>The value changed by the operator.</summary>
    public event EventHandler? ValueChanged;

    /// <summary>The element's value (null: none, or not valid – <see cref="InputError"/> says why).</summary>
    public abstract PolicyElementValue? Value { get; }

    /// <summary>A value the editor cannot even turn into an element value ("not a number"), else null.</summary>
    public virtual PolicyValueProblemKind? InputError => null;

    /// <summary>Shows <paramref name="value"/> (from Registry.pol or the defaults) without raising <see cref="ValueChanged"/>.</summary>
    public abstract void Show(PolicyElementValue? value);

    protected void RaiseValueChanged() => ValueChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Static text of the dialog.</summary>
public sealed class StaticTextEditorViewModel(string text) : PolicyElementEditorViewModel(null, text)
{
    public override PolicyElementValue? Value => null;

    public override void Show(PolicyElementValue? value)
    {
    }
}

/// <summary>A check box.</summary>
public sealed partial class CheckBoxEditorViewModel(BooleanElement element, string label) : PolicyElementEditorViewModel(element, label)
{
    private bool _showing;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    public override PolicyElementValue? Value => new BooleanElementValue(IsChecked);

    public override void Show(PolicyElementValue? value)
    {
        _showing = true;
        IsChecked = value is BooleanElementValue { Value: true };
        _showing = false;
    }

    partial void OnIsCheckedChanged(bool value)
    {
        if (!_showing)
        {
            RaiseValueChanged();
        }
    }
}

/// <summary>A number with optional spin buttons (decimal and long decimal elements).</summary>
public sealed partial class NumberEditorViewModel : PolicyElementEditorViewModel
{
    private bool _showing;

    public NumberEditorViewModel(DecimalElement element, string label, bool spin, ulong spinStep)
        : base(element, label)
    {
        Number = element;
        HasSpin = spin;
        SpinStep = spinStep == 0 ? 1 : spinStep;
    }

    public DecimalElement Number { get; }

    public bool HasSpin { get; }

    public ulong SpinStep { get; }

    /// <summary>"0 – 7".</summary>
    public string RangeText => string.Create(CultureInfo.CurrentCulture, $"{Number.MinValue} – {Number.MaxValue}");

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    public override PolicyElementValue? Value =>
        ulong.TryParse(Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? new NumberElementValue(number) : null;

    public override PolicyValueProblemKind? InputError =>
        Text.Trim().Length > 0 && Value is null ? PolicyValueProblemKind.OutOfRange : null;

    public override void Show(PolicyElementValue? value)
    {
        _showing = true;
        Text = value is NumberElementValue number ? number.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
        _showing = false;
    }

    partial void OnTextChanged(string value)
    {
        if (!_showing)
        {
            RaiseValueChanged();
        }
    }

    [RelayCommand]
    private void Up() => Step(up: true);

    [RelayCommand]
    private void Down() => Step(up: false);

    private void Step(bool up)
    {
        if (!IsEditable)
        {
            return;
        }

        var current = Value is NumberElementValue number ? number.Value : Number.MinValue;
        var next = up
            ? (current > Number.MaxValue - Math.Min(SpinStep, Number.MaxValue) ? Number.MaxValue : current + SpinStep)
            : (current < Number.MinValue + SpinStep ? Number.MinValue : current - SpinStep);
        Text = Math.Clamp(next, Number.MinValue, Number.MaxValue).ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>A text box (with suggestions for a combo box).</summary>
public sealed partial class TextEditorViewModel(TextElement element, string label, IReadOnlyList<string>? suggestions = null)
    : PolicyElementEditorViewModel(element, label)
{
    private bool _showing;

    public TextElement TextElement { get; } = element;

    public IReadOnlyList<string> Suggestions { get; } = suggestions ?? [];

    public bool HasSuggestions => Suggestions.Count > 0;

    public int MaxLength => TextElement.MaxLength > 0 ? TextElement.MaxLength : 0;

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    public override PolicyElementValue? Value => new TextElementValue(Text);

    public override void Show(PolicyElementValue? value)
    {
        _showing = true;
        Text = value is TextElementValue text ? text.Value : string.Empty;
        _showing = false;
    }

    partial void OnTextChanged(string value)
    {
        if (!_showing)
        {
            RaiseValueChanged();
        }
    }
}

/// <summary>Lines of text (one value per line).</summary>
public sealed partial class MultiTextEditorViewModel(MultiTextElement element, string label, int height) : PolicyElementEditorViewModel(element, label)
{
    private bool _showing;

    /// <summary>Height in lines.</summary>
    public int Height { get; } = Math.Clamp(height, 2, 20);

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    public override PolicyElementValue? Value => new MultiTextElementValue([.. Text.Replace("\r\n", "\n", StringComparison.Ordinal)
        .Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0)]);

    public override void Show(PolicyElementValue? value)
    {
        _showing = true;
        Text = value is MultiTextElementValue lines ? string.Join(Environment.NewLine, lines.Lines) : string.Empty;
        _showing = false;
    }

    partial void OnTextChanged(string value)
    {
        if (!_showing)
        {
            RaiseValueChanged();
        }
    }
}

/// <summary>A drop-down list of an enum's items.</summary>
public sealed partial class DropDownEditorViewModel(EnumElement element, string label, IReadOnlyList<string> items) : PolicyElementEditorViewModel(element, label)
{
    private bool _showing;

    public IReadOnlyList<string> Items { get; } = items ?? [];

    [ObservableProperty]
    public partial int SelectedIndex { get; set; } = -1;

    public override PolicyElementValue? Value => SelectedIndex >= 0 ? new EnumElementValue(SelectedIndex) : null;

    public override void Show(PolicyElementValue? value)
    {
        _showing = true;
        SelectedIndex = value is EnumElementValue chosen && chosen.Index < Items.Count ? chosen.Index : -1;
        _showing = false;
    }

    partial void OnSelectedIndexChanged(int value)
    {
        if (!_showing)
        {
            RaiseValueChanged();
        }
    }
}

/// <summary>An entry of a list editor.</summary>
public sealed partial class ListEntryViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;
}

/// <summary>The entries of a list element (named by the operator for explicit lists).</summary>
public sealed partial class ListEditorViewModel : PolicyElementEditorViewModel
{
    private bool _showing;

    public ListEditorViewModel(ListElement element, string label)
        : base(element, label)
    {
        List = element;
        Entries.CollectionChanged += (_, e) =>
        {
            foreach (var item in e.NewItems?.OfType<ListEntryViewModel>() ?? [])
            {
                item.PropertyChanged += OnEntryChanged;
            }

            foreach (var item in e.OldItems?.OfType<ListEntryViewModel>() ?? [])
            {
                item.PropertyChanged -= OnEntryChanged;
            }

            if (!_showing)
            {
                RaiseValueChanged();
            }
        };
    }

    public ListElement List { get; }

    /// <summary>The operator names each value (two columns).</summary>
    public bool IsExplicit => List.ExplicitValue;

    public ObservableCollection<ListEntryViewModel> Entries { get; } = [];

    public override PolicyElementValue? Value => new ListElementValue([.. Entries
        .Where(e => e.Name.Trim().Length > 0 || e.Value.Trim().Length > 0)
        .Select(e => new ListElementItem(IsExplicit ? e.Name.Trim() : string.Empty, e.Value))]);

    public override void Show(PolicyElementValue? value)
    {
        _showing = true;
        Entries.Clear();
        foreach (var item in (value as ListElementValue)?.Items ?? [])
        {
            Entries.Add(new ListEntryViewModel { Name = item.Name, Value = item.Value });
        }

        _showing = false;
    }

    [RelayCommand]
    private void Add()
    {
        if (!IsEditable)
        {
            return;
        }

        Entries.Add(new ListEntryViewModel());
    }

    /// <summary>Removes an entry (the row's remove button passes it).</summary>
    [RelayCommand]
    private void Remove(ListEntryViewModel? entry)
    {
        if (entry is not null && IsEditable)
        {
            Entries.Remove(entry);
        }
    }

    private void OnEntryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_showing)
        {
            RaiseValueChanged();
        }
    }
}
