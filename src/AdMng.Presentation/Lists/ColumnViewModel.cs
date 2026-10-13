using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.Lists;

/// <summary>Sort state of a column header.</summary>
public enum SortDirection
{
    None,
    Ascending,
    Descending,
}

/// <summary>
/// One column of an object list. The view binds a DataGrid column to <c>row[Attribute]</c> (the row indexer
/// returns the formatted value).
/// </summary>
public sealed partial class ColumnViewModel(string attribute, string header, double width = 0) : ObservableObject
{
    /// <summary>LDAP display name, or a pseudo column (see <see cref="ColumnSets"/>).</summary>
    public string Attribute { get; } = attribute;

    /// <summary>Binding path for the view: <c>[attribute]</c>.</summary>
    public string BindingPath => $"[{Attribute}]";

    /// <summary>Localized header (schema display name for the UI culture, else a localized fallback).</summary>
    [ObservableProperty]
    public partial string Header { get; set; } = header;

    /// <summary>Width in device independent pixels; 0 = automatic.</summary>
    [ObservableProperty]
    public partial double Width { get; set; } = width;

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    [ObservableProperty]
    public partial SortDirection SortDirection { get; set; }
}
