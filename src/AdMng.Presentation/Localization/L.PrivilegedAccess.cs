namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>
    /// The "Uprawnienia" tab of the account review (<see cref="AdMng.Presentation.AccountReview.PrivilegedAccessViewModel"/>, README U4).
    /// Dynamic families (see <see cref="AdMng.Presentation.AccountReview.PrivilegedAccessText"/>): <c>PrivilegedAccess.Level.{PrivilegeLevel}</c>,
    /// <c>PrivilegedAccess.LevelHint.{PrivilegeLevel}</c>, <c>PrivilegedAccess.Source.{PrivilegeSource}</c>,
    /// <c>PrivilegedAccess.Kind.{PrivilegeFindingKind}</c>, <c>PrivilegedAccess.Group.{catalogue id}</c>,
    /// <c>PrivilegedAccess.Scope.{PrivilegeScopeKind}</c>, <c>PrivilegedAccess.Type.{PrivilegedAccountType}</c>,
    /// <c>PrivilegedAccess.Peer.{PeerDimension}</c>, <c>PrivilegedAccess.Target.{PrivilegeRuleTarget}</c> and
    /// <c>PrivilegedAccess.Column.{column id}</c>.
    /// </summary>
    public static class PrivilegedAccess
    {
        public const string TabAccounts = "PrivilegedAccess.TabAccounts";
        public const string TabPrivileges = "PrivilegedAccess.TabPrivileges";
        public const string Settings = "PrivilegedAccess.Settings";
        public const string AccountTypes = "PrivilegedAccess.AccountTypes";
        public const string IncludeComputers = "PrivilegedAccess.IncludeComputers";
        public const string IncludeServiceAccounts = "PrivilegedAccess.IncludeServiceAccounts";
        public const string Peers = "PrivilegedAccess.Peers";
        public const string PeersByOu = "PrivilegedAccess.PeersByOu";
        public const string PeersByDepartment = "PrivilegedAccess.PeersByDepartment";
        public const string PeersByTitle = "PrivilegedAccess.PeersByTitle";
        public const string PeersMinimum = "PrivilegedAccess.PeersMinimum";
        public const string PeersShare = "PrivilegedAccess.PeersShare";
        public const string GroupLevels = "PrivilegedAccess.GroupLevels";
        public const string Marks = "PrivilegedAccess.Marks";
        public const string MarkGroup = "PrivilegedAccess.MarkGroup";
        public const string MarkAccount = "PrivilegedAccess.MarkAccount";
        public const string RemoveMark = "PrivilegedAccess.RemoveMark";
        public const string Note = "PrivilegedAccess.Note";
        public const string Rules = "PrivilegedAccess.Rules";
        public const string AddRule = "PrivilegedAccess.AddRule";
        public const string RemoveRule = "PrivilegedAccess.RemoveRule";
        public const string RuleFilter = "PrivilegedAccess.RuleFilter";
        public const string RuleTarget = "PrivilegedAccess.RuleTarget";
        public const string NewFilter = "PrivilegedAccess.NewFilter";
        public const string ManageFilters = "PrivilegedAccess.ManageFilters";
        public const string NoFilters = "PrivilegedAccess.NoFilters";
        public const string Level = "PrivilegedAccess.LevelLabel";
        public const string Levels = "PrivilegedAccess.Levels";
        public const string Sources = "PrivilegedAccess.Sources";
        public const string OnlyPeerOutliers = "PrivilegedAccess.OnlyPeerOutliers";
        public const string DetailsTitle = "PrivilegedAccess.DetailsTitle";
        public const string NoSelection = "PrivilegedAccess.NoSelection";
        public const string PathLabel = "PrivilegedAccess.PathLabel";
        public const string ScopeLabel = "PrivilegedAccess.ScopeLabel";
        public const string GrantsLabel = "PrivilegedAccess.GrantsLabel";
        public const string ValuesLabel = "PrivilegedAccess.ValuesLabel";
        public const string NoteLabel = "PrivilegedAccess.NoteLabel";
        public const string RemoveFromGroup = "PrivilegedAccess.RemoveFromGroup";
        public const string RemoveFromGroupTitle = "PrivilegedAccess.RemoveFromGroupTitle";
        public const string PrimaryGroupNotRemovable = "PrivilegedAccess.PrimaryGroupNotRemovable";
        public const string ClearAdminCount = "PrivilegedAccess.ClearAdminCount";
        public const string NotRun = "PrivilegedAccess.NotRun";
        public const string Empty = "PrivilegedAccess.Empty";
        public const string Status = "PrivilegedAccess.Status";
        public const string StatusRunning = "PrivilegedAccess.StatusRunning";
        public const string StatusStopped = "PrivilegedAccess.StatusStopped";
        public const string StatusPeers = "PrivilegedAccess.StatusPeers";
        public const string SkippedRules = "PrivilegedAccess.SkippedRules";
        public const string ShowPrivileges = "PrivilegedAccess.ShowPrivileges";
        public const string ShowPrivilegesCount = "PrivilegedAccess.ShowPrivilegesCount";
        public const string PrimaryGroup = "PrivilegedAccess.PrimaryGroup";
        public const string Everyone = "PrivilegedAccess.Everyone";
        public const string AuthenticatedUsers = "PrivilegedAccess.AuthenticatedUsers";
        public const string Disabled = "PrivilegedAccess.Disabled";
        public const string Unusual = "PrivilegedAccess.Unusual";
        public const string HoldersNone = "PrivilegedAccess.HoldersNone";
        public const string Holders = "PrivilegedAccess.Holders";
        public const string FindingCount = "PrivilegedAccess.FindingCount";
        public const string ExportTitle = "PrivilegedAccess.ExportTitle";
        public const string ScopeFilter = "PrivilegedAccess.ScopeFilter";
        public const string ScopeFilterNone = "PrivilegedAccess.ScopeFilterNone";
        public const string FilterInvalid = "PrivilegedAccess.FilterInvalid";
        public const string MarkAdded = "PrivilegedAccess.MarkAdded";
    }

    /// <summary>
    /// The filter builder and the saved filters (<see cref="AdMng.Presentation.Filters.FilterEditorViewModel"/>, <see cref="AdMng.Presentation.Filters.SavedFiltersViewModel"/>).
    /// Dynamic families: <c>Filters.Operator.{FilterOperator}</c>, <c>Filters.Combinator.{FilterCombinator}</c>,
    /// <c>Filters.Issue.{FilterIssueCode}</c>.
    /// </summary>
    public static class Filters
    {
        public const string NewTitle = "Filters.NewTitle";
        public const string EditTitle = "Filters.EditTitle";
        public const string SavedTitle = "Filters.SavedTitle";
        public const string GroupsCombine = "Filters.GroupsCombine";
        public const string Group = "Filters.Group";
        public const string ConditionsCombine = "Filters.ConditionsCombine";
        public const string AddCondition = "Filters.AddCondition";
        public const string RemoveCondition = "Filters.RemoveCondition";
        public const string AddGroup = "Filters.AddGroup";
        public const string RemoveGroup = "Filters.RemoveGroup";
        public const string Attribute = "Filters.Attribute";
        public const string Operator = "Filters.Operator";
        public const string Value = "Filters.Value";
        public const string ChooseGroup = "Filters.ChooseGroup";
        public const string Raw = "Filters.Raw";
        public const string Preview = "Filters.Preview";
        public const string ClientCheck = "Filters.ClientCheck";
        public const string IssueAt = "Filters.IssueAt";
        public const string And = "Filters.And";
        public const string Or = "Filters.Or";
        public const string Saved = "Filters.Saved";
        public const string DeleteTitle = "Filters.DeleteTitle";
        public const string DeleteConfirm = "Filters.DeleteConfirm";
        public const string ExportTitle = "Filters.ExportTitle";
        public const string ImportTitle = "Filters.ImportTitle";
        public const string Exported = "Filters.Exported";
        public const string Imported = "Filters.Imported";
        public const string InvalidFile = "Filters.InvalidFile";
        public const string Empty = "Filters.Empty";
        public const string New = "Filters.New";
        public const string Export = "Filters.Export";
        public const string Import = "Filters.Import";
        public const string ChipsLabel = "Filters.ChipsLabel";
        public const string Summary = "Filters.Summary";
    }
}
