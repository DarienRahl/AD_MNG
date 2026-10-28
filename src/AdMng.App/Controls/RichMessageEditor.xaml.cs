using System.Windows;
using System.Windows.Controls;
using AdMng.Presentation.Correspondence;

namespace AdMng.App.Controls;

/// <summary>
/// The message editor of Korespondencja (design note 9.2): <see cref="Html"/> is the body as the sanitized subset (two-way,
/// debounced 300 ms from the document), <see cref="Host"/> the view model's paste and image services, <see cref="FieldChoices"/>
/// the <c>{{field}}</c> texts of "Wstaw pole".
/// </summary>
public partial class RichMessageEditor : UserControl
{
    public static readonly DependencyProperty HtmlProperty = DependencyProperty.Register(
        nameof(Html), typeof(string), typeof(RichMessageEditor),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty HostProperty = DependencyProperty.Register(
        nameof(Host), typeof(IRichEditorHost), typeof(RichMessageEditor), new PropertyMetadata(null));

    public static readonly DependencyProperty FieldChoicesProperty = DependencyProperty.Register(
        nameof(FieldChoices), typeof(IReadOnlyList<string>), typeof(RichMessageEditor), new PropertyMetadata(null));

    public RichMessageEditor()
    {
        InitializeComponent();
    }

    /// <summary>The body: an HTML fragment of the sanitized subset with <c>cid:</c> images.</summary>
    public string Html
    {
        get => (string)GetValue(HtmlProperty);
        set => SetValue(HtmlProperty, value);
    }

    /// <summary>The view model's paste workflow, image registration and error line.</summary>
    public IRichEditorHost? Host
    {
        get => (IRichEditorHost?)GetValue(HostProperty);
        set => SetValue(HostProperty, value);
    }

    /// <summary>The <c>{{field}}</c> texts of "Wstaw pole".</summary>
    public IReadOnlyList<string>? FieldChoices
    {
        get => (IReadOnlyList<string>?)GetValue(FieldChoicesProperty);
        set => SetValue(FieldChoicesProperty, value);
    }
}
