namespace AdMng.Core.GroupPolicy.Diagnostics;

/// <summary>
/// The rules about which objects a GPO reaches (Full): the users and computers counted by their parent container are put
/// through the inheritance of the domain head and the OUs (blocking, enforced and disabled links as clients see them), so a
/// GPO "reaches" the objects of every container whose chain applies it.
/// </summary>
public static partial class GroupPolicyDiagnostics
{
    private static void ScopeRules(Context c)
    {
        var users = new Dictionary<Guid, long>();
        var computers = new Dictionary<Guid, long>();
        var loopbackReached = new HashSet<Guid>();
        var foldersKnown = c.Evaluates(GpoRuleGroup.Folders);
        foreach (var (parent, count) in c.Snapshot.ObjectCounts!)
        {
            if (count.Users == 0 && count.Computers == 0)
            {
                continue;
            }

            var chain = c.Inheritance.Resolve(parent, siteDn: null);
            foreach (var applied in chain)
            {
                users[applied.GpoId] = users.GetValueOrDefault(applied.GpoId) + count.Users;
                computers[applied.GpoId] = computers.GetValueOrDefault(applied.GpoId) + count.Computers;
            }

            // Loopback: a user who logs on to these computers gets the user configuration of the computers' GPOs.
            if (foldersKnown && count.Computers > 0 && LoopbackOn(c, chain))
            {
                loopbackReached.UnionWith(chain.Select(a => a.GpoId));
            }
        }

        foreach (var gpo in c.InScope)
        {
            var links = c.LinksOf(gpo.Id).Where(l => l.Enabled).ToList();

            // A site link reaches the objects of the site's computers, which no container count tells.
            if (links.Count == 0 || links.Any(l => l.Kind == GroupPolicySomKind.Site) || c.VersionOf(gpo) is not { } version)
            {
                continue;
            }

            var reachesComputers = computers.GetValueOrDefault(gpo.Id) > 0;
            if (ComputerEnabled(gpo) && version.Computer > 0 && !reachesComputers)
            {
                c.Add(GpoFindingCodes.ComputerSettingsWithoutComputers, gpo.Id);
            }

            if (!foldersKnown)
            {
                continue;
            }

            if (UserEnabled(gpo) && version.User > 0 && users.GetValueOrDefault(gpo.Id) == 0 && !loopbackReached.Contains(gpo.Id))
            {
                c.Add(GpoFindingCodes.UserSettingsWithoutUsers, gpo.Id);
            }

            if (ComputerEnabled(gpo) && !reachesComputers && c.View(gpo.Id)?.LoopbackMode is { } mode && mode is 1 or 2)
            {
                c.Add(GpoFindingCodes.LoopbackWithoutComputers, gpo.Id,
                    settingKey: "Computer/Registry/HKLM\\" + Modeling.GroupPolicyModeler.LoopbackKey + "!" + Modeling.GroupPolicyModeler.LoopbackValueName,
                    arguments: [mode == 1 ? "Merge" : "Replace"]);
            }
        }
    }

    /// <summary>
    /// The loopback mode of a chain: the highest-precedence GPO (the last applied) whose computer configuration is enabled
    /// and whose Registry.pol sets or deletes <c>UserPolicyMode</c> decides, as the modeller does.
    /// </summary>
    private static bool LoopbackOn(Context c, IReadOnlyList<AppliedGroupPolicy> chain)
    {
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            if (c.Gpo(chain[i].GpoId) is not { } gpo || !ComputerEnabled(gpo) || c.View(gpo.Id)?.LoopbackMode is not { } mode)
            {
                continue;
            }

            return mode is 1 or 2;
        }

        return false;
    }
}
