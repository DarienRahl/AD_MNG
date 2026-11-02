namespace AdMng.Core.GroupPolicy.AdministrativeTemplates;

/// <summary>A control of a policy's dialog (<c>presentationTable/presentation</c> of the ADML).</summary>
public abstract record PresentationControl
{
    /// <summary>The element the control edits (<c>refId</c>); null for static text.</summary>
    public string? RefId { get; init; }

    /// <summary>The control's label.</summary>
    public string Label { get; init; } = string.Empty;
}

/// <summary>Static text (<c>text</c>).</summary>
public sealed record TextPresentation : PresentationControl;

/// <summary>A number box (<c>decimalTextBox</c>, <c>longDecimalTextBox</c>).</summary>
public sealed record NumberBoxPresentation : PresentationControl
{
    public ulong? DefaultValue { get; init; }

    /// <summary>Shows spin buttons (the default).</summary>
    public bool Spin { get; init; } = true;

    public ulong SpinStep { get; init; } = 1;
}

/// <summary>A text box (<c>textBox</c>).</summary>
public sealed record TextBoxPresentation : PresentationControl
{
    public string? DefaultValue { get; init; }
}

/// <summary>A multi-line text box (<c>multiTextBox</c>).</summary>
public sealed record MultiTextBoxPresentation : PresentationControl
{
    /// <summary>Height in lines (<c>defaultHeight</c>, default 3).</summary>
    public int DefaultHeight { get; init; } = 3;
}

/// <summary>A check box (<c>checkBox</c>).</summary>
public sealed record CheckBoxPresentation : PresentationControl
{
    public bool DefaultChecked { get; init; }
}

/// <summary>A text box with suggestions (<c>comboBox</c>).</summary>
public sealed record ComboBoxPresentation : PresentationControl
{
    public string? DefaultValue { get; init; }

    public IReadOnlyList<string> Suggestions { get; init; } = [];

    public bool NoSort { get; init; }
}

/// <summary>A drop-down list of an enum (<c>dropdownList</c>).</summary>
public sealed record DropDownListPresentation : PresentationControl
{
    /// <summary>The 0-based index of the item chosen by default.</summary>
    public int? DefaultItem { get; init; }

    public bool NoSort { get; init; }
}

/// <summary>A list editor (<c>listBox</c>, the "Pokaż…" dialog of a list element).</summary>
public sealed record ListBoxPresentation : PresentationControl;

/// <summary>The dialog of a policy: its controls in order.</summary>
/// <param name="Id">The presentation id.</param>
/// <param name="Controls">The controls.</param>
public sealed record PolicyPresentation(string Id, IReadOnlyList<PresentationControl> Controls);

/// <summary>One parsed ADML file (the language resources of an ADMX file).</summary>
public sealed record AdmlFile
{
    /// <summary>The file name (<c>WindowsUpdate.adml</c>).</summary>
    public required string FileName { get; init; }

    /// <summary>The culture folder it was read from (<c>pl-PL</c>).</summary>
    public required string Culture { get; init; }

    public string? Revision { get; init; }

    /// <summary>String id → text (ids compared without case).</summary>
    public required IReadOnlyDictionary<string, string> Strings { get; init; }

    /// <summary>Presentation id → dialog (ids compared without case).</summary>
    public required IReadOnlyDictionary<string, PolicyPresentation> Presentations { get; init; }

    public override string ToString() => Culture + "/" + FileName;
}
