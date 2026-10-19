using System.ComponentModel;
using AdMng.Presentation.Icons;

namespace AdMng.Presentation.Shell;

/// <summary>
/// A document tab of the main window: an object list (<see cref="Lists.ObjectListViewModel"/>) or the Watchdog tab
/// (<see cref="Watchdog.WatchdogDocumentViewModel"/>). The shell shows its title, icon and status texts and calls
/// <see cref="RefreshAsync"/> on F5; closing the tab (Ctrl+W, its close button, the main window) first awaits
/// <see cref="CanCloseAsync"/> when <see cref="HasUnsavedChanges"/>, then disposes it.
/// </summary>
public interface IShellDocument : INotifyPropertyChanged, IDisposable
{
    string Title { get; }

    ObjectIcon Icon { get; }

    bool IsLoading { get; }

    string StatusText { get; }

    string SelectionText { get; }

    /// <summary>F5 on this tab.</summary>
    Task RefreshAsync();

    /// <summary>The tab holds unsaved changes that closing it would discard (the main window asks before it closes).</summary>
    bool HasUnsavedChanges => false;

    /// <summary>Asks before unsaved changes are discarded. True when the tab may close.</summary>
    Task<bool> CanCloseAsync() => Task.FromResult(true);
}
