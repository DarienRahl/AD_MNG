using System.Windows.Controls;
using System.Windows.Input;
using AdMng.App.Behaviors;
using AdMng.App.Localization;
using AdMng.Core.Model;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Services;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Views;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.App.Views;

/// <summary>Context menu of a navigation tree node, built when it opens (the actions depend on the node type).</summary>
internal static class TreeMenu
{
    public static IReadOnlyList<Control> Create(TreeNodeViewModel node, ShellViewModel shell, ViewLibrary views, IDialogService dialogs)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(shell);
        var items = new List<Control>();
        switch (node)
        {
            case ContainerNodeViewModel container:
                items.Add(Item(L.Commands.OpenInNewTab, new AsyncRelayCommand(() =>
                    shell.OpenLocationInNewTabAsync(ListLocation.ForContainer(container.Dn, container.Name, container.Icon)))));
                items.Add(Item(L.Commands.Refresh, container.RefreshCommand));
                if (container is { Kind: ObjectKind.Domain, IsPinnedEntry: false })
                {
                    // As in ADUC: the domain root offers "Change Domain Controller...".
                    items.Add(CommandItem("session.changeDc", shell));
                }

                items.Add(new Separator());
                items.Add(NewMenu(shell, container));
                AddGroupPolicyItems(items, shell, container);
                items.Add(new Separator());
                if (container.IsPinnedEntry)
                {
                    items.Add(Item(L.Commands.UnpinContainer, shell.UnpinContainerCommand, container));
                    items.Add(Item(L.Common.MoveUp, shell.MovePinnedUpCommand, container));
                    items.Add(Item(L.Common.MoveDown, shell.MovePinnedDownCommand, container));
                }
                else
                {
                    var pin = Item(L.Commands.PinContainer, new AsyncRelayCommand(() => views.PinAsync(container.Dn, container.Name)));
                    pin.IsEnabled = !views.IsPinned(container.Dn);
                    items.Add(pin);
                }

                items.Add(new Separator());
                items.Add(Item(L.Commands.Properties, new AsyncRelayCommand(() => dialogs.ShowPropertiesAsync(container.Dn))));
                break;

            case ViewNodeViewModel { SavedView: not null } view:
                items.Add(Item(L.Commands.EditView, shell.EditViewCommand, view));
                items.Add(Item(L.Commands.DeleteView, shell.DeleteViewCommand, view));
                items.Add(new Separator());
                items.Add(Item(L.Common.MoveUp, shell.MoveViewUpCommand, view));
                items.Add(Item(L.Common.MoveDown, shell.MoveViewDownCommand, view));
                break;

            case GpoNodeViewModel gpo:
                AddGpoItems(items, gpo);
                break;

            case GroupPolicyObjectsNodeViewModel gpoRoot:
                items.Add(Item(L.GroupPolicy.OpenCommand, shell.OpenGroupPolicyObjectsCommand));
                items.Add(Item(L.GroupPolicy.NewCommand, new AsyncRelayCommand(() => gpoRoot.GroupPolicy.Actions.NewAsync())));
                items.Add(Item(L.GroupPolicy.RestoreCommand, new AsyncRelayCommand(gpoRoot.GroupPolicy.Actions.RestoreAsync)));
                items.Add(new Separator());
                items.Add(Item(L.GroupPolicy.RefreshCommand, gpoRoot.RefreshCommand));
                break;

            case WmiFiltersNodeViewModel wmi:
                items.Add(Item(L.GroupPolicy.OpenCommand, shell.OpenWmiFiltersCommand));
                items.Add(Item(L.GroupPolicy.NewCommand, new AsyncRelayCommand(() => wmi.GroupPolicy.Actions.NewWmiFilterAsync())));
                break;

            case SitesNodeViewModel sites:
                items.Add(Item(L.GroupPolicy.RefreshCommand, sites.RefreshCommand));
                break;

            case SiteNodeViewModel site:
                items.Add(Item(L.GroupPolicy.ContainerPoliciesCommand, shell.OpenContainerPoliciesCommand, site.Dn));
                items.Add(Item(L.GroupPolicy.NewLinkedCommand, shell.NewLinkedGpoCommand, site.Dn));
                items.Add(Item(L.GroupPolicy.LinkExistingCommand, shell.LinkExistingGpoCommand, site.Dn));
                break;

            case FolderNodeViewModel folder when ReferenceEquals(folder, shell.Tree.ViewsNode):
                items.Add(CommandItem("view.importViews", shell));
                items.Add(CommandItem("view.exportViews", shell));
                break;
        }

