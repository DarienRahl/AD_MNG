using System.ComponentModel;
using AdMng.Core.Directory;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Dialogs;

/// <summary>
/// ADUC-style container browser (Move, search base, CSV lookup base): the domain tree with lazily loaded
/// containers, reusing the navigation tree's node view models. <see cref="Result"/> holds the chosen DN after OK.
/// </summary>
/// <remarks>The tree follows the session manager's current session (the session passed by the caller).</remarks>
public sealed partial class ContainerPickerViewModel : WindowViewModelBase
{
    private readonly IDirectorySession _session;
    private readonly string? _startDn;

    public ContainerPickerViewModel(DialogServices services, NavigationTreeViewModel tree, IDirectorySession session, string? startDn)
        : base(services)
    {
        Tree = tree ?? throw new ArgumentNullException(nameof(tree));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _startDn = startDn;
        Tree.PropertyChanged += OnTreeChanged;
    }

    public override string WindowTitle => Localizer[L.Picker.ContainerWindowTitle];

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

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>The chosen container DN after OK.</summary>
    public string? Result { get; private set; }

    public override async Task InitializeAsync()
    {
        IsLoading = true;
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Tree.PropertyChanged -= OnTreeChanged;
            Tree.Dispose();
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
