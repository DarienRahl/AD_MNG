namespace AdMng.Presentation.PowerShell;

/// <summary>
/// The command history of one PowerShell window (track R, decision D5): in the memory of the window and nowhere else - there
/// is no PSReadLine and no history file, and the list is gone when the window closes. A line that carries a plain-text
/// password is never remembered (the engine decides, <see cref="Core.PowerShell.IPowerShellSession.ShouldRemember"/>: the
/// filter PSReadLine applies), and a line that repeats the one before is not remembered twice.
/// </summary>
public sealed class PowerShellInputHistory
{
    /// <summary>How many lines the history keeps; the oldest are dropped first.</summary>
    public const int Capacity = 500;

    private readonly List<string> _entries = [];
    private readonly Func<string, bool> _shouldRemember;

    // Up / Down: -1 = not looking at the history (the text of the editor is the draft), else the index of the line shown.
    private int _position = -1;
    private string _draft = string.Empty;

    /// <param name="shouldRemember">False for a line that must not enter the history (a plain-text password).</param>
    public PowerShellInputHistory(Func<string, bool> shouldRemember)
    {
        _shouldRemember = shouldRemember ?? throw new ArgumentNullException(nameof(shouldRemember));
    }

    /// <summary>The remembered lines, oldest first.</summary>
    public IReadOnlyList<string> Entries => _entries;

    /// <summary>True while Up / Down show a line of the history instead of what the operator was typing.</summary>
    public bool IsNavigating => _position >= 0;

    /// <summary>
    /// Remembers <paramref name="line"/> (trailing blank space removed) and ends the navigation. False for an empty line, one
    /// the policy refuses and one equal to the last remembered line.
    /// </summary>
    public bool Add(string line)
    {
        ResetNavigation();
        var text = (line ?? string.Empty).TrimEnd();
        if (text.Length == 0 || !_shouldRemember(text))
        {
            return false;
        }

        if (_entries.Count > 0 && string.Equals(_entries[^1], text, StringComparison.Ordinal))
        {
            return false;
        }

        _entries.Add(text);
        if (_entries.Count > Capacity)
        {
            _entries.RemoveAt(0);
        }

        return true;
    }

    /// <summary>
    /// Up: the line before the one shown. The first call keeps <paramref name="current"/> as the draft that Down comes back to.
    /// Null when there is no older line (the oldest stays shown).
    /// </summary>
    public string? Previous(string current)
    {
        if (_entries.Count == 0)
        {
            return null;
        }

        if (_position < 0)
        {
            _draft = current ?? string.Empty;
            _position = _entries.Count;
        }

        if (_position == 0)
        {
            return null;
        }

        _position--;
        return _entries[_position];
    }

    /// <summary>Down: the line after the one shown, then the draft. Null when the history is not being navigated.</summary>
    public string? Next()
    {
        if (_position < 0)
        {
            return null;
        }

        _position++;
        if (_position >= _entries.Count)
        {
            _position = -1;
            var draft = _draft;
            _draft = string.Empty;
            return draft;
        }

        return _entries[_position];
    }

    /// <summary>Forgets where Up / Down were (the operator typed something, or ran the line).</summary>
    public void ResetNavigation()
    {
        _position = -1;
        _draft = string.Empty;
    }

    /// <summary>
    /// The index of the newest line before <paramref name="before"/> that contains <paramref name="query"/> (ignoring case) -
    /// the step of the incremental search (Ctrl+R); -1 when there is none. An empty query matches nothing.
    /// </summary>
    public int FindOlder(string query, int before)
    {
        if (string.IsNullOrEmpty(query))
        {
            return -1;
        }

        for (var index = Math.Min(before, _entries.Count) - 1; index >= 0; index--)
        {
            if (_entries[index].Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }
}
