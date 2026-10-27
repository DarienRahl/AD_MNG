namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>
    /// Formatted messages pasted from Outlook with inline images (U1c, design note docs/dev/u1c-u1d-rich-html-and-salutations.md):
    /// the HTML section of the composer, the image strip, the paste results, the rendered preview and the transport bar.
    /// Dynamic family <c>UserMessages.Rich.Problem.{HtmlImportProblemKind}</c> (argument {0} = the detail) through
    /// <see cref="ProblemKey"/>; the issue codes of <c>HtmlIssueCodes</c> live in <c>UserMessages.Issue.*</c> and the
    /// transport codes in <c>UserMessages.Code.*</c>.
    /// </summary>
    public static class UserMessagesRich
    {
        public const string Section = "UserMessages.Rich.Section";
        public const string PasteFormatted = "UserMessages.Rich.PasteFormatted";
        public const string PasteFormattedTip = "UserMessages.Rich.PasteFormattedTip";
        public const string PasteImage = "UserMessages.Rich.PasteImage";
        public const string ShowPreview = "UserMessages.Rich.ShowPreview";
        public const string ShowSource = "UserMessages.Rich.ShowSource";
        public const string GenerateText = "UserMessages.Rich.GenerateText";
        public const string GenerateTextConfirm = "UserMessages.Rich.GenerateTextConfirm";
        public const string ReplaceHtmlConfirm = "UserMessages.Rich.ReplaceHtmlConfirm";
        public const string CancelPaste = "UserMessages.Rich.CancelPaste";
        public const string Pasting = "UserMessages.Rich.Pasting";
        public const string Pasted = "UserMessages.Rich.Pasted";
        public const string PastedText = "UserMessages.Rich.PastedText";
        public const string PasteCancelled = "UserMessages.Rich.PasteCancelled";
        public const string ClipboardEmpty = "UserMessages.Rich.ClipboardEmpty";
        public const string ClipboardTooLarge = "UserMessages.Rich.ClipboardTooLarge";
        public const string ClipboardNoImage = "UserMessages.Rich.ClipboardNoImage";
        public const string ImageNotAccepted = "UserMessages.Rich.ImageNotAccepted";
        public const string ImageInserted = "UserMessages.Rich.ImageInserted";
        public const string TemplateImageLimit = "UserMessages.Rich.TemplateImageLimit";
        public const string Images = "UserMessages.Rich.Images";
        public const string ImageSize = "UserMessages.Rich.ImageSize";
        public const string RemoveImage = "UserMessages.Rich.RemoveImage";
        public const string ImagesLoading = "UserMessages.Rich.ImagesLoading";
        public const string ImagesMissing = "UserMessages.Rich.ImagesMissing";
        public const string Problems = "UserMessages.Rich.Problems";
        public const string ProblemCount = "UserMessages.Rich.ProblemCount";
        public const string AllowDomain = "UserMessages.Rich.AllowDomain";
        public const string DomainAllowed = "UserMessages.Rich.DomainAllowed";
        public const string RemoveLink = "UserMessages.Rich.RemoveLink";
        public const string LinkRemoved = "UserMessages.Rich.LinkRemoved";
        public const string TabView = "UserMessages.Rich.TabView";
        public const string TabText = "UserMessages.Rich.TabText";
        public const string TabSource = "UserMessages.Rich.TabSource";
        public const string PreviewUnavailable = "UserMessages.Rich.PreviewUnavailable";
        public const string PreviewDisabled = "UserMessages.Rich.PreviewDisabled";
        public const string PreviewDownload = "UserMessages.Rich.PreviewDownload";
        public const string PreviewEmpty = "UserMessages.Rich.PreviewEmpty";
        public const string InlineImages = "UserMessages.Rich.InlineImages";
        public const string FitImagesNotSupported = "UserMessages.Rich.FitImagesNotSupported";
        public const string FitImagesOverLimits = "UserMessages.Rich.FitImagesOverLimits";
        public const string FitImageStorage = "UserMessages.Rich.FitImageStorage";
        public const string FitHtmlTooLong = "UserMessages.Rich.FitHtmlTooLong";
        public const string SendDirect = "UserMessages.Rich.SendDirect";
        public const string SendWithoutImages = "UserMessages.Rich.SendWithoutImages";
        public const string FitCancel = "UserMessages.Rich.FitCancel";
        public const string ViaSmtpForRun = "UserMessages.Rich.ViaSmtpForRun";
        public const string RunWithoutImages = "UserMessages.Rich.RunWithoutImages";
        public const string RunDirect = "UserMessages.Rich.RunDirect";
        public const string ImportImagesRejected = "UserMessages.Rich.ImportImagesRejected";
        public const string ImageFolderLarge = "UserMessages.Rich.ImageFolderLarge";
        public const string RemoveMissing = "UserMessages.Rich.RemoveMissing";

        /// <summary><c>UserMessages.Rich.Problem.{kind}</c>.</summary>
        public static string ProblemKey(Core.Messaging.Html.HtmlImportProblemKind kind) => "UserMessages.Rich.Problem." + kind;
    }
}
