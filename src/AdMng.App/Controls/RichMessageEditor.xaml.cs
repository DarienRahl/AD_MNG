using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AdMng.App.Correspondence;
using AdMng.App.Localization;
using AdMng.Core.Messaging.Images;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Localization;

namespace AdMng.App.Controls;

/// <summary>
/// The message editor of Korespondencja (design note 9.2): <see cref="Html"/> is the body as the sanitized subset (two-way,
/// written <see cref="HtmlDelay"/> after the last change and at once when the editor loses the keyboard focus or
/// <see cref="FlushHtml"/> is called), <see cref="Host"/> the view model's paste and image services, <see cref="FieldChoices"/>
/// the <c>{{field}}</c> texts of "Wstaw pole".
/// </summary>
/// <remarks>
/// Pasting (Ctrl+V, the paste command) never takes the clipboard's own rich formats: formatted HTML goes through
/// <see cref="IRichEditorHost.PasteFormattedAsync"/> (the U1c importer: sanitizer, Word lists, Office images) and is
/// inserted at the caret; a bitmap alone becomes an image registered by <see cref="IRichEditorHost.AddImage"/>; anything
/// else is pasted as text. Only content copied from this editor in this process is pasted natively (formatting kept).
/// "Wklej bez formatowania" and Ctrl+Shift+V paste the clipboard's text.
/// </remarks>
public partial class RichMessageEditor : UserControl
{
    public static readonly DependencyProperty HtmlProperty = DependencyProperty.Register(
        nameof(Html), typeof(string), typeof(RichMessageEditor),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHtmlChanged));

    public static readonly DependencyProperty HostProperty = DependencyProperty.Register(
        nameof(Host), typeof(IRichEditorHost), typeof(RichMessageEditor), new PropertyMetadata(null, OnHostChanged));

    public static readonly DependencyProperty FieldChoicesProperty = DependencyProperty.Register(
        nameof(FieldChoices), typeof(IReadOnlyList<string>), typeof(RichMessageEditor), new PropertyMetadata(null, OnFieldChoicesChanged));

    /// <summary>The data format that marks content copied from an editor of this process (pasted natively).</summary>
    internal const string CopyMarkerFormat = "AdMng.RichMessageEditor";

    private static readonly string ProcessMarker = Guid.NewGuid().ToString("N");
    private static readonly IReadOnlyDictionary<string, MessageImage> NoImages = new Dictionary<string, MessageImage>();

    private readonly DispatcherTimer _htmlTimer;
    private string _lastHtml = string.Empty;
    private string _loadedHtml = string.Empty;
    private bool _loading;
    private bool _dirty;
    private bool _pasting;

    public RichMessageEditor()
    {
        InitializeComponent();
        _htmlTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = HtmlDelay };
        _htmlTimer.Tick += (_, _) => FlushHtml();
        LoadDocument(string.Empty);
        MessageEditorBox.TextChanged += OnTextChanged;
        MessageEditorBox.SelectionChanged += (_, _) => UpdateToolbar();
        MessageEditorBox.LostKeyboardFocus += (_, _) => FlushHtml();
        DataObject.AddPastingHandler(MessageEditorBox, OnPasting);
        DataObject.AddCopyingHandler(MessageEditorBox, OnCopying);
        MessageEditorBox.CommandBindings.Add(new CommandBinding(PastePlainCommand, (_, _) => PastePlain()));
        MessageEditorBox.InputBindings.Add(new KeyBinding(PastePlainCommand, Key.V, ModifierKeys.Control | ModifierKeys.Shift));
        InsertFieldButton.IsEnabled = false;
        Loaded += OnLoaded;
        Unloaded += (_, _) => FlushHtml();
    }

    /// <summary>"Wklej bez formatowania" (Ctrl+Shift+V in the editor).</summary>
    public static RoutedCommand PastePlainCommand { get; } = new(nameof(PastePlainCommand), typeof(RichMessageEditor));

    /// <summary>How long typing pauses before <see cref="Html"/> is written (300 ms).</summary>
    public static TimeSpan HtmlDelay { get; } = TimeSpan.FromMilliseconds(300);

    /// <summary>The body: an HTML fragment of the sanitized subset with <c>cid:</c> images ("" when empty).</summary>
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

    /// <summary>Tests: answers the link prompt instead of the dialog (null: cancelled).</summary>
    internal Func<string, string?>? LinkPrompt { get; set; }

    /// <summary>Writes pending changes of the document to <see cref="Html"/> now (before the body is read).</summary>
    public void FlushHtml()
    {
        _htmlTimer.Stop();
        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        var html = FlowDocumentHtml.ToHtml(MessageEditorBox.Document, ContentIdFor);
        if (string.Equals(html, _loadedHtml, StringComparison.Ordinal))
        {
            // Nothing changed since the body was loaded (it may differ from Html only in its normalization).
            return;
        }

        _loadedHtml = html;
        _lastHtml = html;
        if (!string.Equals(Html, html, StringComparison.Ordinal))
        {
            SetCurrentValue(HtmlProperty, html);
        }
    }

    /// <summary>Inserts <paramref name="field"/> (a <c>{{field}}</c> text) at the caret, replacing the selection.</summary>
    public void InsertField(string field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.Length == 0)
        {
            return;
        }

        var selection = MessageEditorBox.Selection;
        selection.Text = field;
        selection.Select(selection.End, selection.End);
        MessageEditorBox.Focus();
    }

    /// <summary>Pastes <paramref name="data"/> as the paste command would (tests call it with their own data).</summary>
    internal async Task PasteAsync(IDataObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (_pasting)
        {
            return;
        }

        _pasting = true;
        try
        {
            var host = Host;
            if (host is not null && HasFormat(data, DataFormats.Html))
            {
                var paste = await host.PasteFormattedAsync().ConfigureAwait(true);
                if (paste is not null)
                {
                    InsertHtml(paste, host);
                }

                return;
            }

            if (host is not null && BitmapOf(data) is { } png)
            {
                if (host.AddImage(png, null) is { } image)
                {
                    InsertImage(image);
                }

                return;
            }

            if (TextOf(data) is { Length: > 0 } text)
            {
                InsertPlainText(text);
            }
        }
        catch (ExternalException)
        {
            ReportError(TranslationSource.Localizer[L.Ui.ClipboardBusy]);
        }
        finally
        {
            _pasting = false;
        }
    }

    /// <summary>Makes the selection a link to <paramref name="address"/> (http(s), mailto, tel, an e-mail address, a
    /// domain, or a <c>{{field}}</c>); an empty address removes the link at the caret. False when nothing changed (the
    /// reason went to the host).</summary>
    internal bool ApplyLink(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var selection = MessageEditorBox.Selection;
        var existing = HyperlinkAt(selection.Start);
        var text = address.Trim();
        if (text.Length == 0)
        {
            if (existing is null)
            {
                return false;
            }

            Unwrap(existing);
            return true;
        }

        if (LinkUri(text) is not { } uri)
        {
            ReportError(TranslationSource.Localizer.Format(L.CorrespondenceApp.InvalidLink, text));
            return false;
        }

        if (existing is not null && (selection.IsEmpty || (selection.Start.CompareTo(existing.ContentStart) >= 0 && selection.End.CompareTo(existing.ContentEnd) <= 0)))
        {
            existing.NavigateUri = uri;
            return true;
        }

        if (selection.IsEmpty)
        {
            ReportError(TranslationSource.Localizer[L.CorrespondenceApp.SelectTextForLink]);
            return false;
        }

        var start = selection.Start;
        var end = selection.End;
        if (start.Paragraph is { } first && end.Paragraph is { } last && first != last && end.CompareTo(last.ContentStart) <= 0)
        {
            // A whole paragraph selected with its end: the link ends with the paragraph's text.
            end = first.ContentEnd;
        }

        if (start.Paragraph is null || start.Paragraph != end.Paragraph)
        {
            ReportError(TranslationSource.Localizer[L.CorrespondenceApp.LinkInOneParagraph]);
            return false;
        }

        try
        {
            _ = new Hyperlink(start, end) { NavigateUri = uri, Foreground = FlowDocumentHtml.LinkBrush };
            return true;
        }
        catch (InvalidOperationException)
        {
            ReportError(TranslationSource.Localizer[L.CorrespondenceApp.LinkInOneParagraph]);
            return false;
        }
    }

    /// <summary>The address a link of the editor may lead to, or null.</summary>
    internal static Uri? LinkUri(string text)
    {
        var value = text.Trim();
        if (value.Length == 0 || value.Any(char.IsControl))
        {
            return null;
        }

        if (value.StartsWith("{{", StringComparison.Ordinal) && value.EndsWith("}}", StringComparison.Ordinal) && value.Length > 4)
        {
            return Uri.TryCreate(value, UriKind.Relative, out var field) ? field : null;
        }

        if (value.Any(char.IsWhiteSpace))
        {
            return null;
        }

        var colon = value.IndexOf(':', StringComparison.Ordinal);
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        var hasScheme = colon > 0 && (slash < 0 || colon < slash);
        if (!hasScheme)
        {
            if (value.Contains('@', StringComparison.Ordinal) && !value.Contains('/', StringComparison.Ordinal))
            {
                value = "mailto:" + value;
            }
            else if (value.Contains('.', StringComparison.Ordinal) && !value.StartsWith('.') && !value.StartsWith('/'))
            {
                value = "https://" + value;
            }
            else
            {
                return null;
            }
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.Scheme switch
        {
            "http" or "https" => string.IsNullOrEmpty(uri.Host) ? null : uri,
            "mailto" or "tel" => value.Length > uri.Scheme.Length + 1 ? uri : null,
            _ => null,
        };
    }

    // ---- Html <-> document ---------------------------------------------------------------------------------------------

    private static void OnHtmlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (RichMessageEditor)d;
        var html = e.NewValue as string ?? string.Empty;
        if (!string.Equals(html, editor._lastHtml, StringComparison.Ordinal))
        {
            editor.LoadDocument(html);
        }
    }

    private static void OnHostChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (RichMessageEditor)d;
        editor.FlushHtml();
        if (!string.IsNullOrEmpty(editor.Html))
        {
            // The images of the body are the host's: show them.
            editor.LoadDocument(editor.Html);
        }
    }

    private static void OnFieldChoicesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (RichMessageEditor)d;
        var choices = e.NewValue as IReadOnlyList<string>;
        var selected = editor.FieldBox.SelectedItem as string;
        editor.FieldBox.ItemsSource = choices;
        if (selected is not null && choices?.Contains(selected) == true)
        {
            editor.FieldBox.SelectedItem = selected;
        }
        else if (choices is { Count: > 0 })
        {
            editor.FieldBox.SelectedIndex = 0;
        }

        editor.InsertFieldButton.IsEnabled = choices is { Count: > 0 };
    }

    private void LoadDocument(string html)
    {
        _htmlTimer.Stop();
        _loading = true;
        try
        {
            MessageEditorBox.Document = FlowDocumentHtml.ToDocument(html, Host?.Images ?? NoImages);
        }
        finally
        {
            _loading = false;
        }

        _dirty = false;
        _lastHtml = html;
        _loadedHtml = FlowDocumentHtml.ToHtml(MessageEditorBox.Document, ContentIdFor);
        UpdateToolbar();
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateToolbar();
        if (_loading)
        {
            return;
        }

        _dirty = true;
        _htmlTimer.Stop();
        _htmlTimer.Start();
    }

    /// <summary>The content id of an image of the document: its tag when the host knows it, else the bitmap registered
    /// with the host as a new image (copied within the editor, dropped); null when the host refused it.</summary>
    private string? ContentIdFor(Image image)
    {
        var id = FlowDocumentHtml.ContentIdOf(image);
        var host = Host;
        if (host is null || (id is not null && host.Images.ContainsKey(id)) || image.Source is not BitmapSource bitmap)
        {
            return id;
        }

        byte[] png;
        try
        {
            png = FlowDocumentHtml.EncodePng(bitmap);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException or COMException)
        {
            return null;
        }

        if (host.AddImage(png, "obraz.png") is not { } added)
        {
            return null;
        }

        image.Tag = added.ContentId;
        return added.ContentId;
    }

    // ---- toolbar -------------------------------------------------------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // The spelling of the operator's language when Windows has it (pl-PL), else no marks.
            SpellCheck.SetIsEnabled(MessageEditorBox, true);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            // No spelling service: the editor works without it.
        }
    }

    private void OnBoldClick(object sender, RoutedEventArgs e) => Execute(EditingCommands.ToggleBold);

    private void OnItalicClick(object sender, RoutedEventArgs e) => Execute(EditingCommands.ToggleItalic);

    private void OnUnderlineClick(object sender, RoutedEventArgs e) => Execute(EditingCommands.ToggleUnderline);

    private void OnBulletsClick(object sender, RoutedEventArgs e) => Execute(EditingCommands.ToggleBullets);

    private void OnNumberingClick(object sender, RoutedEventArgs e) => Execute(EditingCommands.ToggleNumbering);

    private void OnLinkClick(object sender, RoutedEventArgs e) => InsertLink();

    private void OnClearFormattingClick(object sender, RoutedEventArgs e) => ClearFormatting();

    private void OnPastePlainClick(object sender, RoutedEventArgs e) => PastePlain();

    private void OnInsertFieldClick(object sender, RoutedEventArgs e)
    {
        if (FieldBox.SelectedItem is string field)
        {
            InsertField(field);
        }
    }

    private void Execute(RoutedCommand command)
    {
        command.Execute(null, MessageEditorBox);
        MessageEditorBox.Focus();
        UpdateToolbar();
    }

    /// <summary>B / I / U show the formatting at the caret or of the whole selection.</summary>
    private void UpdateToolbar()
    {
        var selection = MessageEditorBox.Selection;
        BoldButton.IsChecked = selection.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight weight && weight.ToOpenTypeWeight() >= 600;
        ItalicButton.IsChecked = selection.GetPropertyValue(TextElement.FontStyleProperty) is FontStyle style
            && (style == FontStyles.Italic || style == FontStyles.Oblique);
        UnderlineButton.IsChecked = selection.GetPropertyValue(Inline.TextDecorationsProperty) is TextDecorationCollection decorations
            && decorations.Any(d => d.Location == TextDecorationLocation.Underline);
    }

    private void InsertLink()
    {
        var selection = MessageEditorBox.Selection;
        var existing = HyperlinkAt(selection.Start);
        if (selection.IsEmpty && existing is null)
        {
            ReportError(TranslationSource.Localizer[L.CorrespondenceApp.SelectTextForLink]);
            return;
        }

        var address = (LinkPrompt ?? PromptForLink)(existing?.NavigateUri?.OriginalString ?? "https://");
        if (address is not null)
        {
            ApplyLink(address);
        }

        MessageEditorBox.Focus();
    }

    private string? PromptForLink(string initial)
    {
        var localizer = TranslationSource.Localizer;
        var prompt = new TextPromptViewModel(localizer[L.CorrespondenceApp.LinkTitle], localizer[L.CorrespondenceApp.LinkMessage], initial, multiline: false);
        var window = new TextPromptWindow(prompt);
        if (Window.GetWindow(this) is { IsLoaded: true } owner)
        {
            window.Owner = owner;
        }

        window.ShowDialog();
        return prompt.Result;
    }

    /// <summary>MailerWPiA: the selection back to the body font and size, without character formatting.</summary>
    private void ClearFormatting()
    {
        var selection = MessageEditorBox.Selection;
        if (selection.IsEmpty)
        {
            ReportError(TranslationSource.Localizer[L.CorrespondenceApp.SelectTextToClear]);
            return;
        }

        selection.ClearAllProperties();
        selection.ApplyPropertyValue(TextElement.FontFamilyProperty, new FontFamily(FlowDocumentHtml.FontFamilyName));
        selection.ApplyPropertyValue(TextElement.FontSizeProperty, FlowDocumentHtml.DefaultFontSize);
        MessageEditorBox.Focus();
        UpdateToolbar();
    }

    private void PastePlain()
    {
        string? text;
        try
        {
            text = Clipboard.ContainsText(TextDataFormat.UnicodeText) ? Clipboard.GetText(TextDataFormat.UnicodeText) : null;
        }
        catch (ExternalException)
        {
            ReportError(TranslationSource.Localizer[L.Ui.ClipboardBusy]);
            return;
        }

        if (string.IsNullOrEmpty(text))
        {
            ReportError(TranslationSource.Localizer[L.CorrespondenceApp.ClipboardEmpty]);
            return;
        }

        InsertPlainText(text);
    }

    // ---- paste ---------------------------------------------------------------------------------------------------------

    private void OnCopying(object sender, DataObjectCopyingEventArgs e) => e.DataObject.SetData(CopyMarkerFormat, ProcessMarker);

    private void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        var data = e.DataObject;
        if (IsOwnContent(data))
        {
            // Copied or dragged within an editor of this process: WPF's own paste keeps the formatting.
            if (HasFormat(data, DataFormats.XamlPackage))
            {
                e.FormatToApply = DataFormats.XamlPackage;
            }

            return;
        }

        if (e.IsDragDrop)
        {
            // Dropped from another program: its text where it was dropped, never its formatting.
            if (HasFormat(data, DataFormats.UnicodeText))
            {
                e.FormatToApply = DataFormats.UnicodeText;
            }
            else
            {
                e.CancelCommand();
            }

            return;
        }

        e.CancelCommand();
        PasteDetached(data);
    }

    /// <summary>The paste of the command, continued after the event (errors reach the application's handler).</summary>
    private async void PasteDetached(IDataObject data) => await PasteAsync(data).ConfigureAwait(true);

    private static bool IsOwnContent(IDataObject data)
    {
        try
        {
            return data.GetDataPresent(CopyMarkerFormat)
                && data.GetData(CopyMarkerFormat) is string marker && string.Equals(marker, ProcessMarker, StringComparison.Ordinal)
                && (data.GetDataPresent(DataFormats.XamlPackage) || data.GetDataPresent(DataFormats.Xaml));
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    private static bool HasFormat(IDataObject data, string format)
    {
        try
        {
            return data.GetDataPresent(format);
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    /// <summary>PNG bytes of a bitmap on the clipboard (Office's PNG, else the bitmap encoded), or null.</summary>
    private static byte[]? BitmapOf(IDataObject data)
    {
        if (HasFormat(data, "PNG") && data.GetData("PNG") is MemoryStream png && png.Length > 0)
        {
            return png.ToArray();
        }

        if (HasFormat(data, DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            try
            {
                return FlowDocumentHtml.EncodePng(bitmap);
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException or COMException)
            {
                return null;
            }
        }

        return null;
    }

    private static string? TextOf(IDataObject data) =>
        HasFormat(data, DataFormats.UnicodeText) ? data.GetData(DataFormats.UnicodeText) as string : null;

    private void InsertHtml(RichPaste paste, IRichEditorHost host)
    {
        var images = new Dictionary<string, MessageImage>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, image) in host.Images)
        {
            images.TryAdd(id, image);
        }

        foreach (var image in paste.Images)
        {
            // The pasted images are the body's from now on (the host validates them; a refused one is left out).
            if (!images.ContainsKey(image.ContentId) && host.AddImage(image.Data, image.FileName) is { } added)
            {
                images.TryAdd(added.ContentId, added);
            }
        }

        InsertFragment(FlowDocumentHtml.ToDocument(paste.Html, images));
    }

    private void InsertImage(MessageImage image)
    {
        var fragment = FlowDocumentHtml.NewDocument();
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new InlineUIContainer(FlowDocumentHtml.CreateImage(image)) { BaselineAlignment = BaselineAlignment.Baseline });
        fragment.Blocks.Add(paragraph);
        InsertFragment(fragment);
    }

    private void InsertPlainText(string text)
    {
        var selection = MessageEditorBox.Selection;
        selection.Text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "\r\n", StringComparison.Ordinal);
        selection.Select(selection.End, selection.End);
        MessageEditorBox.Focus();
    }

    /// <summary>
    /// Inserts the blocks of <paramref name="fragment"/> at the caret (the selection replaced): one paragraph joins the
    /// text where the caret is; several blocks split the paragraph there (empty halves are dropped).
    /// </summary>
    private void InsertFragment(FlowDocument fragment)
    {
        var blocks = fragment.Blocks.ToList();
        if (blocks.Count == 0 || (blocks is [Paragraph { Inlines.Count: 0 }]))
        {
            return;
        }

        var box = MessageEditorBox;
        box.BeginChange();
        try
        {
            if (!box.Selection.IsEmpty)
            {
                box.Selection.Text = string.Empty;
            }

            var position = box.Selection.Start.GetInsertionPosition(LogicalDirection.Forward);
            if (HyperlinkAt(position) is { } link && (blocks.Count > 1 || (blocks[0] is Paragraph pasted && ContainsHyperlink(pasted))))
            {
                // Links do not nest, and a paragraph break must not split one: insert after the link.
                position = link.ElementEnd.GetInsertionPosition(LogicalDirection.Forward);
            }

            TextPointer caret;
            try
            {
                caret = blocks is [Paragraph single] ? InsertInlines(position, single) : InsertBlocks(position, fragment, blocks);
            }
            catch (InvalidOperationException)
            {
                // A position WPF cannot split (an embedded element): the content goes to the end of the body instead.
                foreach (var block in fragment.Blocks.ToList())
                {
                    fragment.Blocks.Remove(block);
                    box.Document.Blocks.Add(block);
                }

                caret = box.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
            }

            box.Selection.Select(caret, caret);
        }
        finally
        {
            box.EndChange();
        }

        box.Focus();
    }

    /// <summary>The inlines of <paramref name="source"/> in a span at <paramref name="position"/>; when WPF refuses the
    /// position they are back in <paramref name="source"/> and the exception is rethrown.</summary>
    private static TextPointer InsertInlines(TextPointer position, Paragraph source)
    {
        var inlines = source.Inlines.ToList();
        source.Inlines.Clear();
        Span? span = null;
        try
        {
            span = new Span(inlines[0], position);
            foreach (var inline in inlines.Skip(1))
            {
                span.Inlines.Add(inline);
            }
        }
        catch (InvalidOperationException)
        {
            span?.SiblingInlines?.Remove(span);
            foreach (var inline in inlines)
            {
                inline.SiblingInlines?.Remove(inline);
                source.Inlines.Add(inline);
            }

            throw;
        }

        return span.ElementEnd.GetInsertionPosition(LogicalDirection.Forward);
    }

    private TextPointer InsertBlocks(TextPointer position, FlowDocument fragment, IReadOnlyList<Block> blocks)
    {
        var document = MessageEditorBox.Document;
        if (position.Paragraph is null)
        {
            foreach (var block in blocks)
            {
                fragment.Blocks.Remove(block);
                document.Blocks.Add(block);
            }

            return document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
        }

        var first = position.Paragraph;
        var second = position.InsertParagraphBreak().Paragraph;
        var siblings = first.SiblingBlocks;
        Block anchor = first;
        foreach (var block in blocks)
        {
            fragment.Blocks.Remove(block);
            siblings.InsertAfter(anchor, block);
            anchor = block;
        }

        if (IsEmpty(first))
        {
            siblings.Remove(first);
        }

        if (second is not null && IsEmpty(second) && anchor is Paragraph)
        {
            siblings.Remove(second);
            second = null;
        }

        return (second?.ContentStart ?? anchor.ElementEnd).GetInsertionPosition(LogicalDirection.Forward);
    }

    private static bool IsEmpty(Paragraph paragraph) =>
        paragraph.Inlines.Count == 0
        || (string.IsNullOrEmpty(new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text) && !ContainsEmbedded(paragraph.Inlines));

    private static bool ContainsEmbedded(InlineCollection inlines) =>
        inlines.Any(i => i is InlineUIContainer or LineBreak || (i is Span span && ContainsEmbedded(span.Inlines)));

    private static bool ContainsHyperlink(Paragraph paragraph) => ContainsHyperlink(paragraph.Inlines);

    private static bool ContainsHyperlink(InlineCollection inlines) =>
        inlines.Any(i => i is Hyperlink || (i is Span span && ContainsHyperlink(span.Inlines)));

    private static Hyperlink? HyperlinkAt(TextPointer position)
    {
        for (var node = position.Parent; node is TextElement element; node = element.Parent)
        {
            if (element is Hyperlink hyperlink)
            {
                return hyperlink;
            }
        }

        return null;
    }

    private static void Unwrap(Hyperlink link)
    {
        var siblings = link.SiblingInlines;
        if (siblings is null)
        {
            return;
        }

        foreach (var inline in link.Inlines.ToList())
        {
            link.Inlines.Remove(inline);
            siblings.InsertBefore(link, inline);
        }

        siblings.Remove(link);
    }

    private void ReportError(string text) => Host?.ReportEditorError(text);
}
