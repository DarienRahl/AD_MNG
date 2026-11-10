using System.Globalization;
using System.Windows.Input;
using AdMng.Core.Directory;

namespace AdMng.Presentation.Feedback;

/// <summary>
/// What kind of thing a page says in its status line (track V, stage V2; design note docs/dev/v-interface-renewal.md 4.5):
/// the neutral summary of the page, or one of the four severities with a fixed title.
/// </summary>
public enum PageStatusKind
{
    /// <summary>Nothing happened: the neutral summary of the page ("2 314 objects, 12 selected"), possibly empty.</summary>
    Idle,

    /// <summary>An operation runs. Cannot be closed; it ends as one of the other kinds.</summary>
    Working,

    /// <summary>What happened, how many, and where the result is.</summary>
    Done,

    /// <summary>What was skipped, why, and what to do.</summary>
    Attention,

    /// <summary>What we tried: why it failed. What to do next.</summary>
    Error,
}

/// <summary>How far a running operation is: not known (indeterminate) or a fraction between 0 and 1.</summary>
public readonly record struct PageProgress
{
    private readonly bool _known;
    private readonly double _fraction;

    private PageProgress(double fraction)
    {
        _known = true;
        _fraction = fraction;
    }

    /// <summary>The operation runs and cannot say how far it is (also the default value).</summary>
    public static PageProgress Indeterminate => default;

    /// <summary>True when the operation cannot say how far it is.</summary>
    public bool IsIndeterminate => !_known;

    /// <summary>The fraction done, 0 to 1; null while <see cref="IsIndeterminate"/>.</summary>
    public double? Fraction => _known ? _fraction : null;

    /// <summary>A fraction (clamped to 0..1); a value that is not a number is indeterminate.</summary>
    public static PageProgress Of(double fraction) =>
        double.IsNaN(fraction) ? default : new PageProgress(Math.Clamp(fraction, 0d, 1d));

    /// <summary><paramref name="done"/> of <paramref name="total"/>; indeterminate while the total is not known (0 or less).</summary>
    public static PageProgress Of(long done, long total) => total <= 0 ? default : Of((double)done / total);

    public override string ToString() => _known ? _fraction.ToString("P0", CultureInfo.InvariantCulture) : "indeterminate";
}

/// <summary>
/// The one follow-up a status offers ("Retry", "Clear filter", "Show the report"): a label and the command behind it.
/// </summary>
/// <param name="Label">Localized text of the link, a verb.</param>
/// <param name="Command">What it runs.</param>
/// <param name="Parameter">The command parameter, if the command takes one.</param>
public sealed record PageStatusAction(string Label, ICommand Command, object? Parameter = null);

/// <summary>
/// A result code as it may be shown in parentheses at the end of an error text and written to the diagnostic log: a
/// number or a name the program itself produced (an LDAP result code, a Win32 error, an error kind, an exception type),
/// never a text that came from a server or from the directory. It can only be made by the factories below, so a caller
/// cannot put a directory value into it (CLAUDE.md rule 1).
/// </summary>
public readonly record struct ResultCode
{
    private readonly string? _text;

    private ResultCode(string text) => _text = text;

    /// <summary>No code (also the default value).</summary>
    public static ResultCode None => default;

    /// <summary>True when there is no code to show.</summary>
    public bool IsNone => string.IsNullOrEmpty(_text);

    /// <summary>An LDAP result code (RFC 4511): "LDAP 81".</summary>
    public static ResultCode Ldap(int code) => new("LDAP " + code.ToString(CultureInfo.InvariantCulture));

    /// <summary>A Win32 error or an HRESULT: "0x52D".</summary>
    public static ResultCode Win32(int code) => new("0x" + code.ToString("X", CultureInfo.InvariantCulture));

    /// <summary>A directory error kind: "ServerUnavailable".</summary>
    public static ResultCode Kind(DirectoryErrorKind kind) => new(kind.ToString());

    /// <summary>
    /// The code of an exception: the LDAP result code and the Win32 detail of a directory error (else its kind), the
    /// type name of any other exception; none for a cancellation. The message of the exception is never read.
    /// </summary>
    public static ResultCode Of(Exception? exception) => exception switch
    {
        null or OperationCanceledException => default,
        AggregateException { InnerExceptions.Count: 1 } aggregate => Of(aggregate.InnerExceptions[0]),
        DirectoryException { LdapResultCode: { } ldap, Win32Error: { } win32 } => new ResultCode($"{Ldap(ldap)}, {Win32(win32)}"),
        DirectoryException { LdapResultCode: { } ldap } => Ldap(ldap),
        DirectoryException { Win32Error: { } win32 } => Win32(win32),
        DirectoryException directory => Kind(directory.Kind),
        _ => new ResultCode(exception.GetType().Name),
    };

    public override string ToString() => _text ?? string.Empty;
}

