using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;

namespace AdMng.App.GroupPolicy;

/// <summary>
/// The administrative templates editor (K3); the tree selection goes to the view model, and the category column is shown
/// only while the list spans categories (<see cref="AdministrativeTemplatesEditorViewModel.ShowsCategoryColumn"/>, K6).
/// </summary>
public partial class AdministrativeTemplatesEditorView : UserControl
{
    private const int CategoryColumnIndex = 3;
    private AdministrativeTemplatesEditorViewModel? _editor;

    public AdministrativeTemplatesEditorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Observe(null);
        Loaded += (_, _) => Observe(DataContext as AdministrativeTemplatesEditorViewModel);
    }

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is AdministrativeTemplatesEditorViewModel editor && e.NewValue is PolicyTreeNodeViewModel node)
        {
            editor.SelectedNode = node;
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => Observe(e.NewValue as AdministrativeTemplatesEditorViewModel);

    private void Observe(AdministrativeTemplatesEditorViewModel? editor)
    {
        if (_editor is not null)
        {
            _editor.PropertyChanged -= OnEditorPropertyChanged;
        }

        _editor = editor;
        if (editor is not null)
        {
            editor.PropertyChanged += OnEditorPropertyChanged;
        }

        UpdateCategoryColumn();
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AdministrativeTemplatesEditorViewModel.ShowsCategoryColumn))
        {
            UpdateCategoryColumn();
        }
    }

    private void UpdateCategoryColumn() => GroupPolicyUi.ShowColumn(PolicyGrid, CategoryColumn, _editor?.ShowsCategoryColumn ?? true, CategoryColumnIndex);
}
