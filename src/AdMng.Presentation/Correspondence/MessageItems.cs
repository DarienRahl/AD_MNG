using AdMng.Core.Correspondence.Fields;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.Correspondence;

/// <summary>A signature of the combo box: "(bez podpisu)" first (<see cref="Name"/> null), then the Outlook signatures.</summary>
public sealed record SignatureOption(string? Name, string Text)
{
    public override string ToString() => Text;
}

/// <summary>A template of the combo box: built-ins first, then the saved ones.</summary>
public sealed record TemplateOption(string Id, string Text, bool IsBuiltIn)
{
    public override string ToString() => Text;
}

/// <summary>A file attached to every message of the run (the list is not persisted).</summary>
/// <param name="path">The file.</param>
/// <param name="name">The file name shown.</param>
/// <param name="length">Its size in bytes (0 when it does not exist).</param>
/// <param name="sizeText">The size as shown ("1,4 MB").</param>
/// <param name="exists">The file exists (a missing one turns the header red and blocks sending).</param>
public sealed class AttachmentViewModel(string path, string name, long length, string sizeText, bool exists)
{
    public string Path { get; } = path ?? throw new ArgumentNullException(nameof(path));

    public string Name { get; } = name ?? string.Empty;

    public long Length { get; } = length;

    public string SizeText { get; } = sizeText ?? string.Empty;

    public bool Exists { get; } = exists;

    public override string ToString() => nameof(AttachmentViewModel);
}

/// <summary>
/// An editable custom field: a text field (<c>Name</c> = <c>Value</c>, the value may use other <c>{{fields}}</c>) or a
/// date field (<see cref="CustomFieldKind.Date"/>: a date picked in the calendar, a .NET date format from
/// <see cref="FormatChoices"/> or typed, and the <see cref="Sample"/> as messages show it).
/// </summary>
public sealed partial class CustomFieldViewModel : ObservableObject
{
    /// <summary>A text field.</summary>
    public CustomFieldViewModel(string name = "", string value = "")
    {
        Name = name ?? string.Empty;
        Value = value ?? string.Empty;
    }

    /// <summary>A field of <paramref name="kind"/> without a value (a date field without a date, in the default format).</summary>
    public CustomFieldViewModel(CustomFieldKind kind, string name = "")
        : this(name)
    {
        Kind = kind == CustomFieldKind.Date ? CustomFieldKind.Date : CustomFieldKind.Text;
    }

    /// <summary>The field of a definition (a template, the last draft); a date that does not parse is no date.</summary>
    public CustomFieldViewModel(CustomFieldDefinition definition)
        : this(definition?.Name ?? string.Empty, definition?.Kind == CustomFieldKind.Date ? string.Empty : definition?.Value ?? string.Empty)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Kind == CustomFieldKind.Date)
        {
            Kind = CustomFieldKind.Date;
            Date = CustomFieldFormats.TryParseValue(definition.Value, out var date) ? date.ToDateTime(TimeOnly.MinValue) : null;
            Format = string.IsNullOrWhiteSpace(definition.Format) ? CustomFieldFormats.DefaultDate : definition.Format.Trim();
        }
    }

    /// <summary>The date formats offered by the editable format box (<see cref="CustomFieldFormats.DateChoices"/>).</summary>
    public static IReadOnlyList<string> FormatChoices => CustomFieldFormats.DateChoices;

    /// <summary>Text or date (chosen when the field is added: "Dodaj pole", "Dodaj datę").</summary>
    public CustomFieldKind Kind { get; }

    public bool IsDate => Kind == CustomFieldKind.Date;

    [ObservableProperty]
    public partial string Name { get; set; }

    /// <summary>The value of a text field (a date field keeps its value in <see cref="Date"/>).</summary>
    [ObservableProperty]
    public partial string Value { get; set; }

    /// <summary>The date of a date field (the day only), or null when none is chosen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Sample))]
    public partial DateTime? Date { get; set; }

    /// <summary>The format of a date field (blank: <see cref="CustomFieldFormats.DefaultDate"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Sample))]
    public partial string Format { get; set; } = CustomFieldFormats.DefaultDate;

    /// <summary>A date field's date as messages show it ("15 listopada 2026r."), or "" without a date (and for text fields).</summary>
    public string Sample => IsDate && Date is { } date ? CustomFieldFormats.FormatDate(DateOnly.FromDateTime(date), Format) : string.Empty;

    /// <summary>Why the field cannot be used (empty or repeated name, a name of a column or built-in, no date), or null.</summary>
    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>The definition of the field (the name trimmed; a date field's value <c>yyyy-MM-dd</c>, "" without a date,
    /// its format trimmed, blank → the default).</summary>
    public CustomFieldDefinition ToDefinition() => IsDate
        ? new CustomFieldDefinition(Name?.Trim() ?? string.Empty,
            Date is { } date ? CustomFieldFormats.ValueOf(DateOnly.FromDateTime(date)) : string.Empty,
            CustomFieldKind.Date,
            string.IsNullOrWhiteSpace(Format) ? null : Format.Trim())
        : new CustomFieldDefinition(Name?.Trim() ?? string.Empty, Value ?? string.Empty);

    public override string ToString() => $"{nameof(CustomFieldViewModel)} {Name} ({Kind})";
}