/// <summary>
/// The copy of a status for the diagnostic log (design note 4.5, CLAUDE.md rule 1): the key of the message, the counts
/// it was filled with and a result code - never the text shown on screen, which may name objects. The type holds
/// nothing else, so a status cannot carry a directory value into a log.
/// </summary>
public sealed class PageStatusLog
{
    private readonly long[] _counts;

    /// <param name="key">The resource key of the message (a constant of <c>L</c>), never a formatted text.</param>
    /// <param name="counts">The numbers the message was filled with (objects read, skipped, failed…).</param>
    public PageStatusLog(string key, params long[] counts)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(counts);
        Key = key;
        _counts = [.. counts];
    }

    /// <summary>No log copy (the idle summary of a page is not logged).</summary>
    public static PageStatusLog None { get; } = new(string.Empty);

    /// <summary>The resource key of the message.</summary>
    public string Key { get; }

    /// <summary>The numbers the message was filled with.</summary>
    public IReadOnlyList<long> Counts => _counts;

    /// <summary>The result code of an error, or none.</summary>
    public ResultCode Code { get; private init; }

    /// <summary>The same copy with a result code.</summary>
    public PageStatusLog WithCode(ResultCode code) => new(Key, _counts) { Code = code };

    /// <summary>"List.Status.Stopped (150)", "List.Status.ReadFailed [LDAP 81]".</summary>
    public override string ToString()
    {
        var counts = _counts.Length == 0 ? string.Empty : $" ({CountsText})";
        var code = Code.IsNone ? string.Empty : $" [{Code}]";
        return Key + counts + code;
    }

    /// <summary>The counts as "150, 3" (empty without counts).</summary>
    internal string CountsText => string.Join(", ", _counts.Select(count => count.ToString(CultureInfo.InvariantCulture)));
}

/// <summary>
/// What a page says right now (track V, stage V2; design note 4.5 and 6.7): the neutral summary, or one of the four
/// severities - with the text for the screen (it may name objects), the progress of a running operation, at most one
/// follow-up action, whether the operator may close it, and the value-free copy for the diagnostic log. Immutable; a
/// page shows it through its <see cref="PageStatusHolder"/>.
/// </summary>
/// <remarks>
/// The fixed title of a severity ("Working", "Done", "Attention", "Error") is not part of the value: the view shows it
/// from <see cref="Kind"/> (<see cref="PageStatusText.Title"/>), so no page can give a severity another name.
/// </remarks>
public sealed class PageStatus
{
    private PageStatus(PageStatusKind kind, string message, PageProgress? progress, PageStatusAction? action, bool canClose, PageStatusLog log)
    {
        Kind = kind;
        Message = message;
        Progress = progress;
        Action = action;
        CanClose = canClose;
        Log = log;
    }

    /// <summary>The idle state without a summary.</summary>
    public static PageStatus Empty { get; } = new(PageStatusKind.Idle, string.Empty, null, null, false, PageStatusLog.None);

    public PageStatusKind Kind { get; }

    /// <summary>The text on screen. It may name objects - it is never logged (see <see cref="Log"/>).</summary>
    public string Message { get; }

    /// <summary>The progress of a running operation; null for every kind but <see cref="PageStatusKind.Working"/>.</summary>
    public PageProgress? Progress { get; }

