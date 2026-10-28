using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.Presentation.Correspondence;

namespace AdMng.App.Correspondence;

/// <summary>
/// Korespondencja (<see cref="CorrespondenceViewModel"/>, modeless; one per connection window from the Tools menu, a new
/// one for selected objects or mailbox owners). The view's own work:
/// <list type="bullet">
/// <item>the field columns of the recipient grid, rebuilt from <see cref="RecipientsPageViewModel.Columns"/> (cells bound
/// to the row indexer);</item>
/// <item>the multi-selections of the recipient grid and the attachment list handed to the page view models;</item>
/// <item>files dropped on the recipient grid or the attachment list;</item>
/// <item>"Wstaw pole" (<see cref="MessagePageViewModel.InsertFieldRequested"/>) at the caret of the subject box or the
/// editor, whichever had the keyboard focus last;</item>
/// <item>the editor's pending edits written to <see cref="MessagePageViewModel.BodyHtml"/> before any button runs its
/// command, before another tab is chosen and before the window closes, so previews, sends and the saved last draft never
/// use a stale body.</item>
/// </list>
/// The preview renders through the application's <see cref="WebView2Host"/> (null: the HTML source is shown). The size is
/// clamped to the work area (1366×768 screens).
/// </summary>
public partial class CorrespondenceWindow : Window, IDialogResultWindow
{
    /// <summary>The default width of a field column of the recipient grid.</summary>
    internal const double FieldColumnWidth = 140;

    private readonly WindowViewModelLifecycle _lifecycle;
    private readonly int _fixedRecipientColumns;
    private FieldTarget _fieldTarget = FieldTarget.Body;

    public CorrespondenceWindow(CorrespondenceViewModel viewModel, WebView2Host? previewHost = null)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        UiLanguage.ApplyTo(this);
        FitToWorkArea(this, SystemParameters.WorkArea);
        PreviewView.Host = previewHost;
        DataContext = viewModel;

        viewModel.Recipients.SelectedItems = RecipientsGrid.SelectedItems;
        viewModel.Message.SelectedAttachments = AttachmentsList.SelectedItems;
        _fixedRecipientColumns = RecipientsGridView.Columns.Count;
        BuildFieldColumns();
        viewModel.Recipients.Columns.CollectionChanged += OnRecipientColumnsChanged;
        viewModel.Message.InsertFieldRequested += OnInsertFieldRequested;

        SubjectBox.GotKeyboardFocus += (_, _) => _fieldTarget = FieldTarget.Subject;
        MessageEditor.GotKeyboardFocus += (_, _) => _fieldTarget = FieldTarget.Body;
        ((INotifyCollectionChanged)SubjectFieldBox.Items).CollectionChanged += (_, _) => SelectFirstField();
        SelectFirstField();

