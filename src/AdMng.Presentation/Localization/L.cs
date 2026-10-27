namespace AdMng.Presentation.Localization;

/// <summary>
/// Resource keys of <c>Resources/Strings*.resx</c>. Use these constants (never string literals) so a unit test can
/// verify that every key exists in both languages. Dynamic key families (enum names, issue codes, attributes) are
/// built by <see cref="DisplayNames"/>.
/// </summary>
public static partial class L
{
    public static class App
    {
        public const string Title = "App.Title";
        public const string ShortTitle = "App.ShortTitle";
    }

    public static class Common
    {
        public const string Ok = "Common.Ok";
        public const string Cancel = "Common.Cancel";
        public const string Apply = "Common.Apply";
        public const string Close = "Common.Close";
        public const string Save = "Common.Save";
        public const string Yes = "Common.Yes";
        public const string No = "Common.No";
        public const string Add = "Common.Add";
        public const string Remove = "Common.Remove";
        public const string Edit = "Common.Edit";
        public const string Delete = "Common.Delete";
        public const string Browse = "Common.Browse";
        public const string Copy = "Common.Copy";
        public const string Retry = "Common.Retry";
        public const string Next = "Common.Next";
        public const string Back = "Common.Back";
        public const string Finish = "Common.Finish";
        public const string Search = "Common.Search";
        public const string Filter = "Common.Filter";
        public const string Clear = "Common.Clear";
        public const string Stop = "Common.Stop";
        public const string Loading = "Common.Loading";
        public const string Unknown = "Common.Unknown";
        public const string NotSet = "Common.NotSet";
        public const string Name = "Common.Name";
        public const string Description = "Common.Description";
        public const string Error = "Common.Error";
        public const string Warning = "Common.Warning";
        public const string Information = "Common.Information";
        public const string MoveUp = "Common.MoveUp";
        public const string MoveDown = "Common.MoveDown";
        public const string ResetToDefault = "Common.ResetToDefault";
    }

    public static class Format
    {
        /// <summary>FILETIME "never/not set" sentinel (accountExpires, pwdLastSet = 0...).</summary>
        public const string Never = "Format.Never";
        public const string True = "Format.True";
        public const string False = "Format.False";
    }

    public static class Connect
    {
        public const string Title = "Connect.Title";
        public const string Subtitle = "Connect.Subtitle";
        public const string Profile = "Connect.Profile";
        public const string DeleteProfile = "Connect.DeleteProfile";
        public const string Domain = "Connect.Domain";
        public const string Server = "Connect.Server";
        public const string ServerAutomatic = "Connect.ServerAutomatic";
        public const string FindDomainControllers = "Connect.FindDomainControllers";
        public const string Authentication = "Connect.Authentication";
        public const string CurrentUserFormat = "Connect.CurrentUserFormat";
        public const string UserName = "Connect.UserName";
        public const string UserNameHint = "Connect.UserNameHint";
        public const string Password = "Connect.Password";
        public const string Security = "Connect.Security";
        public const string SaveProfile = "Connect.SaveProfile";
        public const string ProfileName = "Connect.ProfileName";
        public const string ConnectButton = "Connect.ConnectButton";
        public const string Connecting = "Connect.Connecting";
        public const string FindingDomainControllers = "Connect.FindingDomainControllers";
        public const string DomainControllersFound = "Connect.DomainControllersFound";
        public const string DomainRequired = "Connect.DomainRequired";
        public const string UserNameRequired = "Connect.UserNameRequired";
        public const string PasswordRequired = "Connect.PasswordRequired";
        public const string SimpleBindRequiresTls = "Connect.SimpleBindRequiresTls";
        public const string ProfileNameRequired = "Connect.ProfileNameRequired";
        public const string Site = "Connect.Site";
        public const string GlobalCatalog = "Connect.GlobalCatalog";
        public const string ReadOnlyDc = "Connect.ReadOnlyDc";
        public const string PasswordNeverStored = "Connect.PasswordNeverStored";
    }

    public static class Shell
    {
        public const string WindowTitleConnected = "Shell.WindowTitleConnected";

        /// <summary>{0} = domain, {1} = account (with the ordinal of a second identical window), {2} = app title.</summary>
        public const string WindowTitleConnection = "Shell.WindowTitleConnection";

