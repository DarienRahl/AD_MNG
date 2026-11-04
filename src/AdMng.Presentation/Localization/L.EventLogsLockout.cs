namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>"Skąd blokada konta – &lt;konto&gt;" (track E2, docs/dev/e-event-logs.md 4.1); its notes are the dynamic family <c>EventLogs.Lockout.&lt;key&gt;</c>.</summary>
    public static class EventLogsLockout
    {
        /// <summary>{0} = the account.</summary>
        public const string Title = "EvtLock.Title";

        public const string NeverRun = "EvtLock.NeverRun";

        public const string Run = "EvtLock.Run";

        public const string WindowLabel = "EvtLock.WindowLabel";

        public const string StageAccount = "EvtLock.StageAccount";

        public const string StageDomainControllers = "EvtLock.StageDomainControllers";

        /// <summary>{0} = the reading progress.</summary>
        public const string StageLogs = "EvtLock.StageLogs";

        public const string StageAnalysis = "EvtLock.StageAnalysis";

        /// <summary>{0} sources, {1} lockouts, {2} events.</summary>
        public const string StatusDone = "EvtLock.StatusDone";

        public const string AccountNotFound = "EvtLock.AccountNotFound";

        public const string NotAUser = "EvtLock.NotAUser";

        public const string AccountNotOnDc = "EvtLock.AccountNotOnDc";

        public const string SummaryHeading = "EvtLock.SummaryHeading";

        /// <summary>{0} = the DCs.</summary>
        public const string LockedNow = "EvtLock.LockedNow";

        public const string NotLockedNow = "EvtLock.NotLockedNow";

        /// <summary>{0} = the time, {1} = the DC.</summary>
        public const string LastLockout = "EvtLock.LastLockout";

        /// <summary>{0} = the time.</summary>
        public const string LastLockoutState = "EvtLock.LastLockoutState";

        public const string NoLockout = "EvtLock.NoLockout";

        /// <summary>{0} = the threshold, {1} = the observation window, {2} = the duration, {3} = where the policy comes from.</summary>
        public const string Policy = "EvtLock.Policy";

        public const string PolicyNever = "EvtLock.PolicyNever";

        public const string PolicyDomain = "EvtLock.PolicyDomain";

        /// <summary>{0} = the PSO name.</summary>
        public const string PolicyPso = "EvtLock.PolicyPso";

        public const string UntilUnlocked = "EvtLock.UntilUnlocked";

        public const string Counts = "EvtLock.Counts";

        public const string Window = "EvtLock.Window";

        public const string SourcesHeading = "EvtLock.SourcesHeading";

        public const string NoSources = "EvtLock.NoSources";

        /// <summary>{0} = why.</summary>
        public const string ProxyHint = "EvtLock.ProxyHint";

        public const string SourceIsDomainController = "EvtLock.SourceIsDomainController";

        /// <summary>{0} = the names.</summary>
        public const string RelatedNames = "EvtLock.RelatedNames";

        public const string TimelineHeading = "EvtLock.TimelineHeading";

        public const string DomainControllersHeading = "EvtLock.DomainControllersHeading";

        public const string NotesHeading = "EvtLock.NotesHeading";

        public const string AuditHeading = "EvtLock.AuditHeading";

        public const string AuditHint = "EvtLock.AuditHint";

        public const string Originating = "EvtLock.Originating";

        public const string NotLocked = "EvtLock.NotLocked";

        public const string DcRecords = "EvtLock.DcRecords";

        public const string ColumnRank = "EvtLock.ColumnRank";

        public const string ColumnKind = "EvtLock.ColumnKind";

        public const string ColumnSource = "EvtLock.ColumnSource";

        public const string ColumnFailures = "EvtLock.ColumnFailures";

        public const string ColumnBadPasswords = "EvtLock.ColumnBadPasswords";

        public const string ColumnContributing = "EvtLock.ColumnContributing";

        public const string ColumnLockouts = "EvtLock.ColumnLockouts";

        public const string ColumnFirst = "EvtLock.ColumnFirst";

        public const string ColumnLast = "EvtLock.ColumnLast";

        public const string ColumnDcs = "EvtLock.ColumnDcs";

        public const string ColumnCodes = "EvtLock.ColumnCodes";

        public const string ColumnHint = "EvtLock.ColumnHint";

        public const string ColumnDc = "EvtLock.ColumnDc";

        public const string ColumnRoles = "EvtLock.ColumnRoles";

        public const string ColumnLocked = "EvtLock.ColumnLocked";

        public const string ColumnBadPwdCount = "EvtLock.ColumnBadPwdCount";

        public const string ColumnBadPasswordTime = "EvtLock.ColumnBadPasswordTime";

        public const string ColumnLastLogon = "EvtLock.ColumnLastLogon";

        public const string ColumnSubcategory = "EvtLock.ColumnSubcategory";

        public const string ColumnRecords = "EvtLock.ColumnRecords";

        public const string Pivot = "EvtLock.Pivot";

        public const string PivotHeadingLabel = "EvtLock.PivotHeadingLabel";

        /// <summary>{0} = the source.</summary>
        public const string PivotResolving = "EvtLock.PivotResolving";

        /// <summary>{0} = the address, {1} = its name.</summary>
        public const string PivotResolvedFromIp = "EvtLock.PivotResolvedFromIp";

        /// <summary>{0} = the source.</summary>
        public const string PivotNotFound = "EvtLock.PivotNotFound";

        public const string PivotNotFoundHint = "EvtLock.PivotNotFoundHint";

        /// <summary>{0} = the name, {1} = the DNS name.</summary>
        public const string PivotComputer = "EvtLock.PivotComputer";

        public const string PivotNotAvailable = "EvtLock.PivotNotAvailable";

        /// <summary>{0} = the sessions.</summary>
        public const string PivotSessions = "EvtLock.PivotSessions";

        public const string PivotConsole = "EvtLock.PivotConsole";

        public const string PivotNoSessions = "EvtLock.PivotNoSessions";

        /// <summary>{0} = the services.</summary>
        public const string PivotServices = "EvtLock.PivotServices";

        public const string PivotNoServices = "EvtLock.PivotNoServices";

        /// <summary>{0} = the tasks.</summary>
        public const string PivotTasks = "EvtLock.PivotTasks";

        public const string PivotTaskStoresPassword = "EvtLock.PivotTaskStoresPassword";

        public const string PivotNoTasks = "EvtLock.PivotNoTasks";

        /// <summary>{0} = the error.</summary>
        public const string PivotFailed = "EvtLock.PivotFailed";

        public const string OpenRemote = "EvtLock.OpenRemote";

        public const string ShowInModel = "EvtLock.ShowInModel";

        public const string GroupPolicyDiagnostics = "EvtLock.GroupPolicyDiagnostics";

        public const string WatchdogHistory = "EvtLock.WatchdogHistory";

        /// <summary>The policy line when the account's PSO cannot be read; {0} = the PSO's name.</summary>
        public const string PolicyPsoUnreadable = "EvtLock.PolicyPsoUnreadable";
    }
}
