namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>
    /// Korespondencja (U5, design note docs/dev/u5-outlook-correspondence.md 6.5): the shell commands and the window. The
    /// pages have their own classes (<c>L.Correspondence*</c>); dynamic families are listed by
    /// <c>CorrespondenceText.AllDynamicKeys</c>.
    /// </summary>
    public static class Correspondence
    {
        public const string Command = "Correspondence.Command";
        public const string ObjectCommand = "Correspondence.ObjectCommand";
        public const string MailboxOwnersCommand = "Correspondence.MailboxOwnersCommand";

        /// <summary>"Korespondencja – {0}" ({0} = the domain of the window's session).</summary>
        public const string WindowTitle = "Correspondence.WindowTitle";

        /// <summary>"Korespondencja" (a window opened while disconnected).</summary>
        public const string WindowTitleNoDomain = "Correspondence.WindowTitleNoDomain";

        /// <summary>Closing the window while messages are being sent.</summary>
        public const string StopOnClose = "Correspondence.StopOnClose";

        /// <summary>"Pomoc" (the button) and the title of the help report.</summary>
        public const string Help = "Correspondence.Help";

        public const string HelpTitle = "Correspondence.HelpTitle";

        /// <summary>The help text of the window (MailerWPiA's "Pomoc" adapted, in the report window).</summary>
        public const string HelpText = "Correspondence.HelpText";

        /// <summary>"Skrzynki…" – shows the mailbox owners tab.</summary>
        public const string ShowMailboxes = "Correspondence.ShowMailboxes";

        /// <summary>The journal key could not be read: "… zachowano jako {0}" ({0} = the renamed journal).</summary>
        public const string JournalReset = "Correspondence.JournalReset";

        /// <summary>The journal cannot be used: {0} = the reason.</summary>
        public const string JournalUnavailable = "Correspondence.JournalUnavailable";

        /// <summary>The reason of <see cref="JournalUnavailable"/> on a host without a key protector.</summary>
        public const string JournalNoProtector = "Correspondence.JournalNoProtector";
    }
}