        /// <summary>{0} = domain: closing one connection window while its background operations run.</summary>
        public const string CloseWindowWhileRunning = "Shell.CloseWindowWhileRunning";
        public const string MenuFile = "Shell.MenuFile";
        public const string MenuAction = "Shell.MenuAction";
        public const string MenuView = "Shell.MenuView";
        public const string MenuTools = "Shell.MenuTools";
        public const string MenuHelp = "Shell.MenuHelp";
        public const string MenuNew = "Shell.MenuNew";
        public const string ConnectedTo = "Shell.ConnectedTo";
        public const string BoundAs = "Shell.BoundAs";
        public const string Version = "Shell.Version";
        public const string VersionToolTip = "Shell.VersionToolTip";
        public const string PrivilegedIdentity = "Shell.PrivilegedIdentity";
        public const string AddressPlaceholder = "Shell.AddressPlaceholder";
        public const string AddressNotFound = "Shell.AddressNotFound";
        public const string SearchPlaceholder = "Shell.SearchPlaceholder";
        public const string ShowAllResults = "Shell.ShowAllResults";
        public const string NoResults = "Shell.NoResults";
        public const string DetailsPane = "Shell.DetailsPane";
        public const string NoSelection = "Shell.NoSelection";
        public const string CopiedDn = "Shell.CopiedDn";
        public const string DisconnectWhileRunning = "Shell.DisconnectWhileRunning";
        public const string CommandPalettePlaceholder = "Shell.CommandPalettePlaceholder";
        public const string Recent = "Shell.Recent";
    }

    public static class CommandCategories
    {
        public const string Object = "CommandCategories.Object";
        public const string Navigation = "CommandCategories.Navigation";
        public const string New = "CommandCategories.New";
        public const string View = "CommandCategories.View";
        public const string Tools = "CommandCategories.Tools";
        public const string Session = "CommandCategories.Session";
    }

    public static class Commands
    {
        /// <summary>"Nowe połączenie w nowym oknie…" (File menu, Ctrl+Shift+N, palette, badge menu).</summary>
        public const string NewConnectionWindow = "Commands.NewConnectionWindow";
        public const string Refresh = "Commands.Refresh";
        public const string Properties = "Commands.Properties";
        public const string Delete = "Commands.Delete";
        public const string Rename = "Commands.Rename";
        public const string Move = "Commands.Move";
        public const string Enable = "Commands.Enable";
        public const string Disable = "Commands.Disable";
        public const string Unlock = "Commands.Unlock";
        public const string ResetPassword = "Commands.ResetPassword";
        public const string AddToGroup = "Commands.AddToGroup";
        public const string BatchEdit = "Commands.BatchEdit";
        public const string CopyDn = "Commands.CopyDn";
        public const string NewUser = "Commands.NewUser";
        public const string NewGroup = "Commands.NewGroup";
        public const string NewOrganizationalUnit = "Commands.NewOrganizationalUnit";
        public const string NewComputer = "Commands.NewComputer";
        public const string NewContact = "Commands.NewContact";
        public const string Find = "Commands.Find";
        public const string QuickSearch = "Commands.QuickSearch";
        public const string Back = "Commands.Back";
        public const string Forward = "Commands.Forward";
        public const string Up = "Commands.Up";
        public const string GoToAddress = "Commands.GoToAddress";
        public const string NewTab = "Commands.NewTab";
        public const string OpenInNewTab = "Commands.OpenInNewTab";
        public const string CloseTab = "Commands.CloseTab";
        public const string ChooseColumns = "Commands.ChooseColumns";
        public const string ExportList = "Commands.ExportList";
        public const string PinContainer = "Commands.PinContainer";
        public const string UnpinContainer = "Commands.UnpinContainer";
        public const string SaveAsView = "Commands.SaveAsView";
        public const string EditView = "Commands.EditView";
        public const string DeleteView = "Commands.DeleteView";
        public const string ImportViews = "Commands.ImportViews";
        public const string ExportViews = "Commands.ExportViews";
        public const string ToggleAdvancedFeatures = "Commands.ToggleAdvancedFeatures";
        public const string ToggleDetailsPane = "Commands.ToggleDetailsPane";
        public const string CommandPalette = "Commands.CommandPalette";
        public const string SessionLog = "Commands.SessionLog";
        public const string Settings = "Commands.Settings";
        public const string Disconnect = "Commands.Disconnect";
        public const string ChangeDomainController = "Commands.ChangeDomainController";
        public const string CancelLoad = "Commands.CancelLoad";
        public const string ImportCsv = "Commands.ImportCsv";
        public const string RecycleBin = "Commands.RecycleBin";
        public const string AccountReview = "Commands.AccountReview";
        public const string SavedFilters = "Commands.SavedFilters";
        public const string Watchdog = "Commands.Watchdog";
        public const string WatchdogHistory = "Commands.WatchdogHistory";

