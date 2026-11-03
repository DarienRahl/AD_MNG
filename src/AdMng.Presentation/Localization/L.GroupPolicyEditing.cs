namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>Editing comfort of the Group Policy editors (K6, docs/dev/k-computers-and-gpo.md 20.3).</summary>
    public static class GroupPolicyEditing
    {
        /// <summary>The palette category of the commands of the active document ("Dokument").</summary>
        public const string DocumentCategory = "CommandCategories.Document";

        /// <summary>"Zapisz zmiany…" of the active editor (Ctrl+S, palette).</summary>
        public const string SaveCommand = "GpEditing.SaveCommand";

        /// <summary>"Edytuj…" of a link in "Zasady grupy" (opens the GPO's settings).</summary>
        public const string EditGpoCommand = "GpEditing.EditGpoCommand";

        /// <summary>"Zmiany ustawień ({0})" above the rows of the change plan preview.</summary>
        public const string SettingChanges = "GpEditing.SettingChanges";

        public const string ColumnArea = "GpEditing.ColumnArea";

        /// <summary>The value in a summary when the setting has none ("—").</summary>
        public const string NoValue = "GpEditing.NoValue";

        // K3
        public const string InvalidKept = "GpEditing.InvalidKept";
        public const string InvalidMark = "GpEditing.InvalidMark";
        public const string RegistryValueLine = "GpEditing.RegistryValueLine";
        public const string SettingNotFound = "GpEditing.SettingNotFound";
        public const string ExtraValue = "GpEditing.ExtraValue";

        // K4
        public const string SecuritySearchPlaceholder = "GpEditing.SecuritySearchPlaceholder";
        public const string OnlyDefined = "GpEditing.OnlyDefined";
        public const string MembersOf = "GpEditing.MembersOf";
        public const string MemberOfOf = "GpEditing.MemberOfOf";
        public const string GppItemSummary = "GpEditing.GppItemSummary";

        // GPO list
        public const string GpoSearchPlaceholder = "GpEditing.GpoSearchPlaceholder";
        public const string GpoNoMatches = "GpEditing.GpoNoMatches";
        public const string GposShown = "GpEditing.GposShown";

        /// <summary>The tooltip of a search box: "Ctrl+F – szukaj, Esc – wyczyść".</summary>
        public const string SearchHint = "GpEditing.SearchHint";
    }
}
