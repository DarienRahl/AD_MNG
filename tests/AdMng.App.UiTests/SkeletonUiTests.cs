using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using AdMng.App.Behaviors;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;

namespace AdMng.App.UiTests;

/// <summary>
/// The placeholder rows (track V, stage V2b-5b; docs/dev/v-interface-renewal.md 5.2 row 10 and 6.7 - "the first page of a list is
/// loading: at most 12 placeholder rows"): collapsed until the first read begins, drawn after the delay the status line has
/// before "Working", rows of grey bars in the shape of rows, a calm static picture without motion and a gentle pulse only with it.
/// The delay is driven by a manual clock, so no test waits. Property checks on the named parts of the template.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class SkeletonUiTests
{
    [Fact]
    public Task ASkeleton_IsCollapsed_UntilItIsActive_AndDrawsItsRowsAfterTheDelayOfTheStatusLine() => WpfTestHost.RunAsync(async () =>
    {
        var clock = new ManualStatusScheduler();
        var skeleton = new Skeleton { Scheduler = clock, RowCount = 6, Width = 400 };
        var window = ControlHost.Show(skeleton);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var delay = Assert.NotNull(Motion.TimeOf(Motion.WorkingDelayKey));

            // Not active: not there at all - no room, no rows in the tree that anything would draw.
            Assert.Same(Application.Current.FindResource(Skeleton.StyleKey), skeleton.Style);
            Assert.False(skeleton.IsActive);
            Assert.False(skeleton.IsShown);
            Assert.Equal(Visibility.Collapsed, skeleton.Visibility);
            Assert.False(skeleton.IsVisible);
            Assert.Equal(0, clock.Pending);

            // The first read begins: the control is in the layout, but nothing is drawn yet - a read that ends at once never flashes.
            skeleton.IsActive = true;
            window.UpdateLayout();
            Assert.True(skeleton.IsVisible);
            Assert.False(skeleton.IsShown);
            var rows = ControlHost.Part<Panel>(skeleton, Skeleton.RowsPartName);
            Assert.False(rows.IsVisible);
            Assert.Equal([delay], clock.Requested);
            Assert.Equal(1, clock.Pending);
            clock.Advance(delay - TimeSpan.FromMilliseconds(1));
            Assert.False(skeleton.IsShown);

            // The delay ran out: the rows.
            clock.Advance(1);
            window.UpdateLayout();
            Assert.True(skeleton.IsShown);
            Assert.True(rows.IsVisible);
            Assert.Equal(0, clock.Pending);
            Assert.Equal(6, skeleton.BuiltRows);
            Assert.Equal(6, rows.Children.Count);
            Assert.Equal(6 * 26d, skeleton.ActualHeight);

            // The rows came: gone at once, nothing waits.
            skeleton.IsActive = false;
            window.UpdateLayout();
            Assert.False(skeleton.IsShown);
            Assert.False(skeleton.IsVisible);
            Assert.Equal(0d, skeleton.ActualHeight);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AFirstRead_ThatEndsBeforeTheDelay_DrawsNothingAtAll() => WpfTestHost.RunAsync(async () =>
    {
        var clock = new ManualStatusScheduler();
        var skeleton = new Skeleton { Scheduler = clock };
        var window = ControlHost.Show(skeleton);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);

            skeleton.IsActive = true;
            Assert.Equal(1, clock.Pending);
            clock.Advance(100);
            skeleton.IsActive = false;

            // The read ended: the delay was cancelled, and a late tick of the clock shows nothing.
            Assert.Equal(0, clock.Pending);
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.False(skeleton.IsShown);
            Assert.False(skeleton.IsVisible);

            // The next first read starts its own delay from its own beginning.
            skeleton.IsActive = true;
            Assert.Equal(1, clock.Pending);
            Assert.False(skeleton.IsShown);
            clock.Advance(Assert.NotNull(Motion.TimeOf(Motion.WorkingDelayKey)));
            Assert.True(skeleton.IsShown);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task EveryRow_IsAPictureAndThreeGreyBars_OfTheHeightOfARow_AndTheSameRowsAreTheSameBars() => WpfTestHost.RunAsync(async () =>
    {
        var first = new Skeleton { Scheduler = new ManualStatusScheduler(), RowCount = 5, RowHeight = 30, IsActive = true, Width = 500 };
        var second = new Skeleton { Scheduler = new ManualStatusScheduler(), RowCount = 5, RowHeight = 30, IsActive = true, Width = 500 };
        var window = ControlHost.Show(first, second);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var rows = ControlHost.Part<Panel>(first, Skeleton.RowsPartName).Children.OfType<Grid>().ToList();
            var colour = ControlHost.ThemeColor(first, "CardStrokeColorDefaultBrush");

            Assert.Equal(5, rows.Count);
            Assert.All(rows, row =>
            {
                // A picture of 16 px and three bars; every bar is a rounded grey shape in the colour of the strokes of the theme.
                Assert.Equal(30d, row.Height);
                var bars = row.Children.OfType<Border>().ToList();
                Assert.Equal(4, bars.Count);
                Assert.Equal(new Size(16, 16), new Size(bars[0].Width, bars[0].Height));
                Assert.All(bars.Skip(1), bar => Assert.Equal(10d, bar.Height));
                Assert.All(bars, bar =>
                {
                    Assert.Equal(colour, ControlHost.ColorOf(bar.Background));
                    Assert.True(bar.CornerRadius.TopLeft >= 3);
                });
            });

            // The rows differ from each other (a list of names is not a list of equal bars) and never change by themselves:
            // the same rows of another skeleton are the same bars.
            var widths = rows.Select(row => row.Children.OfType<Border>().ElementAt(1).Width).ToList();
            Assert.True(widths.Distinct().Count() > 1, "every row has the same bars");
            var other = ControlHost.Part<Panel>(second, Skeleton.RowsPartName).Children.OfType<Grid>().ToList();
            Assert.Equal(
                rows.Select(row => row.Children.OfType<Border>().Select(bar => bar.Width).ToList()),
                other.Select(row => row.Children.OfType<Border>().Select(bar => bar.Width).ToList()));

            // A change of the height or the count builds the rows again.
            first.RowHeight = 40;
            first.RowCount = 3;
            window.UpdateLayout();
            var rebuilt = ControlHost.Part<Panel>(first, Skeleton.RowsPartName).Children.OfType<Grid>().ToList();
            Assert.Equal(3, rebuilt.Count);
            Assert.All(rebuilt, row => Assert.Equal(40d, row.Height));
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(8, 8)]
    [InlineData(12, 12)]
    [InlineData(13, 12)]
    [InlineData(500, 12)]
    public Task ThereAreAtLeastOneRow_AndAtMostTwelve(int asked, int shown) => WpfTestHost.RunAsync(() =>
    {
        var skeleton = new Skeleton { RowCount = asked };

        Assert.Equal(shown, skeleton.RowCount);
        Assert.Equal(12, Skeleton.MaxRows);
        Assert.Equal(8, new Skeleton().RowCount);
        Assert.Equal(26d, new Skeleton().RowHeight);
        return Task.CompletedTask;
    });

    [Fact]
    public Task WithoutMotion_TheRowsAreACalmStaticPicture() => WpfTestHost.RunAsync(async () =>
    {
        var clock = new ManualStatusScheduler();
        var skeleton = new Skeleton { Scheduler = clock, IsActive = true };
        var window = ControlHost.Show(skeleton);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            clock.Advance(Assert.NotNull(Motion.TimeOf(Motion.WorkingDelayKey)));
            window.UpdateLayout();

            // The one switch is off in every UI test: the rows are drawn and stand still.
            Assert.False(Motion.IsEnabled);
            Assert.True(skeleton.IsShown);
            var rows = ControlHost.Part<Panel>(skeleton, Skeleton.RowsPartName);
            Assert.True(rows.IsVisible);
            Assert.False(skeleton.IsPulsing);
            ControlHost.AssertAtRest(rows);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task WithMotion_TheRowsBreathe_OnlyWhileTheyAreDrawnAndVisible_AndLeaveNoClockBehind() => WpfTestHost.RunAsync(async () =>
    {
        using var motion = MotionScope.On();
        var clock = new ManualStatusScheduler();
        var skeleton = new Skeleton { Scheduler = clock, IsActive = true };
        var host = new Border { Child = skeleton };
        var window = ControlHost.Show(host);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var rows = ControlHost.Part<Panel>(skeleton, Skeleton.RowsPartName);

            // Waiting for the delay: nothing is drawn, so nothing moves.
            Assert.False(skeleton.IsPulsing);
            Assert.False(rows.HasAnimatedProperties);

            // Drawn: the opacity of the rows - and only of the rows - goes down and up, on and on.
            clock.Advance(Assert.NotNull(Motion.TimeOf(Motion.WorkingDelayKey)));
            window.UpdateLayout();
            Assert.True(skeleton.IsShown);
            Assert.True(skeleton.IsPulsing);
            Assert.True(rows.HasAnimatedProperties);
            Assert.False(skeleton.HasAnimatedProperties);
            Assert.False(skeleton.IsHitTestVisible);

            // Hidden by its host - a closed tab, a collapsed row: no clock runs for what nobody sees.
            host.Visibility = Visibility.Collapsed;
            Assert.False(skeleton.IsVisible);
            Assert.False(skeleton.IsPulsing);
            Assert.False(rows.HasAnimatedProperties);
            host.Visibility = Visibility.Visible;
            Assert.True(skeleton.IsPulsing);
            Assert.True(rows.HasAnimatedProperties);

            // The rows came: the pulse is gone and the rows have their base values.
            skeleton.IsActive = false;
            Assert.False(skeleton.IsPulsing);
            Assert.False(rows.HasAnimatedProperties);
            ControlHost.AssertAtRest(rows);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task ASkeleton_IsDecorative_NotForTheKeyboard_NotForThePointer_NotForAScreenReader() => WpfTestHost.RunAsync(async () =>
    {
        var clock = new ManualStatusScheduler();
        var skeleton = new Skeleton { Scheduler = clock, IsActive = true };
        var window = ControlHost.Show(skeleton);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            clock.Advance(Assert.NotNull(Motion.TimeOf(Motion.WorkingDelayKey)));
            window.UpdateLayout();

            Assert.False(skeleton.Focusable);
            Assert.False(skeleton.IsTabStop);
            Assert.False(skeleton.IsHitTestVisible);
            Assert.True(skeleton.ClipToBounds);
            Assert.Null(UIElementAutomationPeer.CreatePeerForElement(skeleton));
            Assert.Empty(UiWait.Descendants<Control>(skeleton));
        }
        finally
        {
            window.Close();
        }
    });
}
