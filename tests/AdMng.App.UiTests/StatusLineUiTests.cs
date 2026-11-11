using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using AdMng.App.Behaviors;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.Presentation.Feedback;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.App.UiTests;

/// <summary>
/// The status line of a page (track V, stage V2; docs/dev/v-interface-renewal.md 4.5, 6.1 and 6.7): what it shows for
/// each kind of <see cref="PageStatus"/>, that it is one line of a fixed height unless a result has to wrap (to three
/// lines at most), its two buttons, what a screen reader is told, and the entrance of a new message (only with motion
/// on). Property checks on the control and the named parts of its template - what it looks like is checked on the CI
/// screenshots.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class StatusLineUiTests
{
    private const string Summary = "2 314 obiektów · zaznaczono 12";
    private const string Reading = "Wczytywanie „Kraków”: na razie 1 500 obiektów…";
    private const string Disabled = "Wyłączono 12 kont w contoso.test. Raport jest na liście działań.";
    private const string Skipped = "Pominięto 3 z 15 kont: brak prawa zapisu. Zaloguj się na inne konto.";
    private const string Failed = "Nie udało się wczytać „Księgowość”: kontroler domeny jest niedostępny. Wybierz Ponów. (LDAP 81)";

    [Fact]
    public Task TheLine_ShowsEachKindOfStatus_WithItsPictureItsTitleAndItsButtons() => WpfTestHost.RunAsync(async () =>
    {
        using var language = LanguageScope.Use("en-US");
        var stop = new RelayCommand(() => { });
        var line = new StatusLine { Width = 640, CloseCommand = new RelayCommand(() => { }) };
        var window = ControlHost.Show(line);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var parts = new Parts(line);

            // The control takes the style of Themes/Controls.xaml by itself; it is not a tab stop (its buttons are).
            Assert.Same(Application.Current.FindResource(StatusLine.StyleKey), line.Style);
            Assert.False(line.Focusable);
            Assert.False(line.IsTabStop);

            // Nothing to say: an empty line - no picture, no title, no button, no fill.
            Assert.Null(line.Status);
            AssertEmpty(line, parts);

            // The summary of the page: the info picture, no title, quiet text; the whole text is the tool tip.
            line.Status = PageStatus.Idle(Summary);
            Assert.Equal(PageStatusKind.Idle, line.Kind);
            Assert.Equal(string.Empty, line.Title);
            Assert.Equal(Visibility.Collapsed, parts.Title.Visibility);
            Assert.Equal(Summary, line.Message);
            Assert.Equal(Summary, parts.Message.Text);
            Assert.Equal(Summary, Assert.IsType<string>(parts.Message.ToolTip));
            Assert.Equal(TextTrimming.CharacterEllipsis, parts.Message.TextTrimming);
            Assert.Equal(TextWrapping.NoWrap, parts.Message.TextWrapping);
            Assert.Equal(ControlHost.ThemeColor(line, "TextFillColorSecondaryBrush"), ControlHost.ColorOf(parts.Message.Foreground));
            Assert.True(line.HasIcon);
            Assert.Equal(Visibility.Visible, parts.IconSlot.Visibility);
            Assert.Equal(AppIcons.StatusInfo, line.IconKey);
            Assert.Equal(AppIcons.StatusInfo, parts.Icon.IconKey);
            Assert.Equal(Glyphs.Info, parts.Icon.Glyph);
            Assert.False(parts.Ring.IsActive);
            Assert.False(line.HasAction);
            Assert.False(line.CanClose);
            Assert.Equal(Visibility.Collapsed, parts.Action.Visibility);
            Assert.Equal(Visibility.Collapsed, parts.Close.Visibility);
            Assert.Equal(Colors.Transparent, ControlHost.ColorOf(parts.Surface.Background));

            // A running operation: the turning ring in place of a picture, the title in words, Stop, no close button.
            line.Status = PageStatus.Working(Reading, new PageStatusLog("Test.Reading", 1500), action: new PageStatusAction("Zatrzymaj", stop));
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.True(line.IsWorking);
            Assert.Equal("Working", line.Title);
            Assert.Equal("Working", parts.Title.Text);
            Assert.Equal(Visibility.Visible, parts.Title.Visibility);
            Assert.Equal(Reading, parts.Message.Text);
            Assert.True(line.HasIcon);
            Assert.Equal(Visibility.Visible, parts.IconSlot.Visibility);
            Assert.Null(line.IconKey);
            Assert.Null(parts.Icon.IconKey);
            Assert.Null(parts.Icon.Glyph);
            Assert.True(parts.Ring.IsActive);
            Assert.True(double.IsNaN(line.ProgressFraction));
            Assert.True(double.IsNaN(parts.Ring.Fraction));
            Assert.True(parts.Ring.IsTurning, "the ring of a running operation does not turn");
            Assert.True(line.HasAction);
            Assert.Equal(Visibility.Visible, parts.Action.Visibility);
            Assert.Equal("Zatrzymaj", Assert.IsType<string>(parts.Action.Content));
            Assert.Same(stop, parts.Action.Command);
            Assert.False(line.CanClose);
            Assert.Equal(Visibility.Collapsed, parts.Close.Visibility);
            Assert.Equal(Colors.Transparent, ControlHost.ColorOf(parts.Surface.Background));

            // A line nobody sees keeps no clock running; shown again, the ring turns again.
            line.Visibility = Visibility.Collapsed;
            Assert.False(parts.Ring.IsTurning);
            Assert.False(parts.Ring.RenderTransform.HasAnimatedProperties);
            line.Visibility = Visibility.Visible;
            Assert.True(parts.Ring.IsTurning);

            // The operation knows how far it is: the ring shows the part done and stands still.
            line.Status = PageStatus.Working(Reading, new PageStatusLog("Test.Reading", 1500), PageProgress.Of(0.25));
            Assert.True(line.IsWorking);
            Assert.Equal(0.25, line.ProgressFraction);
            Assert.Equal(0.25, parts.Ring.Fraction);
            Assert.True(parts.Ring.IsActive);
            Assert.False(parts.Ring.IsTurning);
            Assert.False(line.HasAction);
            Assert.Equal(Visibility.Collapsed, parts.Action.Visibility);

            // The three results: each has its picture, its title in words and its surface; they can be closed.
            line.Status = PageStatus.Done(Disabled, new PageStatusLog("Test.Disabled", 12));
            AssertResult(line, parts, PageStatusKind.Done, "Done", AppIcons.StatusOk, Glyphs.Completed, "SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush");
            Assert.Equal(Disabled, parts.Message.Text);
            Assert.True(line.CanClose);
            Assert.Equal(Visibility.Visible, parts.Close.Visibility);
            Assert.Equal("Close", AutomationProperties.GetName(parts.Close));
            Assert.Equal(Glyphs.Close, Assert.IsType<string>(parts.Close.Content));
            Assert.Same(line.CloseCommand, parts.Close.Command);

            line.Status = PageStatus.Attention(Skipped, new PageStatusLog("Test.Skipped", 3, 15));
            AssertResult(line, parts, PageStatusKind.Attention, "Attention", AppIcons.StatusWarning, Glyphs.Warning, "SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush");
            Assert.Equal(Visibility.Visible, parts.Close.Visibility);

            line.Status = PageStatus.Error(Failed, new PageStatusLog("Test.Failed"), new PageStatusAction("Ponów", stop));
            AssertResult(line, parts, PageStatusKind.Error, "Error", AppIcons.StatusError, Glyphs.Error, "SystemFillColorCriticalBackgroundBrush", "SystemFillColorCriticalBrush");
            Assert.Equal(Failed, Assert.IsType<string>(parts.Message.ToolTip));
            Assert.Equal(Visibility.Visible, parts.Action.Visibility);
            Assert.Equal("Ponów", Assert.IsType<string>(parts.Action.Content));
            Assert.Equal(Visibility.Visible, parts.Close.Visibility);

            // A state of the page that lasts cannot be closed; nor can anything without a command to close it with.
            line.Status = PageStatus.Error(Failed, new PageStatusLog("Test.Failed"), canClose: false);
            Assert.False(line.CanClose);
            Assert.Equal(Visibility.Collapsed, parts.Close.Visibility);
            Assert.Equal(Visibility.Collapsed, parts.Action.Visibility);
            line.Status = PageStatus.Done(Disabled, new PageStatusLog("Test.Disabled", 12));
            Assert.True(line.CanClose);
            line.CloseCommand = null;
            Assert.False(line.CanClose);
            Assert.Equal(Visibility.Collapsed, parts.Close.Visibility);

            // Nothing to say again.
            line.Status = null;
            AssertEmpty(line, parts);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task TheSummaryAndARunningOperation_AreOneLineOfAFixedHeight_AndWhatLiesUnderThemNeverMoves() => WpfTestHost.RunAsync(async () =>
    {
        using var language = LanguageScope.Use("pl-PL");
        var command = new RelayCommand(() => { });
        var line = new StatusLine { Width = 480, CloseCommand = command };
        var below = new Border { Width = 40, Height = 20 };
        var window = ControlHost.Show(line, below);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var parts = new Parts(line);
            var height = (double)Application.Current.FindResource("Ad.Size.StatusLine");
            Assert.Equal(28d, height);
            var top = TopOf(below, window);
            Assert.Equal(height, top, 0.5);

            // What changes while the operator works - the summary, a running operation - is one line whatever its
            // length, and so is a result that fits: nothing under the line moves.
            var longText = string.Join(" ", Enumerable.Repeat(Failed, 6));
            PageStatus?[] statuses =
            [
                null,
                PageStatus.Idle(Summary),
                PageStatus.Idle(longText),
                PageStatus.Working(Reading, new PageStatusLog("Test.Reading"), action: new PageStatusAction("Zatrzymaj", command)),
                PageStatus.Working(longText, new PageStatusLog("Test.Reading"), action: new PageStatusAction("Zatrzymaj", command)),
                PageStatus.Done("Wyłączono 12 kont.", new PageStatusLog("Test.Disabled", 12)),
                PageStatus.Error("Brak połączenia.", new PageStatusLog("Test.Failed"), new PageStatusAction("Ponów", command)),
            ];
            foreach (var status in statuses)
            {
                line.Status = status;
                window.UpdateLayout();
                Assert.Equal(height, line.ActualHeight, 0.5);
                Assert.Equal(480d, line.ActualWidth);
                Assert.Equal(top, TopOf(below, window), 0.5);
            }

            // A long text of a running operation stays on one line and is cut; the whole text is in the tool tip.
            line.Status = PageStatus.Working(longText, new PageStatusLog("Test.Reading"), action: new PageStatusAction("Zatrzymaj", command));
            window.UpdateLayout();
            Assert.Equal(TextWrapping.NoWrap, parts.Message.TextWrapping);
            Assert.Equal(TextTrimming.CharacterEllipsis, parts.Message.TextTrimming);
            Assert.Equal(longText, parts.Message.Text);
            Assert.Equal(longText, Assert.IsType<string>(parts.Message.ToolTip));
            Assert.True(parts.Message.ActualHeight <= height, $"the message is {parts.Message.ActualHeight} high");
            Assert.True(parts.Message.ActualWidth < 480, $"the message is {parts.Message.ActualWidth} wide");

            // The line clips what rises into it when a message enters.
            Assert.True(line.ClipToBounds);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AResultThatDoesNotFitTheLine_WrapsToThreeLinesAtMost_AndTheLineGrowsWithIt() => WpfTestHost.RunAsync(async () =>
    {
        using var language = LanguageScope.Use("pl-PL");
        var command = new RelayCommand(() => { });
        var line = new StatusLine { Width = 480, CloseCommand = command };
        var below = new Border { Width = 40, Height = 20 };
        var window = ControlHost.Show(line, below);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var parts = new Parts(line);
            var height = (double)Application.Current.FindResource("Ad.Size.StatusLine");
            var target = (double)Application.Current.FindResource("Ad.Size.PointerTarget");
            const double TextLine = 20;

            // An error with its reason and what to do, longer than the line: it wraps, so it can be read without
            // pointing at it. The line is as much higher as the text has more lines, and what lies under it moves down.
            line.Status = PageStatus.Error(Failed, new PageStatusLog("Test.Failed"), new PageStatusAction("Ponów", command));
            window.UpdateLayout();
            Assert.Equal(TextWrapping.Wrap, parts.Message.TextWrapping);
            var lines = Math.Round(parts.Message.ActualHeight / TextLine);
            Assert.InRange(lines, 2, 3);
            Assert.Equal(lines * TextLine, parts.Message.ActualHeight, 0.5);
            Assert.Equal(height + ((lines - 1) * TextLine), line.ActualHeight, 0.5);
            Assert.Equal(480d, line.ActualWidth);
            Assert.Equal(line.ActualHeight, TopOf(below, window), 0.5);
            Assert.True(parts.Message.ActualWidth < 480, $"the message is {parts.Message.ActualWidth} wide");

            // The picture, the title and both buttons stay in the first line; the action is before the close button and
            // both are pointer targets of the compact size at least.
            Assert.True(parts.Action.IsVisible);
            Assert.True(parts.Close.IsVisible);
            Assert.Equal(height / 2, MiddleOf(parts.IconSlot, line), 0.5);
            Assert.Equal(height / 2, MiddleOf(parts.Title, line), 0.5);
            Assert.Equal(height / 2, MiddleOf(parts.Action, line), 1);
            Assert.Equal(height / 2, MiddleOf(parts.Close, line), 0.5);
            Assert.True(RightOf(parts.Action, line) <= LeftOf(parts.Close, line) + 0.5, "the action is not before the close button");
            Assert.True(RightOf(parts.Close, line) <= 480.5, "the close button is outside the line");
            Assert.True(parts.Action.ActualHeight >= target, $"the action is {parts.Action.ActualHeight} high");
            Assert.Equal(target, parts.Close.ActualHeight);
            Assert.Equal(target, parts.Close.ActualWidth);

            // A text of many lines: three lines at most (the third is cut), the whole text is in the tool tip.
            var longText = string.Join(" ", Enumerable.Repeat(Failed, 6));
            foreach (var status in new[]
            {
                PageStatus.Error(longText, new PageStatusLog("Test.Failed"), new PageStatusAction("Ponów", command)),
                PageStatus.Attention(longText, new PageStatusLog("Test.Skipped", 3)),
                PageStatus.Done(longText, new PageStatusLog("Test.Disabled", 12)),
            })
            {
                line.Status = status;
                window.UpdateLayout();
                Assert.Equal(TextWrapping.Wrap, parts.Message.TextWrapping);
                Assert.Equal(TextTrimming.CharacterEllipsis, parts.Message.TextTrimming);
                Assert.Equal(3 * TextLine, parts.Message.ActualHeight, 0.5);
                Assert.Equal(height + (2 * TextLine), line.ActualHeight, 0.5);
                Assert.Equal(line.ActualHeight, TopOf(below, window), 0.5);
                Assert.Equal(longText, parts.Message.Text);
                Assert.Equal(longText, Assert.IsType<string>(parts.Message.ToolTip));
            }

            // The result is closed (or the next operation starts): one line again, and what lies under it is back.
            line.Status = PageStatus.Idle(Summary);
            window.UpdateLayout();
            Assert.Equal(TextWrapping.NoWrap, parts.Message.TextWrapping);
            Assert.Equal(height, line.ActualHeight, 0.5);
            Assert.Equal(height, TopOf(below, window), 0.5);
            line.Status = PageStatus.Working(longText, new PageStatusLog("Test.Reading"));
            window.UpdateLayout();
            Assert.Equal(height, line.ActualHeight, 0.5);
            Assert.Equal(height, TopOf(below, window), 0.5);

            // The line clips what rises into it when a message enters.
            Assert.True(line.ClipToBounds);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task TheLine_OverAHolder_StopsRunsTheActionAndCloses() => WpfTestHost.RunAsync(async () =>
    {
        using var language = LanguageScope.Use("en-US");
        var holder = new PageStatusHolder(language.Localizer, new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        var line = new StatusLine { Width = 640, CloseCommand = holder.CloseCommand };
        line.SetBinding(StatusLine.StatusProperty, new Binding(nameof(PageStatusHolder.Current)) { Source = holder, Mode = BindingMode.OneWay });
        var window = ControlHost.Show(line);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var parts = new Parts(line);
            holder.SetIdle(Summary);
            Assert.Equal(Summary, line.Message);

            // An operation of the page: working, with Stop as the link of the line.
            using var scope = holder.Begin(Reading, new PageStatusLog("Test.Reading"));
            Assert.True(line.IsWorking);
            Assert.Equal(Reading, line.Message);
            Assert.Equal(Visibility.Visible, parts.Action.Visibility);
            Assert.Equal("Stop", Assert.IsType<string>(parts.Action.Content));
            Assert.Same(holder.CancelCommand, parts.Action.Command);
            Assert.Equal(Visibility.Collapsed, parts.Close.Visibility);

            // Stop pressed: the token is cancelled and the line says "Stopping…" - with nothing left to press.
            ControlHost.Press(parts.Action);
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.True(scope.Token.IsCancellationRequested);
            Assert.True(holder.IsCancelling);
            Assert.True(line.IsWorking);
            Assert.Equal("Stopping…", line.Message);
            Assert.False(line.HasAction);
            Assert.Equal(Visibility.Collapsed, parts.Action.Visibility);
            Assert.Equal(Visibility.Collapsed, parts.Close.Visibility);

            // The operation ends itself: attention, with Refresh as the way on. The link runs its command once and
            // leaves the message where it is.
            var refreshed = 0;
            scope.CompleteAttention(Skipped, new PageStatusLog("Test.Stopped", 150), new PageStatusAction("Odśwież", new RelayCommand(() => refreshed++)));
            Assert.Equal(PageStatusKind.Attention, line.Kind);
            Assert.False(line.IsWorking);
            Assert.Equal(Skipped, line.Message);
            Assert.Equal(Visibility.Visible, parts.Action.Visibility);
            Assert.Equal("Odśwież", Assert.IsType<string>(parts.Action.Content));
            Assert.Equal(Visibility.Visible, parts.Close.Visibility);
            ControlHost.Press(parts.Action);
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.Equal(1, refreshed);
            Assert.Equal(PageStatusKind.Attention, line.Kind);

            // Closed: the page shows its summary again.
            ControlHost.Press(parts.Close);
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.Equal(PageStatusKind.Idle, holder.Current.Kind);
            Assert.Equal(PageStatusKind.Idle, line.Kind);
            Assert.Equal(Summary, line.Message);
            Assert.Equal(Visibility.Collapsed, parts.Action.Visibility);
            Assert.Equal(Visibility.Collapsed, parts.Close.Visibility);

            // A state of the page that lasts has no close button.
            holder.Show(PageStatus.Error(Failed, new PageStatusLog("Test.Ended"), canClose: false));
            Assert.Equal(PageStatusKind.Error, line.Kind);
            Assert.False(line.CanClose);
            Assert.Equal(Visibility.Collapsed, parts.Close.Visibility);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task TheLine_OfAWindowWithAPageInFront_ShowsWhatThePageSays_HowFarItIs_AndClosesItThere() => WpfTestHost.RunAsync(async () =>
    {
        // Stage V2b: one line for a window whose pages say something of their own. The line stays bound to the holder
        // of the window; the holder of the selected page is "in front".
        using var language = LanguageScope.Use("en-US");
        var dispatcher = new WpfUiDispatcher(Dispatcher.CurrentDispatcher);
        var sheet = new PageStatusHolder(language.Localizer, dispatcher);
        var page = new PageStatusHolder(language.Localizer, dispatcher);
        var line = new StatusLine { Width = 640, CloseCommand = sheet.CloseCommand };
        line.SetBinding(StatusLine.StatusProperty, new Binding(nameof(PageStatusHolder.Current)) { Source = sheet, Mode = BindingMode.OneWay });
        var window = ControlHost.Show(line);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var parts = new Parts(line);
            sheet.SetIdle("Read from dc01.contoso.test · as of 12:03:05");
            page.SetIdle("Groups: 12");
            Assert.Equal("Read from dc01.contoso.test · as of 12:03:05", line.Message);

            // The page comes to the front: its summary.
            sheet.Front = page;
            Assert.Equal(PageStatusKind.Idle, line.Kind);
            Assert.Equal("Groups: 12", line.Message);

            // An operation of the page over many objects. Not known how far: the ring turns, Stop is the page's.
            var log = new PageStatusLog("Test.Saving");
            using var scope = page.Begin("Saving the accounts…", log);
            Assert.True(line.IsWorking);
            Assert.False(sheet.IsWorking);
            Assert.True(parts.Ring.IsActive);
            Assert.True(parts.Ring.IsTurning, "the ring of an operation that cannot say how far it is does not turn");
            Assert.True(double.IsNaN(line.ProgressFraction));
            Assert.Same(page.CancelCommand, parts.Action.Command);

            // "12 of 300": the text says how far, the ring shows the part done and stands still - it is one message, so
            // nothing is announced or enters again.
            scope.Report(
                $"Saving the accounts: {PageStatusText.Progress(language.Localizer, 12, 300)}…", PageProgress.Of(12, 300), new PageStatusLog("Test.Saving", 12, 300));
            Assert.Equal("Saving the accounts: 12 of 300…", line.Message);
            Assert.Equal("Working", line.Title);
            Assert.Equal(0.04, line.ProgressFraction, 6);
            Assert.Equal(0.04, parts.Ring.Fraction, 6);
            Assert.True(parts.Ring.IsActive);
            Assert.False(parts.Ring.IsTurning, "a ring that shows the part done still turns");
            Assert.Equal(Visibility.Visible, parts.IconSlot.Visibility);
            scope.Report($"Saving the accounts: {PageStatusText.Progress(language.Localizer, 300, 300)}…", PageProgress.Of(300, 300));
            Assert.Equal("Saving the accounts: 300 of 300…", line.Message);
            Assert.Equal(1d, parts.Ring.Fraction, 6);
            Assert.False(parts.Ring.IsTurning);

            // The result of the page, in the line of the window; the close button closes it in the holder of the page.
            scope.CompleteError(Failed, new PageStatusLog("Test.Failed"));
            Assert.Equal(PageStatusKind.Error, line.Kind);
            Assert.Equal(Failed, line.Message);
            Assert.False(parts.Ring.IsActive);
            Assert.Equal(Visibility.Visible, parts.Close.Visibility);
            Assert.Same(sheet.CloseCommand, parts.Close.Command);
            ControlHost.Press(parts.Close);
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.True(page.Current.IsIdle);
            Assert.Equal(PageStatusKind.Idle, line.Kind);
            Assert.Equal("Groups: 12", line.Message);
            Assert.Equal(Visibility.Collapsed, parts.Close.Visibility);

            // A state of the page that lasts: said, with nothing to close - and only while that page is in front.
            page.SetLasting(PageStatus.Error(Failed, new PageStatusLog("Test.ReadFailed")));
            Assert.Equal(PageStatusKind.Error, line.Kind);
            Assert.False(line.CanClose);
            Assert.Equal(Visibility.Collapsed, parts.Close.Visibility);
            sheet.Front = null;
            Assert.Equal(PageStatusKind.Idle, line.Kind);
            Assert.Equal("Read from dc01.contoso.test · as of 12:03:05", line.Message);
            sheet.Front = page;
            Assert.Equal(PageStatusKind.Error, line.Kind);
            Assert.Equal(Failed, line.Message);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task TheLine_IsALiveRegion_PoliteForProgressAndResults_AssertiveForWhatNeedsTheOperator() => WpfTestHost.RunAsync(async () =>
    {
        using var language = LanguageScope.Use("en-US");
        var line = new StatusLine { Width = 640 };
        var window = ControlHost.Show(line);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var peer = Assert.IsAssignableFrom<AutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(line));
            Assert.Equal(AutomationControlType.StatusBar, peer.GetAutomationControlType());
            Assert.Equal(nameof(StatusLine), peer.GetClassName());

            // The name is the fixed title and the text - the severity is said in words, never by colour alone.
            var log = new PageStatusLog("Test.Message");
            (PageStatus? Status, string Name, AutomationLiveSetting Live)[] cases =
            [
                (null, "Status", AutomationLiveSetting.Polite),
                (PageStatus.Idle(Summary), Summary, AutomationLiveSetting.Polite),
                (PageStatus.Working(Reading, log), "Working: " + Reading, AutomationLiveSetting.Polite),
                (PageStatus.Done(Disabled, log), "Done: " + Disabled, AutomationLiveSetting.Polite),
                (PageStatus.Attention(Skipped, log), "Attention: " + Skipped, AutomationLiveSetting.Assertive),
                (PageStatus.Error(Failed, log), "Error: " + Failed, AutomationLiveSetting.Assertive),
            ];
            foreach (var (status, name, live) in cases)
            {
                line.Status = status;
                Assert.Equal(name, AutomationProperties.GetName(line));
                Assert.Equal(name, peer.GetName());
                Assert.Equal(live, AutomationProperties.GetLiveSetting(line));
                Assert.Equal(live, peer.GetLiveSetting());
            }

            // The rule by kind.
            Assert.Equal(AutomationLiveSetting.Polite, StatusLine.LiveSettingOf(PageStatusKind.Idle));
            Assert.Equal(AutomationLiveSetting.Polite, StatusLine.LiveSettingOf(PageStatusKind.Working));
            Assert.Equal(AutomationLiveSetting.Polite, StatusLine.LiveSettingOf(PageStatusKind.Done));
            Assert.Equal(AutomationLiveSetting.Assertive, StatusLine.LiveSettingOf(PageStatusKind.Attention));
            Assert.Equal(AutomationLiveSetting.Assertive, StatusLine.LiveSettingOf(PageStatusKind.Error));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task EverySeverity_HasAPictureOfTheSetAndAGlyph_AndWorkingHasTheRing() => WpfTestHost.RunAsync(() =>
    {
        foreach (var kind in Enum.GetValues<PageStatusKind>())
        {
            var (iconKey, glyph) = StatusLine.IconOf(kind);
            if (kind == PageStatusKind.Working)
            {
                // No picture on purpose: "in progress" is shown by the ring (design note 4.4).
                Assert.Null(iconKey);
                Assert.Null(glyph);
                continue;
            }

            // The colour picture of the icon set, and the glyph that stands in for it in the monochrome icon style
            // and in High Contrast.
            Assert.True(AppIcons.Find(Assert.IsType<string>(iconKey)) is not null, $"no picture {iconKey} for {kind}");
            Assert.Equal(1, Assert.IsType<string>(glyph).Length);
        }

        // Four different pictures and glyphs: a kind is told by its shape.
        var shown = Enum.GetValues<PageStatusKind>().Where(kind => kind != PageStatusKind.Working).Select(StatusLine.IconOf).ToList();
        Assert.Equal(4, shown.Select(icon => icon.IconKey).Distinct().Count());
        Assert.Equal(4, shown.Select(icon => icon.Glyph).Distinct().Count());
        return Task.CompletedTask;
    });

    [Fact]
    public Task ANewMessage_Enters_OnlyWhileMotionIsOn_AndACountOrTheSummaryNeverMoves() => WpfTestHost.RunAsync(async () =>
    {
        using var language = LanguageScope.Use("en-US");
        var command = new RelayCommand(() => { });
        var line = new StatusLine { Width = 640, CloseCommand = command };
        var window = ControlHost.Show(line);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var parts = new Parts(line);
            Assert.False(Motion.IsEnabled);

            // Motion off: whatever arrives is simply there.
            line.Status = PageStatus.Error(Failed, new PageStatusLog("Test.Failed"));
            ControlHost.AssertAtRest(parts.Content);
            line.Status = PageStatus.Idle(Summary);
            ControlHost.AssertAtRest(parts.Content);
            line.Status = PageStatus.Working(Reading, new PageStatusLog("Test.Reading", 0));
            ControlHost.AssertAtRest(parts.Content);

            using (MotionScope.On())
            {
                // The same message with a new count (rows arrive): nothing moves.
                line.Status = PageStatus.Working(Reading, new PageStatusLog("Test.Reading", 1500));
                Assert.Equal(Reading, parts.Message.Text);
                ControlHost.AssertAtRest(parts.Content);

                // A new message: picture, title and text fade in and rise; the buttons are there at once.
                line.Status = PageStatus.Attention(Skipped, new PageStatusLog("Test.Skipped", 3), new PageStatusAction("Odśwież", command));
                Assert.True(Motion.IsRunning(parts.Content), "the new message does not enter");
                Assert.True(parts.Content.HasAnimatedProperties, "the new message is not faded in");
                Assert.True(Assert.IsType<TranslateTransform>(parts.Content.RenderTransform).HasAnimatedProperties, "the new message does not rise");
                Assert.Equal(Visibility.Visible, parts.Action.Visibility);
                Assert.Equal(Visibility.Visible, parts.Close.Visibility);
                Assert.False(Motion.IsRunning(parts.Action));
                Assert.False(parts.Action.HasAnimatedProperties);
                Assert.True(parts.Action.IsHitTestVisible);
                Assert.True(parts.Content.IsHitTestVisible);

                // The line clips to its bounds also while a message enters: the rise is not drawn over what lies under it.
                Assert.True(line.ClipToBounds);
            }

            // Motion is off again: the next message takes the running entrance off.
            line.Status = PageStatus.Idle(Summary);
            ControlHost.AssertAtRest(parts.Content);

            using (MotionScope.On())
            {
                // The summary changes (a selection, a refresh): live data never moves.
                line.Status = PageStatus.Idle(Summary + " · 1");
                Assert.Equal(Summary + " · 1", parts.Message.Text);
                ControlHost.AssertAtRest(parts.Content);
            }
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task TheLinesOwnWords_FollowTheLanguage_AlsoWhileItIsShown() => WpfTestHost.RunAsync(() =>
    {
        using var language = LanguageScope.Use("pl-PL");
        var line = new StatusLine { Status = PageStatus.Error(Failed, new PageStatusLog("Test.Failed")) };
        Assert.Equal("Błąd", line.Title);
        Assert.Equal("Błąd: " + Failed, AutomationProperties.GetName(line));

        // A language switch while the application runs: the fixed title and the name follow at once; the message is
        // the page's text and stays until the page says it again.
        language.Switch("en-US");
        Assert.Equal("Error", line.Title);
        Assert.Equal(Failed, line.Message);
        Assert.Equal("Error: " + Failed, AutomationProperties.GetName(line));
        Assert.Equal(PageStatusKind.Error, line.Kind);

        (PageStatus Status, string English, string Polish)[] titles =
        [
            (PageStatus.Working(Reading, new PageStatusLog("Test.Reading")), "Working", "Trwa"),
            (PageStatus.Done(Disabled, new PageStatusLog("Test.Disabled")), "Done", "Gotowe"),
            (PageStatus.Attention(Skipped, new PageStatusLog("Test.Skipped")), "Attention", "Uwaga"),
            (PageStatus.Error(Failed, new PageStatusLog("Test.Failed")), "Error", "Błąd"),
        ];
        foreach (var (status, english, polish) in titles)
        {
            line.Status = status;
            Assert.Equal(english, line.Title);
            language.Switch("pl-PL");
            Assert.Equal(polish, line.Title);
            language.Switch("en-US");
        }

        // An empty line has a name in both languages.
        line.Status = null;
        Assert.Equal("Status", AutomationProperties.GetName(line));
        language.Switch("pl-PL");
        Assert.Equal("Stan", AutomationProperties.GetName(line));
        return Task.CompletedTask;
    });

    private static void AssertEmpty(StatusLine line, Parts parts)
    {
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.Equal(string.Empty, line.Title);
        Assert.Equal(string.Empty, line.Message);
        Assert.Equal(string.Empty, parts.Message.Text);
        Assert.Null(parts.Message.ToolTip);
        Assert.False(line.HasIcon);
        Assert.False(line.IsWorking);
        Assert.False(line.HasAction);
        Assert.False(line.CanClose);
        Assert.Equal(Visibility.Collapsed, parts.IconSlot.Visibility);
        Assert.Equal(Visibility.Collapsed, parts.Title.Visibility);
        Assert.Equal(Visibility.Collapsed, parts.Action.Visibility);
        Assert.Equal(Visibility.Collapsed, parts.Close.Visibility);
        Assert.False(parts.Ring.IsActive);
        Assert.False(parts.Ring.IsTurning);
        Assert.Equal(Colors.Transparent, ControlHost.ColorOf(parts.Surface.Background));
    }

    /// <summary>A result (done, attention, error): its picture and glyph, its fixed title, its surface - and no ring.</summary>
    private static void AssertResult(
        StatusLine line, Parts parts, PageStatusKind kind, string title, string iconKey, string glyph, string backgroundKey, string glyphColourKey)
    {
        Assert.Equal(kind, line.Kind);
        Assert.False(line.IsWorking);
        Assert.Equal(title, line.Title);
        Assert.Equal(title, parts.Title.Text);
        Assert.Equal(Visibility.Visible, parts.Title.Visibility);
        Assert.Equal(FontWeights.SemiBold, parts.Title.FontWeight);
        Assert.True(line.HasIcon);
        Assert.Equal(Visibility.Visible, parts.IconSlot.Visibility);
        Assert.Equal(iconKey, line.IconKey);
        Assert.Equal(glyph, line.IconGlyph);
        Assert.Equal(iconKey, parts.Icon.IconKey);
        Assert.Equal(glyph, parts.Icon.Glyph);
        Assert.False(parts.Ring.IsActive);
        Assert.False(parts.Ring.IsTurning);
        Assert.True(double.IsNaN(line.ProgressFraction));

        // The surface is the theme's status background; the glyph of the monochrome style takes the status colour.
        Assert.Equal(ControlHost.ThemeColor(line, backgroundKey), ControlHost.ColorOf(parts.Surface.Background));
        Assert.Equal(ControlHost.ThemeColor(line, glyphColourKey), ControlHost.ColorOf(parts.Icon.Foreground));
    }

    private static double TopOf(FrameworkElement element, Window window) =>
        element.TranslatePoint(new Point(0, 0), (UIElement)window.Content).Y;

    /// <summary>The vertical middle of an element, in the coordinates of <paramref name="relativeTo"/>.</summary>
    private static double MiddleOf(FrameworkElement element, UIElement relativeTo) =>
        element.TranslatePoint(new Point(0, element.ActualHeight / 2), relativeTo).Y;

    private static double LeftOf(FrameworkElement element, UIElement relativeTo) =>
        element.TranslatePoint(new Point(0, 0), relativeTo).X;

    private static double RightOf(FrameworkElement element, UIElement relativeTo) =>
        element.TranslatePoint(new Point(element.ActualWidth, 0), relativeTo).X;

    /// <summary>The named parts of the template of Themes/Controls.xaml (the line was laid out in a shown window).</summary>
    private sealed class Parts(StatusLine line)
    {
        public Border Surface { get; } = ControlHost.Part<Border>(line, "Surface");

        public FrameworkElement Content { get; } = ControlHost.Part<FrameworkElement>(line, StatusLine.ContentPartName);

        public Grid IconSlot { get; } = ControlHost.Part<Grid>(line, "IconSlot");

        public AreaIconView Icon { get; } = ControlHost.Part<AreaIconView>(line, "PART_Icon");

        public ProgressRing Ring { get; } = ControlHost.Part<ProgressRing>(line, "PART_Ring");

        public TextBlock Title { get; } = ControlHost.Part<TextBlock>(line, "PART_Title");

        public TextBlock Message { get; } = ControlHost.Part<TextBlock>(line, "PART_Message");

        public Button Action { get; } = ControlHost.Part<Button>(line, "PART_ActionButton");

        public Button Close { get; } = ControlHost.Part<Button>(line, "PART_CloseButton");
    }
}
