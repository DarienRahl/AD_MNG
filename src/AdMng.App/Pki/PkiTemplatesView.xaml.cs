using System.Windows.Controls;
using AdMng.Presentation.Pki;

namespace AdMng.App.Pki;

/// <summary><i>Szablony certyfikatów</i> (DataContext: <see cref="PkiTemplatesDocumentViewModel"/>).</summary>
public partial class PkiTemplatesView : UserControl
{
    public PkiTemplatesView()
    {
        InitializeComponent();
    }

    /// <summary>The list's selection goes to the view model (two selected templates start the comparison of each other).</summary>
    private void OnTemplatesSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is PkiTemplatesDocumentViewModel document)
        {
            document.SetSelection([.. TemplatesList.SelectedItems.OfType<PkiTemplateRowViewModel>()]);
        }
    }
}
