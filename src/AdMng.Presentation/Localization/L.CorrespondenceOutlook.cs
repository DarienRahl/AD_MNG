namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>
    /// Korespondencja (U5): the Outlook connection and its diagnostics. Keys <c>Correspondence.Outlook.*</c>; the dynamic
    /// families <c>Correspondence.Outlook.State.{state}</c> and <c>Correspondence.Outlook.Code.{code}</c> are listed by
    /// <c>CorrespondenceText.AllDynamicKeys</c>.
    /// </summary>
    public static class CorrespondenceOutlook
    {
        /// <summary>"Outlook: połączono ({0} konta)" – 2–4, 22–24 … accounts (Polish plural).</summary>
        public const string ConnectedFew = "Correspondence.Outlook.ConnectedFew";

        /// <summary>"Outlook: połączono ({0} kont)" – 5 and more accounts (Polish plural).</summary>
        public const string ConnectedMany = "Correspondence.Outlook.ConnectedMany";

        /// <summary>"{0} (kod {1})": a failure text with its HRESULT.</summary>
        public const string WithHResult = "Correspondence.Outlook.WithHResult";

        /// <summary>"konto domyślne profilu" (no account chosen).</summary>
        public const string DefaultAccount = "Correspondence.Outlook.DefaultAccount";

        /// <summary>"Sprawdź Outlooka".</summary>
        public const string Check = "Correspondence.Outlook.Check";

        /// <summary>"Połącz z Outlookiem".</summary>
        public const string Connect = "Correspondence.Outlook.Connect";

        /// <summary>"Wyślij jako" (the account).</summary>
        public const string Account = "Correspondence.Outlook.Account";

        /// <summary>"W imieniu".</summary>
        public const string OnBehalfOf = "Correspondence.Outlook.OnBehalfOf";

        public const string OnBehalfOfHint = "Correspondence.Outlook.OnBehalfOfHint";

        // The diagnostics report ("Sprawdź Outlooka").
        public const string CheckTitle = "Correspondence.Outlook.CheckTitle";
        public const string DiagUser = "Correspondence.Outlook.DiagUser";
        public const string DiagComputer = "Correspondence.Outlook.DiagComputer";
        public const string DiagNotConnected = "Correspondence.Outlook.DiagNotConnected";
        public const string DiagOutlook = "Correspondence.Outlook.DiagOutlook";
        public const string DiagAccounts = "Correspondence.Outlook.DiagAccounts";
        public const string DiagCurrentUser = "Correspondence.Outlook.DiagCurrentUser";
        public const string DiagSignatures = "Correspondence.Outlook.DiagSignatures";
        public const string DiagNoSignatures = "Correspondence.Outlook.DiagNoSignatures";
        public const string DiagTestItem = "Correspondence.Outlook.DiagTestItem";
        public const string DiagTestOk = "Correspondence.Outlook.DiagTestOk";
        public const string DiagTestFailed = "Correspondence.Outlook.DiagTestFailed";
        public const string DiagTestSkipped = "Correspondence.Outlook.DiagTestSkipped";
        public const string DiagFailed = "Correspondence.Outlook.DiagFailed";
        public const string DiagJournal = "Correspondence.Outlook.DiagJournal";
        public const string DiagJournalEntries = "Correspondence.Outlook.DiagJournalEntries";
        public const string DiagJournalNotLoaded = "Correspondence.Outlook.DiagJournalNotLoaded";
        public const string DiagDownloads = "Correspondence.Outlook.DiagDownloads";
        public const string DiagSignaturesFolder = "Correspondence.Outlook.DiagSignaturesFolder";
        public const string DiagNotFound = "Correspondence.Outlook.DiagNotFound";
    }

    /// <summary>
    /// Messages to users (U1) through Outlook (U5, design note 5): the settings tab and the composer. Keys
    /// <c>UserMessages.Outlook.*</c>; the delivery codes are <c>UserMessages.Code.outlook.*</c>.
    /// </summary>
    public static class UserMessagesOutlook
    {
        /// <summary>"konto domyślne profilu" ({0} of <see cref="UserMessages.ViaOutlook"/>).</summary>
        public const string DefaultAccount = "UserMessages.Outlook.DefaultAccount";

        /// <summary>"Konto Outlooka" (settings label).</summary>
        public const string Account = "UserMessages.Outlook.Account";

        public const string AccountHint = "UserMessages.Outlook.AccountHint";

        /// <summary>"Pobierz konta z Outlooka".</summary>
        public const string LoadAccounts = "UserMessages.Outlook.LoadAccounts";

        /// <summary>"W imieniu nadawcy" check box.</summary>
        public const string OnBehalfOfSender = "UserMessages.Outlook.OnBehalfOfSender";

        public const string OnBehalfOfSenderHint = "UserMessages.Outlook.OnBehalfOfSenderHint";

        public const string LoadingAccounts = "UserMessages.Outlook.LoadingAccounts";

        /// <summary>"Konta z profilu „{0}”: {1}".</summary>
        public const string AccountsLoaded = "UserMessages.Outlook.AccountsLoaded";

        public const string NoAccounts = "UserMessages.Outlook.NoAccounts";

        /// <summary>"Nie udało się pobrać kont z Outlooka: {0}".</summary>
        public const string AccountsFailed = "UserMessages.Outlook.AccountsFailed";
    }
}
