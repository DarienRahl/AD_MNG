using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AdMng.App.Behaviors;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// The first motion of track V (stage V1, docs/dev/v-interface-renewal.md 5): the tokens of Themes/Motion.xaml, the one
/// switch (<see cref="MotionService"/> and <see cref="Motion.IsEnabled"/>, off in the UI test host), and the behaviours
/// of Behaviors/Motion.cs - hover layer, entrances, a toast on its way out. Motion is off in every UI test; the tests
/// of the motion itself switch it on for a few lines (<see cref="MotionScope"/>) and assert that an animation was
/// started (properties of the element), never what a frame looks like.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class MotionUiTests
{
    private const string MotionDictionary = "Themes/Motion.xaml";
    private const string TokensDictionary = "Themes/Tokens.xaml";

    /// <summary>The durations of design note 5.1, in milliseconds.</summary>
    private static readonly (string Key, int Milliseconds)[] Durations =
    [
        ("Motion.Hover", 83),
        ("Motion.Press", 120),
        ("Motion.Swap", 150),
        ("Motion.Fast", 167),
        ("Motion.Appear", 250),
        ("Motion.Disappear", 167),
        ("Motion.Entrance", 200),
        ("Motion.Expand", 333),
        ("Motion.Collapse", 167),
        ("Motion.Highlight", 2000),
        ("Motion.Shimmer", 1600),
        ("Motion.HoverIntent", 500),
    ];

    private static readonly (string Key, double Pixels)[] Offsets =
    [
        ("Motion.Offset.Page", 24d),
        ("Motion.Offset.Panel", 8d),
        ("Motion.Offset.Toast", 16d),
    ];

    private static readonly string[] Easings = ["Motion.Ease.Enter", "Motion.Ease.Exit", "Motion.Ease.Change"];

    [Fact]
    public Task MotionTokens_AreTheDocumentedTable_InOneDictionary() => WpfTestHost.RunAsync(() =>
    {
        foreach (var (key, milliseconds) in Durations)
        {
            var duration = Assert.IsType<Duration>(Application.Current.FindResource(key));
            Assert.True(duration.HasTimeSpan, $"{key} is not a time span");
            Assert.True(TimeSpan.FromMilliseconds(milliseconds) == duration.TimeSpan, $"{key} is {duration.TimeSpan}, not {milliseconds} ms");
        }

        foreach (var (key, pixels) in Offsets)
        {
            Assert.Equal(pixels, Assert.IsType<double>(Application.Current.FindResource(key)));
        }

        Assert.Equal(EasingMode.EaseOut, Assert.IsType<CubicEase>(Application.Current.FindResource("Motion.Ease.Enter")).EasingMode);
        Assert.Equal(EasingMode.EaseIn, Assert.IsType<CubicEase>(Application.Current.FindResource("Motion.Ease.Exit")).EasingMode);
        Assert.Equal(EasingMode.EaseInOut, Assert.IsType<QuadraticEase>(Application.Current.FindResource("Motion.Ease.Change")).EasingMode);

        // One dictionary with exactly these keys (a new token is added here on purpose), merged after the design tokens.
        var merged = Application.Current.Resources.MergedDictionaries.ToList();
        var dictionary = Assert.Single(merged, d => IsDictionary(d, MotionDictionary));
        string[] expected = [.. Durations.Select(d => d.Key), .. Offsets.Select(o => o.Key), .. Easings];
        Assert.Equal(
            expected.Order(StringComparer.Ordinal).ToArray(),
            dictionary.Keys.OfType<string>().Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(expected.Length, dictionary.Count);
        var tokens = merged.FindIndex(d => IsDictionary(d, TokensDictionary));
        Assert.True(tokens >= 0, "Themes/Tokens.xaml is not merged");
        Assert.True(tokens < merged.IndexOf(dictionary), "Themes/Motion.xaml must be merged after Themes/Tokens.xaml");

        // What the code reads by key: the time of a token, and nothing for a key that is not there.
        Assert.Equal(TimeSpan.FromMilliseconds(83), Motion.TimeOf(Motion.HoverKey));
        Assert.Equal(TimeSpan.FromMilliseconds(167), Motion.TimeOf(Motion.DisappearKey));
        Assert.Null(Motion.TimeOf("Motion.NoSuchToken"));
        return Task.CompletedTask;
    });

    [Fact]
    public Task MotionIsOff_InTheTestHost_WhateverTheSettingSays() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var service = context.Services.GetRequiredService<MotionService>();
        var settings = context.Services.GetRequiredService<ISettingsStore>();

        // The preference of a new profile was applied at start; the host forced motion off before the first window.
        Assert.Equal(MotionPreference.FollowWindows, settings.Current.Motion);
        Assert.Equal(MotionPreference.FollowWindows, service.Preference);
        Assert.True(service.IsForcedOff);
        Assert.False(service.IsOn);
        Assert.False(Motion.IsEnabled);

        // "On" in the settings reaches the service at once and still moves nothing in a forced-off run.
        await settings.UpdateAsync(s => s.Motion = MotionPreference.On, TestContext.Current.CancellationToken);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(MotionPreference.On, service.Preference);
        Assert.True(service.IsForcedOff);
        Assert.False(service.IsOn);
        Assert.False(Motion.IsEnabled);

        // A second connection window shares the one service.
        Assert.Same(service, context.CreateConnection().GetRequiredService<MotionService>());
        context.AssertHealthy();
    });

    [Fact]
    public Task MotionService_FollowsTheRule_AndPublishesTheAnswerToTheBehaviours() => WpfTestHost.RunAsync(() =>
    {
        Assert.False(Motion.IsEnabled);
        var windows = true;
        var hardware = true;
        var asked = 0;
        var service = new MotionService(new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        try
        {
            service.WindowsAnimations = () =>
            {
                asked++;
                return windows;
            };
            service.HardwareAccelerated = () => hardware;

            // Nothing moves before the settings were applied.
            Assert.False(service.IsForcedOff);
            Assert.False(service.IsOn);
            Assert.False(Motion.IsEnabled);

            // Follow Windows: "Animation effects" and hardware acceleration, both.
            service.Apply(MotionPreference.FollowWindows);
            Assert.True(service.IsOn);
            Assert.True(Motion.IsEnabled);
            windows = false;
            service.Refresh();
            Assert.False(service.IsOn);
            Assert.False(Motion.IsEnabled);
            windows = true;
            hardware = false;
            service.Refresh();
            Assert.False(service.IsOn);
            Assert.False(Motion.IsEnabled);
            hardware = true;
            service.Refresh();
            Assert.True(service.IsOn);
            Assert.True(asked > 0);

            // On and Off are the operator's word: Windows is not even asked.
            windows = false;
            hardware = false;
            asked = 0;
            service.Apply(MotionPreference.On);
            Assert.Equal(MotionPreference.On, service.Preference);
            Assert.True(service.IsOn);
            Assert.True(Motion.IsEnabled);
            service.Apply(MotionPreference.Off);
            Assert.False(service.IsOn);
            Assert.False(Motion.IsEnabled);
            Assert.Equal(0, asked);

            // A forced switch-off wins over everything, for the rest of the run.
            windows = true;
            hardware = true;
            service.Apply(MotionPreference.On);
            Assert.True(Motion.IsEnabled);
            service.ForceOff();
            Assert.True(service.IsForcedOff);
            Assert.False(service.IsOn);
            Assert.False(Motion.IsEnabled);
            service.Apply(MotionPreference.On);
            service.Apply(MotionPreference.FollowWindows);
            Assert.False(service.IsOn);
            Assert.False(Motion.IsEnabled);
        }
        finally
        {
            service.Dispose();
            Motion.IsEnabled = false;
        }

        // A service created forced off (ADMNG_UI_TESTS=1, the smoke test) never switches motion on; a disposed one
        // leaves the switch off and ignores what comes later.
        var forced = new MotionService(new WpfUiDispatcher(Dispatcher.CurrentDispatcher), forcedOff: true);
        try
        {
            forced.WindowsAnimations = static () => true;
            forced.HardwareAccelerated = static () => true;
            forced.Apply(MotionPreference.On);
            Assert.True(forced.IsForcedOff);
            Assert.False(forced.IsOn);
            Assert.False(Motion.IsEnabled);
        }
        finally
        {
            forced.Dispose();
            Motion.IsEnabled = false;
        }

        forced.Apply(MotionPreference.On);
        service.Apply(MotionPreference.On);
        service.Refresh();
        Assert.False(Motion.IsEnabled);

        // The variable CI sets for UI test runs.
        Assert.Equal("ADMNG_UI_TESTS", MotionService.TestRunVariable);
        Assert.True(MotionService.IsTestRun("1"));
        Assert.True(MotionService.IsTestRun(" 1 "));
        Assert.False(MotionService.IsTestRun(null));
        Assert.False(MotionService.IsTestRun(string.Empty));
        Assert.False(MotionService.IsTestRun("0"));
        Assert.False(MotionService.IsTestRun("true"));
        return Task.CompletedTask;
    });

    [Fact]
    public Task Shown_SetsTheFinalOpacityAtOnce_WhenMotionIsOff() => WpfTestHost.RunAsync(() =>
    {
        var layer = new Border { Width = 40, Height = 20, Opacity = 0 };
        var window = Show(layer);
        try
        {
            Assert.False(Motion.IsEnabled);
            Assert.True(layer.IsVisible);

            Motion.SetShown(layer, true);
            Assert.Equal(1d, layer.Opacity);
            Assert.False(layer.HasAnimatedProperties);
            Assert.False(Motion.IsRunning(layer));

            Motion.SetShown(layer, false);
            Assert.Equal(0d, layer.Opacity);
            Assert.False(layer.HasAnimatedProperties);
            Assert.False(Motion.IsRunning(layer));

            // Input is never touched (rule 1 of 5.3).
            Assert.True(layer.IsHitTestVisible);
            Assert.True(layer.IsEnabled);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Shown_StartsAnAnimation_OnlyWhileMotionIsOn_AndOnlyOnAVisibleElement() => WpfTestHost.RunAsync(() =>
    {
        var layer = new Border { Width = 40, Height = 20, Opacity = 0 };
        var offScreen = new Border { Opacity = 0 };
        var window = Show(layer);
        try
        {
            using (MotionScope.On())
            {
                Motion.SetShown(layer, true);
                Assert.True(layer.HasAnimatedProperties, "no animation was started on the hover layer");
                Assert.True(Motion.IsRunning(layer));

                // The opposite change replaces the running animation (it does not wait for it).
                Motion.SetShown(layer, false);
                Assert.True(layer.HasAnimatedProperties);
                Assert.True(Motion.IsRunning(layer));

                // An element that is not on screen gets its final value directly.
                Assert.False(offScreen.IsVisible);
                Motion.SetShown(offScreen, true);
                Assert.False(offScreen.HasAnimatedProperties);
                Assert.Equal(1d, offScreen.Opacity);
            }

            // Motion is off again: the next change takes the running animation off and sets the final value.
            Assert.False(Motion.IsEnabled);
            Motion.SetShown(layer, true);
            Assert.False(layer.HasAnimatedProperties);
            Assert.False(Motion.IsRunning(layer));
            Assert.Equal(1d, layer.Opacity);
            Motion.SetShown(layer, false);
            Assert.False(layer.HasAnimatedProperties);
            Assert.Equal(0d, layer.Opacity);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Entrance_LeavesTheElementAtItsBaseValues_WhenMotionIsOff() => WpfTestHost.RunAsync(async () =>
    {
        var page = new Border { Width = 40, Height = 20 };
        var panel = new Border { Width = 40, Height = 20 };
        var toast = new Border { Width = 40, Height = 20 };
        Motion.SetEntrance(page, MotionEntrance.Page);
        Motion.SetEntrance(panel, MotionEntrance.Panel);
        Motion.SetEntrance(toast, MotionEntrance.Toast);
        var window = Show(page, panel, toast);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.False(Motion.IsEnabled);
            Border[] elements = [page, panel, toast];

            // Shown and loaded visible: nothing moved, nothing is left on the elements.
            Assert.All(elements, element => Assert.True(element.IsVisible));
            Assert.All(elements, AssertAtRest);

            // The other two triggers: the trigger value changes, the element becomes visible again.
            foreach (var element in elements)
            {
                Motion.SetEntranceTrigger(element, new object());
                AssertAtRest(element);
                element.Visibility = Visibility.Collapsed;
                AssertAtRest(element);
                element.Visibility = Visibility.Visible;
                AssertAtRest(element);
                Assert.True(element.IsHitTestVisible);
                Assert.True(element.IsEnabled);
            }
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Entrance_StartsOnItsThreeTriggers_OnlyWhileMotionIsOn() => WpfTestHost.RunAsync(async () =>
    {
        var page = new Border { Width = 40, Height = 20 };
        var panel = new Border { Width = 40, Height = 20 };
        var own = new Border { Width = 40, Height = 20, RenderTransform = new ScaleTransform(2, 2) };
        var late = new Border { Width = 40, Height = 20 };
        Motion.SetEntrance(page, MotionEntrance.Page);
        Motion.SetEntrance(panel, MotionEntrance.Panel);
        Motion.SetEntrance(own, MotionEntrance.Page);
        var window = Show(page, panel, own, late);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            AssertAtRest(page);
            AssertAtRest(panel);

            using (MotionScope.On())
            {
                // Trigger value: the page is moved by a transform of the behaviour and never faded.
                Motion.SetEntranceTrigger(page, new object());
                var move = Assert.IsType<TranslateTransform>(page.RenderTransform);
                Assert.True(move.HasAnimatedProperties, "the page is not moved");
                Assert.False(page.HasAnimatedProperties, "a page is never faded");
                Assert.True(Motion.IsRunning(page));

                // The panel fades in and rises.
                Motion.SetEntranceTrigger(panel, new object());
                Assert.True(panel.HasAnimatedProperties, "the panel is not faded");
                Assert.True(Assert.IsType<TranslateTransform>(panel.RenderTransform).HasAnimatedProperties, "the panel is not moved");
                Assert.True(Motion.IsRunning(panel));

                // A new trigger replaces the running entrance: the old transform is taken off, a new one runs.
                Motion.SetEntranceTrigger(page, new object());
                var again = Assert.IsType<TranslateTransform>(page.RenderTransform);
                Assert.NotSame(move, again);
                Assert.False(move.HasAnimatedProperties);
                Assert.True(again.HasAnimatedProperties);

                // Hidden: everything of the entrance is taken off. Visible again: it plays again.
                page.Visibility = Visibility.Collapsed;
                AssertAtRest(page);
                Assert.False(again.HasAnimatedProperties);
                page.Visibility = Visibility.Visible;
                Assert.True(Motion.IsRunning(page));
                Assert.True(Assert.IsType<TranslateTransform>(page.RenderTransform).HasAnimatedProperties);

                // A null trigger and an element that is not visible start nothing.
                panel.Visibility = Visibility.Collapsed;
                Motion.SetEntranceTrigger(panel, null);
                Motion.SetEntranceTrigger(panel, new object());
                AssertAtRest(panel);
                panel.Visibility = Visibility.Visible;
                Assert.True(Motion.IsRunning(panel));

                // An element with a render transform of its own is not moved; a page is not faded, so nothing starts.
                Motion.SetEntranceTrigger(own, new object());
                Assert.IsType<ScaleTransform>(own.RenderTransform);
                Assert.False(own.HasAnimatedProperties);
                Assert.False(Motion.IsRunning(own));

                // Setting the property on an element that is already on screen plays the entrance once.
                Assert.True(late.IsVisible);
                Motion.SetEntrance(late, MotionEntrance.Panel);
                Assert.True(Motion.IsRunning(late));
                Assert.True(late.HasAnimatedProperties);

                // Taking the entrance away takes everything of it off.
                Motion.SetEntrance(late, MotionEntrance.None);
                AssertAtRest(late);
            }

            // Motion is off again: the next trigger puts the elements at their place and leaves nothing on them.
            Assert.False(Motion.IsEnabled);
            Motion.SetEntranceTrigger(page, new object());
            Motion.SetEntranceTrigger(panel, new object());
            AssertAtRest(page);
            AssertAtRest(panel);
            Assert.IsType<ScaleTransform>(own.RenderTransform);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Leaving_FadesAndSlidesOut_OnlyWhileMotionIsOn_AndIsTakenOffWhenItIsWithdrawn() => WpfTestHost.RunAsync(async () =>
    {
        var toast = new Border { Width = 40, Height = 20 };
        Motion.SetEntrance(toast, MotionEntrance.Toast);
        var window = Show(toast);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);

            // Motion off: the owner removes the element at once, so nothing happens here.
            Motion.SetLeaving(toast, true);
            AssertAtRest(toast);
            Motion.SetLeaving(toast, false);
            AssertAtRest(toast);

            using (MotionScope.On())
            {
                Motion.SetLeaving(toast, true);
                var slide = Assert.IsType<TranslateTransform>(toast.RenderTransform);
                Assert.True(slide.HasAnimatedProperties, "the toast does not slide out");
                Assert.True(toast.HasAnimatedProperties, "the toast does not fade out");
                Assert.True(Motion.IsRunning(toast));
                Assert.True(toast.IsHitTestVisible);

                // An element on its way out does not enter again.
                Motion.SetEntranceTrigger(toast, new object());
                Assert.Same(slide, toast.RenderTransform);
                Assert.True(slide.HasAnimatedProperties);
                Assert.True(toast.HasAnimatedProperties);

                // Not leaving after all: everything of the exit is taken off.
                Motion.SetLeaving(toast, false);
                AssertAtRest(toast);
                Assert.False(slide.HasAnimatedProperties);

                // The exit takes over a running entrance where it stands: the same transform.
                Motion.SetEntranceTrigger(toast, new object());
                var entering = Assert.IsType<TranslateTransform>(toast.RenderTransform);
                Assert.True(entering.HasAnimatedProperties);
                Motion.SetLeaving(toast, true);
                Assert.Same(entering, toast.RenderTransform);
                Assert.True(entering.HasAnimatedProperties);
                Assert.True(toast.HasAnimatedProperties);
            }

            Motion.SetLeaving(toast, false);
            AssertAtRest(toast);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Motion_LeavesNoClockBehind_WhenItEnds() => WpfTestHost.RunAsync(async () =>
    {
        var panel = new Border { Width = 40, Height = 20 };
        var page = new Border { Width = 40, Height = 20 };
        var layer = new Border { Width = 40, Height = 20, Opacity = 0 };
        Motion.SetEntrance(panel, MotionEntrance.Panel);
        Motion.SetEntrance(page, MotionEntrance.Page);
        var window = Show(panel, page, layer);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            using (MotionScope.On())
            {
                Motion.SetEntranceTrigger(panel, new object());
                Motion.SetEntranceTrigger(page, new object());
                Motion.SetShown(layer, true);
                Assert.True(Motion.IsRunning(panel));
                Assert.True(Motion.IsRunning(page));
                Assert.True(Motion.IsRunning(layer));
            }

            // A clock of our own, started after them and longer than each of them (Motion.Entrance is 200 ms): when it
            // has ended, theirs have ended. Where animation clocks do not advance at all (a session without
            // rendering), the end of a motion cannot be observed and the test says so instead of failing.
            var ended = false;
            var probe = new TranslateTransform();
            var clock = new DoubleAnimation(0d, 1d, new Duration(TimeSpan.FromMilliseconds(400))) { FillBehavior = FillBehavior.Stop };
            clock.Completed += (_, _) => ended = true;
            probe.BeginAnimation(TranslateTransform.XProperty, clock);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!ended && DateTime.UtcNow < deadline)
            {
                await UiTestSupport.WaitForIdleAsync(rounds: 1);
            }

            Assert.SkipUnless(ended, "Animation clocks do not advance in this session: the end of a motion cannot be observed.");
            await UiTestSupport.WaitForIdleAsync(rounds: 2);

            // Base values again, the transform of the behaviour is gone, no clock is left; the hover layer keeps its
            // end value as a plain value.
            AssertAtRest(panel);
            AssertAtRest(page);
            Assert.False(Motion.IsRunning(layer));
            Assert.False(layer.HasAnimatedProperties);
            Assert.Equal(1d, layer.Opacity);
            GC.KeepAlive(probe);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task IconButton_HasAHoverLayer_AndStaysAButton() => WpfTestHost.RunAsync(async () =>
    {
        var plain = new Button { Content = "OK" };
        var button = new Button { Content = "", ToolTip = "Odśwież", Style = StyleOf("Ad.IconButton") };
        var small = new Button { Content = "", Style = StyleOf("Ad.SmallIconButton") };
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        var window = Show(plain, button, small);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.False(Motion.IsEnabled);

            // One template for the three styles: the small and the copy button inherit it.
            Assert.Same(StyleOf("Ad.IconButton"), StyleOf("Ad.SmallIconButton").BasedOn);
            Assert.Same(StyleOf("Ad.SmallIconButton"), StyleOf("Ad.CopyButton").BasedOn);
            Assert.Same(button.Template, small.Template);
            Assert.NotSame(plain.Template, button.Template);

            // The sizes of the styles are unchanged: 30 high and at least 32 wide, the small one 22 by at least 22.
            Assert.Equal(30d, button.ActualHeight);
            Assert.True(button.ActualWidth >= 32d, $"the icon button is {button.ActualWidth} wide");
            Assert.Equal(22d, small.ActualHeight);
            Assert.True(small.ActualWidth >= 22d, $"the small icon button is {small.ActualWidth} wide");

            foreach (var subject in (Button[])[button, small])
            {
                var hover = Assert.IsType<Border>(subject.Template.FindName("HoverLayer", subject));
                var presenter = Assert.IsType<ContentPresenter>(subject.Template.FindName("ContentPresenter", subject));

                // At rest: the layer is in the tree but transparent, its fill is the theme's subtle fill, nothing runs.
                Assert.Equal(0d, hover.Opacity);
                Assert.False(Motion.GetShown(hover));
                Assert.False(hover.HasAnimatedProperties);
                Assert.Equal(ThemeColor(subject, "SubtleFillColorSecondaryBrush"), ColorOf(hover.Background));
                Assert.Equal(new CornerRadius(4), hover.CornerRadius);
                Assert.Equal(ColorOf(subject.Foreground), ColorOf(TextElement.GetForeground(presenter)));

                // Still a button of the theme: transparent but there for the pointer, a tab stop with the theme's focus
                // visual, and its tool tip is shown when it is disabled.
                Assert.NotNull(subject.Background);
                Assert.True(subject.IsHitTestVisible);
                Assert.True(subject.Focusable);
                Assert.True(subject.IsTabStop);
                Assert.NotNull(subject.FocusVisualStyle);
                Assert.Same(plain.FocusVisualStyle, subject.FocusVisualStyle);
                Assert.True(ToolTipService.GetShowOnDisabled(subject));
            }

            // Pressed (here without the pointer over the button, as from the keyboard): the layer is shown at once in
            // the pressed fill; released: hidden again.
            var layer = (Border)button.Template.FindName("HoverLayer", button);
            SetPressed(button, true);
            Assert.True(Motion.GetShown(layer));
            Assert.Equal(1d, layer.Opacity);
            Assert.Equal(ThemeColor(button, "SubtleFillColorTertiaryBrush"), ColorOf(layer.Background));
            Assert.False(layer.HasAnimatedProperties);
            SetPressed(button, false);
            Assert.False(Motion.GetShown(layer));
            Assert.Equal(0d, layer.Opacity);
            Assert.Equal(ThemeColor(button, "SubtleFillColorSecondaryBrush"), ColorOf(layer.Background));

            // A click like the operator's (UI Automation invoke) raises Click once.
            var invoke = (IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!;
            invoke.Invoke();
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.Equal(1, clicks);

            // Disabled: the content takes the theme's disabled text colour, there is no fill, the tool tip stays.
            var content = (ContentPresenter)button.Template.FindName("ContentPresenter", button);
            button.IsEnabled = false;
            Assert.Equal(ThemeColor(button, "TextFillColorDisabledBrush"), ColorOf(TextElement.GetForeground(content)));
            Assert.Equal(0d, layer.Opacity);
            Assert.Equal("Odśwież", Assert.IsType<string>(button.ToolTip));
            Assert.True(ToolTipService.GetShowOnDisabled(button));
            button.IsEnabled = true;
            Assert.Equal(ColorOf(button.Foreground), ColorOf(TextElement.GetForeground(content)));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task MainWindow_DocumentAndDetailsPane_EnterWhenTheyAreShown_NotOnLiveData() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(window);
        await window.Shell.InitializeAsync();
        await UiTestSupport.WaitForIdleAsync();
        var shell = window.Shell;
        var document = Assert.IsType<ObjectListViewModel>(shell.SelectedTab);
        var page = Assert.IsType<ContentPresenter>(window.DocumentHost.ItemContainerGenerator.ContainerFromItem(document));
        var panel = window.DetailsPaneContent;

        // The wiring: page entrance on the container of a document, panel entrance on the content of the details
        // pane, no trigger bound to data, and the host clips what rises into it.
        Assert.True(window.DocumentHost.ClipToBounds);
        Assert.Equal(MotionEntrance.Page, Motion.GetEntrance(page));
        Assert.Null(Motion.GetEntranceTrigger(page));
        Assert.Equal(MotionEntrance.Panel, Motion.GetEntrance(panel));
        Assert.Null(Motion.GetEntranceTrigger(panel));
        Assert.Same(window.DetailsPane, panel.Parent);
        Assert.Equal(Visibility.Visible, window.DetailsPane.Visibility);
        Assert.True(page.IsVisible);
        Assert.True(panel.IsVisible);

        // The window was shown without motion: everything is at its place and nothing is left on the elements.
        AssertAtRest(page);
        AssertAtRest(panel);

        var visibilityChanges = 0;
        page.IsVisibleChanged += (_, _) => visibilityChanges++;
        panel.IsVisibleChanged += (_, _) => visibilityChanges++;
        using (MotionScope.On())
        {
            // Live data (rule 3 of 5.3): a refresh and a selection never show or hide the two elements, so they cannot
            // replay an entrance.
            await shell.RefreshCommand.ExecuteAsync(null);
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            document.SelectedItem = document.Items.FirstOrDefault(row => !row.IsPlaceholder);
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.Same(document, shell.SelectedTab);
            Assert.Equal(0, visibilityChanges);
            Assert.False(Motion.IsRunning(page));
            Assert.False(Motion.IsRunning(panel));

            // The tab is chosen: its document rises - moved, never faded.
            shell.SelectedTab = null;
            Assert.False(page.IsVisible);
            shell.SelectedTab = document;
            Assert.True(page.IsVisible);
            Assert.True(Motion.IsRunning(page), "the document does not enter when its tab is chosen");
            Assert.True(Assert.IsType<TranslateTransform>(page.RenderTransform).HasAnimatedProperties);
            Assert.False(page.HasAnimatedProperties, "a document is never faded");

            // The details pane is opened (what UpdateDetailsPane does): its content fades in and rises.
            window.DetailsPane.Visibility = Visibility.Collapsed;
            Assert.False(Motion.IsRunning(panel));
            window.DetailsPane.Visibility = Visibility.Visible;
            Assert.True(Motion.IsRunning(panel), "the content of the details pane does not enter when the pane is opened");
            Assert.True(panel.HasAnimatedProperties);
        }

        // Motion is off again: showing them once more puts them at their place.
        shell.SelectedTab = null;
        shell.SelectedTab = document;
        window.DetailsPane.Visibility = Visibility.Collapsed;
        window.DetailsPane.Visibility = Visibility.Visible;
        AssertAtRest(page);
        AssertAtRest(panel);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task Toast_IsRemovedAtOnce_WhenMotionIsOff() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(window);
        await window.Shell.InitializeAsync();
        await UiTestSupport.WaitForIdleAsync();
        var notifications = context.Services.GetRequiredService<NotificationService>();

        notifications.Show(NotificationSeverity.Success, "Zapisano zmiany: Żaneta Łęcka");
        window.UpdateLayout();
        var toast = Assert.Single(notifications.Toasts);
        var root = ToastRoot(window, toast);

        // The template asks for the toast entrance and follows IsLeaving; with motion off the toast is simply there.
        Assert.Equal(MotionEntrance.Toast, Motion.GetEntrance(root));
        Assert.False(Motion.GetLeaving(root));
        Assert.True(root.IsVisible);
        AssertAtRest(root);

        // Dismissed: gone at once, and it was never "leaving".
        toast.DismissCommand.Execute(null);
        Assert.False(toast.IsLeaving);
        Assert.Empty(notifications.Toasts);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task Toast_SlidesInAndLeavesBeforeItIsRemoved_WhenMotionIsOn() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(window);
        await window.Shell.InitializeAsync();
        await UiTestSupport.WaitForIdleAsync();
        var notifications = context.Services.GetRequiredService<NotificationService>();

        using (MotionScope.On())
        {
            // In: the toast fades and slides in as soon as its template is there.
            notifications.Show(NotificationSeverity.Info, "Odświeżono listę");
            window.UpdateLayout();
            var toast = Assert.Single(notifications.Toasts);
            var root = ToastRoot(window, toast);
            Assert.True(Motion.IsRunning(root), "the toast does not enter");
            Assert.True(root.HasAnimatedProperties);
            Assert.True(Assert.IsType<TranslateTransform>(root.RenderTransform).HasAnimatedProperties);

            // Out: dismissed, it is still in the list while it leaves, and the view follows IsLeaving.
            toast.DismissCommand.Execute(null);
            Assert.True(toast.IsLeaving);
            Assert.Contains(toast, notifications.Toasts);
            Assert.True(Motion.GetLeaving(root));
            Assert.True(Motion.IsRunning(root));
            Assert.True(root.IsHitTestVisible);

            // The service removes it after Motion.Disappear (a dispatcher timer).
            await UiWait.UntilAsync(() => notifications.Toasts.Count == 0, "the toast that left is removed");
        }

        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task NotificationService_KeepsALeavingToast_ForTheTimeOfItsExit_AndStaysSafe() => WpfTestHost.RunAsync(async () =>
    {
        // The service alone (no window): the list is all there is to see.
        await using var context = await UiTestContext.CreateAsync();
        var notifications = context.Services.GetRequiredService<NotificationService>();
        notifications.Show(NotificationSeverity.Info, "Pierwszy");
        notifications.Show(NotificationSeverity.Warning, "Drugi");
        var first = notifications.Toasts[0];
        var second = notifications.Toasts[1];

        using (MotionScope.On())
        {
            // Dismissed: leaving, still listed. Dismissing it again - from the button or the timeout - changes nothing.
            notifications.Dismiss(first);
            Assert.True(first.IsLeaving);
            Assert.False(second.IsLeaving);
            Assert.Equal([first, second], notifications.Toasts);
            first.DismissCommand.Execute(null);
            notifications.Dismiss(first);
            Assert.True(first.IsLeaving);
            Assert.Equal([first, second], notifications.Toasts);

            // Room for new toasts is made at once, also from a toast that is leaving: never more than the limit.
            for (var i = 0; i < NotificationService.MaxVisible; i++)
            {
                notifications.Show(NotificationSeverity.Info, $"Nowy {i}");
                Assert.True(notifications.Toasts.Count <= NotificationService.MaxVisible);
            }

            Assert.Equal(NotificationService.MaxVisible, notifications.Toasts.Count);
            Assert.DoesNotContain(first, notifications.Toasts);
            Assert.DoesNotContain(second, notifications.Toasts);
            Assert.Equal("Nowy 0", notifications.Toasts[0].Message);

            // The exit ends by a timer of Motion.Disappear: only then the toast is gone.
            var third = notifications.Toasts[0];
            notifications.Dismiss(third);
            Assert.True(third.IsLeaving);
            Assert.Contains(third, notifications.Toasts);
            await UiWait.UntilAsync(() => !notifications.Toasts.Contains(third), "the toast that left is removed");
            Assert.Equal(NotificationService.MaxVisible - 1, notifications.Toasts.Count);

            // Dismissing toasts that are gone is harmless.
            notifications.Dismiss(third);
            notifications.Dismiss(first);
            Assert.Equal(NotificationService.MaxVisible - 1, notifications.Toasts.Count);

            // The window closes while a toast is leaving: later dismissals remove at once, new toasts are dropped,
            // and the toast that was leaving is still removed by its timer.
            var leaving = notifications.Toasts[0];
            var last = notifications.Toasts[^1];
            notifications.Dismiss(leaving);
            Assert.True(leaving.IsLeaving);
            notifications.MarkClosed();
            notifications.Dismiss(last);
            Assert.False(last.IsLeaving);
            Assert.DoesNotContain(last, notifications.Toasts);
            notifications.Show(NotificationSeverity.Error, "Po zamknięciu okna");
            Assert.DoesNotContain(notifications.Toasts, toast => toast.Message == "Po zamknięciu okna");
            await UiWait.UntilAsync(() => !notifications.Toasts.Contains(leaving), "the toast that was leaving when the window closed is removed");
        }

        context.AssertHealthy();
    });

    /// <summary>The root element of the toast template of a toast shown in the main window.</summary>
    private static Border ToastRoot(MainWindow window, ToastViewModel toast)
    {
        var container = Assert.IsType<ContentPresenter>(window.ToastHost.ItemContainerGenerator.ContainerFromItem(toast));
        Assert.True(VisualTreeHelper.GetChildrenCount(container) == 1, "the toast template is not applied");
        return Assert.IsType<Border>(VisualTreeHelper.GetChild(container, 0));
    }

    /// <summary>
    /// The element is as if no motion had ever been on it: no motion of the behaviours runs, no animation clock is
    /// left, there is no render transform of the behaviour, and the opacity is the base value.
    /// </summary>
    private static void AssertAtRest(FrameworkElement element)
    {
        Assert.False(Motion.IsRunning(element), "a motion is still on the element");
        Assert.False(element.HasAnimatedProperties, "an animation clock is left on the element");
        Assert.Same(DependencyProperty.UnsetValue, element.ReadLocalValue(UIElement.RenderTransformProperty));
        Assert.True(element.RenderTransform.Value.IsIdentity, "the element is still moved");
        Assert.Equal(1d, element.Opacity);
    }

    private static Style StyleOf(string key) => Assert.IsType<Style>(Application.Current.FindResource(key));

    /// <summary>The colour of a solid brush (the brushes of the theme used here are solid in light and dark).</summary>
    private static Color ColorOf(Brush? brush) => Assert.IsType<SolidColorBrush>(brush).Color;

    /// <summary>The colour of a brush of the running theme, as the element finds it.</summary>
    private static Color ThemeColor(FrameworkElement element, string key) => ColorOf(element.FindResource(key) as Brush);

    /// <summary>IsPressed has a protected setter; the template trigger follows the property whoever sets it.</summary>
    private static void SetPressed(ButtonBase button, bool pressed) =>
        typeof(ButtonBase).GetProperty(nameof(ButtonBase.IsPressed), BindingFlags.Instance | BindingFlags.Public)!.SetValue(button, pressed);

    private static bool IsDictionary(ResourceDictionary dictionary, string path) =>
        dictionary.Source is not null && dictionary.Source.OriginalString.EndsWith(path, StringComparison.OrdinalIgnoreCase);

    /// <summary>A window with the elements in a column, shown off-screen (an element is visible only in a shown window).</summary>
    private static Window Show(params FrameworkElement[] elements)
    {
        var panel = new StackPanel { Background = Brushes.White };
        foreach (var element in elements)
        {
            element.HorizontalAlignment = HorizontalAlignment.Left;
            panel.Children.Add(element);
        }

        var window = new Window { Width = 320, Height = 240, Content = panel, WindowStyle = WindowStyle.ToolWindow };
        UiTestSupport.ShowOffScreen(window);
        return window;
    }

    /// <summary>
    /// Switches the one switch of the behaviours on for a part of a test and off again whatever happens (the host
    /// keeps motion off; a test that left it on would let every later test and screenshot see elements on their way).
    /// </summary>
    private sealed class MotionScope : IDisposable
    {
        private MotionScope() => Motion.IsEnabled = true;

        public static MotionScope On()
        {
            Assert.False(Motion.IsEnabled, "motion must be off in the UI test host");
            return new MotionScope();
        }

        public void Dispose() => Motion.IsEnabled = false;
    }
}
