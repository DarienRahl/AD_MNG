using AdMng.Core.PowerShell;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.PowerShell;

/// <summary>What Enter did in the line editor.</summary>
public enum PowerShellEnterAction
{
    /// <summary>The input is complete: the caller runs it.</summary>
    Run = 0,

    /// <summary>The input is not complete (an open quote, a bracket, a trailing pipe): a line was inserted.</summary>
    NewLine = 1,

    /// <summary>The selected completion was put into the text.</summary>
    AcceptedCompletion = 2,

    /// <summary>The line found by the history search was put into the text.</summary>
    AcceptedSearch = 3,
}

/// <summary>What the editor is for right now.</summary>
public enum PowerShellInputMode
{
    /// <summary>A command: highlighted, completed, multi-line, with the history.</summary>
    Command = 0,

    /// <summary>The answer to a question of a running command (<c>Read-Host</c>, a confirmation): one plain line.</summary>
    Answer = 1,

    /// <summary>The answer to a question for a <see cref="System.Security.SecureString"/>: the view shows dots.</summary>
    Secret = 2,
}

/// <summary>
/// The state of the line editor of a PowerShell window (track R, decision D5), apart from its drawing: the text and the caret,
/// the highlight spans from the engine's parser, the completion list and its selection, the history with Up / Down and the
/// incremental search (Ctrl+R), and the decision "Enter runs, or inserts a line". The editor control in AdMng.App only forwards
/// keys and draws what is here, so everything else is tested without a window. Nothing in it is ever written anywhere.
/// </summary>
public sealed partial class PowerShellInputModel : ObservableObject
{
    /// <summary>The longest text that is highlighted on every change (a pasted script past it is shown plain).</summary>
    public const int MaxHighlightedLength = 20_000;

    /// <summary>
    /// The longest text that is highlighted at once, while the key is pressed (a command line is a few hundred characters, and the
    /// parser takes well under a millisecond for it). A longer text - a script that was pasted - is highlighted on a pool thread,
    /// and the next change stops that work (CLAUDE.md rule 6): the text is shown plain until the spans arrive.
    /// </summary>
    public const int SynchronousHighlightLength = 2_000;

    private readonly Func<IPowerShellSession?> _session;
    private readonly Func<bool>? _canComplete;
    private CancellationTokenSource? _completion;
    private CancellationTokenSource? _highlight;
    private int _replaceIndex;
    private int _replaceLength;
    private int _searchIndex;

    /// <param name="session">The window's session, or null while it has none.</param>
    /// <param name="history">The window's history.</param>
    /// <param name="canComplete">False while the window takes no completion (the trusted mode is being turned off); null: always.</param>
    public PowerShellInputModel(Func<IPowerShellSession?> session, PowerShellInputHistory history, Func<bool>? canComplete = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        History = history ?? throw new ArgumentNullException(nameof(history));
        _canComplete = canComplete;
    }

    public PowerShellInputHistory History { get; }

    /// <summary>What the operator has typed; lines are separated by <c>\n</c>.</summary>
    [ObservableProperty]
    public partial string Text { get; private set; } = string.Empty;

    /// <summary>The caret, 0 to <c>Text.Length</c>.</summary>
    [ObservableProperty]
    public partial int CaretIndex { get; private set; }

    /// <summary>The spans of <see cref="Text"/> to colour (empty while the editor takes an answer, or when the text is too long).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PowerShellSyntaxSpan> Spans { get; private set; } = [];

    /// <summary>A command or the answer to a question of the running command; an answer is never highlighted, completed or remembered.</summary>
    [ObservableProperty]
    public partial PowerShellInputMode Mode { get; private set; }

    /// <summary>A secret is being typed (a prompt for a <c>SecureString</c>): the view shows dots.</summary>
    public bool IsSecret => Mode == PowerShellInputMode.Secret;

    /// <summary>The editor takes the answer to a question of the running command (plain or secret).</summary>
    public bool IsAnswering => Mode != PowerShellInputMode.Command;

    /// <summary>The completion list is open.</summary>
    [ObservableProperty]
    public partial bool IsCompletionOpen { get; private set; }

    /// <summary>The matches of the open completion list.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PowerShellCompletionMatch> Completions { get; private set; } = [];

    /// <summary>The index of the selected match, or -1.</summary>
    [ObservableProperty]
    public partial int SelectedCompletionIndex { get; private set; } = -1;

