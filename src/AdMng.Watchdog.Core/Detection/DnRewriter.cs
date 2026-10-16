using AdMng.Core.Ldap;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// The container moves and renames of one cycle (section 10.7). Containers are processed in ascending depth of their
/// old DN; each recorded <see cref="DnRewrite"/> is applied (in order) to later old DNs before diffing, so the children of
/// a renamed OU are not reported as moved. The store applies the same rewrites, in order, to stored descendants and
/// member DNs (descendants keep their USN – AD does not bump them).
/// </summary>
internal sealed class DnRewriter
{
    private readonly List<(DistinguishedName Old, DistinguishedName New)> _rewrites = [];

    /// <summary>The rewrites recorded so far (cumulative, in order).</summary>
    public IReadOnlyList<DnRewrite> Rewrites => _rewrites.Select(r => new DnRewrite(r.Old.ToString(), r.New.ToString())).ToList();

    /// <summary>Records a container whose (already rewritten) old DN differs from its new DN.</summary>
    public void Add(string oldDn, string newDn)
    {
        if (DistinguishedName.TryParse(oldDn, out var before) && DistinguishedName.TryParse(newDn, out var after) && before != after)
        {
            _rewrites.Add((before, after));
        }
    }

    /// <summary>Applies the rewrites in order; DNs not below a rewritten container are returned unchanged.</summary>
    public string Rewrite(string dn)
    {
        ArgumentNullException.ThrowIfNull(dn);
        if (_rewrites.Count == 0 || !DistinguishedName.TryParse(dn, out var current))
        {
            return dn;
        }

        var changed = false;
        foreach (var (before, after) in _rewrites)
        {
            if (current.IsDescendantOf(before))
            {
                current = Rebase(current, before, after);
                changed = true;
            }
        }

        return changed ? current.ToString() : dn;
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
}
