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
}
