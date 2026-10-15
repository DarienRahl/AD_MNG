using System.Globalization;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// The container moves and renames of one cycle (section 10.7). Each moved or renamed container is recorded with its DN
/// at the start of the cycle (its stored snapshot DN) and its new live DN. A DN of the start of the cycle is rewritten
/// once, onto the new DN of its <b>deepest</b> recorded ancestor – never through a chain of rewrites, so a shift (B→C,
/// then A→B) or a swap of names in one poll puts A's children under B, not C, whatever order the server returned.
/// The store gets <see cref="StoreRewrites"/>, which its sequential suffix replacement applies with the same result
/// (descendants keep their USN – AD does not bump them). <see cref="Original"/> maps a DN the store already rewrote back
/// to the DN of the start of the cycle (PreviousDn of a child that left a renamed container).
/// </summary>
internal sealed class DnRewriter
{
    /// <summary>Parent of the temporary DNs of a two-phase store rewrite; no directory DN is ever below it.</summary>
    private static readonly DistinguishedName TemporaryRoot = DistinguishedName.Parse("DC=admng-rewrite,DC=invalid");

    private readonly List<Entry> _rewrites = [];

    /// <summary>The number of recorded container rewrites.</summary>
    public int Count => _rewrites.Count;

    /// <summary>Records a container: <paramref name="oldDn"/> is its stored DN from the start of the cycle.</summary>
    public void Add(string oldDn, string newDn, Guid? container = null)
    {
        if (DistinguishedName.TryParse(oldDn, out var before) && DistinguishedName.TryParse(newDn, out var after)
            && !before.IsRoot && !after.IsRoot && before != after)
        {
            _rewrites.Add(new Entry(before, after, container));
        }
    }

    /// <summary>
    /// The DN a DN of the start of the cycle has now: re-based onto the new DN of its deepest recorded (strict)
    /// ancestor; DNs below no recorded container are returned unchanged.
    /// </summary>
    public string Rewrite(string dn)
    {
        ArgumentNullException.ThrowIfNull(dn);
        if (_rewrites.Count == 0 || !DistinguishedName.TryParse(dn, out var parsed))
        {
            return dn;
        }

        var deepest = _rewrites.Where(r => parsed.IsDescendantOf(r.Old)).MaxBy(r => r.Old.Rdns.Count);
        return deepest is null ? dn : Rebase(parsed, deepest.Old, deepest.New).ToString();
    }

    /// <summary>
    /// The DN of the start of the cycle of a DN the store already rewrote: re-based from the new DN of its deepest
    /// recorded ancestor back onto that container's old DN. Unchanged when no recorded new DN is an ancestor, or when
    /// that new DN was <see cref="MarkOccupied">occupied</see> by an object that did not move (its former children were
    /// never rewritten, so the mapping would be ambiguous).
    /// </summary>
    public string Original(string dn)
    {
        ArgumentNullException.ThrowIfNull(dn);
        if (_rewrites.Count == 0 || !DistinguishedName.TryParse(dn, out var parsed))
        {
            return dn;
        }

        var deepest = _rewrites.Where(r => parsed.IsDescendantOf(r.New)).MaxBy(r => r.New.Rdns.Count);
        return deepest is null || deepest.Occupied ? dn : Rebase(parsed, deepest.New, deepest.Old).ToString();
    }

    /// <summary>The recorded rewrites whose new DN must be checked for a previous occupant (with their container GUID).</summary>
    public IReadOnlyList<(string NewDn, Guid? Container)> NewDns => _rewrites.Select(r => (r.New.ToString(), r.Container)).ToList();

    /// <summary>True when <paramref name="container"/> is a recorded (moved or renamed) container.</summary>
    public bool IsRecorded(Guid container) => _rewrites.Any(r => r.Container == container);

    /// <summary>
    /// Marks <paramref name="newDn"/> as occupied at the start of the cycle by an object that did not move (deleted,
    /// left the scope): <see cref="Original"/> then leaves DNs below it unchanged.
    /// </summary>
    public void MarkOccupied(string newDn)
    {
        if (DistinguishedName.TryParse(newDn, out var parsed))
        {
            foreach (var rewrite in _rewrites.Where(r => r.New == parsed))
            {
                rewrite.Occupied = true;
            }
        }
    }

    /// <summary>
    /// The <see cref="DnRewrite"/> list of the rewrites recorded from index <paramref name="from"/> on, for a store that
    /// applies them one after the other: deepest old DN first; when a new DN lies at or below another rewrite's old DN
    /// (shift or swap of names), in two phases through unique temporary DNs below <c>DC=admng-rewrite,DC=invalid</c>, so
    /// every stored DN is re-based exactly once, by its deepest recorded ancestor.
    /// </summary>
    public IReadOnlyList<DnRewrite> StoreRewrites(int from)
    {
        var pending = _rewrites.Skip(from).OrderByDescending(r => r.Old.Rdns.Count).ToList();
        var conflict = pending.Any(r => pending.Any(o => r.New == o.Old || r.New.IsDescendantOf(o.Old)));
        if (!conflict)
        {
            return pending.Select(r => new DnRewrite(r.Old.ToString(), r.New.ToString())).ToList();
        }

        var temporary = pending.Select((_, i) => TemporaryRoot.Child("OU", "t" + i.ToString(CultureInfo.InvariantCulture))).ToList();
        var result = new List<DnRewrite>(pending.Count * 2);
        result.AddRange(pending.Select((r, i) => new DnRewrite(r.Old.ToString(), temporary[i].ToString())));
        result.AddRange(pending.Select((r, i) => new DnRewrite(temporary[i].ToString(), r.New.ToString())));
        return result;
    }

    /// <summary>Re-bases <paramref name="dn"/> (below <paramref name="oldBase"/>) onto <paramref name="newBase"/>.</summary>
    public static DistinguishedName Rebase(DistinguishedName dn, DistinguishedName oldBase, DistinguishedName newBase)
    {
        var relative = dn.Rdns.Count - oldBase.Rdns.Count;
        var result = newBase;
        for (var i = relative - 1; i >= 0; i--)
        {
            result = result.Child(dn.Rdns[i].Type, dn.Rdns[i].Value);
        }

        return result;
    }

    private sealed class Entry(DistinguishedName old, DistinguishedName @new, Guid? container)
    {
        public DistinguishedName Old { get; } = old;

        public DistinguishedName New { get; } = @new;

        public Guid? Container { get; } = container;

        public bool Occupied { get; set; }
    }
}