    /// <summary>The incremental history search (Ctrl+R) is on.</summary>
    [ObservableProperty]
    public partial bool IsSearching { get; private set; }

    /// <summary>What the operator typed into the search.</summary>
    [ObservableProperty]
    public partial string SearchQuery { get; private set; } = string.Empty;

    /// <summary>The line the search found, or null.</summary>
    [ObservableProperty]
    public partial string? SearchMatch { get; private set; }

    /// <summary>True when the search found nothing for what was typed.</summary>
    public bool SearchFailed => IsSearching && SearchQuery.Length > 0 && SearchMatch is null;

    /// <summary>The text has more than one line.</summary>
    public bool IsMultiLine => Text.Contains('\n', StringComparison.Ordinal);

    /// <summary>The text is empty or blank.</summary>
    public bool IsBlank => string.IsNullOrWhiteSpace(Text);

    /// <summary>
    /// The operator edited the text in the editor (typing, pasting, deleting): the model takes it and the caret. An open completion
    /// list is closed - what was typed after it is what counts.
    /// </summary>
    public void SetText(string? text, int caretIndex)
    {
        text ??= string.Empty;
        if (text == Text)
        {
            CaretIndex = Math.Clamp(caretIndex, 0, text.Length);
            return;
        }

        CloseCompletion();
        Text = text;
        CaretIndex = Math.Clamp(caretIndex, 0, text.Length);
    }

    /// <summary>Inserts <paramref name="text"/> at the caret (paste, Shift+Enter).</summary>
    public void Insert(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var caret = Math.Clamp(CaretIndex, 0, Text.Length);
        SetText(Text[..caret] + text + Text[caret..], caret + text.Length);
    }

    /// <summary>Shift+Enter: a new line at the caret, whatever the input is.</summary>
    public void InsertNewLine() => Insert("\n");

    /// <summary>
    /// Enter. With the completion list open it takes the selected match, with the search on the line found; otherwise the engine
    /// says whether the input is complete: a complete one is to be run (<see cref="PowerShellEnterAction.Run"/>), an open
    /// quote, bracket or pipe gets a new line.
    /// </summary>
    public PowerShellEnterAction PressEnter()
    {
        if (IsCompletionOpen)
        {
            AcceptCompletion();
            return PowerShellEnterAction.AcceptedCompletion;
        }

        if (IsSearching)
        {
            AcceptSearch();
            return PowerShellEnterAction.AcceptedSearch;
        }

        if (!IsAnswering && _session() is { } session && !IsBlank && !session.IsInputComplete(Text))
        {
            InsertNewLine();
            return PowerShellEnterAction.NewLine;
        }

        return PowerShellEnterAction.Run;
    }

    /// <summary>Empties the editor after a run (or a prompt's answer): no text, no list, no search, no place in the history.</summary>
    public void Clear()
    {
        CloseCompletion();
        CancelSearch();
        History.ResetNavigation();
        SetTextCore(string.Empty, 0);
    }

    /// <summary>The editor takes the answer to a question of the command (plain or secret), or goes back to commands. The text starts empty.</summary>
    public void SetMode(PowerShellInputMode mode)
    {
        if (Mode == mode)
        {
            return;
        }

        CloseCompletion();
        CancelSearch();
        History.ResetNavigation();
        Mode = mode;
        OnPropertyChanged(nameof(IsSecret));
        OnPropertyChanged(nameof(IsAnswering));
        SetTextCore(string.Empty, 0);
    }

    // ---- Up and Down -------------------------------------------------------------------------------------------

    /// <summary>
    /// Up: the selection of an open list, else the line above in a multi-line input, else the previous line of the history.
    /// True when it did something.
    /// </summary>
    public bool MoveUp()
    {
        if (IsCompletionOpen)
        {
            SelectCompletion(-1);
            return true;
        }

        if (IsSearching)
        {
            SearchOlder();
            return true;
        }

        if (IsAnswering)
        {
            return false;
        }

        var (line, column) = LineAndColumn(Text, CaretIndex);
        if (line > 0)
        {
            CaretIndex = IndexOf(Text, line - 1, column);
            return true;
        }

        if (History.Previous(Text) is not { } previous)
        {
            return false;
        }

        SetTextCore(previous, previous.Length);
        return true;
    }

