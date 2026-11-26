using System.ComponentModel;

namespace AdMng.Presentation.Feedback;

/// <summary>
/// The view model of a window whose status line is not its own but the one of the page in front of it (the main window: the
/// selected tab says what it reads and what it did). The taskbar button of the window follows that holder
/// (track V, stage V2b-5b; <c>TaskbarProgress</c>): the holder in front is the one that counts.
/// </summary>
public interface IHasFrontStatus : INotifyPropertyChanged
{
    /// <summary>The holder of the page in front, or null. Raises <see cref="INotifyPropertyChanged.PropertyChanged"/> for this name when it changes.</summary>
    PageStatusHolder? FrontStatus { get; }
}
