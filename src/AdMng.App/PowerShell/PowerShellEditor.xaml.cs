using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using AdMng.App.Localization;
using AdMng.Core.PowerShell;
using AdMng.Presentation.Localization;
using AdMng.Presentation.PowerShell;

namespace AdMng.App.PowerShell;

/// <summary>
/// The line editor of the PowerShell window (track R, stage R1, decision D5; see the header of <c>PowerShellEditor.xaml</c> for how it
/// is built). It forwards keys to <see cref="PowerShellInputModel"/> - Enter runs (or inserts a line when the input is not complete),
/// Shift+Enter inserts a line, Tab completes, Up and Down walk through the lines and then the history, Ctrl+R searches the history,
/// Esc closes the list or the search, Ctrl+C copies a selection and otherwise stops the command (also at a secret question, whose box it
/// empties), Ctrl+L clears the window - and draws what
/// the model says: the text with the colours of its tokens, the list of completions under the caret, the line of the search. The text
/// lives in memory only; nothing of it is written anywhere.
/// </summary>
public partial class PowerShellEditor : UserControl
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(PowerShellInputModel), typeof(PowerShellEditor), new PropertyMetadata(null, OnModelChanged));

    public static readonly DependencyProperty SubmitCommandProperty = DependencyProperty.Register(
        nameof(SubmitCommand), typeof(ICommand), typeof(PowerShellEditor), new PropertyMetadata(null));

    public static readonly DependencyProperty SubmitSecretCommandProperty = DependencyProperty.Register(
        nameof(SubmitSecretCommand), typeof(ICommand), typeof(PowerShellEditor), new PropertyMetadata(null));

    public static readonly DependencyProperty StopCommandProperty = DependencyProperty.Register(
        nameof(StopCommand), typeof(ICommand), typeof(PowerShellEditor), new PropertyMetadata(null));

    public static readonly DependencyProperty ClearCommandProperty = DependencyProperty.Register(
        nameof(ClearCommand), typeof(ICommand), typeof(PowerShellEditor), new PropertyMetadata(null));

    public static readonly DependencyProperty PromptProperty = DependencyProperty.Register(
        nameof(Prompt), typeof(string), typeof(PowerShellEditor), new PropertyMetadata(PowerShellWindowViewModel.PromptText, (d, e) => ((PowerShellEditor)d).PromptText.Text = (string)e.NewValue));

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(PowerShellEditor), new PropertyMetadata(string.Empty, OnHintChanged));

    private const int MaxVisibleLines = 8;

    private static void OnHintChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (PowerShellEditor)d;
        editor.HintText.Text = (string)e.NewValue;
        editor.UpdateHint();
    }

    private bool _applying;
    private bool _layerQueued;
    /// <summary>The mode changed: the next text the model puts into the line forgets the undo of what was there (see <see cref="ForgetUndo"/>).</summary>
    private bool _forgetUndo;
    private PowerShellInputModel? _model;

    public PowerShellEditor()
    {
        InitializeComponent();
        InputBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => PlaceLayer()));
        InputBox.LostKeyboardFocus += (_, _) => _model?.CloseCompletion();
        SizeChanged += (_, _) => PlaceLayer();
        Loaded += (_, _) =>
        {
            SizeTheBox();
            RebuildLayer();
        };
    }

    /// <summary>The model the editor shows and edits (the view model's <c>Input</c>).</summary>
    public PowerShellInputModel? Model
    {
        get => (PowerShellInputModel?)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    /// <summary>Enter: run the command (or answer the question, take the completion or the line of the search, or start a new line).</summary>
    public ICommand? SubmitCommand
    {
        get => (ICommand?)GetValue(SubmitCommandProperty);
        set => SetValue(SubmitCommandProperty, value);
    }

    /// <summary>
    /// Enter at a secret question: executed with a copy of what the password box holds, a <see cref="System.Security.SecureString"/> the
    /// command takes over (audit W-08: the secret went through the model's text, a string per key, and stayed in the hidden box).
    /// </summary>
    public ICommand? SubmitSecretCommand
    {
        get => (ICommand?)GetValue(SubmitSecretCommandProperty);
        set => SetValue(SubmitSecretCommandProperty, value);
    }

    /// <summary>Ctrl+C with nothing selected, and Ctrl+C in the password box of a secret question.</summary>
    public ICommand? StopCommand
    {
        get => (ICommand?)GetValue(StopCommandProperty);
        set => SetValue(StopCommandProperty, value);
    }

    /// <summary>Ctrl+L.</summary>
    public ICommand? ClearCommand
    {
        get => (ICommand?)GetValue(ClearCommandProperty);
        set => SetValue(ClearCommandProperty, value);
    }

    /// <summary>What stands before the text: "PS&gt; ", or the label of a question.</summary>
    public string Prompt
    {
        get => (string)GetValue(PromptProperty);
        set => SetValue(PromptProperty, value);
    }

    /// <summary>The words in the empty editor.</summary>
    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    /// <summary>The box that holds the text (for the tests).</summary>
    internal TextBox Box => InputBox;

    /// <summary>The text under the box, as the tests read it: what is shown in colours.</summary>
    internal TextBlock Layer => HighlightLayer;

    internal PasswordBox Secret => SecretBox;

    internal ListBox Completions => CompletionList;

    internal System.Windows.Controls.Primitives.Popup CompletionHost => CompletionPopup;

    internal TextBlock Search => SearchText;

    /// <summary>The keyboard goes to the box that is shown: the command line, or the secret.</summary>
    public void FocusInput()
    {
        if (SecretBox.Visibility == Visibility.Visible)
        {
            SecretBox.Focus();
        }
        else
        {
            InputBox.Focus();
        }
    }

    // ---- The model --------------------------------------------------------------------------------------------------

    private static void OnModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((PowerShellEditor)d).AttachModel(e.NewValue as PowerShellInputModel);

    private void AttachModel(PowerShellInputModel? model)
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
        }

        _model = model;
        if (model is null)
        {
            return;
        }

        model.PropertyChanged += OnModelChanged;
        ApplyText();
        ApplyMode();
        ApplyCompletion();
        ApplySearch();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => OnModelChanged(sender, e));
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(PowerShellInputModel.Text):
                ApplyText();
                break;
            case nameof(PowerShellInputModel.CaretIndex):
                ApplyCaret();
                break;
            case nameof(PowerShellInputModel.Spans):
                RebuildLayer();
                break;
            case nameof(PowerShellInputModel.Mode):
                ApplyMode();
                break;
            case nameof(PowerShellInputModel.IsCompletionOpen):
            case nameof(PowerShellInputModel.Completions):
            case nameof(PowerShellInputModel.SelectedCompletionIndex):
                ApplyCompletion();
                break;
            case nameof(PowerShellInputModel.IsSearching):
            case nameof(PowerShellInputModel.SearchQuery):
            case nameof(PowerShellInputModel.SearchMatch):
                ApplySearch();
                break;
        }
    }

    /// <summary>The model changed the text (a line of the history, a completion, the end of a command): the box shows it.</summary>
    private void ApplyText()
    {
        if (_model is not { } model)
        {
            return;
        }

        if (model.IsSecret)
        {
            // The secret is in the password box only; the model holds no text meanwhile.
            return;
        }

        SyncBox(model);
        UpdateHint();
        RebuildLayer();
        SizeTheBox();
    }

    /// <summary>The command line shows the model's text and caret (also what was typed ahead and comes back after a question, audit W-09).</summary>
    private void SyncBox(PowerShellInputModel model)
    {
        if (PowerShellEditorText.ToModel(InputBox.Text) != model.Text)
        {
            _applying = true;
            try
            {
                InputBox.Text = model.Text;
                InputBox.CaretIndex = PowerShellEditorText.ToBoxIndex(InputBox.Text, model.CaretIndex);
            }
            finally
            {
                _applying = false;
            }

            if (model.Text.Length == 0)
            {
                ForgetUndo();
            }
            else if (_forgetUndo)
            {
                // The text of a new mode (what was typed ahead before a question): nothing before it can be undone into the line.
                _forgetUndo = false;
                ForgetUndo();
            }
        }
    }

    /// <summary>
    /// Empties the undo stack of the command line (audit W-notes-undo-stack). The box keeps an undo step for every text the model puts
    /// into it, so after Enter Ctrl+Z brought back the line that had run - also one the history keeps out because it holds a password
    /// in plain text - and after a question the answer. So the model's emptying of the line (a run, an answer) and its change of mode
    /// leave nothing to undo; a history line or a completion the model puts in can still be undone. Turning undo off empties the
    /// stack.
    /// </summary>
    private void ForgetUndo()
    {
        InputBox.IsUndoEnabled = false;
        InputBox.IsUndoEnabled = true;
    }

    private void ApplyCaret()
    {
        if (_model is not { IsSecret: false } model || _applying)
        {
            return;
        }

        var wanted = PowerShellEditorText.ToBoxIndex(InputBox.Text, model.CaretIndex);
        if (InputBox.CaretIndex != wanted)
        {
            _applying = true;
            try
            {
                InputBox.CaretIndex = wanted;
            }
            finally
            {
                _applying = false;
            }
        }
    }

    /// <summary>The model went to the answer of a question (plain or secret) or back to commands.</summary>
    private void ApplyMode()
    {
        if (_model is not { } model)
        {
            return;
        }

        var secret = model.IsSecret;
        var hadFocus = IsKeyboardFocusWithin;
        SecretBox.Visibility = secret ? Visibility.Visible : Visibility.Collapsed;
        InputBox.Visibility = secret ? Visibility.Collapsed : Visibility.Visible;
        HighlightLayer.Visibility = secret ? Visibility.Collapsed : Visibility.Visible;

        // The password box is empty in every mode but while a secret is typed (audit W-08: it kept the answer for the life of the
        // window), and the command line follows the model on every change of mode (it kept text the model no longer had). What the
        // line held before the change cannot be undone into it: the model sets the text of the new mode right after the mode, and
        // that set forgets the undo too.
        SecretBox.Clear();
        _forgetUndo = true;
        if (!secret)
        {
            SyncBox(model);
            RebuildLayer();
            SizeTheBox();
        }

        ForgetUndo();

        UpdateHint();
        if (hadFocus || secret)
        {
            _ = Dispatcher.InvokeAsync(FocusInput, DispatcherPriority.Input);
        }
    }

    private void UpdateHint() =>
        HintText.Visibility = _model is { IsSecret: false, IsSearching: false } && InputBox.Text.Length == 0 && Hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---- Edits of the operator ----------------------------------------------------------------------------------------

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_applying || _model is null)
        {
            return;
        }

        // The operator types: the text of the new mode is not coming any more (a later history line or completion stays undoable).
        _forgetUndo = false;
        _applying = true;
        try
        {
            _model.SetText(PowerShellEditorText.ToModel(InputBox.Text), PowerShellEditorText.ToModelIndex(InputBox.Text, InputBox.CaretIndex));
        }
        finally
        {
            _applying = false;
        }

        UpdateHint();
        RebuildLayer();
        SizeTheBox();
    }

    private void OnSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_applying || _model is null)
        {
            return;
        }

        // Only the caret moved: the model takes the new place (the same text changes nothing else).
        _applying = true;
        try
        {
            _model.SetText(_model.Text, PowerShellEditorText.ToModelIndex(InputBox.Text, InputBox.CaretIndex));
        }
        finally
        {
            _applying = false;
        }

        PositionCompletion();
    }

    /// <summary>Enter in the password box: a copy of the secret goes to the window, and the box is emptied.</summary>
    private void SubmitSecret()
    {
        var secret = SecretBox.SecurePassword.Copy();
        SecretBox.Clear();
        if (SubmitSecretCommand is { } command && command.CanExecute(secret))
        {
            command.Execute(secret);
        }
        else
        {
            secret.Dispose();
        }
    }

    // ---- Keys ---------------------------------------------------------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleKey(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// What a key does in the editor; true when the editor took it (the box must not). The modifiers are a parameter so that a test can
    /// press Ctrl+C without a keyboard.
    /// </summary>
    internal bool HandleKey(Key key, ModifierKeys modifiers)
    {
        if (_model is not { } model)
        {
            return false;
        }

        var control = modifiers.HasFlag(ModifierKeys.Control);
        var shift = modifiers.HasFlag(ModifierKeys.Shift);
        var alt = modifiers.HasFlag(ModifierKeys.Alt);
        switch (key)
        {
            case Key.Enter when shift && !control && !alt && !model.IsAnswering:
                // A new line at the caret, whatever the input is.
                InputBox.SelectedText = Environment.NewLine;
                InputBox.CaretIndex += InputBox.SelectionLength;
                InputBox.SelectionLength = 0;
                return true;
            case Key.Enter when !control && !alt && model.IsSecret:
                SubmitSecret();
                return true;
            case Key.Enter when !control && !alt:
                SubmitCommand?.Execute(null);
                return true;
            case Key.Tab when !control && !alt && !model.IsAnswering:
                if (shift)
                {
                    model.CompletePrevious();
                }
                else
                {
                    CompleteAsync();
                }

                return true;
            case Key.Space when control && !shift && !alt && !model.IsAnswering:
                CompleteAsync();
                return true;
            case Key.Escape when model.IsCompletionOpen:
                model.CloseCompletion();
                return true;
            case Key.Escape when model.IsSearching:
                model.CancelSearch();
                return true;
            case Key.Up when !control && !shift && !alt:
                return model.MoveUp();
            case Key.Down when !control && !shift && !alt:
                return model.MoveDown();
            case Key.R when control && !shift && !alt && !model.IsAnswering:
                model.BeginSearch();
                return true;
            case Key.Back when model.IsSearching:
                model.SetSearchQuery(model.SearchQuery.Length > 0 ? model.SearchQuery[..^1] : string.Empty);
                return true;
            case Key.L when control && !shift && !alt:
                ClearCommand?.Execute(null);
                return true;
            case Key.C when control && !shift && !alt && model.IsSecret:
                // At a secret question Ctrl+C is Stop too, as at a console (owner answer 11; the password box copies nothing): what
                // was typed of the secret is thrown away first, so nothing of it outlives the question.
                SecretBox.Clear();
                StopCommand?.Execute(null);
                return true;
            case Key.C when control && !shift && !alt && InputBox.SelectionLength == 0:
                // Ctrl+C copies a selection (the box does it); with nothing selected it is Stop, as at a console.
                StopCommand?.Execute(null);
                return true;
            default:
                return false;
        }
    }

    /// <summary>While the history is searched, what is typed is the search, not the text of the command.</summary>
    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_model is { IsSearching: true } model && !string.IsNullOrEmpty(e.Text))
        {
            model.SetSearchQuery(model.SearchQuery + e.Text);
            e.Handled = true;
        }
    }

    private async void CompleteAsync()
    {
        if (_model is not { } model)
        {
            return;
        }

        try
        {
            await model.CompleteAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A completion that fails (the engine is gone with the window) offers nothing; the text stays as it is.
        }
    }

    // ---- The list of completions and the search -------------------------------------------------------------------------

    private void ApplyCompletion()
    {
        if (_model is not { } model)
        {
            return;
        }

        if (!model.IsCompletionOpen)
        {
            CompletionPopup.IsOpen = false;
            return;
        }

        if (!ReferenceEquals(CompletionList.ItemsSource, model.Completions))
        {
            CompletionList.ItemsSource = model.Completions;
        }

        CompletionList.SelectedIndex = model.SelectedCompletionIndex;
        if (model.SelectedCompletionIndex >= 0 && model.SelectedCompletionIndex < model.Completions.Count)
        {
            CompletionList.ScrollIntoView(model.Completions[model.SelectedCompletionIndex]);
        }

        PositionCompletion();
        CompletionPopup.IsOpen = true;
    }

    /// <summary>The list stands under the caret.</summary>
    private void PositionCompletion()
    {
        if (!CompletionPopup.IsOpen && _model is not { IsCompletionOpen: true })
        {
            return;
        }

        var rect = InputBox.GetRectFromCharacterIndex(InputBox.CaretIndex);
        if (!rect.IsEmpty)
        {
            CompletionPopup.HorizontalOffset = Math.Max(0, rect.Left);
            CompletionPopup.VerticalOffset = rect.Bottom + 2;
        }
    }

    private void OnCompletionItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item && _model is { } model)
        {
            model.AcceptCompletionAt(CompletionList.ItemContainerGenerator.IndexFromContainer(item));
            FocusInput();
            e.Handled = true;
        }
    }

    private void ApplySearch()
    {
        if (_model is not { } model || !model.IsSearching)
        {
            SearchText.Visibility = Visibility.Collapsed;
            UpdateHint();
            return;
        }

        var localizer = TranslationSource.Localizer;
        var line = localizer.Format(L.PowerShell.SearchLabel, model.SearchQuery);
        SearchText.Text = model.SearchFailed
            ? line + " – " + localizer[L.PowerShell.SearchFailed]
            : model.SearchMatch is { } match ? line + " → " + match.Replace('\n', ' ') : line;
        SearchText.Visibility = Visibility.Visible;
        UpdateHint();
    }

    // ---- The coloured text ---------------------------------------------------------------------------------------------

    /// <summary>The theme's text colour of a kind of token (resource keys, so a switch of the theme or High Contrast recolours the text).</summary>
    internal static string BrushKeyOf(PowerShellSyntaxKind kind) => kind switch
    {
        PowerShellSyntaxKind.Command or PowerShellSyntaxKind.Keyword or PowerShellSyntaxKind.Type => "AccentTextFillColorPrimaryBrush",
        PowerShellSyntaxKind.Variable => "SystemFillColorSuccessBrush",
        PowerShellSyntaxKind.String => "SystemFillColorCautionBrush",
        PowerShellSyntaxKind.Number => "SystemFillColorAttentionBrush",
        PowerShellSyntaxKind.Parameter or PowerShellSyntaxKind.Operator or PowerShellSyntaxKind.Comment or PowerShellSyntaxKind.Member => "TextFillColorSecondaryBrush",
        _ => "TextFillColorPrimaryBrush",
    };

    private void RebuildLayer()
    {
        HighlightLayer.Inlines.Clear();
        if (_model is not { IsSecret: false } model)
        {
            return;
        }

        var text = model.Text;
        var position = 0;
        foreach (var span in model.Spans.OrderBy(span => span.Start))
        {
            // Spans that do not fit the text (they were made for another one) are left out.
            if (span.Start < position || span.Length <= 0 || span.Start + span.Length > text.Length)
            {
                continue;
            }

            if (span.Start > position)
            {
                AddRun(text[position..span.Start], PowerShellSyntaxKind.Other);
            }

            AddRun(text.Substring(span.Start, span.Length), span.Kind);
            position = span.Start + span.Length;
        }

        if (position < text.Length)
        {
            AddRun(text[position..], PowerShellSyntaxKind.Other);
        }

        PlaceLayer();
    }

    private void AddRun(string text, PowerShellSyntaxKind kind)
    {
        var run = new Run(text);
        run.SetResourceReference(TextElement.ForegroundProperty, BrushKeyOf(kind));
        HighlightLayer.Inlines.Add(run);
    }

    /// <summary>
    /// Puts the coloured text where the box draws its own: the first character of the box, whatever the padding, the scroll offset and
    /// the internals of the text host are (asked of the box after the layout, once; a second look is made when the layout changed).
    /// </summary>
    private void PlaceLayer()
    {
        if (_layerQueued)
        {
            return;
        }

        _layerQueued = true;
        _ = Dispatcher.InvokeAsync(
            () =>
            {
                _layerQueued = false;
                var origin = InputBox.GetRectFromCharacterIndex(0);
                HighlightLayer.Margin = !origin.IsEmpty && !double.IsInfinity(origin.X) && !double.IsInfinity(origin.Y)
                    ? new Thickness(origin.X, origin.Y, 0, 0)
                    : new Thickness(InputBox.Padding.Left, InputBox.Padding.Top, 0, 0);
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>The box is one line high, and grows with its lines up to <see cref="MaxVisibleLines"/>; past that it scrolls.</summary>
    private void SizeTheBox()
    {
        var line = Math.Ceiling(InputBox.FontFamily.LineSpacing * InputBox.FontSize);
        var padding = InputBox.Padding.Top + InputBox.Padding.Bottom;
        InputBox.MinHeight = line + padding;
        InputBox.MaxHeight = (line * MaxVisibleLines) + padding;
    }
}
