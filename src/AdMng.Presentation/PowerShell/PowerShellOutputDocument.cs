using AdMng.Core.PowerShell;
using AdMng.Presentation.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.PowerShell;

/// <summary>What a line of the window's scrollback is, so that the view can choose its colour and glyph.</summary>
public enum PowerShellLineKind
{
    /// <summary>The command the operator ran, echoed with the prompt.</summary>
    Command = 0,

    /// <summary>Text of the output stream: the engine's formatted objects, <c>Write-Host</c>.</summary>
    Output = 1,

    /// <summary>The error stream (the status line's error glyph and colour).</summary>
    Error = 2,

    /// <summary>The warning stream.</summary>
    Warning = 3,

    /// <summary>The verbose stream.</summary>
    Verbose = 4,

    /// <summary>The debug stream.</summary>
    Debug = 5,

    /// <summary>A line of the window itself: what the window is, what a switch did.</summary>
    Information = 6,

    /// <summary>A question the running command asks (<c>Read-Host</c>, a confirmation) and the answer to it.</summary>
    Prompt = 7,

    /// <summary>The note about what the policies of the workstation record (the status line's attention glyph).</summary>
    PolicyNote = 8,
}

/// <summary>A piece of a line: its text, the colours <c>Write-Host</c> asked for and, for a distinguished name, the name (a link).</summary>
public readonly record struct PowerShellTextRun(string Text, ConsoleColor? Foreground = null, ConsoleColor? Background = null, string? DistinguishedName = null)
{
    /// <summary>The run is a link: <i>Open in the program</i>.</summary>
    public bool IsLink => DistinguishedName is not null;
}

/// <summary>
/// One line of the scrollback: a kind and its runs. A line that has no end yet (<see cref="IsOpen"/>) grows with the text that follows:
/// a piece of the same colours joins the last run, and the line tells it once per batch of the document, not once per piece (audit W-04).
/// </summary>
public sealed class PowerShellOutputLine : ObservableObject
{
    private IReadOnlyList<PowerShellTextRun> _runs;
    private string _text;

    // While the line grows: its runs and its text, published as Runs and Text at the end of the document's batch.
    private List<PowerShellTextRun>? _growing;
    private System.Text.StringBuilder? _builder;
    private bool _changed;

    internal PowerShellOutputLine(PowerShellLineKind kind, IReadOnlyList<PowerShellTextRun> runs, bool isOpen)
    {
        Kind = kind;
        _runs = runs;
        _text = Join(runs);
        IsOpen = isOpen;
    }

    public PowerShellLineKind Kind { get; }

    /// <summary>The pieces in order; replaced (<see cref="System.ComponentModel.INotifyPropertyChanged"/>) once per batch while an open line grows.</summary>
    public IReadOnlyList<PowerShellTextRun> Runs => _runs;

    /// <summary>The whole text of the line, without its line end.</summary>
    public string Text => _text;

    /// <summary>True until the text that ends the line arrived.</summary>
    public bool IsOpen { get; internal set; }

    /// <summary>The characters of the line, also those not published yet.</summary>
    internal int Length => _builder?.Length ?? _text.Length;

    /// <summary>Adds pieces at the end; a piece of the same colours as the last run, neither of them a link, joins it.</summary>
    internal void Append(IReadOnlyList<PowerShellTextRun> runs)
    {
        if (runs.Count == 0)
        {
            return;
        }

        if (_growing is null)
        {
            _growing = [.. _runs.Where(run => run.Text.Length > 0 || run.IsLink)];
            _builder = new System.Text.StringBuilder(_text, Math.Max(16, _text.Length * 2));
        }

        foreach (var run in runs)
        {
            if (_growing.Count > 0 && _growing[^1] is { IsLink: false } last && !run.IsLink
                && last.Foreground == run.Foreground && last.Background == run.Background)
            {
                _growing[^1] = last with { Text = last.Text + run.Text };
            }
            else
            {
                _growing.Add(run);
            }

            _builder!.Append(run.Text);
        }

        _changed = true;
    }

    /// <summary>Tells what grew since the last batch (one change of <see cref="Runs"/> and <see cref="Text"/>); nothing when nothing did.</summary>
    internal void Publish()
    {
        if (_growing is null)
        {
            return;
        }

        var changed = _changed;
        if (changed)
        {
            _runs = _growing.Count == 0 ? [new PowerShellTextRun(string.Empty)] : [.. _growing];
            _text = _builder!.ToString();
            _changed = false;
        }

        if (!IsOpen)
        {
            // An ended line grows no more: what it held for growing is not kept.
            _growing = null;
            _builder = null;
        }

        if (changed)
        {
            OnPropertyChanged(nameof(Runs));
            OnPropertyChanged(nameof(Text));
        }
    }

    /// <summary>Kind and length only: a line may hold directory values.</summary>
    public override string ToString() => $"PowerShellOutputLine({Kind}, {Text.Length} characters)";

