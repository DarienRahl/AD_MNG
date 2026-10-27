using System.Globalization;
using System.Text;
using AdMng.Core.Ldap;

namespace AdMng.Core.GroupPolicy;

/// <summary>
/// An editable <c>gPLink</c> value of a domain head, OU or site ([MS-GPOL] 2.2.2; design note
/// docs/dev/k-computers-and-gpo.md 5.1). Entries are kept in the order of the value: the first entry has the highest link
/// order number (lowest precedence), the last one is link order 1. Entries of other domains and entries that cannot be
/// parsed are kept byte for byte; a new entry is written as GPMC and Samba write it:
/// <c>[LDAP://cn={GUID},cn=policies,cn=system,&lt;domain DN&gt;;&lt;options&gt;]</c>. Every operation returns a new value
/// (the instance is immutable); <see cref="ToAttributeValue"/> is null when no entry is left (the attribute is cleared).
/// </summary>
public sealed class GpLinkValue
{
    private readonly IReadOnlyList<Entry> _entries;

    private GpLinkValue(string domainDn, IReadOnlyList<Entry> entries)
    {
        DomainDn = domainDn;
        _entries = entries;
    }

    /// <summary>The domain whose GPOs this value links (entries naming GPOs of other domains are foreign).</summary>
    public string DomainDn { get; }

    /// <summary>Links to GPOs of <see cref="DomainDn"/> in value order, with their link order (1 = highest precedence).</summary>
    public IReadOnlyList<GpLinkEntry> Links
    {
        get
        {
            var result = new List<GpLinkEntry>();
            for (var i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].GpoId is { } id)
                {
                    result.Add(new GpLinkEntry(id, _entries[i].Options, _entries.Count - i));
                }
            }

