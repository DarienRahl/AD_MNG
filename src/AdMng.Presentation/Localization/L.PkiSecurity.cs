namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>
    /// <i>Przegląd bezpieczeństwa AD CS</i> (P1, docs/dev/p-certificate-services.md 2.5, W6): the document, its filters, list
    /// columns, details headings, the notes bar and the exports. The finding texts are <c>PkiFindingText</c> (contract block);
    /// the per-variant ESC14 messages and the names of the well-known principals the findings name by SID are here.
    /// </summary>
    public static class PkiSecurity
    {
        public const string Title = "PkiEsc.Title";

        /// <summary>"Sprawdź ponownie" (F5): the PKI, the accounts and the ACLs are read again.</summary>
        public const string CheckAgain = "PkiEsc.CheckAgain";
        public const string CheckAgainHint = "PkiEsc.CheckAgainHint";

        // Progress and status
        /// <summary>{0} = the PKI objects read so far.</summary>
        public const string StageSnapshot = "PkiEsc.StageSnapshot";

        /// <summary>{0} = the accounts streamed so far.</summary>
        public const string StageAccounts = "PkiEsc.StageAccounts";
        public const string StageAccessControl = "PkiEsc.StageAccessControl";
        public const string StagePrincipals = "PkiEsc.StagePrincipals";
        public const string StageAnalysis = "PkiEsc.StageAnalysis";

        /// <summary>{0} = the time, {1} = the DC.</summary>
        public const string LastRun = "PkiEsc.LastRun";
        public const string NeverRun = "PkiEsc.NeverRun";
        public const string Cancelled = "PkiEsc.Cancelled";
        public const string FindingsOne = "PkiEsc.FindingsOne";
        public const string FindingsFew = "PkiEsc.FindingsFew";
        public const string FindingsMany = "PkiEsc.FindingsMany";

        /// <summary>{0} shown of {1}.</summary>
        public const string FindingsShown = "PkiEsc.FindingsShown";
        public const string NoFindings = "PkiEsc.NoFindings";
        public const string NoMatches = "PkiEsc.NoMatches";
        public const string NotEvaluatedList = "PkiEsc.NotEvaluatedList";
        public const string NoPki = "PkiEsc.NoPki";

        // Notes bar
        public const string ScopeNote = "PkiEsc.ScopeNote";

        /// <summary>{0} = the rule groups that were not evaluated.</summary>
        public const string NoteNotEvaluated = "PkiEsc.NoteNotEvaluated";

        /// <summary>{0} = the notice text, {1} = the objects it concerns.</summary>
        public const string NoteWithObjects = "PkiEsc.NoteWithObjects";

        /// <summary>{0} = the number of further objects.</summary>
        public const string NoteMoreObjects = "PkiEsc.NoteMoreObjects";
        public const string GroupEnrollment = "PkiEsc.Group.Enrollment";
        public const string GroupAccessControl = "PkiEsc.Group.AccessControl";
        public const string GroupMapping = "PkiEsc.Group.Mapping";

        // Filters and list
        public const string FilterSeverity = "PkiEsc.FilterSeverity";
        public const string FilterEsc = "PkiEsc.FilterEsc";
        public const string FilterArea = "PkiEsc.FilterArea";
        public const string FilterTemplate = "PkiEsc.FilterTemplate";
        public const string AllSeverities = "PkiEsc.AllSeverities";
        public const string AllEsc = "PkiEsc.AllEsc";
        public const string AllAreas = "PkiEsc.AllAreas";
        public const string AllTemplates = "PkiEsc.AllTemplates";
        public const string SearchPlaceholder = "PkiEsc.SearchPlaceholder";
        public const string ClearFilters = "PkiEsc.ClearFilters";
        public const string SummaryLabel = "PkiEsc.SummaryLabel";
        public const string ColumnSeverity = "PkiEsc.ColumnSeverity";
        public const string ColumnEsc = "PkiEsc.ColumnEsc";
        public const string ColumnArea = "PkiEsc.ColumnArea";
        public const string ColumnObject = "PkiEsc.ColumnObject";
        public const string ColumnPrincipals = "PkiEsc.ColumnPrincipals";
        public const string ColumnFinding = "PkiEsc.ColumnFinding";

        /// <summary>{0} = the area, {1} = the object (the group header of the list).</summary>
        public const string GroupHeader = "PkiEsc.GroupHeader";

        // Details
        public const string DetailsHeading = "PkiEsc.DetailsHeading";
        public const string DetailsEmpty = "PkiEsc.DetailsEmpty";
        public const string ConditionHeading = "PkiEsc.ConditionHeading";
        public const string AttackPathHeading = "PkiEsc.AttackPathHeading";
        public const string WhatHeading = "PkiEsc.WhatHeading";
        public const string ObjectHeading = "PkiEsc.ObjectHeading";
        public const string PrincipalsHeading = "PkiEsc.PrincipalsHeading";
        public const string NotesHeading = "PkiEsc.NotesHeading";
        public const string RelatedHeading = "PkiEsc.RelatedHeading";
        public const string IdsHeading = "PkiEsc.IdsHeading";

        /// <summary>{0} = Certipy's ESC, {1} = Locksmith's technique, {2} = the MDI assessment ("–" when none).</summary>
        public const string Ids = "PkiEsc.Ids";
        public const string NoId = "PkiEsc.NoId";

        /// <summary>{0} = the name, {1} = the class, {2} = the account count text.</summary>
        public const string PrincipalLine = "PkiEsc.PrincipalLine";

        /// <summary>{0} = the principal line, {1} = the right.</summary>
        public const string PrincipalLineRight = "PkiEsc.PrincipalLineRight";

        /// <summary>{0} = the accounts.</summary>
        public const string AccountCount = "PkiEsc.AccountCount";
        public const string AccountCountUnknown = "PkiEsc.AccountCountUnknown";
        public const string AccountCountOtherDomain = "PkiEsc.AccountCountOtherDomain";

        /// <summary>The count argument "?" of an enrollment finding (no principal's count known).</summary>
        public const string CountUnknown = "PkiEsc.CountUnknown";
        public const string RelatedCa = "PkiEsc.RelatedCa";
        public const string RelatedTemplate = "PkiEsc.RelatedTemplate";
        public const string RelatedOid = "PkiEsc.RelatedOid";

        /// <summary>{0} = the number of accounts of the aggregated ESC14 finding.</summary>
        public const string AggregatedAccounts = "PkiEsc.AggregatedAccounts";

        // ESC14 per variant (the contract message is generic)
        /// <summary>{0} = the Tier 0 account, {1} = the weak mapping kinds.</summary>
        public const string Esc14Tier0Mapping = "PkiEsc.Esc14.Tier0Mapping";

        /// <summary>{0} = the number of accounts, {1} = the weak mapping kinds.</summary>
        public const string Esc14Aggregated = "PkiEsc.Esc14.Aggregated";

        /// <summary>{0} = the Tier 0 account, {1} = the rights, {2} = the principals.</summary>
        public const string Esc14Write = "PkiEsc.Esc14.Write";

        // Well-known principals the findings name by SID
        public const string PrincipalThisOrganization = "PkiEsc.Principal.ThisOrganization";

        // Actions
        public const string OpenTemplate = "PkiEsc.OpenTemplate";
        public const string OpenCa = "PkiEsc.OpenCa";
        public const string OpenAccount = "PkiEsc.OpenAccount";
        public const string OpenComputer = "PkiEsc.OpenComputer";
        public const string OpenObject = "PkiEsc.OpenObject";
        public const string NoFixNote = "PkiEsc.NoFixNote";
        public const string Copied = "PkiEsc.Copied";
        public const string ExportCsv = "PkiEsc.ExportCsv";
        public const string ExportHtml = "PkiEsc.ExportHtml";
        public const string HtmlFilter = "PkiEsc.HtmlFilter";

        /// <summary>{0} = the domain.</summary>
        public const string ReportTitle = "PkiEsc.ReportTitle";

        /// <summary>{0} = the DC, {1} = the time.</summary>
        public const string ReportSubtitle = "PkiEsc.ReportSubtitle";

        /// <summary>{0} shown of {1}.</summary>
        public const string ReportFilters = "PkiEsc.ReportFilters";
        public const string ReportPrivacy = "PkiEsc.ReportPrivacy";
    }
}
