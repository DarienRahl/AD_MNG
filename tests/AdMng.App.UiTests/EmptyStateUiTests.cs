using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.Presentation.Icons;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.App.UiTests;

/// <summary>
/// The empty state (track V, stage V2b-5b; docs/dev/v-interface-renewal.md 6.7 - "a list with no rows: a 48 px icon, one
/// sentence why, one action"): collapsed while it has nothing to say, centred in its host, the picture, the sentence and at
/// most one action that runs its command. Property checks on the named parts of the template.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class EmptyStateUiTests
{
    private const string NoMatch = "Żaden obiekt nie pasuje do filtra.";

    [Fact]
    public Task AnEmptyState_WithoutASentence_IsCollapsed_AndTakesNoRoom() => WpfTestHost.RunAsync(async () =>
    {
        var state = new EmptyState();
        var before = new TextBlock { Text = "przed" };
        var after = new TextBlock { Text = "po" };
        var window = ControlHost.Show(before, state, after);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);

            // Nothing to say: nothing there - not even the room of its margin.
            Assert.Same(Application.Current.FindResource(EmptyState.StyleKey), state.Style);
            Assert.False(state.HasMessage);
            Assert.Equal(Visibility.Collapsed, state.Visibility);
            Assert.False(state.IsVisible);
            Assert.Equal(0d, state.ActualHeight);
            var gap = after.TranslatePoint(new Point(0, 0), window).Y - before.TranslatePoint(new Point(0, 0), window).Y;
            Assert.Equal(before.ActualHeight, gap, 0.5);

            // A sentence: it is there; no sentence again: it is gone again.
            state.Message = NoMatch;
            window.UpdateLayout();
            Assert.True(state.HasMessage);
            Assert.Equal(Visibility.Visible, state.Visibility);
            Assert.True(state.ActualHeight > 0);
            state.Message = string.Empty;
            window.UpdateLayout();
            Assert.False(state.IsVisible);
            Assert.Equal(0d, state.ActualHeight);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AnEmptyState_ShowsA48PxPicture_AndOneSentence_AndNoActionWithoutOne() => WpfTestHost.RunAsync(async () =>
    {
        var state = new EmptyState { Icon = ObjectIcon.Query, Message = NoMatch };
        var window = ControlHost.Show(state);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var icon = ControlHost.Part<ObjectIconView>(state, "PART_Icon");
            var message = ControlHost.Part<TextBlock>(state, "PART_Message");
            var action = ControlHost.Part<Button>(state, EmptyState.ActionPartName);

            // The picture of the icon at 48 px, centred over the sentence; the sentence wraps and is centred too.
            Assert.Equal(ObjectIcon.Query, icon.Icon);
            Assert.Equal(48d, icon.Size);
            Assert.Equal(new Size(48, 48), icon.RenderSize);
            Assert.Equal(NoMatch, message.Text);
            Assert.Equal(TextWrapping.Wrap, message.TextWrapping);
            Assert.Equal(TextAlignment.Center, message.TextAlignment);
            Assert.Equal(ControlHost.ThemeColor(state, "TextFillColorSecondaryBrush"), ControlHost.ColorOf(message.Foreground));
            Assert.True(Top(message, window) > Top(icon, window) + 48, "the sentence is not under the picture");
            Assert.Equal(Centre(icon, window), Centre(message, window), 1.5);

            // No label and no command: no action.
            Assert.False(state.HasAction);
            Assert.False(action.IsVisible);

            // A label alone is not an action, nor is a command alone.
            state.ActionText = "Wyczyść filtr";
            window.UpdateLayout();
            Assert.False(state.HasAction);
            Assert.False(action.IsVisible);
            state.ActionText = string.Empty;
            state.ActionCommand = new RelayCommand(() => { });
            window.UpdateLayout();
            Assert.False(state.HasAction);
            Assert.False(action.IsVisible);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task TheAction_IsALinkButton_ThatRunsItsCommandWithItsParameter() => WpfTestHost.RunAsync(async () =>
    {
        var seen = new List<object?>();
        var command = new RelayCommand<object?>(seen.Add);
        var state = new EmptyState { Message = NoMatch, ActionText = "Wyczyść filtr", ActionCommand = command, ActionParameter = "parametr" };
        var window = ControlHost.Show(state);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var action = ControlHost.Part<Button>(state, EmptyState.ActionPartName);

            // One action: a real button with the label, the command and the parameter, the link of the status line, a tab stop.
            Assert.True(state.HasAction);
            Assert.True(action.IsVisible);
            Assert.Same(Application.Current.FindResource("Ad.LinkButton"), action.Style);
            Assert.Equal("Wyczyść filtr", action.Content);
            Assert.Same(command, action.Command);
            Assert.Equal("parametr", action.CommandParameter);
            Assert.True(action.IsTabStop);
            Assert.False(state.IsTabStop);
            Assert.Single(UiWait.Descendants<Button>(state));
            Assert.True(action.ActualHeight >= 24, "the action is smaller than a pointer target");

            // Pressed like the operator: the command runs once, with its parameter. (The press is posted below the priority of the
            // continuations of a call - wait for what it does, not for a time.)
            ControlHost.Press(action);
            await UiWait.UntilAsync(() => seen.Count == 1, "the command of the action");
            Assert.Equal(["parametr"], seen);

            // A command that cannot run: the button is disabled, and a press is refused.
            var blocked = new RelayCommand(() => seen.Add("nie"), () => false);
            state.ActionCommand = blocked;
            window.UpdateLayout();
            Assert.False(action.IsEnabled);
            Assert.Throws<System.Windows.Automation.ElementNotEnabledException>(() => ControlHost.Press(action));
            Assert.Single(seen);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AnEmptyState_IsCentredInItsHost() => WpfTestHost.RunAsync(async () =>
    {
        var state = new EmptyState { Message = NoMatch, ActionText = "Odśwież", ActionCommand = new RelayCommand(() => { }) };
        var host = new Grid { Width = 640, Height = 360, Background = Brushes.White };
        host.Children.Add(state);
        var window = new Window { Width = 700, Height = 420, Content = host, WindowStyle = WindowStyle.ToolWindow };
        UiTestSupport.ShowOffScreen(window);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);

            Assert.Equal(HorizontalAlignment.Center, state.HorizontalAlignment);
            Assert.Equal(VerticalAlignment.Center, state.VerticalAlignment);
            Assert.True(state.ActualWidth > 0 && state.ActualHeight > 0);
            Assert.Equal(host.ActualWidth / 2, Centre(state, host), 1.5);
            var middle = state.TranslatePoint(new Point(0, state.ActualHeight / 2), host).Y;
            Assert.Equal(host.ActualHeight / 2, middle, 1.5);

            // A narrow host: the sentence wraps, the control never grows beyond its maximum width.
            host.Width = 200;
            window.UpdateLayout();
            Assert.True(state.ActualWidth <= 200);
            Assert.True(state.MaxWidth <= 420);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task ForAScreenReader_TheEmptyState_IsAGroupNamedBySentence_OnlyWhileItHasOne() => WpfTestHost.RunAsync(async () =>
    {
        var state = new EmptyState();
        var window = ControlHost.Show(state);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var peer = Assert.IsAssignableFrom<AutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(state));
            Assert.Equal(AutomationControlType.Group, peer.GetAutomationControlType());
            Assert.Equal(nameof(EmptyState), peer.GetClassName());

            // Nothing to say: not in the content of the page.
            Assert.False(peer.IsControlElement());
            Assert.False(peer.IsContentElement());

            state.Message = NoMatch;
            Assert.Equal(NoMatch, peer.GetName());
            Assert.True(peer.IsControlElement());
            Assert.True(peer.IsContentElement());

            // The picture is decorative; the action is a button of its own.
            state.ActionText = "Wyczyść filtr";
            state.ActionCommand = new RelayCommand(() => { });
            window.UpdateLayout();
            var action = ControlHost.Part<Button>(state, EmptyState.ActionPartName);
            Assert.Null(UIElementAutomationPeer.CreatePeerForElement(ControlHost.Part<ObjectIconView>(state, "PART_Icon")));
            Assert.Equal("Wyczyść filtr", Assert.IsAssignableFrom<AutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(action)).GetName());
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task TheTextProperties_TakeNullAsNoText() => WpfTestHost.RunAsync(async () =>
    {
        var state = new EmptyState { Message = NoMatch, ActionText = "Odśwież" };
        var window = ControlHost.Show(state);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);

            // A binding to a view model that has no state yet hands over null: that is silence, not "null".
            state.Message = null!;
            state.ActionText = null!;
            Assert.Equal(string.Empty, state.Message);
            Assert.Equal(string.Empty, state.ActionText);
            Assert.False(state.HasMessage);
            Assert.False(state.HasAction);
        }
        finally
        {
            window.Close();
        }
    });

    private static double Top(FrameworkElement element, UIElement relativeTo) => element.TranslatePoint(new Point(0, 0), relativeTo).Y;

    private static double Centre(FrameworkElement element, UIElement relativeTo) =>
        element.TranslatePoint(new Point(element.ActualWidth / 2, 0), relativeTo).X;
}
