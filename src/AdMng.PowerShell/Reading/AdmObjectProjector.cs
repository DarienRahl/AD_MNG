using System.Management.Automation;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.PowerShell.Commands;

namespace AdMng.PowerShell.Reading;

/// <summary>A directory object as a script object, and the mandatory attributes the server did not return (a read restriction).</summary>
internal sealed record AdmProjected(PSObject Object, IReadOnlyList<string> HiddenAttributes);

/// <summary>
/// Turns a <see cref="DirectoryObject"/> into the typed object a script gets (docs/dev/r1-powershell-window.md 12.2): a
/// <c>PSObject</c> with the type names <c>AdMng.User</c> / <c>AdMng.Group</c> / <c>AdMng.Computer</c> / <c>AdMng.Object</c>
/// (the format data of <see cref="Formats.AdmFormats"/> keys on them), the properties <c>DistinguishedName</c>, <c>Name</c> and
/// <c>ObjectKind</c> on every object, the attributes under their LDAP names with the values of <see cref="AdmValueConverter"/>, and
/// the facts a script wants as ordinary properties (<c>Enabled</c>, <c>LockedOut</c>, <c>GroupScope</c>, ...). Ranged values
/// (<c>member;range=0-1499</c>) are completed with ranged retrieval, secrets that are never shown are masked, and a mandatory
/// attribute that did not come back is reported as hidden.
/// </summary>
internal sealed class AdmObjectProjector
{
    private readonly IDirectoryReader _reader;
    private readonly AdmKnownContainers? _known;

    // The mandatory attributes of a class set, by the classes of the object: 100,000 objects have a handful of class sets.
    private readonly Dictionary<string, IReadOnlyCollection<string>> _mandatory = new(StringComparer.OrdinalIgnoreCase);

    public AdmObjectProjector(IDirectoryReader reader, AdmKnownContainers? known)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _known = known;
    }

    /// <summary>The type names of an object of <paramref name="kind"/>, most specific first.</summary>
    public static string[] TypeNames(ObjectKind kind) => kind switch
    {
        ObjectKind.User or ObjectKind.InetOrgPerson => ["AdMng.User", "AdMng.Object"],
        ObjectKind.Computer => ["AdMng.Computer", "AdMng.Object"],
        ObjectKind.Group => ["AdMng.Group", "AdMng.Object"],
        _ => ["AdMng.Object"],
    };

    public async Task<AdmProjected> ProjectAsync(DirectoryObject obj, AdmProjection projection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(projection);
        var schema = _reader.Schema;
        var kind = obj.Kind;
        var result = new PSObject();
        result.TypeNames.Clear();
        foreach (var typeName in TypeNames(kind))
        {
            result.TypeNames.Add(typeName);
        }

        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, object? value)
        {
            if (added.Add(name))
            {
                result.Properties.Add(new PSNoteProperty(name, value));
            }
        }

        Add("DistinguishedName", obj.DistinguishedName);
        Add("Name", obj.Name);
        Add("ObjectKind", kind.ToString());
        AddComputed(obj, kind, Add);
        if (kind.IsContainer())
        {
            _known?.Remember(obj.DistinguishedName);
        }

        // Attributes in the order they were asked for (defaults first), then whatever else the server returned.
        var order = new List<string>();
        foreach (var name in projection.Request.Concat(projection.Explicit))
        {
            if (name != "*" && !order.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                order.Add(name);
            }
        }

        foreach (var name in obj.Attributes.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!order.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                order.Add(name);
            }
        }

        var explicitNames = new HashSet<string>(projection.Explicit, StringComparer.OrdinalIgnoreCase);
        foreach (var name in order)
        {
            if (!obj.TryGet(name, out var values))
            {
                if (explicitNames.Contains(name))
                {
                    Add(name, schema.FindAttribute(name) is { IsSingleValued: false } ? Array.Empty<string>() : null);
                }

                continue;
            }

            IReadOnlyList<string>? ranged = null;
            if (values.Name.Contains(";range=", StringComparison.OrdinalIgnoreCase) && !values.Name.EndsWith("-*", StringComparison.Ordinal))
            {
                ranged = await ReadAllAsync(obj.DistinguishedName, name, cancellationToken).ConfigureAwait(false);
            }

            Add(name, AdmValueConverter.Convert(name, ranged, values, schema));
        }

        return new AdmProjected(result, Hidden(obj, projection, schema));
    }

    /// <summary>The facts a script wants as properties: Enabled, LockedOut and the flag names of an account, the scope and category of a group.</summary>
    private static void AddComputed(DirectoryObject obj, ObjectKind kind, Action<string, object?> add)
    {
        if (kind.IsAccount())
        {
            var uac = obj.GetInt32(Attr.UserAccountControl);
            add("Enabled", uac is null ? null : (uac.Value & (int)UserAccountControl.AccountDisable) == 0);
            if (kind is ObjectKind.User or ObjectKind.InetOrgPerson)
            {
                add("LockedOut", obj.IsLockedOut);
            }

            add("UserAccountControlFlags", uac is null ? null : Core.Formatting.AttributeFormatter.GetUserAccountControlFlagNames(uac.Value).ToArray());
        }
        else if (kind == ObjectKind.Group)
        {
            if (obj.GetInt32(Attr.GroupType) is { } groupType)
            {
                add("GroupScope", GroupTypes.GetScope(groupType).ToString());
                add("GroupCategory", GroupTypes.IsSecurity(groupType) ? "Security" : "Distribution");
                add("GroupTypeFlags", Core.Formatting.AttributeFormatter.GetGroupTypeFlagNames(groupType).ToArray());
            }
        }
    }

    /// <summary>
    /// A mandatory attribute (the schema says every object of the class has it) that was asked for and did not come back:
    /// the account may not read it - the DC returns an unreadable attribute as if it were empty.
    /// </summary>
    private IReadOnlyList<string> Hidden(DirectoryObject obj, AdmProjection projection, ISchemaCatalog schema)
    {
        var classes = obj.GetStrings(Attr.ObjectClass);
        if (classes.Count == 0)
        {
            return [];
        }

        var key = string.Join('\n', classes);
        if (!_mandatory.TryGetValue(key, out var mandatory))
        {
            mandatory = schema.GetMandatoryAttributes(classes);
            _mandatory[key] = mandatory;
        }

        return [.. projection.Request.Where(name => name != "*" && mandatory.Contains(name) && !obj.Has(name))];
    }

    private async Task<IReadOnlyList<string>> ReadAllAsync(string distinguishedName, string attribute, CancellationToken cancellationToken)
    {
        var all = new List<string>();
        await foreach (var value in _reader.GetAllValuesAsync(distinguishedName, attribute, cancellationToken).ConfigureAwait(false))
        {
            all.Add(value);
        }

        return all;
    }
}
