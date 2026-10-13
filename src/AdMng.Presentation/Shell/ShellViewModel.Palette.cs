using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Shell;

/// <summary>Command descriptors (palette, menus, key bindings). Shortcuts follow ADUC where ADUC has one.</summary>
public sealed partial class ShellViewModel
{
    /// <summary>All shell commands with ids, localized title keys, categories and keyboard shortcuts.</summary>
    public IReadOnlyList<CommandDescriptor> CommandDescriptors => CommandPalette.Commands;

    private IEnumerable<CommandDescriptor> CreateCommandDescriptors()
    {
        const string obj = L.CommandCategories.Object;
        const string nav = L.CommandCategories.Navigation;
        const string create = L.CommandCategories.New;
        const string view = L.CommandCategories.View;
        const string tools = L.CommandCategories.Tools;
        const string session = L.CommandCategories.Session;
        return
        [
            new("object.refresh", L.Commands.Refresh, view, "F5", RefreshCommand),
            new("object.properties", L.Commands.Properties, obj, "Alt+Enter", PropertiesCommand),
            new("object.delete", L.Commands.Delete, obj, "Delete", DeleteCommand),
            new("object.rename", L.Commands.Rename, obj, "F2", BeginRenameCommand),
            new("object.move", L.Commands.Move, obj, "Ctrl+M", MoveCommand),
            new("object.enable", L.Commands.Enable, obj, string.Empty, EnableCommand),
            new("object.disable", L.Commands.Disable, obj, string.Empty, DisableCommand),
            new("object.unlock", L.Commands.Unlock, obj, string.Empty, UnlockCommand),
            new("object.resetPassword", L.Commands.ResetPassword, obj, string.Empty, ResetPasswordCommand),
            new("object.addToGroup", L.Commands.AddToGroup, obj, string.Empty, AddToGroupCommand),
            new("object.batchEdit", L.Commands.BatchEdit, obj, "Ctrl+Shift+B", BatchEditCommand),
            new("object.copyDn", L.Commands.CopyDn, obj, "Ctrl+Shift+C", CopyDnCommand),
            new("new.user", L.Commands.NewUser, create, "Ctrl+Shift+U", NewUserCommand),
            new("new.group", L.Commands.NewGroup, create, "Ctrl+Shift+G", NewGroupCommand),
            new("new.ou", L.Commands.NewOrganizationalUnit, create, "Ctrl+Shift+O", NewOrganizationalUnitCommand),
            new("new.computer", L.Commands.NewComputer, create, string.Empty, NewComputerCommand),
            new("new.contact", L.Commands.NewContact, create, string.Empty, NewContactCommand),
            new("nav.find", L.Commands.Find, nav, "Ctrl+F", FindCommand),
            new("nav.search", L.Commands.QuickSearch, nav, "Ctrl+E", ShowAllSearchResultsCommand),
            new("nav.back", L.Commands.Back, nav, "Alt+Left", GoBackCommand),
            new("nav.forward", L.Commands.Forward, nav, "Alt+Right", GoForwardCommand),
            new("nav.up", L.Commands.Up, nav, "Alt+Up", GoUpCommand),
            new("nav.address", L.Commands.GoToAddress, nav, "Ctrl+L", NavigateToAddressCommand),
            new("nav.newTab", L.Commands.NewTab, nav, "Ctrl+T", NewTabCommand),
            new("nav.openInNewTab", L.Commands.OpenInNewTab, nav, "Ctrl+Enter", OpenInNewTabCommand),
            new("nav.closeTab", L.Commands.CloseTab, nav, "Ctrl+W", CloseTabCommand),
            new("view.columns", L.Commands.ChooseColumns, view, string.Empty, ChooseColumnsCommand),
            new("view.export", L.Commands.ExportList, view, "Ctrl+Shift+E", ExportListCommand),
            new("view.pin", L.Commands.PinContainer, view, string.Empty, PinContainerCommand),
            new("view.saveAsView", L.Commands.SaveAsView, view, "Ctrl+Shift+S", SaveAsViewCommand),
            new("view.importViews", L.Commands.ImportViews, view, string.Empty, ImportViewsCommand),
            new("view.exportViews", L.Commands.ExportViews, view, string.Empty, ExportViewsCommand),
            new("view.advanced", L.Commands.ToggleAdvancedFeatures, view, string.Empty, ToggleAdvancedFeaturesCommand),
            new("view.details", L.Commands.ToggleDetailsPane, view, "Ctrl+D", ToggleDetailsPaneCommand),
            new("tools.palette", L.Commands.CommandPalette, tools, "Ctrl+K", CommandPalette.OpenCommand),
            new("tools.sessionLog", L.Commands.SessionLog, tools, "Ctrl+Shift+L", ShowSessionLogCommand),
            new("tools.settings", L.Commands.Settings, tools, "Ctrl+OemComma", ShowSettingsCommand),
            new("session.disconnect", L.Commands.Disconnect, session, string.Empty, DisconnectCommand),
        ];
    }
}
