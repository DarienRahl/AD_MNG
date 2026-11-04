using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AdMng.Core.GroupPolicy.Security;

/// <summary>A local group item as the editor saves it (GPP Local Users and Groups, [MS-GPPREF] 2.2.1.5.1).</summary>
/// <param name="GroupName">The group's name as GPMC writes it (<c>Administrators (built-in)</c>, <c>Serwisanci</c>).</param>
/// <param name="GroupSid">The group's SID (built-in groups), null for a group named only.</param>
/// <param name="Action">Create, replace, update or delete.</param>
/// <param name="DeleteAllUsers">"Usuń wszystkich użytkowników członkowskich" before the member changes.</param>
/// <param name="DeleteAllGroups">"Usuń wszystkie grupy członkowskie" before the member changes.</param>
/// <param name="Members">The member lines (add or remove).</param>
public sealed record GppLocalGroupEdit(
    string GroupName,
    string? GroupSid,
    GppItemAction Action,
    bool DeleteAllUsers,
    bool DeleteAllGroups,
    IReadOnlyList<GppGroupMember> Members);

/// <summary>
/// <c>Machine/Preferences/Groups/Groups.xml</c> as an editable document (K4): local group items are added, changed or
/// removed by their position in <see cref="Items"/> (the order of <see cref="GppGroupsParser.Parse"/>), and everything the
/// editor does not touch – <c>User</c> items, collections, item-level targeting (<c>Filters</c>), common options, unknown
/// attributes – is kept as read. A changed item gets a new <c>changed</c> time and the <c>image</c> of its action, as GPMC
/// does. Loaded with the hardened <see cref="GpoXml"/>; written as UTF-8 with BOM without indentation.
/// </summary>
public sealed class GppGroupsDocument
{
    /// <summary>The file's path in a GPO folder.</summary>
    public const string RelativePath = GppGroupsParser.RelativePath;

    /// <summary>The class id of the <c>Groups</c> root.</summary>
    public const string GroupsClassId = "{3125E937-EB16-4b4c-9934-544FC6D24D26}";

    /// <summary>The class id of a local group item.</summary>
    public const string GroupClassId = "{6D4A79E4-529C-4481-ABD0-F5BD7EA93BA7}";

    private readonly XDocument _document;

    /// <summary>The items as read, by uid: an edit that returns an item to this state restores its element as read.</summary>
    private readonly Dictionary<string, (XElement Element, GppLocalGroup Item)> _originals = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<GppLocalGroup>? _items;

    private GppGroupsDocument(XDocument document)
    {
        _document = document;
        foreach (var (element, item) in GppGroupsParser.GroupElements(document).Zip(Items))
        {
            if (item.Uid is { } uid)
            {
                _originals.TryAdd(uid, (new XElement(element), item));
            }
        }
    }

    /// <summary>A new file without items.</summary>
    public static GppGroupsDocument New() => new(new XDocument(new XElement("Groups", new XAttribute("clsid", GroupsClassId))));

    /// <summary>The document of a file; an empty or missing file is a new one.</summary>
    /// <exception cref="GpoParseException">Not well-formed XML, a prohibited feature, or another root than <c>Groups</c>.</exception>
    public static GppGroupsDocument Parse(ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty)
        {
            return New();
        }

        var document = GpoXml.Load(content);
        if (!document.Root!.Name.LocalName.Equals("Groups", StringComparison.OrdinalIgnoreCase))
        {
            throw new GpoParseException("Groups.xml has another root element than Groups.");
        }

