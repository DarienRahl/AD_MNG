using AdMng.Core.GroupPolicy.Cleanup;

namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>
    /// "Porządki w GPO" (K7, docs/dev/k-computers-and-gpo.md 21.6, 21.8): the cleanup advisor's document. The kinds, actions
    /// and notes of the proposals are dynamic families (<see cref="KindTitleKey"/>, <see cref="KindMessageKey"/>,
    /// <see cref="KindRecommendationKey"/>, <see cref="ActionKey"/>, <see cref="NoteKey"/>, <see cref="AllKeys"/>).
    /// </summary>
    public static class GpoCleanup
    {
        public const string Title = "GpoCleanup.Title";

        /// <summary>"Porządki w GPO…" (menu Akcja → Zasady grupy, palette <c>gpo.cleanup</c>, tree).</summary>
        public const string Command = "GpoCleanup.Command";

        /// <summary>"Porządki…" on the toolbar of "Obiekty zasad grupy".</summary>
        public const string ToolbarCommand = "GpoCleanup.ToolbarCommand";

        public const string Analyze = "GpoCleanup.Analyze";
        public const string AnalyzeHint = "GpoCleanup.AnalyzeHint";
        public const string ReadAll = "GpoCleanup.ReadAll";
        public const string ReadAllHint = "GpoCleanup.ReadAllHint";
        public const string Stale = "GpoCleanup.Stale";
        public const string NeverRun = "GpoCleanup.NeverRun";
        public const string Cancelled = "GpoCleanup.Cancelled";
        public const string AnalyzeStage = "GpoCleanup.AnalyzeStage";

        /// <summary>{0} = GPOs analysed, {1} = when the settings were read.</summary>
        public const string LastRun = "GpoCleanup.LastRun";
        public const string ProposalsOne = "GpoCleanup.ProposalsOne";
        public const string ProposalsFew = "GpoCleanup.ProposalsFew";
        public const string ProposalsMany = "GpoCleanup.ProposalsMany";

        /// <summary>{0} shown of {1}.</summary>
        public const string ProposalsShown = "GpoCleanup.ProposalsShown";
        public const string NoProposals = "GpoCleanup.NoProposals";
        public const string NoMatches = "GpoCleanup.NoMatches";

        /// <summary>{0} = the number of GPOs, {1} = their names with the reason.</summary>
        public const string NoteNotEvaluated = "GpoCleanup.NoteNotEvaluated";

        // List, chips and filters
        public const string ColumnKind = "GpoCleanup.ColumnKind";
        public const string ColumnProposal = "GpoCleanup.ColumnProposal";
        public const string ColumnActions = "GpoCleanup.ColumnActions";
        public const string ColumnRelated = "GpoCleanup.ColumnRelated";
        public const string ColumnPath = "GpoCleanup.ColumnPath";

        /// <summary>{0} = the kind, {1} = the count.</summary>
        public const string Chip = "GpoCleanup.Chip";
        public const string SummaryLabel = "GpoCleanup.SummaryLabel";
        public const string FilterKind = "GpoCleanup.FilterKind";
        public const string FilterGpo = "GpoCleanup.FilterGpo";
        public const string AllKinds = "GpoCleanup.AllKinds";
        public const string AllGpos = "GpoCleanup.AllGpos";
        public const string SearchPlaceholder = "GpoCleanup.SearchPlaceholder";
        public const string ClearFilters = "GpoCleanup.ClearFilters";

        // Details
        public const string DetailsHeading = "GpoCleanup.DetailsHeading";
        public const string DetailsEmpty = "GpoCleanup.DetailsEmpty";
        public const string WhyHeading = "GpoCleanup.WhyHeading";
        public const string WhatHeading = "GpoCleanup.WhatHeading";
        public const string GposHeading = "GpoCleanup.GposHeading";
        public const string SettingHeading = "GpoCleanup.SettingHeading";
        public const string NotesHeading = "GpoCleanup.NotesHeading";
        public const string NoActions = "GpoCleanup.NoActions";

        /// <summary>{0} = the related GPOs.</summary>
        public const string RelatedLine = "GpoCleanup.RelatedLine";

        /// <summary>{0} = the containers linking the GPO.</summary>
        public const string LinksLine = "GpoCleanup.LinksLine";
        public const string NoLinks = "GpoCleanup.NoLinks";

        /// <summary>{0} = the setting units that cannot be copied.</summary>
        public const string BlockingLine = "GpoCleanup.BlockingLine";
        public const string OpenGpo = "GpoCleanup.OpenGpo";
        public const string Compare = "GpoCleanup.Compare";
        public const string ShowInSearch = "GpoCleanup.ShowInSearch";
        public const string Copied = "GpoCleanup.Copied";

        // Export
        public const string ExportCsv = "GpoCleanup.ExportCsv";
        public const string ExportHtml = "GpoCleanup.ExportHtml";

        /// <summary>{0} = the domain.</summary>
        public const string ReportTitle = "GpoCleanup.ReportTitle";

        /// <summary>{0} = the DC, {1} = when the settings were read.</summary>
        public const string ReportSubtitle = "GpoCleanup.ReportSubtitle";

        /// <summary>{0} shown of {1}.</summary>
        public const string ReportFilters = "GpoCleanup.ReportFilters";
        public const string ReportPrivacy = "GpoCleanup.ReportPrivacy";

        /// <summary><c>GpoCleanup.Kind.&lt;Kind&gt;.Title</c>.</summary>
        public static string KindTitleKey(GpoCleanupKind kind) => "GpoCleanup.Kind." + kind + ".Title";

        /// <summary><c>GpoCleanup.Kind.&lt;Kind&gt;.Message</c>: {0} = the GPO, {1} = the related GPOs, {2} = the setting.</summary>
        public static string KindMessageKey(GpoCleanupKind kind) => "GpoCleanup.Kind." + kind + ".Message";

        /// <summary><c>GpoCleanup.Kind.&lt;Kind&gt;.Recommendation</c>: {0} = the GPO, {1} = the related GPOs, {2} = the setting.</summary>
        public static string KindRecommendationKey(GpoCleanupKind kind) => "GpoCleanup.Kind." + kind + ".Recommendation";

        /// <summary><c>GpoCleanup.Action.&lt;Action&gt;</c>.</summary>
        public static string ActionKey(GpoCleanupAction action) => "GpoCleanup.Action." + action;

        /// <summary><c>GpoCleanup.Note.&lt;Note&gt;</c>.</summary>
        public static string NoteKey(GpoCleanupNote note) => "GpoCleanup.Note." + note;

        /// <summary>Every key of the dynamic families (the localization tests check both languages).</summary>
        public static IEnumerable<string> AllKeys() =>
        [
            .. Enum.GetValues<GpoCleanupKind>().SelectMany(k => new[] { KindTitleKey(k), KindMessageKey(k), KindRecommendationKey(k) }),
            .. Enum.GetValues<GpoCleanupAction>().Select(ActionKey),
            .. Enum.GetValues<GpoCleanupNote>().Select(NoteKey),
        ];
    }
}
