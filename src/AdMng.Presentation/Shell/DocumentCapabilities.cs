using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Shell;

/// <summary>
/// A document tab whose edits are saved by "Zapisz zmiany…" (K6, docs/dev/k-computers-and-gpo.md 20.3): Ctrl+S and the
/// palette entry <c>document.save</c> of the shell run <see cref="SaveCommand"/> of the active tab when it can execute.
/// </summary>
public interface ISaveableDocument : IShellDocument
{
    /// <summary>"Zapisz zmiany…" of the document (its CanExecute decides whether Ctrl+S does anything).</summary>
    IAsyncRelayCommand SaveCommand { get; }
}

/// <summary>
/// A document tab with its own search box (K6, 20.3): Ctrl+F moves the keyboard there (<see cref="FocusSearch"/> raises
/// <see cref="SearchFocusRequested"/>, which the view handles) instead of opening the directory Find; Esc in the box clears
/// <see cref="SearchText"/>.
/// </summary>
public interface ISearchableDocument : IShellDocument
{
    /// <summary>What the document's search box holds (an empty text shows everything).</summary>
    string SearchText { get; set; }

    /// <summary>Raised by <see cref="FocusSearch"/>: the view focuses the document's search box and selects its text.</summary>
    event EventHandler? SearchFocusRequested;

    /// <summary>Asks the view to move the keyboard to the search box (Ctrl+F).</summary>
    void FocusSearch();
}
