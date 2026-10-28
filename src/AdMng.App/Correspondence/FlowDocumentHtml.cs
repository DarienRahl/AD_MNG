using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AdMng.Core.Messaging.Html;
using AdMng.Core.Messaging.Images;
using AngleSharp.Html.Parser;
using HtmlElement = AngleSharp.Dom.IElement;
using HtmlNode = AngleSharp.Dom.INode;
using HtmlText = AngleSharp.Dom.IText;

namespace AdMng.App.Correspondence;

/// <summary>
/// The HTML round trip of the Korespondencja editor (design note 9.2) for the sanitized subset: paragraphs, alignment,
/// lists, tables, runs (bold, italic, underline/strike, colour, background, size, font), links, line breaks and
/// <c>cid:</c> images. <see cref="ToHtml"/> is MailerWPiA's <c>FromDocument</c> (inline styles only, so webmail that drops
/// <c>&lt;style&gt;</c> shows the same message); its output is re-sanitized by the U1c sanitizer (fragment, the written
/// images as <c>InlineImageIds</c>, <c>{{field}}</c> links allowed). <see cref="ToDocument"/> is the inverse for that
/// subset (the input is sanitized first, anything else becomes text); <c>ToDocument(ToHtml(document))</c> gives the same
/// HTML again. Call on the UI thread (the documents are WPF objects).
/// </summary>
public static partial class FlowDocumentHtml
{
    /// <summary>The body font of the editor and the messages (other fonts are written per run).</summary>
    public const string FontFamilyName = "Calibri";

    /// <summary>The body size in points (MailerWPiA's 11 pt; other sizes are written per run).</summary>
    public const double BodyPoints = 11;

    /// <summary>The body size in device-independent pixels (11 pt).</summary>
    public const double DefaultFontSize = BodyPoints * 96 / 72;

    /// <summary><see cref="FrameworkElement.Tag"/> of the line of a horizontal rule (<c>&lt;hr&gt;</c>).</summary>
    public const string HorizontalRuleTag = "AdMng.HorizontalRule";

    /// <summary>The space below a paragraph, a list and a table: 8 pt (MailerWPiA).</summary>
    internal const double BlockSpacing = 8 * 96.0 / 72;

    internal const string BlockMargin = "margin:0 0 8pt 0";

    private const double CellPadding = 5;

    /// <summary>MailerWPiA's link colour (#065FD5); link text in it is written without a colour of its own.</summary>
    internal static readonly Color LinkColor = Color.FromRgb(0x06, 0x5F, 0xD5);

    internal static readonly Color CellBorderColor = Color.FromRgb(0xBF, 0xBF, 0xBF);

    private static readonly Color RuleColor = Color.FromRgb(0xA6, 0xA6, 0xA6);

