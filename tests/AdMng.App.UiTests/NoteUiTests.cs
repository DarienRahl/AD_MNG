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

                // For a screen reader the note is its text; the glyph, a part of the template, is no element of its own.
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
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("Ad.Note.Info", "TextFillColorSecondaryBrush")]
    [InlineData("Ad.Note.Attention", "SystemFillColorCautionBrush")]
    [InlineData("Ad.Note.Error", "SystemFillColorCriticalBrush")]
    public Task ANote_MayChooseItsGlyph_FromTheStatusIcons_AndKeepsTheRestOfItsStyle(string styleKey, string glyphColourKey) =>
        WpfTestHost.RunAsync(async () =>
        {
            var text = new TextBlock { Text = Weak, TextWrapping = TextWrapping.Wrap };
            var note = new ContentControl { Style = (Style)Application.Current.FindResource(styleKey), Content = text, Width = 320 };
            var window = ControlHost.Show(note);
            try
            {
                await UiTestSupport.WaitForIdleAsync(rounds: 2);
                var glyph = ControlHost.Part<TextBlock>(note, "PART_Glyph");
                var colour = ControlHost.ThemeColor(note, glyphColourKey);

                // Without the property: the glyph of the style, as ever.
                Assert.Null(AdNote.GetIcon(note));
                Assert.Equal(GlyphOf(styleKey), glyph.Text);

                // Each icon of the closed set puts its glyph in place of the style's - and nothing else changes: the
                // colour, the size, the text and the room the glyph takes are those of the style.
                var width = glyph.ActualWidth;
                foreach (var icon in AdNote.Icons)
                {
                    AdNote.SetIcon(note, icon);
                    note.UpdateLayout();
                    Assert.Equal(AdNote.GlyphOf(icon), glyph.Text);
                    Assert.Equal(AdNote.GlyphOf(icon), Assert.IsType<string>(note.Tag));
                    Assert.Equal(colour, ControlHost.ColorOf(glyph.Foreground));
                    Assert.Equal(12d, glyph.FontSize);
                    Assert.Same(text, ControlHost.Part<ContentPresenter>(note, "PART_Text").Content);
                    Assert.True(glyph.ActualWidth > 0 && glyph.ActualWidth <= width + 4, "the glyph of the note takes another room");
                }

                // The information glyph on a note of attention - the case of the replication note of the bulk creation.
                AdNote.SetIcon(note, AppIcons.StatusInfo);
                Assert.Equal(Glyphs.Info, glyph.Text);

                // A key outside the set, or none: the glyph of the style again.
                AdNote.SetIcon(note, AppIcons.User);
                Assert.Equal(GlyphOf(styleKey), glyph.Text);
                AdNote.SetIcon(note, AppIcons.StatusError);
                AdNote.SetIcon(note, null);
                Assert.Equal(GlyphOf(styleKey), glyph.Text);
                Assert.Equal(GlyphOf(styleKey), Assert.IsType<string>(note.Tag));
                Assert.Equal(colour, ControlHost.ColorOf(glyph.Foreground));
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task TheIconsOfANote_AreTheClosedSetOfTheStatusLine_EachWithTheGlyphThatTheLineShows() => WpfTestHost.RunAsync(() =>
    {
        // A key of the icon set, one glyph each, the same glyph the status line shows for the same severity.
        Assert.Equal(4, AdNote.Icons.Count);
        Assert.Equal(AdNote.Icons.Count, AdNote.Icons.Distinct(StringComparer.Ordinal).Count());
        Assert.All(AdNote.Icons, icon =>
        {
            Assert.Contains(icon, AppIcons.All);
            Assert.True(AppIcons.Find(icon) is not null, $"no picture {icon}");
            Assert.False(string.IsNullOrEmpty(AdNote.GlyphOf(icon)), $"no glyph for {icon}");
        });
        Assert.Equal(AdNote.Icons.Count, AdNote.Icons.Select(AdNote.GlyphOf).Distinct(StringComparer.Ordinal).Count());
        foreach (var kind in Enum.GetValues<AdMng.Presentation.Feedback.PageStatusKind>().Where(kind => kind != AdMng.Presentation.Feedback.PageStatusKind.Working))
        {
            var (key, glyph) = StatusLine.IconOf(kind);
            Assert.Contains(Assert.IsType<string>(key), AdNote.Icons);
            Assert.Equal(glyph, AdNote.GlyphOf(key));
        }

        // Outside the set: nothing (the glyph of the style stays).
        Assert.Null(AdNote.GlyphOf(null));
        Assert.Null(AdNote.GlyphOf(string.Empty));
        Assert.Null(AdNote.GlyphOf(AppIcons.StatusUnknown));
        Assert.Null(AdNote.GlyphOf(AppIcons.User));
        Assert.Null(AdNote.GlyphOf("Icon.NoSuchIcon"));
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