    private static string Join(IReadOnlyList<PowerShellTextRun> runs) => runs.Count == 1 ? runs[0].Text : string.Concat(runs.Select(run => run.Text));
}

/// <summary>
/// The scrollback of a PowerShell window (track R, decisions D6 and D8): the lines the operator sees, in the memory of the
/// window and nowhere else. It is bounded - past <see cref="MaxLines"/> the oldest lines are dropped, in steps, and
/// <see cref="DroppedLines"/> says how many - so that a result of 100,000 objects never freezes the window. A distinguished name in
/// the text of a result is a link run (<see cref="PowerShellDnLinkFinder"/>). The document touches the UI collection only
/// through batches (<see cref="Add"/>): one change for any number of items.
/// </summary>
public sealed class PowerShellOutputDocument : ObservableObject
{
    /// <summary>The lines the window keeps.</summary>
    public const int MaxLines = 20_000;

    /// <summary>
    /// The longest line: a longer text goes on in a new line (audit W-04: a line without an end grew without a limit, and the cap of
    /// <see cref="MaxLines"/> bounded nothing). As long as the longest text the link finder looks at.
    /// </summary>
    public const int MaxLineLength = PowerShellDnLinkFinder.MaxTextLength;

    /// <summary>
    /// The lines the document may hold past <see cref="MaxLines"/> before it drops the oldest down to <see cref="MaxLines"/>, so that
    /// a window that is fed line by line does not copy its list for every line.
    /// </summary>
    public const int Slack = 2_000;

    private readonly BulkObservableCollection<PowerShellOutputLine> _lines = [];
    private PowerShellOutputLine? _open;
    private PowerShellStream _openStream;

    // The lines that grew in this batch: each tells it once, when the batch is committed.
    private readonly List<PowerShellOutputLine> _grown = [];
    private long _dropped;
    private long _written;

    /// <summary>
    /// The width the engine formats the running command with (the window sets it with the session's): a distinguished name that may be
    /// wrapped at it is no link (<see cref="PowerShellDnLinkFinder.Find(string?, int)"/>, audit W-06). 0 when unknown.
    /// </summary>
    public int Width { get; set; }

    /// <summary>The lines, oldest first (a list the view binds to).</summary>
    public BulkObservableCollection<PowerShellOutputLine> Lines => _lines;

    /// <summary>How many lines were dropped since the window was opened or last cleared.</summary>
    public long DroppedLines
    {
        get => _dropped;
        private set => SetProperty(ref _dropped, value);
    }

    /// <summary>How many lines of output (not the echo of a command, not the window's own lines) were written; the status line says it after a run.</summary>
    public long OutputLinesWritten => _written;

    /// <summary>Adds the items of one batch (text of the streams) with one change of the list.</summary>
    public void Add(IEnumerable<PowerShellTextItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var added = new List<PowerShellOutputLine>();
        foreach (var item in items)
        {
            AddText(item, added);
        }

        Commit(added);
    }

    /// <summary>Adds one line of the window's own (<see cref="PowerShellLineKind.Information"/>, <see cref="PowerShellLineKind.PolicyNote"/>, <see cref="PowerShellLineKind.Prompt"/>).</summary>
    public void AddLine(PowerShellLineKind kind, string text)
    {
        var added = new List<PowerShellOutputLine>();
        CloseOpenLine();
        foreach (var piece in SplitLines(text))
        {
            added.Add(new PowerShellOutputLine(kind, [new PowerShellTextRun(piece)], isOpen: false));
        }

        Commit(added);
    }

    /// <summary>The echo of a command: the first line after the prompt, the others after the continuation prompt.</summary>
    public void AddCommand(string script, string prompt, string continuation)
    {
        var added = new List<PowerShellOutputLine>();
        CloseOpenLine();
        var first = true;
        foreach (var piece in SplitLines(script))
        {
            added.Add(new PowerShellOutputLine(PowerShellLineKind.Command, [new PowerShellTextRun((first ? prompt : continuation) + piece)], isOpen: false));
            first = false;
        }

        Commit(added);
    }

    /// <summary>The command ended: a line the output left open (no line end at the very end) ends here, so the next text starts on a new line.</summary>
    public void EndRun() => CloseOpenLine();

    /// <summary>Clears the scrollback (Ctrl+L). The count of dropped lines starts again; the history of commands is not the document's.</summary>
    public void Clear()
    {
        _open = null;
        _grown.Clear();
        _lines.Clear();
        DroppedLines = 0;
    }

    private void AddText(PowerShellTextItem item, List<PowerShellOutputLine> added)
    {
        var kind = KindOf(item.Stream);
        var text = item.Text;
        var start = 0;
        while (start <= text.Length)
        {
            var newline = text.IndexOf('\n', start);
            var end = newline < 0 ? text.Length : newline;
            var piece = text[start..end].TrimEnd('\r');
            var endsLine = newline >= 0;
            if (piece.Length > 0 || endsLine)
            {
                AppendBounded(kind, item.Stream, piece, item.Foreground, item.Background, endsLine, added);
            }

            if (!endsLine)
            {
                break;
            }

            start = newline + 1;
        }
    }

