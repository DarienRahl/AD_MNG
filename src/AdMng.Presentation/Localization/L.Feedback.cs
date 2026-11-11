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

        /// <summary>{0} = the domain controllers that did not answer, each with its reason.</summary>
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
}
