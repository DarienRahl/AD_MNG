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

    /// <summary>
    /// What a window says when it is closed while an operation that writes runs (stage V2b-5b; <c>RunningWork</c>). {0} is
    /// what is running - the text of its status line, one line for each operation.
    /// </summary>
    public static class CloseWork
    {
        /// <summary>An execution of a change plan: nothing new is started, the object in progress is finished first.</summary>
        public const string StopChanges = "CloseWork.StopChanges";

        /// <summary>A send: what was accepted cannot be recalled.</summary>
        public const string StopMessages = "CloseWork.StopMessages";

        /// <summary>Anything else that writes: what was written stays, the rest is not done.</summary>
        public const string StopOther = "CloseWork.StopOther";

        /// <summary>An operation that cannot be stopped: the window stays open until it ends.</summary>
        public const string Wait = "CloseWork.Wait";

        /// <summary>
        /// The execution of changes that may go on without the window (the owner's decision D-A): the three answers - stop and
        /// close, close and continue in the background, do not close - and what each of them does.
        /// </summary>
        public const string Changes = "CloseWork.Changes";

        /// <summary>The button that stops what runs and closes the window once it ended.</summary>
        public const string StopAndClose = "CloseWork.StopAndClose";

        /// <summary>The button that closes the window at once and lets the execution go on to its end.</summary>
        public const string CloseAndContinue = "CloseWork.CloseAndContinue";

        /// <summary>The button that keeps the window (also Enter and Esc).</summary>
        public const string KeepOpen = "CloseWork.KeepOpen";

        /// <summary>Why the execution cannot go on without the window: it generates passwords that only the window shows.</summary>
        public const string NoBackgroundPasswords = "CloseWork.NoBackground.Passwords";

        /// <summary>Why not: the window it was opened from waits for its result (the change plan preview of a GPO).</summary>
        public const string NoBackgroundCaller = "CloseWork.NoBackground.Caller";

        /// <summary>Why not yet: the bulk creation wizard creates its first account, and starts the others itself.</summary>
        public const string NoBackgroundFirstAccount = "CloseWork.NoBackground.FirstAccount";

        /// <summary>Why not in the connection window: closing it ends the connection.</summary>
        public const string NoBackgroundConnection = "CloseWork.NoBackground.Connection";

        /// <summary>One line of the connection window's question: {0} = the title of the activity, {1} = how far it is.</summary>
        public const string BackgroundLine = "CloseWork.BackgroundLine";
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

        /// <summary>
        /// The numbers of a stopped execution in the three plural forms of the total (one, few, many): "{0} of {1} objects
        /// done, failed: {2}" - {0} = the objects that were done, {1} = the objects the plan had to change, {2} = the objects
        /// that failed.
        /// </summary>
        public const string CountsStoppedOne = "Changes.Status.CountsStopped.One";
        public const string CountsStoppedFew = "Changes.Status.CountsStopped.Few";
        public const string CountsStoppedMany = "Changes.Status.CountsStopped.Many";

        /// <summary>
        /// What the Stop did to the objects that ran when it came, in the three plural forms ({0} = how many): "the one that
        /// ran was finished" - Stop finishes the object in progress.
        /// </summary>
        public const string FinishedOne = "Changes.Status.Finished.One";
        public const string FinishedFew = "Changes.Status.Finished.Few";
        public const string FinishedMany = "Changes.Status.Finished.Many";

        /// <summary>No object was changed; {0} = the objects that failed.</summary>
        public const string NothingChanged = "Changes.Status.NothingChanged";

        /// <summary>Where the reasons of the failures are.</summary>
        public const string ReasonsNext = "Changes.Status.ReasonsNext";

        /// <summary>
        /// The numbers of a result in which some objects were changed only in part: {0} = the objects that were changed, fully
        /// or in part ("3 objects"), {1} = those changed only in part, {2} = the objects of which nothing was changed.
        /// </summary>
        public const string CountsPartly = "Changes.Status.CountsPartly";

        /// <summary>
        /// The last sentence of the failure dialog of a run that the connection reports because its window closed: where every
        /// change and its result are.
        /// </summary>
        public const string SessionLogNext = "Changes.Status.SessionLogNext";
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

        /// <summary>{0} = the domain controllers that could not be read, each with why ("dc02.contoso.test (Access is denied); …").</summary>
        public const string ExactNotRead = "AccountReview.Status.ExactNotRead";

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

    /// <summary>
    /// What the Group Policy documents - the tabs of the main window - say in their status lines (stage V2b-3): the read
    /// of a document, an operation that is being prepared for the change plan preview, and the check of one GPO on its
    /// "Status" tab. The summaries are the texts the documents had (<see cref="GroupPolicy.ObjectsCount"/> and the like).
    /// </summary>
    public static class GroupPolicyStatus
    {
        /// <summary>{0} = the title of the document.</summary>
        public const string Reading = "GroupPolicy.Status.Reading";

        /// <summary>{0} = the title of the document.</summary>
        public const string Stopped = "GroupPolicy.Status.Stopped";

        /// <summary>What was tried, without a full stop; {0} = the title of the document.</summary>
        public const string ReadFailed = "GroupPolicy.Status.ReadFailed";

        /// <summary>The plan of an operation is being made; {0} = the title of the operation ("Rename GPO “…”").</summary>
        public const string Preparing = "GroupPolicy.Status.Preparing";

        /// <summary>What was skipped, without a full stop: the lead of "… – nothing was changed: why."; {0} = the title of the operation.</summary>
        public const string NotChanged = "GroupPolicy.Status.NotChanged";

        /// <summary>Why nothing was changed when the operator stopped the preparation.</summary>
        public const string PreparingStopped = "GroupPolicy.Status.PreparingStopped";

        /// <summary>{0} = the text of the "Check again" button.</summary>
        public const string HealthStopped = "GroupPolicy.Status.HealthStopped";

        /// <summary>What was tried, without a full stop.</summary>
        public const string HealthFailed = "GroupPolicy.Status.HealthFailed";
    }

    /// <summary>
    /// What the administrative templates editor says in its status line besides what every Group Policy document says
    /// (<see cref="GroupPolicyStatus"/>; stage V2b-3): the stage of its read that takes the time.
    /// </summary>
    public static class AdministrativeTemplatesStatus
    {
        /// <summary>The ADMX and ADML files of the central store (or of this computer) are being read; a stage of the load, logged as <see cref="GroupPolicyStatus.Reading"/>.</summary>
        public const string ReadingTemplates = "AdmTemplates.Status.ReadingTemplates";
    }

    /// <summary>
    /// What the GPO picker ("Link an existing GPO…", "Compare with…") says in its status line (stage V2b-3): the read of the
    /// GPOs of the domain when the dialog opens.
    /// </summary>
    public static class GpoPickerStatus
    {
        /// <summary>{0} = the domain.</summary>
        public const string Reading = "GpoPicker.Status.Reading";

        /// <summary>The summary of the list; {0} = how many GPOs it shows.</summary>
        public const string Found = "GpoPicker.Status.Found";

        /// <summary>{0} = the text of the "Retry" button.</summary>
        public const string Stopped = "GpoPicker.Status.Stopped";

        /// <summary>What was tried, without a full stop; {0} = the domain.</summary>
        public const string ReadFailed = "GpoPicker.Status.ReadFailed";
    }

    /// <summary>
    /// What the comparison of two GPOs says in its status line (stage V2b-3): the comparison itself - reading both GPOs, then
    /// the administrative templates that name the policies - and what became of it when it did not end.
    /// </summary>
    public static class GpoComparisonStatus
    {
        /// <summary>{0}, {1} = the names of the two GPOs.</summary>
        public const string Comparing = "GpoComparison.Status.Comparing";

        /// <summary>A stage of the comparison, logged as <see cref="Comparing"/>.</summary>
        public const string ReadingTemplates = "GpoComparison.Status.ReadingTemplates";

        /// <summary>Stopped before anything was shown; {0} = the text of the "Refresh" button.</summary>
        public const string Stopped = "GpoComparison.Status.Stopped";

        /// <summary>Stopped when the rows of the comparison before are shown; {0} = the text of the "Refresh" button.</summary>
        public const string StoppedStale = "GpoComparison.Status.StoppedStale";

        /// <summary>{0} = the text of the "Refresh" button.</summary>
        public const string TemplatesStopped = "GpoComparison.Status.TemplatesStopped";

        /// <summary>What was tried, without a full stop; {0}, {1} = the names of the two GPOs.</summary>
        public const string CompareFailed = "GpoComparison.Status.CompareFailed";

        /// <summary>What was skipped, without a full stop: the lead of "…: why."</summary>
        public const string ExportFailed = "GpoComparison.Status.ExportFailed";
    }

    /// <summary>
    /// What the backup, restore and import window says in its status line (stage V2b-3): the read of the backups of the
    /// folder, the read of the selected backup, the backup itself and what became of them when they did not end. The plan of
    /// a restore or an import says what every Group Policy operation says (<see cref="GroupPolicyStatus.Preparing"/>).
    /// </summary>
    public static class GpoBackupStatus
    {
        public const string ReadingFolder = "GpoBackup.Status.ReadingFolder";

        /// <summary>The summary of the list; {0} = how many backups the folder holds.</summary>
        public const string Found = "GpoBackup.Status.Found";

        public const string NoneFound = "GpoBackup.Status.NoneFound";

        /// <summary>{0} = the text of the "Retry" button.</summary>
        public const string ListStopped = "GpoBackup.Status.ListStopped";

        /// <summary>What was tried, without a full stop.</summary>
        public const string ListFailed = "GpoBackup.Status.ListFailed";

        /// <summary>{0} = the name of the GPO the backup is of.</summary>
        public const string ReadingBackup = "GpoBackup.Status.ReadingBackup";

        /// <summary>{0} = the text of the "Retry" button.</summary>
        public const string BackupReadStopped = "GpoBackup.Status.BackupReadStopped";

        /// <summary>What was tried, without a full stop; {0} = the name of the GPO the backup is of.</summary>
        public const string BackupReadFailed = "GpoBackup.Status.BackupReadFailed";

        public const string BackingUp = "GpoBackup.Status.BackingUp";

        /// <summary>{0} = how far it is ("1 of 3"); logged as <see cref="BackingUp"/>.</summary>
        public const string BackingUpProgress = "GpoBackup.Status.BackingUpProgress";

        /// <summary>{0} = the backups written, {1} = the GPOs, {2} = the text of the button that backs up.</summary>
        public const string BackupStopped = "GpoBackup.Status.BackupStopped";

        /// <summary>What was tried, without a full stop; {0} = the backups that failed, {1} = the GPOs.</summary>
        public const string BackupFailed = "GpoBackup.Status.BackupFailed";

        /// <summary>What was skipped, without a full stop.</summary>
        public const string NotBackedUp = "GpoBackup.Status.NotBackedUp";
    }

    /// <summary>
    /// What the window that updates the ADMX templates of the central store says in its status line (stage V2b-3) besides
    /// the texts it had (<see cref="CentralStore"/>: what is read, downloaded, compared and prepared, and the result of an
    /// update): what became of a load, a comparison and an update when they did not end well. The log copies of the window
    /// are these keys and the keys of <see cref="CentralStore"/> - never a file, an address or a package.
    /// </summary>
    public static class CentralStoreStatus
    {
        /// <summary>What was tried, without a full stop.</summary>
        public const string LoadFailed = "CentralStore.Status.LoadFailed";

        /// <summary>What was tried, without a full stop; {0} = the vendor ("Microsoft Edge").</summary>
        public const string DownloadFailed = "CentralStore.Status.DownloadFailed";

        /// <summary>What to do after a package that cannot be used (a file or a folder the operator picks).</summary>
        public const string ChooseAnother = "CentralStore.Status.ChooseAnother";

        public const string LoadStopped = "CentralStore.Status.LoadStopped";

        /// <summary>{0} = the text of the "Retry" button.</summary>
        public const string CompareStopped = "CentralStore.Status.CompareStopped";

        /// <summary>What was tried, without a full stop.</summary>
        public const string CompareFailed = "CentralStore.Status.CompareFailed";

        /// <summary>What was skipped, without a full stop.</summary>
        public const string NotUpdated = "CentralStore.Status.NotUpdated";

        /// <summary>{0} = the text of the button that updates ("Update…").</summary>
        public const string UpdateStopped = "CentralStore.Status.UpdateStopped";
    }

    /// <summary>
    /// What the bulk creation wizard says in the status line of the batch editor (stage V2b-4): the three pages that say
    /// something of their own - the source of the data, the naming rules, the check - each as the page in front of the
    /// line. The texts of the profile commands and of the results are the ones the pages already had
    /// (<see cref="BulkCreate"/>); the log copies of the wizard are these keys and those - never a cell, a file name, a
    /// profile name or an account.
    /// </summary>
    public static class BulkCreateStatus
    {
        /// <summary>Step 1: a table pasted from the clipboard is being read.</summary>
        public const string ReadingPaste = "BulkCreate.Status.ReadingPaste";

        /// <summary>Step 1: {0} = the name of the file.</summary>
        public const string ReadingFile = "BulkCreate.Status.ReadingFile";

        /// <summary>Step 1: the table is being rebuilt (a header choice, repeated rows appended or replacing the table).</summary>
        public const string Updating = "BulkCreate.Status.Updating";

        /// <summary>{0} = the text of the button that pasted ("Paste", "Paste and replace").</summary>
        public const string PasteStopped = "BulkCreate.Status.PasteStopped";

        /// <summary>{0} = the name of the file, {1} = the text of "Load file…".</summary>
        public const string FileStopped = "BulkCreate.Status.FileStopped";

        /// <summary>What was skipped, without a full stop: a paste that held no table or no rows.</summary>
        public const string NothingPasted = "BulkCreate.Status.NothingPasted";

        /// <summary>What was skipped, without a full stop; {0} = the name of the file.</summary>
        public const string FileNotLoaded = "BulkCreate.Status.FileNotLoaded";

        /// <summary>Why a file was not loaded: the limit.</summary>
        public const string FileTooLarge = "BulkCreate.Status.FileTooLarge";

        /// <summary>What to do after a file that cannot be used.</summary>
        public const string FileNext = "BulkCreate.Status.FileNext";

        /// <summary>What was skipped, without a full stop: a header row that is not in the table.</summary>
        public const string HeaderNotChanged = "BulkCreate.Status.HeaderNotChanged";

        /// <summary>What was skipped, without a full stop: a column that would be one too many.</summary>
        public const string ColumnNotAdded = "BulkCreate.Status.ColumnNotAdded";

        /// <summary>Step 3, what was skipped, without a full stop: a profile that cannot be saved.</summary>
        public const string ProfileNotSaved = "BulkCreate.Status.ProfileNotSaved";

        /// <summary>Step 3, what was skipped, without a full stop: the default profile of the container.</summary>
        public const string DefaultNotChanged = "BulkCreate.Status.DefaultNotChanged";

        /// <summary>Step 3, what was skipped, without a full stop: the account that can join computers.</summary>
        public const string JoinerNotSet = "BulkCreate.Status.JoinerNotSet";

        /// <summary>Step 3, what was tried, without a full stop: reading the account the operator chose.</summary>
        public const string JoinerNotRead = "BulkCreate.Status.JoinerNotRead";
    }

    /// <summary>What the check step (step 4) of the bulk creation wizard says in the status line (stage V2b-4).</summary>
    public static class BulkCheckStatus
    {
        /// <summary>Before the first row was checked (the wait for the global catalog is part of it).</summary>
        public const string CheckingStart = "BulkCheck.Status.CheckingStart";

        /// <summary>{0} = how far ("12 of 120").</summary>
        public const string Checking = "BulkCheck.Status.Checking";

        /// <summary>{0} = the text of the "Check again" button.</summary>
        public const string Stopped = "BulkCheck.Status.Stopped";

        /// <summary>What was tried, without a full stop.</summary>
        public const string Failed = "BulkCheck.Status.Failed";

        /// <summary>What was skipped, without a full stop: the earlier steps are not complete, or there is no session.</summary>
        public const string NotChecked = "BulkCheck.Status.NotChecked";

        /// <summary>{0} = the text of the "Check again" button.</summary>
        public const string Stale = "BulkCheck.Status.Stale";
    }

    /// <summary>What the readiness check says in its status line (stage V2b-4).</summary>
    public static class ReadinessStatus
    {
        /// <summary>{0} = the name of the check ("Quick check"), {1} = how far ("5 of 8"), {2} = the area that is checked.</summary>
        public const string Checking = "Readiness.Status.Checking";

        /// <summary>{0} = the text of the button that checks again.</summary>
        public const string Stopped = "Readiness.Status.Stopped";

        /// <summary>{0} = the text of the button that checks again.</summary>
        public const string Stale = "Readiness.Status.Stale";
    }

    /// <summary>
    /// What the event log documents say in their status line (stage V2b-4): the reader, the lockout investigation and the
    /// failed logons share one set of words. The title of an operation is also the key of its log copy.
    /// </summary>
    public static class EventLogsStatus
    {
        /// <summary>The reader: "Reading the event logs".</summary>
        public const string Reading = "EventLogs.Status.Reading";

        /// <summary>"Could not read the event logs", without a full stop.</summary>
        public const string ReadFailed = "EventLogs.Status.ReadFailed";

        /// <summary>"Loading the channels of {0}"; {0} = the computer.</summary>
        public const string Channels = "EventLogs.Status.Channels";

        /// <summary>"Could not load the channels of {0}", without a full stop.</summary>
        public const string ChannelsFailed = "EventLogs.Status.ChannelsFailed";

        /// <summary>The failed logons: "Checking the failed logons".</summary>
        public const string FailedLogons = "EventLogs.Status.FailedLogons";

        /// <summary>"Could not check the failed logons", without a full stop.</summary>
        public const string FailedLogonsFailed = "EventLogs.Status.FailedLogonsFailed";

        /// <summary>The lockout investigation: "Investigating the lockout of “{0}”"; {0} = the account.</summary>
        public const string Lockout = "EventLogs.Status.Lockout";

        /// <summary>"Could not investigate the lockout of “{0}”", without a full stop.</summary>
        public const string LockoutFailed = "EventLogs.Status.LockoutFailed";

        /// <summary>"{0}…" - an operation that has no stage to say yet; {0} = its title.</summary>
        public const string Working = "EventLogs.Status.Working";

        /// <summary>"{0} – {1}…" - the stage of an operation; {0} = its title, {1} = the stage.</summary>
        public const string Stage = "EventLogs.Status.Stage";

        /// <summary>Stage of the reader: "sources: {0}, events: {1}"; {0} = how far ("2 of 3").</summary>
        public const string StageSources = "EventLogs.Status.StageSources";

        /// <summary>Stage: "reading the logs, sources: {0}, events: {1}".</summary>
        public const string StageLogs = "EventLogs.Status.StageLogs";

        /// <summary>Stage: "reading the failed logons, sources: {0}, events: {1}".</summary>
        public const string StageFailures = "EventLogs.Status.StageFailures";

        /// <summary>Stage: "reading the successful logons, accounts: {0}".</summary>
        public const string StageSuccesses = "EventLogs.Status.StageSuccesses";

        public const string StageAccount = "EventLogs.Status.StageAccount";

        public const string StageDomainControllers = "EventLogs.Status.StageDomainControllers";

        public const string StageAnalysis = "EventLogs.Status.StageAnalysis";

        /// <summary>{0} = the title of the operation, {1} = the text of the button that reads again.</summary>
        public const string Stopped = "EventLogs.Status.Stopped";

        /// <summary>{0} = the title of the operation, {1} = the events that were read and are shown, {2} = the text of the button.</summary>
        public const string StoppedPartial = "EventLogs.Status.StoppedPartial";

        /// <summary>{0} = the title of the operation, {1} = the sources not read ("1 of 3"), {2} = their names, {3} = the title of the panel that says why.</summary>
        public const string NotRead = "EventLogs.Status.NotRead";

        /// <summary>"Could not open the model of {0}", without a full stop.</summary>
        public const string ModelFailed = "EventLogs.Status.ModelFailed";
    }

    /// <summary>What the computer review says in its status line (stage V2b-4).</summary>
    public static class ComputerReviewStatus
    {
        /// <summary>{0} = the scope.</summary>
        public const string Checking = "ComputerReview.Status.Checking";

        /// <summary>{0} = the scope, {1} = the computers read so far, {2} = the computers with findings so far.</summary>
        public const string CheckingCount = "ComputerReview.Status.CheckingCount";

        /// <summary>{0} = the computers that were read, {1} = the text of the button that checks again.</summary>
        public const string Stopped = "ComputerReview.Status.Stopped";

        /// <summary>What was tried, without a full stop.</summary>
        public const string Failed = "ComputerReview.Status.Failed";

        /// <summary>What was skipped, without a full stop: the scope cannot be resolved.</summary>
        public const string NotChecked = "ComputerReview.Status.NotChecked";

        /// <summary>{0} = the number of computers.</summary>
        public const string ReadingExact = "ComputerReview.Status.ReadingExact";

        /// <summary>{0} = the domain controllers that could not be read, each with why ("dc02.contoso.test (Access is denied); …").</summary>
        public const string ExactNotRead = "ComputerReview.Status.ExactNotRead";

        /// <summary>{0} = the text of the button that reads again.</summary>
        public const string ExactStopped = "ComputerReview.Status.ExactStopped";

        /// <summary>What was tried, without a full stop.</summary>
        public const string ExactFailed = "ComputerReview.Status.ExactFailed";

        /// <summary>{0} = the number of computers.</summary>
        public const string ResolvingSites = "ComputerReview.Status.ResolvingSites";

        /// <summary>{0} = how far ("120 of 300").</summary>
        public const string ResolvingSitesCount = "ComputerReview.Status.ResolvingSitesCount";

        /// <summary>{0} = how many computers have a site ("280 of 300").</summary>
        public const string SitesDone = "ComputerReview.Status.SitesDone";

        /// <summary>{0} = the text of the button that finds the sites.</summary>
        public const string SitesStopped = "ComputerReview.Status.SitesStopped";

        /// <summary>What was tried, without a full stop.</summary>
        public const string SitesFailed = "ComputerReview.Status.SitesFailed";

        /// <summary>{0} = the computer.</summary>
        public const string Pinging = "ComputerReview.Status.Pinging";

        /// <summary>{0} = the computer, {1} = the text of the button.</summary>
        public const string PingStopped = "ComputerReview.Status.PingStopped";

        /// <summary>What was tried, without a full stop; {0} = the computer.</summary>
        public const string PingFailed = "ComputerReview.Status.PingFailed";
    }

    /// <summary>What the remote actions window says in its status line (stage V2b-4); {0} is always the computer.</summary>
    public static class RemoteStatus
    {
        /// <summary>"{0}…" - the title of an action with the dots.</summary>
        public const string Working = "RemoteActions.Status.Working";

        public const string TitleStatus = "RemoteActions.Status.TitleStatus";
        public const string TitleUsers = "RemoteActions.Status.TitleUsers";
        public const string TitleServices = "RemoteActions.Status.TitleServices";
        public const string TitlePolicy = "RemoteActions.Status.TitlePolicy";
        public const string TitleRestart = "RemoteActions.Status.TitleRestart";
        public const string TitleAbortRestart = "RemoteActions.Status.TitleAbortRestart";

        /// <summary>What was tried, without a full stop.</summary>
        public const string FailedStatus = "RemoteActions.Status.FailedStatus";
        public const string FailedUsers = "RemoteActions.Status.FailedUsers";
        public const string FailedServices = "RemoteActions.Status.FailedServices";
        public const string FailedPolicy = "RemoteActions.Status.FailedPolicy";
        public const string FailedRestart = "RemoteActions.Status.FailedRestart";
        public const string FailedAbortRestart = "RemoteActions.Status.FailedAbortRestart";

        /// <summary>A read that was stopped; {0} = its title, {1} = the text of the button that reads again.</summary>
        public const string StoppedRead = "RemoteActions.Status.StoppedRead";

        /// <summary>An action that was stopped: whether the computer got the request is not known; {0} = its title.</summary>
        public const string StoppedAction = "RemoteActions.Status.StoppedAction";

        /// <summary>{0} = the computer, {1} = what happened.</summary>
        public const string ActionDone = "RemoteActions.Status.ActionDone";
    }

    /// <summary>
    /// What the four PKI documents (the overview, the templates, the readiness report and the security review) say in their
    /// status line (stage V2b-4, unit 3): one set of words for the read that is the operation of the tab, its stages, and the
    /// two checks that touch the domain controllers or the network. The title of an operation is also the key of its log copy.
    /// </summary>
    public static class PkiDocumentStatus
    {
        /// <summary>"{0}…" - an operation that has no stage to say yet; {0} = its title.</summary>
        public const string Working = "PkiDoc.Status.Working";

        /// <summary>"{0} – {1}…" - the stage of an operation; {0} = its title, {1} = the stage.</summary>
        public const string Stage = "PkiDoc.Status.Stage";

        /// <summary>The read of a document, without the dots: "Reading “{0}”"; {0} = the title of the document.</summary>
        public const string Reading = "PkiDoc.Status.Reading";

        /// <summary>What was tried, without a full stop: "Could not read “{0}”"; {0} = the title of the document.</summary>
        public const string ReadFailed = "PkiDoc.Status.ReadFailed";

        /// <summary>A read that was stopped; {0} = its title, {1} = the text of the button that reads again.</summary>
        public const string Stopped = "PkiDoc.Status.Stopped";

        /// <summary>A check that was stopped and changed nothing; {0} = its title, {1} = the text of the button that checks again.</summary>
        public const string StoppedKept = "PkiDoc.Status.StoppedKept";

        /// <summary>The detail of a summary: "read from {0}, {1}"; {0} = the domain controller, {1} = when.</summary>
        public const string ReadAt = "PkiDoc.Status.ReadAt";

        /// <summary>Stage: "AD CS objects: {0}"; {0} = how many were read.</summary>
        public const string StageObjects = "PkiDoc.Status.StageObjects";

        /// <summary>Stage: "reading the rights of the templates".</summary>
        public const string StageRights = "PkiDoc.Status.StageRights";

        /// <summary>Stage: "accounts: {0}"; {0} = how many were read.</summary>
        public const string StageAccounts = "PkiDoc.Status.StageAccounts";

        /// <summary>Stage: "reading the domain controllers".</summary>
        public const string StageControllers = "PkiDoc.Status.StageControllers";

        /// <summary>Stage: "checking the LDAPS certificates of the domain controllers".</summary>
        public const string StageLdaps = "PkiDoc.Status.StageLdaps";

        /// <summary>Stage: "reading the permissions of the PKI objects".</summary>
        public const string StageAccessControl = "PkiDoc.Status.StageAccessControl";

        /// <summary>Stage: "resolving the accounts and groups".</summary>
        public const string StagePrincipals = "PkiDoc.Status.StagePrincipals";

        /// <summary>Stage: "evaluating the ESC rules".</summary>
        public const string StageAnalysis = "PkiDoc.Status.StageAnalysis";

        /// <summary>The probe of the LDAPS certificates, without the dots: "Checking the LDAPS certificates of the domain controllers".</summary>
        public const string LdapsChecking = "PkiDoc.Status.LdapsChecking";

        /// <summary>What was tried, without a full stop.</summary>
        public const string LdapsFailed = "PkiDoc.Status.LdapsFailed";
    }

    /// <summary>What the PKI overview says besides <see cref="PkiDocumentStatus"/>: the check of the HTTP addresses (stage V2b-4, unit 3).</summary>
    public static class PkiOverviewStatus
    {
        /// <summary>"Checking the AIA, CDP and OCSP addresses", without the dots.</summary>
        public const string HttpChecking = "PkiOverview.Status.HttpChecking";

        /// <summary>What was tried, without a full stop.</summary>
        public const string HttpFailed = "PkiOverview.Status.HttpFailed";
    }

    /// <summary>What the templates document says besides <see cref="PkiDocumentStatus"/>: the count of the accounts (stage V2b-4, unit 3).</summary>
    public static class PkiTemplatesStatus
    {
        /// <summary>Stage: "counting the accounts: {0}"; {0} = how many were read.</summary>
        public const string StageCounting = "PkiTemplates.Status.StageCounting";

        /// <summary>The detail of the summary: "accounts counted: {0}".</summary>
        public const string CountsDone = "PkiTemplates.Status.CountsDone";

        /// <summary>What was tried, without a full stop.</summary>
        public const string CountFailed = "PkiTemplates.Status.CountFailed";

        /// <summary>What the list lacks and what to do; {0} = the text of the Retry action.</summary>
        public const string CountFailedNext = "PkiTemplates.Status.CountFailedNext";
    }

    /// <summary>What the security review says instead of <see cref="PkiDocumentStatus"/>'s reading: its check (stage V2b-4, unit 3).</summary>
    public static class PkiSecurityStatus
    {
        /// <summary>"Checking AD CS security", without the dots.</summary>
        public const string Checking = "PkiSecurity.Status.Checking";

        /// <summary>What was tried, without a full stop.</summary>
        public const string CheckFailed = "PkiSecurity.Status.CheckFailed";

        /// <summary>{0} = the title of the check, {1} = the text of the button that checks again.</summary>
        public const string Stopped = "PkiSecurity.Status.Stopped";
    }

    /// <summary>What the Watchdog tab says about its connection to the service in its one status line (stage V2b-4, unit 4).</summary>
    public static class WatchdogDocStatus
    {
        /// <summary>What was tried, without a full stop; {0} = host:port of the service.</summary>
        public const string ConnectFailed = "WatchdogDoc.Status.ConnectFailed";

        /// <summary>What to do about a certificate; {0} = the text of the action.</summary>
        public const string ReviewNext = "WatchdogDoc.Status.ReviewNext";
    }

    /// <summary>What the event feed of the Watchdog tab says in the status line.</summary>
    public static class WatchdogEventsStatus
    {
        /// <summary>The read of the newest page with the current filter.</summary>
        public const string Reading = "WatchdogEvents.Status.Reading";

        /// <summary>{0} = the events loaded so far.</summary>
        public const string ReadingOlder = "WatchdogEvents.Status.ReadingOlder";

        /// <summary>{0} = how far it is ("12 of 300").</summary>
        public const string Acknowledging = "WatchdogEvents.Status.Acknowledging";

        /// <summary>What was tried, without a full stop.</summary>
        public const string ReadFailed = "WatchdogEvents.Status.ReadFailed";

        /// <summary>What was tried, without a full stop.</summary>
        public const string OlderFailed = "WatchdogEvents.Status.OlderFailed";

        /// <summary>What was tried, without a full stop.</summary>
        public const string AckFailed = "WatchdogEvents.Status.AckFailed";

        /// <summary>What to do after a partial acknowledgement.</summary>
        public const string AckPartial = "WatchdogEvents.Status.AckPartial";

        /// <summary>{0} = the text of the action that reads again.</summary>
        public const string Stopped = "WatchdogEvents.Status.Stopped";

        /// <summary>{0} = the text of the action.</summary>
        public const string StoppedOlder = "WatchdogEvents.Status.StoppedOlder";
    }

    /// <summary>What the details pane of an event says in the status line of the feed.</summary>
    public static class WatchdogDetailsStatus
    {
        /// <summary>The acknowledgement of the shown event.</summary>
        public const string Acknowledging = "WatchdogDetails.Status.Acknowledging";

        /// <summary>What was tried, without a full stop.</summary>
        public const string AckFailed = "WatchdogDetails.Status.AckFailed";

        /// <summary>What was tried, without a full stop.</summary>
        public const string OpenFailed = "WatchdogDetails.Status.OpenFailed";
    }

    /// <summary>What the configuration of the service (the Rules, Channels, Digest and Sources tabs) says in the status line.</summary>
    public static class WatchdogConfigStatus
    {
        /// <summary>The read of the configuration.</summary>
        public const string Loading = "WatchdogConfig.Status.Loading";

        /// <summary>What was tried, without a full stop.</summary>
        public const string ReadFailed = "WatchdogConfig.Status.ReadFailed";

        /// <summary>{0} = the text of the action.</summary>
        public const string Stopped = "WatchdogConfig.Status.Stopped";

        /// <summary>The validation on the service.</summary>
        public const string Validating = "WatchdogConfig.Status.Validating";

        /// <summary>What was tried, without a full stop.</summary>
        public const string ValidateFailed = "WatchdogConfig.Status.ValidateFailed";

        /// <summary>{0} = the text of the action.</summary>
        public const string ValidateStopped = "WatchdogConfig.Status.ValidateStopped";

        /// <summary>The answer of a validation that found errors.</summary>
        public const string Invalid = "WatchdogConfig.Status.Invalid";

        /// <summary>The save (no Stop: it is a write).</summary>
        public const string Saving = "WatchdogConfig.Status.Saving";

        /// <summary>What was skipped, without a full stop.</summary>
        public const string SaveFailed = "WatchdogConfig.Status.SaveFailed";
    }

    /// <summary>What the digest tab says in the status line.</summary>
    public static class WatchdogDigestStatus
    {
        /// <summary>The preview of the digest as it would be sent now.</summary>
        public const string Previewing = "WatchdogDigest.Status.Previewing";

        /// <summary>What was tried, without a full stop.</summary>
        public const string PreviewFailed = "WatchdogDigest.Status.PreviewFailed";

        /// <summary>{0} = the text of the action.</summary>
        public const string PreviewStopped = "WatchdogDigest.Status.PreviewStopped";

        /// <summary>The "send now" (no Stop: it is a write).</summary>
        public const string Queuing = "WatchdogDigest.Status.Queuing";

        /// <summary>What was tried, without a full stop.</summary>
        public const string QueueFailed = "WatchdogDigest.Status.QueueFailed";
    }

    /// <summary>What the rules tab says in the status line (the preview of a rule, the reset of a built-in rule).</summary>
    public static class WatchdogRulesStatus
    {
        /// <summary>The preview of the rule being edited.</summary>
        public const string Previewing = "WatchdogRules.Status.Previewing";

        /// <summary>What was tried, without a full stop.</summary>
        public const string PreviewFailed = "WatchdogRules.Status.PreviewFailed";

        /// <summary>{0} = the text of the action.</summary>
        public const string PreviewStopped = "WatchdogRules.Status.PreviewStopped";

        /// <summary>The read of the shipped rules behind "reset to default".</summary>
        public const string ResetReading = "WatchdogRules.Status.ResetReading";

        /// <summary>What was skipped, without a full stop.</summary>
        public const string ResetFailed = "WatchdogRules.Status.ResetFailed";

        /// <summary>{0} = the text of the action.</summary>
        public const string ResetStopped = "WatchdogRules.Status.ResetStopped";
    }

    /// <summary>What the channels tab says in the status line besides the existing words of a test (stage V2b-4, unit 4).</summary>
    public static class WatchdogChannelsStatus
    {
        /// <summary>A test that was stopped.</summary>
        public const string TestStopped = "WatchdogChannels.Status.TestStopped";

        /// <summary>What to do after a failed test.</summary>
        public const string TestFailedNext = "WatchdogChannels.Status.TestFailedNext";
    }

    /// <summary>What the status tab of the service says in the status line.</summary>
    public static class WatchdogPageStatus
    {
        /// <summary>The read of the status.</summary>
        public const string Reading = "WatchdogPage.Status.Reading";

        /// <summary>What was tried, without a full stop.</summary>
        public const string ReadFailed = "WatchdogPage.Status.ReadFailed";

        /// <summary>{0} = the text of the action.</summary>
        public const string Stopped = "WatchdogPage.Status.Stopped";

        /// <summary>The summary; {0} = the time.</summary>
        public const string Summary = "WatchdogPage.Status.Summary";

        /// <summary>The read of a page of the delivery log.</summary>
        public const string LogReading = "WatchdogPage.Status.LogReading";

        /// <summary>What was tried, without a full stop.</summary>
        public const string LogFailed = "WatchdogPage.Status.LogFailed";

        /// <summary>{0} = the text of the action.</summary>
        public const string LogStopped = "WatchdogPage.Status.LogStopped";

        /// <summary>The retry of a dead delivery (no Stop: it is a write).</summary>
        public const string RetryQueuing = "WatchdogPage.Status.RetryQueuing";

        /// <summary>What was tried, without a full stop.</summary>
        public const string RetryFailed = "WatchdogPage.Status.RetryFailed";

        /// <summary>The read of a page of the configuration audit.</summary>
        public const string AuditReading = "WatchdogPage.Status.AuditReading";

        /// <summary>What was tried, without a full stop.</summary>
        public const string AuditFailed = "WatchdogPage.Status.AuditFailed";

        /// <summary>{0} = the text of the action.</summary>
        public const string AuditStopped = "WatchdogPage.Status.AuditStopped";
    }
    /// <summary>What the Korespondencja window says in its line besides the sentences it had (stage V2b-4, unit 5).</summary>
    public static class CorrespondenceStatus
    {
        /// <summary>Added to "the session of this window ended" on the pages that read the directory.</summary>
        public const string OutlookStillWorks = "Correspondence.Status.OutlookStillWorks";

        /// <summary>What was tried, without a full stop.</summary>
        public const string SignaturesNotRead = "Correspondence.Status.SignaturesNotRead";
    }

    /// <summary>What a page that needs Outlook says while Outlook cannot be used.</summary>
    public static class OutlookStatus
    {
        /// <summary>{0} = why (the text of the failure).</summary>
        public const string Unavailable = "Correspondence.Outlook.Status.Unavailable";
    }

    /// <summary>What the recipients page says in the line besides the sentences it had.</summary>
    public static class RecipientsStatus
    {
        /// <summary>{0} = the objects, counted ("12 objects").</summary>
        public const string DirectoryReading = "Correspondence.Recipients.Status.DirectoryReading";

        /// <summary>What was tried, without a full stop.</summary>
        public const string DirectoryNothingAdded = "Correspondence.Recipients.Status.DirectoryNothingAdded";

        /// <summary>Why nothing was added: the operator stopped the read.</summary>
        public const string DirectoryStopped = "Correspondence.Recipients.Status.DirectoryStopped";

        /// <summary>{0} = the name of the file.</summary>
        public const string FileReading = "Correspondence.Recipients.Status.FileReading";

        /// <summary>{0} = how far ("2 of 5").</summary>
        public const string FilesReading = "Correspondence.Recipients.Status.FilesReading";

        /// <summary>{0} = files read, {1} = files.</summary>
        public const string FilesStopped = "Correspondence.Recipients.Status.FilesStopped";

        /// <summary>{0} = what was loaded, {1} = why the other files were not.</summary>
        public const string ImportPartly = "Correspondence.Recipients.Status.ImportPartly";

        /// <summary>{0} = why.</summary>
        public const string ImportFailed = "Correspondence.Recipients.Status.ImportFailed";

        /// <summary>What was tried, without a full stop.</summary>
        public const string NothingLoaded = "Correspondence.Recipients.Status.NothingLoaded";
    }

    /// <summary>What the mailbox owners page says in the line besides the sentences it had.</summary>
    public static class MailboxesStatus
    {
        public const string Finding = "Correspondence.Mailboxes.Status.Finding";

        /// <summary>{0} = mailboxes read.</summary>
        public const string FindingMailboxes = "Correspondence.Mailboxes.Status.FindingMailboxes";

        /// <summary>{0} = mailboxes, {1} = persons read.</summary>
        public const string FindingPersons = "Correspondence.Mailboxes.Status.FindingPersons";

        /// <summary>What was stopped, without a full stop.</summary>
        public const string FindStopped = "Correspondence.Mailboxes.Status.FindStopped";

        /// <summary>What the list is like after a stop and what to do.</summary>
        public const string FindStoppedReason = "Correspondence.Mailboxes.Status.FindStoppedReason";

        /// <summary>What was tried, without a full stop.</summary>
        public const string FindFailed = "Correspondence.Mailboxes.Status.FindFailed";

        /// <summary>What was tried, without a full stop.</summary>
        public const string NotSearched = "Correspondence.Mailboxes.Status.NotSearched";

        /// <summary>What was tried, without a full stop.</summary>
        public const string NothingAdded = "Correspondence.Mailboxes.Status.NothingAdded";

        /// <summary>What was tried, without a full stop.</summary>
        public const string AddFailed = "Correspondence.Mailboxes.Status.AddFailed";
    }

    /// <summary>What the send page says in the line besides the sentences it had.</summary>
    public static class SendStatus
    {
        /// <summary>{0} = done, {1} = messages.</summary>
        public const string Sending = "Correspondence.Send.Status.Sending";

        /// <summary>{0} = done, {1} = messages, {2} = the time that is left.</summary>
        public const string SendingEta = "Correspondence.Send.Status.SendingEta";

        /// <summary>{0} = done, {1} = messages.</summary>
        public const string Drafting = "Correspondence.Send.Status.Drafting";

        /// <summary>{0} = done, {1} = messages.</summary>
        public const string Testing = "Correspondence.Send.Status.Testing";

        /// <summary>Where the result of every message is.</summary>
        public const string ResultsBelow = "Correspondence.Send.Status.ResultsBelow";

        public const string FailedNext = "Correspondence.Send.Status.FailedNext";
        public const string StoppedNextSend = "Correspondence.Send.Status.StoppedNextSend";
        public const string StoppedNextDrafts = "Correspondence.Send.Status.StoppedNextDrafts";
        public const string StoppedNextTest = "Correspondence.Send.Status.StoppedNextTest";

        /// <summary>{0} = how many failures in a row.</summary>
        public const string StoppedFailuresInRow = "Correspondence.Send.Status.StoppedFailuresInRow";

        public const string OutlookStoppedNext = "Correspondence.Send.Status.OutlookStoppedNext";

        /// <summary>What was tried, without a full stop (a press that came to nothing).</summary>
        public const string NothingSent = "Correspondence.Send.Status.NothingSent";

        /// <summary>What was tried, without a full stop.</summary>
        public const string NothingDrafted = "Correspondence.Send.Status.NothingDrafted";

        /// <summary>What was tried, without a full stop.</summary>
        public const string TestNotSent = "Correspondence.Send.Status.TestNotSent";
    }

    /// <summary>What the responses page says in the line besides the sentences it had.</summary>
    public static class ResponsesStatus
    {
        /// <summary>What was tried, without a full stop.</summary>
        public const string CampaignsFailed = "Correspondence.Responses.Status.CampaignsFailed";

        public const string CampaignsStopped = "Correspondence.Responses.Status.CampaignsStopped";

        /// <summary>What was tried, without a full stop.</summary>
        public const string ResponsesFailed = "Correspondence.Responses.Status.ResponsesFailed";

        public const string ResponsesStopped = "Correspondence.Responses.Status.ResponsesStopped";
    }

    /// <summary>What the preview page says in the line.</summary>
    public static class PreviewStatus
    {
        public const string OutlookBuilding = "Correspondence.Preview.Status.OutlookBuilding";
        public const string OutlookStopped = "Correspondence.Preview.Status.OutlookStopped";

        /// <summary>What was tried, without a full stop.</summary>
        public const string OutlookFailed = "Correspondence.Preview.Status.OutlookFailed";
    }
}
