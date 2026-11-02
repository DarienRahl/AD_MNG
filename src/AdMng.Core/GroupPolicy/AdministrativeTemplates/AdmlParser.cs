using System.Globalization;
using System.Xml.Linq;

namespace AdMng.Core.GroupPolicy.AdministrativeTemplates;

/// <summary>
/// Parses ADML files (<c>policyDefinitionResources</c>: the string table and the presentation table of one language).
/// Untrusted input as <see cref="AdmxParser"/>: hardened XML, limits, a structural error fails the file. Unknown controls
/// are skipped (a newer schema may add some); a repeated id keeps its first definition.
/// </summary>
public static class AdmlParser
{
    /// <summary>Most strings of one file (the largest Windows file has about 6 000).</summary>
    public const int MaxStrings = 100_000;

    /// <summary>Most presentations of one file.</summary>
    public const int MaxPresentations = 20_000;

    /// <summary>Most controls of one presentation.</summary>
    public const int MaxControls = 256;

    public static AdmlFile Parse(string fileName, string culture, ReadOnlySpan<byte> content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(culture);
        var root = GpoXml.Load(content).Root!;
        if (!AdmxParser.Is(root, "policyDefinitionResources"))
        {
            throw new GpoParseException("The file is not an ADML file (policyDefinitionResources).");
        }

        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var presentations = new Dictionary<string, PolicyPresentation>(StringComparer.OrdinalIgnoreCase);
        if (AdmxParser.Child(root, "resources") is { } resources)
        {
            if (AdmxParser.Child(resources, "stringTable") is { } table)
            {
                foreach (var entry in AdmxParser.Children(table, "string"))
                {
                    if (strings.Count >= MaxStrings)
                    {
                        throw new GpoParseException("The ADML file has too many strings.");
                    }

                    strings.TryAdd(Id(entry), entry.Value);
                }
            }

            if (AdmxParser.Child(resources, "presentationTable") is { } presentationTable)
            {
                foreach (var presentation in AdmxParser.Children(presentationTable, "presentation"))
                {
                    if (presentations.Count >= MaxPresentations)
                    {
                        throw new GpoParseException("The ADML file has too many presentations.");
                    }

                    var id = Id(presentation);
                    presentations.TryAdd(id, new PolicyPresentation(id, Controls(presentation)));
                }
            }
        }

        return new AdmlFile
        {
            FileName = fileName,
            Culture = culture,
            Revision = GpoXml.Attribute(root, "revision"),
            Strings = strings,
            Presentations = presentations,
        };
    }

    private static List<PresentationControl> Controls(XElement presentation)
    {
        var controls = new List<PresentationControl>();
        foreach (var element in presentation.Elements())
        {
            if (controls.Count >= MaxControls)
            {
                throw new GpoParseException("A presentation has too many controls.");
            }

            var refId = GpoXml.Attribute(element, "refId");
            PresentationControl? control = element.Name.LocalName.ToUpperInvariant() switch
            {
                "TEXT" => new TextPresentation { Label = element.Value },
                "DECIMALTEXTBOX" or "LONGDECIMALTEXTBOX" => new NumberBoxPresentation
                {
                    RefId = refId,
                    Label = element.Value,
                    DefaultValue = Number(GpoXml.Attribute(element, "defaultValue")),
                    Spin = Bool(GpoXml.Attribute(element, "spin"), fallback: true),
                    SpinStep = Number(GpoXml.Attribute(element, "spinStep")) is { } step and > 0 ? step : 1,
                },
                "TEXTBOX" => new TextBoxPresentation
                {
                    RefId = refId,
                    Label = AdmxParser.Child(element, "label")?.Value ?? string.Empty,
                    DefaultValue = AdmxParser.Child(element, "defaultValue")?.Value,
                },
                "MULTITEXTBOX" => new MultiTextBoxPresentation
                {
                    RefId = refId,
                    Label = element.Value,
                    DefaultHeight = (int)Math.Clamp(Number(GpoXml.Attribute(element, "defaultHeight")) ?? 3, 1, 50),
                },
                "CHECKBOX" => new CheckBoxPresentation
                {
                    RefId = refId,
                    Label = element.Value,
                    DefaultChecked = Bool(GpoXml.Attribute(element, "defaultChecked"), fallback: false),
                },
                "COMBOBOX" => new ComboBoxPresentation
                {
                    RefId = refId,
                    Label = AdmxParser.Child(element, "label")?.Value ?? string.Empty,
                    DefaultValue = AdmxParser.Child(element, "default")?.Value,
                    Suggestions = [.. AdmxParser.Children(element, "suggestion").Select(s => s.Value).Take(AdmxParser.MaxItems)],
                    NoSort = Bool(GpoXml.Attribute(element, "noSort"), fallback: false),
                },
                "DROPDOWNLIST" => new DropDownListPresentation
                {
                    RefId = refId,
                    Label = element.Value,
                    DefaultItem = Number(GpoXml.Attribute(element, "defaultItem")) is { } index and <= int.MaxValue ? (int)index : null,
                    NoSort = Bool(GpoXml.Attribute(element, "noSort"), fallback: false),
                },
                "LISTBOX" => new ListBoxPresentation { RefId = refId, Label = element.Value },
                _ => null,
            };
            if (control is not null)
            {
                controls.Add(control);
            }
        }

        return controls;
    }

    private static string Id(XElement element)
    {
        var id = GpoXml.Attribute(element, "id");
        return string.IsNullOrWhiteSpace(id) || id.Length > AdmxParser.MaxNameLength
            ? throw new GpoParseException("An ADML entry has no valid id.")
            : id.Trim();
    }

    private static ulong? Number(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null
        : ulong.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value
        : throw new GpoParseException("A number of a presentation is not valid.");

    private static bool Bool(string? text, bool fallback) => text?.Trim().ToUpperInvariant() switch
    {
        null or "" => fallback,
        "TRUE" or "1" => true,
        "FALSE" or "0" => false,
        _ => throw new GpoParseException("A boolean of a presentation is not valid."),
    };
}