        /// <summary>"Porównaj z obiektem w innym połączeniu…" (docs/dev/multi-window.md 8).</summary>
        public const string CompareWithConnection = "Commands.CompareWithConnection";
    }

    public static class Tree
    {
        public const string Views = "Tree.Views";
        public const string QuickFilters = "Tree.QuickFilters";
        public const string LoadFailed = "Tree.LoadFailed";
    }

    public static class Columns
    {
        public const string Name = "Columns.Name";
        public const string Type = "Columns.Type";
        public const string Location = "Columns.Location";
        public const string ChooserTitle = "Columns.ChooserTitle";
        public const string Available = "Columns.Available";
        public const string Selected = "Columns.Selected";
    }

    public static class List
    {
        public const string Loading = "List.Loading";
        public const string Partial = "List.Partial";
        public const string LoadSummary = "List.LoadSummary";
        public const string Selected = "List.Selected";
        public const string FilteringLoaded = "List.FilteringLoaded";
        public const string FilterMatches = "List.FilterMatches";
        public const string QuickFilterPlaceholder = "List.QuickFilterPlaceholder";
        public const string Empty = "List.Empty";
        public const string LocationDeleted = "List.LocationDeleted";
        public const string ServerFilters = "List.ServerFilters";
    }

    public static class Search
    {
        public const string TabTitle = "Search.TabTitle";
    }

    public static class Details
    {
        public const string State = "Details.State";
        public const string StateEnabled = "Details.StateEnabled";
        public const string StateDisabled = "Details.StateDisabled";
        public const string StateLocked = "Details.StateLocked";
        public const string PasswordExpires = "Details.PasswordExpires";
        public const string PasswordNeverExpires = "Details.PasswordNeverExpires";
        public const string MustChangePassword = "Details.MustChangePassword";
        public const string ManySelected = "Details.ManySelected";
        public const string ProtectedObject = "Details.ProtectedObject";
        public const string Path = "Details.Path";
        public const string DistinguishedName = "Details.DistinguishedName";
        public const string QuickActions = "Details.QuickActions";
    }

    public static class Delete
    {
        public const string ConfirmOne = "Delete.ConfirmOne";
        public const string ConfirmMany = "Delete.ConfirmMany";
        public const string SubtreeWarning = "Delete.SubtreeWarning";
        public const string TypeToConfirm = "Delete.TypeToConfirm";
    }

    public static class Move
    {
        public const string ConfirmDrop = "Move.ConfirmDrop";
        public const string InvalidTarget = "Move.InvalidTarget";
    }

    public static class Rename
    {
        public const string TooLong = "Rename.TooLong";
    }

    public static class Changes
    {
        public const string NothingToDo = "Changes.NothingToDo";
        public const string ContinueWithValid = "Changes.ContinueWithValid";
        public const string Succeeded = "Changes.Succeeded";
        public const string Cancelled = "Changes.Cancelled";
        public const string Failed = "Changes.Failed";
        public const string MoreIssues = "Changes.MoreIssues";
    }

    public static class Activity
    {
        public const string Title = "Activity.Title";
        public const string Running = "Activity.Running";
        public const string Progress = "Activity.Progress";
        public const string ProgressWithFailures = "Activity.ProgressWithFailures";
        public const string Completed = "Activity.Completed";
        public const string CompletedWithFailures = "Activity.CompletedWithFailures";
        public const string Cancelled = "Activity.Cancelled";
        public const string ClearFinished = "Activity.ClearFinished";
        public const string None = "Activity.None";
    }

