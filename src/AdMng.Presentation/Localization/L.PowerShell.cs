namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>
    /// The PowerShell window itself (track R, stage R1): its title, the first lines of its scrollback, the switch to the trusted
    /// mode and the words of a prompt. A text may name the domain; nothing here holds a command or a result.
    /// </summary>
    public static class PowerShell
    {
        public const string Name = "PowerShell.Name";

        /// <summary>{0} = the domain, {1} = the domain controller, {2} = the account.</summary>
        public const string WindowTitle = "PowerShell.WindowTitle";

        /// <summary>The title while the trusted mode is on; {0} = the domain, {1} = the domain controller, {2} = the account.</summary>
        public const string WindowTitleTrusted = "PowerShell.WindowTitleTrusted";

        public const string WelcomeMode = "PowerShell.Welcome.Mode";
        public const string WelcomeHelp = "PowerShell.Welcome.Help";
        public const string WelcomeLockdown = "PowerShell.Welcome.Lockdown";

        /// <summary>The label of the switch.</summary>
        public const string TrustedSwitch = "PowerShell.Trusted.Switch";

        public const string TrustedOn = "PowerShell.Trusted.On";
        public const string TrustedOff = "PowerShell.Trusted.Off";
        public const string TrustedConfirmTitle = "PowerShell.Trusted.ConfirmTitle";

        /// <summary>{0} = the domain.</summary>
        public const string TrustedConfirmMessage = "PowerShell.Trusted.ConfirmMessage";

        /// <summary>Why the switch is disabled.</summary>
        public const string TrustedBlockedLockdown = "PowerShell.Trusted.BlockedLockdown";
        public const string TrustedBlockedRunning = "PowerShell.Trusted.BlockedRunning";
        public const string TrustedBlockedStarting = "PowerShell.Trusted.BlockedStarting";

        /// <summary>{0} = the lines that were dropped, {1} = the lines the window keeps.</summary>
        public const string DroppedLines = "PowerShell.DroppedLines";

        /// <summary>The tool tip of a distinguished name: {0} = the name.</summary>
        public const string OpenLinkTip = "PowerShell.OpenLinkTip";

        /// <summary>A choice prompt: the answer is none of the choices.</summary>
        public const string PromptInvalid = "PowerShell.Prompt.Invalid";

        public const string PromptHelp = "PowerShell.Prompt.Help";

        /// <summary>{0} = the choice that is taken by an empty answer.</summary>
        public const string PromptDefault = "PowerShell.Prompt.Default";

        /// <summary>What a screen reader hears for the command line (the editor) and for the output.</summary>
        public const string EditorName = "PowerShell.Editor.Name";

        public const string OutputName = "PowerShell.Output.Name";

        public const string CompletionName = "PowerShell.Completion.Name";

        /// <summary>The hint in the empty editor.</summary>
        public const string EditorHint = "PowerShell.Editor.Hint";

        /// <summary>The hint in the editor while a command waits for an answer.</summary>
        public const string AnswerHint = "PowerShell.Editor.AnswerHint";

        /// <summary>The line of the history search; {0} = what was typed.</summary>
        public const string SearchLabel = "PowerShell.Search.Label";

        public const string SearchFailed = "PowerShell.Search.Failed";

        /// <summary>The label of the button that starts the command; it becomes "Stop" while one runs.</summary>
        public const string RunButton = "PowerShell.Run";

        public const string ClearButton = "PowerShell.Clear";

        public const string ClearTip = "PowerShell.Clear.Tip";

        /// <summary>The context menu of the output.</summary>
        public const string OpenInProgram = "PowerShell.Menu.OpenInProgram";

        public const string CopyOutput = "PowerShell.Menu.CopyOutput";

        public const string SelectAll = "PowerShell.Menu.SelectAll";

        /// <summary>The words a screen reader hears before the glyph-and-colour of a line kind (never colour alone).</summary>
        public const string KindError = "PowerShell.Kind.Error";

        public const string KindWarning = "PowerShell.Kind.Warning";

        public const string KindNote = "PowerShell.Kind.Note";
    }

    /// <summary>The grid window of <c>Out-AdmGrid</c> (track R, stage R1): its title, filter, summary, copy and export.</summary>
    public static class PowerShellGrid
    {
        public const string Title = "PowerShellGrid.Title";

        /// <summary>{0} = the title, {1} = the domain.</summary>
        public const string WindowTitle = "PowerShellGrid.WindowTitle";

        public const string FilterHint = "PowerShellGrid.FilterHint";

        public const string RowsOne = "PowerShellGrid.Rows.One";
        public const string RowsFew = "PowerShellGrid.Rows.Few";
        public const string RowsMany = "PowerShellGrid.Rows.Many";
        public const string ColumnsOne = "PowerShellGrid.Columns.One";
        public const string ColumnsFew = "PowerShellGrid.Columns.Few";
        public const string ColumnsMany = "PowerShellGrid.Columns.Many";

        /// <summary>{0} = the rows shown, {1} = all the rows, {2} = "4 columns".</summary>
        public const string Shown = "PowerShellGrid.Shown";

        /// <summary>{0} = "12 rows", {1} = "4 columns".</summary>
        public const string Summary = "PowerShellGrid.Summary";

        /// <summary>{0} = the number of rows copied.</summary>
        public const string CopiedRows = "PowerShellGrid.CopiedRows";

        public const string Exporting = "PowerShellGrid.Exporting";

        /// <summary>What was tried, without a full stop.</summary>
        public const string ExportFailed = "PowerShellGrid.ExportFailed";

        public const string CopyRows = "PowerShellGrid.Menu.CopyRows";
        public const string CopyRowsWithHeaders = "PowerShellGrid.Menu.CopyRowsWithHeaders";
    }

    /// <summary>
    /// What the PowerShell window says in its status line (track R, stage R1; the wording of docs/dev/v-interface-renewal.md 4.5):
    /// starting the engine, a running command and how it ended, the language mode, the notes that last. A text may name an error of
    /// the command; the log copy never does.
    /// </summary>
    public static class PowerShellStatus
    {
        public const string Starting = "PowerShell.Status.Starting";

        /// <summary>What was tried, without a full stop.</summary>
        public const string StartFailed = "PowerShell.Status.StartFailed";

        public const string StartFailedNext = "PowerShell.Status.StartFailedNext";

        /// <summary>Why: the build has no engine.</summary>
        public const string NotAvailable = "PowerShell.Status.NotAvailable";

        public const string Running = "PowerShell.Status.Running";

        /// <summary>{0} = the activity, {1} = its status text (<c>Write-Progress</c>).</summary>
        public const string Progress = "PowerShell.Status.Progress";

        public const string Waiting = "PowerShell.Status.Waiting";

        /// <summary>{0} = "12 lines of output", {1} = the time ("0.8 s").</summary>
        public const string Done = "PowerShell.Status.Done";

        /// <summary>{0} = the time.</summary>
        public const string DoneNoOutput = "PowerShell.Status.DoneNoOutput";

        /// <summary>"{0} line of output" in the three plural forms.</summary>
        public const string LinesOne = "PowerShell.Status.Lines.One";
        public const string LinesFew = "PowerShell.Status.Lines.Few";
        public const string LinesMany = "PowerShell.Status.Lines.Many";

        /// <summary>{0} = the number of seconds.</summary>
        public const string Seconds = "PowerShell.Status.Seconds";

        /// <summary>What was skipped, without a full stop: the command wrote errors.</summary>
        public const string WithErrors = "PowerShell.Status.WithErrors";

        public const string WithErrorsNext = "PowerShell.Status.WithErrorsNext";

        /// <summary>What was tried, without a full stop.</summary>
        public const string Failed = "PowerShell.Status.Failed";

        public const string FailedNext = "PowerShell.Status.FailedNext";

        /// <summary>{0} = the time.</summary>
        public const string Stopped = "PowerShell.Status.Stopped";

        /// <summary>What was tried, without a full stop.</summary>
        public const string CouldNotRun = "PowerShell.Status.CouldNotRun";

        /// <summary>What was skipped, without a full stop.</summary>
        public const string ModeNotChanged = "PowerShell.Status.ModeNotChanged";

        public const string TrustedNote = "PowerShell.Status.TrustedNote";

        /// <summary>More than one note applies at once; the text on screen is the notes one after the other.</summary>
        public const string Notes = "PowerShell.Status.Notes";

        /// <summary>What was tried, without a full stop: the policies could not be read.</summary>
        public const string PolicyUnreadable = "PowerShell.Status.PolicyUnreadable";

        public const string PolicyUnreadableNext = "PowerShell.Status.PolicyUnreadableNext";
    }

    /// <summary>
    /// What the PowerShell policies of the workstation say (track R, stage R1, decision D7): which setting records what and
    /// where it was set. A text may name the directory a policy writes transcripts to; the log copy never does.
    /// </summary>
    public static class PowerShellPolicy
    {
        /// <summary>The log copy of the note (the text on screen is built from the parts below).</summary>
        public const string Note = "PowerShell.Policy.Note";

        /// <summary>{0} = where the policy was set, {1} = the directory of the transcripts.</summary>
        public const string Transcription = "PowerShell.Policy.Transcription";

        /// <summary>{0} = where the policy was set; the policy names no directory.</summary>
        public const string TranscriptionDefault = "PowerShell.Policy.TranscriptionDefault";

        /// <summary>{0} = where the policy was set.</summary>
        public const string ScriptBlockLogging = "PowerShell.Policy.ScriptBlockLogging";

        /// <summary>{0} = where the policy was set, {1} = the modules.</summary>
        public const string ModuleLogging = "PowerShell.Policy.ModuleLogging";

        /// <summary>The module logging policy lists no module.</summary>
        public const string NoModules = "PowerShell.Policy.NoModules";

        /// <summary>The sentence that closes the note: the program changes no policy and does not get around one.</summary>
        public const string Closing = "PowerShell.Policy.Closing";

        public const string OriginCoreComputer = "PowerShell.Policy.Origin.CoreComputer";
        public const string OriginCoreUser = "PowerShell.Policy.Origin.CoreUser";
        public const string OriginWindowsComputer = "PowerShell.Policy.Origin.WindowsComputer";
        public const string OriginWindowsUser = "PowerShell.Policy.Origin.WindowsUser";
        public const string OriginFileComputer = "PowerShell.Policy.Origin.FileComputer";
        public const string OriginFileUser = "PowerShell.Policy.Origin.FileUser";
        public const string OriginUnknown = "PowerShell.Policy.Origin.Unknown";
    }
}
