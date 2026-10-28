namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>
    /// Korespondencja (U5, design note 9.1–9.4): the WPF parts of the app – the message editor (toolbar, link prompt, its
    /// error lines), the text prompt, the report window, the Korespondencja window (tabs, page controls, status bar) and the
    /// Outlook section of the settings. Keys <c>Correspondence.App.*</c>.
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

        // ---- The tabs (design note 9.1).
        public const string TabMailboxes = "Correspondence.App.Tab.Mailboxes";
        public const string TabRecipients = "Correspondence.App.Tab.Recipients";
        public const string TabMessage = "Correspondence.App.Tab.Message";
        public const string TabPreview = "Correspondence.App.Tab.Preview";
        public const string TabSend = "Correspondence.App.Tab.Send";
        public const string TabResponses = "Correspondence.App.Tab.Responses";

        // ---- The window: status bar, Outlook buttons, shared column and field texts.
        public const string Help = "Correspondence.App.Help";
        public const string OutlookState = "Correspondence.App.OutlookState";
        public const string Connect = "Correspondence.App.Connect";
        public const string CheckOutlook = "Correspondence.App.CheckOutlook";
        public const string Journal = "Correspondence.App.Journal";
        public const string ColumnInclude = "Correspondence.App.ColumnInclude";
        public const string FieldName = "Correspondence.App.FieldName";
        public const string FieldExpression = "Correspondence.App.FieldExpression";
        public const string FieldValue = "Correspondence.App.FieldValue";
        public const string AddField = "Correspondence.App.AddField";

        // ---- 1 · Odbiorcy.
        public const string RecipientsFromDownloads = "Correspondence.App.Recipients.FromDownloads";
        public const string RecipientsFromDownloadsHint = "Correspondence.App.Recipients.FromDownloadsHint";
        public const string RecipientsFromFiles = "Correspondence.App.Recipients.FromFiles";
        public const string RecipientsPaste = "Correspondence.App.Recipients.Paste";
        public const string RecipientsPasteHint = "Correspondence.App.Recipients.PasteHint";
        public const string RecipientsManual = "Correspondence.App.Recipients.Manual";
        public const string RecipientsDirectory = "Correspondence.App.Recipients.Directory";
        public const string RecipientsFromSelection = "Correspondence.App.Recipients.FromSelection";
        public const string RecipientsFromSelectionHint = "Correspondence.App.Recipients.FromSelectionHint";
        public const string RecipientsPickUsers = "Correspondence.App.Recipients.PickUsers";
        public const string RecipientsGroupMembers = "Correspondence.App.Recipients.GroupMembers";
        public const string RecipientsMailboxOwners = "Correspondence.App.Recipients.MailboxOwners";
        public const string RecipientsMailboxOwnersHint = "Correspondence.App.Recipients.MailboxOwnersHint";
        public const string RecipientsSearchHint = "Correspondence.App.Recipients.SearchHint";
        public const string RecipientsGrid = "Correspondence.App.Recipients.Grid";
        public const string RecipientsColumnEmail = "Correspondence.App.Recipients.ColumnEmail";
        public const string RecipientsColumnName = "Correspondence.App.Recipients.ColumnName";
        public const string RecipientsColumnSource = "Correspondence.App.Recipients.ColumnSource";
        public const string RecipientsColumnStatus = "Correspondence.App.Recipients.ColumnStatus";
        public const string RecipientsColumnPreviouslySent = "Correspondence.App.Recipients.ColumnPreviouslySent";
        public const string RecipientsEmpty = "Correspondence.App.Recipients.Empty";
        public const string RecipientsIncludeAll = "Correspondence.App.Recipients.IncludeAll";
        public const string RecipientsIncludeNone = "Correspondence.App.Recipients.IncludeNone";
        public const string RecipientsRemoveSelected = "Correspondence.App.Recipients.RemoveSelected";
        public const string RecipientsExport = "Correspondence.App.Recipients.Export";
        public const string RecipientsClear = "Correspondence.App.Recipients.Clear";
        public const string RecipientsExclusions = "Correspondence.App.Recipients.Exclusions";
        public const string RecipientsExclusionsHint = "Correspondence.App.Recipients.ExclusionsHint";
        public const string RecipientsExclusionsFromFile = "Correspondence.App.Recipients.ExclusionsFromFile";
        public const string RecipientsExclusionsPaste = "Correspondence.App.Recipients.ExclusionsPaste";
        public const string RecipientsSkipAlreadySent = "Correspondence.App.Recipients.SkipAlreadySent";
        public const string RecipientsDirectoryFields = "Correspondence.App.Recipients.DirectoryFields";

        /// <summary>
        /// A composite format without arguments (the XAML shows it through <c>Ad.Format</c>): the doubled braces become the
        /// single braces of U1 expressions.
        /// </summary>
        public const string RecipientsDirectoryFieldsHint = "Correspondence.App.Recipients.DirectoryFieldsHint";

        // ---- Skrzynki (mailbox owners, U5c).
        public const string MailboxesScope = "Correspondence.App.Mailboxes.Scope";
        public const string MailboxesScopeDomain = "Correspondence.App.Mailboxes.ScopeDomain";
        public const string MailboxesScopeOu = "Correspondence.App.Mailboxes.ScopeOu";

        /// <summary>The objects selected when the window was opened ("Zapytaj właścicieli skrzynek…").</summary>
        public const string MailboxesScopeSelection = "Correspondence.App.Mailboxes.ScopeSelection";

        public const string MailboxesBrowseScope = "Correspondence.App.Mailboxes.BrowseScope";
        public const string MailboxesSelection = "Correspondence.App.Mailboxes.Selection";
        public const string MailboxesNoExchange = "Correspondence.App.Mailboxes.NoExchange";
        public const string MailboxesExtraFilter = "Correspondence.App.Mailboxes.ExtraFilter";
        public const string MailboxesExtraFilterHint = "Correspondence.App.Mailboxes.ExtraFilterHint";
        public const string MailboxesOwners = "Correspondence.App.Mailboxes.Owners";
        public const string MailboxesNameSource = "Correspondence.App.Mailboxes.NameSource";
        public const string MailboxesNameAttribute = "Correspondence.App.Mailboxes.NameAttribute";
        public const string MailboxesPersonBase = "Correspondence.App.Mailboxes.PersonBase";
        public const string MailboxesBrowsePersonBase = "Correspondence.App.Mailboxes.BrowsePersonBase";
        public const string MailboxesWholeDomain = "Correspondence.App.Mailboxes.WholeDomain";
        public const string MailboxesEnabledOwnersOnly = "Correspondence.App.Mailboxes.EnabledOwnersOnly";
        public const string MailboxesGroupByOwner = "Correspondence.App.Mailboxes.GroupByOwner";
        public const string MailboxesFields = "Correspondence.App.Mailboxes.Fields";

        /// <summary>
        /// A composite format without arguments (the XAML shows it through <c>Ad.Format</c>): the doubled braces become the
        /// single braces of U1 expressions.
        /// </summary>
        public const string MailboxesFieldsHint = "Correspondence.App.Mailboxes.FieldsHint";
        public const string MailboxesTargetMailbox = "Correspondence.App.Mailboxes.TargetMailbox";
        public const string MailboxesTargetPerson = "Correspondence.App.Mailboxes.TargetPerson";
        public const string MailboxesFind = "Correspondence.App.Mailboxes.Find";
        public const string MailboxesColumnMailbox = "Correspondence.App.Mailboxes.ColumnMailbox";
        public const string MailboxesColumnAddress = "Correspondence.App.Mailboxes.ColumnAddress";
        public const string MailboxesColumnOwnerName = "Correspondence.App.Mailboxes.ColumnOwnerName";
        public const string MailboxesColumnStatus = "Correspondence.App.Mailboxes.ColumnStatus";
        public const string MailboxesColumnOwner = "Correspondence.App.Mailboxes.ColumnOwner";
        public const string MailboxesColumnOwnerAddress = "Correspondence.App.Mailboxes.ColumnOwnerAddress";
        public const string MailboxesColumnDepartment = "Correspondence.App.Mailboxes.ColumnDepartment";
        public const string MailboxesColumnActions = "Correspondence.App.Mailboxes.ColumnActions";
        public const string MailboxesColumnLocation = "Correspondence.App.Mailboxes.ColumnLocation";
        public const string MailboxesColumnDetail = "Correspondence.App.Mailboxes.ColumnDetail";
        public const string MailboxesPickOwner = "Correspondence.App.Mailboxes.PickOwner";
        public const string MailboxesTypeAddress = "Correspondence.App.Mailboxes.TypeAddress";
        public const string MailboxesExclude = "Correspondence.App.Mailboxes.Exclude";
        public const string MailboxesRestore = "Correspondence.App.Mailboxes.Restore";
        public const string MailboxesEmpty = "Correspondence.App.Mailboxes.Empty";
        public const string MailboxesReviewHint = "Correspondence.App.Mailboxes.ReviewHint";
        public const string MailboxesAddToRecipients = "Correspondence.App.Mailboxes.AddToRecipients";

        // ---- 2 · Wiadomość.
        public const string MessageSendAs = "Correspondence.App.Message.SendAs";
        public const string MessageSendAsHint = "Correspondence.App.Message.SendAsHint";
        public const string MessageOnBehalfOf = "Correspondence.App.Message.OnBehalfOf";
        public const string MessageSubject = "Correspondence.App.Message.Subject";
        public const string MessageInsertFieldHint = "Correspondence.App.Message.InsertFieldHint";
        public const string MessageSalutation = "Correspondence.App.Message.Salutation";
        public const string MessageSalutationHint = "Correspondence.App.Message.SalutationHint";
        public const string MessageBodyHint = "Correspondence.App.Message.BodyHint";
        public const string MessageSignature = "Correspondence.App.Message.Signature";
        public const string MessageSignatureHint = "Correspondence.App.Message.SignatureHint";
        public const string MessageAttachments = "Correspondence.App.Message.Attachments";
        public const string MessageAttachmentsDropHint = "Correspondence.App.Message.AttachmentsDropHint";
        public const string MessageAttachmentsHint = "Correspondence.App.Message.AttachmentsHint";
        public const string MessageAddAttachments = "Correspondence.App.Message.AddAttachments";
        public const string MessageSending = "Correspondence.App.Message.Sending";
        public const string MessageSendMode = "Correspondence.App.Message.SendMode";
        public const string MessageSendModeHint = "Correspondence.App.Message.SendModeHint";
        public const string MessageBccChunk = "Correspondence.App.Message.BccChunk";
        public const string MessageDelay = "Correspondence.App.Message.Delay";
        public const string MessageReadReceipt = "Correspondence.App.Message.ReadReceipt";
        public const string MessageDeliverLater = "Correspondence.App.Message.DeliverLater";
        public const string MessageDeliverDate = "Correspondence.App.Message.DeliverDate";
        public const string MessageDeliverTime = "Correspondence.App.Message.DeliverTime";
        public const string MessageDeliverHint = "Correspondence.App.Message.DeliverHint";
        public const string MessageUseVoting = "Correspondence.App.Message.UseVoting";
        public const string MessageVotingOptions = "Correspondence.App.Message.VotingOptions";
        public const string MessageVotingHint = "Correspondence.App.Message.VotingHint";
        public const string MessageCustomFields = "Correspondence.App.Message.CustomFields";
        public const string MessageCustomFieldsHint = "Correspondence.App.Message.CustomFieldsHint";
        public const string MessageGroupByAddress = "Correspondence.App.Message.GroupByAddress";
        public const string MessageListItem = "Correspondence.App.Message.ListItem";
        public const string MessageTemplates = "Correspondence.App.Message.Templates";
        public const string MessageTemplate = "Correspondence.App.Message.Template";
        public const string MessageTemplateName = "Correspondence.App.Message.TemplateName";
        public const string MessageDeleteTemplate = "Correspondence.App.Message.DeleteTemplate";
        public const string MessageNewMessage = "Correspondence.App.Message.NewMessage";
        public const string MessageExportTemplates = "Correspondence.App.Message.ExportTemplates";
        public const string MessageImportTemplates = "Correspondence.App.Message.ImportTemplates";

        // ---- 3 · Podgląd.
        public const string PreviewRecipient = "Correspondence.App.Preview.Recipient";
        public const string PreviewWidth = "Correspondence.App.Preview.Width";
        public const string PreviewViaOutlook = "Correspondence.App.Preview.ViaOutlook";
        public const string PreviewViaOutlookHint = "Correspondence.App.Preview.ViaOutlookHint";
        public const string PreviewOpenInBrowser = "Correspondence.App.Preview.OpenInBrowser";
        public const string PreviewOpenInBrowserHint = "Correspondence.App.Preview.OpenInBrowserHint";
        public const string PreviewSaveHtml = "Correspondence.App.Preview.SaveHtml";
        public const string PreviewFromOutlook = "Correspondence.App.Preview.FromOutlook";
        public const string PreviewWarnings = "Correspondence.App.Preview.Warnings";

        // ---- 4 · Wysyłka.
        public const string SendSummary = "Correspondence.App.Send.Summary";
        public const string SendTest = "Correspondence.App.Send.Test";
        public const string SendTestHint = "Correspondence.App.Send.TestHint";
        public const string SendDrafts = "Correspondence.App.Send.Drafts";
        public const string SendDraftsHint = "Correspondence.App.Send.DraftsHint";
        public const string SendButton = "Correspondence.App.Send.Send";
        public const string SendRetryFailed = "Correspondence.App.Send.RetryFailed";
        public const string SendProgress = "Correspondence.App.Send.Progress";
        public const string SendSent = "Correspondence.App.Send.Sent";
        public const string SendFailed = "Correspondence.App.Send.Failed";
        public const string SendSkipped = "Correspondence.App.Send.Skipped";
        public const string SendResults = "Correspondence.App.Send.Results";
        public const string SendExportReport = "Correspondence.App.Send.ExportReport";

        // ---- 5 · Odpowiedzi.
        public const string ResponsesSince = "Correspondence.App.Responses.Since";
        public const string ResponsesLoadCampaigns = "Correspondence.App.Responses.LoadCampaigns";
        public const string ResponsesCampaign = "Correspondence.App.Responses.Campaign";
        public const string ResponsesLoadResponses = "Correspondence.App.Responses.LoadResponses";
        public const string ResponsesExport = "Correspondence.App.Responses.Export";
        public const string ResponsesHint = "Correspondence.App.Responses.Hint";
        public const string ResponsesColumnRecipient = "Correspondence.App.Responses.ColumnRecipient";
        public const string ResponsesColumnMailboxes = "Correspondence.App.Responses.ColumnMailboxes";
        public const string ResponsesColumnResponse = "Correspondence.App.Responses.ColumnResponse";
        public const string ResponsesColumnRespondedAt = "Correspondence.App.Responses.ColumnRespondedAt";

        // ---- Settings, tab "Wiadomości e-mail": the Outlook transport of messages to users (U1).
        public const string SettingsOutlookSection = "Correspondence.App.Settings.OutlookSection";
        public const string SettingsOutlookAccount = "Correspondence.App.Settings.OutlookAccount";
        public const string SettingsLoadOutlookAccounts = "Correspondence.App.Settings.LoadOutlookAccounts";
        public const string SettingsOutlookAccountHint = "Correspondence.App.Settings.OutlookAccountHint";
        public const string SettingsOutlookOnBehalf = "Correspondence.App.Settings.OutlookOnBehalf";
    }
}