    private static readonly HashSet<string> GenericFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "serif", "sans-serif", "monospace", "cursive", "fantasy", "system-ui",
    };

    private static readonly HashSet<string> ParagraphElements = new(StringComparer.Ordinal) { "p", "h1", "h2", "h3", "h4", "h5", "h6", "pre" };

    private static readonly HashSet<string> BlockContainers = new(StringComparer.Ordinal)
    {
        "div", "blockquote", "center", "dl", "dt", "dd", "caption", "address", "li", "tr", "td", "th", "thead", "tbody", "tfoot",
        "colgroup", "col", "figure", "figcaption", "section", "article", "header", "footer", "main", "nav", "aside",
    };

    /// <summary>The link brush of the editor (frozen).</summary>
    public static Brush LinkBrush { get; } = Frozen(new SolidColorBrush(LinkColor));

    /// <summary>
    /// The document as an HTML fragment of the sanitized subset; <paramref name="contentIdOf"/> gives an image's content
    /// id (null: the image is left out). An empty document (only white space) gives an empty string.
    /// </summary>
    public static string ToHtml(FlowDocument document, Func<Image, string?> contentIdOf)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(contentIdOf);
        if (!HasContent(document.Blocks))
        {
            return string.Empty;
        }

        var writer = new HtmlWriter(contentIdOf);
        writer.WriteBlocks(document.Blocks, listDepth: 0);
        return Sanitize(writer.Html, writer.ImageIds);
    }

    /// <summary>
    /// The inverse of <see cref="ToHtml"/> for the sanitized subset (<c>p</c>, <c>div</c>, <c>br</c>, <c>b/strong</c>,
    /// <c>i/em</c>, <c>u</c>, <c>s/strike</c>, <c>span</c> and <c>font</c> with their styles, <c>a</c>, nested
    /// <c>ul/ol/li</c>, <c>table/tr/td/th</c> with spans, <c>h1–h6</c>, <c>hr</c>); <c>img cid:</c> becomes an inline
    /// image tagged with its content id (unknown ids are dropped). Anything else becomes text.
    /// </summary>
    public static FlowDocument ToDocument(string html, IReadOnlyDictionary<string, MessageImage> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        var document = NewDocument();
        var known = new Dictionary<string, MessageImage>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, image) in images)
        {
            if (!string.IsNullOrEmpty(id) && image is not null)
            {
                known.TryAdd(id, image);
            }
        }

        if (!string.IsNullOrWhiteSpace(html))
        {
            var sanitized = Sanitize(html, known.Keys);
            var parsed = new HtmlParser().ParseDocument("<!DOCTYPE html><html><head></head><body>" + sanitized + "</body></html>");
            if (parsed.Body is { } body)
            {
                var builder = new DocumentBuilder(document.Blocks, known);
                builder.AppendChildren(body, TextFormat.Default);
                builder.Flush();
            }
        }

        if (document.Blocks.Count == 0)
        {
            document.Blocks.Add(new Paragraph());
        }

        return document;
    }

    /// <summary>An empty document with the message defaults: Calibri 11 pt, black text, left aligned.</summary>
    public static FlowDocument NewDocument() => new()
    {
        FontFamily = new FontFamily(FontFamilyName),
        FontSize = DefaultFontSize,
        Foreground = Brushes.Black,
        TextAlignment = TextAlignment.Left,
        PagePadding = new Thickness(4),
    };

    /// <summary>
    /// The editor's image of <paramref name="image"/>: decoded from memory, tagged with its content id, sized like the
    /// <c>&lt;img&gt;</c> (else the image's pixels) and named by its alternative text.
    /// </summary>
    public static Image CreateImage(MessageImage image, double? width = null, double? height = null, string? alternativeText = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        var source = Decode(image.Data);
        double naturalWidth = image.Width > 0 ? image.Width : source?.PixelWidth ?? 0;
        double naturalHeight = image.Height > 0 ? image.Height : source?.PixelHeight ?? 0;
        if (width is > 0 && height is not > 0 && naturalWidth > 0)
        {
            height = Math.Round(width.Value * naturalHeight / naturalWidth);
        }
        else if (height is > 0 && width is not > 0 && naturalHeight > 0)
        {
            width = Math.Round(height.Value * naturalWidth / naturalHeight);
        }

        var control = new Image
        {
            Source = source,
            Stretch = Stretch.Fill,
            Tag = image.ContentId,
            SnapsToDevicePixels = true,
        };
        var w = width is > 0 ? width.Value : naturalWidth;
        var h = height is > 0 ? height.Value : naturalHeight;
        if (w > 0 && h > 0)
        {
            control.Width = w;
            control.Height = h;
        }

        if (!string.IsNullOrWhiteSpace(alternativeText))
        {
            AutomationProperties.SetName(control, alternativeText);
        }

        RenderOptions.SetBitmapScalingMode(control, BitmapScalingMode.HighQuality);
        return control;
    }

    /// <summary>The content id an image of the editor is tagged with, or null.</summary>
    public static string? ContentIdOf(Image image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.Tag is string id && id.Length > 0 ? id : null;
    }

    /// <summary>True for the line of a horizontal rule (<see cref="HorizontalRuleTag"/>).</summary>
    public static bool IsHorizontalRule(BlockUIContainer block) =>
        block?.Child is FrameworkElement { Tag: HorizontalRuleTag };

    /// <summary>The U1c sanitizer over a fragment of the editor: the given images, <c>{{field}}</c> links allowed.</summary>
    internal static string Sanitize(string html, IEnumerable<string> imageIds) =>
        MessageHtmlSanitizer.Sanitize(html, new HtmlSanitizeOptions
        {
            Mode = HtmlSanitizeMode.Fragment,
            InlineImageIds = new HashSet<string>(imageIds, StringComparer.OrdinalIgnoreCase),
            AllowFieldUrls = true,
        }).Html;

    /// <summary>A bitmap decoded from memory (frozen; GIF: its first frame), or null when WPF cannot decode it.</summary>
    internal static BitmapSource? Decode(byte[] data)
    {
        if (data is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.StreamSource = new MemoryStream(data, writable: false);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or IOException
            or System.Runtime.InteropServices.COMException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>PNG bytes of a bitmap (an image pasted or dropped without its original bytes).</summary>
    internal static byte[] EncodePng(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static bool HasContent(IEnumerable<Block> blocks) => blocks.Any(block => block switch
    {
        Paragraph paragraph => HasContent(paragraph.Inlines),
        List list => list.ListItems.Count > 0,
        Table table => table.RowGroups.Any(g => g.Rows.Count > 0),
        Section section => HasContent(section.Blocks),
        BlockUIContainer container => container.Child is not null,
        _ => false,
    });

    private static bool HasContent(InlineCollection inlines) => inlines.Any(inline => inline switch
    {
        Run run => run.Text.Any(c => !char.IsWhiteSpace(c)),
        InlineUIContainer container => container.Child is not null,
        Span span => HasContent(span.Inlines),
        _ => false,
    });

    /// <summary>The deepest last inline of a collection (inside spans and links).</summary>
    private static Inline? LastInline(InlineCollection inlines)
    {
        var last = inlines.LastInline;
        while (last is Span { Inlines.LastInline: { } nested })
        {
            last = nested;
        }

        return last;
    }

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static string Hex(Color color) => string.Create(CultureInfo.InvariantCulture, $"#{color.R:X2}{color.G:X2}{color.B:X2}");

    private static string Escape(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

    [GeneratedRegex("  +", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceRun();

    [GeneratedRegex(@"[ \t\n\r\f]+", RegexOptions.CultureInvariant)]
    private static partial Regex CollapsibleWhiteSpace();

    // ---- FlowDocument → HTML (MailerWPiA FromDocument) ---------------------------------------------------------------

    /// <summary>Writes blocks and inlines as HTML with inline styles (the sanitizer normalizes the result).</summary>
    private sealed class HtmlWriter(Func<Image, string?> contentIdOf)
    {
        private readonly StringBuilder _html = new();
        private readonly HashSet<string> _imageIds = new(StringComparer.OrdinalIgnoreCase);

        public string Html => _html.ToString();

        public IReadOnlyCollection<string> ImageIds => _imageIds;

        public void WriteBlocks(IEnumerable<Block> blocks, int listDepth)
        {
            foreach (var block in blocks)
            {
                WriteBlock(block, listDepth);
            }
        }

        private void WriteBlock(Block block, int listDepth)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    var content = Inlines(paragraph);
                    _html.Append("<p style=\"").Append(BlockMargin).Append(AlignCss(paragraph.TextAlignment)).Append("\">")
                        .Append(string.IsNullOrWhiteSpace(content) ? "&nbsp;" : content).Append("</p>");
                    break;
                case List list:
                    WriteList(list, listDepth);
                    break;
                case Table table:
                    WriteTable(table, listDepth);
                    break;
                case Section section:
                    WriteBlocks(section.Blocks, listDepth);
                    break;
                case BlockUIContainer container:
                    WriteBlockContainer(container);
                    break;
            }
        }

        private void WriteBlockContainer(BlockUIContainer container)
        {
            if (container.Child is Image image)
            {
                var html = new StringBuilder();
                WriteImage(image, html);
                if (html.Length > 0)
                {
                    _html.Append("<p style=\"").Append(BlockMargin).Append("\">").Append(html).Append("</p>");
                }
            }
            else if (IsHorizontalRule(container))
            {
                _html.Append("<hr>");
            }
        }

        private void WriteList(List list, int listDepth)
        {
            var marker = list.MarkerStyle;
            var ordered = marker is not (TextMarkerStyle.Disc or TextMarkerStyle.Circle or TextMarkerStyle.Square or TextMarkerStyle.Box or TextMarkerStyle.None);
            var tag = ordered ? "ol" : "ul";
            _html.Append('<').Append(tag);
            var type = marker switch
            {
                TextMarkerStyle.LowerLatin => "a",
                TextMarkerStyle.UpperLatin => "A",
                TextMarkerStyle.LowerRoman => "i",
                TextMarkerStyle.UpperRoman => "I",
                TextMarkerStyle.Circle => "circle",
                TextMarkerStyle.Square or TextMarkerStyle.Box => "square",
                _ => null,
            };
            if (type is not null)
            {
                _html.Append(" type=\"").Append(type).Append('"');
            }

            if (ordered && list.StartIndex > 1)
            {
                _html.Append(" start=\"").Append(list.StartIndex.ToString(CultureInfo.InvariantCulture)).Append('"');
            }

            _html.Append(" style=\"").Append(listDepth == 0 ? BlockMargin : "margin:0");
            if (marker == TextMarkerStyle.None)
            {
                _html.Append(";list-style-type:none");
            }

            _html.Append("\">");
            foreach (var item in list.ListItems)
            {
                var paragraphs = item.Blocks.OfType<Paragraph>().ToList();
                var single = paragraphs.Count == 1 ? paragraphs[0] : null;
                _html.Append("<li");
                if (single is not null && AlignCss(single.TextAlignment) is { Length: > 0 } align)
                {
                    _html.Append(" style=\"").Append(align.TrimStart(';')).Append('"');
                }

                _html.Append('>');
                foreach (var block in item.Blocks)
                {
                    if (block is Paragraph paragraph)
                    {
                        var content = Inlines(paragraph);
                        if (single is not null)
                        {
                            _html.Append(content);
                        }
                        else
                        {
                            // Several paragraphs of one item stay paragraphs (MailerWPiA ran them together).
                            var paragraphAlign = AlignCss(paragraph.TextAlignment);
                            _html.Append("<div").Append(paragraphAlign.Length > 0 ? " style=\"" + paragraphAlign.TrimStart(';') + "\"" : string.Empty).Append('>')
                                .Append(string.IsNullOrWhiteSpace(content) ? "&nbsp;" : content).Append("</div>");
                        }
                    }
                    else
                    {
                        WriteBlock(block, listDepth + 1);
                    }
                }

                _html.Append("</li>");
            }

            _html.Append("</").Append(tag).Append('>');
        }

        private void WriteTable(Table table, int listDepth)
        {
            var rows = table.RowGroups.SelectMany(g => g.Rows).ToList();
            if (rows.Count == 0)
            {
                return;
            }

            _html.Append("<table cellpadding=\"5\" cellspacing=\"0\" style=\"border-collapse:collapse;").Append(BlockMargin).Append("\">");
            foreach (var row in rows)
            {
                _html.Append("<tr>");
                foreach (var cell in row.Cells)
                {
                    _html.Append("<td style=\"border:1px solid ").Append(Hex(CellBorderColor)).Append(";vertical-align:top");
                    if (cell.Background is SolidColorBrush { Color: var background } && IsVisibleBackground(background))
                    {
                        _html.Append(";background-color:").Append(Hex(background));
                    }

                    _html.Append('"');
                    if (cell.ColumnSpan > 1)
                    {
                        _html.Append(" colspan=\"").Append(cell.ColumnSpan.ToString(CultureInfo.InvariantCulture)).Append('"');
                    }

                    if (cell.RowSpan > 1)
                    {
                        _html.Append(" rowspan=\"").Append(cell.RowSpan.ToString(CultureInfo.InvariantCulture)).Append('"');
                    }

                    _html.Append('>');
                    WriteBlocks(cell.Blocks, listDepth);
                    _html.Append("</td>");
                }

                _html.Append("</tr>");
            }

            _html.Append("</table>");
        }

        /// <summary>The inlines of a paragraph; a paragraph that ends with a line break keeps its empty last line (an extra
        /// <c>&lt;br&gt;</c>, as browsers drop a trailing one).</summary>
        private string Inlines(Paragraph paragraph)
        {
            var html = new StringBuilder();
            var line = new LineState();
            WriteInlines(paragraph.Inlines, html, line, link: null);
            if (LastInline(paragraph.Inlines) is LineBreak)
            {
                html.Append("<br>");
            }

            return html.ToString();
        }

        private void WriteInlines(InlineCollection inlines, StringBuilder html, LineState line, Hyperlink? link)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case Run run:
                        html.Append(WrapRun(run, line, link));
                        break;
                    case LineBreak:
                        html.Append("<br>");
                        line.AtLineStart = true;
                        break;
                    case Hyperlink hyperlink when link is null:
                        var inner = new StringBuilder();
                        WriteInlines(hyperlink.Inlines, inner, line, hyperlink);
                        var address = hyperlink.NavigateUri?.OriginalString.Trim();
                        if (!string.IsNullOrEmpty(address) && inner.Length > 0)
                        {
                            html.Append("<a href=\"").Append(Escape(address)).Append("\">").Append(inner).Append("</a>");
                        }
                        else
                        {
                            html.Append(inner);
                        }

                        break;
                    case InlineUIContainer { Child: Image image }:
                        WriteImage(image, html);
                        line.AtLineStart = false;
                        break;
                    case Span span:
                        WriteInlines(span.Inlines, html, line, link);
                        break;
                }
            }
        }

        private void WriteImage(Image image, StringBuilder html)
        {
            var id = contentIdOf(image);
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            _imageIds.Add(id);
            var source = image.Source as BitmapSource;
            var width = !double.IsNaN(image.Width) && image.Width > 0 ? image.Width : source?.PixelWidth ?? 0;
            var height = !double.IsNaN(image.Height) && image.Height > 0 ? image.Height : source?.PixelHeight ?? 0;
            html.Append("<img src=\"cid:").Append(Escape(id)).Append('"');
            if (AutomationProperties.GetName(image) is { Length: > 0 } alternative && !string.IsNullOrWhiteSpace(alternative))
            {
                html.Append(" alt=\"").Append(Escape(alternative)).Append('"');
            }

            if (width > 0 && height > 0)
            {
                html.Append(" width=\"").Append(Math.Round(width).ToString(CultureInfo.InvariantCulture))
                    .Append("\" height=\"").Append(Math.Round(height).ToString(CultureInfo.InvariantCulture)).Append('"');
            }

            html.Append(" style=\"max-width:100%;height:auto\">");
        }

        /// <summary>A run: its text (line breaks, tab stops and runs of spaces kept) in a span with its formatting.</summary>
        private static string WrapRun(Run run, LineState line, Hyperlink? link)
        {
            var text = run.Text;
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var body = new StringBuilder();
            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\t", "    ", StringComparison.Ordinal).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    body.Append("<br>");
                    line.AtLineStart = true;
                }

                var part = lines[i];
                if (part.Length == 0)
                {
                    continue;
                }

                if (line.AtLineStart && part[0] == ' ')
                {
                    // A space at the start of a line would collapse in HTML.
                    part = " " + part[1..];
                }

                // Runs of spaces: one breakable space, then non-breaking ones (stable when read back, also at a line end).
                body.Append(SpaceRun().Replace(Escape(part), m => " " + string.Concat(Enumerable.Repeat("&nbsp;", m.Length - 1))));
                line.AtLineStart = false;
            }

            if (body.Length == 0)
            {
                return string.Empty;
            }

            var style = RunStyle(run, link);
            return style.Length == 0 ? body.ToString() : "<span style=\"" + style + "\">" + body + "</span>";
        }

        private static string RunStyle(Run run, Hyperlink? link)
        {
            var style = new List<string>();
            if (run.FontWeight.ToOpenTypeWeight() >= 600)
            {
                style.Add("font-weight:bold");
            }

            if (run.FontStyle == FontStyles.Italic || run.FontStyle == FontStyles.Oblique)
            {
                style.Add("font-style:italic");
            }

            var (underline, strike) = Decorations(run);
            if (underline || strike)
            {
                style.Add("text-decoration:" + (underline && strike ? "underline line-through" : underline ? "underline" : "line-through"));
            }

            if (run.Foreground is SolidColorBrush { Color: var foreground } && foreground.A > 0 && foreground != Colors.Black
                && !(link?.Foreground is SolidColorBrush { Color: var linkColor } && linkColor == foreground))
            {
                style.Add("color:" + Hex(foreground));
            }

            if (Background(run) is { } background)
            {
                style.Add("background-color:" + Hex(background));
            }

            var points = Math.Round(run.FontSize * 72.0 / 96.0, 1);
            if (Math.Abs(points - BodyPoints) > 0.6)
            {
                style.Add("font-size:" + points.ToString(CultureInfo.InvariantCulture) + "pt");
            }

            if (FontFamilyCss(run.FontFamily) is { } family)
            {
                style.Add("font-family:" + family);
            }

            return string.Join(';', style);
        }

        /// <summary>Underline and strike-through of the run and the spans around it (not a link's own underline).</summary>
        private static (bool Underline, bool Strike) Decorations(Run run)
        {
            bool underline = false, strike = false;
            for (DependencyObject? node = run; node is Inline inline; node = inline.Parent)
            {
                if (inline is Hyperlink || inline.TextDecorations is null)
                {
                    continue;
                }

                foreach (var decoration in inline.TextDecorations)
                {
                    underline |= decoration.Location == TextDecorationLocation.Underline;
                    strike |= decoration.Location == TextDecorationLocation.Strikethrough;
                }
            }

            return (underline, strike);
        }

        /// <summary>The highlight of the run or of the nearest span around it (not white, not transparent).</summary>
        private static Color? Background(Run run)
        {
            for (DependencyObject? node = run; node is Inline inline; node = inline.Parent)
            {
                if (inline.Background is SolidColorBrush { Color: var color })
                {
                    return IsVisibleBackground(color) ? color : null;
                }
            }

            return null;
        }

        private static bool IsVisibleBackground(Color color) => color.A > 0 && color != Colors.White && color != Colors.Transparent;

        /// <summary>CSS of a font family list other than the body font: names quoted, generic families bare.</summary>
        private static string? FontFamilyCss(FontFamily? family)
        {
            var names = (family?.Source ?? string.Empty).Split(',').Select(n => n.Trim().Trim('"', '\'')).Where(n => n.Length > 0).ToList();
            if (names.Count == 0 || names[0].StartsWith(FontFamilyName, StringComparison.OrdinalIgnoreCase)
                || names.Any(n => n.IndexOfAny(['#', '/', '\\', '"', '\'', ';', '<', '>', '@']) >= 0))
            {
                return null;
            }

            return string.Join(',', names.Select(n => GenericFamilies.Contains(n) ? n.ToLowerInvariant() : "'" + n + "'"));
        }

        private static string AlignCss(TextAlignment alignment) => alignment switch
        {
            TextAlignment.Center => ";text-align:center",
            TextAlignment.Right => ";text-align:right",
            TextAlignment.Justify => ";text-align:justify",
            _ => string.Empty,
        };

        /// <summary>Whether the next text starts a line (paragraph start or after a line break).</summary>
        private sealed class LineState
        {
            public bool AtLineStart { get; set; } = true;
        }
    }

    // ---- HTML → FlowDocument -----------------------------------------------------------------------------------------

    /// <summary>The formatting the HTML gives the text (inherited down the tree) and the paragraph alignment.</summary>
    private sealed record TextFormat
    {
        public static TextFormat Default { get; } = new();

        public bool? Bold { get; init; }

        public bool? Italic { get; init; }

        public bool Underline { get; init; }

        public bool Strike { get; init; }

        public Color? Foreground { get; init; }

        public Color? Background { get; init; }

        /// <summary>Device-independent pixels.</summary>
        public double? FontSize { get; init; }

        public string? FontFamily { get; init; }

        public TextAlignment? Alignment { get; init; }

        public bool Preformatted { get; init; }

        /// <summary>The <c>&lt;a&gt;</c> the text belongs to (one hyperlink per element and paragraph).</summary>
        public HtmlElement? LinkElement { get; init; }

        public Uri? LinkUri { get; init; }

        /// <summary>The format inside <paramref name="element"/>: its meaning, attributes and inline style.</summary>
        public TextFormat With(HtmlElement element)
        {
            var name = element.LocalName;
            var format = this;
            switch (name)
            {
                case "b" or "strong":
                    format = format with { Bold = true };
                    break;
                case "i" or "em" or "cite" or "dfn" or "var":
                    format = format with { Italic = true };
                    break;
                case "u" or "ins":
                    format = format with { Underline = true };
                    break;
                case "s" or "strike" or "del":
                    format = format with { Strike = true };
                    break;
                case "code" or "tt" or "kbd" or "samp":
                    format = format with { FontFamily = "Consolas" };
                    break;
                case "pre":
                    format = format with { FontFamily = "Consolas", Preformatted = true };
                    break;
                case "th":
                    format = format with { Bold = true, Alignment = TextAlignment.Center };
                    break;
                case "center":
                    format = format with { Alignment = TextAlignment.Center };
                    break;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    format = format with { Bold = true, FontSize = HeadingPoints(name) * 96 / 72 };
                    break;
                case "a":
                    // Browsers give links their own colour.
                    format = format with { Foreground = null };
                    break;
                case "font":
                    format = format.WithFontAttributes(element);
                    break;
            }

            if (element.GetAttribute("align") is { } align && name is not ("table" or "img") && Align(align) is { } fromAttribute)
            {
                format = format with { Alignment = fromAttribute };
            }

            foreach (var declaration in CssParser.ParseDeclarations(element.GetAttribute("style")))
            {
                format = format.WithDeclaration(declaration.Property, declaration.Value);
            }

            return format;
        }

        private TextFormat WithFontAttributes(HtmlElement font)
        {
            var format = this;
            if (font.GetAttribute("color") is { } color && ParseColor(color) is { } foreground)
            {
                format = format with { Foreground = foreground };
            }

            if (font.GetAttribute("face") is { } face && FamilySource(face) is { } family)
            {
                format = format with { FontFamily = family };
            }

            if (font.GetAttribute("size") is { } size && LegacyFontSize(size) is { } pixels)
            {
                format = format with { FontSize = pixels };
            }

            return format;
        }

        private TextFormat WithDeclaration(string property, string value)
        {
            var v = value.Trim();
            var lower = v.ToLowerInvariant();
            switch (property)
            {
                case "font-weight":
                    return lower switch
                    {
                        "bold" or "bolder" or "600" or "700" or "800" or "900" => this with { Bold = true },
                        "normal" or "lighter" or "100" or "200" or "300" or "400" or "500" => this with { Bold = false },
                        _ => this,
                    };
                case "font-style":
                    return lower switch
                    {
                        "italic" or "oblique" => this with { Italic = true },
                        "normal" => this with { Italic = false },
                        _ => this,
                    };
                case "text-decoration" or "text-decoration-line":
                    var tokens = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    return this with
                    {
                        Underline = Underline || tokens.Contains("underline"),
                        Strike = Strike || tokens.Contains("line-through"),
                    };
                case "color":
                    return ParseColor(v) is { } color ? this with { Foreground = color } : this;
                case "background-color" or "background":
                    return ParseColor(v) is { } background ? this with { Background = background.A == 0 ? null : background } : this;
                case "font-size":
                    return FontSizePixels(lower, FontSize ?? DefaultFontSize) is { } size ? this with { FontSize = size } : this;
                case "font-family":
                    return FamilySource(v) is { } family ? this with { FontFamily = family } : this;
                case "font":
                    return WithFontShorthand(lower, v);
                case "text-align":
                    return Align(lower) is { } alignment ? this with { Alignment = alignment } : this;
                case "white-space":
                    return lower is "pre" or "pre-wrap" ? this with { Preformatted = true } : lower is "normal" or "nowrap" or "pre-line" ? this with { Preformatted = false } : this;
                default:
                    return this;
            }
        }

        /// <summary><c>font: [style] [variant] [weight] size[/line-height] family</c>.</summary>
        private TextFormat WithFontShorthand(string lower, string original)
        {
            var tokens = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var originalTokens = original.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var format = this;
            var i = 0;
            for (; i < tokens.Length; i++)
            {
                var token = tokens[i];
                if (token is "italic" or "oblique")
                {
                    format = format with { Italic = true };
                }
                else if (token is "bold" or "bolder" or "600" or "700" or "800" or "900")
                {
                    format = format with { Bold = true };
                }
                else if (token is not ("normal" or "small-caps" or "lighter" or "100" or "200" or "300" or "400" or "500"))
                {
                    break;
                }
            }

            if (i < tokens.Length && FontSizePixels(tokens[i].Split('/')[0], FontSize ?? DefaultFontSize) is { } size)
            {
                format = format with { FontSize = size };
                i++;
                if (i < tokens.Length && tokens[i].StartsWith('/'))
                {
                    i++;
                }

                if (i < originalTokens.Length && FamilySource(string.Join(' ', originalTokens.Skip(i))) is { } family)
                {
                    format = format with { FontFamily = family };
                }
            }

            return format;
        }

        private static double HeadingPoints(string name) => name switch
        {
            "h1" => 22,
            "h2" => 16.5,
            "h3" => 13,
            "h4" => 11,
            "h5" => 9,
            _ => 7.5,
        };
    }

    private static TextAlignment? Align(string value) => value.Trim().ToLowerInvariant() switch
    {
        "left" or "start" => TextAlignment.Left,
        "center" => TextAlignment.Center,
        "right" or "end" => TextAlignment.Right,
        "justify" => TextAlignment.Justify,
        _ => null,
    };

    /// <summary>A CSS colour (<c>#rgb</c>, <c>#rrggbb</c>, <c>rgb()</c>, <c>rgba()</c>, a name), or null.</summary>
    internal static Color? ParseColor(string value)
    {
        var v = value.Trim();
        if (v.Length == 0)
        {
            return null;
        }

        if (v[0] == '#')
        {
            var hex = v[1..];
            if (hex.Length == 3 && hex.All(char.IsAsciiHexDigit))
            {
                hex = string.Concat(hex.Select(c => new string(c, 2)));
            }

            return hex.Length == 6 && hex.All(char.IsAsciiHexDigit)
                ? Color.FromRgb(
                    byte.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    byte.Parse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    byte.Parse(hex[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                : null;
        }

        var lower = v.ToLowerInvariant();
        var open = lower.IndexOf('(', StringComparison.Ordinal);
        if (open > 0 && lower.EndsWith(')'))
        {
            var function = lower[..open].Trim();
            var parts = lower[(open + 1)..^1].Split(',').Select(p => p.Trim()).ToArray();
            if ((function == "rgb" && parts.Length == 3) || (function == "rgba" && parts.Length == 4))
            {
                var channels = parts.Take(3).Select(Channel).ToArray();
                var alpha = parts.Length == 4 && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                    ? (byte)Math.Clamp(Math.Round(a * 255), 0, 255)
                    : (byte)255;
                return channels.All(c => c is not null) ? Color.FromArgb(alpha, channels[0]!.Value, channels[1]!.Value, channels[2]!.Value) : null;
            }

            return null;
        }

        switch (lower)
        {
            case "transparent":
                return Colors.Transparent;
            case "windowtext":
                return Colors.Black;
            case "window":
                return Colors.White;
            case "rebeccapurple":
                return Color.FromRgb(0x66, 0x33, 0x99);
        }

        if (!lower.All(char.IsAsciiLetter))
        {
            return null;
        }

        try
        {
            return ColorConverter.ConvertFromString(lower.Replace("grey", "gray", StringComparison.Ordinal)) is Color named ? named : null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }

        static byte? Channel(string part)
        {
            if (part.EndsWith('%') && double.TryParse(part[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            {
                return (byte)Math.Clamp(Math.Round(percent * 2.55), 0, 255);
            }

            return double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? (byte)Math.Clamp(Math.Round(number), 0, 255) : null;
        }
    }

    /// <summary>A CSS font size in device-independent pixels (4–400), relative ones to <paramref name="current"/>.</summary>
    internal static double? FontSizePixels(string value, double current)
    {
        var v = value.Trim().ToLowerInvariant();
        double? pixels = v switch
        {
            "xx-small" => 9,
            "x-small" => 10,
            "small" => 13,
            "medium" => 16,
            "large" => 18,
            "x-large" => 24,
            "xx-large" => 32,
            "smaller" => current / 1.2,
            "larger" => current * 1.2,
            _ => Length(v, current),
        };
        return pixels is > 0 ? Math.Clamp(pixels.Value, 4, 400) : null;
    }

    /// <summary>A CSS length in device-independent pixels (<c>%</c> and <c>em</c> of <paramref name="relativeTo"/>).</summary>
    private static double? Length(string value, double relativeTo)
    {
        (string Unit, double Pixels)[] units = [("px", 1), ("pt", 96.0 / 72), ("rem", DefaultFontSize), ("em", relativeTo), ("%", relativeTo / 100), ("in", 96), ("cm", 96 / 2.54), ("mm", 96 / 25.4)];
        foreach (var (unit, factor) in units)
        {
            if (value.EndsWith(unit, StringComparison.Ordinal)
                && double.TryParse(value[..^unit.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return number * factor;
            }
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain) && plain == 0 ? 0 : null;
    }

    /// <summary>The size of <c>&lt;font size&gt;</c> (1–7, or relative to 3) in pixels.</summary>
    private static double? LegacyFontSize(string value)
    {
        var v = value.Trim();
        if (!int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var size))
        {
            return null;
        }

        if (v.StartsWith('+') || v.StartsWith('-'))
        {
            size = 3 + size;
        }

        return Math.Clamp(size, 1, 7) switch
        {
            1 => 10,
            2 => 13,
            3 => 16,
            4 => 18,
            5 => 24,
            6 => 32,
            _ => 48,
        };
    }

    /// <summary>A WPF font family list ("Aptos, sans-serif") from a CSS one, or null.</summary>
    private static string? FamilySource(string value)
    {
        var names = SplitOutsideQuotes(value, ',')
            .Select(n => n.Trim().Trim('"', '\'').Trim())
            .Where(n => n.Length is > 0 and <= 100 && n.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_'))
            .ToList();
        return names.Count == 0 ? null : string.Join(", ", names);
    }

    /// <summary>Parts of a CSS list split at <paramref name="separator"/> outside quotes.</summary>
    private static List<string> SplitOutsideQuotes(string value, char separator)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        foreach (var c in value)
        {
            if (quote is null && c is '"' or '\'')
            {
                quote = c;
            }
            else if (quote == c)
            {
                quote = null;
            }

            if (quote is null && c == separator)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        parts.Add(current.ToString());
        return parts;
    }

    /// <summary>A pixel size of an <c>&lt;img&gt;</c> attribute or style (percentages and <c>auto</c>: none).</summary>
    private static double? ImageLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var v = value.Trim().ToLowerInvariant();
        if (v.EndsWith('%') || v == "auto")
        {
            return null;
        }

        var pixels = v.All(char.IsAsciiDigit) ? double.Parse(v, CultureInfo.InvariantCulture) : Length(v, DefaultFontSize);
        return pixels is > 0 and <= 10_000 ? Math.Round(pixels.Value) : null;
    }

    /// <summary>Builds blocks from the sanitized HTML tree: inline content goes into the current paragraph (made when
    /// needed), block elements end it. White space collapses as in a browser.</summary>
    private sealed class DocumentBuilder(BlockCollection target, IReadOnlyDictionary<string, MessageImage> images)
    {
        private BlockCollection _target = target;
        private Paragraph? _paragraph;
        private bool _paragraphPreformatted;
        private Hyperlink? _hyperlink;
        private HtmlElement? _hyperlinkElement;
        private Run? _lastRun;
        private bool _atLineStart = true;
        private bool _afterSpace;
        private bool _inListItem;
        private int _listDepth;
        private int _paragraphsMade;

        public void AppendChildren(HtmlNode parent, TextFormat format)
        {
            foreach (var child in parent.ChildNodes)
            {
                AppendNode(child, format);
            }
        }

        /// <summary>Ends the current paragraph: trailing white space and line break removed, a lone <c>&amp;nbsp;</c>
        /// (MailerWPiA's empty line) emptied.</summary>
        public void Flush()
        {
            if (_paragraph is null)
            {
                return;
            }

            if (!_paragraphPreformatted)
            {
                TrimLastRun();
            }

            if (LastInline(_paragraph.Inlines) is LineBreak lineBreak)
            {
                Remove(lineBreak);
            }

            if (IsLoneNonBreakingSpace(_paragraph.Inlines))
            {
                _paragraph.Inlines.Clear();
            }

            _paragraph = null;
            _hyperlink = null;
            _hyperlinkElement = null;
            _lastRun = null;
            _atLineStart = true;
            _afterSpace = false;
        }

        private void AppendNode(HtmlNode node, TextFormat format)
        {
            switch (node)
            {
                case HtmlText text:
                    AppendText(text.Data, format);
                    break;
                case HtmlElement element:
                    AppendElement(element, format);
                    break;
            }
        }

        private void AppendElement(HtmlElement element, TextFormat format)
        {
            var name = element.LocalName;
            switch (name)
            {
                case "br":
                    AppendLineBreak(format);
                    return;
                case "img":
                    AppendImage(element, format);
                    return;
                case "hr":
                    Flush();
                    _target.Add(CreateRule());
                    return;
                case "ul" or "ol":
                    Flush();
                    AppendList(element, format.With(element));
                    return;
                case "table":
                    Flush();
                    AppendTable(element, format.With(element));
                    return;
            }

            if (ParagraphElements.Contains(name))
            {
                Flush();
                var made = _paragraphsMade;
                var paragraphFormat = format.With(element);
                AppendChildren(element, paragraphFormat);
                if (_paragraphsMade == made)
                {
                    // An empty paragraph is an empty line.
                    EnsureParagraph(paragraphFormat);
                }

                Flush();
                return;
            }

            if (BlockContainers.Contains(name))
            {
                Flush();
                AppendChildren(element, format.With(element));
                Flush();
                return;
            }

            var inner = format.With(element);
            if (name == "a")
            {
                inner = inner with { LinkElement = null, LinkUri = null };
                if (format.LinkElement is null && LinkUri(element.GetAttribute("href")) is { } uri)
                {
                    inner = inner with { LinkElement = element, LinkUri = uri };
                }
                else if (format.LinkElement is not null)
                {
                    inner = inner with { LinkElement = format.LinkElement, LinkUri = format.LinkUri };
                }
            }

            AppendChildren(element, inner);
        }

        private void AppendText(string data, TextFormat format)
        {
            if (format.Preformatted)
            {
                var lines = data.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    if (i > 0)
                    {
                        AppendLineBreak(format);
                    }

                    if (lines[i].Length > 0)
                    {
                        AddRun(lines[i], format);
                    }
                }

                return;
            }

            var text = CollapsibleWhiteSpace().Replace(data, " ");
            if (_atLineStart || _afterSpace)
            {
                text = text.TrimStart(' ');
            }

            if (text.Length > 0)
            {
                AddRun(text, format);
            }
        }

        private void AddRun(string text, TextFormat format)
        {
            var run = new Run(text);
            Apply(run, format);
            Container(format).Add(run);
            _lastRun = run;
            _atLineStart = false;
            _afterSpace = text.EndsWith(' ');
        }

        private void AppendLineBreak(TextFormat format)
        {
            var inlines = Container(format);
            if (!format.Preformatted)
            {
                TrimLastRun();
            }

            inlines.Add(new LineBreak());
            _lastRun = null;
            _atLineStart = true;
            _afterSpace = false;
        }

        private void AppendImage(HtmlElement element, TextFormat format)
        {
            var source = element.GetAttribute("src");
            if (source is null || !source.StartsWith("cid:", StringComparison.OrdinalIgnoreCase) || !images.TryGetValue(source[4..], out var image))
            {
                return;
            }

            var style = CssParser.ParseDeclarations(element.GetAttribute("style"));
            var width = ImageLength(element.GetAttribute("width")) ?? ImageLength(style.LastOrDefault(d => d.Property == "width")?.Value);
            var height = ImageLength(element.GetAttribute("height")) ?? ImageLength(style.LastOrDefault(d => d.Property == "height")?.Value);
            var control = CreateImage(image, width, height, element.GetAttribute("alt"));
            Container(format).Add(new InlineUIContainer(control) { BaselineAlignment = BaselineAlignment.Baseline });
            _lastRun = null;
            _atLineStart = false;
            _afterSpace = false;
        }

        private void AppendList(HtmlElement element, TextFormat format)
        {
            var ordered = element.LocalName == "ol";
            var list = new List
            {
                MarkerStyle = Marker(element, ordered),
                Margin = _listDepth == 0 ? new Thickness(0, 0, 0, BlockSpacing) : new Thickness(0),
            };
            if (ordered && int.TryParse(element.GetAttribute("start"), NumberStyles.None, CultureInfo.InvariantCulture, out var start) && start > 1)
            {
                list.StartIndex = start;
            }

            _target.Add(list);
            var outer = _target;
            var outerInListItem = _inListItem;
            _listDepth++;
            ListItem? item = null;
            try
            {
                foreach (var child in element.ChildNodes)
                {
                    if (child is HtmlText text && string.IsNullOrWhiteSpace(text.Data))
                    {
                        continue;
                    }

                    // Content outside an <li> (a nested list written straight into the list) joins the previous item.
                    var isItem = child is HtmlElement { LocalName: "li" };
                    if (isItem || item is null)
                    {
                        item = new ListItem();
                        list.ListItems.Add(item);
                    }

                    _target = item.Blocks;
                    _inListItem = true;
                    if (isItem)
                    {
                        AppendChildren(child, format.With((HtmlElement)child));
                    }
                    else
                    {
                        AppendNode(child, format);
                    }

                    Flush();
                    if (item.Blocks.Count == 0)
                    {
                        item.Blocks.Add(new Paragraph { Margin = new Thickness(0) });
                    }
                }
            }
            finally
            {
                _target = outer;
                _inListItem = outerInListItem;
                _listDepth--;
            }

            if (list.ListItems.Count == 0)
            {
                _target.Remove(list);
            }
        }

        private void AppendTable(HtmlElement element, TextFormat format)
        {
            foreach (var caption in element.Children.Where(c => c.LocalName == "caption"))
            {
                AppendChildren(caption, format.With(caption));
                Flush();
            }

            var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, BlockSpacing) };
            var group = new TableRowGroup();
            table.RowGroups.Add(group);
            var outer = _target;
            var outerInListItem = _inListItem;
            try
            {
                foreach (var row in Rows(element))
                {
                    var tableRow = new TableRow();
                    group.Rows.Add(tableRow);
                    foreach (var cell in row.Children.Where(c => c.LocalName is "td" or "th"))
                    {
                        var tableCell = new TableCell
                        {
                            BorderBrush = Frozen(new SolidColorBrush(CellBorderColor)),
                            BorderThickness = new Thickness(1),
                            Padding = new Thickness(CellPadding),
                        };
                        if (Span(cell.GetAttribute("colspan")) is { } columns)
                        {
                            tableCell.ColumnSpan = columns;
                        }

                        if (Span(cell.GetAttribute("rowspan")) is { } rows)
                        {
                            tableCell.RowSpan = rows;
                        }

                        var cellFormat = format.With(cell);
                        if (cellFormat.Background is { } background && background != format.Background)
                        {
                            tableCell.Background = Frozen(new SolidColorBrush(background));
                        }
                        else if (cell.GetAttribute("bgcolor") is { } bgcolor && ParseColor(bgcolor) is { A: > 0 } legacy)
                        {
                            tableCell.Background = Frozen(new SolidColorBrush(legacy));
                        }

                        tableRow.Cells.Add(tableCell);
                        _target = tableCell.Blocks;
                        _inListItem = false;
                        AppendChildren(cell, cellFormat with { Background = format.Background });
                        Flush();
                        if (tableCell.Blocks.Count == 0)
                        {
                            tableCell.Blocks.Add(new Paragraph());
                        }
                    }
                }
            }
            finally
            {
                _target = outer;
                _inListItem = outerInListItem;
            }

            if (group.Rows.Count > 0)
            {
                _target.Add(table);
            }

            static IEnumerable<HtmlElement> Rows(HtmlElement table)
            {
                foreach (var child in table.Children)
                {
                    if (child.LocalName == "tr")
                    {
                        yield return child;
                    }
                    else if (child.LocalName is "thead" or "tbody" or "tfoot")
                    {
                        foreach (var row in child.Children.Where(r => r.LocalName == "tr"))
                        {
                            yield return row;
                        }
                    }
                }
            }

            static int? Span(string? value) =>
                int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var span) && span > 1 ? Math.Min(span, 1000) : null;
        }

        private Paragraph EnsureParagraph(TextFormat format)
        {
            if (_paragraph is not null)
            {
                return _paragraph;
            }

            _paragraph = new Paragraph();
            if (format.Alignment is { } alignment)
            {
                _paragraph.TextAlignment = alignment;
            }

            if (_inListItem)
            {
                _paragraph.Margin = new Thickness(0);
            }

            _target.Add(_paragraph);
            _paragraphsMade++;
            _paragraphPreformatted = format.Preformatted;
            _hyperlink = null;
            _hyperlinkElement = null;
            _atLineStart = true;
            _afterSpace = false;
            return _paragraph;
        }

        /// <summary>Where an inline of <paramref name="format"/> goes: the paragraph, or the hyperlink of its link.</summary>
        private InlineCollection Container(TextFormat format)
        {
            var paragraph = EnsureParagraph(format);
            if (format.LinkElement is null)
            {
                return paragraph.Inlines;
            }

            if (_hyperlink is null || !ReferenceEquals(_hyperlinkElement, format.LinkElement) || !ReferenceEquals(_hyperlink.Parent, paragraph))
            {
                _hyperlink = new Hyperlink { NavigateUri = format.LinkUri, Foreground = LinkBrush };
                _hyperlinkElement = format.LinkElement;
                paragraph.Inlines.Add(_hyperlink);
            }

            return _hyperlink.Inlines;
        }

        private void TrimLastRun()
        {
            if (_lastRun is null)
            {
                return;
            }

            var trimmed = _lastRun.Text.TrimEnd(' ');
            if (trimmed.Length == 0)
            {
                Remove(_lastRun);
            }
            else if (trimmed.Length != _lastRun.Text.Length)
            {
                _lastRun.Text = trimmed;
            }

            _lastRun = null;
        }

        private static void Remove(Inline inline)
        {
            var parent = inline.Parent;
            inline.SiblingInlines?.Remove(inline);
            if (parent is Hyperlink { Inlines.Count: 0 } emptyLink)
            {
                emptyLink.SiblingInlines?.Remove(emptyLink);
            }
        }

        private static bool IsLoneNonBreakingSpace(InlineCollection inlines)
        {
            var all = Flatten(inlines).ToList();
            return all is [Run { Text: " " }];

            static IEnumerable<Inline> Flatten(InlineCollection collection)
            {
                foreach (var inline in collection)
                {
                    if (inline is Span span)
                    {
                        foreach (var nested in Flatten(span.Inlines))
                        {
                            yield return nested;
                        }
                    }
                    else
                    {
                        yield return inline;
                    }
                }
            }
        }

        private static void Apply(Run run, TextFormat format)
        {
            if (format.Bold is { } bold)
            {
                run.FontWeight = bold ? FontWeights.Bold : FontWeights.Normal;
            }

            if (format.Italic is { } italic)
            {
                run.FontStyle = italic ? FontStyles.Italic : FontStyles.Normal;
            }

            if (format.Underline || format.Strike)
            {
                var decorations = new TextDecorationCollection();
                if (format.Underline)
                {
                    decorations.Add(TextDecorations.Underline);
                }

                if (format.Strike)
                {
                    decorations.Add(TextDecorations.Strikethrough);
                }

                decorations.Freeze();
                run.TextDecorations = decorations;
            }

            if (format.Foreground is { A: > 0 } foreground)
            {
                run.Foreground = Frozen(new SolidColorBrush(foreground));
            }

            if (format.Background is { A: > 0 } background)
            {
                run.Background = Frozen(new SolidColorBrush(background));
            }

            if (format.FontSize is { } size)
            {
                run.FontSize = size;
            }

            if (format.FontFamily is { } family)
            {
                run.FontFamily = new FontFamily(family);
            }
        }

        private static TextMarkerStyle Marker(HtmlElement list, bool ordered)
        {
            var type = list.GetAttribute("type")?.Trim();
            var styleType = CssParser.ParseDeclarations(list.GetAttribute("style")).LastOrDefault(d => d.Property == "list-style-type")?.Value.Trim().ToLowerInvariant();
            return styleType switch
            {
                "none" => TextMarkerStyle.None,
                "disc" => TextMarkerStyle.Disc,
                "circle" => TextMarkerStyle.Circle,
                "square" => TextMarkerStyle.Square,
                "decimal" or "decimal-leading-zero" => TextMarkerStyle.Decimal,
                "lower-alpha" or "lower-latin" => TextMarkerStyle.LowerLatin,
                "upper-alpha" or "upper-latin" => TextMarkerStyle.UpperLatin,
                "lower-roman" => TextMarkerStyle.LowerRoman,
                "upper-roman" => TextMarkerStyle.UpperRoman,
                _ => type switch
                {
                    "a" => TextMarkerStyle.LowerLatin,
                    "A" => TextMarkerStyle.UpperLatin,
                    "i" => TextMarkerStyle.LowerRoman,
                    "I" => TextMarkerStyle.UpperRoman,
                    "1" => TextMarkerStyle.Decimal,
                    _ when type?.Equals("circle", StringComparison.OrdinalIgnoreCase) == true => TextMarkerStyle.Circle,
                    _ when type?.Equals("square", StringComparison.OrdinalIgnoreCase) == true => TextMarkerStyle.Square,
                    _ when type?.Equals("disc", StringComparison.OrdinalIgnoreCase) == true => TextMarkerStyle.Disc,
                    _ => ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                },
            };
        }

        private static BlockUIContainer CreateRule() => new(new Border
        {
            Height = 1,
            Background = Frozen(new SolidColorBrush(RuleColor)),
            Margin = new Thickness(0, 6, 0, 6),
            SnapsToDevicePixels = true,
            Tag = HorizontalRuleTag,
        });

        /// <summary>The address of a link the editor keeps: http(s), mailto, tel, or a <c>{{field}}</c>; else null.</summary>
        private static Uri? LinkUri(string? href)
        {
            var value = href?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            if (value.StartsWith('{'))
            {
                return Uri.TryCreate(value, UriKind.Relative, out var field) ? field : null;
            }

            return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto" or "tel" ? uri : null;
        }
    }
}
