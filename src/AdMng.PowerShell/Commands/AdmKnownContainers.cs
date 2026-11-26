namespace AdMng.PowerShell.Commands;

/// <summary>
/// The containers (OUs, containers, the domain) that this window's commands have already returned, kept in memory so that
/// <c>-SearchBase</c> can complete them (docs/dev/r1-powershell-window.md 12.2): a completion never reads the directory.
/// A bounded list owned by one window and gone with it; it is not written anywhere (CLAUDE.md rule 1) and holds only the
/// distinguished names the window showed as results.
/// </summary>
public sealed class AdmKnownContainers
{
    /// <summary>How many distinguished names are remembered; the oldest are forgotten first.</summary>
    public const int Capacity = 256;

    private readonly Lock _gate = new();
    private readonly List<string> _order = [];
    private readonly HashSet<string> _set = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remembers a container DN (a DN seen again moves to the newest place).</summary>
    public void Remember(string distinguishedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distinguishedName);
        lock (_gate)
        {
            if (!_set.Add(distinguishedName))
            {
                var index = _order.FindIndex(dn => string.Equals(dn, distinguishedName, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    _order.RemoveAt(index);
                }
            }

            _order.Add(distinguishedName);
            while (_order.Count > Capacity)
            {
                _set.Remove(_order[0]);
                _order.RemoveAt(0);
            }
        }
    }

    /// <summary>The remembered DNs, the newest first.</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            var copy = _order.ToArray();
            Array.Reverse(copy);
            return copy;
        }
    }
}
