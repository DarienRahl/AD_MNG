using System.Windows.Controls;
using System.Windows.Input;
using AdMng.App.Behaviors;
using AdMng.App.Localization;
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
                items.Add(new Separator());
                items.Add(NewMenu(shell, container));
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

            case FolderNodeViewModel folder when ReferenceEquals(folder, shell.Tree.ViewsNode):
                items.Add(CommandItem("view.importViews", shell));
                items.Add(CommandItem("view.exportViews", shell));
                break;
        }

        return items;
    }

    private static MenuItem NewMenu(ShellViewModel shell, ContainerNodeViewModel container)
    {
        var menu = new MenuItem();
        menu.SetBinding(HeaderedItemsControl.HeaderProperty, new TrBinding(L.Shell.MenuNew));

        // Creates in the right-clicked container itself, not in the current tab's (it may show another container).
        menu.Items.Add(Item(L.Commands.NewUser, shell.NewUserCommand, container.Dn));
        menu.Items.Add(Item(L.Commands.NewGroup, shell.NewGroupCommand, container.Dn));
        menu.Items.Add(Item(L.Commands.NewOrganizationalUnit, shell.NewOrganizationalUnitCommand, container.Dn));
        menu.Items.Add(Item(L.Commands.NewComputer, shell.NewComputerCommand, container.Dn));
        menu.Items.Add(Item(L.Commands.NewContact, shell.NewContactCommand, container.Dn));
        return menu;
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
