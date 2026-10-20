using System.ComponentModel;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Navigation;

namespace AdMng.Presentation.Shell;

/// <summary>Default containers of new objects from the domain's well-known containers (wellKnownObjects).</summary>
public sealed partial class ShellViewModel
{
    /// <summary>The kinds the New commands create (their targets' create permissions are read ahead).</summary>
    private static readonly ObjectKind[] CreatableKinds =
        [ObjectKind.User, ObjectKind.Group, ObjectKind.OrganizationalUnit, ObjectKind.Computer, ObjectKind.Contact];

    /// <summary>
    /// Where a New command without an explicit parent creates <paramref name="kind"/>: the current container tab, unless
    /// it shows the domain root or the tab is not a container (search results, a view, Find results) - then users,
    /// inetOrgPersons, groups and contacts go to the well-known Users container and computers to the well-known
    /// Computers container, as the domain head's wellKnownObjects names them (redirusr / redircmp are followed, names are
    /// never guessed). Without such an entry (and for OUs) the old rule applies: the container tab, else the selected
    /// tree container, else the domain root.
    /// </summary>
    public string? DefaultCreateContainer(ObjectKind kind)
    {
        var domain = Sessions.Current?.Info.DefaultNamingContext;
        if (SelectedDocument?.Location is { Kind: ListLocationKind.Container } location && !DistinguishedName.AreEqual(location.BaseDn, domain))
        {
            return location.BaseDn;
        }

        return WellKnownTarget(kind) is { } role && Tree.WellKnown.Find(role) is { } wellKnown ? wellKnown : CurrentContainerDn;
    }

    /// <summary>The container of a New command: the explicit parent (tree context menu) or <see cref="DefaultCreateContainer"/>.</summary>
    private string? CreateTarget(ObjectKind kind, string? parentDn) =>
        string.IsNullOrWhiteSpace(parentDn) ? DefaultCreateContainer(kind) : parentDn;

    /// <summary>The distinct default targets of the New commands (their create permissions are read once they change).</summary>
    private List<string> CreateTargets()
    {
        var targets = new List<string>();
        foreach (var kind in CreatableKinds)
        {
            if (DefaultCreateContainer(kind) is { } target && !targets.Any(t => DistinguishedName.AreEqual(t, target)))
            {
                targets.Add(target);
            }
        }

        return targets;
    }

    private static WellKnownContainer? WellKnownTarget(ObjectKind kind) => kind switch
    {
        ObjectKind.User or ObjectKind.InetOrgPerson or ObjectKind.Group or ObjectKind.Contact => WellKnownContainer.Users,
        ObjectKind.Computer => WellKnownContainer.Computers,
        _ => null,
    };

    /// <summary>The well-known containers arrived (after the tree was built): the New commands' targets may have changed.</summary>
    private void OnTreePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NavigationTreeViewModel.WellKnown) && !_disposed)
        {
            RefreshCommandStates();
        }
    }
}
