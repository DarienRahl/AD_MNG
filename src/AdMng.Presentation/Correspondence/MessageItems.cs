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

/// <summary>An editable custom field (<c>Name</c> = <c>Value</c>, the value may use other <c>{{fields}}</c>).</summary>
public sealed partial class CustomFieldViewModel : ObservableObject
{
    public CustomFieldViewModel(string name = "", string value = "")
    {
        Name = name ?? string.Empty;
        Value = value ?? string.Empty;
    }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Value { get; set; }

    /// <summary>Why the field cannot be used (empty or repeated name, a name of a column or built-in), or null.</summary>
    [ObservableProperty]
    public partial string? Error { get; set; }

    public override string ToString() => $"{nameof(CustomFieldViewModel)} {Name}";
}
