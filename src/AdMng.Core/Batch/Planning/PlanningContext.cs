using System.Collections.Concurrent;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Schema;

namespace AdMng.Core.Batch.Planning;

/// <summary>
/// State of one planning run: the session, options and per-run caches of referenced objects (groups, containers,
/// managers), effective permissions and schema lookups. Lives only for the duration of one PlanAsync call, so the
/// "no cache of directory data" rule is respected - everything is read live for this preview.
/// </summary>
internal sealed class PlanningContext
{
    /// <summary>Attributes read for referenced objects (groups, target containers, DN values).</summary>
    private static readonly string[] ReferenceAttributes =
        [Attr.ObjectClass, Attr.DistinguishedName, Attr.Name, Attr.ObjectSid, Attr.GroupType];

    /// <summary>Attribute option of a partial (ranged) value list, e.g. <c>member;range=0-1499</c>.</summary>
    private const string RangeOption = ";range=";

    private readonly ConcurrentDictionary<string, Lazy<Task<EffectivePermissions>>> _permissions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<DirectoryObject?>>> _references = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ClassAttributeSets> _classSets = new(StringComparer.OrdinalIgnoreCase);

    public PlanningContext(IDirectorySession session, PlanningOptions options, CancellationToken cancellationToken)
    {
        Session = session;
        Options = options;
        CancellationToken = cancellationToken;
        HasSchema = session.Schema.Attributes.Count > 0;
    }

    public IDirectorySession Session { get; }

    public ISchemaCatalog Schema => Session.Schema;

    public PlanningOptions Options { get; }

    public CancellationToken CancellationToken { get; }

    /// <summary>False when the session has no schema metadata - schema validation is skipped then.</summary>
    public bool HasSchema { get; }

    public int MaxDegreeOfParallelism => Math.Clamp(Options.MaxDegreeOfParallelism, 1, 64);

    /// <summary>Normalized cache key of a DN (escaping and spacing normalized; the caches compare case-insensitively).</summary>
    public static string Key(string dn) => DistinguishedName.TryParse(dn, out var parsed) ? parsed.ToString() : dn.Trim();

    /// <summary>
    /// Effective permissions of the bound identity on an object, or <see cref="EffectivePermissions.Unknown"/> when
    /// checking is disabled or the server cannot evaluate them (the server then decides at execution time).
    /// </summary>
    public Task<EffectivePermissions> GetPermissionsAsync(string dn)
    {
        if (!Options.CheckPermissions)
        {
            return Task.FromResult(EffectivePermissions.Unknown);
        }

        return _permissions.GetOrAdd(Key(dn), _ => new Lazy<Task<EffectivePermissions>>(() => LoadPermissionsAsync(dn))).Value;
    }

    /// <summary>Reads a referenced object once per planning run (null when it does not exist).</summary>
    public Task<DirectoryObject?> GetReferenceAsync(string dn) =>
        _references.GetOrAdd(Key(dn), _ => new Lazy<Task<DirectoryObject?>>(
            () => Session.GetAsync(dn, ReferenceAttributes, CancellationToken))).Value;

    /// <summary>
    /// Reads the remaining values of attributes the server returned only partially. AD returns at most MaxValRange
    /// (1500) values of a multi-valued attribute per read, as <c>member;range=0-1499</c>; planning on such a truncated
    /// list would turn removals of later values into silent no-ops and make a clear (compare-and-swap) delete only the
    /// first chunk. Binary attributes are left as they are (they cannot be edited in a batch).
    /// </summary>
    public async Task<DirectoryObject> CompleteRangedValuesAsync(DirectoryObject obj)
    {
        List<AttributeValues>? completed = null;
        foreach (var values in obj.Attributes.Values)
        {
            var name = values.Name;
            var range = name.IndexOf(RangeOption, StringComparison.OrdinalIgnoreCase);
            if (range <= 0 || name.EndsWith("-*", StringComparison.Ordinal))
            {
                continue;
            }

            var baseName = name[..name.IndexOf(';', StringComparison.Ordinal)];
            if (Schema.IsBinary(baseName))
            {
                continue;
            }

            var all = new List<object>();
            await foreach (var value in Session.GetAllValuesAsync(obj.DistinguishedName, baseName, CancellationToken).ConfigureAwait(false))
            {
                all.Add(value);
            }

            (completed ??= []).Add(new AttributeValues(baseName, all));
        }

        return completed is null ? obj : obj.With(completed);
    }

    /// <summary>Allowed and mandatory attributes for a combination of object classes (cached per combination).</summary>
    public ClassAttributeSets GetClassAttributes(IReadOnlyList<string> objectClasses) =>
        _classSets.GetOrAdd(string.Join('|', objectClasses), _ => new ClassAttributeSets(
            new HashSet<string>(Schema.GetAllowedAttributes(objectClasses), StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(Schema.GetMandatoryAttributes(objectClasses), StringComparer.OrdinalIgnoreCase)));

    private async Task<EffectivePermissions> LoadPermissionsAsync(string dn)
    {
        try
        {
            return await Session.GetEffectivePermissionsAsync(dn, CancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryException)
        {
            return EffectivePermissions.Unknown;
        }
    }
}

/// <summary>Schema attribute sets of one objectClass combination.</summary>
internal sealed record ClassAttributeSets(IReadOnlySet<string> Allowed, IReadOnlySet<string> Mandatory);