        return items;
    }

    /// <summary>
    /// Group Policy of a domain or OU (K2): "Zasady grupy", "Utwórz GPO w tej domenie i połącz je tutaj…", "Połącz istniejący
    /// GPO…", "Zablokuj dziedziczenie" (as GPMC) - when the window has the Group Policy services.
    /// </summary>
    private static void AddGroupPolicyItems(List<Control> items, ShellViewModel shell, ContainerNodeViewModel container)
    {
        if (!shell.IsGroupPolicyAvailable || container.Kind is not (ObjectKind.Domain or ObjectKind.OrganizationalUnit))
        {
            return;
        }

        items.Add(new Separator());
        items.Add(Item(L.GroupPolicy.ContainerPoliciesCommand, shell.OpenContainerPoliciesCommand, container.Dn));
        items.Add(Item(L.GroupPolicy.NewLinkedCommand, shell.NewLinkedGpoCommand, container.Dn));
        items.Add(Item(L.GroupPolicy.LinkExistingCommand, shell.LinkExistingGpoCommand, container.Dn));
        items.Add(Item(L.GroupPolicy.BlockInheritanceCommand, shell.ToggleBlockInheritanceCommand, container.Dn));
    }

    /// <summary>A GPO node: Stan GPO, Kopia zapasowa…, Kopiuj, Zmień nazwę, Usuń, Porównaj z…, Historia w Watchdogu.</summary>
    private static void AddGpoItems(List<Control> items, GpoNodeViewModel node)
    {
        var actions = node.GroupPolicy.Actions;
        var gpo = node.Gpo;
        var open = Item(L.GroupPolicy.OpenCommand, new RelayCommand(() => actions.OpenGpo(gpo.Id, gpo.DisplayName)));
        open.FontWeight = System.Windows.FontWeights.SemiBold;
        items.Add(open);
        items.Add(Item(L.GroupPolicy.GpoStatusCommand, new RelayCommand(() => actions.OpenGpo(gpo.Id, gpo.DisplayName, GpoDocumentTab.Status))));
        items.Add(new Separator());
        items.Add(Item(L.GroupPolicy.BackupCommand, new AsyncRelayCommand(() => actions.BackupAsync([gpo]))));
        items.Add(Item(L.GroupPolicy.CopyCommand, new AsyncRelayCommand(() => actions.CopyAsync(gpo))));
        items.Add(Item(L.GroupPolicy.RenameCommand, new AsyncRelayCommand(() => actions.RenameAsync(gpo))));
        var delete = Item(L.GroupPolicy.DeleteCommand, new AsyncRelayCommand(() => actions.DeleteAsync(gpo)));
        delete.IsEnabled = !gpo.IsDefault;
        items.Add(delete);
        items.Add(new Separator());
        items.Add(Item(L.GroupPolicy.CompareWithCommand, new AsyncRelayCommand(() => actions.CompareAsync(gpo))));
        var history = Item(L.GroupPolicy.HistoryCommand, new AsyncRelayCommand(() => actions.ShowHistoryAsync(gpo)));
        history.IsEnabled = actions.CanShowHistory;
        items.Add(history);
    }

    private static MenuItem NewMenu(ShellViewModel shell, ContainerNodeViewModel container)
    {
        var menu = new MenuItem();
        menu.SetBinding(HeaderedItemsControl.HeaderProperty, new TrBinding(L.Shell.MenuNew));

        // Creates in the right-clicked container itself, not in the current tab's (it may show another container).
        // Its permissions are read now: classes the operator may not create there are disabled, with the reason.
        menu.Items.Add(NewItem(shell, L.Commands.NewUser, shell.NewUserCommand, ObjectKind.User, container.Dn));
        menu.Items.Add(NewItem(shell, L.Commands.NewGroup, shell.NewGroupCommand, ObjectKind.Group, container.Dn));
        menu.Items.Add(NewItem(shell, L.Commands.NewOrganizationalUnit, shell.NewOrganizationalUnitCommand, ObjectKind.OrganizationalUnit, container.Dn));
        menu.Items.Add(NewItem(shell, L.Commands.NewComputer, shell.NewComputerCommand, ObjectKind.Computer, container.Dn));
        menu.Items.Add(NewItem(shell, L.Commands.NewContact, shell.NewContactCommand, ObjectKind.Contact, container.Dn));
        shell.LoadCreatePermissionsAsync(container.Dn).Forget();
        return menu;
    }

    private static MenuItem NewItem(ShellViewModel shell, string titleKey, ICommand command, ObjectKind kind, string containerDn)
    {
        var item = Item(titleKey, command, containerDn);
        DisabledReasonToolTip.Apply(item, titleKey, shortcut: null, () => shell.CreateBlockedReason(kind, containerDn));
        return item;
    }

    private static MenuItem CommandItem(string id, ShellViewModel shell)
    {
        var item = new MenuItem();
        CommandMenu.SetId(item, id);
        if (shell.CommandPalette.Find(id) is { } descriptor)
        {
            CommandMenu.Apply(item, descriptor);
        }

        return item;
    }

    private static MenuItem Item(string titleKey, ICommand command, object? parameter = null)
    {
        var item = new MenuItem { Command = command, CommandParameter = parameter };
        item.SetBinding(HeaderedItemsControl.HeaderProperty, new TrBinding(titleKey));
        return item;
    }
}
