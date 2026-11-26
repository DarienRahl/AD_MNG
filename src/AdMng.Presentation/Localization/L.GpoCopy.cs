using AdMng.Core.GroupPolicy.Management;

namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>
    /// Copying, removing and merging GPO settings (K7, docs/dev/k-computers-and-gpo.md 21.5, 21.8): the "Kopiuj ustawienia"
    /// window in its copy and merge modes, the conflict modes, the titles of the plans, the "Zmiany ustawień" texts and the
    /// "nothing would change" dialog. The outcome texts are a dynamic family (<see cref="OutcomeKey"/>, <see cref="AllKeys"/>);
    /// the refusals (<c>GpoCopy.Refusal.*</c>) are <see cref="AdMng.Presentation.GroupPolicy.Copy.GpoUnitRefusalText"/>.
    /// </summary>
    public static class GpoCopy
    {
        /// <summary>{0} = the target GPO.</summary>
        public const string WindowTitle = "GpoCopy.WindowTitle";

        /// <summary>{0} = A (merged), {1} = B (target).</summary>
        public const string MergeWindowTitle = "GpoCopy.MergeWindowTitle";

        /// <summary>The GPO picker of the target ("sources are not offered").</summary>
        public const string PickTargetTitle = "GpoCopy.PickTargetTitle";

        /// <summary>{0} = the target GPO.</summary>
        public const string TargetText = "GpoCopy.TargetText";

        /// <summary>{0} = checked, {1} = offered.</summary>
        public const string CheckedCount = "GpoCopy.CheckedCount";

        /// <summary>{0} = the number of settings that cannot be copied.</summary>
        public const string RefusedNote = "GpoCopy.RefusedNote";

        /// <summary>{0} = source GPOs chosen, {1} = the limit.</summary>
        public const string TooManySources = "GpoCopy.TooManySources";

        public const string ColumnSetting = "GpoCopy.ColumnSetting";
        public const string ColumnSource = "GpoCopy.ColumnSource";
        public const string ColumnValue = "GpoCopy.ColumnValue";
        public const string ColumnRemark = "GpoCopy.ColumnRemark";
        public const string ConflictLabel = "GpoCopy.ConflictLabel";
        public const string ConflictReplace = "GpoCopy.ConflictReplace";
        public const string ConflictReplaceHint = "GpoCopy.ConflictReplaceHint";
        public const string ConflictSkip = "GpoCopy.ConflictSkip";
        public const string ConflictSkipHint = "GpoCopy.ConflictSkipHint";
        public const string ConflictUnion = "GpoCopy.ConflictUnion";
        public const string ConflictUnionHint = "GpoCopy.ConflictUnionHint";

        /// <summary>{0} = the derived mode ("Zastąp"/"Pomiń").</summary>
        public const string ConflictDerived = "GpoCopy.ConflictDerived";
        public const string ConflictDerivedHint = "GpoCopy.ConflictDerivedHint";
        public const string CopyButton = "GpoCopy.CopyButton";
        public const string CheckAll = "GpoCopy.CheckAll";
        public const string CheckNone = "GpoCopy.CheckNone";
        public const string MergeButton = "GpoCopy.MergeButton";

        // Merge mode
        /// <summary>{0} = A, {1} = B.</summary>
        public const string MergeIntro = "GpoCopy.MergeIntro";

        /// <summary>{0} = A, {1} = B.</summary>
        public const string MergeOrderSourceFirst = "GpoCopy.MergeOrderSourceFirst";

        /// <summary>{0} = A, {1} = B.</summary>
        public const string MergeOrderTargetFirst = "GpoCopy.MergeOrderTargetFirst";

        /// <summary>{0} = A, {1} = B.</summary>
        public const string MergeOrderNoShared = "GpoCopy.MergeOrderNoShared";

        /// <summary>{0} = A, {1} = B.</summary>
        public const string MergeOrderMixed = "GpoCopy.MergeOrderMixed";
        public const string MergeBlockingHeading = "GpoCopy.MergeBlockingHeading";
        public const string MergeWarningsHeading = "GpoCopy.MergeWarningsHeading";
        public const string MergeSecurityFilterDiffers = "GpoCopy.MergeSecurityFilterDiffers";
        public const string MergeWmiFilterDiffers = "GpoCopy.MergeWmiFilterDiffers";

        /// <summary>{0} = the GPO linked alone there, {1} = the containers, {2} = the other GPO.</summary>
        public const string MergeOnlySourceLinked = "GpoCopy.MergeOnlySourceLinked";

        /// <summary>{0} = the GPO linked alone there, {1} = the containers, {2} = the other GPO.</summary>
        public const string MergeOnlyTargetLinked = "GpoCopy.MergeOnlyTargetLinked";

        /// <summary>{0} = the number of settings of A that are copied.</summary>
        public const string MergeSettingsHeading = "GpoCopy.MergeSettingsHeading";

        // Plan titles
        /// <summary>{0} = the target GPO.</summary>
        public const string TitleCopy = "GpoCopy.TitleCopy";

        /// <summary>{0} = the GPO.</summary>
        public const string TitleRemove = "GpoCopy.TitleRemove";

        /// <summary>{0} = A, {1} = B.</summary>
        public const string TitleMerge = "GpoCopy.TitleMerge";

        /// <summary>{0} = the GPO.</summary>
        public const string TitleUnlinkAll = "GpoCopy.TitleUnlinkAll";

        // Nothing would change, refusals, "Zmiany ustawień"
        /// <summary>{0} = the GPO.</summary>
        public const string NothingCopied = "GpoCopy.NothingCopied";

        /// <summary>{0} = the GPO.</summary>
        public const string NothingRemoved = "GpoCopy.NothingRemoved";

        /// <summary>{0} = the setting, {1} = its GPO, {2} = the reason.</summary>
        public const string OutcomeLine = "GpoCopy.OutcomeLine";

        /// <summary>{0} = the GPO.</summary>
        public const string ChangedSinceAnalysis = "GpoCopy.ChangedSinceAnalysis";
        public const string NotConfigured = "GpoCopy.NotConfigured";
        public const string NoSettings = "GpoCopy.NoSettings";

        /// <summary><c>GpoCopy.Outcome.&lt;GpoUnitOutcomeKind&gt;</c>.</summary>
        public static string OutcomeKey(GpoUnitOutcomeKind kind) => "GpoCopy.Outcome." + kind;

        /// <summary>Every key of the dynamic family (the localization tests check both languages).</summary>
        public static IEnumerable<string> AllKeys() => Enum.GetValues<GpoUnitOutcomeKind>().Select(OutcomeKey);
    }
}