    public static class Views
    {
        public const string NewViewTitle = "Views.NewViewTitle";
        public const string EditViewTitle = "Views.EditViewTitle";
        public const string Name = "Views.Name";
        public const string BaseDn = "Views.BaseDn";
        public const string Scope = "Views.Scope";
        public const string Filter = "Views.Filter";
        public const string FilterHint = "Views.FilterHint";
        public const string Test = "Views.Test";
        public const string Testing = "Views.Testing";
        public const string TestResult = "Views.TestResult";
        public const string InvalidFilter = "Views.InvalidFilter";
        public const string Saved = "Views.Saved";
        public const string DeleteTitle = "Views.DeleteTitle";
        public const string DeleteConfirm = "Views.DeleteConfirm";
        public const string Pinned = "Views.Pinned";
        public const string ExportTitle = "Views.ExportTitle";
        public const string ImportTitle = "Views.ImportTitle";
        public const string Exported = "Views.Exported";
        public const string Imported = "Views.Imported";
        public const string InvalidFile = "Views.InvalidFile";
    }

    public static class Files
    {
        public const string CsvFilter = "Files.CsvFilter";
        public const string JsonFilter = "Files.JsonFilter";
        public const string Exported = "Files.Exported";

        /// <summary>{0} = headers of the list columns that hold secret values.</summary>
        public const string ExportSecretColumns = "Files.ExportSecretColumns";
    }

    public static class Errors
    {
        public const string Title = "Errors.Title";
        public const string Cancelled = "Errors.Cancelled";
        public const string Conflict = "Errors.Conflict";
        public const string Unexpected = "Errors.Unexpected";
        public const string File = "Errors.File";
        public const string NotConnected = "Errors.NotConnected";
        public const string AccountLockedOut = "Errors.AccountLockedOut";
        public const string AccountDisabled = "Errors.AccountDisabled";
        public const string AccountExpired = "Errors.AccountExpired";
        public const string PasswordExpired = "Errors.PasswordExpired";
        public const string MustChangePassword = "Errors.MustChangePassword";
        public const string InvalidLogonHours = "Errors.InvalidLogonHours";
        public const string InvalidWorkstation = "Errors.InvalidWorkstation";
        public const string ParentNotRestored = "Errors.ParentNotRestored";
    }

    public static class Hints
    {
        public const string InvalidCredentials = "Hints.InvalidCredentials";
        public const string AccountRestriction = "Hints.AccountRestriction";
        public const string ChangePassword = "Hints.ChangePassword";
        public const string ServerUnavailable = "Hints.ServerUnavailable";
        public const string StrongAuthRequired = "Hints.StrongAuthRequired";
        public const string UnwillingToPerform = "Hints.UnwillingToPerform";
    }

    /// <summary>Property sheet tabs (ADUC names and order), shared with the property sheet view models.</summary>
    public static class Pages
    {
        public const string General = "Pages.General";
        public const string Address = "Pages.Address";
        public const string Account = "Pages.Account";
        public const string Profile = "Pages.Profile";
        public const string Telephones = "Pages.Telephones";
        public const string Organization = "Pages.Organization";
        public const string MemberOf = "Pages.MemberOf";
        public const string Members = "Pages.Members";
        public const string EmailAddresses = "Pages.EmailAddresses";
        public const string Object = "Pages.Object";
        public const string AttributeEditor = "Pages.AttributeEditor";
        public const string OperatingSystem = "Pages.OperatingSystem";
        public const string Delegation = "Pages.Delegation";
        public const string Location = "Pages.Location";
        public const string ManagedBy = "Pages.ManagedBy";
    }

    public static class Settings
    {
        public const string Title = "Settings.Title";

        /// <summary>The settings window title: the settings are shared by all connection windows.</summary>
        public const string TitleShared = "Settings.TitleShared";
        public const string Language = "Settings.Language";
        public const string LanguageSystem = "Settings.LanguageSystem";
        public const string LanguagePolish = "Settings.LanguagePolish";
        public const string LanguageEnglish = "Settings.LanguageEnglish";
        public const string Theme = "Settings.Theme";
        public const string PageSize = "Settings.PageSize";
        public const string BatchParallelism = "Settings.BatchParallelism";
        public const string ConfirmationThreshold = "Settings.ConfirmationThreshold";
        public const string InactiveDays = "Settings.InactiveDays";
        public const string LiveUpdates = "Settings.LiveUpdates";
        public const string OutOfRange = "Settings.OutOfRange";
    }
}
