using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Feedback;

/// <summary>
/// The wording of status messages in one place (track V, stage V2; design note 4.5): the fixed titles of the four
/// severities, counts in the plural form of the language, the one shape of an error text and the name a screen reader
/// hears.
/// </summary>
public static class PageStatusText
{
    private const string SentenceEnds = ".!?…";

    /// <summary>The resource key of the fixed title of a severity; empty for the summary, which has no title.</summary>
    public static string TitleKey(PageStatusKind kind) => kind switch
    {
        PageStatusKind.Working => L.Feedback.TitleWorking,
        PageStatusKind.Done => L.Feedback.TitleDone,
        PageStatusKind.Attention => L.Feedback.TitleAttention,
        PageStatusKind.Error => L.Feedback.TitleError,
        _ => string.Empty,
    };

    /// <summary>The fixed title of a severity ("Working", "Done", "Attention", "Error"); empty for the summary.</summary>
    public static string Title(ILocalizer localizer, PageStatusKind kind)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return TitleKey(kind) is { Length: > 0 } key ? localizer[key] : string.Empty;
    }

    /// <summary>
    /// A count with its noun in the plural form of the language: "1 konto", "2 konta", "5 kont". The three keys hold the
    /// forms for one, for a few (2-4, 22-24… but not 12-14) and for many; English uses the first and repeats the second.
    /// The same rule as everywhere else in the program (<see cref="CorrespondenceText"/>).
    /// </summary>
    public static string Count(ILocalizer localizer, int count, string one, string few, string many) =>
        CorrespondenceText.Plural(localizer, count, one, few, many);

    /// <summary>"1 object", "2 314 objects" - "1 obiekt", "2 obiekty", "5 obiektów".</summary>
    public static string Objects(ILocalizer localizer, int count) =>
        Count(localizer, count, L.Feedback.ObjectsOne, L.Feedback.ObjectsFew, L.Feedback.ObjectsMany);

    /// <summary>
    /// The one shape of an error text: "what we tried: why it failed. What to do next. (code)". Also the shape of an
    /// "attention" text - what was skipped: why. What to do. Every part is a localized text; the result code goes in
    /// parentheses at the end and never stands alone.
    /// </summary>
    /// <param name="tried">What the program tried, without a full stop ("Could not read “Kraków”").</param>
    /// <param name="reason">Why it failed, a sentence ("The domain controller is not available.").</param>
    /// <param name="next">What the operator can do, a sentence ("Press Retry to try again."), or null.</param>
    /// <param name="code">The result code, if there is one.</param>
    public static string Failure(string tried, string? reason, string? next = null, ResultCode code = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tried);
        var lead = tried.Trim().TrimEnd(':', '.', ' ');
        var why = Sentence(reason);
        var text = why.Length == 0 ? Sentence(lead) : $"{lead}: {why}";
        if (Sentence(next) is { Length: > 0 } advice)
        {
            text = $"{text} {advice}";
        }

        return code.IsNone ? text : $"{text} ({code})";
    }

    /// <summary>
    /// What a screen reader hears for a status: "Error: Could not read…" - the fixed title, then the text; the summary
    /// alone for the idle state; "Status" for an empty line, so the region always has a name.
    /// </summary>
    public static string Name(ILocalizer localizer, PageStatus status)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(status);
        var title = Title(localizer, status.Kind);
        if (title.Length == 0)
        {
            return status.Message.Length == 0 ? localizer[L.Feedback.StatusName] : status.Message;
        }

        return status.Message.Length == 0 ? title : $"{title}: {status.Message}";
    }

    /// <summary>The text as a sentence: trimmed, with a full stop when it has no end of its own; empty for null or blank.</summary>
    private static string Sentence(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return SentenceEnds.Contains(trimmed[^1], StringComparison.Ordinal) ? trimmed : trimmed + ".";
    }
}
