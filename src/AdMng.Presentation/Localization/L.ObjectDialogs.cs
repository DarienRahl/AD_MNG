namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>
    /// Texts of the WPF object dialogs (property sheet chrome, New Object, Reset Password, pickers, Find) that no view
    /// model owns. Used by the XAML of AdMng.App; listed here so the localization tests check both languages.
    /// </summary>
    public static class ObjectDialogs
    {
        public const string PendingChanges = "ObjectDialogs.PendingChanges";
        public const string NewValue = "ObjectDialogs.NewValue";
        public const string Values = "ObjectDialogs.Values";
        public const string Primary = "ObjectDialogs.Primary";
        public const string SetPrimaryGroup = "ObjectDialogs.SetPrimaryGroup";
        public const string SamTemplate = "ObjectDialogs.SamTemplate";
        public const string PasswordLength = "ObjectDialogs.PasswordLength";
        public const string GeneratedPasswordsHint = "ObjectDialogs.GeneratedPasswordsHint";
        public const string SelectedObjects = "ObjectDialogs.SelectedObjects";
        public const string SearchResults = "ObjectDialogs.SearchResults";
        public const string FindKind = "ObjectDialogs.FindKind";
        public const string AddCondition = "ObjectDialogs.AddCondition";
        public const string RebuildFilter = "ObjectDialogs.RebuildFilter";
        public const string RawFilterNote = "ObjectDialogs.RawFilterNote";
    }
}
