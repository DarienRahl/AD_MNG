using System.ComponentModel;
using AdMng.Presentation.Icons;

namespace AdMng.Presentation.Shell;

/// <summary>
/// A document tab of the main window: an object list (<see cref="Lists.ObjectListViewModel"/>) or the Watchdog tab
/// (<see cref="Watchdog.WatchdogDocumentViewModel"/>). The shell shows its title, icon and status texts and calls
/// <see cref="RefreshAsync"/> on F5; closing the tab disposes it.
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
}
