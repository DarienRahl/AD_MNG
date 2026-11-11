using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;

namespace AdMng.App.UiTests;

/// <summary>
/// The note (track V, stage V2b; docs/dev/v-interface-renewal.md, "V2b-1 as built"): the look of a sentence that belongs
/// to one field or one section - the styles <c>Ad.Note.Info</c>, <c>Ad.Note.Attention</c> and <c>Ad.Note.Error</c> on a
/// <see cref="ContentControl"/>. Quiet and inline: a glyph of the severity in its status colour and the content, no
/// fill and no border - not a banner, not the status line. Property checks on the named parts of the template.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class NoteUiTests
{
    private const string Weak = "Słabe mapowania: 1. Przy pełnym wymuszaniu silnego powiązania certyfikatów kontroler domeny je odrzuca – zastąp je mapowaniem wystawcy i numeru seryjnego.";

    [Theory]
    [InlineData("Ad.Note.Info", "TextFillColorSecondaryBrush", "TextFillColorSecondaryBrush")]
    [InlineData("Ad.Note.Attention", "SystemFillColorCautionBrush", "TextFillColorPrimaryBrush")]
    [InlineData("Ad.Note.Error", "SystemFillColorCriticalBrush", "TextFillColorPrimaryBrush")]
    public Task ANote_IsAGlyphInItsStatusColourAndItsContent_WithoutASurface(string styleKey, string glyphColourKey, string textColourKey) =>
        WpfTestHost.RunAsync(async () =>
        {
            var text = new TextBlock { Text = Weak, TextWrapping = TextWrapping.Wrap };
            var note = new ContentControl { Style = (Style)Application.Current.FindResource(styleKey), Content = text, Width = 320 };
            var window = ControlHost.Show(note);
            try
            {
                await UiTestSupport.WaitForIdleAsync(rounds: 2);
                var glyph = ControlHost.Part<TextBlock>(note, "PART_Glyph");
                var presenter = ControlHost.Part<ContentPresenter>(note, "PART_Text");

                // The glyph of the severity - told by its shape, not only by its colour - in the status colour.
                Assert.Equal(GlyphOf(styleKey), glyph.Text);
                Assert.Equal(GlyphOf(styleKey), Assert.IsType<string>(note.Tag));
                Assert.Equal(12d, glyph.FontSize);
                Assert.True(glyph.IsVisible);
                Assert.True(glyph.ActualWidth > 0, "the glyph of the note takes no room");
                Assert.Equal(ControlHost.ThemeColor(note, glyphColourKey), ControlHost.ColorOf(glyph.Foreground));

                // The content: the text of the note in the text colour of its severity, wrapped under itself - to the
                // right of the glyph, which stays on the first line.
                Assert.Same(text, presenter.Content);
                Assert.Equal(ControlHost.ThemeColor(note, textColourKey), ControlHost.ColorOf(text.Foreground));
                Assert.True(text.ActualHeight > glyph.ActualHeight * 2, "the text of the note did not wrap");
                Assert.True(LeftOf(text, note) >= glyph.ActualWidth + 6, "the text of the note starts under its glyph");
                Assert.True(TopOf(glyph, note) < glyph.ActualHeight, "the glyph is not on the first line of the note");
                Assert.Equal(320d, note.ActualWidth);

                // Quiet: no fill, no border, no element of a banner - and nothing to stop at with the keyboard.
                Assert.Null(note.Background);
                Assert.Equal(new Thickness(0), note.BorderThickness);
                Assert.Empty(UiWait.Descendants<Border>(note));
                Assert.False(note.Focusable);
                Assert.False(note.IsTabStop);

                // For a screen reader the note is its text: the control itself and its glyph are no elements of their own.
                Assert.Null(UIElementAutomationPeer.CreatePeerForElement(note));
                var peer = Assert.IsAssignableFrom<AutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(text));
                Assert.Equal(Weak, peer.GetName());
                Assert.False(Assert.IsAssignableFrom<AutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(glyph)).IsControlElement());
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task TheThreeSeverities_DifferInTheirGlyph_AndShareOneTemplate() => WpfTestHost.RunAsync(() =>
    {
        var info = (Style)Application.Current.FindResource("Ad.Note.Info");
        var attention = (Style)Application.Current.FindResource("Ad.Note.Attention");
        var error = (Style)Application.Current.FindResource("Ad.Note.Error");

        Assert.All([info, attention, error], style => Assert.Equal(typeof(ContentControl), style.TargetType));
        Assert.Same(info, attention.BasedOn);
        Assert.Same(info, error.BasedOn);
        Assert.Equal(3, new[] { GlyphOf("Ad.Note.Info"), GlyphOf("Ad.Note.Attention"), GlyphOf("Ad.Note.Error") }.Distinct(StringComparer.Ordinal).Count());

        // The banners of before stage V2 are still there for the views that were not converted yet.
        Assert.Equal(typeof(Border), Assert.IsType<Style>(Application.Current.FindResource("Ad.ErrorBar")).TargetType);
        Assert.Equal(typeof(Border), Assert.IsType<Style>(Application.Current.FindResource("Ad.WarningBar")).TargetType);
        return Task.CompletedTask;
    });

    private static string GlyphOf(string styleKey) => styleKey switch
    {
        "Ad.Note.Info" => Glyphs.Info,
        "Ad.Note.Attention" => Glyphs.Warning,
        _ => Glyphs.Error,
    };

    private static double LeftOf(FrameworkElement element, UIElement relativeTo) => element.TranslatePoint(new Point(0, 0), relativeTo).X;

    private static double TopOf(FrameworkElement element, UIElement relativeTo) => element.TranslatePoint(new Point(0, 0), relativeTo).Y;
}
