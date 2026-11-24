using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
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
/// The time of the status line (track V, stage V2b-5b; docs/dev/v-interface-renewal.md "V2b-5b as built"): the delay
/// before "Working" is shown (owner's decision 5) and the polite, throttled announcement of a summary that only changes
/// its text (decision 2), through the real control in a shown window. The logic itself is asserted at the millisecond in
/// <see cref="StatusFlowTests"/>; here the control is driven by a clock the test moves (<see cref="ManualStatusScheduler"/>),
/// except for one test that runs the real clock over a held operation. The status line keeps its band of 28 px whatever
/// it shows (decision 3), which these tests assert at every step.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class StatusLineTimingUiTests
{
    private const string Summary = "2 314 obiektów · zaznaczono 12";
    private const string Reading = "Wczytywanie „Kraków”: na razie 1 500 obiektów…";
    private const string Disabled = "Wyłączono 12 kont w contoso.test.";
    private const string Failed = "Nie udało się wczytać „Księgowość”: kontroler domeny jest niedostępny. (LDAP 81)";

    private static TimeSpan Delay => Motion.TimeOf(Motion.WorkingDelayKey)!.Value;

    private static TimeSpan Quiet => Motion.TimeOf(Motion.SummaryThrottleKey)!.Value;

    private static readonly TimeSpan Millisecond = TimeSpan.FromMilliseconds(1);

    // ---- The delay before "Working" -------------------------------------------------------------------------------

    [Fact]
    public Task TheDelayTokens_AreTimesOfThemesMotionXaml_AndAreThereWithMotionOff() => WpfTestHost.RunAsync(() =>
    {
        // The delay is not motion: it is a token of Themes/Motion.xaml, read by key, and it is there with motion off.
        Assert.False(Motion.IsEnabled);
        Assert.Equal(TimeSpan.FromMilliseconds(150), Delay);
        Assert.Equal(TimeSpan.FromMilliseconds(800), Quiet);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Working_IsShownOnlyWhenTheDelayHasRunOut_AndTheEmptyBandKeepsItsHeightAndWhatLiesUnderIt() => WpfTestHost.RunAsync(async () =>
    {
        using var language = LanguageScope.Use("en-US");
        var clock = new ManualStatusScheduler();
        var stop = new RelayCommand(() => { });
        var line = new StatusLine { Width = 480, Scheduler = clock };
        var below = new Border { Width = 40, Height = 20 };
        var window = ControlHost.Show(line, below);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var parts = new Parts(line);
            var band = (double)Application.Current.FindResource("Ad.Size.StatusLine");

            // A page with nothing to say at rest: the empty band, and the content under it at its place.
            AssertEmptyBand(line, parts, window, below, band);

            // The page begins an operation. The model says "Working" at once; the line goes on showing the empty band.
            var announced = line.Announcements;
            var working = PageStatus.Working(Reading, new PageStatusLog("Test.Reading"), action: new PageStatusAction("Stop", stop));
            line.Status = working;
            Assert.Same(working, line.Status);
            Assert.True(line.IsWaitingForWorking);
            AssertEmptyBand(line, parts, window, below, band);

            clock.Advance(Delay - Millisecond);
            Assert.True(line.IsWaitingForWorking);
            AssertEmptyBand(line, parts, window, below, band);
            Assert.Equal(announced, line.Announcements);

            // The delay is over: the working look - the ring, the title, the text, Stop - and one announcement, now.
            clock.Advance(Millisecond);
            Assert.False(line.IsWaitingForWorking);
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.True(line.IsWorking);
            Assert.Equal("Working", line.Title);
            Assert.Equal(Reading, line.Message);
            Assert.True(parts.Ring.IsActive);
            Assert.True(parts.Ring.IsTurning);
            Assert.Equal(Visibility.Visible, parts.Action.Visibility);
            Assert.Equal("Stop", Assert.IsType<string>(parts.Action.Content));
            Assert.Equal("Working: " + Reading, AutomationProperties.GetName(line));
            Assert.Equal(announced + 1, line.Announcements);
            window.UpdateLayout();
            Assert.Equal(band, line.ActualHeight, 0.5);
            Assert.Equal(band, TopOf(below, window), 0.5);
            Assert.Equal(0, clock.Pending);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task TheLineKeepsWhatItShowed_ASummaryOrAResult_UntilTheDelayHasRunOut() => WpfTestHost.RunAsync(async () =>
    {
        using var language = LanguageScope.Use("pl-PL");
        var clock = new ManualStatusScheduler();
        var line = new StatusLine { Width = 640, CloseCommand = new RelayCommand(() => { }), Scheduler = clock };
        var window = ControlHost.Show(line);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var parts = new Parts(line);

            // The summary stays for the delay: its text, its picture, its name for a screen reader.
            line.Status = PageStatus.Idle(Summary);
            line.Status = PageStatus.Working(Reading, new PageStatusLog("Test.Reading"));
            Assert.Equal(PageStatusKind.Idle, line.Kind);
            Assert.Equal(Summary, line.Message);
            Assert.Equal(AppIcons.StatusInfo, line.IconKey);
            Assert.False(parts.Ring.IsActive);
            Assert.Equal(Summary, AutomationProperties.GetName(line));
            clock.Advance(Delay);
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal(Reading, line.Message);

            // A result stays for the delay as well - with its close button - and then gives way to the next operation.
            line.Status = PageStatus.Done(Disabled, new PageStatusLog("Test.Disabled", 12));
            Assert.Equal(PageStatusKind.Done, line.Kind);
            Assert.Equal(Visibility.Visible, parts.Close.Visibility);
            line.Status = PageStatus.Working(Reading, new PageStatusLog("Test.Reading"));
            Assert.Equal(PageStatusKind.Done, line.Kind);
            Assert.Equal(Disabled, line.Message);
            Assert.Equal(Visibility.Visible, parts.Close.Visibility);
            clock.Advance(Delay - Millisecond);
            Assert.Equal(PageStatusKind.Done, line.Kind);
            clock.Advance(Millisecond);
            Assert.Equal(PageStatusKind.Working, line.Kind);
            Assert.Equal(Visibility.Collapsed, parts.Close.Visibility);
            Assert.False(line.CanClose);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AnOperationThatEndsWithinTheDelay_NeverShowsTheWorkingLook_AndTheLineGoesStraightToItsResult() => WpfTestHost.RunAsync(async () =>
    {
        using var language = LanguageScope.Use("pl-PL");
        var clock = new ManualStatusScheduler();
        var line = new StatusLine { Width = 480, CloseCommand = new RelayCommand(() => { }), Scheduler = clock };
        var below = new Border { Width = 40, Height = 20 };
        var window = ControlHost.Show(line, below);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var parts = new Parts(line);
            var band = (double)Application.Current.FindResource("Ad.Size.StatusLine");
            line.Status = PageStatus.Idle(Summary);

            // Everything the line shows from here on, in order.
            var kinds = new List<PageStatusKind>();
            var working = new List<bool>();
            var rings = new List<bool>();
            var kindDescriptor = DependencyPropertyDescriptor.FromProperty(StatusLine.KindProperty, typeof(StatusLine));
            var workingDescriptor = DependencyPropertyDescriptor.FromProperty(StatusLine.IsWorkingProperty, typeof(StatusLine));
            var ringDescriptor = DependencyPropertyDescriptor.FromProperty(ProgressRing.IsActiveProperty, typeof(ProgressRing));
            EventHandler onKind = (_, _) => kinds.Add(line.Kind);
            EventHandler onWorking = (_, _) => working.Add(line.IsWorking);
            EventHandler onRing = (_, _) => rings.Add(parts.Ring.IsActive);
            kindDescriptor.AddValueChanged(line, onKind);
            workingDescriptor.AddValueChanged(line, onWorking);
            ringDescriptor.AddValueChanged(parts.Ring, onRing);
            try
            {
                var announced = line.Announcements;

                // The operation ends 100 ms after it began, as a result: the line goes straight from the summary to it.
                line.Status = PageStatus.Working(Reading, new PageStatusLog("Test.Reading"));
                clock.Advance(100);
                line.Status = PageStatus.Done(Disabled, new PageStatusLog("Test.Disabled", 12));
                Assert.Equal([PageStatusKind.Done], kinds);
                Assert.Equal(PageStatusKind.Done, line.Kind);
                Assert.Equal(Disabled, line.Message);
                Assert.False(line.IsWaitingForWorking);
                Assert.Equal(0, clock.Pending);
                Assert.Equal(announced + 1, line.Announcements);

                // Nothing comes of the delay afterwards.
                clock.Advance(5000);
                Assert.Equal([PageStatusKind.Done], kinds);
                Assert.Equal(PageStatusKind.Done, line.Kind);

                // The next one ends within the delay as its summary (a read whose result is the content itself): the
                // summary is replaced, the working look is not shown, and the content under the line never moved.
                line.Status = PageStatus.Idle(Summary);
                kinds.Clear();
                line.Status = PageStatus.Working(Reading, new PageStatusLog("Test.Reading"));
                clock.Advance(50);
                line.Status = PageStatus.Idle(Summary + " · 3 nowe");
                Assert.Empty(kinds);
                Assert.Equal(PageStatusKind.Idle, line.Kind);
                Assert.Equal(Summary + " · 3 nowe", line.Message);
                clock.Advance(5000);
                Assert.Empty(kinds);

                // The working look never showed in all of it: not the kind, not the flag, not the ring.
                Assert.Empty(working);
                Assert.Empty(rings);
                window.UpdateLayout();
                Assert.Equal(band, line.ActualHeight, 0.5);
                Assert.Equal(band, TopOf(below, window), 0.5);
            }
            finally
            {
                kindDescriptor.RemoveValueChanged(line, onKind);
                workingDescriptor.RemoveValueChanged(line, onWorking);
                ringDescriptor.RemoveValueChanged(parts.Ring, onRing);
            }
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AnOperationThatIsShown_IsNotDelayedAgain_ForItsNextStage() => WpfTestHost.RunAsync(async () =>
    {
        using var language = LanguageScope.Use("en-US");
        var clock = new ManualStatusScheduler();
        var holder = new PageStatusHolder(language.Localizer, new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        var line = new StatusLine { Width = 640, CloseCommand = holder.CloseCommand, Scheduler = clock };
        line.SetBinding(StatusLine.StatusProperty, new Binding(nameof(PageStatusHolder.Current)) { Source = holder, Mode = BindingMode.OneWay });
        var window = ControlHost.Show(line);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            holder.SetIdle(Summary);
            using var scope = holder.Begin(Reading, new PageStatusLog("Test.Reading"));
            Assert.True(line.IsWaitingForWorking);
            clock.Advance(Delay);
            Assert.True(line.IsWorking);

            // A new count, then another stage of the same operation: shown at once, no clock is asked.
            var requested = clock.Requested.Count;
            scope.Report("Saving the accounts: 12 of 300…", PageProgress.Of(12, 300), new PageStatusLog("Test.Reading", 12, 300));
            Assert.Equal("Saving the accounts: 12 of 300…", line.Message);
            scope.Report("Checking the groups…", PageProgress.Indeterminate, new PageStatusLog("Test.Groups"));
            Assert.Equal("Checking the groups…", line.Message);
            Assert.True(line.IsWorking);
            Assert.False(line.IsWaitingForWorking);
            Assert.Equal(requested, clock.Requested.Count);

            // "Stopping…" is another message of the same running operation: shown at once as well.
            holder.CancelCommand.Execute(null);
            Assert.True(holder.IsCancelling);
            Assert.Equal("Stopping…", line.Message);
            Assert.False(line.IsWaitingForWorking);
            Assert.Equal(requested, clock.Requested.Count);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task TheEntranceOfTheWorkingLook_IsPlayedWhenItIsShown_NotWhenThePageSaysIt() => WpfTestHost.RunAsync(async () =>
    {
        using var language = LanguageScope.Use("en-US");
        var clock = new ManualStatusScheduler();
        var line = new StatusLine { Width = 640, Scheduler = clock };
        var window = ControlHost.Show(line);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var parts = new Parts(line);
            line.Status = PageStatus.Idle(Summary);
            using (MotionScope.On())
            {
                line.Status = PageStatus.Working(Reading, new PageStatusLog("Test.Reading"));
                Assert.False(Motion.IsRunning(parts.Content), "the working look enters before it is shown");
                clock.Advance(Delay);
                Assert.True(Motion.IsRunning(parts.Content), "the working look does not enter when it is shown");
            }
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task OverAHeldOperation_TheRealClockShowsWorkingAfterTheDelay_AndNotBefore() => WpfTestHost.RunAsync(async () =>
    {
        // The real clock (a dispatcher timer) over an operation that stays open until the test ends it - what a held
        // call of a window does: the page is "working" the whole time, the line shows it a moment later.
        using var language = LanguageScope.Use("en-US");
        var holder = new PageStatusHolder(language.Localizer, new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        var line = new StatusLine { Width = 640, CloseCommand = holder.CloseCommand };
        line.SetBinding(StatusLine.StatusProperty, new Binding(nameof(PageStatusHolder.Current)) { Source = holder, Mode = BindingMode.OneWay });
        var window = ControlHost.Show(line);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            holder.SetIdle(Summary);
            var kinds = new List<PageStatusKind>();
            var kindDescriptor = DependencyPropertyDescriptor.FromProperty(StatusLine.KindProperty, typeof(StatusLine));
            EventHandler onKind = (_, _) => kinds.Add(line.Kind);
            kindDescriptor.AddValueChanged(line, onKind);
            try
            {
                // An operation that ends in the same turn of the dispatcher: the delay has not run out and cannot have.
                var quick = holder.Begin(Reading, new PageStatusLog("Test.Reading"));
                Assert.True(holder.IsWorking);
                Assert.False(line.IsWorking);
                Assert.Equal(Summary, line.Message);
                quick.CompleteDone(Disabled, new PageStatusLog("Test.Disabled", 12));
                Assert.Equal([PageStatusKind.Done], kinds);
                await UiTestSupport.WaitForIdleAsync(rounds: 8);
                Assert.Equal([PageStatusKind.Done], kinds);
                Assert.False(line.IsWorking);
                holder.CloseCommand.Execute(null);
                Assert.Equal(PageStatusKind.Idle, line.Kind);
                kinds.Clear();

                // An operation that is held: working in the model at once, in the line a moment later - and it stays.
                using var held = holder.Begin(Reading, new PageStatusLog("Test.Reading"));
                Assert.True(holder.IsWorking);
                Assert.False(line.IsWorking);
                Assert.Equal(PageStatusKind.Idle, line.Kind);
                Assert.Equal(Summary, line.Message);
                Assert.True(line.IsWaitingForWorking);
                await UiWait.UntilAsync(() => line.IsWorking, "the working look after the delay");
                Assert.Equal(PageStatusKind.Working, line.Kind);
                Assert.Equal(Reading, line.Message);
                Assert.Equal([PageStatusKind.Working], kinds);
                Assert.False(line.IsWaitingForWorking);
                await UiTestSupport.WaitForIdleAsync(rounds: 2);
                Assert.True(line.IsWorking);
            }
            finally
            {
                kindDescriptor.RemoveValueChanged(line, onKind);
            }
        }
        finally
        {
            window.Close();
        }
    });

    // ---- The announcement of a summary ----------------------------------------------------------------------------

    [Fact]
    public Task ASummaryThatChangesItsText_IsAnnouncedPolitelyOnce_AfterTheQuietTime_ForTheLastText() => WpfTestHost.RunAsync(async () =>
    {
        using var language = LanguageScope.Use("en-US");
        var clock = new ManualStatusScheduler();
        var line = new StatusLine { Width = 640, Scheduler = clock, MayAnnounceSummary = () => true };
        var window = ControlHost.Show(line);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var peer = Assert.IsAssignableFrom<AutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(line));
            var told = new List<string>();
            line.Announced += (_, _) => told.Add(AutomationProperties.GetName(line));

            // The first summary of the page is what the page says when it opens: not announced.
            line.Status = PageStatus.Idle("GPOs searched: 7 · 12 settings found");
            Assert.Empty(told);
            Assert.Equal(0, clock.Pending);

            // The operator types: the count changes with each letter, 300 ms apart. The text on the screen follows at
            // once; the name a screen reader reads and the announcement wait for the quiet time.
            line.Status = PageStatus.Idle("GPOs searched: 7 · 4 settings found");
            Assert.Equal("GPOs searched: 7 · 4 settings found", line.Message);
            Assert.Equal("GPOs searched: 7 · 12 settings found", AutomationProperties.GetName(line));
            clock.Advance(300);
            line.Status = PageStatus.Idle("GPOs searched: 7 · 3 settings found");
            clock.Advance(300);
            line.Status = PageStatus.Idle("GPOs searched: 7 · 2 settings found");
            Assert.True(line.IsAnnouncingSummary);
            Assert.Empty(told);

            clock.Advance(Quiet - Millisecond);
            Assert.Empty(told);
            Assert.Equal("GPOs searched: 7 · 12 settings found", AutomationProperties.GetName(line));

            // Once, after the last change, for the last text - politely (a summary is a polite live region).
            clock.Advance(Millisecond);
            Assert.Equal(["GPOs searched: 7 · 2 settings found"], told);
            Assert.Equal("GPOs searched: 7 · 2 settings found", AutomationProperties.GetName(line));
            Assert.Equal("GPOs searched: 7 · 2 settings found", peer.GetName());
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(line));
            Assert.Equal(AutomationLiveSetting.Polite, peer.GetLiveSetting());
            Assert.False(line.IsAnnouncingSummary);
            clock.Advance(10_000);
            Assert.Single(told);

            // It did not move: a summary never enters.
            Assert.Equal(PageStatusKind.Idle, line.Kind);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task ASummaryIsNotAnnounced_ForANewPage_ForTheSameTextAgain_OrWhenThePageIsNotInFrontOrItsWindowIsNotActive() => WpfTestHost.RunAsync(async () =>
    {
        using var language = LanguageScope.Use("en-US");
        var clock = new ManualStatusScheduler();
        var front = false;
        var line = new StatusLine { Width = 640, Scheduler = clock, MayAnnounceSummary = () => front };
        var window = ControlHost.Show(line);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var told = 0;
            line.Announced += (_, _) => told++;
            line.Status = PageStatus.Idle("12 settings found");

            // A new page: its first summary is not news, and nothing waits for it.
            Assert.Equal(0, told);
            Assert.Equal(0, clock.Pending);

            // The page is not in front or the window is not active: the text is consumed, nothing is told.
            line.Status = PageStatus.Idle("4 settings found");
            clock.Advance(Quiet);
            Assert.Equal(0, told);
            Assert.Equal("4 settings found", AutomationProperties.GetName(line));

            // In front again: the same text is not news; the next change is - once.
            front = true;
            line.Status = PageStatus.Idle("4 settings found");
            Assert.Equal(0, clock.Pending);
            line.Status = PageStatus.Idle("3 settings found");
            clock.Advance(Quiet);
            Assert.Equal(1, told);

            // A summary that goes away is not told.
            line.Status = PageStatus.Empty;
            clock.Advance(Quiet);
            Assert.Equal(1, told);
            Assert.Equal("Status", AutomationProperties.GetName(line));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task TheDefaultGate_IsAVisibleLineInAnActiveWindow() => WpfTestHost.RunAsync(async () =>
    {
        using var language = LanguageScope.Use("en-US");
        var line = new StatusLine { Width = 640 };
        Assert.False(line.MayAnnounceSummary(), "a line that is in no window is announced");
        var window = ControlHost.Show(line);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);

            // The windows of the tests are shown without being activated; whatever the system makes of that, the line
            // may announce exactly when its window is the active one, and never when the line is not visible.
            Assert.Equal(window.IsActive, line.MayAnnounceSummary());
            line.Visibility = Visibility.Collapsed;
            Assert.False(line.MayAnnounceSummary());
            line.Visibility = Visibility.Visible;
            Assert.Equal(window.IsActive, line.MayAnnounceSummary());
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AResult_KeepsItsBehaviourOfToday_AnnouncedAtOnce_AssertiveForAttentionAndError() => WpfTestHost.RunAsync(async () =>
    {
        using var language = LanguageScope.Use("en-US");
        var clock = new ManualStatusScheduler();
        var line = new StatusLine { Width = 640, Scheduler = clock, MayAnnounceSummary = () => false };
        var window = ControlHost.Show(line);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var told = new List<(string Name, AutomationLiveSetting Live)>();
            line.Announced += (_, _) => told.Add((AutomationProperties.GetName(line), AutomationProperties.GetLiveSetting(line)));
            line.Status = PageStatus.Idle(Summary);

            // Said at once, whatever the gate of the summary says: the page and the window need not be active for it.
            line.Status = PageStatus.Done(Disabled, new PageStatusLog("Test.Disabled", 12));
            line.Status = PageStatus.Attention("Pominięto 3 z 15 kont: brak prawa zapisu.", new PageStatusLog("Test.Skipped", 3, 15));
            line.Status = PageStatus.Error(Failed, new PageStatusLog("Test.Failed"));
            Assert.Equal(
                [
                    ("Done: " + Disabled, AutomationLiveSetting.Polite),
                    ("Attention: Pominięto 3 z 15 kont: brak prawa zapisu.", AutomationLiveSetting.Assertive),
                    ("Error: " + Failed, AutomationLiveSetting.Assertive),
                ],
                told);

            // A new count of the same message is not announced, as before.
            line.Status = PageStatus.Error(Failed + " (2)", new PageStatusLog("Test.Failed", 2));
            Assert.Equal(3, told.Count);
            Assert.Equal(0, clock.Pending);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AnOperationThatEndsWithinTheDelay_AsTheResultTheLineShows_EntersAndIsAnnouncedAgain() => WpfTestHost.RunAsync(async () =>
    {
        using var language = LanguageScope.Use("en-US");
        var clock = new ManualStatusScheduler();
        var line = new StatusLine { Width = 640, CloseCommand = new RelayCommand(() => { }), Scheduler = clock, MayAnnounceSummary = () => false };
        var window = ControlHost.Show(line);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var parts = new Parts(line);
            var told = new List<(string Name, AutomationLiveSetting Live)>();
            line.Announced += (_, _) => told.Add((AutomationProperties.GetName(line), AutomationProperties.GetLiveSetting(line)));
            line.Status = PageStatus.Error(Failed, new PageStatusLog("Test.Failed"));
            Assert.Single(told);

            using (MotionScope.On())
            {
                // Retry: the page says "Working", and 20 ms later the same failure - a new status of the same message.
                line.Status = PageStatus.Working(Reading, new PageStatusLog("Test.Reading"));
                clock.Advance(20);
                Assert.False(Motion.IsRunning(parts.Content));
                line.Status = PageStatus.Error(Failed, new PageStatusLog("Test.Failed"));

                // The working look never showed, but the result enters and is told again, assertively.
                Assert.True(Motion.IsRunning(parts.Content), "a result that ended an operation at once does not enter");
                Assert.Equal(2, told.Count);
                Assert.Equal(("Error: " + Failed, AutomationLiveSetting.Assertive), told[^1]);
                Assert.Equal(PageStatusKind.Error, line.Kind);
                Assert.False(line.IsWaitingForWorking);
            }

            // "Saved" twice in a row, the second within the delay: told again, politely.
            line.Status = PageStatus.Done(Disabled, new PageStatusLog("Test.Disabled", 12));
            var before = told.Count;
            line.Status = PageStatus.Working(Reading, new PageStatusLog("Test.Reading"));
            clock.Advance(40);
            line.Status = PageStatus.Done(Disabled, new PageStatusLog("Test.Disabled", 12));
            Assert.Equal(before + 1, told.Count);
            Assert.Equal(("Done: " + Disabled, AutomationLiveSetting.Polite), told[^1]);

            // The same result again with nothing run in between stays quiet, as before.
            line.Status = PageStatus.Done(Disabled, new PageStatusLog("Test.Disabled", 12));
            Assert.Equal(before + 1, told.Count);
            Assert.Equal(0, clock.Pending);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AResultThatArrivesWhileASummaryWaits_DropsTheSummary() => WpfTestHost.RunAsync(async () =>
    {
        using var language = LanguageScope.Use("en-US");
        var clock = new ManualStatusScheduler();
        var line = new StatusLine { Width = 640, Scheduler = clock, MayAnnounceSummary = () => true };
        var window = ControlHost.Show(line);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var told = new List<string>();
            line.Announced += (_, _) => told.Add(AutomationProperties.GetName(line));
            line.Status = PageStatus.Idle("12 settings found");
            line.Status = PageStatus.Idle("4 settings found");
            Assert.True(line.IsAnnouncingSummary);

            line.Status = PageStatus.Error(Failed, new PageStatusLog("Test.Failed"));
            Assert.False(line.IsAnnouncingSummary);
            clock.Advance(Quiet);

            // Only the result was told - once.
            Assert.Equal(["Error: " + Failed], told);
        }
        finally
        {
            window.Close();
        }
    });

    // ---- Helpers --------------------------------------------------------------------------------------------------

    /// <summary>The line shows nothing and is the empty band: its height is the token's, and the content under it is where it was.</summary>
    private static void AssertEmptyBand(StatusLine line, Parts parts, Window window, FrameworkElement below, double band)
    {
        Assert.Equal(PageStatusKind.Idle, line.Kind);
        Assert.False(line.IsWorking);
        Assert.Equal(string.Empty, line.Title);
        Assert.Equal(string.Empty, line.Message);
        Assert.False(line.HasIcon);
        Assert.False(line.HasAction);
        Assert.False(parts.Ring.IsActive);
        Assert.False(parts.Ring.IsTurning);
        Assert.Equal("Status", AutomationProperties.GetName(line));
        window.UpdateLayout();
        Assert.Equal(band, line.ActualHeight, 0.5);
        Assert.Equal(band, TopOf(below, window), 0.5);
    }

    private static double TopOf(FrameworkElement element, Window window) =>
        element.TranslatePoint(new Point(0, 0), (UIElement)window.Content).Y;

    private sealed class Parts(StatusLine line)
    {
        public FrameworkElement Content { get; } = ControlHost.Part<FrameworkElement>(line, StatusLine.ContentPartName);

        public ProgressRing Ring { get; } = ControlHost.Part<ProgressRing>(line, "PART_Ring");

        public Button Action { get; } = ControlHost.Part<Button>(line, "PART_ActionButton");

        public Button Close { get; } = ControlHost.Part<Button>(line, "PART_CloseButton");
    }
}
