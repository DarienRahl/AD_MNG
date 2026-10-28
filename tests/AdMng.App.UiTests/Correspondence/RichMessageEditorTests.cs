using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using AdMng.App.Controls;
using AdMng.App.Correspondence;
using AdMng.App.Infrastructure;
using AdMng.App.Localization;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.Core.Messaging.Images;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Localization;
using AdMng.Testing;
using AdMng.Testing.RichHtml;

namespace AdMng.App.UiTests.Correspondence;

/// <summary>
/// The Korespondencja editor (U5, design note 9.2) in an off-screen window: the body loads from <c>Html</c> and edits
/// flow back after the pause (two-way binding); B / I / U, lists, link, clear formatting and paste without formatting;
/// "Wstaw pole" at the caret; Ctrl+V through the host – Outlook's CF_HTML (U1c importer, images registered), a bitmap,
/// text – inserted at the caret; content copied from the editor itself pasted with its formatting; no binding errors.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class RichMessageEditorTests
{
    private static readonly MessageImage Logo = MessageImage.Create(MessageImageSamples.Png(14, 6, seed: 0x75), "logo.png");

    [Fact]
    public Task Html_LoadsTheBody_AndEditsFlowBackAfterThePause() => WpfTestHost.RunAsync(async () =>
    {
        var body = new BodyModel { Html = "<p>Szanowni Państwo,</p><p>dzień <b>dobry</b>.</p>" };
        await WithEditorAsync(new FakeEditorHost(), body, async editor =>
        {
            Assert.Equal(2, editor.MessageEditorBox.Document.Blocks.Count);
            Assert.Equal("dzień dobry.", Text(editor.MessageEditorBox.Document.Blocks.LastBlock));

            // Loaded but not edited: the view model keeps its text (no normalized copy is pushed back).
            await Task.Delay(RichMessageEditor.HtmlDelay * 2);
            await UiTestSupport.WaitForIdleAsync();
            Assert.Equal("<p>Szanowni Państwo,</p><p>dzień <b>dobry</b>.</p>", body.Html);

            editor.MessageEditorBox.AppendText(" Zażółć gęślą jaźń");
            Assert.DoesNotContain("Zażółć", body.Html, StringComparison.Ordinal);
            await UiWait.UntilAsync(() => body.Html.Contains("Zażółć gęślą jaźń", StringComparison.Ordinal), "the body after the pause");
            Assert.Contains("<span style=\"font-weight:bold\">dobry</span>", body.Html, StringComparison.Ordinal);
            Assert.Equal(body.Html, editor.Html);

            // A body set by the view model replaces the document.
            body.Html = "<ul><li>Nowa treść</li></ul>";
            await UiTestSupport.WaitForIdleAsync();
            Assert.IsType<List>(Assert.Single(editor.MessageEditorBox.Document.Blocks));
        });
    });

    [Fact]
    public Task FlushHtml_WritesPendingEditsAtOnce_AndAnEmptyEditorIsAnEmptyBody() => WpfTestHost.RunAsync(async () =>
    {
        var body = new BodyModel { Html = "<p>Treść</p>" };
        await WithEditorAsync(new FakeEditorHost(), body, editor =>
        {
            editor.MessageEditorBox.SelectAll();
            editor.MessageEditorBox.Selection.Text = string.Empty;
            editor.FlushHtml();
            Assert.Equal(string.Empty, body.Html);

            editor.MessageEditorBox.AppendText("Na nowo");
            editor.FlushHtml();
            Assert.Equal("<p style=\"margin:0 0 8pt 0\">Na nowo</p>\n", body.Html);
            return Task.CompletedTask;
        });
    });

    [Fact]
    public Task FormattingButtons_FormatTheSelection_AndShowItsState() => WpfTestHost.RunAsync(async () =>
    {
        var body = new BodyModel { Html = "<p>Zażółć gęślą jaźń</p>" };
        await WithEditorAsync(new FakeEditorHost(), body, async editor =>
        {
            Select(editor, "gęślą");
            Click(editor.BoldButton);
            Click(editor.ItalicButton);
            Click(editor.UnderlineButton);
            Assert.True(editor.BoldButton.IsChecked);
            Assert.True(editor.ItalicButton.IsChecked);
            Assert.True(editor.UnderlineButton.IsChecked);
            editor.FlushHtml();
            Assert.Contains("Zażółć <span style=\"font-style:italic;font-weight:bold;text-decoration:underline\">gęślą</span> jaźń", body.Html, StringComparison.Ordinal);

            // The caret elsewhere: the buttons show plain text.
            Select(editor, "jaźń");
            await UiTestSupport.WaitForIdleAsync();
            Assert.False(editor.BoldButton.IsChecked);
            Assert.False(editor.ItalicButton.IsChecked);

            // Clear formatting takes everything off the selection.
            Select(editor, "gęślą");
            Click(editor.ClearFormattingButton);
            editor.FlushHtml();
            Assert.Equal("<p style=\"margin:0 0 8pt 0\">Zażółć gęślą jaźń</p>\n", body.Html);
            Assert.False(editor.BoldButton.IsChecked);

            // Lists: bullets, then numbering.
            Click(editor.BulletsButton);
            editor.FlushHtml();
            Assert.StartsWith("<ul style=\"margin:0 0 8pt 0\"><li>Zażółć gęślą jaźń</li>", body.Html, StringComparison.Ordinal);
            Click(editor.NumberingButton);
            editor.FlushHtml();
            Assert.StartsWith("<ol style=\"margin:0 0 8pt 0\"><li>Zażółć gęślą jaźń</li>", body.Html, StringComparison.Ordinal);
        });
    });

    [Fact]
    public Task ClearFormatting_WithoutASelection_AsksForOne() => WpfTestHost.RunAsync(async () =>
    {
        var host = new FakeEditorHost();
        await WithEditorAsync(host, new BodyModel { Html = "<p><b>Pogrubione</b></p>" }, editor =>
        {
            editor.MessageEditorBox.CaretPosition = editor.MessageEditorBox.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
            Click(editor.ClearFormattingButton);
            Assert.Equal(TranslationSource.Localizer[L.CorrespondenceApp.SelectTextToClear], Assert.Single(host.Errors));
            return Task.CompletedTask;
        });
    });

    [Fact]
    public Task Link_MakesChangesAndRemovesLinks_AndRefusesBadAddresses() => WpfTestHost.RunAsync(async () =>
    {
        var host = new FakeEditorHost();
        var body = new BodyModel { Html = "<p>Szczegóły na stronie sprawy.</p><p>Drugi akapit</p>" };
        await WithEditorAsync(host, body, editor =>
        {
            var localizer = TranslationSource.Localizer;
            string? answer = null;
            var asked = new List<string>();
            editor.LinkPrompt = initial =>
            {
                asked.Add(initial);
                return answer;
            };

            // Nothing selected: no prompt.
            editor.MessageEditorBox.CaretPosition = editor.MessageEditorBox.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
            Click(editor.LinkButton);
            Assert.Empty(asked);
            Assert.Equal(localizer[L.CorrespondenceApp.SelectTextForLink], Assert.Single(host.Errors));

            // A domain becomes https.
            Select(editor, "stronie");
            answer = "www.contoso.pl";
            Click(editor.LinkButton);
            Assert.Equal("https://", asked[^1]);
            editor.FlushHtml();
            Assert.Contains("na <a href=\"https://www.contoso.pl\">stronie</a> sprawy", body.Html, StringComparison.Ordinal);

            // The caret in the link: the prompt shows its address, a new one replaces it.
            Select(editor, "ron");
            answer = "{{Strona sprawy}}";
            Click(editor.LinkButton);
            Assert.Equal("https://www.contoso.pl", asked[^1]);
            editor.FlushHtml();
            Assert.Contains("<a href=\"{{Strona sprawy}}\">stronie</a>", body.Html, StringComparison.Ordinal);

            // Cancelled: nothing changes.
            answer = null;
            Click(editor.LinkButton);
            editor.FlushHtml();
            Assert.Contains("<a href=\"{{Strona sprawy}}\">stronie</a>", body.Html, StringComparison.Ordinal);

            // An empty address removes the link.
            answer = " ";
            Click(editor.LinkButton);
            editor.FlushHtml();
            Assert.DoesNotContain("<a", body.Html, StringComparison.Ordinal);
            Assert.Contains("na stronie sprawy", body.Html, StringComparison.Ordinal);

            // Addresses a message must not link to.
            host.Errors.Clear();
            foreach (var bad in new[] { "javascript:alert(1)", "file:///C:/Windows", "ftp://pliki.contoso.pl", "nie adres", "http://" })
            {
                Select(editor, "Szczegóły");
                answer = bad;
                Click(editor.LinkButton);
            }

            Assert.Equal(5, host.Errors.Count);
            Assert.Equal(localizer.Format(L.CorrespondenceApp.InvalidLink, "javascript:alert(1)"), host.Errors[0]);
            editor.FlushHtml();
            Assert.DoesNotContain("<a", body.Html, StringComparison.Ordinal);

            // An e-mail address becomes mailto:, across two paragraphs a link is refused.
            Select(editor, "Szczegóły");
            answer = "pomoc@contoso.pl";
            Click(editor.LinkButton);
            editor.FlushHtml();
            Assert.Contains("<a href=\"mailto:pomoc@contoso.pl\">Szczegóły</a>", body.Html, StringComparison.Ordinal);
            host.Errors.Clear();
            var box = editor.MessageEditorBox;
            box.Selection.Select(Find(box, "sprawy").Start, Find(box, "Drugi").End);
            answer = "https://contoso.pl";
            Click(editor.LinkButton);
            Assert.Equal(localizer[L.CorrespondenceApp.LinkInOneParagraph], Assert.Single(host.Errors));
            return Task.CompletedTask;
        });
    });

    [Fact]
    public Task Link_AsksThroughThePromptWindow() => WpfTestHost.RunAsync(async () =>
    {
        var body = new BodyModel { Html = "<p>Formularz wniosku</p>" };
        await WithEditorAsync(new FakeEditorHost(), body, async editor =>
        {
            Select(editor, "Formularz");
            var drive = UiWait.DriveModalAsync<TextPromptWindow>(async window =>
            {
                await UiTestSupport.WaitForIdleAsync();
                Assert.Equal(TranslationSource.Localizer[L.CorrespondenceApp.LinkTitle], window.Title);
                Assert.Equal(TranslationSource.Localizer[L.CorrespondenceApp.LinkMessage], window.MessageText.Text);
                Assert.Equal("https://", window.PromptTextBox.Text);
                Assert.False(window.PromptTextBox.AcceptsReturn);
                window.PromptTextBox.Text = "https://wnioski.contoso.pl/formularz";
                window.ViewModel.OkCommand.Execute(null);
            });
            Click(editor.LinkButton);
            await drive;
            editor.FlushHtml();
            Assert.Contains("<a href=\"https://wnioski.contoso.pl/formularz\">Formularz</a> wniosku", body.Html, StringComparison.Ordinal);
        });
    });

    [Fact]
    public Task InsertField_PutsTheFieldAtTheCaret() => WpfTestHost.RunAsync(async () =>
    {
        var body = new BodyModel { Html = "<p>Szanowna Pani ,</p>" };
        await WithEditorAsync(new FakeEditorHost(), body, editor =>
        {
            Assert.False(editor.InsertFieldButton.IsEnabled);
            editor.FieldChoices = ["{{Imię}}", "{{Nazwisko}}", "{{Kod pocztowy}}"];
            Assert.Equal(3, editor.FieldBox.Items.Count);
            Assert.Equal("{{Imię}}", editor.FieldBox.SelectedItem);
            Assert.True(editor.InsertFieldButton.IsEnabled);

            var box = editor.MessageEditorBox;
            box.CaretPosition = Find(box, ",").Start;
            editor.FieldBox.SelectedItem = "{{Nazwisko}}";
            Click(editor.InsertFieldButton);
            editor.FlushHtml();
            Assert.Contains("Szanowna Pani {{Nazwisko}},", body.Html, StringComparison.Ordinal);

            // The selection is replaced; a new list keeps the chosen field when it is still there.
            Select(editor, "Pani");
            editor.InsertField("{{Zwrot osobowy}}");
            editor.FlushHtml();
            Assert.Contains("Szanowna {{Zwrot osobowy}} {{Nazwisko}},", body.Html, StringComparison.Ordinal);
            editor.FieldChoices = ["{{Kod pocztowy}}", "{{Nazwisko}}"];
            Assert.Equal("{{Nazwisko}}", editor.FieldBox.SelectedItem);
            editor.FieldChoices = [];
            Assert.False(editor.InsertFieldButton.IsEnabled);
            return Task.CompletedTask;
        });
    });

    [Fact]
    public Task PasteOfOutlookHtml_GoesThroughTheHost_AndIsInsertedAtTheCaret() => WpfTestHost.RunAsync(async () =>
    {
        var host = new FakeEditorHost { Paste = FakeEditorHost.Imported("outlook-classic") };
        var body = new BodyModel { Html = "<p>Początek listu</p><p>Koniec listu</p>" };
        await WithEditorAsync(host, body, async editor =>
        {
            var box = editor.MessageEditorBox;
            box.CaretPosition = Find(box, "Początek listu").End;

            await editor.PasteAsync(HtmlData());

            Assert.Equal(1, host.PasteCalls);
            Assert.Equal(2, host.Images.Count);   // the pasted images are the body's now
            editor.FlushHtml();
            var html = body.Html;
            foreach (var part in new[]
            {
                "Szanowni Państwo,", "<ul style=\"margin:0 0 8pt 0\">", "<ol style=\"margin:0 0 8pt 0\">", "<table cellpadding=\"5\" cellspacing=\"0\"",
                "<a href=\"https://haslo.contoso.pl/\">", "Zespół Wsparcia IT – Żółć i Gęś sp. z o.o.",
            })
            {
                Assert.Contains(part, html, StringComparison.Ordinal);
            }

            Assert.All(host.Images.Keys, id => Assert.Contains("cid:" + id, html, StringComparison.Ordinal));
            Assert.True(html.IndexOf("Początek listu", StringComparison.Ordinal) < html.IndexOf("Szanowni Państwo,", StringComparison.Ordinal));
            Assert.True(html.IndexOf("Zespół Wsparcia IT", StringComparison.Ordinal) < html.IndexOf("Koniec listu", StringComparison.Ordinal));
            Assert.Empty(host.Errors);
        });
    });

    [Fact]
    public Task CtrlV_FromTheClipboard_IsInterceptedForTheHost() => WpfTestHost.RunAsync(async () =>
    {
        var host = new FakeEditorHost { Paste = FakeEditorHost.Imported("outlook-classic") };
        var body = new BodyModel { Html = "<p>Treść:</p>" };
        await WithEditorAsync(host, body, async editor =>
        {
            ClipboardFixture.Put(CfHtmlFixture.Build(CfHtmlFixture.Html("outlook-classic")));
            var box = editor.MessageEditorBox;
            box.CaretPosition = box.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);

            ApplicationCommands.Paste.Execute(null, box);
            await UiWait.UntilAsync(() => host.PasteCalls == 1 && host.Images.Count == 2, "the paste through the host");
            await UiTestSupport.WaitForIdleAsync();

            editor.FlushHtml();
            Assert.Contains("Szanowni Państwo,", body.Html, StringComparison.Ordinal);
            Assert.DoesNotContain("<o:p>", body.Html, StringComparison.Ordinal);
            Assert.DoesNotContain("mso-", body.Html, StringComparison.Ordinal);

            // Ctrl+Shift+V is the paste without formatting.
            Assert.Contains(box.InputBindings.OfType<KeyBinding>(), b => b.Key == Key.V && b.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)
                                                                         && ReferenceEquals(b.Command, RichMessageEditor.PastePlainCommand));
        });
    });

    [Fact]
    public Task PasteOfOneParagraph_JoinsTheTextAtTheCaret() => WpfTestHost.RunAsync(async () =>
    {
        var host = new FakeEditorHost { Paste = () => Task.FromResult<RichPaste?>(new RichPaste("<p><b>nie</b></p>", [])) };
        var body = new BodyModel { Html = "<p>Ala ma kota</p>" };
        await WithEditorAsync(host, body, async editor =>
        {
            var box = editor.MessageEditorBox;
            box.CaretPosition = Find(box, "ma kota").Start;

            await editor.PasteAsync(HtmlData());

            Assert.Single(box.Document.Blocks);
            editor.FlushHtml();
            Assert.Equal("<p style=\"margin:0 0 8pt 0\">Ala <span style=\"font-weight:bold\">nie</span>ma kota</p>\n", body.Html);
        });
    });

    [Fact]
    public Task PasteOfALink_IntoALink_IsInsertedAfterIt() => WpfTestHost.RunAsync(async () =>
    {
        var host = new FakeEditorHost { Paste = () => Task.FromResult<RichPaste?>(new RichPaste("<p><a href=\"https://b.contoso.pl/\">druga</a></p>", [])) };
        var body = new BodyModel { Html = "<p><a href=\"https://a.contoso.pl/\">pierwsza</a> strona</p>" };
        await WithEditorAsync(host, body, async editor =>
        {
            var box = editor.MessageEditorBox;
            box.CaretPosition = Find(box, "pierwsza").Start.GetPositionAtOffset(3)!;

            await editor.PasteAsync(HtmlData());

            editor.FlushHtml();
            Assert.Contains("<a href=\"https://a.contoso.pl/\">pierwsza</a><a href=\"https://b.contoso.pl/\">druga</a> strona", body.Html, StringComparison.Ordinal);
        });
    });

    [Fact]
    public Task PasteOfABitmap_RegistersTheImageWithTheHost() => WpfTestHost.RunAsync(async () =>
    {
        var host = new FakeEditorHost();
        var body = new BodyModel { Html = "<p>Zrzut ekranu:</p>" };
        await WithEditorAsync(host, body, async editor =>
        {
            var box = editor.MessageEditorBox;
            box.CaretPosition = box.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
            var data = new DataObject();
            data.SetData("PNG", new MemoryStream(MessageImageSamples.Png(12, 4, seed: 0x22)));

            await editor.PasteAsync(data);

            var image = Assert.Single(host.Images.Values);
            Assert.Null(Assert.Single(host.AddedFileNames));
            editor.FlushHtml();
            Assert.Contains($"Zrzut ekranu:<img src=\"cid:{image.ContentId}\" width=\"12\" height=\"4\"", body.Html, StringComparison.Ordinal);

            // A refused image is not inserted (the host shows why).
            host.RefuseImages = true;
            data = new DataObject();
            data.SetData("PNG", new MemoryStream(MessageImageSamples.Png(5, 5, seed: 0x23)));
            await editor.PasteAsync(data);
            Assert.Single(host.Images);
            Assert.Equal("Obraz odrzucony.", Assert.Single(host.Errors));
        });
    });

    [Fact]
    public Task PasteOfText_IsPlainText_EvenWithoutAHost() => WpfTestHost.RunAsync(async () =>
    {
        var body = new BodyModel { Html = "<p>Lista:</p>" };
        await WithEditorAsync(host: null, body, async editor =>
        {
            var box = editor.MessageEditorBox;
            box.CaretPosition = box.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
            var data = new DataObject();
            data.SetData(DataFormats.Html, "<b>pogrubione</b>");
            data.SetData(DataFormats.UnicodeText, " pierwszy <b>\r\ndrugi");

            await editor.PasteAsync(data);

            editor.FlushHtml();
            Assert.Contains("Lista: pierwszy &lt;b&gt;", body.Html, StringComparison.Ordinal);
            Assert.Contains("drugi", body.Html, StringComparison.Ordinal);
            Assert.DoesNotContain("pogrubione", body.Html, StringComparison.Ordinal);
            Assert.DoesNotContain("font-weight", body.Html, StringComparison.Ordinal);
        });
    });

    [Fact]
    public Task PasteWithoutFormatting_TakesTheClipboardText() => WpfTestHost.RunAsync(async () =>
    {
        var host = new FakeEditorHost();
        var body = new BodyModel { Html = "<p>Kod:</p>" };
        await WithEditorAsync(host, body, async editor =>
        {
            var box = editor.MessageEditorBox;
            box.CaretPosition = box.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
            var data = new DataObject();
            data.SetData(ClipboardHtmlReader.HtmlFormat, new MemoryStream(CfHtmlFixture.Build("<b>tekst sformatowany</b>")));
            data.SetText(" <b>Zażółć</b>", TextDataFormat.UnicodeText);
            Retry(() => Clipboard.SetDataObject(data, copy: true));

            Click(editor.PastePlainButton);
            await UiTestSupport.WaitForIdleAsync();

            Assert.Equal(0, host.PasteCalls);
            editor.FlushHtml();
            Assert.Equal("<p style=\"margin:0 0 8pt 0\">Kod: &lt;b&gt;Zażółć&lt;/b&gt;</p>\n", body.Html);

            ClearClipboard();
            Click(editor.PastePlainButton);
            Assert.Equal(TranslationSource.Localizer[L.CorrespondenceApp.ClipboardEmpty], Assert.Single(host.Errors));
        });
    });

    [Fact]
    public Task ContentCopiedInTheEditor_IsPastedWithItsFormatting() => WpfTestHost.RunAsync(async () =>
    {
        var host = new FakeEditorHost();
        var body = new BodyModel { Html = "<p><b>Pogrubiony</b> zwykły</p>" };
        await WithEditorAsync(host, body, async editor =>
        {
            var box = editor.MessageEditorBox;
            Select(editor, "Pogrubiony");
            ApplicationCommands.Copy.Execute(null, box);
            box.CaretPosition = box.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);

            ApplicationCommands.Paste.Execute(null, box);
            await UiTestSupport.WaitForIdleAsync();

            Assert.Equal(0, host.PasteCalls);
            editor.FlushHtml();
            Assert.Equal(2, body.Html.Split("Pogrubiony").Length - 1);
            Assert.Contains(" zwykły<span style=\"font-weight:bold\">Pogrubiony</span>", body.Html, StringComparison.Ordinal);
        });
    });

    [Fact]
    public Task ImagesOfTheHost_AreShown_WhenTheHostComesAfterTheBody() => WpfTestHost.RunAsync(async () =>
    {
        var body = new BodyModel { Html = $"<p>Logo: <img src=\"cid:{Logo.ContentId}\" alt=\"Logo\"></p>" };
        await WithEditorAsync(host: null, body, async editor =>
        {
            Assert.Empty(Images(editor));

            editor.Host = new FakeEditorHost().With(Logo);
            await UiTestSupport.WaitForIdleAsync();

            var image = Assert.Single(Images(editor));
            Assert.Equal(Logo.ContentId, FlowDocumentHtml.ContentIdOf(image));
            Assert.NotNull(image.Source);
            Assert.Equal($"<p>Logo: <img src=\"cid:{Logo.ContentId}\" alt=\"Logo\"></p>", body.Html);   // not rewritten
        });
    });

    [Fact]
    public Task TheEditingSurface_IsAWhiteCalibriPage_InEveryTheme() => WpfTestHost.RunAsync(async () =>
    {
        await WithEditorAsync(new FakeEditorHost(), new BodyModel(), editor =>
        {
            var box = editor.MessageEditorBox;
            Assert.Equal(Colors.White, ((SolidColorBrush)box.Background).Color);
            Assert.Equal(Colors.Black, ((SolidColorBrush)box.Foreground).Color);
            Assert.Equal(FlowDocumentHtml.FontFamilyName, box.Document.FontFamily.Source);
            Assert.Equal(FlowDocumentHtml.DefaultFontSize, box.Document.FontSize, 3);
            Assert.True(box.AcceptsTab);
            Assert.False(box.IsDocumentEnabled);
            Assert.Equal("pl-PL", box.Language.IetfLanguageTag);
            Assert.All(
                new FrameworkElement[] { editor.BoldButton, editor.ItalicButton, editor.UnderlineButton, editor.BulletsButton, editor.NumberingButton, editor.LinkButton, editor.ClearFormattingButton, editor.PastePlainButton, editor.InsertFieldButton },
                b => Assert.False(b.Focusable));
            Assert.All(
                new FrameworkElement[] { editor.BoldButton, editor.LinkButton, editor.ClearFormattingButton, editor.FieldBox, box },
                e => Assert.False(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(e))));
            return Task.CompletedTask;
        });
    });

    private static async Task WithEditorAsync(FakeEditorHost? host, BodyModel body, Func<RichMessageEditor, Task> test)
    {
        WpfTestHost.Bindings.Clear();
        var editor = new RichMessageEditor { Width = 760, Height = 420 };
        editor.SetBinding(RichMessageEditor.HtmlProperty, new Binding(nameof(BodyModel.Html)) { Source = body, Mode = BindingMode.TwoWay });
        editor.Host = host;
        var window = new Window { Content = editor, Width = 800, Height = 480 };
        UiTestSupport.ShowOffScreen(window);
        await UiTestSupport.WaitForIdleAsync();
        try
        {
            await test(editor);
            Assert.True(WpfTestHost.Bindings.Errors.Count == 0, "WPF binding errors:" + Environment.NewLine + string.Join(Environment.NewLine, WpfTestHost.Bindings.Errors));
        }
        finally
        {
            window.Close();
        }
    }

    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static void Select(RichMessageEditor editor, string text)
    {
        var range = Find(editor.MessageEditorBox, text);
        editor.MessageEditorBox.Selection.Select(range.Start, range.End);
    }

    /// <summary>The range of the first occurrence of <paramref name="text"/> inside one run.</summary>
    private static TextRange Find(RichTextBox box, string text)
    {
        for (var position = box.Document.ContentStart; position is not null; position = position.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (position.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text)
            {
                continue;
            }

            var index = position.GetTextInRun(LogicalDirection.Forward).IndexOf(text, StringComparison.Ordinal);
            if (index >= 0)
            {
                var start = position.GetPositionAtOffset(index)!;
                return new TextRange(start, start.GetPositionAtOffset(text.Length)!);
            }
        }

        throw new InvalidOperationException("Not in the document: " + text);
    }

    private static string Text(Block block) => new TextRange(block.ContentStart, block.ContentEnd).Text;

    private static List<Image> Images(RichMessageEditor editor)
    {
        var images = new List<Image>();
        for (var position = editor.MessageEditorBox.Document.ContentStart; position is not null; position = position.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (position.GetAdjacentElement(LogicalDirection.Forward) is InlineUIContainer { Child: Image image } && !images.Contains(image))
            {
                images.Add(image);
            }
        }

        return images;
    }

    private static DataObject HtmlData()
    {
        var data = new DataObject();
        data.SetData(DataFormats.Html, "<p>Treść ze schowka</p>");
        return data;
    }

    private static void ClearClipboard() => Retry(Clipboard.Clear);

    private static void Retry(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (ExternalException) when (attempt < 10)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>The view model side of the <c>Html</c> binding.</summary>
    private sealed class BodyModel : INotifyPropertyChanged
    {
        private string _html = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Html
        {
            get => _html;
            set
            {
                if (!string.Equals(_html, value, StringComparison.Ordinal))
                {
                    _html = value;
                    OnPropertyChanged();
                }
            }
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