    /// <summary>Down: the selection of an open list, else the line below, else the next line of the history (then the draft).</summary>
    public bool MoveDown()
    {
        if (IsCompletionOpen)
        {
            SelectCompletion(1);
            return true;
        }

        if (IsAnswering)
        {
            return false;
        }

        var (line, column) = LineAndColumn(Text, CaretIndex);
        var lines = Text.Count(c => c == '\n') + 1;
        if (line < lines - 1)
        {
            CaretIndex = IndexOf(Text, line + 1, column);
            return true;
        }

        if (History.Next() is not { } next)
        {
            return false;
        }

        SetTextCore(next, next.Length);
        return true;
    }

    // ---- Completion --------------------------------------------------------------------------------------------

    /// <summary>
    /// Tab. With the list open it selects the next match. Otherwise it asks the engine to complete the text at the caret: one match
    /// is put in at once, several open the list (the first selected), none does nothing. A result for a text that has changed
    /// since is dropped.
    /// </summary>
    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (IsAnswering)
        {
            return;
        }

        if (IsCompletionOpen)
        {
            SelectCompletion(1);
            return;
        }

        if (_canComplete?.Invoke() == false || _session() is not { } session)
        {
            return;
        }

        var text = Text;
        var caret = CaretIndex;
        var previous = Interlocked.Exchange(ref _completion, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
        previous?.Cancel();
        var mine = _completion!;
        try
        {
            var result = await session.CompleteAsync(text, caret, mine.Token).ConfigureAwait(true);
            if (mine.IsCancellationRequested || Text != text || CaretIndex != caret || result.Matches.Count == 0)
            {
                return;
            }

            _replaceIndex = Math.Clamp(result.ReplacementIndex, 0, text.Length);
            _replaceLength = Math.Clamp(result.ReplacementLength, 0, text.Length - _replaceIndex);
            if (result.Matches.Count == 1)
            {
                Apply(result.Matches[0]);
                return;
            }

            Completions = result.Matches;
            SelectedCompletionIndex = 0;
            IsCompletionOpen = true;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later Tab, or the window closed: nothing to show.
        }
    }

    /// <summary>Shift+Tab: the previous match of an open list.</summary>
    public void CompletePrevious()
    {
        if (IsCompletionOpen)
        {
            SelectCompletion(-1);
        }
    }

    /// <summary>Takes the selected match of the open list into the text.</summary>
    public void AcceptCompletion()
    {
        if (IsCompletionOpen && SelectedCompletionIndex >= 0 && SelectedCompletionIndex < Completions.Count)
        {
            Apply(Completions[SelectedCompletionIndex]);
        }
        else
        {
            CloseCompletion();
        }
    }

    /// <summary>A click on a match of the open list: takes that match into the text. Nothing when the list is closed or the index is outside it.</summary>
    public void AcceptCompletionAt(int index)
    {
        if (IsCompletionOpen && index >= 0 && index < Completions.Count)
        {
            Apply(Completions[index]);
        }
    }

    /// <summary>Escape: closes the list; the text stays as it is.</summary>
    public void CloseCompletion()
    {
        var pending = Interlocked.Exchange(ref _completion, null);
        pending?.Cancel();
        if (IsCompletionOpen)
        {
            IsCompletionOpen = false;
            Completions = [];
            SelectedCompletionIndex = -1;
        }
    }

    /// <summary>
    /// The window's session is about to change its runspace (the trusted mode is turned off): the completion list closes and a request
    /// on its way is dropped, and a highlight that runs on a pool thread is stopped. <see cref="Highlight"/> colours the text again after.
    /// </summary>
    public void CancelPendingWork()
    {
        CloseCompletion();
        Interlocked.Exchange(ref _highlight, null)?.Cancel();
    }

    /// <summary>Highlights the text again, as a change of it would (after <see cref="CancelPendingWork"/>).</summary>
    public void Highlight() => OnTextChanged(Text);

    // ---- Search of the history (Ctrl+R) -------------------------------------------------------------------------

    /// <summary>Ctrl+R: starts the incremental search; pressed again it goes to the next older match.</summary>
    public void BeginSearch()
    {
        if (IsAnswering)
        {
            return;
        }

        if (IsSearching)
        {
            SearchOlder();
            return;
        }

        CloseCompletion();
        IsSearching = true;
        SearchQuery = string.Empty;
        SearchMatch = null;
        _searchIndex = History.Entries.Count;
        OnPropertyChanged(nameof(SearchFailed));
    }

