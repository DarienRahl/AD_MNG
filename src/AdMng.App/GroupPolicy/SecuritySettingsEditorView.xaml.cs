using System.ComponentModel;
using System.Windows.Controls;
using AdMng.Presentation.GroupPolicy.SecuritySettings;

namespace AdMng.App.GroupPolicy;

/// <summary>
/// The security settings editor (K4): every page binds to <c>SecuritySettingsEditorViewModel</c>; the group column of the
/// options list is shown only while a search or a filter spans the groups (K6).
/// </summary>
public partial class SecuritySettingsEditorView : UserControl
{
    private const int GroupColumnIndex = 3;
    private SecuritySettingsEditorViewModel? _editor;

    public SecuritySettingsEditorView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Observe(e.NewValue as SecuritySettingsEditorViewModel);
        Unloaded += (_, _) => Observe(null);
        Loaded += (_, _) => Observe(DataContext as SecuritySettingsEditorViewModel);
    }

    private void Observe(SecuritySettingsEditorViewModel? editor)
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

        UpdateGroupColumn();
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SecuritySettingsEditorViewModel.ShowsOptionGroupColumn))
        {
            UpdateGroupColumn();
        }
    }

    private void UpdateGroupColumn() =>
        GroupPolicyUi.ShowColumn(OptionGrid, OptionGroupColumn, _editor?.ShowsOptionGroupColumn ?? false, GroupColumnIndex);
}
