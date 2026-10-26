using AdMng.Core.Model;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Shell;

/// <summary>Input field of the main window that a keyboard shortcut moves the focus to.</summary>
public enum ShellFocusTarget
{
    /// <summary>Global quick search box (Ctrl+E).</summary>
    Search,

    /// <summary>Editable address bar (Ctrl+L).</summary>
    Address,
}

/// <summary>Command descriptors (palette, menus, key bindings). Shortcuts follow ADUC where ADUC has one.</summary>
public sealed partial class ShellViewModel
{
    /// <summary>Asks the view to focus (and select the text of) an input field - Ctrl+E / Ctrl+L.</summary>
    public event EventHandler<ShellFocusTarget>? FocusRequested;
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
            new("object.sendMessage", L.UserMessages.Command, obj, string.Empty, SendMessageCommand),
            new("object.addToGroup", L.Commands.AddToGroup, obj, string.Empty, AddToGroupCommand),
            new("object.batchEdit", L.Commands.BatchEdit, obj, "Ctrl+Shift+B", BatchEditCommand),
            new("object.copyRows", L.Copy.Rows, obj, "Ctrl+C", CopyRowsCommand),
            new("object.copyRowsWithHeaders", L.Copy.RowsWithHeaders, obj, string.Empty, CopyRowsWithHeadersCommand),
            new("copyAs.mail", L.Copy.Mail, L.Copy.Category, "Ctrl+Shift+M", CopyAsCommand) { Parameter = CopyFormat.Mail },
            new("copyAs.upn", L.Copy.Upn, L.Copy.Category, string.Empty, CopyAsCommand) { Parameter = CopyFormat.UserPrincipalName },
            new("copyAs.login", L.Copy.Login, L.Copy.Category, string.Empty, CopyAsCommand) { Parameter = CopyFormat.SamAccountName },
            new("copyAs.domainLogin", L.Copy.DomainLogin, L.Copy.Category, string.Empty, CopyAsCommand) { Parameter = CopyFormat.DomainLogin },
            new("copyAs.name", L.Copy.Name, L.Copy.Category, string.Empty, CopyAsCommand) { Parameter = CopyFormat.Name },
            new("copyAs.displayName", L.Copy.DisplayName, L.Copy.Category, string.Empty, CopyAsCommand) { Parameter = CopyFormat.DisplayName },
            new("object.copyDn", L.Copy.DistinguishedName, L.Copy.Category, "Ctrl+Shift+C", CopyDnCommand),
            new("copyAs.path", L.Copy.CanonicalPath, L.Copy.Category, string.Empty, CopyAsCommand) { Parameter = CopyFormat.CanonicalPath },
            new("copyAs.guid", L.Copy.Guid, L.Copy.Category, string.Empty, CopyAsCommand) { Parameter = CopyFormat.ObjectGuid },
            new("copyAs.sid", L.Copy.Sid, L.Copy.Category, string.Empty, CopyAsCommand) { Parameter = CopyFormat.ObjectSid },
            new("object.watchdogHistory", L.Commands.WatchdogHistory, obj, string.Empty, ShowWatchdogHistoryCommand),
            new("object.compareWithConnection", L.Commands.CompareWithConnection, obj, string.Empty, CompareWithConnectionCommand)
            {
                DisabledReason = _ => CompareBlockedReason(),
            },
            new("new.user", L.Commands.NewUser, create, "Ctrl+Shift+U", NewUserCommand)
            {
                DisabledReason = p => CreateBlockedReason(ObjectKind.User, p as string),
            },
            new("new.group", L.Commands.NewGroup, create, "Ctrl+Shift+G", NewGroupCommand)
            {
                DisabledReason = p => CreateBlockedReason(ObjectKind.Group, p as string),
            },
            new("new.ou", L.Commands.NewOrganizationalUnit, create, "Ctrl+Shift+O", NewOrganizationalUnitCommand)
            {
                DisabledReason = p => CreateBlockedReason(ObjectKind.OrganizationalUnit, p as string),
            },
            new("new.computer", L.Commands.NewComputer, create, string.Empty, NewComputerCommand)
            {
                DisabledReason = p => CreateBlockedReason(ObjectKind.Computer, p as string),
            },
            new("new.contact", L.Commands.NewContact, create, string.Empty, NewContactCommand)
            {
                DisabledReason = p => CreateBlockedReason(ObjectKind.Contact, p as string),
            },
            new("nav.find", L.Commands.Find, nav, "Ctrl+F", FindCommand),
            new("nav.search", L.Commands.QuickSearch, nav, "Ctrl+E", FocusSearchCommand),
            new("nav.back", L.Commands.Back, nav, "Alt+Left", GoBackCommand),
            new("nav.forward", L.Commands.Forward, nav, "Alt+Right", GoForwardCommand),
            new("nav.up", L.Commands.Up, nav, "Alt+Up", GoUpCommand),
            new("nav.address", L.Commands.GoToAddress, nav, "Ctrl+L", FocusAddressCommand),
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
            new("tools.importCsv", L.Commands.ImportCsv, tools, string.Empty, ImportCsvCommand),
            new("tools.sessionLog", L.Commands.SessionLog, tools, "Ctrl+Shift+L", ShowSessionLogCommand),
            new("tools.recycleBin", L.Commands.RecycleBin, tools, "Ctrl+Shift+R", ShowRecycleBinCommand),
            new("tools.accountReview", L.Commands.AccountReview, tools, string.Empty, ShowAccountReviewCommand),
            new("tools.watchdog", L.Commands.Watchdog, tools, "Ctrl+Shift+A", ShowWatchdogCommand),
            new("tools.settings", L.Commands.Settings, tools, "Ctrl+OemComma", ShowSettingsCommand),
            new("session.newWindow", L.Commands.NewConnectionWindow, session, "Ctrl+Shift+N", NewConnectionWindowCommand),
            new("session.changeDc", L.Commands.ChangeDomainController, session, string.Empty, ChangeDomainControllerCommand),
            new("session.disconnect", L.Commands.Disconnect, session, string.Empty, DisconnectCommand),
        ];
    }

    /// <summary>Ctrl+E: the search box takes the keyboard (typing then searches as you type).</summary>
    [RelayCommand]
    private void FocusSearch() => FocusRequested?.Invoke(this, ShellFocusTarget.Search);

    /// <summary>Ctrl+L: the address bar takes the keyboard (Enter then runs <see cref="NavigateToAddressCommand"/>).</summary>
    [RelayCommand]
    private void FocusAddress() => FocusRequested?.Invoke(this, ShellFocusTarget.Address);
}
