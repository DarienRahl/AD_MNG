using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AdMng.Core.GroupPolicy.Content;

namespace AdMng.Core.GroupPolicy.Management;

/// <summary>
/// Any Group Policy Preferences file (<c>Machine|User/Preferences/&lt;Type&gt;/&lt;Type&gt;.xml</c>) as an editable document
/// for the K7 copy, remove and merge planners (docs/dev/k-computers-and-gpo.md 21.5): items are cloned and removed as
/// whole elements, so item-level targeting, common options and attributes no editor of AD Manager knows survive. Items
/// are listed in the order and with the ids <see cref="GppXmlParser.Parse"/> gives them (<c>uid</c>, else
/// <c>&lt;Element&gt;#n</c> counted over every item of the file), each with its ancestor <c>Collection</c> elements.
/// Written as <see cref="Security.GppGroupsDocument"/> writes Groups.xml: UTF-8 with BOM, declaration, no indentation.
/// In memory only; content is untrusted (<see cref="GpoParseException"/>). Not thread-safe.
/// </summary>
public sealed class GppPreferenceDocument
{
    private readonly XDocument _document;

    /// <summary>The items as read, by their id then: ids stay valid while items are removed (positional ids would shift).</summary>
    private readonly Dictionary<string, XElement> _original = new(StringComparer.OrdinalIgnoreCase);

    private GppPreferenceDocument(XDocument document)
    {
        _document = document;
        foreach (var item in Enumerate(document))
        {
            _original.TryAdd(item.ItemId, item.Element);
        }
    }

    /// <summary>A new, empty file whose root has <paramref name="rootName"/> (namespace included) and the <c>clsid</c> of the source's root.</summary>
    public static GppPreferenceDocument New(XName rootName, string? classId)
    {
        ArgumentNullException.ThrowIfNull(rootName);
        var root = new XElement(rootName);
        if (!string.IsNullOrEmpty(classId))
        {
            root.SetAttributeValue("clsid", classId);
        }

        return new GppPreferenceDocument(new XDocument(root));
    }

    /// <summary>The document of a file (hardened loading of <see cref="GpoXml.Load"/>).</summary>
    /// <exception cref="GpoParseException">Empty, not well-formed XML or a prohibited feature.</exception>
    public static GppPreferenceDocument Parse(ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty)
        {
            throw new GpoParseException("The preferences file is empty.");
        }

        return new GppPreferenceDocument(GpoXml.Load(content));
    }

    /// <summary>The root element's name (with its namespace).</summary>
    public XName RootName => _document.Root!.Name;

    /// <summary>The root element's <c>clsid</c>, or null.</summary>
    public string? RootClassId => GpoXml.Attribute(_document.Root!, "clsid");

    /// <summary>The items now, in parser order (collection pseudo-items with a <c>uid</c> included, as the parser counts them).</summary>
    public IReadOnlyList<GppXmlItem> Items => [.. Enumerate(_document)];

    /// <summary>The item that had <paramref name="itemId"/> when the file was read (or, for an item appended since, its id now); null when gone.</summary>
    public GppXmlItem? Find(string itemId)
    {
        ArgumentNullException.ThrowIfNull(itemId);
        if (_original.TryGetValue(itemId, out var element))
        {
            return element.Parent is null ? null : new GppXmlItem(itemId, element, Collections(element));
        }

        return Items.FirstOrDefault(i => i.ItemId.Equals(itemId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Appends a copy of <paramref name="item"/> at the end of the root (the client applies it after the others) with a new
    /// upper-case <c>uid</c> in braces and <c>changed</c> = <paramref name="changed"/> (<c>yyyy-MM-dd HH:mm:ss</c>); its
    /// <c>Filters</c>, <c>Properties</c> and other attributes are kept. Elements of the source's namespace are moved into the
    /// target's root namespace when they differ (GPP files have none). Returns the new uid.
    /// </summary>
    public string Append(XElement item, DateTime changed, Guid? uid = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var clone = new XElement(item);
        var targetNamespace = _document.Root!.Name.Namespace;
        if (clone.Name.Namespace != targetNamespace)
        {
            foreach (var element in clone.DescendantsAndSelf().Where(e => e.Name.Namespace == item.Name.Namespace))
            {
                element.Name = targetNamespace + element.Name.LocalName;
            }
        }

        var id = (uid ?? Guid.NewGuid()).ToString("B", CultureInfo.InvariantCulture).ToUpperInvariant();
        SetItemAttribute(clone, "uid", id);
        SetItemAttribute(clone, "changed", changed.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        _document.Root.Add(clone);
        _original.TryAdd(id, clone);
        return id;
    }

    /// <summary>Removes the item with <paramref name="itemId"/> (see <see cref="Find"/>); a collection left empty stays, as in GPMC. False when gone.</summary>
    public bool Remove(string itemId)
    {
        if (Find(itemId) is not { } item)
        {
            return false;
        }

        item.Element.Remove();
        return true;
    }

    /// <summary>The XML (UTF-8 with BOM, declaration, no indentation).</summary>
    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), Indent = false, NewLineHandling = NewLineHandling.Entitize };
        using (var writer = XmlWriter.Create(stream, settings))
        {
            new XDocument(new XDeclaration("1.0", "utf-8", null), _document.Root).Save(writer);
        }

        return stream.ToArray();
    }

    /// <summary>Never content.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{_document.Root!.Name.LocalName} ({Items.Count} items)");

    /// <summary>Every element with a <c>Properties</c> child or a <c>uid</c> below the root, as <see cref="GppXmlParser.Parse"/> numbers them.</summary>
    private static IEnumerable<GppXmlItem> Enumerate(XDocument document)
    {
        var index = 0;
        foreach (var element in document.Root!.Descendants().Where(IsItem).ToList())
        {
            index++;
            var uid = GpoXml.Attribute(element, "uid");
            var id = string.IsNullOrWhiteSpace(uid) ? element.Name.LocalName + "#" + GpoValues.Number(index) : uid.Trim();
            yield return new GppXmlItem(id, element, Collections(element));
        }
    }

    private static bool IsItem(XElement element) =>
        GpoXml.Child(element, "Properties") is not null || GpoXml.Attribute(element, "uid") is not null;

    private static IReadOnlyList<XElement> Collections(XElement element) =>
        [.. element.Ancestors().Where(a => a.Name.LocalName.Equals("Collection", StringComparison.OrdinalIgnoreCase)).Reverse()];

    /// <summary>Sets an attribute of the item, keeping the spelling of an existing one (GPP writers differ in case).</summary>
    private static void SetItemAttribute(XElement element, string name, string value)
    {
        var existing = element.Attributes().FirstOrDefault(a => !a.IsNamespaceDeclaration && a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.Value = value;
        }
        else
        {
            element.SetAttributeValue(name, value);
        }
    }
}
