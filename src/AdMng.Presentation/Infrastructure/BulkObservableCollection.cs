using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AdMng.Presentation.Infrastructure;

/// <summary>
/// <see cref="ObservableCollection{T}"/> with batch operations that raise ONE
/// <see cref="NotifyCollectionChangedAction.Reset"/> notification per batch instead of one per item.
/// WPF collection views do not support multi-item Add notifications, so Reset is the portable single
/// notification; with UI virtualization it only re-realizes the visible rows.
/// </summary>
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    public BulkObservableCollection()
    {
    }

    public BulkObservableCollection(IEnumerable<T> items)
        : base(items)
    {
    }

    /// <summary>Appends all items with a single notification. No notification when <paramref name="items"/> is empty.</summary>
    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        CheckReentrancy();
        var added = false;
        foreach (var item in items)
        {
            Items.Add(item);
            added = true;
        }

        if (added)
        {
            RaiseReset();
        }
    }

    /// <summary>Replaces the whole content with a single notification.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        CheckReentrancy();
        var snapshot = items as IReadOnlyCollection<T> ?? items.ToList();
        if (Items.Count == 0 && snapshot.Count == 0)
        {
            return;
        }

        Items.Clear();
        foreach (var item in snapshot)
        {
            Items.Add(item);
        }

        RaiseReset();
    }

    /// <summary>Removes all matching items with a single notification. Returns the number removed.</summary>
    public int RemoveWhere(Predicate<T> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        CheckReentrancy();
        var removed = 0;
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (match(Items[i]))
            {
                Items.RemoveAt(i);
                removed++;
            }
        }

        if (removed > 0)
        {
            RaiseReset();
        }

        return removed;
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
