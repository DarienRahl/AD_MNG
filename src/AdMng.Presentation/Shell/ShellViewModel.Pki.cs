using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Pki;
using CommunityToolkit.Mvvm.Input;
using AdMng.Presentation.Services;

namespace AdMng.Presentation.Shell;

/// <summary>
/// AD CS in the main window (P1, docs/dev/p-certificate-services.md 2.5): the documents <i>Usługi kluczy publicznych</i>,
/// <i>Szablony certyfikatów</i>, <i>Przegląd bezpieczeństwa AD CS</i> and <i>Gotowość na silne mapowanie i NTAuth</i> open as
/// tabs (one per kind; an open one is selected and pointed at what the request names), the PKI nodes of the tree open their
/// documents, the palette lists <c>pki.overview</c>, <c>pki.templates</c>, <c>pki.security</c>, <c>pki.readiness</c> and
/// <c>view.pkiNodes</c> (<i>Widok → Usługi kluczy publicznych w drzewie</i>), and the documents' <c>DocumentRequested</c>
/// (<i>Otwórz szablon/urząd</i>) opens the document they name. The window has PKI documents when its tree has the
/// <see cref="PkiViewModelFactory"/> (<see cref="NavigationTreeViewModel.Pki"/>).
/// </summary>
public sealed partial class ShellViewModel
{
    private IRelayCommand[]? _pkiStateCommands;

    /// <summary>The open PKI tabs.</summary>
    public IEnumerable<PkiDocumentBase> PkiDocuments => Documents.OfType<PkiDocumentBase>();

    /// <summary>The PKI node is shown in the tree (<i>Widok → Usługi kluczy publicznych w drzewie</i>).</summary>
    public bool IsPkiNodesShown => Tree.IsPkiShown;

    /// <summary>
    /// The PKI document the window is on (P1, docs/dev/p-certificate-services.md 2.5): the active PKI tab, null when none.
    /// The <c>pki.*</c> commands are kind-generic, so no arm needs the selected template or CA.
    /// </summary>
    public PkiDocumentBase? ActivePki() => SelectedTab switch
    {
        PkiDocumentBase pki => pki,
        _ => null,
    };

    /// <summary>The commands whose CanExecute depends on the session or the PKI services.</summary>
    private IRelayCommand[] PkiStateCommands => _pkiStateCommands ??=
    [
        OpenPkiOverviewCommand, OpenPkiTemplatesCommand, OpenPkiSecurityReviewCommand, OpenPkiReadinessCommand, TogglePkiNodesCommand,
    ];

    /// <summary>
    /// Opens the PKI document a request names, or selects it when it is open (an open document is pointed at the request's
    /// template or CA through <see cref="IPkiRequestTarget"/>). Null when the window has no PKI services.
    /// </summary>
    public PkiDocumentBase? OpenPkiDocument(PkiDocumentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Tree.Pki is not { } factory || _disposed)
        {
            return null;
        }

        var document = PkiDocuments.FirstOrDefault(d => PkiViewModelFactory.Matches(d, request));
        if (document is null)
        {
            document = (PkiDocumentBase)factory.Create(request);
            Documents.Add(document);
            document.LoadAsync().Forget();
        }
        else if (document is IPkiRequestTarget target)
        {
            target.Apply(request);
        }

        SelectedTab = document;
        return document;
    }

    /// <summary>The <c>pki.*</c> descriptors (kind-generic: the W6 documents need no type here) and the tree toggle.</summary>
    private IEnumerable<CommandDescriptor> PkiCommandDescriptors()
    {
        const string tools = L.CommandCategories.Tools;
        const string view = L.CommandCategories.View;
        return
        [
            new("pki.overview", L.Pki.OverviewCommand, tools, string.Empty, OpenPkiOverviewCommand),
            new("pki.templates", L.PkiTemplates.Command, tools, string.Empty, OpenPkiTemplatesCommand),
            new("pki.security", L.Pki.SecurityReviewCommand, tools, string.Empty, OpenPkiSecurityReviewCommand),
            new("pki.readiness", L.Pki.ReadinessCommand, tools, string.Empty, OpenPkiReadinessCommand),
            new("view.pkiNodes", L.Pki.ToggleTreeCommand, view, string.Empty, TogglePkiNodesCommand),
        ];
    }

    /// <summary>Subscribes to the document requests of the window's PKI documents (constructor).</summary>
    private void AttachPki()
    {
        if (Tree.Pki is { } factory)
        {
            factory.Pki.DocumentRequested += OnPkiDocumentRequested;
        }

        Tree.PropertyChanged += OnTreePkiChanged;
    }

    /// <summary>Ends the subscriptions (Dispose).</summary>
    private void DetachPki()
    {
        if (Tree.Pki is { } factory)
        {
            factory.Pki.DocumentRequested -= OnPkiDocumentRequested;
        }

        Tree.PropertyChanged -= OnTreePkiChanged;
    }

    /// <summary>A PKI node was selected in the tree: its document opens (true when the node was one of them).</summary>
    private bool TryActivatePkiNode(TreeNodeViewModel node)
    {
        if (node is not PkiNodeViewModel pki)
        {
            return false;
        }

        OpenPkiDocument(pki.Request);
        return true;
    }

    private void OnPkiDocumentRequested(object? sender, PkiDocumentRequest request) =>
        _ = _lists.Dispatcher.RunAsync(() => OpenPkiDocument(request));

    private void OnTreePkiChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NavigationTreeViewModel.IsPkiShown))
        {
            OnPropertyChanged(nameof(IsPkiNodesShown));
        }
    }

    /// <summary><i>Usługi kluczy publicznych…</i>: the PKI overview.</summary>
    [RelayCommand(CanExecute = nameof(CanUsePki))]
    private void OpenPkiOverview() => OpenPkiDocument(new PkiDocumentRequest(PkiDocumentKind.Overview));

    /// <summary><i>Szablony certyfikatów…</i>.</summary>
    [RelayCommand(CanExecute = nameof(CanUsePki))]
    private void OpenPkiTemplates() => OpenPkiDocument(new PkiDocumentRequest(PkiDocumentKind.Templates));

    /// <summary><i>Przegląd bezpieczeństwa AD CS…</i> (the W6 document).</summary>
    [RelayCommand(CanExecute = nameof(CanUsePki))]
    private void OpenPkiSecurityReview() => OpenPkiDocument(new PkiDocumentRequest(PkiDocumentKind.SecurityReview));

    /// <summary><i>Gotowość na silne mapowanie i NTAuth…</i> (the W6 document).</summary>
    [RelayCommand(CanExecute = nameof(CanUsePki))]
    private void OpenPkiReadiness() => OpenPkiDocument(new PkiDocumentRequest(PkiDocumentKind.Readiness));

    /// <summary><i>Widok → Usługi kluczy publicznych w drzewie</i> (shared by all windows: every tree follows the setting).</summary>
    [RelayCommand(CanExecute = nameof(CanUsePkiAlways))]
    private async Task TogglePkiNodesAsync()
    {
        await Settings.UpdateAsync(s => s.Pki.ShowInTree = !s.Pki.ShowInTree).ConfigureAwait(true);
        OnPropertyChanged(nameof(IsPkiNodesShown));
    }

    private bool CanUsePki() => Tree.Pki is not null && Sessions.Current is not null;

    private bool CanUsePkiAlways() => Tree.Pki is not null;
}