            return result;
        }
    }

    /// <summary>Number of entries of the value (own, foreign and unparsed), i.e. the highest link order.</summary>
    public int Count => _entries.Count;

    /// <summary>Parses a value; null, empty and a single space (what some tools leave behind) are "no links".</summary>
    public static GpLinkValue Parse(string? value, string domainDn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domainDn);
        var domain = DistinguishedName.Parse(domainDn);
        var policies = domain.Child("CN", "System").Child("CN", "Policies");
        var entries = new List<Entry>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return new GpLinkValue(domain.ToString(), entries);
        }

        var position = 0;
        while (position < value.Length)
        {
            var open = value.IndexOf('[', position);
            if (open < 0)
            {
                Unparsed(value[position..]);
                break;
            }

            if (open > position)
            {
                Unparsed(value[position..open]);
            }

            var close = value.IndexOf(']', open + 1);
            if (close < 0)
            {
                Unparsed(value[open..]);
                break;
            }

            var raw = value[open..(close + 1)];
            position = close + 1;
            entries.Add(ParseEntry(raw, policies));
        }

        return new GpLinkValue(domain.ToString(), entries);

        void Unparsed(string text)
        {
            if (text.Trim().Length > 0)
            {
                entries.Add(new Entry(text, null, 0));
            }
        }
    }

    /// <summary>The DN of a GPO's container in a domain.</summary>
    public static string GpoDn(Guid gpoId, string domainDn) =>
        DistinguishedName.Parse(domainDn).Child("CN", "System").Child("CN", "Policies").Child("CN", SysvolPaths.FolderName(gpoId)).ToString();

    /// <summary>True when the value links <paramref name="gpoId"/>.</summary>
    public bool Contains(Guid gpoId) => _entries.Any(e => e.GpoId == gpoId);

    /// <summary>The link of <paramref name="gpoId"/>, or null.</summary>
    public GpLinkEntry? Find(Guid gpoId) => Links.FirstOrDefault(l => l.GpoId == gpoId);

    /// <summary>
    /// Adds a link. Without <paramref name="order"/> the new link gets the lowest precedence (it is put first in the value,
    /// as GPMC and <c>New-GPLink</c> do); with an order it is placed so that it has that link order.
    /// </summary>
    /// <exception cref="InvalidOperationException">The GPO is already linked.</exception>
    public GpLinkValue Insert(Guid gpoId, int options, int? order = null)
    {
        if (Contains(gpoId))
        {
            throw new InvalidOperationException("The GPO is already linked to this container.");
        }

        var entry = new Entry(Format(gpoId, DomainDn, options), gpoId, options);
        var list = _entries.ToList();
        var target = order is { } o ? Math.Clamp(o, 1, list.Count + 1) : list.Count + 1;
        list.Insert(list.Count + 1 - target, entry);
        return new GpLinkValue(DomainDn, list);
    }

    /// <summary>Removes the link of <paramref name="gpoId"/> (no change when it is not linked).</summary>
    public GpLinkValue Remove(Guid gpoId) => new(DomainDn, [.. _entries.Where(e => e.GpoId != gpoId)]);

    /// <summary>Moves the link of <paramref name="gpoId"/> to link order <paramref name="order"/> (1 = highest precedence).</summary>
    /// <exception cref="InvalidOperationException">The GPO is not linked.</exception>
    public GpLinkValue MoveTo(Guid gpoId, int order)
    {
        var list = _entries.ToList();
        var index = list.FindIndex(e => e.GpoId == gpoId);
        if (index < 0)
        {
            throw new InvalidOperationException("The GPO is not linked to this container.");
        }

        var entry = list[index];
        list.RemoveAt(index);
        var target = Math.Clamp(order, 1, list.Count + 1);
        list.Insert(list.Count + 1 - target, entry);
        return new GpLinkValue(DomainDn, list);
    }

    /// <summary>Sets the enforced and/or the disabled option of a link (other option bits are kept).</summary>
    /// <exception cref="InvalidOperationException">The GPO is not linked.</exception>
    public GpLinkValue SetOptions(Guid gpoId, bool? enforced = null, bool? enabled = null)
    {
        var list = _entries.ToList();
        var index = list.FindIndex(e => e.GpoId == gpoId);
        if (index < 0)
        {
            throw new InvalidOperationException("The GPO is not linked to this container.");
        }

        var options = list[index].Options;
        if (enforced is { } e)
        {
            options = e ? options | GroupPolicyLink.EnforcedOption : options & ~GroupPolicyLink.EnforcedOption;
        }

        if (enabled is { } on)
        {
            options = on ? options & ~GroupPolicyLink.DisabledOption : options | GroupPolicyLink.DisabledOption;
        }

        if (options != list[index].Options)
        {
            list[index] = list[index] with { Raw = WithOptions(list[index].Raw, options), Options = options };
        }

        return new GpLinkValue(DomainDn, list);
    }

    /// <summary>The value to write: the entries concatenated, or null when none is left (the attribute is cleared).</summary>
    public string? ToAttributeValue()
    {
        if (_entries.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var entry in _entries)
        {
            sb.Append(entry.Raw);
        }

        return sb.ToString();
    }

    /// <summary>A new entry as GPMC writes it.</summary>
    public static string Format(Guid gpoId, string domainDn, int options) => string.Create(
        CultureInfo.InvariantCulture,
        $"[LDAP://cn={SysvolPaths.FolderName(gpoId)},cn=policies,cn=system,{DistinguishedName.Parse(domainDn)};{options}]");

    /// <summary>For diagnostics: counts only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{_entries.Count} entries, {Links.Count} own links");

    private static Entry ParseEntry(string raw, DistinguishedName policies)
    {
        var body = raw[1..^1];
        var separator = body.LastIndexOf(';');
        if (separator < 0 || !int.TryParse(body.AsSpan(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var options))
        {
            return new Entry(raw, null, 0);
        }

        var path = body[..separator];
        if (path.StartsWith("LDAP://", StringComparison.OrdinalIgnoreCase))
        {
            path = path[7..];
        }

        if (DistinguishedName.TryParse(path, out var dn) && !dn.IsRoot && dn.Parent is { } parent && parent == policies
            && string.Equals(dn.Leaf.Type, "CN", StringComparison.OrdinalIgnoreCase)
            && GroupPolicyLinks.TryParseGpoId(dn.Name, out var id))
        {
            return new Entry(raw, id, options);
        }

        // A GPO of another domain (or something else): kept as it is.
        return new Entry(raw, null, options);
    }

    private static string WithOptions(string raw, int options)
    {
        var separator = raw.LastIndexOf(';');
        return raw[..(separator + 1)] + options.ToString(CultureInfo.InvariantCulture) + "]";
    }

    /// <summary>One bracketed entry (or an unparsed fragment) of the value.</summary>
    private sealed record Entry(string Raw, Guid? GpoId, int Options);
}

/// <summary>A link of <see cref="GpLinkValue"/> to a GPO of its domain.</summary>
/// <param name="GpoId">The GPO.</param>
/// <param name="Options">1 = disabled, 2 = enforced.</param>
/// <param name="Order">Link order (1 = highest precedence).</param>
public sealed record GpLinkEntry(Guid GpoId, int Options, int Order)
{
    public bool Enforced => (Options & GroupPolicyLink.EnforcedOption) != 0;

    public bool Enabled => (Options & GroupPolicyLink.DisabledOption) == 0;
}
