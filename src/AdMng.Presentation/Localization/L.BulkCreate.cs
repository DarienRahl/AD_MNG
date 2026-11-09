namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>
    /// The bulk creation wizard <em>Zakładanie wielu kont</em> (docs/dev/phase3-bulk-create.md 2.6, 2.9). The contract keys
    /// (command, sub-steps, the host's header and footer) are the lead's; B3 adds the keys of steps 1–3 below them. The
    /// dynamic key families are in <see cref="global::AdMng.Presentation.BulkCreate.BulkCreateTextKeys"/>.
    /// </summary>
    public static class BulkCreate
    {
        // ---- Contract (lead) ----

        /// <summary><em>Wiele kont…</em> (palette <c>new.bulkCreate</c>, the <em>Nowy</em> menus).</summary>
        public const string Command = "BulkCreate.Command";

        /// <summary>The window title with the container: <c>{0}</c> the kind's title, <c>{1}</c> the container's name.</summary>
        public const string TitleInContainer = "BulkCreate.TitleInContainer";

        public const string StepSource = "BulkCreate.StepSource";
        public const string StepColumns = "BulkCreate.StepColumns";
        public const string StepRules = "BulkCreate.StepRules";
        public const string StepCheck = "BulkCreate.StepCheck";

        /// <summary><em>Rodzaj obiektów</em> (the kind combo).</summary>
        public const string Kind = "BulkCreate.Kind";

        /// <summary><em>Kontener docelowy</em>.</summary>
        public const string Container = "BulkCreate.Container";

        /// <summary>No container chosen yet.</summary>
        public const string NoContainer = "BulkCreate.NoContainer";

        /// <summary>Asks before a kind change replaces changed naming rules with the kind's default profile.</summary>
        public const string ConfirmKindChange = "BulkCreate.ConfirmKindChange";

        /// <summary>The did-you-mean of a pattern problem: <c>{0}</c> the suggestion (<c>{givenName}</c>).</summary>
        public const string PatternSuggestion = "BulkCreate.PatternSuggestion";

        // ---- B3 (steps 1–3, profiles) ----
    }
}
