namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>Korespondencja (U5): the message page (2 · Wiadomość). Keys <c>Correspondence.Message.*</c>; the dynamic families
    /// <c>Correspondence.SendMode.{mode}</c> and <c>Correspondence.Message.SignatureProblem.{code}</c> are listed by
    /// <c>CorrespondenceText</c>.</summary>
    public static class CorrespondenceMessage
    {
        /// <summary>The first signature choice: the message has no signature.</summary>
        public const string NoSignature = "Correspondence.Message.NoSignature";


        /// <summary>{0} = the signatures folder.</summary>
        public const string NoSignatures = "Correspondence.Message.NoSignatures";

        public const string SignaturesUnreadable = "Correspondence.Message.SignaturesUnreadable";
        public const string SignatureNotFound = "Correspondence.Message.SignatureNotFound";
        public const string SignatureUnreadable = "Correspondence.Message.SignatureUnreadable";

        /// <summary>{0} = the signature, {1} = its problems.</summary>
        public const string SignatureProblems = "Correspondence.Message.SignatureProblems";


        /// <summary>The header of the attachment list without attachments.</summary>
        public const string AttachmentsHeader = "Correspondence.Message.AttachmentsHeader";


        /// <summary>{0} = the number of files, {1} = their size.</summary>
        public const string AttachmentsHeaderCount = "Correspondence.Message.AttachmentsHeaderCount";


        /// <summary>The size column of an attachment whose file is gone.</summary>
        public const string AttachmentMissing = "Correspondence.Message.AttachmentMissing";

        public const string AddAttachmentsTitle = "Correspondence.Message.AddAttachmentsTitle";
        public const string AllFiles = "Correspondence.Message.AllFiles";
        public const string LargeAttachmentsTitle = "Correspondence.Message.LargeAttachmentsTitle";

        /// <summary>MailerWPiA's warning above 15 MB ({0} = the size).</summary>
        public const string LargeAttachments = "Correspondence.Message.LargeAttachments";

        public const string DeliverTimeInvalid = "Correspondence.Message.DeliverTimeInvalid";
        public const string DeliverNotFuture = "Correspondence.Message.DeliverNotFuture";
        public const string DeliverTooFar = "Correspondence.Message.DeliverTooFar";
        public const string SubjectEmpty = "Correspondence.Message.SubjectEmpty";
        public const string SubjectTooLong = "Correspondence.Message.SubjectTooLong";
        public const string BodyEmpty = "Correspondence.Message.BodyEmpty";
        public const string AttachmentsMissing = "Correspondence.Message.AttachmentsMissing";
        public const string CustomFieldError = "Correspondence.Message.CustomFieldError";
        public const string CustomFieldNameEmpty = "Correspondence.Message.CustomFieldNameEmpty";
        public const string CustomFieldNameInvalid = "Correspondence.Message.CustomFieldNameInvalid";
        public const string CustomFieldDuplicate = "Correspondence.Message.CustomFieldDuplicate";
        public const string CustomFieldBuiltIn = "Correspondence.Message.CustomFieldBuiltIn";
        public const string CustomFieldColumn = "Correspondence.Message.CustomFieldColumn";
        public const string CustomFieldUsesCustom = "Correspondence.Message.CustomFieldUsesCustom";
        public const string ImagesOverLimit = "Correspondence.Message.ImagesOverLimit";
        public const string VotingEmpty = "Correspondence.Message.VotingEmpty";
        public const string ImageRejected = "Correspondence.Message.ImageRejected";
        public const string ImageTooLarge = "Correspondence.Message.ImageTooLarge";

        /// <summary>Why a signature image was refused: larger than the limit of Korespondencja ({0} = the limit).</summary>
        public const string ImageTooLargeReason = "Correspondence.Message.ImageTooLargeReason";
        public const string ImageLimit = "Correspondence.Message.ImageLimit";
        public const string PasteTooLarge = "Correspondence.Message.PasteTooLarge";
        public const string PasteProblems = "Correspondence.Message.PasteProblems";
        public const string BuiltInTemplate = "Correspondence.Message.BuiltInTemplate";
        public const string TemplatesTitle = "Correspondence.Message.TemplatesTitle";
        public const string ReplaceMessage = "Correspondence.Message.ReplaceMessage";
        public const string TemplateNameRequired = "Correspondence.Message.TemplateNameRequired";
        public const string TemplateSaved = "Correspondence.Message.TemplateSaved";
        public const string TemplateTooMany = "Correspondence.Message.TemplateTooMany";
        public const string DeleteTemplate = "Correspondence.Message.DeleteTemplate";
        public const string TemplateDeleted = "Correspondence.Message.TemplateDeleted";
        public const string ExportTitle = "Correspondence.Message.ExportTitle";
        public const string ImportTitle = "Correspondence.Message.ImportTitle";
        public const string JsonFilter = "Correspondence.Message.JsonFilter";

        /// <summary>{0} = the file.</summary>
        public const string Exported = "Correspondence.Message.Exported";

        public const string Imported = "Correspondence.Message.Imported";
        public const string ImportedImagesRejected = "Correspondence.Message.ImportedImagesRejected";
        public const string ImportInvalid = "Correspondence.Message.ImportInvalid";
        public const string FileFailed = "Correspondence.Message.FileFailed";
        public const string NewMessageTitle = "Correspondence.Message.NewMessageTitle";
        public const string NewMessage = "Correspondence.Message.NewMessage";
    }
}
