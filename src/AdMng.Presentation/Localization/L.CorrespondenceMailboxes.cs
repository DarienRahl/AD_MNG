namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>Korespondencja (U5): the mailbox owner query (Skrzynki). Keys <c>Correspondence.Mailboxes.*</c>.</summary>
    public static class CorrespondenceMailboxes
    {
        /// <summary>The scope "selected objects": {0} = their number.</summary>
        public const string ScopeSelection = "Correspondence.Mailboxes.ScopeSelection";

        public const string ScopeSelectionNone = "Correspondence.Mailboxes.ScopeSelectionNone";
        public const string ScopeOuNone = "Correspondence.Mailboxes.ScopeOuNone";

        /// <summary>The persons of the whole domain: {0} = the domain.</summary>
        public const string PersonBaseDomain = "Correspondence.Mailboxes.PersonBaseDomain";

        /// <summary>{0} = the diagnostic of <c>LdapFilter.Validate</c>.</summary>
        public const string ExtraFilterInvalid = "Correspondence.Mailboxes.ExtraFilterInvalid";

        /// <summary>{0} = the attribute typed.</summary>
        public const string NameAttributeInvalid = "Correspondence.Mailboxes.NameAttributeInvalid";

        /// <summary>"Skrzynki: {0} · osoby: {1}".</summary>
        public const string Progress = "Correspondence.Mailboxes.Progress";

        /// <summary>"Skrzynki: {0}" (while reading the mailboxes).</summary>
        public const string ProgressMailboxes = "Correspondence.Mailboxes.ProgressMailboxes";

        /// <summary>{0} = the limit.</summary>
        public const string TooManyMailboxes = "Correspondence.Mailboxes.TooManyMailboxes";

        /// <summary>{0} = the limit.</summary>
        public const string TooManyPersons = "Correspondence.Mailboxes.TooManyPersons";

        /// <summary>{0} = the progress so far.</summary>
        public const string Cancelled = "Correspondence.Mailboxes.Cancelled";

        /// <summary>{0} = the reason.</summary>
        public const string Failed = "Correspondence.Mailboxes.Failed";

        public const string NoMailboxes = "Correspondence.Mailboxes.NoMailboxes";

        /// <summary>{0} matched, {1} to review, {2} not found, {3} excluded.</summary>
        public const string Summary = "Correspondence.Mailboxes.Summary";

        /// <summary>A candidate of the combo box: {0} = the display name, {1} = department, address and marks.</summary>
        public const string Candidate = "Correspondence.Mailboxes.Candidate";

        public const string CandidateNoAddress = "Correspondence.Mailboxes.CandidateNoAddress";
        public const string CandidateDisabled = "Correspondence.Mailboxes.CandidateDisabled";
        public const string CandidateDelegate = "Correspondence.Mailboxes.CandidateDelegate";
        public const string DetailDelegate = "Correspondence.Mailboxes.DetailDelegate";
        public const string DetailDisabled = "Correspondence.Mailboxes.DetailDisabled";
        public const string DetailNoAddress = "Correspondence.Mailboxes.DetailNoAddress";
        public const string DetailNoName = "Correspondence.Mailboxes.DetailNoName";

        /// <summary>{0} = the owner's name as parsed.</summary>
        public const string DetailNotFound = "Correspondence.Mailboxes.DetailNotFound";

        /// <summary>{0} = the number of candidates.</summary>
        public const string DetailAmbiguous = "Correspondence.Mailboxes.DetailAmbiguous";

        public const string DetailManual = "Correspondence.Mailboxes.DetailManual";
        public const string DetailTyped = "Correspondence.Mailboxes.DetailTyped";
        public const string DetailExcluded = "Correspondence.Mailboxes.DetailExcluded";
        public const string DetailNotChosen = "Correspondence.Mailboxes.DetailNotChosen";

        public const string TypeAddressTitle = "Correspondence.Mailboxes.TypeAddressTitle";

        /// <summary>{0} = the mailbox.</summary>
        public const string TypeAddressPrompt = "Correspondence.Mailboxes.TypeAddressPrompt";

        /// <summary>{0} = the text typed.</summary>
        public const string InvalidAddress = "Correspondence.Mailboxes.InvalidAddress";

        public const string AddTitle = "Correspondence.Mailboxes.AddTitle";

        public const string AddNothing = "Correspondence.Mailboxes.AddNothing";

        /// <summary>{0} = mailbox rows added, {1} = owners.</summary>
        public const string Added = "Correspondence.Mailboxes.Added";

        /// <summary>The progress while the owners are read again before they are added: {0} = owners.</summary>
        public const string ReadingOwners = "Correspondence.Mailboxes.ReadingOwners";

        /// <summary>"Przerwano – …" of a cancelled "add to recipients" (the argument of <see cref="Cancelled"/>).</summary>
        public const string AddCancelled = "Correspondence.Mailboxes.AddCancelled";

        /// <summary>The name of a new mailbox field definition.</summary>
        public const string NewFieldName = "Correspondence.Mailboxes.NewFieldName";
    }
}