    /// <summary>What the operator typed into the search: the newest line that contains it is the match.</summary>
    public void SetSearchQuery(string? query)
    {
        if (!IsSearching)
        {
            return;
        }

        SearchQuery = query ?? string.Empty;
        _searchIndex = History.Entries.Count;
        Find();
    }

    /// <summary>The next older line that contains the query; the match stays when there is none.</summary>
    public void SearchOlder()
    {
        if (IsSearching)
        {
            Find();
        }
    }

    /// <summary>Takes the match into the text (the caret at its end) and ends the search. Without a match it only ends it.</summary>
    public void AcceptSearch()
    {
        var match = SearchMatch;
        CancelSearch();
        if (match is not null)
        {
            History.ResetNavigation();
            SetTextCore(match, match.Length);
        }
    }

    /// <summary>Escape: ends the search; the text is as it was.</summary>
    public void CancelSearch()
    {
        if (!IsSearching)
        {
            return;
        }

        IsSearching = false;
        SearchQuery = string.Empty;
        SearchMatch = null;
        OnPropertyChanged(nameof(SearchFailed));
    }

    // ---- Internals ---------------------------------------------------------------------------------------------

    partial void OnTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsMultiLine));
        OnPropertyChanged(nameof(IsBlank));
        Interlocked.Exchange(ref _highlight, null)?.Cancel();
        if (IsAnswering || value.Length is 0 or > MaxHighlightedLength || _session() is not { } session)
        {
            Spans = [];
            HighlightTask = Task.CompletedTask;
            return;
        }

        if (value.Length <= SynchronousHighlightLength)
        {
            Spans = session.Tokenize(value);
            HighlightTask = Task.CompletedTask;
            return;
        }

        Spans = [];
        var mine = new CancellationTokenSource();
        _highlight = mine;
        HighlightTask = HighlightAsync(session, value, mine.Token);
    }

    /// <summary>The highlighting of the last change: completed at once for a command line, running for a long text. (Tests.)</summary>
    internal Task HighlightTask { get; private set; } = Task.CompletedTask;

    private async Task HighlightAsync(IPowerShellSession session, string text, CancellationToken cancellationToken)
    {
        try
        {
            var spans = await Task.Run(() => session.Tokenize(text), cancellationToken).ConfigureAwait(true);
            if (!cancellationToken.IsCancellationRequested && Text == text)
            {
                Spans = spans;
            }
        }
        catch (OperationCanceledException)
        {
            // The text changed again: its own highlighting is on the way.
        }
    }

    private void Find()
    {
        var index = History.FindOlder(SearchQuery, _searchIndex);
        if (index >= 0)
        {
            _searchIndex = index;
            SearchMatch = History.Entries[index];
        }
        else if (_searchIndex >= History.Entries.Count)
        {
            // Nothing for this query at all (a later "older" that finds nothing keeps the last match).
            SearchMatch = null;
        }

        OnPropertyChanged(nameof(SearchFailed));
    }

    private void SelectCompletion(int step)
    {
        if (Completions.Count == 0)
        {
            return;
        }

        SelectedCompletionIndex = (SelectedCompletionIndex + step + Completions.Count) % Completions.Count;
    }

    private void Apply(PowerShellCompletionMatch match)
    {
        var text = Text[.._replaceIndex] + match.Text + Text[(_replaceIndex + _replaceLength)..];
        var caret = _replaceIndex + match.Text.Length;
        CloseCompletion();
        SetTextCore(text, caret);
    }

    /// <summary>Sets the text the model itself chose (a recalled line, a completion, nothing): not an edit of the operator's.</summary>
    private void SetTextCore(string text, int caret)
    {
        Text = text;
        CaretIndex = Math.Clamp(caret, 0, text.Length);
    }

    private static (int Line, int Column) LineAndColumn(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var line = 0;
        var lineStart = 0;
        for (var index = 0; index < caret; index++)
        {
            if (text[index] == '\n')
            {
                line++;
                lineStart = index + 1;
            }
        }

        return (line, caret - lineStart);
    }

    /// <summary>The index of <paramref name="column"/> on <paramref name="line"/>, or the end of that line when it is shorter.</summary>
    private static int IndexOf(string text, int line, int column)
    {
        var start = 0;
        for (var current = 0; current < line; current++)
        {
            start = text.IndexOf('\n', start) + 1;
        }

        var end = text.IndexOf('\n', start);
        end = end < 0 ? text.Length : end;
        return Math.Min(start + column, end);
    }
}
