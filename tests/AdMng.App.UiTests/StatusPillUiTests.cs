using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.Presentation.Feedback;

namespace AdMng.App.UiTests;

/// <summary>
/// The status pill (track V, stage V2b-5b; docs/dev/v2-status-controls.md): a small rounded label with the picture of a state
/// and a short word, for the state of one row or one item. The meaning is told by the picture and the word - never by colour
/// alone - and the word is the name a screen reader hears. Property checks on the named parts of the template.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class StatusPillUiTests
{
    [Theory]
    [InlineData(StatusPillKind.Ok, AppIcons.StatusOk, "SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush")]
    [InlineData(StatusPillKind.Info, AppIcons.StatusInfo, "SystemFillColorAttentionBackgroundBrush", "SystemFillColorAttentionBrush")]
    [InlineData(StatusPillKind.Attention, AppIcons.StatusWarning, "SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush")]
    [InlineData(StatusPillKind.Error, AppIcons.StatusError, "SystemFillColorCriticalBackgroundBrush", "SystemFillColorCriticalBrush")]
    public Task APill_ShowsThePictureOfItsKind_AndTheWord_OnTheSurfaceOfThatKind(StatusPillKind kind, string iconKey, string surfaceKey, string pictureKey) =>
        WpfTestHost.RunAsync(async () =>
        {
            var pill = new StatusPill { Kind = kind, Text = "Ostrzeżenie" };
            var window = ControlHost.Show(pill);
            try
            {
                await UiTestSupport.WaitForIdleAsync(rounds: 2);
                var surface = ControlHost.Part<Border>(pill, "Surface");
                var icon = ControlHost.Part<AreaIconView>(pill, "PART_Icon");
                var text = ControlHost.Part<TextBlock>(pill, "PART_Text");

                // The picture of the kind - 16 px - and its word, both there.
                Assert.Same(Application.Current.FindResource(StatusPill.StyleKey), pill.Style);
                Assert.Equal(iconKey, icon.IconKey);
                Assert.Equal(StatusPill.IconOf(kind).Glyph, icon.Glyph);
                Assert.Equal(16d, icon.Size);
                Assert.Equal("Ostrzeżenie", text.Text);
                Assert.True(pill.HasText);
                Assert.True(text.IsVisible);
                Assert.True(icon.IsVisible);
                Assert.True(text.ActualWidth > 0);

                // The surface of the kind and the picture in the status colour of the kind (the glyph of the monochrome style
                // takes it; the colour style draws the coloured picture). The text keeps the colour of the text.
                Assert.Equal(ControlHost.ThemeColor(pill, surfaceKey), ControlHost.ColorOf(surface.Background));
                Assert.Equal(ControlHost.ThemeColor(pill, pictureKey), ControlHost.ColorOf(icon.Foreground));
                Assert.Equal(ControlHost.ThemeColor(pill, "TextFillColorPrimaryBrush"), ControlHost.ColorOf(text.Foreground));

                // A pill and not a banner: a small rounded label with an outline - 22 px high, the radius of the pill token.
                Assert.InRange(pill.ActualHeight, 22, 24);
                Assert.Equal((CornerRadius)Application.Current.FindResource("Ad.Radius.Pill"), surface.CornerRadius);
                Assert.Equal(new Thickness(1), surface.BorderThickness);
                Assert.Equal(ControlHost.ThemeColor(pill, "CardStrokeColorDefaultBrush"), ControlHost.ColorOf(surface.BorderBrush));
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task AnUnknownPill_IsTheQuietOne_WithoutATint() => WpfTestHost.RunAsync(async () =>
    {
        // The default kind: not checked, not known - the surface of the page, with the outline.
        var pill = new StatusPill { Text = "Nie sprawdzono" };
        var window = ControlHost.Show(pill);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var surface = ControlHost.Part<Border>(pill, "Surface");
            var icon = ControlHost.Part<AreaIconView>(pill, "PART_Icon");

            Assert.Equal(StatusPillKind.Unknown, pill.Kind);
            Assert.Equal(AppIcons.StatusUnknown, icon.IconKey);
            Assert.Equal(Glyphs.Question, icon.Glyph);
            Assert.Equal(System.Windows.Media.Colors.Transparent, ControlHost.ColorOf(surface.Background));
            Assert.Equal(ControlHost.ThemeColor(pill, "TextFillColorSecondaryBrush"), ControlHost.ColorOf(icon.Foreground));
            Assert.Equal("Nie sprawdzono", ControlHost.Part<TextBlock>(pill, "PART_Text").Text);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task TheKinds_DifferByTheirPicture_NotByColourAlone() => WpfTestHost.RunAsync(() =>
    {
        var kinds = Enum.GetValues<StatusPillKind>();
        var icons = kinds.Select(StatusPill.IconOf).ToList();

        // Five kinds, five pictures and five glyphs: the shape says what the colour only underlines.
        Assert.Equal(5, kinds.Length);
        Assert.Equal(kinds.Length, icons.Select(icon => icon.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(kinds.Length, icons.Select(icon => icon.Glyph).Distinct(StringComparer.Ordinal).Count());
        Assert.All(icons, icon =>
        {
            Assert.Contains(icon.Key, AppIcons.All);
            Assert.True(AppIcons.Find(icon.Key) is not null, $"no picture {icon.Key}");
        });

        // The four of the status line and the notes are the pictures of the same severities.
        Assert.Equal(StatusLine.IconOf(PageStatusKind.Done), StatusPill.IconOf(StatusPillKind.Ok));
        Assert.Equal(StatusLine.IconOf(PageStatusKind.Attention), StatusPill.IconOf(StatusPillKind.Attention));
        Assert.Equal(StatusLine.IconOf(PageStatusKind.Error), StatusPill.IconOf(StatusPillKind.Error));
        Assert.Equal(StatusLine.IconOf(PageStatusKind.Idle), StatusPill.IconOf(StatusPillKind.Info));
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheWord_IsTheAutomationName_AndThePillIsOneTextElement() => WpfTestHost.RunAsync(async () =>
    {
        var pill = new StatusPill { Kind = StatusPillKind.Error, Text = "Błąd" };
        var window = ControlHost.Show(pill);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);

            // The name a screen reader hears is the word - and follows it.
            Assert.Equal("Błąd", AutomationProperties.GetName(pill));
            var peer = Assert.IsAssignableFrom<AutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(pill));
            Assert.Equal("Błąd", peer.GetName());
            Assert.Equal(AutomationControlType.Text, peer.GetAutomationControlType());
            Assert.Equal(nameof(StatusPill), peer.GetClassName());
            Assert.True(peer.IsControlElement());
            Assert.True(peer.IsContentElement());

            // One element: the word and the picture inside it are not elements of their own (the word would be read twice).
            Assert.Empty(peer.GetChildren() ?? []);

            pill.Text = "OK";
            pill.Kind = StatusPillKind.Ok;
            Assert.Equal("OK", AutomationProperties.GetName(pill));
            Assert.Equal("OK", peer.GetName());
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task APillWithoutAWord_ShowsOnlyThePicture_AndSaysNothing() => WpfTestHost.RunAsync(async () =>
    {
        var pill = new StatusPill { Kind = StatusPillKind.Ok, Text = "OK" };
        var window = ControlHost.Show(pill);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var text = ControlHost.Part<TextBlock>(pill, "PART_Text");
            Assert.True(text.IsVisible);

            pill.Text = string.Empty;
            window.UpdateLayout();
            Assert.False(pill.HasText);
            Assert.False(text.IsVisible);
            Assert.Equal(string.Empty, AutomationProperties.GetName(pill));
            Assert.True(ControlHost.Part<AreaIconView>(pill, "PART_Icon").IsVisible);

            // A null text is no text (the binding of a row without a word).
            pill.Text = null!;
            Assert.Equal(string.Empty, pill.Text);
            Assert.False(pill.HasText);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task APill_IsALabel_NotAControl_ToTabTo() => WpfTestHost.RunAsync(async () =>
    {
        var pill = new StatusPill { Kind = StatusPillKind.Attention, Text = "Ostrzeżenie" };
        var window = ControlHost.Show(pill);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);

            // Nothing to focus and nothing to press; it sits in a row that has its own commands.
            Assert.False(pill.Focusable);
            Assert.False(pill.IsTabStop);
            Assert.Empty(UiWait.Descendants<System.Windows.Controls.Primitives.ButtonBase>(pill));
            Assert.Equal(0, UiWait.Descendants<Control>(pill).Count(control => control.IsTabStop));
        }
        finally
        {
            window.Close();
        }
    });
}
