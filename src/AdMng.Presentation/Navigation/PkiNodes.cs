using AdMng.Presentation.Icons;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Pki;

namespace AdMng.Presentation.Navigation;

/// <summary>
/// Base of the virtual AD CS nodes under the domain (P1, docs/dev/p-certificate-services.md 2.5, decision 9): an own base, not
/// <see cref="GroupPolicyNodeViewModel"/> (the Group Policy toggle would remove it and the shell's Group Policy activation
/// would swallow it). They are not directory containers: <see cref="ContainerNodeViewModel"/> keeps them before the
/// containers when it merges its children, and selecting one opens its document in the shell (<see cref="Kind"/>).
/// </summary>
public abstract class PkiNodeViewModel : TreeNodeViewModel
{
    private protected PkiNodeViewModel(NavigationTreeViewModel tree, TreeNodeViewModel parent, string name, PkiViewModelFactory pki)
        : base(tree, parent, name, ObjectIcon.Certificate)
    {
        Tree = tree;
        Pki = pki;
    }

    /// <summary>The window's PKI documents (the tree menu and the shell use them).</summary>
    public PkiViewModelFactory Pki { get; }

    /// <summary>The document the node opens.</summary>
    public abstract PkiDocumentKind Kind { get; }

    /// <summary>The request the shell opens when the node is selected.</summary>
    public PkiDocumentRequest Request => new(Kind);

    private protected NavigationTreeViewModel Tree { get; }

    /// <summary>Re-localizes the node's name (and its children's).</summary>
    internal abstract void Relocalize();
}

/// <summary>
/// <i>Usługi kluczy publicznych</i>: opens the PKI overview; its children open the templates, the AD CS security review and the
/// strong-mapping / NTAuth readiness report (static, nothing is read to show them).
/// </summary>
public sealed class PublicKeyServicesNodeViewModel : PkiNodeViewModel
{
    internal PublicKeyServicesNodeViewModel(NavigationTreeViewModel tree, TreeNodeViewModel parent, PkiViewModelFactory pki)
        : base(tree, parent, tree.Localizer[L.Pki.NodeTitle], pki)
    {
        Children.Add(new PkiDocumentNodeViewModel(tree, this, pki, PkiDocumentKind.Templates));
        Children.Add(new PkiDocumentNodeViewModel(tree, this, pki, PkiDocumentKind.SecurityReview));
        Children.Add(new PkiDocumentNodeViewModel(tree, this, pki, PkiDocumentKind.Readiness));
    }

    public override PkiDocumentKind Kind => PkiDocumentKind.Overview;

    public override string? HelpText => Tree.Localizer[L.Pki.NodeHelp];

    internal override void Relocalize()
    {
        Name = Tree.Localizer[L.Pki.NodeTitle];
        OnPropertyChanged(nameof(HelpText));
        foreach (var child in Children.OfType<PkiNodeViewModel>())
        {
            child.Relocalize();
        }
    }
}

/// <summary>A child of <see cref="PublicKeyServicesNodeViewModel"/> that opens one PKI document.</summary>
public sealed class PkiDocumentNodeViewModel : PkiNodeViewModel
{
    internal PkiDocumentNodeViewModel(NavigationTreeViewModel tree, TreeNodeViewModel parent, PkiViewModelFactory pki, PkiDocumentKind kind)
        : base(tree, parent, tree.Localizer[TitleKey(kind)], pki)
    {
        Kind = kind;
    }

    public override PkiDocumentKind Kind { get; }

    internal override void Relocalize() => Name = Tree.Localizer[TitleKey(Kind)];

    /// <summary>The node title of a document kind.</summary>
    internal static string TitleKey(PkiDocumentKind kind) => kind switch
    {
        PkiDocumentKind.Templates => L.PkiTemplates.Title,
        PkiDocumentKind.SecurityReview => L.Pki.SecurityReviewNode,
        PkiDocumentKind.Readiness => L.Pki.ReadinessNode,
        _ => L.Pki.NodeTitle,
    };
}
