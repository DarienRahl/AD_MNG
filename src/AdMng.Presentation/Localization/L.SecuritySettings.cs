namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>
    /// The security settings editor (K4, docs/dev/k-computers-and-gpo.md 19). The catalog names are dynamic families of
    /// <c>SecuritySettingsText</c> (<c>SecRight.*</c>, <c>SecOption.*</c>, <c>SecAudit.*</c> …).
    /// </summary>
    public static class SecuritySettings
    {
        public const string Title = "SecSettings.Title";
        public const string EditCommand = "SecSettings.EditCommand";
        public const string PlanTitle = "SecSettings.PlanTitle";
        public const string Save = "SecSettings.Save";
        public const string Discard = "SecSettings.Discard";
        public const string DiscardTitle = "SecSettings.DiscardTitle";
        public const string DiscardMessage = "SecSettings.DiscardMessage";
        public const string CloseDiscard = "SecSettings.CloseDiscard";
        public const string SaveBlocked = "SecSettings.SaveBlocked";
        public const string ChangedCount = "SecSettings.ChangedCount";
        public const string ChangedMark = "SecSettings.ChangedMark";
        public const string Loaded = "SecSettings.Loaded";
        public const string ConflictNote = "SecSettings.ConflictNote";
        public const string FileUnreadable = "SecSettings.FileUnreadable";
        public const string TypedNameHint = "SecSettings.TypedNameHint";
        public const string NameRequired = "SecSettings.NameRequired";
        public const string NameInvalid = "SecSettings.NameInvalid";
        public const string EditRefused = "SecSettings.EditRefused";
        public const string AlreadyListed = "SecSettings.AlreadyListed";
        public const string NotDefined = "SecSettings.NotDefined";
        public const string Nobody = "SecSettings.Nobody";
        public const string PrincipalPlaceholder = "SecSettings.PrincipalPlaceholder";
        public const string GroupPlaceholder = "SecSettings.GroupPlaceholder";
        public const string AddByName = "SecSettings.AddByName";
        public const string AddFromDirectory = "SecSettings.AddFromDirectory";
        public const string Remove = "SecSettings.Remove";

        // Pages
        public const string TabGroups = "SecSettings.TabGroups";
        public const string TabRights = "SecSettings.TabRights";
        public const string TabOptions = "SecSettings.TabOptions";
        public const string TabAudit = "SecSettings.TabAudit";
        public const string TabServices = "SecSettings.TabServices";

        // Local groups
        public const string PrecedenceNote = "SecSettings.PrecedenceNote";
        public const string RestrictedGroupsHeader = "SecSettings.RestrictedGroupsHeader";
        public const string GppGroupsHeader = "SecSettings.GppGroupsHeader";
        public const string ColumnGroup = "SecSettings.ColumnGroup";
        public const string ColumnMembers = "SecSettings.ColumnMembers";
        public const string ColumnMemberOf = "SecSettings.ColumnMemberOf";
        public const string ColumnAction = "SecSettings.ColumnAction";
        public const string ColumnMemberChanges = "SecSettings.ColumnMemberChanges";
        public const string AddGroup = "SecSettings.AddGroup";
        public const string PickGroup = "SecSettings.PickGroup";
        public const string RemoveGroup = "SecSettings.RemoveGroup";
        public const string MembersDefined = "SecSettings.MembersDefined";
        public const string MembersHeader = "SecSettings.MembersHeader";
        public const string MemberOfHeader = "SecSettings.MemberOfHeader";
        public const string NoMembers = "SecSettings.NoMembers";
        public const string NoMembersWarning = "SecSettings.NoMembersWarning";
        public const string NoMemberChanges = "SecSettings.NoMemberChanges";
        public const string SelectRestrictedGroup = "SecSettings.SelectRestrictedGroup";
        public const string SelectGppGroup = "SecSettings.SelectGppGroup";
        public const string GppActionCreate = "SecSettings.GppActionCreate";
        public const string GppActionReplace = "SecSettings.GppActionReplace";
        public const string GppActionUpdate = "SecSettings.GppActionUpdate";
        public const string GppActionDelete = "SecSettings.GppActionDelete";
        public const string GppMemberAdd = "SecSettings.GppMemberAdd";
        public const string GppMemberRemove = "SecSettings.GppMemberRemove";
        public const string GppNewMemberRemoves = "SecSettings.GppNewMemberRemoves";
        public const string DeleteAllUsers = "SecSettings.DeleteAllUsers";
        public const string DeleteAllGroups = "SecSettings.DeleteAllGroups";
        public const string TargetingBadge = "SecSettings.TargetingBadge";
        public const string DisabledBadge = "SecSettings.DisabledBadge";

        // User rights
        public const string ColumnRight = "SecSettings.ColumnRight";
        public const string ColumnConstant = "SecSettings.ColumnConstant";
        public const string ColumnHolders = "SecSettings.ColumnHolders";
        public const string DefineRight = "SecSettings.DefineRight";
        public const string HoldersHeader = "SecSettings.HoldersHeader";
        public const string SelectRight = "SecSettings.SelectRight";
        public const string UnknownRight = "SecSettings.UnknownRight";

        // Account policies and security options
        public const string ColumnSetting = "SecSettings.ColumnSetting";
        public const string ColumnValue = "SecSettings.ColumnValue";
        public const string ColumnSection = "SecSettings.ColumnSection";
        public const string ColumnKey = "SecSettings.ColumnKey";
        public const string DefineOption = "SecSettings.DefineOption";
        public const string SelectOption = "SecSettings.SelectOption";
        public const string Enabled = "SecSettings.Enabled";
        public const string Disabled = "SecSettings.Disabled";
        public const string Range = "SecSettings.Range";
        public const string NotANumber = "SecSettings.NotANumber";
        public const string OutOfRange = "SecSettings.OutOfRange";
        public const string ChoiceRequired = "SecSettings.ChoiceRequired";
        public const string TextRequired = "SecSettings.TextRequired";
        public const string TextInvalid = "SecSettings.TextInvalid";
        public const string EmptyText = "SecSettings.EmptyText";
        public const string MultiTextHint = "SecSettings.MultiTextHint";
        public const string PasswordLengthNeedsRelax = "SecSettings.PasswordLengthNeedsRelax";
        public const string DomainAccountPolicyNote = "SecSettings.DomainAccountPolicyNote";
        public const string RawGroup = "SecSettings.RawGroup";
        public const string RawHint = "SecSettings.RawHint";
        public const string RemoveRawEntry = "SecSettings.RemoveRawEntry";
        public const string ReplaceRawValue = "SecSettings.ReplaceRawValue";

        // Audit
        public const string LegacyAuditHeader = "SecSettings.LegacyAuditHeader";
        public const string AdvancedAuditHeader = "SecSettings.AdvancedAuditHeader";
        public const string AuditNotConfigured = "SecSettings.AuditNotConfigured";
        public const string AuditNone = "SecSettings.AuditNone";
        public const string AuditSuccess = "SecSettings.AuditSuccess";
        public const string AuditFailure = "SecSettings.AuditFailure";
        public const string AuditBoth = "SecSettings.AuditBoth";
        public const string AuditBothNote = "SecSettings.AuditBothNote";

        // Services
        public const string ColumnService = "SecSettings.ColumnService";
        public const string ColumnStartMode = "SecSettings.ColumnStartMode";
        public const string ColumnSecurity = "SecSettings.ColumnSecurity";
        public const string ServiceAutomatic = "SecSettings.ServiceAutomatic";
        public const string ServiceManual = "SecSettings.ServiceManual";
        public const string ServiceDisabled = "SecSettings.ServiceDisabled";
        public const string ServiceModeOther = "SecSettings.ServiceModeOther";
        public const string NoSecurityDescriptor = "SecSettings.NoSecurityDescriptor";
        public const string ServiceNamePlaceholder = "SecSettings.ServiceNamePlaceholder";
        public const string ServiceNameInvalid = "SecSettings.ServiceNameInvalid";
        public const string ServiceSecurityNote = "SecSettings.ServiceSecurityNote";
        public const string AddService = "SecSettings.AddService";
        public const string RemoveService = "SecSettings.RemoveService";
    }
}