    /// <summary>
    /// Puts a piece of text in the lines of its stream, none longer than <see cref="MaxLineLength"/>: what does not fit in the open line
    /// ends it and goes on in a new one (a cut never splits a character of two halves).
    /// </summary>
    private void AppendBounded(PowerShellLineKind kind, PowerShellStream stream, string piece, ConsoleColor? foreground, ConsoleColor? background, bool endsLine, List<PowerShellOutputLine> added)
    {
        while (true)
        {
            var used = _open is { } open && _openStream == stream && open.Kind == kind ? open.Length : 0;
            var room = MaxLineLength - used;
            if (piece.Length <= room)
            {
                Append(kind, stream, piece, foreground, background, endsLine, added);
                return;
            }

            var take = room;
            if (take > 0 && char.IsHighSurrogate(piece[take - 1]))
            {
                take--;
            }

            if (take <= 0 && used == 0)
            {
                // A line of one character of two halves at the very end: it stays whole.
                take = Math.Min(piece.Length, 2);
            }

            Append(kind, stream, piece[..take], foreground, background, endsLine: true, added);
            piece = piece[take..];
        }
    }

    /// <summary>Puts a piece of text in the open line of the same stream, or starts a line.</summary>
    private void Append(PowerShellLineKind kind, PowerShellStream stream, string piece, ConsoleColor? foreground, ConsoleColor? background, bool endsLine, List<PowerShellOutputLine> added)
    {
        var runs = RunsOf(piece, foreground, background, Width);
        if (_open is { } open && _openStream == stream && open.Kind == kind)
        {
            if (runs.Count > 0)
            {
                open.Append(runs);
                if (!_grown.Contains(open))
                {
                    _grown.Add(open);
                }
            }

            if (endsLine)
            {
                open.IsOpen = false;
                _open = null;
                _written++;
            }

            return;
        }

        // Another stream, or no open line: the open line ends where it is.
        CloseOpenLine();
        var line = new PowerShellOutputLine(kind, runs.Count == 0 ? [new PowerShellTextRun(string.Empty)] : runs, isOpen: !endsLine);
        added.Add(line);
        if (endsLine)
        {
            _written++;
        }
        else
        {
            _open = line;
            _openStream = stream;
        }
    }

    private void CloseOpenLine()
    {
        if (_open is { } open)
        {
            open.IsOpen = false;
            _written++;
            _open = null;

            // It may have grown in a batch not committed yet (EndRun): it tells it now and lets go of what it held for growing.
            _grown.Remove(open);
            open.Publish();
        }
    }

    private static List<PowerShellTextRun> RunsOf(string piece, ConsoleColor? foreground, ConsoleColor? background, int width)
    {
        var runs = new List<PowerShellTextRun>();
        if (piece.Length == 0)
        {
            return runs;
        }

        var position = 0;
        foreach (var span in PowerShellDnLinkFinder.Find(piece, width))
        {
            if (span.Start > position)
            {
                runs.Add(new PowerShellTextRun(piece[position..span.Start], foreground, background));
            }

            runs.Add(new PowerShellTextRun(span.DistinguishedName, foreground, background, span.DistinguishedName));
            position = span.Start + span.Length;
        }

        if (position < piece.Length)
        {
            runs.Add(new PowerShellTextRun(piece[position..], foreground, background));
        }

        return runs;
    }

    private void Commit(List<PowerShellOutputLine> added)
    {
        // Each line that grew in this batch tells it once (audit W-04: one change per piece made the view rebuild the line per piece).
        foreach (var line in _grown)
        {
            line.Publish();
        }

        _grown.Clear();
        if (added.Count == 1)
        {
            // One line: the ordinary notification, which keeps a scrolled list where it is; more: one Reset for the lot.
            _lines.Add(added[0]);
        }
        else if (added.Count > 1)
        {
            _lines.AddRange(added);
        }

        if (_lines.Count > MaxLines + Slack)
        {
            var drop = _lines.Count - MaxLines;
            _lines.ReplaceAll(_lines.Skip(drop).ToList());
            DroppedLines += drop;
        }
    }

    private static IEnumerable<string> SplitLines(string text) =>
        (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static PowerShellLineKind KindOf(PowerShellStream stream) => stream switch
    {
        PowerShellStream.Error => PowerShellLineKind.Error,
        PowerShellStream.Warning => PowerShellLineKind.Warning,
        PowerShellStream.Verbose => PowerShellLineKind.Verbose,
        PowerShellStream.Debug => PowerShellLineKind.Debug,
        _ => PowerShellLineKind.Output,
    };
}
