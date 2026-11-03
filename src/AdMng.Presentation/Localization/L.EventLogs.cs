namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>"Dzienniki zdarzeń" (track E1, docs/dev/e-event-logs.md 5): the reader, the shared texts of the event log documents and their entry points. The texts of codes, levels, channels, access errors, raw XPath reasons and the analysis enums are dynamic families (<see cref="AdMng.Presentation.EventLogs.EventLogText.AllKeys"/>).</summary>
    public static class EventLogs
    {
        public const string Title = "EvtLog.Title";

        /// <summary>Narzędzia → "Dzienniki zdarzeń…", palette events.open.</summary>
        public const string Command = "EvtLog.Command";

        /// <summary>The computer context menu and its "Stan" page, palette events.computer.</summary>
        public const string ComputerCommand = "EvtLog.ComputerCommand";

        /// <summary>The user context menu, the "Konto" tab, the account review, palette events.lockout.</summary>
        public const string LockoutCommand = "EvtLog.LockoutCommand";

        /// <summary>Narzędzia → "Nieudane logowania…", palette events.failedLogons.</summary>
        public const string FailedLogonsCommand = "EvtLog.FailedLogonsCommand";

        public const string NotSupported = "EvtLog.NotSupported";

        public const string NotSupportedSimpleBind = "EvtLog.NotSupportedSimpleBind";

        /// <summary>The first line of a CSV export and the top of an HTML export (operator decision).</summary>
        public const string PersonalDataNote = "EvtLog.PersonalDataNote";

        public const string ExportCsv = "EvtLog.ExportCsv";

        public const string ExportHtml = "EvtLog.ExportHtml";

        public const string HtmlFilter = "EvtLog.HtmlFilter";

        public const string Run = "EvtLog.Run";

        public const string Cancelled = "EvtLog.Cancelled";

        public const string NeverRun = "EvtLog.NeverRun";

        /// <summary>{0} sources done, {1} sources, {2} events.</summary>
        public const string StatusReading = "EvtLog.StatusReading";

        /// <summary>{0} events, {1} sources.</summary>
        public const string StatusDone = "EvtLog.StatusDone";

        /// <summary>{0} events, {1} sources, {2} failed.</summary>
        public const string StatusDoneWithFailures = "EvtLog.StatusDoneWithFailures";

        /// <summary>{0} events.</summary>
        public const string StatusCancelled = "EvtLog.StatusCancelled";

        /// <summary>{0} kept, {1} read.</summary>
        public const string RecordsDropped = "EvtLog.RecordsDropped";

        public const string NoRecords = "EvtLog.NoRecords";

        public const string NoMatches = "EvtLog.NoMatches";

        public const string NoSourceSelected = "EvtLog.NoSourceSelected";

        public const string SourcesHeading = "EvtLog.SourcesHeading";

        public const string AddComputer = "EvtLog.AddComputer";

        public const string ComputerPlaceholder = "EvtLog.ComputerPlaceholder";

        public const string PickComputers = "EvtLog.PickComputers";

        public const string OpenFiles = "EvtLog.OpenFiles";

        public const string EvtxFilter = "EvtLog.EvtxFilter";

        public const string RemoveSource = "EvtLog.RemoveSource";

        public const string KindDomainController = "EvtLog.KindDomainController";

        public const string KindPdc = "EvtLog.KindPdc";

        public const string KindComputer = "EvtLog.KindComputer";

        public const string KindFiles = "EvtLog.KindFiles";

        /// <summary>{0} = the error.</summary>
        public const string NoDomainControllers = "EvtLog.NoDomainControllers";

        /// <summary>{0} = the typed name.</summary>
        public const string InvalidHost = "EvtLog.InvalidHost";

        /// <summary>{0} = the computer name(s).</summary>
        public const string ComputerNoDns = "EvtLog.ComputerNoDns";

        public const string QueryHeading = "EvtLog.QueryHeading";

        public const string ChannelLabel = "EvtLog.ChannelLabel";

        public const string LoadChannels = "EvtLog.LoadChannels";

        /// <summary>{0} = the host.</summary>
        public const string LoadingChannels = "EvtLog.LoadingChannels";

        /// <summary>{0} = the host, {1} = the count.</summary>
        public const string ChannelsLoaded = "EvtLog.ChannelsLoaded";

        public const string InvalidChannel = "EvtLog.InvalidChannel";

        public const string TimeLabel = "EvtLog.TimeLabel";

        public const string FromLabel = "EvtLog.FromLabel";

        public const string ToLabel = "EvtLog.ToLabel";

        public const string InvalidRange = "EvtLog.InvalidRange";

        public const string EventIdsLabel = "EvtLog.EventIdsLabel";

        public const string EventIdsPlaceholder = "EvtLog.EventIdsPlaceholder";

        /// <summary>{0} = the maximum count.</summary>
        public const string InvalidEventIds = "EvtLog.InvalidEventIds";

        public const string LevelsLabel = "EvtLog.LevelsLabel";

        public const string ProvidersLabel = "EvtLog.ProvidersLabel";

        public const string ProvidersPlaceholder = "EvtLog.ProvidersPlaceholder";

        public const string OutcomeLabel = "EvtLog.OutcomeLabel";

        public const string AccountLabel = "EvtLog.AccountLabel";

        public const string SidLabel = "EvtLog.SidLabel";

        public const string IpLabel = "EvtLog.IpLabel";

        public const string WorkstationLabel = "EvtLog.WorkstationLabel";

        public const string FieldLabel = "EvtLog.FieldLabel";

        public const string FieldsOnlySecurity = "EvtLog.FieldsOnlySecurity";

        /// <summary>{0} = the field label.</summary>
        public const string InvalidValue = "EvtLog.InvalidValue";

        public const string QueryRejected = "EvtLog.QueryRejected";

        public const string RawMode = "EvtLog.RawMode";

        public const string RawPlaceholder = "EvtLog.RawPlaceholder";

        public const string RawValid = "EvtLog.RawValid";

        public const string RawTimeNote = "EvtLog.RawTimeNote";

        public const string SavedQueriesLabel = "EvtLog.SavedQueriesLabel";

        public const string SaveQuery = "EvtLog.SaveQuery";

        public const string SaveQueryName = "EvtLog.SaveQueryName";

        public const string LoadQuery = "EvtLog.LoadQuery";

        public const string DeleteQuery = "EvtLog.DeleteQuery";

        /// <summary>{0} = the name.</summary>
        public const string DeleteQueryConfirm = "EvtLog.DeleteQueryConfirm";

        /// <summary>{0} = the name.</summary>
        public const string QuerySaved = "EvtLog.QuerySaved";

        public const string QueryNameRequired = "EvtLog.QueryNameRequired";

        public const string SearchPlaceholder = "EvtLog.SearchPlaceholder";

        public const string HostsHeading = "EvtLog.HostsHeading";

        public const string ColumnTime = "EvtLog.ColumnTime";

        public const string ColumnHost = "EvtLog.ColumnHost";

        public const string ColumnChannel = "EvtLog.ColumnChannel";

        public const string ColumnId = "EvtLog.ColumnId";

        public const string ColumnLevel = "EvtLog.ColumnLevel";

        public const string ColumnProvider = "EvtLog.ColumnProvider";

        public const string ColumnSummary = "EvtLog.ColumnSummary";

        public const string ColumnData = "EvtLog.ColumnData";

        public const string ColumnState = "EvtLog.ColumnState";

        public const string ColumnRecords = "EvtLog.ColumnRecords";

        public const string ColumnCoverage = "EvtLog.ColumnCoverage";

        public const string ColumnKind = "EvtLog.ColumnKind";

        /// <summary>{0} = the time.</summary>
        public const string CoverageOldest = "EvtLog.CoverageOldest";

        public const string CoverageEmpty = "EvtLog.CoverageEmpty";

        /// <summary>{0} = the count.</summary>
        public const string CoverageRecords = "EvtLog.CoverageRecords";

        /// <summary>{0} = the size, {1} = the maximum size.</summary>
        public const string CoverageSize = "EvtLog.CoverageSize";

        public const string CoverageFull = "EvtLog.CoverageFull";

        public const string CoverageDisabled = "EvtLog.CoverageDisabled";

        public const string CoverageUnknown = "EvtLog.CoverageUnknown";

        /// <summary>{0} = the host, {1} = the duration, {2} = the oldest record.</summary>
        public const string CoverageShort = "EvtLog.CoverageShort";

        public const string DurationMinutes = "EvtLog.DurationMinutes";

        public const string DurationHours = "EvtLog.DurationHours";

        public const string DurationDays = "EvtLog.DurationDays";

        public const string Megabytes = "EvtLog.Megabytes";

        /// <summary>{0} = the text, {1} = the Windows error code.</summary>
        public const string WithErrorCode = "EvtLog.WithErrorCode";

        public const string DetailsHeading = "EvtLog.DetailsHeading";

        public const string DetailsEmpty = "EvtLog.DetailsEmpty";

        public const string ShowMessage = "EvtLog.ShowMessage";

        public const string MessageUnavailable = "EvtLog.MessageUnavailable";

        public const string Copied = "EvtLog.Copied";

        public const string FieldTime = "EvtLog.FieldTime";

        public const string FieldHost = "EvtLog.FieldHost";

        public const string FieldComputer = "EvtLog.FieldComputer";

        public const string FieldChannel = "EvtLog.FieldChannel";

        public const string FieldProvider = "EvtLog.FieldProvider";

        public const string FieldEventId = "EvtLog.FieldEventId";

        public const string FieldLevel = "EvtLog.FieldLevel";

        public const string FieldRecordId = "EvtLog.FieldRecordId";

        public const string FieldUser = "EvtLog.FieldUser";

        public const string UnknownCode = "EvtLog.UnknownCode";

        public const string NoCode = "EvtLog.NoCode";

        /// <summary>{0} = the code as logged, {1} = its meaning.</summary>
        public const string CodeWithMeaning = "EvtLog.CodeWithMeaning";

        /// <summary>{0} = the event ID, {1} = the code with its meaning, {2} = the count.</summary>
        public const string CodeCountLine = "EvtLog.CodeCountLine";

        /// <summary>{0} = the type, {1} = its meaning.</summary>
        public const string LogonTypeShort = "EvtLog.LogonTypeShort";

        /// <summary>{0} = the kind, {1} = the value.</summary>
        public const string SourceWithKind = "EvtLog.SourceWithKind";

        /// <summary>{0} = the event ID, {1} = success or failure.</summary>
        public const string AuditEventLine = "EvtLog.AuditEventLine";

        public const string AuditSuccess = "EvtLog.AuditSuccess";

        public const string AuditFailure = "EvtLog.AuditFailure";

        /// <summary>{0} = the domain.</summary>
        public const string ReportTitle = "EvtLog.ReportTitle";
    }
}
