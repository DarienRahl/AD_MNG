using AdMng.Presentation.GroupPolicy.Search;

namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>
    /// "Wyszukiwarka ustawień GPO" (K7, docs/dev/k-computers-and-gpo.md 21.4, 21.8): title, entry points, fields, facets, the
    /// group summary, notes, row actions and the export headers. Fields, areas and agreements are dynamic families
    /// (<see cref="FieldKey"/>, <see cref="AreaKey"/>, <see cref="AgreementKey"/>, <see cref="AllKeys"/>).
    /// </summary>
    public static class GpoSearch
    {
        public const string Title = "GpoSearch.Title";

        /// <summary>"Wyszukaj ustawienie we wszystkich GPO…" (palette <c>gpo.searchSettings</c>, menu Akcja → Zasady grupy, tree).</summary>
        public const string Command = "GpoSearch.Command";

        /// <summary>"Wyszukaj ustawienia…" on the toolbar of "Obiekty zasad grupy".</summary>
        public const string ToolbarCommand = "GpoSearch.ToolbarCommand";

        /// <summary>"Znajdź we wszystkich GPO" on the GPO document's "Ustawienia" tab.</summary>
        public const string FindInAll = "GpoSearch.FindInAll";
        public const string FindInAllHint = "GpoSearch.FindInAllHint";

        public const string SearchPlaceholder = "GpoSearch.SearchPlaceholder";
        public const string SearchHint = "GpoSearch.SearchHint";
        public const string FieldLabel = "GpoSearch.FieldLabel";
        public const string ScopeLabel = "GpoSearch.ScopeLabel";
        public const string AllScopes = "GpoSearch.AllScopes";
        public const string ScopeComputer = "GpoSearch.ScopeComputer";
        public const string ScopeUser = "GpoSearch.ScopeUser";
        public const string AreaLabel = "GpoSearch.AreaLabel";
        public const string AllAreas = "GpoSearch.AllAreas";

        /// <summary>{0} = "Preferencje", {1} = the preference type ("Mapowania dysków").</summary>
        public const string PreferenceArea = "GpoSearch.PreferenceArea";
        public const string OnlyDifferent = "GpoSearch.OnlyDifferent";
        public const string OnlyDifferentHint = "GpoSearch.OnlyDifferentHint";
        public const string ClearFilters = "GpoSearch.ClearFilters";

        // Reading the index
        public const string Refresh = "GpoSearch.Refresh";
        public const string RefreshHint = "GpoSearch.RefreshHint";
        public const string ReadAll = "GpoSearch.ReadAll";
        public const string ReadAllHint = "GpoSearch.ReadAllHint";
        public const string Preparing = "GpoSearch.Preparing";
        public const string NeverRead = "GpoSearch.NeverRead";
        public const string Cancelled = "GpoSearch.Cancelled";
        public const string Stale = "GpoSearch.Stale";

        /// <summary>{0} = the GPOs seen, {1} = the time of the read.</summary>
        public const string IndexInfo = "GpoSearch.IndexInfo";

        // Summary and results
        public const string SummaryHeading = "GpoSearch.SummaryHeading";

        /// <summary>{0} = the area, {1} = settings, {2} = occurrences, {3} = GPOs.</summary>
        public const string SummaryArea = "GpoSearch.SummaryArea";

        /// <summary>{0} = settings, {1} = occurrences, {2} = GPOs with settings.</summary>
        public const string SummaryTotal = "GpoSearch.SummaryTotal";

        /// <summary>{0} = settings (groups), {1} = occurrences.</summary>
        public const string Results = "GpoSearch.Results";
        public const string NoResults = "GpoSearch.NoResults";

        /// <summary>{0} = groups shown ("2 000"), {1} = groups found.</summary>
        public const string Capped = "GpoSearch.Capped";
        public const string ListLabel = "GpoSearch.ListLabel";

        // Rows
        public const string ColumnMarks = "GpoSearch.ColumnMarks";
        public const string MarkConditional = "GpoSearch.MarkConditional";
        public const string MarkDisabled = "GpoSearch.MarkDisabled";
        public const string MarkMasked = "GpoSearch.MarkMasked";
        public const string MarkIncomplete = "GpoSearch.MarkIncomplete";

        /// <summary>{0} = the container of a disabled link.</summary>
        public const string LinkDisabled = "GpoSearch.LinkDisabled";

        // Notes
        public const string NoteNoDefinitions = "GpoSearch.NoteNoDefinitions";
        public const string NoteDefinitionsFailed = "GpoSearch.NoteDefinitionsFailed";

        /// <summary>{0} = the masked occurrences.</summary>
        public const string NoteMasked = "GpoSearch.NoteMasked";

        /// <summary>{0} = the GPO, {1} = its read status (the tool tip of a note).</summary>
        public const string NoteGpo = "GpoSearch.NoteGpo";

        // Details and actions
        public const string DetailsHeading = "GpoSearch.DetailsHeading";
        public const string DetailsEmpty = "GpoSearch.DetailsEmpty";
        public const string DetailsPath = "GpoSearch.DetailsPath";
        public const string DetailsTechnical = "GpoSearch.DetailsTechnical";

        /// <summary>{0} = why the setting cannot be copied.</summary>
        public const string DetailsRefusal = "GpoSearch.DetailsRefusal";
        public const string OpenGpo = "GpoSearch.OpenGpo";
        public const string EditSetting = "GpoSearch.EditSetting";
        public const string Compare = "GpoSearch.Compare";
        public const string CompareHint = "GpoSearch.CompareHint";
        public const string CopyTo = "GpoSearch.CopyTo";
        public const string CopyToHint = "GpoSearch.CopyToHint";
        public const string Copied = "GpoSearch.Copied";
        public const string ExportCsv = "GpoSearch.ExportCsv";
        public const string ExportHtml = "GpoSearch.ExportHtml";
        public const string HtmlFilter = "GpoSearch.HtmlFilter";

        // Export
        public const string ColumnAgreement = "GpoSearch.ColumnAgreement";
        public const string ColumnPath = "GpoSearch.ColumnPath";
        public const string ColumnElements = "GpoSearch.ColumnElements";

        /// <summary>{0} = the domain.</summary>
        public const string ReportTitle = "GpoSearch.ReportTitle";

        /// <summary>{0} = the DC, {1} = the query, {2} = the time of the read.</summary>
        public const string ReportSubtitle = "GpoSearch.ReportSubtitle";
        public const string ReportPrivacy = "GpoSearch.ReportPrivacy";

        /// <summary><c>GpoSearch.Field.&lt;GpoSearchField&gt;</c>.</summary>
        public static string FieldKey(GpoSearchField field) => "GpoSearch.Field." + field;

        /// <summary><c>GpoSearch.Area.&lt;GpoSearchArea&gt;</c>.</summary>
        public static string AreaKey(GpoSearchArea area) => "GpoSearch.Area." + area;

        /// <summary><c>GpoSearch.Agreement.&lt;GpoValueAgreement&gt;</c>: {0} = the GPOs that set the unit.</summary>
        public static string AgreementKey(GpoValueAgreement agreement) => "GpoSearch.Agreement." + agreement;

        /// <summary>The keys of the dynamic families (the localization tests check both languages).</summary>
        public static IEnumerable<string> AllKeys() =>
        [
            .. Enum.GetValues<GpoSearchField>().Select(FieldKey),
            .. Enum.GetValues<GpoSearchArea>().Select(AreaKey),
            .. Enum.GetValues<GpoValueAgreement>().Select(AgreementKey),
        ];
    }
}
