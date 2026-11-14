using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AdMng.App.Behaviors;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.App.UiTests;

/// <summary>
/// The action button (track V, stage V2; docs/dev/v-interface-renewal.md 6.7 and goal G3 - cancel where you started):
/// its three states and what each shows, the one width of all states, what a press does in each, that it stays enabled
/// while its operation runs (so it keeps the keyboard focus), what UI Automation is told, and the cross-fade of its
/// contents (only with motion on); the accent variant and the variants for a dialog footer (stage V2b). Property checks
/// on the control and the named parts of its content template.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ActionButtonUiTests
{
    private const string FindNow = "Znajdź teraz";

    [Fact]
    public Task TheButton_ShowsItsThreeStates_InTheWidthOfItsWidestState() => WpfTestHost.RunAsync(async () =>
    {
        using var language = LanguageScope.Use("pl-PL");
        var button = new ActionButton { Content = FindNow };
        var plain = new Button { Content = FindNow };
        var window = ControlHost.Show(button, plain);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var parts = new Parts(button);
            Assert.False(Motion.IsEnabled);

            // The theme's button - same height, same focus visual, a tab stop - with a content template of ours. (The
            // template objects are not compared: a plain button takes the one of the theme dictionary of this moment,
            // and tests that switch the theme replace that dictionary.)
            Assert.Same(Application.Current.FindResource(ActionButton.StyleKey), button.Style);
            Assert.Same(Application.Current.FindResource("Ad.ActionButtonContent"), button.ContentTemplate);
            Assert.Same(plain.FocusVisualStyle, button.FocusVisualStyle);
            Assert.True(button.Focusable);
            Assert.True(button.IsTabStop);
            Assert.Equal(plain.ActualHeight, button.ActualHeight, 0.5);

            // What the two busy states say, in the language of the interface.
            Assert.Equal("Anuluj", parts.CancelText.Text);
            Assert.Equal("Zatrzymywanie…", parts.StoppingText.Text);

            // One width for all states: every content fits (with the padding of the button), and the button is wider
            // than a plain one with the same text - "Zatrzymywanie…" with its ring needs more than "Znajdź teraz".
            var width = button.ActualWidth;
            var height = button.ActualHeight;
            foreach (var content in parts.Contents)
            {
                Assert.True(content.ActualWidth > 0, "a content of the button was not laid out");
                Assert.True(width >= content.ActualWidth + 20, $"the button is {width} wide, a content {content.ActualWidth}");
            }

            Assert.True(parts.Stopping.ActualWidth > parts.Idle.ActualWidth, "the test needs a busy text that is wider than the text at rest");
            Assert.True(width > plain.ActualWidth, $"the action button is {width} wide, a plain one {plain.ActualWidth}");

            // At rest: the button's own content, no ring.
            Assert.Equal(ActionButtonState.Idle, button.State);
            Assert.False(button.IsBusy);
            AssertShown(parts, parts.Idle);
            AssertRings(parts, running: false, stopping: false);
            Assert.True(button.IsHitTestVisible);

            // The operation runs: the ring and "Cancel"; nothing changed its size.
            button.IsRunning = true;
            window.UpdateLayout();
            Assert.Equal(ActionButtonState.Running, button.State);
            Assert.True(button.IsBusy);
            AssertShown(parts, parts.Running);
            AssertRings(parts, running: true, stopping: false);
            Assert.True(button.IsEnabled);
            Assert.True(button.IsHitTestVisible);
            Assert.Equal(width, button.ActualWidth);
            Assert.Equal(height, button.ActualHeight);

            // Asked to stop (here: reported by the view model): the ring and "Stopping…" - enabled, so it keeps the
            // focus, but the pointer gets no answer.
            button.IsCancelling = true;
            window.UpdateLayout();
            Assert.Equal(ActionButtonState.Cancelling, button.State);
            Assert.True(button.IsBusy);
            AssertShown(parts, parts.Stopping);
            AssertRings(parts, running: false, stopping: true);
            Assert.True(button.IsEnabled);
            Assert.False(button.IsHitTestVisible);
            Assert.Equal(width, button.ActualWidth);
            Assert.Equal(height, button.ActualHeight);

            // The operation ended.
            button.IsRunning = false;
            window.UpdateLayout();
            Assert.Equal(ActionButtonState.Idle, button.State);
            Assert.False(button.IsBusy);
            AssertShown(parts, parts.Idle);
            AssertRings(parts, running: false, stopping: false);
            Assert.True(button.IsHitTestVisible);
            Assert.Equal(width, button.ActualWidth);
            button.IsCancelling = false;
            Assert.Equal(ActionButtonState.Idle, button.State);
            AssertShown(parts, parts.Idle);

            // "Cancelling" alone is not a running operation.
            button.IsCancelling = true;
            Assert.Equal(ActionButtonState.Idle, button.State);
            button.IsCancelling = false;
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task APress_Starts_ThenCancels_AndStoppingCannotBePressed() => WpfTestHost.RunAsync(async () =>
    {
        using var language = LanguageScope.Use("en-US");
        var started = 0;
        var cancelled = 0;
        var clicks = 0;
        var button = new ActionButton { Content = "Find Now" };
        button.Command = new RelayCommand(() =>
        {
            started++;
            button.IsRunning = true;
        });
        button.CancelCommand = new RelayCommand(() => cancelled++);
        button.Click += (_, _) => clicks++;
        var window = ControlHost.Show(button);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);

            // A normal button for UI Automation, without children; its name says what a press does now.
            var peer = Assert.IsAssignableFrom<AutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(button));
            Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
            Assert.Equal("Button", peer.GetClassName());
            Assert.Empty(peer.GetChildren() ?? []);
            Assert.Equal("Find Now", peer.GetName());
            Assert.True(peer.IsEnabled());

            // Pressed at rest: the start command runs (and Click is raised, as for any button).
            ControlHost.Press(button);
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.Equal(1, started);
            Assert.Equal(1, clicks);
            Assert.Equal(ActionButtonState.Running, button.State);
            Assert.Equal("Cancel: Find Now", peer.GetName());
            Assert.True(peer.IsEnabled());

            // Pressed while the operation runs: the cancel command runs - cancel where you started - and the button
            // says "Stopping…" by itself, whether or not the view model reports the cancellation.
            ControlHost.Press(button);
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.Equal(1, cancelled);
            Assert.Equal(1, started);
            Assert.Equal(1, clicks);
            Assert.False(button.IsCancelling);
            Assert.Equal(ActionButtonState.Cancelling, button.State);
            Assert.Equal("Stopping…", peer.GetName());

            // Stopping: enabled (it keeps the focus), but UI Automation is told there is nothing to press, and a press
            // that reaches the control anyway (Space on the focused button) does nothing.
            Assert.True(button.IsEnabled);
            Assert.False(peer.IsEnabled());
            Assert.Throws<ElementNotEnabledException>(() => ControlHost.Press(button));
            Click(button);
            Assert.Equal(1, cancelled);
            Assert.Equal(1, started);
            Assert.Equal(ActionButtonState.Cancelling, button.State);

            // The operation ended: the button is what it was, and starts again.
            button.IsRunning = false;
            Assert.Equal(ActionButtonState.Idle, button.State);
            Assert.Equal("Find Now", peer.GetName());
            Assert.True(peer.IsEnabled());
            ControlHost.Press(button);
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.Equal(2, started);
            Assert.Equal(ActionButtonState.Running, button.State);

            // A cancel command that cannot execute: the press does nothing and the button stays "Cancel".
            var allowed = false;
            button.CancelCommand = new RelayCommand(() => cancelled++, () => allowed);
            Click(button);
            Assert.Equal(1, cancelled);
            Assert.Equal(ActionButtonState.Running, button.State);
            allowed = true;
            Click(button);
            Assert.Equal(2, cancelled);
            Assert.Equal(ActionButtonState.Cancelling, button.State);

            // The view model reports the cancellation too - and then that it is over while an operation still runs:
            // the cancelled operation was replaced by a new one, which can be cancelled again.
            button.IsCancelling = true;
            Assert.Equal(ActionButtonState.Cancelling, button.State);
            button.IsCancelling = false;
            Assert.True(button.IsRunning);
            Assert.Equal(ActionButtonState.Running, button.State);
            Assert.Equal("Cancel: Find Now", peer.GetName());
            Click(button);
            Assert.Equal(3, cancelled);
            Assert.Equal(ActionButtonState.Cancelling, button.State);
            button.IsRunning = false;
            Assert.Equal(ActionButtonState.Idle, button.State);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task TheButton_StaysEnabledWhileItsOperationRuns_WhateverItsStartCommandSays() => WpfTestHost.RunAsync(async () =>
    {
        // What keeps the keyboard focus on the button: a disabled element loses it. (The focus itself is not asserted:
        // the windows of the tests are never the active window.)
        var canStart = true;
        RelayCommand? start = null;
        start = new RelayCommand(
            () =>
            {
                // Like an asynchronous command while it runs.
                canStart = false;
                start!.NotifyCanExecuteChanged();
            },
            () => canStart);
        var button = new ActionButton { Content = FindNow, Command = start };
        var plain = new Button { Content = FindNow, Command = start };
        var window = ControlHost.Show(button, plain);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.True(button.IsEnabled);
            Assert.True(plain.IsEnabled);

            // Pressed: the command cannot execute any more. A plain button is disabled at once; the action button is
            // not - not even in the moment before the operation said that it runs.
            Click(button);
            Assert.False(start.CanExecute(null));
            Assert.False(plain.IsEnabled);
            Assert.Equal(ActionButtonState.Idle, button.State);
            Assert.True(button.IsEnabled);

            // The operation runs, then is asked to stop: enabled all the time.
            button.IsRunning = true;
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.Equal(ActionButtonState.Running, button.State);
            Assert.True(button.IsEnabled);
            Assert.False(plain.IsEnabled);
            button.IsCancelling = true;
            Assert.True(button.IsEnabled);

            // The operation ended, its command has not said so yet: still enabled for a moment (the two normally end
            // together), then the button follows its command.
            button.IsCancelling = false;
            button.IsRunning = false;
            Assert.Equal(ActionButtonState.Idle, button.State);
            Assert.True(button.IsEnabled);
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.False(button.IsEnabled);

            // The command can execute again.
            canStart = true;
            start.NotifyCanExecuteChanged();
            Assert.True(button.IsEnabled);
            Assert.True(plain.IsEnabled);

            // A press that never becomes a running operation: after the moment of grace the button follows its command.
            Click(button);
            Assert.False(start.CanExecute(null));
            Assert.True(button.IsEnabled);
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.Equal(ActionButtonState.Idle, button.State);
            Assert.False(button.IsEnabled);

            // Disabled from outside, the button is disabled in every state.
            canStart = true;
            start.NotifyCanExecuteChanged();
            button.IsRunning = true;
            button.IsEnabled = false;
            Assert.False(button.IsEnabled);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task EnterFromElsewhereInTheWindow_Starts_ButNeverCancels() => WpfTestHost.RunAsync(async () =>
    {
        var started = 0;
        var cancelled = 0;
        var button = new ActionButton
        {
            Content = FindNow,
            IsDefault = true,
            Command = new RelayCommand(() => started++),
            CancelCommand = new RelayCommand(() => cancelled++),
        };
        var window = ControlHost.Show(button);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var source = Assert.IsAssignableFrom<PresentationSource>(PresentationSource.FromVisual(button));
            Assert.False(button.IsKeyboardFocused);

            // Enter typed in a text box of the window reaches the default button as its access key: at rest it starts
            // the operation.
            AccessKeyManager.ProcessKey(source, "\r", false);
            Assert.Equal(1, started);
            Assert.Equal(0, cancelled);

            // Typed again while the operation runs: nothing happens - Enter never stops what it started.
            button.IsRunning = true;
            Assert.True(button.IsEnabled);
            AccessKeyManager.ProcessKey(source, "\r", false);
            Assert.Equal(1, started);
            Assert.Equal(0, cancelled);
            Assert.Equal(ActionButtonState.Running, button.State);

            // A press of the button itself cancels.
            Click(button);
            Assert.Equal(1, cancelled);
            Assert.Equal(ActionButtonState.Cancelling, button.State);

            // At rest again, Enter starts again.
            button.IsRunning = false;
            AccessKeyManager.ProcessKey(source, "\r", false);
            Assert.Equal(2, started);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task TheContents_CrossFade_OnlyWhileMotionIsOn_AndNothingAnimatesTheSize() => WpfTestHost.RunAsync(async () =>
    {
        var button = new ActionButton { Content = FindNow };
        var window = ControlHost.Show(button);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var parts = new Parts(button);
            var width = button.ActualWidth;
            AssertShown(parts, parts.Idle);

            using (MotionScope.On())
            {
                // The operation starts: the content at rest fades out, the running one fades in (design note 5.2,
                // row 2). The logical state does not wait: the ring turns and the button can be pressed at once.
                button.IsRunning = true;
                Assert.Equal(ActionButtonState.Running, button.State);
                Assert.True(parts.Idle.HasAnimatedProperties, "the content at rest is not faded out");
                Assert.True(parts.Running.HasAnimatedProperties, "the running content is not faded in");
                Assert.True(Motion.IsRunning(parts.Idle));
                Assert.True(Motion.IsRunning(parts.Running));
                Assert.False(parts.Stopping.HasAnimatedProperties);
                Assert.True(parts.RunningRing.IsTurning);
                Assert.True(button.IsEnabled);
                Assert.True(button.IsHitTestVisible);

                // Only the opacity of the contents moves: the button and its size are not animated.
                Assert.False(button.HasAnimatedProperties);
                window.UpdateLayout();
                Assert.Equal(width, button.ActualWidth);
            }

            // Motion is off again: every further swap sets its final values at once and leaves no clock.
            button.IsCancelling = true;
            Assert.Equal(0d, parts.Running.Opacity);
            Assert.False(parts.Running.HasAnimatedProperties);
            Assert.Equal(1d, parts.Stopping.Opacity);
            Assert.False(parts.Stopping.HasAnimatedProperties);
            button.IsRunning = false;
            button.IsCancelling = false;
            AssertShown(parts, parts.Idle);
            Assert.Equal(width, button.ActualWidth);

            using (MotionScope.On())
            {
                // From "Cancel" to "Stopping…": the two busy contents cross-fade as well.
                button.IsRunning = true;
                button.IsCancelling = true;
                Assert.Equal(ActionButtonState.Cancelling, button.State);
                Assert.True(parts.Running.HasAnimatedProperties, "the running content is not faded out");
                Assert.True(parts.Stopping.HasAnimatedProperties, "the stopping content is not faded in");
                Assert.True(parts.StoppingRing.IsTurning);
                Assert.False(parts.RunningRing.IsTurning);
                Assert.False(button.HasAnimatedProperties);
            }
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AGlyph_IsShownBeforeTheTextAtRest_OnlyWhenTheButtonHasOne() => WpfTestHost.RunAsync(async () =>
    {
        var button = new ActionButton { Content = "Odśwież" };
        var window = ControlHost.Show(button);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var glyph = Assert.IsType<TextBlock>(button.FindContentPart("IdleGlyph"));
            var idle = Assert.IsAssignableFrom<FrameworkElement>(button.FindContentPart("IdleContent"));
            Assert.Null(button.Glyph);
            Assert.Equal(Visibility.Collapsed, glyph.Visibility);
            var width = idle.ActualWidth;
            Assert.True(width > 0, "the content at rest was not laid out");

            // (The content at rest is measured, not the button: the button is as wide as its widest state.)
            button.Glyph = Glyphs.Refresh;
            window.UpdateLayout();
            Assert.Equal(Visibility.Visible, glyph.Visibility);
            Assert.Equal(Glyphs.Refresh, glyph.Text);
            Assert.True(idle.ActualWidth > width, "the glyph takes no room");
            Assert.Equal(ActionButtonState.Idle, button.State);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task TheAccentVariant_IsTheThemesAccentButton_WithTheSameThreeStates() => WpfTestHost.RunAsync(async () =>
    {
        using var language = LanguageScope.Use("en-US");

        // The primary action of a window (stage V2b): the style is taken explicitly, as Ad.AccentDialogButton is by a
        // plain button.
        var accentStyle = Assert.IsType<Style>(Application.Current.FindResource("Ad.AccentActionButton"));
        var button = new ActionButton { Content = "Preview", Style = accentStyle };
        var accentPlain = new Button { Content = "Preview", Style = (Style)Application.Current.FindResource("Ad.AccentDialogButton") };
        var plainAction = new ActionButton { Content = "Preview" };
        var started = 0;
        var cancelled = 0;
        button.Command = new RelayCommand(() => started++);
        button.CancelCommand = new RelayCommand(() => cancelled++);
        var window = ControlHost.Show(button, accentPlain, plainAction);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var parts = new Parts(button);

            // The explicit style stands (the control's own default is a resource reference that a local value replaces),
            // it is based on the accent style the dialog buttons are based on, and it has the content of every action
            // button.
            Assert.Same(accentStyle, button.Style);
            Assert.Equal(typeof(ActionButton), accentStyle.TargetType);
            Assert.Same(accentPlain.Style.BasedOn, accentStyle.BasedOn);
            Assert.NotNull(accentStyle.BasedOn);
            Assert.Same(Application.Current.FindResource("Ad.ActionButtonContent"), button.ContentTemplate);

            // It looks like the accent button next to it - the same fill, text colour and height - and not like the
            // plain action button.
            Assert.Equal(ControlHost.ColorOf(accentPlain.Background), ControlHost.ColorOf(button.Background));
            Assert.Equal(ControlHost.ColorOf(accentPlain.Foreground), ControlHost.ColorOf(button.Foreground));
            Assert.NotEqual(ControlHost.ColorOf(plainAction.Background), ControlHost.ColorOf(button.Background));
            Assert.Equal(accentPlain.ActualHeight, button.ActualHeight, 0.5);
            Assert.Same(accentPlain.FocusVisualStyle, button.FocusVisualStyle);

            // At rest; a press starts.
            Assert.Equal(ActionButtonState.Idle, button.State);
            AssertShown(parts, parts.Idle);
            AssertRings(parts, running: false, stopping: false);
            Click(button);
            Assert.Equal(1, started);

            // Running: the ring and "Cancel" - the ring in the colour of the text next to it, whatever the accent
            // button makes of it; a press cancels.
            button.IsRunning = true;
            window.UpdateLayout();
            Assert.Equal(ActionButtonState.Running, button.State);
            AssertShown(parts, parts.Running);
            AssertRings(parts, running: true, stopping: false);
            Assert.Equal("Cancel", parts.CancelText.Text);
            Assert.Equal(ControlHost.ColorOf(parts.CancelText.Foreground), ControlHost.ColorOf(parts.RunningRing.Foreground));
            Assert.True(button.IsEnabled);
            Click(button);
            Assert.Equal(1, cancelled);
            Assert.Equal(1, started);

            // Stopping: nothing to press, still enabled; the style's trigger takes the pointer away.
            window.UpdateLayout();
            Assert.Equal(ActionButtonState.Cancelling, button.State);
            AssertShown(parts, parts.Stopping);
            AssertRings(parts, running: false, stopping: true);
            Assert.True(button.IsEnabled);
            Assert.False(button.IsHitTestVisible);
            Click(button);
            Assert.Equal(1, cancelled);

            button.IsRunning = false;
            window.UpdateLayout();
            Assert.Equal(ActionButtonState.Idle, button.State);
            AssertShown(parts, parts.Idle);
            Assert.True(button.IsHitTestVisible);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task TheDialogVariants_HaveTheMetricsOfTheDialogButtons() => WpfTestHost.RunAsync(async () =>
    {
        using var language = LanguageScope.Use("en-US");
        var dialog = (Style)Application.Current.FindResource("Ad.DialogButton");
        var accentDialog = (Style)Application.Current.FindResource("Ad.AccentDialogButton");
        var action = new ActionButton { Content = "OK", Style = (Style)Application.Current.FindResource("Ad.DialogActionButton") };
        var accentAction = new ActionButton { Content = "OK", Style = (Style)Application.Current.FindResource("Ad.AccentDialogActionButton") };
        var plain = new Button { Content = "OK", Style = dialog };
        var accent = new Button { Content = "OK", Style = accentDialog };
        var window = ControlHost.Show(action, accentAction, plain, accent);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);

            // In a footer an action button stands in the row of the dialog buttons: the same minimum width and the same
            // distance to its neighbour.
            Assert.Same(Application.Current.FindResource(ActionButton.StyleKey), action.Style.BasedOn);
            Assert.Same(Application.Current.FindResource("Ad.AccentActionButton"), accentAction.Style.BasedOn);
            foreach (var button in new[] { action, accentAction })
            {
                Assert.Equal(plain.MinWidth, button.MinWidth);
                Assert.Equal(plain.Margin, button.Margin);
                Assert.Equal(plain.ActualHeight, button.ActualHeight, 0.5);
                Assert.True(button.ActualWidth >= plain.ActualWidth, "an action button of a dialog is narrower than a dialog button");
                Assert.Same(Application.Current.FindResource("Ad.ActionButtonContent"), button.ContentTemplate);
                Assert.Equal(ActionButtonState.Idle, button.State);
            }

            Assert.Equal(accent.MinWidth, accentAction.MinWidth);
            Assert.Equal(accent.Margin, accentAction.Margin);
            Assert.Equal(ControlHost.ColorOf(accent.Background), ControlHost.ColorOf(accentAction.Background));
            Assert.Equal(ControlHost.ColorOf(plain.Background), ControlHost.ColorOf(action.Background));
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>Exactly one content is shown (opacity 1), the other two are in the layout but transparent; no clock is left.</summary>
    private static void AssertShown(Parts parts, FrameworkElement shown)
    {
        foreach (var content in parts.Contents)
        {
            Assert.Equal(ReferenceEquals(content, shown) ? 1d : 0d, content.Opacity);
            Assert.False(content.HasAnimatedProperties, "an animation clock is left on a content of the button");
            Assert.False(Motion.IsRunning(content));
        }
    }

    /// <summary>A ring is active and turns only while its content is the one shown.</summary>
    private static void AssertRings(Parts parts, bool running, bool stopping)
    {
        Assert.Equal(running, parts.RunningRing.IsActive);
        Assert.Equal(running, parts.RunningRing.IsTurning);
        Assert.Equal(stopping, parts.StoppingRing.IsActive);
        Assert.Equal(stopping, parts.StoppingRing.IsTurning);
    }

    /// <summary>
    /// A click that reaches the control synchronously, as from the keyboard (Space on the focused button): OnClick is
    /// protected, and a UI Automation invoke is dispatched and refused for a button that is reported as not enabled.
    /// </summary>
    private static void Click(ButtonBase button) =>
        typeof(ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, null);

    /// <summary>The named parts of the content template of Themes/Controls.xaml (the button was laid out in a shown window).</summary>
    private sealed class Parts
    {
        public Parts(ActionButton button)
        {
            Idle = Part<FrameworkElement>(button, "IdleContent");
            Running = Part<FrameworkElement>(button, "RunningContent");
            Stopping = Part<FrameworkElement>(button, "StoppingContent");
            RunningRing = Part<ProgressRing>(button, "RunningRing");
            StoppingRing = Part<ProgressRing>(button, "StoppingRing");
            CancelText = Part<TextBlock>(button, "CancelText");
            StoppingText = Part<TextBlock>(button, "StoppingText");
            Contents = [Idle, Running, Stopping];
        }

        public FrameworkElement Idle { get; }

        public FrameworkElement Running { get; }

        public FrameworkElement Stopping { get; }

        public ProgressRing RunningRing { get; }

        public ProgressRing StoppingRing { get; }

        public TextBlock CancelText { get; }

        public TextBlock StoppingText { get; }

        public IReadOnlyList<FrameworkElement> Contents { get; }

        private static T Part<T>(ActionButton button, string name)
            where T : class
        {
            var part = button.FindContentPart(name);
            Assert.True(part is not null, $"the content template of the button has no part {name} (is it applied?)");
            return Assert.IsAssignableFrom<T>(part);
        }
    }
}
