namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>"Pomoc → Zgłoś problem…" (<see cref="Diagnostics.ProblemReportViewModel"/>, design note s-pilot-readiness.md 3).</summary>
    public static class ProblemReport
    {
        /// <summary>Menu item and palette command <c>help.reportProblem</c>.</summary>
        public const string Command = "ProblemReport.Command";
        public const string WindowTitle = "ProblemReport.WindowTitle";

        /// <summary>The explanation above the report in the window.</summary>
        public const string Intro = "ProblemReport.Intro";
        public const string SaveAs = "ProblemReport.SaveAs";
        public const string Copied = "ProblemReport.Copied";
        public const string SaveTitle = "ProblemReport.SaveTitle";
        public const string FileFilter = "ProblemReport.FileFilter";

        /// <summary>{0} = file name.</summary>
        public const string Saved = "ProblemReport.Saved";

        // ---- The report text ----
        public const string Heading = "ProblemReport.Heading";

        /// <summary>{0} = time (UTC, ISO 8601).</summary>
        public const string Created = "ProblemReport.Created";
        public const string ValueFree = "ProblemReport.ValueFree";
        public const string SectionEnvironment = "ProblemReport.SectionEnvironment";

        /// <summary>{0} = version.</summary>
        public const string Version = "ProblemReport.Version";

        /// <summary>{0} = operating system, {1} = its architecture.</summary>
        public const string OperatingSystem = "ProblemReport.OperatingSystem";

        /// <summary>{0} = .NET runtime, {1} = process architecture.</summary>
        public const string Runtime = "ProblemReport.Runtime";

        /// <summary>{0} = UI culture, {1} = culture of formats.</summary>
        public const string Culture = "ProblemReport.Culture";
        public const string SectionConnection = "ProblemReport.SectionConnection";
        public const string NotConnected = "ProblemReport.NotConnected";

        /// <summary>{0} = localized logon type, {1} = its code.</summary>
        public const string Logon = "ProblemReport.Logon";

        /// <summary>{0} = <see cref="MechanismNegotiate"/> or <see cref="MechanismSimpleBind"/>.</summary>
        public const string Mechanism = "ProblemReport.Mechanism";
        public const string MechanismNegotiate = "ProblemReport.MechanismNegotiate";
        public const string MechanismSimpleBind = "ProblemReport.MechanismSimpleBind";

        /// <summary>{0} = localized channel protection, {1} = its code.</summary>
        public const string Protection = "ProblemReport.Protection";

        /// <summary>{0} signing, {1} sealing, {2} LDAPS, {3} StartTLS (yes/no each).</summary>
        public const string Flags = "ProblemReport.Flags";

        /// <summary>{0} = yes/no.</summary>
        public const string GlobalCatalog = "ProblemReport.GlobalCatalog";

        /// <summary>{0} = SASL security strength factor.</summary>
        public const string SecurityStrength = "ProblemReport.SecurityStrength";
        public const string SectionFeatures = "ProblemReport.SectionFeatures";

        /// <summary>{0} = feature state.</summary>
        public const string Outlook = "ProblemReport.Outlook";

        /// <summary>{0} = feature state.</summary>
        public const string WebView2 = "ProblemReport.WebView2";

        /// <summary>{0} = yes/no.</summary>
        public const string Watchdog = "ProblemReport.Watchdog";
        public const string FeatureDisabled = "ProblemReport.FeatureDisabled";
        public const string FeatureUnknown = "ProblemReport.FeatureUnknown";
        public const string SectionReadiness = "ProblemReport.SectionReadiness";
        public const string ReadinessNone = "ProblemReport.ReadinessNone";

        /// <summary>{0} = entries shown, {1} = entries logged since the start.</summary>
        public const string SectionLog = "ProblemReport.SectionLog";
        public const string LogEmpty = "ProblemReport.LogEmpty";
    }
}