        return new GppGroupsDocument(document);
    }

    /// <summary>The local group items in document order (the order the client applies them).</summary>
    public IReadOnlyList<GppLocalGroup> Items => _items ??= GppGroupsParser.Read(_document);

    /// <summary>
    /// Changes the item at <paramref name="index"/> (its targeting, options and other attributes stay). An edit that
    /// returns an item to its state as read restores the element as read (no new <c>changed</c> time), so an edit and its
    /// undo give back the bytes that were read.
    /// </summary>
    public void Update(int index, GppLocalGroupEdit edit, DateTime changed)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var element = Element(index);
        var current = Items[index];
        if (current.Uid is { } uid && _originals.TryGetValue(uid, out var original) && Same(original.Item, edit))
        {
            if (!XNode.DeepEquals(element, original.Element))
            {
                element.ReplaceWith(new XElement(original.Element));
                _items = null;
            }

            return;
        }

        if (Same(current, edit))
        {
            return;
        }

        Write(element, edit, changed);
        _items = null;
    }

    /// <summary>Appends a new item (at the end of the root: applied after the others) and returns its index.</summary>
    public int Add(GppLocalGroupEdit edit, DateTime changed)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var root = _document.Root!;
        var ns = root.Name.Namespace;
        var element = new XElement(ns + "Group",
            new XAttribute("clsid", GroupClassId),
            new XAttribute("name", string.Empty),
            new XAttribute("image", "2"),
            new XAttribute("changed", string.Empty),
            new XAttribute("uid", Guid.NewGuid().ToString("B").ToUpperInvariant()),
            new XElement(ns + "Properties",
                new XAttribute("action", "U"),
                new XAttribute("newName", string.Empty),
                new XAttribute("description", string.Empty),
                new XAttribute("deleteAllUsers", "0"),
                new XAttribute("deleteAllGroups", "0"),
                new XAttribute("removeAccounts", "0"),
                new XAttribute("groupSid", string.Empty),
                new XAttribute("groupName", string.Empty),
                new XElement(ns + "Members")));
        Write(element, edit, changed); // validates before the document changes
        root.Add(element);
        _items = null;
        return Items.Count - 1;
    }

    /// <summary>Removes the item at <paramref name="index"/> (a collection left empty stays, as in GPMC).</summary>
    public void RemoveAt(int index)
    {
        Element(index).Remove();
        _items = null;
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
    public override string ToString() => $"Groups.xml ({Items.Count} groups)";

    /// <summary>The <c>image</c> GPMC shows for an action (0 create, 1 replace, 2 update, 3 delete).</summary>
    public static int Image(GppItemAction action) => action switch
    {
        GppItemAction.Create => 0,
        GppItemAction.Replace => 1,
        GppItemAction.Delete => 3,
        _ => 2,
    };

    private XElement Element(int index)
    {
        var elements = GppGroupsParser.GroupElements(_document).ToList();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, elements.Count);
        return elements[index];
    }

    private static bool Same(GppLocalGroup item, GppLocalGroupEdit edit) =>
        string.Equals(item.GroupName ?? string.Empty, edit.GroupName.Trim(), StringComparison.Ordinal)
        && string.Equals(item.GroupSid, Sid(edit.GroupSid), StringComparison.OrdinalIgnoreCase)
        && item.Action == edit.Action
        && item.DeleteAllUsers == edit.DeleteAllUsers
        && item.DeleteAllGroups == edit.DeleteAllGroups
        && item.Members.SequenceEqual(edit.Members.Select(m => m with { Name = Text(m.Name), Sid = Sid(m.Sid) }));

    private static void Write(XElement element, GppLocalGroupEdit edit, DateTime changed)
    {
        var name = edit.GroupName.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("A local group item needs the group's name.", nameof(edit));
        }

        var ns = element.Name.Namespace;
        var properties = GpoXml.Child(element, "Properties")!;
        SetAttribute(element, "name", name);
        SetAttribute(element, "image", Image(edit.Action).ToString(CultureInfo.InvariantCulture));
        SetAttribute(element, "changed", changed.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        SetAttribute(properties, "action", edit.Action switch
        {
            GppItemAction.Create => "C",
            GppItemAction.Replace => "R",
            GppItemAction.Delete => "D",
            _ => "U",
        });
        SetAttribute(properties, "groupName", name);
        SetAttribute(properties, "groupSid", Sid(edit.GroupSid) ?? string.Empty);
        SetAttribute(properties, "deleteAllUsers", edit.DeleteAllUsers ? "1" : "0");
        SetAttribute(properties, "deleteAllGroups", edit.DeleteAllGroups ? "1" : "0");

        var members = GpoXml.Child(properties, "Members");
        if (members is null)
        {
            members = new XElement(ns + "Members");
            properties.Add(members);
        }

        members.RemoveNodes();
        foreach (var member in edit.Members)
        {
            var memberName = Text(member.Name);
            var sid = Sid(member.Sid);
            if (memberName is null && sid is null)
            {
                continue;
            }

            members.Add(new XElement(ns + "Member",
                new XAttribute("name", memberName ?? string.Empty),
                new XAttribute("action", member.Action == GppMemberAction.Remove ? "REMOVE" : "ADD"),
                new XAttribute("sid", sid ?? string.Empty)));
        }
    }

    /// <summary>Sets an attribute matched case-insensitively (the file's spelling is kept), adds it when missing.</summary>
    private static void SetAttribute(XElement element, string localName, string value)
    {
        var existing = element.Attributes().FirstOrDefault(a => !a.IsNamespaceDeclaration && a.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            element.Add(new XAttribute(localName, value));
        }
        else
        {
            existing.Value = value;
        }
    }

    private static string? Sid(string? sid) => string.IsNullOrWhiteSpace(sid) ? null
        : SecurityTemplate.IsSid(sid.Trim()) ? sid.Trim().ToUpperInvariant()
        : throw new ArgumentException("Not a SID: " + sid, nameof(sid));

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