        // Click is raised before a button executes its command: the body is current for every command of the window.
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, _) => MessageEditor.FlushHtml()), handledEventsToo: true);
        CorrespondenceTabs.PreviewMouseLeftButtonDown += OnTabsMouseDown;
        PreviewKeyDown += OnPreviewKeyDown;

        // Before the lifecycle's Closing handler: the view model saves the last draft from a current body.
        Closing += OnClosing;
        _lifecycle = new WindowViewModelLifecycle(this, viewModel);
        Closed += OnClosed;
    }

    /// <summary>Where "Wstaw pole" inserts.</summary>
    internal enum FieldTarget
    {
        Body,
        Subject,
    }

    public CorrespondenceViewModel ViewModel { get; }

    public bool? Result => _lifecycle.Result;

    /// <summary>Completes when the view model was initialized (tests).</summary>
    public Task Initialization => _lifecycle.Initialization;

    /// <summary>Where "Wstaw pole" inserts now: the subject box or the editor, whichever had the keyboard focus last.</summary>
    internal FieldTarget InsertTarget => _fieldTarget;

    /// <summary>
    /// The binding path of a field column: the row indexer with <paramref name="column"/> as its argument. Every character
    /// that is not a letter or a digit is escaped with '^' (WPF's escape inside an indexer), so column names with spaces,
    /// commas, brackets or parentheses ("Kod pocztowy", "Uwagi (wewn.)") reach the indexer unchanged.
    /// </summary>
    internal static string IndexerPath(string column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var path = new StringBuilder(column.Length * 2 + 2).Append('[');
        foreach (var c in column)
        {
            if (!char.IsLetterOrDigit(c))
            {
                path.Append('^');
            }

            path.Append(c);
        }

        return path.Append(']').ToString();
    }

    /// <summary>
    /// Shrinks the window (and its minimum size) to <paramref name="workArea"/> when the screen is smaller than the
    /// declared size (1320×900, at least 1000×680), so the window fits a 1366×768 screen.
    /// </summary>
    internal static void FitToWorkArea(Window window, Rect workArea)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (workArea.IsEmpty || workArea.Width <= 0 || workArea.Height <= 0)
        {
            return;
        }

        window.MinWidth = Math.Min(window.MinWidth, workArea.Width);
        window.MinHeight = Math.Min(window.MinHeight, workArea.Height);
        window.Width = Math.Min(window.Width, workArea.Width);
        window.Height = Math.Min(window.Height, workArea.Height);
    }

    /// <summary>The files of a drop (Explorer, Outlook attachments saved as files); empty for anything else.</summary>
    internal static IReadOnlyList<string> DroppedFiles(IDataObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            return data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files
                ? [.. files.Where(f => !string.IsNullOrWhiteSpace(f))]
                : [];
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or OutOfMemoryException)
        {
            // A source that cannot render its data: nothing was dropped.
            return [];
        }
    }

    /// <summary>"Wstaw pole": at the caret of the subject box when it had the focus last, else at the editor's caret.</summary>
    internal void InsertField(string field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.Length == 0)
        {
            return;
        }

        if (_fieldTarget == FieldTarget.Subject)
        {
            var caret = SubjectBox.SelectionStart;
            SubjectBox.SelectedText = field;
            SubjectBox.Select(caret + field.Length, 0);
            SubjectBox.Focus();
        }
        else
        {
            MessageEditor.InsertField(field);
        }
    }

    /// <summary>One grid column per field (after the fixed columns), headers as typed in the source file.</summary>
    private void BuildFieldColumns()
    {
        var columns = RecipientsGridView.Columns;
        while (columns.Count > _fixedRecipientColumns)
        {
            columns.RemoveAt(columns.Count - 1);
        }

        foreach (var column in ViewModel.Recipients.Columns)
        {
            // A TextBlock header: the name is data, never an access key ("Kod_pocztowy").
            var header = new TextBlock { Text = column };
            AutomationProperties.SetName(header, column);
            columns.Add(new GridViewColumn
            {
                Header = header,
                Width = FieldColumnWidth,
                DisplayMemberBinding = new Binding(IndexerPath(column)) { Mode = BindingMode.OneWay },
            });
        }
    }

    private void OnRecipientColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            BuildFieldColumns();
        }
        else
        {
            _ = Dispatcher.InvokeAsync(BuildFieldColumns);
        }
    }

    private void OnInsertFieldRequested(object? sender, string field)
    {
        if (Dispatcher.CheckAccess())
        {
            InsertField(field);
        }
        else
        {
            _ = Dispatcher.InvokeAsync(() => InsertField(field));
        }
    }

    /// <summary>The field box of the subject shows the first field instead of nothing when the fields change.</summary>
    private void SelectFirstField()
    {
        if (SubjectFieldBox.SelectedIndex < 0 && SubjectFieldBox.Items.Count > 0)
        {
            SubjectFieldBox.SelectedIndex = 0;
        }
    }

    /// <summary>A click on a tab header (not on a page) changes the tab: the body is written first.</summary>
    private void OnTabsMouseDown(object sender, MouseButtonEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null && !ReferenceEquals(element, CorrespondenceTabs);
            element = (element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : null)
                ?? LogicalTreeHelper.GetParent(element))
        {
            if (element is TabItem)
            {
                MessageEditor.FlushHtml();
                return;
            }
        }
    }

    /// <summary>The window closes: the body is written first (the last draft is saved from it).</summary>
    private void OnClosing(object? sender, CancelEventArgs e) => MessageEditor.FlushHtml();

    /// <summary>Ctrl+Tab and Ctrl+PageUp/PageDown change the tab: the body is written first.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key is Key.Tab or Key.PageUp or Key.PageDown)
        {
            MessageEditor.FlushHtml();
        }
    }

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnRecipientsDrop(object sender, DragEventArgs e)
    {
        var files = DroppedFiles(e.Data);
        if (files.Count == 0)
        {
            return;
        }

        e.Handled = true;
        await ViewModel.Recipients.AddFilesAsync(files).ConfigureAwait(true);
    }

    private async void OnAttachmentsDrop(object sender, DragEventArgs e)
    {
        var files = DroppedFiles(e.Data);
        if (files.Count == 0)
        {
            return;
        }

        e.Handled = true;
        await ViewModel.Message.AddAttachmentFilesAsync(files).ConfigureAwait(true);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        Closing -= OnClosing;
        PreviewKeyDown -= OnPreviewKeyDown;
        CorrespondenceTabs.PreviewMouseLeftButtonDown -= OnTabsMouseDown;
        ViewModel.Recipients.Columns.CollectionChanged -= OnRecipientColumnsChanged;
        ViewModel.Message.InsertFieldRequested -= OnInsertFieldRequested;
        ViewModel.Recipients.SelectedItems = null;
        ViewModel.Message.SelectedAttachments = null;
    }
}
