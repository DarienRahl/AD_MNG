namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>
    /// "Diagnostyka zasad grupy" and the GPO document's "Stan" tab (K6, docs/dev/k-computers-and-gpo.md 20.2). The texts of
    /// the finding codes, severities, areas, rule groups, stages and repairs are dynamic families
    /// (<see cref="AdMng.Presentation.GroupPolicy.Diagnostics.GpoFindingText.AllKeys"/>).
    /// </summary>
    public static class GpoDiagnostics
    {
        public const string Title = "GpoDiag.Title";

        /// <summary>"Diagnostyka zasad grupy…" (menu Akcja → Zasady grupy, palette <c>gpo.diagnose</c>, tree).</summary>
        public const string Command = "GpoDiag.Command";

        /// <summary>"Diagnostyka…" on the toolbar of "Obiekty zasad grupy".</summary>
        public const string ToolbarCommand = "GpoDiag.ToolbarCommand";

        public const string QuickCheck = "GpoDiag.QuickCheck";
        public const string QuickCheckHint = "GpoDiag.QuickCheckHint";
        public const string FullCheck = "GpoDiag.FullCheck";
        public const string FullCheckHint = "GpoDiag.FullCheckHint";
        public const string CheckAgain = "GpoDiag.CheckAgain";

        /// <summary>{0} = the depth ("szybkie"/"pełne"), {1} = the time.</summary>
        public const string LastRun = "GpoDiag.LastRun";
        public const string DepthQuick = "GpoDiag.DepthQuick";
        public const string DepthFull = "GpoDiag.DepthFull";
        public const string NeverRun = "GpoDiag.NeverRun";
        public const string Stale = "GpoDiag.Stale";
        public const string Cancelled = "GpoDiag.Cancelled";
        public const string FindingsOne = "GpoDiag.FindingsOne";
        public const string FindingsFew = "GpoDiag.FindingsFew";
        public const string FindingsMany = "GpoDiag.FindingsMany";

        /// <summary>{0} shown of {1}.</summary>
        public const string FindingsShown = "GpoDiag.FindingsShown";
        public const string NoFindings = "GpoDiag.NoFindings";
        public const string NoMatches = "GpoDiag.NoMatches";
        public const string NamesStage = "GpoDiag.NamesStage";

        // List and filters
        public const string ColumnSeverity = "GpoDiag.ColumnSeverity";
        public const string ColumnArea = "GpoDiag.ColumnArea";
        public const string ColumnWhere = "GpoDiag.ColumnWhere";
        public const string ColumnFinding = "GpoDiag.ColumnFinding";
        public const string FilterSeverity = "GpoDiag.FilterSeverity";
        public const string FilterArea = "GpoDiag.FilterArea";
        public const string FilterGpo = "GpoDiag.FilterGpo";
        public const string AllSeverities = "GpoDiag.AllSeverities";
        public const string AllAreas = "GpoDiag.AllAreas";
        public const string AllGpos = "GpoDiag.AllGpos";
        public const string SearchPlaceholder = "GpoDiag.SearchPlaceholder";
        public const string ClearFilters = "GpoDiag.ClearFilters";
        public const string SummaryLabel = "GpoDiag.SummaryLabel";

        // Details
        public const string DetailsHeading = "GpoDiag.DetailsHeading";
        public const string DetailsEmpty = "GpoDiag.DetailsEmpty";
        public const string WhyHeading = "GpoDiag.WhyHeading";
        public const string WhatHeading = "GpoDiag.WhatHeading";
        public const string AffectedHeading = "GpoDiag.AffectedHeading";
        public const string AffectedGpo = "GpoDiag.AffectedGpo";
        public const string AffectedContainer = "GpoDiag.AffectedContainer";
        public const string AffectedRelated = "GpoDiag.AffectedRelated";
        public const string AffectedSetting = "GpoDiag.AffectedSetting";
        public const string AffectedSettingKey = "GpoDiag.AffectedSettingKey";
        public const string AffectedWmiFilter = "GpoDiag.AffectedWmiFilter";
        public const string FixLine = "GpoDiag.FixLine";
        public const string NoFix = "GpoDiag.NoFix";

        // Actions
        public const string Fix = "GpoDiag.Fix";
        public const string OpenGpo = "GpoDiag.OpenGpo";
        public const string EditSetting = "GpoDiag.EditSetting";
        public const string ShowInModel = "GpoDiag.ShowInModel";
        public const string Copied = "GpoDiag.Copied";
        public const string ExportCsv = "GpoDiag.ExportCsv";
        public const string ExportHtml = "GpoDiag.ExportHtml";
        public const string HtmlFilter = "GpoDiag.HtmlFilter";

        /// <summary>{0} = the domain.</summary>
        public const string ReportTitle = "GpoDiag.ReportTitle";

        /// <summary>{0} = the domain controller, {1} = the check ("pełne, 3.11.2026 14:32").</summary>
        public const string ReportSubtitle = "GpoDiag.ReportSubtitle";
        public const string ReportFilters = "GpoDiag.ReportFilters";
        public const string ReportPrivacy = "GpoDiag.ReportPrivacy";
        public const string TitleRemoveDuplicateLinks = "GpoDiag.TitleRemoveDuplicateLinks";
        public const string TitleRepairExtensions = "GpoDiag.TitleRepairExtensions";

        // What could not be checked
        public const string NoteQuick = "GpoDiag.NoteQuick";
        public const string NoteSysvol = "GpoDiag.NoteSysvol";
        public const string NoteFailed = "GpoDiag.NoteFailed";
        public const string NoteFoldersNotRead = "GpoDiag.NoteFoldersNotRead";
        public const string FolderAccessDenied = "GpoDiag.FolderAccessDenied";
        public const string FolderTooLarge = "GpoDiag.FolderTooLarge";
        public const string FolderFailed = "GpoDiag.FolderFailed";

        // Message arguments
        public const string PartUser = "GpoDiag.PartUser";
        public const string PartComputer = "GpoDiag.PartComputer";
        public const string MissingFolder = "GpoDiag.MissingFolder";
        public const string MissingGptIni = "GpoDiag.MissingGptIni";
        public const string PreferenceItem = "GpoDiag.PreferenceItem";
        public const string SettingRestrictedGroup = "GpoDiag.SettingRestrictedGroup";
        public const string SettingLocalGroups = "GpoDiag.SettingLocalGroups";
        public const string SettingFile = "GpoDiag.SettingFile";

        // "Stan" tab of a GPO
        public const string HealthHint = "GpoDiag.HealthHint";
        public const string HealthNone = "GpoDiag.HealthNone";
        public const string HealthChecking = "GpoDiag.HealthChecking";
        public const string AreaOk = "GpoDiag.AreaOk";
        public const string AreaInfo = "GpoDiag.AreaInfo";

        /// <summary>{0} = the number of findings, {1} = the most serious severity.</summary>
        public const string AreaFindings = "GpoDiag.AreaFindings";

        /// <summary>{0} = why ("tylko pełne sprawdzenie", "SYSVOL niedostępny").</summary>
        public const string AreaNotChecked = "GpoDiag.AreaNotChecked";

        /// <summary>{0} = the area's result, {1} = why a part of it was not checked.</summary>
        public const string AreaPartly = "GpoDiag.AreaPartly";
        public const string ReasonQuickOnly = "GpoDiag.ReasonQuickOnly";
        public const string ReasonSysvol = "GpoDiag.ReasonSysvol";
        public const string ReasonFailed = "GpoDiag.ReasonFailed";

        /// <summary>The recommendation of an unknown SID whose entries are all inherited (no repair on the GPO: the parent object).</summary>
        public const string OrphanedTrusteeInheritedRecommendation = "GpoFinding.OrphanedTrustee.RecommendationInherited";
    }
}
