namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>Korespondencja (U5): the recipients page (1 · Odbiorcy). Keys <c>Correspondence.Recipients.*</c>.</summary>
    public static class CorrespondenceRecipients
    {
        /// <summary>The source of pasted rows ("schowek").</summary>
        public const string SourceClipboard = "Correspondence.Recipients.SourceClipboard";

        /// <summary>The source of typed rows ("ręcznie").</summary>
        public const string SourceManual = "Correspondence.Recipients.SourceManual";

        /// <summary>The source of rows read from the directory ("AD").</summary>
        public const string SourceDirectory = "Correspondence.Recipients.SourceDirectory";

        /// <summary>The source of group members: {0} = the group.</summary>
        public const string SourceGroup = "Correspondence.Recipients.SourceGroup";

        /// <summary>The source of mailbox owner rows ("Skrzynki").</summary>
        public const string SourceMailboxes = "Correspondence.Recipients.SourceMailboxes";

        /// <summary>"Odbiorcy: {0} • do wysłania: {1}".</summary>
        public const string Counts = "Correspondence.Recipients.Counts";

        /// <summary>" • pominięci: {0}".</summary>
        public const string CountsSkipped = "Correspondence.Recipients.CountsSkipped";

        /// <summary>" • błędne: {0}".</summary>
        public const string CountsInvalid = "Correspondence.Recipients.CountsInvalid";

        /// <summary>"Pominięcia: {0} własnych + {1} z dziennika".</summary>
        public const string Exclusions = "Correspondence.Recipients.Exclusions";

        /// <summary>The journal mark of a row: {0} = the date, {1} = the subject as typed.</summary>
        public const string PreviouslySent = "Correspondence.Recipients.PreviouslySent";

        /// <summary>The journal mark of a row without a subject: {0} = the date.</summary>
        public const string PreviouslySentDate = "Correspondence.Recipients.PreviouslySentDate";

        /// <summary>The status of a directory object without an address.</summary>
        public const string NoAddress = "Correspondence.Recipients.NoAddress";

        /// <summary>Import status: {0} = file, {1} = format, {2} = the counts.</summary>
        public const string ImportedFile = "Correspondence.Recipients.ImportedFile";

        /// <summary>Import status: {0} = number of files, {1} = the counts.</summary>
        public const string ImportedFiles = "Correspondence.Recipients.ImportedFiles";

        /// <summary>Paste status: {0} = the counts.</summary>
        public const string Pasted = "Correspondence.Recipients.Pasted";

        /// <summary>Typed addresses status: {0} = the counts.</summary>
        public const string Typed = "Correspondence.Recipients.Typed";

        /// <summary>Directory status: {0} = the counts.</summary>
        public const string FromDirectory = "Correspondence.Recipients.FromDirectory";

        public const string NewOne = "Correspondence.Recipients.NewOne";
        public const string NewFew = "Correspondence.Recipients.NewFew";
        public const string NewMany = "Correspondence.Recipients.NewMany";
        public const string DuplicatesOne = "Correspondence.Recipients.DuplicatesOne";
        public const string DuplicatesFew = "Correspondence.Recipients.DuplicatesFew";
        public const string DuplicatesMany = "Correspondence.Recipients.DuplicatesMany";
        public const string InvalidOne = "Correspondence.Recipients.InvalidOne";
        public const string InvalidFew = "Correspondence.Recipients.InvalidFew";
        public const string InvalidMany = "Correspondence.Recipients.InvalidMany";

        /// <summary>"{0} bez adresu e-mail" (directory objects).</summary>
        public const string WithoutAddress = "Correspondence.Recipients.WithoutAddress";

        /// <summary>Objects that are no users, contacts or groups: {0} = their number.</summary>
        public const string OtherObjects = "Correspondence.Recipients.OtherObjects";

        /// <summary>The list is full: {0} = <c>CorrespondenceLimits.MaxRecipients</c>, {1} = rows not added.</summary>
        public const string OverLimit = "Correspondence.Recipients.OverLimit";

        /// <summary>A group with more members than the limit: {0} = the group, {1} = the limit.</summary>
        public const string GroupTruncated = "Correspondence.Recipients.GroupTruncated";

        /// <summary>A source without any address.</summary>
        public const string NothingFound = "Correspondence.Recipients.NothingFound";

        /// <summary>A standalone status: no address in the source.</summary>
        public const string NoAddressesFound = "Correspondence.Recipients.NoAddressesFound";

        public const string ClipboardEmpty = "Correspondence.Recipients.ClipboardEmpty";

        /// <summary>The title of import error reports.</summary>
        public const string ImportTitle = "Correspondence.Recipients.ImportTitle";

        /// <summary>{0} = file, {1} = the reason.</summary>
        public const string ReadFailed = "Correspondence.Recipients.ReadFailed";

        /// <summary>The reason of an <c>InvalidDataException</c> (corrupt, too large, unknown format).</summary>
        public const string InvalidFile = "Correspondence.Recipients.InvalidFile";

        /// <summary>Dropped files that are not contact files: {0} = their names.</summary>
        public const string Unsupported = "Correspondence.Recipients.Unsupported";

        public const string FilesTitle = "Correspondence.Recipients.FilesTitle";
        public const string FilesFilter = "Correspondence.Recipients.FilesFilter";
        public const string AllFiles = "Correspondence.Recipients.AllFiles";
        public const string NoDownloadsFolder = "Correspondence.Recipients.NoDownloadsFolder";

        /// <summary>{0} = the Downloads folder, {1} = the pattern.</summary>
        public const string NoDownloadsFile = "Correspondence.Recipients.NoDownloadsFile";

        public const string ManualTitle = "Correspondence.Recipients.ManualTitle";
        public const string ManualPrompt = "Correspondence.Recipients.ManualPrompt";
        public const string ExclusionsTitle = "Correspondence.Recipients.ExclusionsTitle";
        public const string ExclusionsPrompt = "Correspondence.Recipients.ExclusionsPrompt";
        public const string ExclusionsFilesTitle = "Correspondence.Recipients.ExclusionsFilesTitle";

        /// <summary>{0} = added, {1} = on the list.</summary>
        public const string ExclusionsAdded = "Correspondence.Recipients.ExclusionsAdded";

        public const string ExclusionsCleared = "Correspondence.Recipients.ExclusionsCleared";
        public const string RemoveNone = "Correspondence.Recipients.RemoveNone";

        /// <summary>{0} = rows removed.</summary>
        public const string Removed = "Correspondence.Recipients.Removed";

        public const string ClearTitle = "Correspondence.Recipients.ClearTitle";

        /// <summary>{0} = rows.</summary>
        public const string ClearConfirm = "Correspondence.Recipients.ClearConfirm";

        public const string Cleared = "Correspondence.Recipients.Cleared";
        public const string ExportTitle = "Correspondence.Recipients.ExportTitle";
        public const string ExportFileName = "Correspondence.Recipients.ExportFileName";
        public const string ExportNothing = "Correspondence.Recipients.ExportNothing";

        /// <summary>{0} = the file.</summary>
        public const string Exported = "Correspondence.Recipients.Exported";

        public const string JournalTitle = "Correspondence.Recipients.JournalTitle";

        /// <summary>{0} = entries, {1} = oldest, {2} = newest, {3} = retention days, {4} = folder.</summary>
        public const string JournalReport = "Correspondence.Recipients.JournalReport";

        /// <summary>A date the journal does not have.</summary>
        public const string JournalNoDate = "Correspondence.Recipients.JournalNoDate";

        public const string JournalUnavailable = "Correspondence.Recipients.JournalUnavailable";

        /// <summary>{0} = retention days.</summary>
        public const string JournalPruneConfirm = "Correspondence.Recipients.JournalPruneConfirm";

        public const string JournalClearConfirm = "Correspondence.Recipients.JournalClearConfirm";

        /// <summary>{0} = entries removed.</summary>
        public const string JournalPruned = "Correspondence.Recipients.JournalPruned";

        public const string JournalCleared = "Correspondence.Recipients.JournalCleared";

        /// <summary>The title of directory errors.</summary>
        public const string DirectoryTitle = "Correspondence.Recipients.DirectoryTitle";

        /// <summary>The name of a new field definition.</summary>
        public const string NewFieldName = "Correspondence.Recipients.NewFieldName";

        public const string FieldEmptyName = "Correspondence.Recipients.FieldEmptyName";

        /// <summary>{0} = the longest name.</summary>
        public const string FieldInvalidName = "Correspondence.Recipients.FieldInvalidName";

        public const string FieldDuplicate = "Correspondence.Recipients.FieldDuplicate";

        /// <summary>Literal braces: rendered with Format.</summary>
        public const string FieldEmptyExpression = "Correspondence.Recipients.FieldEmptyExpression";

        /// <summary>{0} = the engine's diagnostic.</summary>
        public const string FieldSyntax = "Correspondence.Recipients.FieldSyntax";

        /// <summary>{0} = the attribute.</summary>
        public const string FieldForbidden = "Correspondence.Recipients.FieldForbidden";

        /// <summary>{0} = the token name (literal braces around it).</summary>
        public const string FieldUnsupported = "Correspondence.Recipients.FieldUnsupported";
    }
}
