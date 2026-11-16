namespace AdMng.Presentation.Localization;

public static partial class L
{
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
