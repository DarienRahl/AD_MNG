namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>
    /// Korespondencja (U5, design note 9.2–9.4): the WPF parts of the app – the message editor (toolbar, link prompt, its
    /// error lines), the text prompt and the report window. Keys <c>Correspondence.App.*</c>.
    /// </summary>
    public static class CorrespondenceApp
    {
        public const string EditorBody = "Correspondence.App.Editor.Body";
        public const string EditorToolbar = "Correspondence.App.Editor.Toolbar";
        public const string Bold = "Correspondence.App.Editor.Bold";
        public const string Italic = "Correspondence.App.Editor.Italic";
        public const string Underline = "Correspondence.App.Editor.Underline";
        public const string Bullets = "Correspondence.App.Editor.Bullets";
        public const string Numbering = "Correspondence.App.Editor.Numbering";
        public const string Link = "Correspondence.App.Editor.Link";
        public const string ClearFormatting = "Correspondence.App.Editor.ClearFormatting";
        public const string PastePlain = "Correspondence.App.Editor.PastePlain";
        public const string PastePlainHint = "Correspondence.App.Editor.PastePlainHint";
        public const string Field = "Correspondence.App.Editor.Field";
        public const string FieldHint = "Correspondence.App.Editor.FieldHint";
        public const string InsertField = "Correspondence.App.Editor.InsertField";

        /// <summary>The title of the link prompt.</summary>
        public const string LinkTitle = "Correspondence.App.Editor.LinkTitle";

        /// <summary>The text of the link prompt (shows <c>{{Link}}</c> literally – not a format string).</summary>
        public const string LinkMessage = "Correspondence.App.Editor.LinkMessage";

        public const string SelectTextForLink = "Correspondence.App.Editor.SelectTextForLink";
        public const string LinkInOneParagraph = "Correspondence.App.Editor.LinkInOneParagraph";

        /// <summary>{0} = the address as typed.</summary>
        public const string InvalidLink = "Correspondence.App.Editor.InvalidLink";

        public const string SelectTextToClear = "Correspondence.App.Editor.SelectTextToClear";
        public const string ClipboardEmpty = "Correspondence.App.Editor.ClipboardEmpty";
        public const string ImageNotPasted = "Correspondence.App.Editor.ImageNotPasted";

        /// <summary>The automation name of the prompt's text box.</summary>
        public const string PromptText = "Correspondence.App.Prompt.Text";

        /// <summary>The automation name of the report's text box.</summary>
        public const string ReportText = "Correspondence.App.Report.Text";
    }
}
