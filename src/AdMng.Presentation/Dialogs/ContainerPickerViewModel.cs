using System.ComponentModel;
using AdMng.Core.Directory;
using AdMng.Presentation.Connections;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Dialogs;

/// <summary>
/// ADUC-style container browser (Move, search base, CSV lookup base): the domain tree with lazily loaded
/// containers, reusing the navigation tree's node view models. <see cref="Result"/> holds the chosen DN after OK.
/// </summary>
/// <remarks>
/// The tree follows the session manager's current session (the session passed by the caller).
/// <para>The dialog has one status line (<see cref="Status"/>; track V, stage V2b, design note
/// docs/dev/v-interface-renewal.md "V2b-2 as built"): the read of the tree when the dialog opens is its operation
/// ("Reading the containers of …"), and - as the state that lasts - the line says that the dialog has no session, or
/// that the content of the selected container could not be read, with Retry. A container the operator opens later
/// shows that it is being read in its own row, as in the main window.</para>
/// </remarks>
public sealed partial class ContainerPickerViewModel : WindowViewModelBase, IHasPageStatus
{
    private readonly IDirectorySession _session;
    private readonly string? _startDn;

    /// <summary>The selected container, whose read the line follows.</summary>
    private ContainerNodeViewModel? _watched;

    public ContainerPickerViewModel(DialogServices services, NavigationTreeViewModel tree, IDirectorySession session, string? startDn)
        : base(services)
    {
        Tree = tree ?? throw new ArgumentNullException(nameof(tree));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _startDn = startDn;
        Status = new PageStatusHolder(Localizer, services.Dispatcher, services.Lists.StatusLog);
        Tree.PropertyChanged += OnTreeChanged;
        UpdateLasting();
    }

    public override string WindowTitle => Localizer[L.Picker.ContainerWindowTitle];

    /// <summary>
    /// The status of the dialog - what its one status line shows: the read of the tree when the dialog opens, and - for
    /// as long as it lasts - that there is no session, or that the content of the selected container could not be read.
    /// </summary>
    public PageStatusHolder Status { get; }

    /// <summary>The underlying tree (lazy loading, Advanced Features setting respected).</summary>
    public NavigationTreeViewModel Tree { get; }

    /// <summary>Root of the picker: the domain node only (no Views section).</summary>
    public IReadOnlyList<TreeNodeViewModel> Roots => Tree.DomainNode is { } domain ? [domain] : [];

    /// <summary>The selected container (null for non-container nodes).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedPath))]
    public partial ContainerNodeViewModel? SelectedContainer { get; private set; }

    /// <summary>Canonical path of the selection ("contoso.test/Firma/IT").</summary>
    public string SelectedPath => SelectedContainer is null ? string.Empty : AddressResolver.ToDisplayPath(SelectedContainer.Dn);

    /// <summary>The tree is being read and opened at the start container (the status line says it).</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>The chosen container DN after OK.</summary>
    public string? Result { get; private set; }

    /// <summary>
    /// Reads the tree and opens it at the start container - the operation of the dialog: its line says "Reading the
    /// containers of …" until the tree stands. It has no Stop: the tree reads its containers itself, without a token
    /// (as in the main window), and the dialog's own Cancel closes it at any time. A container on the way that could
    /// not be read ends the walk there: it is selected, and the line says why (<see cref="UpdateLasting"/>).
    /// </summary>
    public override async Task InitializeAsync()
    {
        if (Session is not { } session)
        {
            // Not connected, or the session ended before the dialog was shown: the line says so, nothing can be read.
            UpdateLasting();
            return;
        }

        IsLoading = true;
        using var reading = Status.Begin(
            Localizer.Format(L.ContainerPickerStatus.Reading, ConfirmationText.Domain(session)),
            new PageStatusLog(L.ContainerPickerStatus.Reading),
            canCancel: false);
        try
        {
            await Tree.InitializeAsync().ConfigureAwait(true);
            OnPropertyChanged(nameof(Roots));
            var start = _startDn ?? _session.Info.DefaultNamingContext;
            var node = await Tree.ExpandToAsync(start).ConfigureAwait(true);
            if (node is null && Tree.DomainNode is { } domain)
            {
                Tree.SelectSilently(domain);
            }
        }
        catch (DirectoryException)
        {
            // Nodes show their own load errors.
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanOk))]
    private void Ok()
    {
        Result = SelectedContainer?.Dn;
        RequestClose(Result is not null);
    }

    private bool CanOk() => SelectedContainer is not null;

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        RequestClose(false);
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();

        // The dialog's own sentences in the new language (why a container could not be read stays as it was said).
        UpdateLasting();
    }

    protected override void OnSessionEnded()
    {
        // The line says for good that the session ended; a read that still runs ends without a word of its own (the
        // state first, so the line goes straight from "working" to it).
        UpdateLasting();
        Status.Operation?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Tree.PropertyChanged -= OnTreeChanged;
            Watch(null);

            // What still runs says nothing any more.
            Status.Operation?.Dispose();
            Tree.Dispose();
        }
    }

    partial void OnSelectedContainerChanged(ContainerNodeViewModel? value) => Watch(value);

    /// <summary>
    /// The state of the dialog that lasts, the most important first: its session ended, or it was opened without one -
    /// an error that cannot be closed, nothing can be read here -; the content of the selected container could not be
    /// read (after the dialog opened that is the container the walk to the start container ended at).
    /// </summary>
    private void UpdateLasting() => Status.SetLasting(
        SessionEndedStatus
        ?? (Session is null ? PageStatus.Error(NoSessionMessage, new PageStatusLog(L.Errors.NotConnected), canClose: false) : null)
        ?? ReadFailure());

    /// <summary>
    /// "Could not read the containers in “Firma”: reason. Press Retry to try again. (code)" for the selected container
    /// whose read failed, with the command that reads it again; null when there is nothing of the kind to say. The row
    /// of the container marks the failure as well - with a glyph, whose reason is a tool tip only.
    /// </summary>
    private PageStatus? ReadFailure()
    {
        if (_watched is not { LoadError: { Length: > 0 } reason } node)
        {
            return null;
        }

        var code = node.LoadErrorCode;
        return PageStatus.Error(
            PageStatusText.Failure(Localizer.Format(L.ContainerPickerStatus.ReadFailed, node.Name), reason, Localizer[L.Feedback.RetryNext], code),
            new PageStatusLog(L.ContainerPickerStatus.ReadFailed).WithCode(code),
            new PageStatusAction(Localizer[L.Common.Retry], node.RefreshCommand),
            canClose: false);
    }

    /// <summary>Follows the read of the selected container: its failure is said by the line for as long as it is selected.</summary>
    private void Watch(ContainerNodeViewModel? node)
    {
        if (ReferenceEquals(_watched, node))
        {
            return;
        }

        if (_watched is not null)
        {
            _watched.PropertyChanged -= OnWatchedChanged;
        }

        _watched = node;
        if (node is not null)
        {
            node.PropertyChanged += OnWatchedChanged;
        }

        UpdateLasting();
    }

    private void OnWatchedChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ContainerNodeViewModel.LoadError))
        {
            UpdateLasting();
        }
    }

    private void OnTreeChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NavigationTreeViewModel.SelectedNode):
                SelectedContainer = Tree.SelectedNode is ContainerNodeViewModel { IsPinnedEntry: false } container ? container : null;
                break;
            case nameof(NavigationTreeViewModel.DomainNode):
                OnPropertyChanged(nameof(Roots));
                break;
        }
    }
}
