namespace AdMng.Presentation.Lists;

/// <summary>Back/forward history of one document (browser semantics, bounded).</summary>
public sealed class NavigationHistory<T>
    where T : class
{
    private readonly LinkedList<T> _back = new();
    private readonly Stack<T> _forward = new();
    private readonly int _capacity;

    public NavigationHistory(int capacity = 50)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public T? Current { get; private set; }

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    /// <summary>Goes to <paramref name="location"/>; the current entry moves to the back stack and forward history is cleared.</summary>
    public void Navigate(T location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (Current is not null && !Equals(Current, location))
        {
            _back.AddLast(Current);
            if (_back.Count > _capacity)
            {
                _back.RemoveFirst();
            }
        }

        _forward.Clear();
        Current = location;
    }

    /// <summary>Replaces the current entry without touching back/forward (e.g. refined location).</summary>
    public void ReplaceCurrent(T location) => Current = location ?? throw new ArgumentNullException(nameof(location));

    public T? GoBack()
    {
        if (_back.Last is not { } last)
        {
            return null;
        }

        _back.RemoveLast();
        if (Current is not null)
        {
            _forward.Push(Current);
        }

        Current = last.Value;
        return Current;
    }

    public T? GoForward()
    {
        if (_forward.Count == 0)
        {
            return null;
        }

        if (Current is not null)
        {
            _back.AddLast(Current);
        }

        Current = _forward.Pop();
        return Current;
    }
}