    /// <summary>The one follow-up action, or null.</summary>
    public PageStatusAction? Action { get; }

    /// <summary>
    /// The operator may close the message (the page then shows its summary again). Never for a running operation and
    /// never for the summary itself.
    /// </summary>
    public bool CanClose { get; }

    /// <summary>The value-free copy for the diagnostic log: message key, counts, result code.</summary>
    public PageStatusLog Log { get; }

    public bool IsIdle => Kind == PageStatusKind.Idle;

    public bool IsWorking => Kind == PageStatusKind.Working;

    /// <summary>The neutral summary of a page ("2 314 objects, 12 selected"); null or blank gives <see cref="Empty"/>.</summary>
    public static PageStatus Idle(string? summary) =>
        string.IsNullOrWhiteSpace(summary) ? Empty : new PageStatus(PageStatusKind.Idle, summary.Trim(), null, null, false, PageStatusLog.None);

    /// <summary>An operation runs: the action and its scope ("Reading 2 314 accounts from Kraków…"). Cannot be closed.</summary>
    /// <param name="message">The text on screen.</param>
    /// <param name="log">The value-free copy.</param>
    /// <param name="progress">How far it is (indeterminate by default).</param>
    /// <param name="action">The follow-up, normally "Stop".</param>
    public static PageStatus Working(string message, PageStatusLog log, PageProgress progress = default, PageStatusAction? action = null) =>
        Create(PageStatusKind.Working, message, log, progress, action, canClose: false);

    /// <summary>What happened, how many, and where the result is. No "successfully".</summary>
    public static PageStatus Done(string message, PageStatusLog log, PageStatusAction? action = null) =>
        Create(PageStatusKind.Done, message, log, null, action, canClose: true);

    /// <summary>What was skipped, why, and what to do.</summary>
    /// <param name="message">The text on screen.</param>
    /// <param name="log">The value-free copy.</param>
    /// <param name="action">The follow-up, if there is one.</param>
    /// <param name="canClose">False for a state of the page that lasts (it goes when the state goes, not when it was read).</param>
    public static PageStatus Attention(string message, PageStatusLog log, PageStatusAction? action = null, bool canClose = true) =>
        Create(PageStatusKind.Attention, message, log, null, action, canClose);

    /// <summary>What we tried: why it failed. What to do next. Built with <see cref="PageStatusText.Failure"/>.</summary>
    /// <param name="message">The text on screen.</param>
    /// <param name="log">The value-free copy.</param>
    /// <param name="action">The follow-up, normally "Retry".</param>
    /// <param name="canClose">False for a state of the page that lasts (it goes when the state goes, not when it was read).</param>
    public static PageStatus Error(string message, PageStatusLog log, PageStatusAction? action = null, bool canClose = true) =>
        Create(PageStatusKind.Error, message, log, null, action, canClose);

    /// <summary>
    /// True when <paramref name="other"/> is the same message: the same kind and the same message key. A running
    /// operation that reports a new count is the same message; the view announces and animates only a new one. Two
    /// summaries are always the same message.
    /// </summary>
    public bool IsSameMessageAs(PageStatus? other) =>
        other is not null && Kind == other.Kind && string.Equals(Log.Key, other.Log.Key, StringComparison.Ordinal);

    /// <summary>A running operation with a new text, progress, log copy or action; what is not given stays.</summary>
    internal PageStatus Update(string? message, PageProgress? progress, PageStatusLog? log) =>
        new(Kind, message ?? Message, Progress is null ? null : progress ?? Progress, Action, CanClose, log ?? Log);

    /// <summary>The kind and the log copy - never the text on screen.</summary>
    public override string ToString() => IsIdle ? nameof(PageStatusKind.Idle) : $"{Kind}: {Log}";

    private static PageStatus Create(PageStatusKind kind, string message, PageStatusLog log, PageProgress? progress, PageStatusAction? action, bool canClose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(log);
        return new PageStatus(kind, message.Trim(), progress, action, canClose, log);
    }
}
