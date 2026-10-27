namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>
    /// Computers (K1): permission rows of the preview, the computer actions (tools, ping, reset), the type, placement and owner texts. Dynamic families: <c>Computers.Type.{ComputerType}</c>, <c>Computers.Placement.{PlacementFinding}</c>, <c>Computers.Owner.{OwnerState}</c>.
    /// </summary>
    public static class Computers
    {
        public const string AclAdded = "Computers.Acl.Added";
        public const string AclRemoved = "Computers.Acl.Removed";
        public const string AclAllow = "Computers.Acl.Allow";
        public const string AclDeny = "Computers.Acl.Deny";
        public const string AclInherited = "Computers.Acl.Inherited";
        public const string AclRightFullControl = "Computers.Acl.Right.FullControl";
        public const string AclRightGenericRead = "Computers.Acl.Right.GenericRead";
        public const string AclRightGenericWrite = "Computers.Acl.Right.GenericWrite";
        public const string AclRightResetPassword = "Computers.Acl.Right.ResetPassword";
        public const string AclRightAccountRestrictions = "Computers.Acl.Right.AccountRestrictions";
        public const string AclRightDnsHostName = "Computers.Acl.Right.DnsHostName";
        public const string AclRightServicePrincipalName = "Computers.Acl.Right.ServicePrincipalName";
        public const string AclRightApplyGroupPolicy = "Computers.Acl.Right.ApplyGroupPolicy";
        public const string AclRightExtendedRight = "Computers.Acl.Right.ExtendedRight";
        public const string AclRightExtendedRightOf = "Computers.Acl.Right.ExtendedRightOf";
        public const string AclRightReadProperty = "Computers.Acl.Right.ReadProperty";
        public const string AclRightReadPropertyOf = "Computers.Acl.Right.ReadPropertyOf";
        public const string AclRightWriteProperty = "Computers.Acl.Right.WriteProperty";
        public const string AclRightWritePropertyOf = "Computers.Acl.Right.WritePropertyOf";
        public const string AclRightSelf = "Computers.Acl.Right.Self";
        public const string AclRightSelfOf = "Computers.Acl.Right.SelfOf";
        public const string AclRightCreateChild = "Computers.Acl.Right.CreateChild";
        public const string AclRightDeleteChild = "Computers.Acl.Right.DeleteChild";
        public const string AclRightListChildren = "Computers.Acl.Right.ListChildren";
        public const string AclRightListObject = "Computers.Acl.Right.ListObject";
        public const string AclRightDeleteTree = "Computers.Acl.Right.DeleteTree";
        public const string AclRightDelete = "Computers.Acl.Right.Delete";
        public const string AclRightReadControl = "Computers.Acl.Right.ReadControl";
        public const string AclRightWriteDacl = "Computers.Acl.Right.WriteDacl";
        public const string AclRightWriteOwner = "Computers.Acl.Right.WriteOwner";
        public const string AclRightOther = "Computers.Acl.Right.Other";
        public const string NotAComputer = "Computers.NotAComputer";
        public const string NoDnsName = "Computers.NoDnsName";
        public const string ToolsNotAvailable = "Computers.ToolsNotAvailable";
        public const string ToolCredentialConfirm = "Computers.ToolCredentialConfirm";
        public const string Ping = "Computers.Ping";
        public const string PingReplies = "Computers.PingReplies";
        public const string PingNoReply = "Computers.PingNoReply";
        public const string RemoteDesktop = "Computers.RemoteDesktop";
        public const string ComputerManagement = "Computers.ComputerManagement";
        public const string EventViewer = "Computers.EventViewer";
        public const string RemoteActionsCommand = "Computers.RemoteActionsCommand";
        public const string ComputerMenu = "Computers.ComputerMenu";
        public const string ResetAccount = "Computers.ResetAccount";
        public const string ResetAccountCommand = "Computers.ResetAccountCommand";
        public const string ResetConfirmOne = "Computers.ResetConfirmOne";
        public const string ResetConfirmMany = "Computers.ResetConfirmMany";
        public const string ResetSkipped = "Computers.ResetSkipped";
        public const string ResetWarning = "Computers.ResetWarning";
        public const string TypeDomainToConfirm = "Computers.TypeDomainToConfirm";
        public const string ResetDone = "Computers.ResetDone";
        public const string ComputerReviewCommand = "Computers.ComputerReviewCommand";
        public const string PrestageJoiner = "Computers.Prestage.Joiner";
        public const string PrestageJoinerNone = "Computers.Prestage.JoinerNone";
        public const string PrestageManagedBy = "Computers.Prestage.ManagedBy";
        public const string PrestageManagedByNone = "Computers.Prestage.ManagedByNone";
        public const string PrestageLocation = "Computers.Prestage.Location";
        public const string PrestageChange = "Computers.Prestage.Change";
        public const string PrestageClear = "Computers.Prestage.Clear";
        public const string PrestageHardening = "Computers.Prestage.Hardening";
        public const string PrestageOwnerIsAdmin = "Computers.Prestage.OwnerIsAdmin";
        public const string PrestageJoinFails = "Computers.Prestage.JoinFails";
        public const string PrestageOnlyAdmins = "Computers.Prestage.OnlyAdmins";
        public const string PrestageNoPreWindows2000 = "Computers.Prestage.NoPreWindows2000";
        public const string PrestageJoinerNotFound = "Computers.Prestage.JoinerNotFound";
        public const string StatusPage = "Computers.Page.Status";
        public const string CheckAllDomainControllers = "Computers.Status.CheckAllDomainControllers";
        public const string ResolveSite = "Computers.Status.ResolveSite";
        public const string ObjectOwner = "Computers.Status.ObjectOwner";
        public const string StatusActions = "Computers.Status.Actions";
        public const string ExactNotAnswered = "Computers.Status.ExactNotAnswered";
        public const string NotRead = "Computers.Status.NotRead";
        public const string SettingsTab = "Computers.Settings.Tab";
        public const string SettingsRemoteHeader = "Computers.Settings.RemoteHeader";
        public const string SettingsTimeout = "Computers.Settings.Timeout";
        public const string SettingsUseHttps = "Computers.Settings.UseHttps";
        public const string SettingsPort = "Computers.Settings.Port";
        public const string SettingsPortHint = "Computers.Settings.PortHint";
        public const string SettingsRefreshUserPolicy = "Computers.Settings.RefreshUserPolicy";
        public const string SettingsRefreshUserPolicyHint = "Computers.Settings.RefreshUserPolicyHint";
        public const string SettingsRestartHeader = "Computers.Settings.RestartHeader";
        public const string SettingsRestartDelay = "Computers.Settings.RestartDelay";
        public const string SettingsRestartMessage = "Computers.Settings.RestartMessage";
        public const string SettingsRestartMessageHint = "Computers.Settings.RestartMessageHint";
        public const string SettingsEndingSoonDays = "Computers.Settings.EndingSoonDays";
        public const string SettingsPlacementHeader = "Computers.Settings.PlacementHeader";
        public const string SettingsPlacementHint = "Computers.Settings.PlacementHint";
        public const string SettingsWorkstationContainers = "Computers.Settings.WorkstationContainers";
        public const string SettingsServerContainers = "Computers.Settings.ServerContainers";
        public const string SettingsContainerInvalid = "Computers.Settings.ContainerInvalid";
        public const string SettingsOverridesHeader = "Computers.Settings.OverridesHeader";
        public const string SettingsOverridesHint = "Computers.Settings.OverridesHint";
        public const string SettingsBuild = "Computers.Settings.Build";
        public const string SettingsEdition = "Computers.Settings.Edition";
        public const string SettingsEndOfSupport = "Computers.Settings.EndOfSupport";
        public const string SettingsAddOverride = "Computers.Settings.AddOverride";
        public const string SettingsRemoveOverride = "Computers.Settings.RemoveOverride";
        public const string SettingsNoOverrides = "Computers.Settings.NoOverrides";
        public const string SettingsBuildInvalid = "Computers.Settings.BuildInvalid";
        public const string SettingsTooManyOverrides = "Computers.Settings.TooManyOverrides";
    }

    /// <summary>
    /// The computer review window (<see cref="AdMng.Presentation.Computers.ComputerReviewViewModel"/>). Dynamic families: <c>ComputerReview.Report.{ComputerReport}</c>, <c>ComputerReview.Hint.{ComputerReport}</c>, <c>ComputerReview.Area.{ComputerReportArea}</c>, <c>ComputerReview.Column.{column id}</c>.
    /// </summary>
    public static class ComputerReview
    {
        public const string WindowTitle = "ComputerReview.WindowTitle";
        public const string WindowTitleDomain = "ComputerReview.WindowTitleDomain";
        public const string ScopeNotChosen = "ComputerReview.ScopeNotChosen";
        public const string ViewNotFound = "ComputerReview.ViewNotFound";
        public const string GroupNotFound = "ComputerReview.GroupNotFound";
        public const string Parameters = "ComputerReview.Parameters";
        public const string Reports = "ComputerReview.Reports";
        public const string InactiveDays = "ComputerReview.InactiveDays";
        public const string EndingSoonDays = "ComputerReview.EndingSoonDays";
        public const string Run = "ComputerReview.Run";
        public const string Status = "ComputerReview.Status";
        public const string StatusRunning = "ComputerReview.StatusRunning";
        public const string StatusStopped = "ComputerReview.StatusStopped";
        public const string Empty = "ComputerReview.Empty";
        public const string NotRun = "ComputerReview.NotRun";
        public const string Selected = "ComputerReview.Selected";
        public const string ExportCsv = "ComputerReview.ExportCsv";
        public const string ExactLastLogon = "ComputerReview.ExactLastLogon";
        public const string ReadingDomainControllers = "ComputerReview.ReadingDomainControllers";
        public const string ExactDone = "ComputerReview.ExactDone";
        public const string ExactOn = "ComputerReview.ExactOn";
        public const string Never = "ComputerReview.Never";
        public const string DaysAgo = "ComputerReview.DaysAgo";
        public const string ResolveSites = "ComputerReview.ResolveSites";
        public const string ResolveSitesConfirm = "ComputerReview.ResolveSitesConfirm";
        public const string ResolvingSites = "ComputerReview.ResolvingSites";
        public const string SitesTooltip = "ComputerReview.SitesTooltip";
        public const string InvalidSubnets = "ComputerReview.InvalidSubnets";
        public const string SiteNotResolved = "ComputerReview.SiteNotResolved";
        public const string NoSubnet = "ComputerReview.NoSubnet";
        public const string LapsUntil = "ComputerReview.LapsUntil";
        public const string LapsNone = "ComputerReview.LapsNone";
        public const string OwnerWithState = "ComputerReview.OwnerWithState";
        public const string SetOwner = "ComputerReview.SetOwner";
        public const string DisableConfirmOne = "ComputerReview.DisableConfirmOne";
        public const string DisableConfirmMany = "ComputerReview.DisableConfirmMany";
        public const string DeleteConfirmOne = "ComputerReview.DeleteConfirmOne";
        public const string DeleteConfirmMany = "ComputerReview.DeleteConfirmMany";
        public const string DeleteChildrenWarning = "ComputerReview.DeleteChildrenWarning";
        public const string Details = "ComputerReview.Details";
        public const string DetailName = "ComputerReview.Detail.Name";
        public const string DetailDnsName = "ComputerReview.Detail.DnsName";
        public const string DetailType = "ComputerReview.Detail.Type";
        public const string DetailEnabled = "ComputerReview.Detail.Enabled";
        public const string DetailSystem = "ComputerReview.Detail.System";
        public const string DetailRelease = "ComputerReview.Detail.Release";
        public const string DetailSupport = "ComputerReview.Detail.Support";
        public const string DetailLastLogon = "ComputerReview.Detail.LastLogon";
        public const string DetailExactLastLogon = "ComputerReview.Detail.ExactLastLogon";
        public const string DetailCreated = "ComputerReview.Detail.Created";
        public const string DetailAccountRenewed = "ComputerReview.Detail.AccountRenewed";
        public const string DetailOwner = "ComputerReview.Detail.Owner";
        public const string DetailCreator = "ComputerReview.Detail.Creator";
        public const string DetailLaps = "ComputerReview.Detail.Laps";
        public const string DetailSite = "ComputerReview.Detail.Site";
        public const string DetailAddress = "ComputerReview.Detail.Address";
        public const string DetailLocation = "ComputerReview.Detail.Location";
        public const string DetailDescription = "ComputerReview.Detail.Description";
        public const string DetailContainer = "ComputerReview.Detail.Container";
        public const string DetailPlacement = "ComputerReview.Detail.Placement";
        public const string DetailFindings = "ComputerReview.Detail.Findings";
        public const string DetailCatalog = "ComputerReview.Detail.Catalog";
    }

    /// <summary>
    /// The Windows release catalogue (support states, dates). Dynamic family: <c>WindowsRelease.Support.{SupportState}</c>, <c>WindowsRelease.Edition.{EditionClass}</c>.
    /// </summary>
    public static class WindowsRelease
    {
        public const string CatalogAsOf = "WindowsRelease.CatalogAsOf";
        public const string CatalogStale = "WindowsRelease.CatalogStale";
        public const string EndedOn = "WindowsRelease.EndedOn";
        public const string EndsOn = "WindowsRelease.EndsOn";
        public const string SupportedUntil = "WindowsRelease.SupportedUntil";
        public const string Overridden = "WindowsRelease.Overridden";
    }

    /// <summary>
    /// The remote actions window, the restart dialog and the remote errors. Dynamic families: <c>RemoteActions.Error.{RemoteErrorKind}</c>, <c>RemoteActions.Hint.{RemoteErrorKind}</c>.
    /// </summary>
    public static class RemoteActions
    {
        public const string WindowTitle = "RemoteActions.WindowTitle";
        public const string WindowTitleShort = "RemoteActions.WindowTitleShort";
        public const string ConnectionText = "RemoteActions.ConnectionText";
        public const string Status = "RemoteActions.Status";
        public const string RefreshStatus = "RemoteActions.RefreshStatus";
        public const string PingLabel = "RemoteActions.PingLabel";
        public const string WinRmLabel = "RemoteActions.WinRmLabel";
        public const string SystemLabel = "RemoteActions.SystemLabel";
        public const string LastBootLabel = "RemoteActions.LastBootLabel";
        public const string WinRmAvailable = "RemoteActions.WinRmAvailable";
        public const string WinRmUnavailable = "RemoteActions.WinRmUnavailable";
        public const string StatusRead = "RemoteActions.StatusRead";
        public const string Users = "RemoteActions.Users";
        public const string LoadUsers = "RemoteActions.LoadUsers";
        public const string UsersNote = "RemoteActions.UsersNote";
        public const string UsersRead = "RemoteActions.UsersRead";
        public const string Console = "RemoteActions.Console";
        public const string ColumnAccount = "RemoteActions.Column.Account";
        public const string ColumnSession = "RemoteActions.Column.Session";
        public const string ColumnSince = "RemoteActions.Column.Since";
        public const string ServicesTitle = "RemoteActions.Services";
        public const string LoadServices = "RemoteActions.LoadServices";
        public const string OnlyStoppedAutomatic = "RemoteActions.OnlyStoppedAutomatic";
        public const string ServicesRead = "RemoteActions.ServicesRead";
        public const string Delayed = "RemoteActions.Delayed";
        public const string ColumnService = "RemoteActions.Column.Service";
        public const string ColumnState = "RemoteActions.Column.State";
        public const string ColumnStartMode = "RemoteActions.Column.StartMode";
        public const string ColumnLogOnAs = "RemoteActions.Column.LogOnAs";
        public const string Actions = "RemoteActions.Actions";
        public const string ActionStatus = "RemoteActions.Action.Status";
        public const string ActionUsers = "RemoteActions.Action.Users";
        public const string ActionServices = "RemoteActions.Action.Services";
        public const string ActionRefreshPolicy = "RemoteActions.Action.RefreshPolicy";
        public const string ActionRestart = "RemoteActions.Action.Restart";
        public const string ActionAbortRestart = "RemoteActions.Action.AbortRestart";
        public const string PolicyRefreshed = "RemoteActions.PolicyRefreshed";
        public const string PolicyRunning = "RemoteActions.PolicyRunning";
        public const string PolicyUsersScheduled = "RemoteActions.PolicyUsersScheduled";
        public const string RestartScheduled = "RemoteActions.RestartScheduled";
        public const string RestartScheduledNow = "RemoteActions.RestartScheduledNow";
        public const string RestartAbortRequested = "RemoteActions.RestartAbortRequested";
        public const string Log = "RemoteActions.Log";
        public const string Tools = "RemoteActions.Tools";
        public const string RestartTitle = "RemoteActions.RestartTitle";
        public const string RestartDelay = "RemoteActions.RestartDelay";
        public const string RestartMessage = "RemoteActions.RestartMessage";
        public const string RestartForce = "RemoteActions.RestartForce";
        public const string RestartConfirm = "RemoteActions.RestartConfirm";
        public const string RestartNow = "RemoteActions.RestartNow";
        public const string RestartIn = "RemoteActions.RestartIn";
        public const string RestartTypeName = "RemoteActions.RestartTypeName";
        public const string RestartDelayRange = "RemoteActions.RestartDelayRange";
        public const string RestartMessageTooLong = "RemoteActions.RestartMessageTooLong";
        public const string RestartMessageLength = "RemoteActions.RestartMessageLength";
        public const string RestartDefaultMessage = "RemoteActions.RestartDefaultMessage";
    }
}
