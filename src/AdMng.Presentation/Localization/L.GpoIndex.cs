using AdMng.Core.GroupPolicy.Search;

namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>
    /// The per-window settings index of K7 (docs/dev/k-computers-and-gpo.md 21.3, 21.8): its build stages, the read status
    /// of a GPO and the notes the search and the cleanup advisor show about it. Stages and statuses are dynamic families
    /// (<see cref="StageKey"/>, <see cref="StatusKey"/>, <see cref="AllKeys"/>).
    /// </summary>
    public static class GpoIndex
    {
        /// <summary>{0} = the stage text, {1} = done, {2} = total ("Odczyt folderów GPO: 12 z 480").</summary>
        public const string Progress = "GpoIndex.Progress";

        /// <summary>{0} = GPOs seen ("Odczytano ustawienia {0} obiektów GPO").</summary>
        public const string Summary = "GpoIndex.Summary";

        /// <summary>{0} = GPOs whose folder was not read.</summary>
        public const string NoteNotRead = "GpoIndex.Note.NotRead";

        /// <summary>{0} = GPOs read only in part ("nie wszystkie ustawienia odczytane").</summary>
        public const string NoteIncomplete = "GpoIndex.Note.Incomplete";

        /// <summary>{0} = the reason SYSVOL could not be opened.</summary>
        public const string NoteNoSysvol = "GpoIndex.Note.NoSysvol";

        public const string NoteNoDefinitions = "GpoIndex.Note.NoDefinitions";

        public const string NoteDefinitionsFailed = "GpoIndex.Note.DefinitionsFailed";

        /// <summary>{0} = the other domains of the forest (their links to these GPOs are not visible).</summary>
        public const string NoteOtherDomains = "GpoIndex.Note.OtherDomains";

        public const string NoteStale = "GpoIndex.Note.Stale";

        /// <summary><c>GpoIndex.Stage.&lt;GpoIndexStage&gt;</c>.</summary>
        public static string StageKey(GpoIndexStage stage) => "GpoIndex.Stage." + stage;

        /// <summary><c>GpoIndex.Status.&lt;GpoIndexStatus&gt;</c>.</summary>
        public static string StatusKey(GpoIndexStatus status) => "GpoIndex.Status." + status;

        /// <summary>Every key of the family (the localization tests check both languages).</summary>
        public static IEnumerable<string> AllKeys() =>
        [
            Progress, Summary, NoteNotRead, NoteIncomplete, NoteNoSysvol, NoteNoDefinitions, NoteDefinitionsFailed, NoteOtherDomains, NoteStale,
            .. Enum.GetValues<GpoIndexStage>().Select(StageKey),
            .. Enum.GetValues<GpoIndexStatus>().Select(StatusKey),
        ];
    }
}
