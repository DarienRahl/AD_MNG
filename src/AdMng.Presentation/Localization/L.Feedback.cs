namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>
    /// The status line and the operation of a page (track V, stage V2; design note docs/dev/v-interface-renewal.md 4.5):
    /// the fixed titles of the four severities and the words every page shares.
    /// </summary>
    public static class Feedback
    {
        public const string TitleWorking = "Feedback.Title.Working";
        public const string TitleDone = "Feedback.Title.Done";
        public const string TitleAttention = "Feedback.Title.Attention";
        public const string TitleError = "Feedback.Title.Error";

        /// <summary>What the status line and the action button say from "Stop" until the operation ended.</summary>
        public const string Stopping = "Feedback.Stopping";

        /// <summary>The name a screen reader hears for a status line that says nothing.</summary>
        public const string StatusName = "Feedback.StatusName";

        /// <summary>"{0} object" in the three plural forms (one, few, many).</summary>
        public const string ObjectsOne = "Feedback.Objects.One";
        public const string ObjectsFew = "Feedback.Objects.Few";
        public const string ObjectsMany = "Feedback.Objects.Many";

        /// <summary>How far an operation over many objects is: "{0} of {1}" (done, total).</summary>
        public const string Progress = "Feedback.Progress";

        /// <summary>What to do after a failed read that the line offers to repeat: "Press Retry to try again."</summary>
        public const string RetryNext = "Feedback.RetryNext";
    }

    /// <summary>What the object list says in its status line (the first page of stage V2).</summary>
    public static class ListStatus
    {
        /// <summary>{0} = the title of the location.</summary>
        public const string Reading = "List.Status.Reading";

        /// <summary>{0} = the title of the location, {1} = the objects read so far ("1 500 objects").</summary>
        public const string ReadingCount = "List.Status.ReadingCount";

        /// <summary>{0} = the title of the location, {1} = the objects that were read.</summary>
        public const string Stopped = "List.Status.Stopped";

        /// <summary>What was tried, without a full stop; {0} = the title of the location.</summary>
        public const string ReadFailed = "List.Status.ReadFailed";
        public const string ReadFailedNext = "List.Status.ReadFailedNext";

        /// <summary>What to do when the shown container was deleted.</summary>
        public const string GoneNext = "List.Status.GoneNext";
    }

    /// <summary>What the Find window says in its status line.</summary>
    public static class FindStatus
    {
        /// <summary>The summary before the first search.</summary>
        public const string Hint = "Find.Status.Hint";
    }

    /// <summary>
    /// What the property sheet says in its status line (stage V2b): the window itself (reading, saving, the states that
    /// last) and its pages, which say "the “Member Of” tab" and "the “All (nested)” list" with their own titles.
    /// </summary>
    public static class PropertySheetStatus
    {
        /// <summary>The summary of the window: {0} = the domain controller the values were read from, {1} = the time.</summary>
        public const string Summary = "PropertySheet.Status.Summary";

        /// <summary>{0} = the name of the object.</summary>
        public const string Reading = "PropertySheet.Status.Reading";

        /// <summary>{0} = the name of the object.</summary>
        public const string Stopped = "PropertySheet.Status.Stopped";

        /// <summary>What was tried, without a full stop; {0} = the name of the object.</summary>
        public const string ReadFailed = "PropertySheet.Status.ReadFailed";

        /// <summary>What to do when the object no longer exists.</summary>
        public const string GoneNext = "PropertySheet.Status.GoneNext";

        /// <summary>The object was deleted while its window was open; {0} = its name.</summary>
        public const string Deleted = "PropertySheet.Status.Deleted";

        /// <summary>{0} = the name of the object.</summary>
        public const string Saving = "PropertySheet.Status.Saving";

        /// <summary>{0} = the name of the object, {1} = the domain.</summary>
        public const string Saved = "PropertySheet.Status.Saved";

        /// <summary>The plan had nothing to write; {0} = the name of the object.</summary>
        public const string NothingToSave = "PropertySheet.Status.NothingToSave";

        /// <summary>What was skipped, without a full stop: the lead of "Nothing was saved: why."</summary>
        public const string NotSaved = "PropertySheet.Status.NotSaved";

        /// <summary>The execution was cancelled in the activity list; {0} = the name of the object.</summary>
        public const string SaveStopped = "PropertySheet.Status.SaveStopped";

        /// <summary>What was tried, without a full stop; {0} = the name of the object.</summary>
        public const string SaveFailed = "PropertySheet.Status.SaveFailed";
        public const string SaveFailedNext = "PropertySheet.Status.SaveFailedNext";

        /// <summary>A page that reads by itself; {0} = the title of the tab.</summary>
        public const string PageReading = "PropertySheet.Status.PageReading";
        public const string PageStopped = "PropertySheet.Status.PageStopped";

        /// <summary>What was tried, without a full stop; {0} = the title of the tab.</summary>
        public const string PageReadFailed = "PropertySheet.Status.PageReadFailed";

        /// <summary>A list inside a page that reads by itself; {0} = the header of the list ("All (nested)").</summary>
        public const string ListReading = "PropertySheet.Status.ListReading";
        public const string ListStopped = "PropertySheet.Status.ListStopped";

        /// <summary>What was tried, without a full stop; {0} = the header of the list.</summary>
        public const string ListReadFailed = "PropertySheet.Status.ListReadFailed";

        /// <summary>"Check on all DCs" of the account state on the Account tab.</summary>
        public const string AccountChecking = "AccountStatus.Status.Checking";

        /// <summary>{0} = the text of the button that runs the check again.</summary>
        public const string AccountCheckStopped = "AccountStatus.Status.Stopped";

        /// <summary>"Check on all DCs" of a computer's "Stan" tab.</summary>
        public const string ComputerReadingExact = "Computers.Status.ReadingExact";

        /// <summary>{0} = the text of the button that reads again.</summary>
        public const string ComputerExactStopped = "Computers.Status.ExactStopped";

        /// <summary>What was tried, without a full stop.</summary>
        public const string ComputerExactFailed = "Computers.Status.ExactFailed";

        /// <summary>What is shown although not every domain controller answered.</summary>
        public const string ComputerExactPartialNext = "Computers.Status.ExactPartialNext";

        /// <summary>"Determine" (the site) of a computer's "Stan" tab.</summary>
        public const string ComputerResolvingSite = "Computers.Status.ResolvingSite";

        /// <summary>{0} = the text of the button that tries again.</summary>
        public const string ComputerSiteStopped = "Computers.Status.SiteStopped";

        /// <summary>What was tried, without a full stop.</summary>
        public const string ComputerSiteFailed = "Computers.Status.SiteFailed";
    }

    /// <summary>
    /// The execution of a change plan as the operation of a window (stage V2b; <c>Changes.ExecutionStatus</c>): the batch
    /// editor and the change plan preview say with these how far the execution is and how it ended. {0} is the title of
    /// the change, {1} the domain - a result that changed the directory names it.
    /// </summary>
    public static class ChangeStatus
    {
        /// <summary>{2} = how far ("12 of 300").</summary>
        public const string Executing = "Changes.Status.Executing";

        /// <summary>{2} = how far, {3} = the objects that failed so far.</summary>
        public const string ExecutingFailures = "Changes.Status.ExecutingFailures";

        /// <summary>A window without a domain (opened while disconnected): {0} = the title, {1} = how far.</summary>
        public const string ExecutingAnywhere = "Changes.Status.ExecutingAnywhere";

        /// <summary>Done: {2} = the objects that were changed ("12 objects").</summary>
        public const string Executed = "Changes.Status.Executed";

        /// <summary>What happened, without a full stop: some objects failed.</summary>
        public const string ExecutedPartly = "Changes.Status.ExecutedPartly";

        /// <summary>What happened, without a full stop: the execution was stopped.</summary>
        public const string Stopped = "Changes.Status.Stopped";

        /// <summary>What was tried, without a full stop.</summary>
        public const string ExecuteFailed = "Changes.Status.ExecuteFailed";

        /// <summary>What was tried, without a full stop and without a domain; {0} = the title.</summary>
        public const string ExecuteFailedAnywhere = "Changes.Status.ExecuteFailedAnywhere";

        /// <summary>The numbers of a result: {0} = the objects that were changed, {1} = the objects that failed.</summary>
        public const string Counts = "Changes.Status.Counts";

        /// <summary>The numbers of a stopped execution: {0} = the objects that were changed, {1} = the objects that failed.</summary>
        public const string CountsStopped = "Changes.Status.CountsStopped";

        /// <summary>No object was changed; {0} = the objects that failed.</summary>
        public const string NothingChanged = "Changes.Status.NothingChanged";

        /// <summary>Where the reasons of the failures are.</summary>
        public const string ReasonsNext = "Changes.Status.ReasonsNext";
    }

    /// <summary>
    /// What the batch editor says in its status line (stage V2b): the window (checking - planning - the changes, the
    /// execution through <see cref="ChangeStatus"/>) and the source of its first step, the selected objects or the CSV
    /// file.
    /// </summary>
    public static class BatchStatus
    {
        /// <summary>{0} = the number of selected objects.</summary>
        public const string ReadingTargets = "Batch.Status.ReadingTargets";
        public const string TargetsStopped = "Batch.Status.TargetsStopped";

        /// <summary>What was tried, without a full stop.</summary>
        public const string TargetsFailed = "Batch.Status.TargetsFailed";

        /// <summary>Planning, before the first object was checked.</summary>
        public const string CheckingStart = "Batch.Status.CheckingStart";

        /// <summary>Planning: {0} = how far ("120 of 300").</summary>
        public const string Checking = "Batch.Status.Checking";

        /// <summary>What was stopped, without a full stop, and what that means.</summary>
        public const string CheckStopped = "Batch.Status.CheckStopped";
        public const string CheckStoppedReason = "Batch.Status.CheckStoppedReason";

        /// <summary>What was tried, without a full stop.</summary>
        public const string CheckFailed = "Batch.Status.CheckFailed";

        /// <summary>What was tried, without a full stop: the window could not be filled.</summary>
        public const string OpenFailed = "Batch.Status.OpenFailed";

        /// <summary>What to do with the objects that failed or were not executed; {0} = the text of "Retry failed".</summary>
        public const string RetryNext = "Batch.Status.RetryNext";

        /// <summary>The CSV file of the import; {0} = the name of the file.</summary>
        public const string FileReading = "Csv.Status.Reading";
        public const string FileStopped = "Csv.Status.Stopped";

        /// <summary>What was tried, without a full stop; {0} = the name of the file.</summary>
        public const string FileReadFailed = "Csv.Status.ReadFailed";

        /// <summary>What happened, without a full stop: a file that was read cannot be used.</summary>
        public const string FileNotLoaded = "Csv.Status.NotLoaded";
        public const string FileNext = "Csv.Status.Next";
    }

    /// <summary>What the change plan preview says in its status line beyond <see cref="ChangeStatus"/> (stage V2b).</summary>
    public static class PlanPreviewStatus
    {
        /// <summary>What to do after an execution that failed in part.</summary>
        public const string UndoNext = "PlanPreview.Status.UndoNext";
    }

    /// <summary>
    /// What the AD Recycle Bin says in its status line (stage V2b): the list of deleted objects (reading it) and the
    /// window (checking - planning - a restore; the restore itself through <see cref="ChangeStatus"/>).
    /// </summary>
    public static class RecycleBinStatus
    {
        /// <summary>{0} = the domain.</summary>
        public const string Reading = "RecycleBin.Status.Reading";

        /// <summary>{0} = the domain, {1} = the objects read so far ("1 500 objects").</summary>
        public const string ReadingCount = "RecycleBin.Status.ReadingCount";

        /// <summary>{0} = the objects that were read.</summary>
        public const string Stopped = "RecycleBin.Status.Stopped";

        /// <summary>What was tried, without a full stop; {0} = the domain.</summary>
        public const string ReadFailed = "RecycleBin.Status.ReadFailed";

        /// <summary>Planning a restore; {0} = the number of selected objects.</summary>
        public const string Checking = "RecycleBin.Status.Checking";

        /// <summary>What was stopped, without a full stop, and what that means.</summary>
        public const string CheckStopped = "RecycleBin.Status.CheckStopped";
        public const string CheckStoppedReason = "RecycleBin.Status.CheckStoppedReason";

        /// <summary>What was tried, without a full stop.</summary>
        public const string CheckFailed = "RecycleBin.Status.CheckFailed";

        /// <summary>Where the objects are that a restore did not bring back.</summary>
        public const string NotRestoredNext = "RecycleBin.Status.NotRestoredNext";
    }

    /// <summary>
    /// What the account review says in its status line (stage V2b): the check of the window - of the "Account state" tab
    /// or of the "Privileges" tab - and the read of the exact last logon on the "Account state" tab.
    /// </summary>
    public static class AccountReviewStatus
    {
        /// <summary>{0} = the scope.</summary>
        public const string Checking = "AccountReview.Status.Checking";

        /// <summary>{0} = the scope, {1} = the accounts read so far, {2} = the accounts with findings so far.</summary>
        public const string CheckingCount = "AccountReview.Status.CheckingCount";

        /// <summary>{0} = the accounts that were read, {1} = the text of the button that checks again.</summary>
        public const string Stopped = "AccountReview.Status.Stopped";

        /// <summary>What was tried, without a full stop.</summary>
        public const string Failed = "AccountReview.Status.Failed";

        /// <summary>"Exact last logon (all DCs)"; {0} = the number of accounts.</summary>
        public const string ReadingExact = "AccountReview.Status.ReadingExact";

        /// <summary>{0} = the domain controllers that answered ("2 of 3"), {1} = the accounts that left the list.</summary>
        public const string ExactDone = "AccountReview.Status.ExactDone";

        /// <summary>{0} = the names of the domain controllers that did not answer.</summary>
        public const string ExactNoAnswer = "AccountReview.Status.ExactNoAnswer";

        /// <summary>{0} = the text of the button that reads again.</summary>
        public const string ExactStopped = "AccountReview.Status.ExactStopped";

        /// <summary>What was tried, without a full stop.</summary>
        public const string ExactFailed = "AccountReview.Status.ExactFailed";

        /// <summary>The "Privileges" tab; {0} = the scope.</summary>
        public const string PrivilegesChecking = "PrivilegedAccess.Status.Checking";

        /// <summary>{0} = the scope, {1} = the accounts read so far, {2} = the accounts with rights beyond a domain user.</summary>
        public const string PrivilegesCheckingCount = "PrivilegedAccess.Status.CheckingCount";

        /// <summary>The last step of the check: the comparison with similar accounts; {0} = the scope.</summary>
        public const string PrivilegesCheckingPeers = "PrivilegedAccess.Status.CheckingPeers";

        /// <summary>{0} = the accounts that were read, {1} = the text of the button that checks again.</summary>
        public const string PrivilegesStopped = "PrivilegedAccess.Status.Stopped";

        /// <summary>What was tried, without a full stop.</summary>
        public const string PrivilegesFailed = "PrivilegedAccess.Status.Failed";
    }

    /// <summary>
    /// What the comparison of two objects says in its status line (stage V2b): the comparison and the reads of nested
    /// membership and nested members that follow it.
    /// </summary>
    public static class ComparisonStatus
    {
        /// <summary>{0} = the name of the object, {1} = the label of the other connection.</summary>
        public const string Comparing = "Comparison.Status.Comparing";

        /// <summary>{0} = the name of the object, {1} = the text of the button that compares again.</summary>
        public const string Stopped = "Comparison.Status.Stopped";

        /// <summary>
        /// Several objects match, without a full stop; {0} = how many ("2 objects"; the candidates are listed for a choice).
        /// </summary>
        public const string Ambiguous = "Comparison.Status.Ambiguous";

        /// <summary>What was tried, without a full stop; {0} = the name of the object.</summary>
        public const string Failed = "Comparison.Status.Failed";

        /// <summary>The read of the nested members of both groups (nested membership: <c>Comparison.NestedLoading</c>).</summary>
        public const string ReadingNestedMembers = "Comparison.Status.ReadingNestedMembers";

        /// <summary>{0} = the text of the button that reads again.</summary>
        public const string NestedStopped = "Comparison.Status.NestedStopped";

        /// <summary>What was tried, without a full stop, and what is shown instead.</summary>
        public const string NestedFailed = "Comparison.Status.NestedFailed";
        public const string NestedFailedNext = "Comparison.Status.NestedFailedNext";

        /// <summary>{0} = the text of the button that reads again.</summary>
        public const string NestedMembersStopped = "Comparison.Status.NestedMembersStopped";

        /// <summary>What was tried, without a full stop, and what is shown instead.</summary>
        public const string NestedMembersFailed = "Comparison.Status.NestedMembersFailed";
        public const string NestedMembersFailedNext = "Comparison.Status.NestedMembersFailedNext";
    }

    /// <summary>
    /// What the Connect window says in its status line (stage V2b-2): connecting and looking for the domain controllers
    /// of the domain. Nothing of the credential is ever part of a text.
    /// </summary>
    public static class ConnectStatus
    {
        /// <summary>What was skipped, without a full stop: the lead of "Not connected: why."</summary>
        public const string NotConnected = "Connect.Status.NotConnected";

        /// <summary>{0} = the domain controller or the domain, {1} = the text of the button that connects.</summary>
        public const string Stopped = "Connect.Status.Stopped";

        /// <summary>What was tried, without a full stop; {0} = the domain controller or the domain.</summary>
        public const string Failed = "Connect.Status.Failed";

        /// <summary>What was skipped, without a full stop: no domain to look in.</summary>
        public const string NotSearched = "Connect.Status.NotSearched";

        /// <summary>{0} = the domain, {1} = the text of the button that looks again.</summary>
        public const string FindStopped = "Connect.Status.FindStopped";

        /// <summary>What was tried, without a full stop; {0} = the domain.</summary>
        public const string FindFailed = "Connect.Status.FindFailed";
    }

    /// <summary>
    /// What "Change domain controller" says in its status line (stage V2b-2): the list of domain controllers (reading
    /// it) and the window (moving the session to the chosen one).
    /// </summary>
    public static class ChangeDcStatus
    {
        /// <summary>{0} = the domain.</summary>
        public const string Reading = "ChangeDc.Status.Reading";
        public const string ReadStopped = "ChangeDc.Status.ReadStopped";

        /// <summary>What was tried, without a full stop, and what to do instead.</summary>
        public const string ReadFailed = "ChangeDc.Status.ReadFailed";
        public const string ReadFailedNext = "ChangeDc.Status.ReadFailedNext";

        /// <summary>"{0} domain controller" in the three plural forms (one, few, many): the summary of the list.</summary>
        public const string CountOne = "ChangeDc.Status.Count.One";
        public const string CountFew = "ChangeDc.Status.Count.Few";
        public const string CountMany = "ChangeDc.Status.Count.Many";

        /// <summary>{0} = the chosen domain controller, {1} = the one the session stays on, {2} = the text of the button.</summary>
        public const string Stopped = "ChangeDc.Status.Stopped";

        /// <summary>What was tried, without a full stop; {0} = the chosen domain controller.</summary>
        public const string Failed = "ChangeDc.Status.Failed";

        /// <summary>What was skipped, without a full stop: the lead of "The domain controller was not changed: why."</summary>
        public const string NotChanged = "ChangeDc.Status.NotChanged";
    }

    /// <summary>
    /// What "Reset Password" says in its status line (stage V2b-2): the list of accounts (reading their names) and the
    /// window (checking - planning - the reset; the reset itself through <see cref="ChangeStatus"/>). A password is
    /// never part of a text.
    /// </summary>
    public static class ResetPasswordStatus
    {
        /// <summary>{0} = the number of accounts.</summary>
        public const string Reading = "ResetPassword.Status.Reading";
        public const string ReadStopped = "ResetPassword.Status.ReadStopped";

        /// <summary>What was tried, without a full stop.</summary>
        public const string ReadFailed = "ResetPassword.Status.ReadFailed";

        /// <summary>What was skipped, without a full stop: the lead of "No password was reset: why."</summary>
        public const string NotReset = "ResetPassword.Status.NotReset";

        /// <summary>Planning the reset; {0} = the number of accounts.</summary>
        public const string Checking = "ResetPassword.Status.Checking";

        /// <summary>What was stopped, without a full stop, and what that means.</summary>
        public const string CheckStopped = "ResetPassword.Status.CheckStopped";
        public const string CheckStoppedReason = "ResetPassword.Status.CheckStoppedReason";

        /// <summary>What was tried, without a full stop.</summary>
        public const string CheckFailed = "ResetPassword.Status.CheckFailed";
    }

    /// <summary>What "New Object" says in its status line (stage V2b-2): creating the object. A password is never part of a text.</summary>
    public static class NewObjectStatus
    {
        /// <summary>{0} = the name of the new object, {1} = the domain.</summary>
        public const string Creating = "NewObject.Status.Creating";

        /// <summary>What was skipped, without a full stop: the lead of "Nothing was created: why."</summary>
        public const string NotCreated = "NewObject.Status.NotCreated";

        /// <summary>The creation was cancelled in the list of background operations; {0} = the name of the new object.</summary>
        public const string Stopped = "NewObject.Status.Stopped";

        /// <summary>What was tried, without a full stop; {0} = the name of the new object.</summary>
        public const string Failed = "NewObject.Status.Failed";

        /// <summary>What was skipped, without a full stop: the chosen account cannot be the one that joins the computer.</summary>
        public const string JoinerNotSet = "NewObject.Status.JoinerNotSet";

        /// <summary>What was tried, without a full stop.</summary>
        public const string JoinerFailed = "NewObject.Status.JoinerFailed";
    }

    /// <summary>
    /// What the message window ("Send message to users") says in its status line (stage V2b-2): preparing the preview,
    /// sending with the part done, the test message, pasting formatted content. The keys are <c>MessageWindow.Status.*</c>:
    /// <c>UserMessages.Status.*</c> is the family of the states of a planned message. A text may name a domain or the
    /// operator's test address; the log copy never does, and no text ever holds the content of a message or the SMTP
    /// password.
    /// </summary>
    public static class MessageWindowStatus
    {
        /// <summary>{0} = the number of accounts.</summary>
        public const string Reading = "MessageWindow.Status.Reading";

        /// <summary>What was skipped, without a full stop: the lead of "No message was prepared: why."</summary>
        public const string NotPrepared = "MessageWindow.Status.NotPrepared";

        /// <summary>What was stopped, without a full stop, and what that means.</summary>
        public const string ReadStopped = "MessageWindow.Status.ReadStopped";
        public const string ReadStoppedReason = "MessageWindow.Status.ReadStoppedReason";

        /// <summary>What was tried, without a full stop.</summary>
        public const string ReadFailed = "MessageWindow.Status.ReadFailed";

        /// <summary>What was skipped, without a full stop: the lead of "No message was sent: why."</summary>
        public const string NotSent = "MessageWindow.Status.NotSent";

        /// <summary>{0} = how far ("12 of 300"); with failures {1} = their number; "Retry": waiting to send one again.</summary>
        public const string Sending = "MessageWindow.Status.Sending";
        public const string SendingFailures = "MessageWindow.Status.SendingFailures";
        public const string SendingRetry = "MessageWindow.Status.SendingRetry";
        public const string SendingFailuresRetry = "MessageWindow.Status.SendingFailuresRetry";

        /// <summary>"{0} message" in the three plural forms (one, few, many).</summary>
        public const string MessagesOne = "MessageWindow.Status.Messages.One";
        public const string MessagesFew = "MessageWindow.Status.Messages.Few";
        public const string MessagesMany = "MessageWindow.Status.Messages.Many";

        /// <summary>{0} = the messages ("12 messages"), {1} = the domain of the recipients.</summary>
        public const string Sent = "MessageWindow.Status.Sent";

        /// <summary>{0} = the messages; for a window without a domain.</summary>
        public const string SentAnywhere = "MessageWindow.Status.SentAnywhere";

        /// <summary>What happened, without a full stop: the leads of a send that did not deliver everything.</summary>
        public const string SentPartly = "MessageWindow.Status.SentPartly";
        public const string SendStopped = "MessageWindow.Status.SendStopped";
        public const string SendFailed = "MessageWindow.Status.SendFailed";

        /// <summary>{0} = accepted, {1} = failed, {2} = not sent.</summary>
        public const string SendCounts = "MessageWindow.Status.SendCounts";

        /// <summary>Where the reasons are, and what to do; {0} = the text of the "Retry failed" button.</summary>
        public const string ReasonsNext = "MessageWindow.Status.ReasonsNext";
        public const string RetryFailedNext = "MessageWindow.Status.RetryFailedNext";

        /// <summary>{0} = the operator's address for test messages (settings).</summary>
        public const string TestSending = "MessageWindow.Status.TestSending";

        /// <summary>What was skipped, without a full stop: the lead of "The test message was not sent: why."</summary>
        public const string TestNotSent = "MessageWindow.Status.TestNotSent";
        public const string TestStopped = "MessageWindow.Status.TestStopped";

        /// <summary>What was skipped, without a full stop: the lead of "Nothing was pasted: why."</summary>
        public const string NotPasted = "MessageWindow.Status.NotPasted";
        public const string PasteStopped = "MessageWindow.Status.PasteStopped";
    }

    /// <summary>
    /// What the settings window says in its status line (stage V2b-2): the tests of the "E-mail messages" tab - the
    /// connection test, the test message (the words of <see cref="MessageWindowStatus"/>) and the accounts of the Outlook
    /// profile. The SMTP password is never part of a text.
    /// </summary>
    public static class SettingsStatus
    {
        /// <summary>{0} = the server ("smtp.contoso.com:587").</summary>
        public const string Testing = "Settings.Status.Testing";

        /// <summary>What was skipped, without a full stop: the lead of "The connection was not tested: why."</summary>
        public const string NotTested = "Settings.Status.NotTested";

        /// <summary>{0} = the server, {1} = "encrypted (…)" or "NOT encrypted", {2} = the authentication used.</summary>
        public const string TestOk = "Settings.Status.TestOk";

        /// <summary>What was found, without a full stop; {0} = the server.</summary>
        public const string TestFailed = "Settings.Status.TestFailed";

        /// <summary>{0} = the server.</summary>
        public const string TestStopped = "Settings.Status.TestStopped";

        /// <summary>{0} = the name of the Outlook profile, {1} = its accounts ("2 accounts").</summary>
        public const string OutlookRead = "Settings.Status.OutlookRead";

        /// <summary>"{0} account" in the three plural forms (one, few, many).</summary>
        public const string AccountsOne = "Settings.Status.Accounts.One";
        public const string AccountsFew = "Settings.Status.Accounts.Few";
        public const string AccountsMany = "Settings.Status.Accounts.Many";

        /// <summary>What was tried, without a full stop.</summary>
        public const string OutlookFailed = "Settings.Status.OutlookFailed";
        public const string OutlookStopped = "Settings.Status.OutlookStopped";
    }

    /// <summary>
    /// What the filter builder says in its status line (stage V2b-2): the test of the filter on the domain controller.
    /// What is wrong with the filter itself is a note next to it (<c>Filters.Issue.*</c>).
    /// </summary>
    public static class FilterEditorStatus
    {
        /// <summary>{0} = the domain.</summary>
        public const string Testing = "FilterEditor.Status.Testing";

        /// <summary>{0} = the domain, {1} = the number of objects, {2} = the seconds it took.</summary>
        public const string Tested = "FilterEditor.Status.Tested";

        /// <summary>{0} = the text of the "Test" button.</summary>
        public const string TestStopped = "FilterEditor.Status.TestStopped";

        /// <summary>What was tried, without a full stop; {0} = the domain.</summary>
        public const string TestFailed = "FilterEditor.Status.TestFailed";

        /// <summary>What was skipped, without a full stop: the lead of "The filter was not tested: why."</summary>
        public const string NotTested = "FilterEditor.Status.NotTested";
    }

    /// <summary>
    /// What the editor of a saved view says in its status line (stage V2b-2): the test of the view - its search base,
    /// scope and filter - on the domain controller. A filter that cannot be read is a note under the filter.
    /// </summary>
    public static class SavedViewEditorStatus
    {
        /// <summary>{0} = the search base as a path ("contoso.test/Firma/IT").</summary>
        public const string Testing = "SavedViewEditor.Status.Testing";

        /// <summary>{0} = the search base as a path, {1} = the number of objects, {2} = the seconds it took.</summary>
        public const string Tested = "SavedViewEditor.Status.Tested";

        /// <summary>{0} = the text of the "Test" button.</summary>
        public const string TestStopped = "SavedViewEditor.Status.TestStopped";

        /// <summary>What was tried, without a full stop; {0} = the search base as a path.</summary>
        public const string TestFailed = "SavedViewEditor.Status.TestFailed";

        /// <summary>What was skipped, without a full stop: the lead of "The view was not tested: why."</summary>
        public const string NotTested = "SavedViewEditor.Status.NotTested";
    }

    /// <summary>
    /// What "Browse for Container" says in its status line (stage V2b-2): the read of the tree when the dialog opens,
    /// and the container whose content could not be read.
    /// </summary>
    public static class ContainerPickerStatus
    {
        /// <summary>{0} = the domain.</summary>
        public const string Reading = "ContainerPicker.Status.Reading";

        /// <summary>What was tried, without a full stop; {0} = the name of the container.</summary>
        public const string ReadFailed = "ContainerPicker.Status.ReadFailed";
    }

    /// <summary>
    /// What the object picker ("Select Users, Computers, or Groups") says in its status line (stage V2b-2): the search
    /// of "Find Now" and the check of the typed names. The sentences about single names are the ones the dialog had
    /// (<see cref="Picker.NotFound"/>, <see cref="Picker.Ambiguous"/>).
    /// </summary>
    public static class ObjectPickerStatus
    {
        /// <summary>{0} = the location as a path ("contoso.test/Firma").</summary>
        public const string Searching = "ObjectPicker.Status.Searching";

        /// <summary>The summary of the list of results; {0} = the objects ("4 objects").</summary>
        public const string Found = "ObjectPicker.Status.Found";

        /// <summary>{0} = the most objects the list shows.</summary>
        public const string TooMany = "ObjectPicker.Status.TooMany";

        /// <summary>{0} = the text of the "Find Now" button.</summary>
        public const string SearchStopped = "ObjectPicker.Status.SearchStopped";

        /// <summary>What was tried, without a full stop; {0} = the location as a path.</summary>
        public const string SearchFailed = "ObjectPicker.Status.SearchFailed";

        /// <summary>{0} = the number of names.</summary>
        public const string Checking = "ObjectPicker.Status.Checking";

        /// <summary>What was skipped, without a full stop: the lead of "Not every name was resolved: why."</summary>
        public const string NotResolved = "ObjectPicker.Status.NotResolved";

        /// <summary>{0} = the text of the "Check Names" button.</summary>
        public const string CheckStopped = "ObjectPicker.Status.CheckStopped";

        /// <summary>What was tried, without a full stop.</summary>
        public const string CheckFailed = "ObjectPicker.Status.CheckFailed";
    }
}
