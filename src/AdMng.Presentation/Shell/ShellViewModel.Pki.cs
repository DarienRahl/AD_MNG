using AdMng.Presentation.Navigation;
using AdMng.Presentation.Pki;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Shell;

/// <summary>
/// AD CS in the main window (P1, docs/dev/p-certificate-services.md 2.5): the documents <i>Usługi kluczy publicznych</i>,
/// <i>Szablony certyfikatów</i>, <i>Przegląd bezpieczeństwa AD CS</i> and <i>Gotowość na silne mapowanie i NTAuth</i> open as
/// tabs (one per kind; an open one is selected), the PKI node of the tree opens the overview, and the palette lists
/// <c>pki.overview</c>, <c>pki.templates</c>, <c>pki.security</c>, <c>pki.readiness</c>. The contract commit wires the call
/// sites (constructor, <c>Dispose</c>, <c>OnNodeActivated</c>, the palette, <c>StateCommands</c>, <see cref="ActivePki"/>) to
/// these members; W5 writes their bodies (the tree's <c>PkiViewModelFactory</c>, <c>DocumentRequested</c>, the four
/// descriptors). Until then they do nothing, so the window behaves as before.
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>The open PKI tabs.</summary>
    public IEnumerable<PkiDocumentBase> PkiDocuments => Documents.OfType<PkiDocumentBase>();

    /// <summary>
    /// The PKI document the window is on (P1, docs/dev/p-certificate-services.md 2.5): the active PKI tab, null when none.
    /// W5 adds the arms its palette commands need (the selected template or CA of the active document).
    /// </summary>
    public PkiDocumentBase? ActivePki() => SelectedTab switch
    {
        PkiDocumentBase pki => pki,
        _ => null,
    };

    /// <summary>The commands whose CanExecute depends on the session or the PKI services (W5).</summary>
    private IRelayCommand[] PkiStateCommands => [];

    /// <summary>
    /// Opens the PKI document a request names, or selects it when it is open (W5: <c>PkiViewModelFactory.Matches</c>,
    /// <c>Create</c>, <c>LoadAsync</c>). Null when the window has no PKI services.
    /// </summary>
    public PkiDocumentBase? OpenPkiDocument(PkiDocumentRequest request) =>
        throw new NotImplementedException("P1 W5: ShellViewModel.OpenPkiDocument");

    /// <summary>The four <c>pki.*</c> descriptors (W5; kind-generic, they need no W6 type). None in the contract stub.</summary>
    private IEnumerable<CommandDescriptor> PkiCommandDescriptors() => [];

    /// <summary>Subscribes to <c>PkiServices.DocumentRequested</c> (constructor; W5).</summary>
    private void AttachPki()
    {
    }

    /// <summary>Ends the subscription (Dispose; W5).</summary>
    private void DetachPki()
    {
    }

    /// <summary>A PKI node was selected in the tree: its document opens (true when the node was one of them; W5).</summary>
    private bool TryActivatePkiNode(TreeNodeViewModel node) => false;
}
