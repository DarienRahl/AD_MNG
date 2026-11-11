using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using AdMng.App.Behaviors;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;

namespace AdMng.App.UiTests;

/// <summary>
/// The progress ring (track V, stage V2; docs/dev/v-interface-renewal.md 5.2, row 11): a light element of its size that
/// is drawn only while it is active, and turns only while it is active, visible and its progress is not known - also
/// with motion switched off, because progress is information. No pixel is compared: the drawing and the clock are
/// asserted through properties.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ProgressRingUiTests
{
    [Fact]
    public Task ARing_IsALightElementOfItsSize_DrawnOnlyWhileItIsActive() => WpfTestHost.RunAsync(async () =>
    {
        var ring = new ProgressRing();
        var part = new ProgressRing { Size = 14, IsActive = true, Fraction = 0.5 };
        var window = ControlHost.Show(ring, part);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);

            // Exactly Size x Size (16 by default), no template, no children, not for the keyboard, the pointer or
            // UI Automation: the text next to it says what runs.
            Assert.Equal(16d, ring.Size);
            Assert.Equal(new Size(16, 16), ring.DesiredSize);
            Assert.Equal(new Size(16, 16), ring.RenderSize);
            Assert.Equal(new Size(14, 14), part.RenderSize);
            Assert.Equal(0, VisualTreeHelper.GetChildrenCount(ring));
            Assert.False(ring.Focusable);
            Assert.False(ring.IsHitTestVisible);
            Assert.Null(UIElementAutomationPeer.CreatePeerForElement(ring));
            Assert.True(double.IsNaN(ring.Fraction));

            // Not active: nothing is drawn.
            Assert.False(ring.IsActive);
            Assert.True(Drawn(ring).IsEmpty, "a ring that is not active is drawn");

            // Active: the circle and the arc, inside the square of the element.
            ring.IsActive = true;
            await UiTestSupport.WaitForIdleAsync(rounds: 1);
            AssertDrawnInside(ring, 16);
            AssertDrawnInside(part, 14);

            // A known progress is drawn as well (the arc is the part done); a finished and an empty one too.
            ring.Fraction = 1;
            await UiTestSupport.WaitForIdleAsync(rounds: 1);
            AssertDrawnInside(ring, 16);
            ring.Fraction = 0;
            await UiTestSupport.WaitForIdleAsync(rounds: 1);
            AssertDrawnInside(ring, 16);

            // Not active any more: nothing is drawn.
            ring.IsActive = false;
            await UiTestSupport.WaitForIdleAsync(rounds: 1);
            Assert.True(Drawn(ring).IsEmpty, "a ring that is not active any more is still drawn");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task ARing_IsDrawnInABrushThatCannotBeFrozen() => WpfTestHost.RunAsync(async () =>
    {
        // The brushes of the theme are not frozen: their colour is a resource reference that follows the theme and
        // the accent colour, and the status line gives its ring the accent brush. Drawing with such a brush must not
        // fail - an exception while an element is drawn ends the UI thread.
        var bound = new SolidColorBrush();
        BindingOperations.SetBinding(bound, SolidColorBrush.ColorProperty, new Binding { Source = Colors.SteelBlue });
        Assert.False(bound.CanFreeze);

        var ring = new ProgressRing { Foreground = bound, IsActive = true };
        var part = new ProgressRing { Foreground = bound, IsActive = true, Fraction = 0.25 };
        var themed = new ProgressRing { IsActive = true };
        themed.SetResourceReference(ProgressRing.ForegroundProperty, "AccentFillColorDefaultBrush");
        var window = ControlHost.Show(ring, part, themed);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            AssertDrawnInside(ring, 16);
            AssertDrawnInside(part, 16);
            AssertDrawnInside(themed, 16);
            Assert.NotSame(Brushes.Black, themed.Foreground);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task ARing_TurnsOnlyWhileItIsActiveVisibleAndItsProgressIsNotKnown_AlsoWithMotionOff() => WpfTestHost.RunAsync(async () =>
    {
        var ring = new ProgressRing();
        var early = new ProgressRing { IsActive = true };
        var host = new Border { Child = ring };
        var rotation = Assert.IsType<RotateTransform>(ring.RenderTransform);

        // Active before anybody can see it: no clock.
        Assert.False(early.IsVisible);
        Assert.False(early.IsTurning);
        Assert.False(early.RenderTransform.HasAnimatedProperties);

        var window = ControlHost.Show(host, early);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);

            // Progress is information, not motion: the switch of the motion behaviours is off in every UI test.
            Assert.False(Motion.IsEnabled);
            Assert.True(early.IsVisible);
            Assert.True(early.IsTurning, "a ring that was active before it was shown does not turn");
            Assert.True(early.RenderTransform.HasAnimatedProperties);

            // It turns around its centre; what turns is its own transform, which stays for its whole life.
            Assert.Equal(new Point(0.5, 0.5), ring.RenderTransformOrigin);
            Assert.False(ring.IsTurning);
            Assert.False(rotation.HasAnimatedProperties);

            ring.IsActive = true;
            Assert.True(ring.IsTurning, "an active ring does not turn");
            Assert.True(rotation.HasAnimatedProperties);
            Assert.False(ring.HasAnimatedProperties, "only the transform of the ring is animated");

            // A known progress stands still, at the base angle.
            ring.Fraction = 0.4;
            Assert.False(ring.IsTurning);
            Assert.False(rotation.HasAnimatedProperties);
            Assert.Equal(0d, rotation.Angle);
            ring.Fraction = double.NaN;
            Assert.True(ring.IsTurning);
            Assert.True(rotation.HasAnimatedProperties);

            // Not visible - itself or through its parent (a hidden tab, a collapsed row): no clock. Visible again: it turns.
            ring.Visibility = Visibility.Collapsed;
            Assert.False(ring.IsTurning);
            Assert.False(rotation.HasAnimatedProperties);
            ring.Visibility = Visibility.Visible;
            Assert.True(ring.IsTurning);
            host.Visibility = Visibility.Collapsed;
            Assert.False(ring.IsVisible);
            Assert.False(ring.IsTurning);
            Assert.False(rotation.HasAnimatedProperties);
            host.Visibility = Visibility.Visible;
            Assert.True(ring.IsTurning);
            Assert.True(rotation.HasAnimatedProperties);

            // The operation ended.
            ring.IsActive = false;
            Assert.False(ring.IsTurning);
            Assert.False(rotation.HasAnimatedProperties);
            Assert.Equal(0d, rotation.Angle);
            Assert.Same(rotation, ring.RenderTransform);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>The bounds of what the element drew (its render data, in its own coordinates); empty when it drew nothing.</summary>
    private static Rect Drawn(UIElement element) => VisualTreeHelper.GetDrawing(element) is { } drawing ? drawing.Bounds : Rect.Empty;

    private static void AssertDrawnInside(ProgressRing ring, double size)
    {
        // The bounds of a stroked figure are an estimate: a few pixels of tolerance, the width of the stroke.
        const double Tolerance = 3;
        var bounds = Drawn(ring);
        Assert.False(bounds.IsEmpty, "an active ring draws nothing");
        Assert.True(bounds.Width > size / 2 && bounds.Height > size / 2, $"the ring is drawn in {bounds}");
        Assert.True(
            bounds.Left >= -Tolerance && bounds.Top >= -Tolerance && bounds.Right <= size + Tolerance && bounds.Bottom <= size + Tolerance,
            $"the ring is drawn outside its {size} px square: {bounds}");
    }
}
