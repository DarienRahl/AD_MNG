using AdMng.App.Controls;
using AdMng.App.UiTests.Infrastructure;
using AdMng.Presentation.Feedback;

namespace AdMng.App.UiTests;

/// <summary>
/// The time logic of the status line (track V, stage V2b-5b; design note docs/dev/v-interface-renewal.md "V2b-5b as
/// built"): the delay before "Working" is shown and the polite, throttled announcement of a changed summary. The logic
/// is <see cref="StatusFlow"/> - no WPF type, no UI thread - driven by a clock the test moves by hand
/// (<see cref="ManualStatusScheduler"/>), so every assertion is about a millisecond and nothing waits. The control that
/// uses it is covered in <c>StatusLineTimingUiTests</c>.
/// </summary>
public sealed class StatusFlowTests
{
    private static readonly PageStatusLog Reading = new("Test.Reading");
    private static readonly PageStatusLog Saving = new("Test.Saving");
    private static readonly PageStatusLog Finished = new("Test.Finished");

    private static PageStatus Working(string text = "Reading…", PageStatusLog? log = null) => PageStatus.Working(text, log ?? Reading);

    private static PageStatus Done(string text = "Done.") => PageStatus.Done(text, Finished);

    /// <summary>A new result of the kind, always the same message ("Saved 1 object.", "Could not read “Kraków”: …").</summary>
    private static PageStatus Result(PageStatusKind kind) => kind switch
    {
        PageStatusKind.Done => PageStatus.Done("Saved 1 object.", new PageStatusLog("Test.Saved", 1)),
        PageStatusKind.Attention => PageStatus.Attention("Skipped 3 of 15 accounts: no right to write.", new PageStatusLog("Test.Skipped", 3, 15)),
        _ => PageStatus.Error("Could not read “Kraków”: the domain controller is not available. (LDAP 81)", new PageStatusLog("Test.ReadFailed")),
    };

    // ---- The delay before "Working" -------------------------------------------------------------------------------

    [Fact]
    public void Working_IsShownOnlyAfterTheDelay_AndTheLineKeepsWhatItShowedMeanwhile()
    {
        var h = new Harness();
        h.Flow.Set(PageStatus.Idle("2 314 objects"));
        h.Shows.Clear();

        // The page says "Working" at once; the line keeps the summary - nothing is shown, nothing is announced.
        h.Flow.Set(Working());
        Assert.True(h.Flow.IsWaiting);
        Assert.Equal("2 314 objects", h.Flow.Shown.Message);
        Assert.Equal(PageStatusKind.Idle, h.Flow.Shown.Kind);
        Assert.Empty(h.Shows);
        Assert.Equal([TimeSpan.FromMilliseconds(150)], h.Clock.Requested);

        h.Clock.Advance(149);
        Assert.True(h.Flow.IsWaiting);
        Assert.Equal(PageStatusKind.Idle, h.Flow.Shown.Kind);
        Assert.Empty(h.Shows);

        // The delay is over: the working look, as a new message (it is announced and enters like any new message).
        h.Clock.Advance(1);
        Assert.False(h.Flow.IsWaiting);
        var shown = Assert.Single(h.Shows);
        Assert.Equal(PageStatusKind.Working, shown.Status.Kind);
        Assert.True(shown.IsNew);
        Assert.True(shown.NameNow);
        Assert.Equal(PageStatusKind.Working, h.Flow.Shown.Kind);
        Assert.Equal(0, h.Clock.Pending);
    }

    [Fact]
    public void AnOperationThatEndsWithinTheDelay_NeverShowsTheWorkingLook()
    {
        var h = new Harness();
        h.Flow.Set(PageStatus.Idle("2 314 objects"));
        h.Shows.Clear();

        h.Flow.Set(Working());
        h.Clock.Advance(100);
        h.Flow.Set(Done("Disabled 12 accounts."));

        // Straight to the result: it is a new message against the summary the line showed, and the delay is gone.
        var shown = Assert.Single(h.Shows);
        Assert.Equal(PageStatusKind.Done, shown.Status.Kind);
        Assert.True(shown.IsNew);
        Assert.False(h.Flow.IsWaiting);
        Assert.Equal(0, h.Clock.Pending);

        h.Clock.Advance(5000);
        Assert.Single(h.Shows);
        Assert.DoesNotContain(h.Shows, s => s.Status.IsWorking);
    }

    [Theory]
    [InlineData(PageStatusKind.Error, 20)]
    [InlineData(PageStatusKind.Done, 40)]
    [InlineData(PageStatusKind.Attention, 149)]
    public void AnOperationThatEndsWithinTheDelay_AsTheResultTheLineShows_SaysItAgainAsANewMessage(PageStatusKind kind, int after)
    {
        // Retry of a read that fails the same way, a second save of one object: the operator must see and hear that it
        // ran, though the working look never showed and the result is the same message as the one on the line.
        var h = new Harness();
        h.Flow.Set(Result(kind));
        h.Shows.Clear();

        h.Flow.Set(Working());
        h.Clock.Advance(after);
        h.Flow.Set(Result(kind));

        var shown = Assert.Single(h.Shows);
        Assert.Equal(kind, shown.Status.Kind);
        Assert.True(shown.IsNew);
        Assert.True(shown.NameNow);
        Assert.False(h.Flow.IsWaiting);
        Assert.Equal(0, h.Clock.Pending);
        h.Clock.Advance(5000);
        Assert.Single(h.Shows);
        Assert.DoesNotContain(h.Shows, s => s.Status.IsWorking);
    }

    [Fact]
    public void AStateThatLasts_AndIsBackWhenAnOperationEndedWithinTheDelay_IsSaidAgainAsANewMessage()
    {
        // The page's state that lasts is the very same object when the operation ended without a message of its own.
        var h = new Harness();
        var state = PageStatus.Error("Could not read the folder.", Finished, canClose: false);
        h.Flow.Set(state);
        h.Shows.Clear();

        h.Flow.Set(Working());
        h.Clock.Advance(30);
        h.Flow.Set(state);

        var shown = Assert.Single(h.Shows);
        Assert.Same(state, shown.Status);
        Assert.True(shown.IsNew);
    }

    [Fact]
    public void TheSameMessage_WithoutAnOperationInBetween_OrAsTheSummary_StaysQuiet()
    {
        // A page that computes its result anew: the same message, nothing ran - no entrance, no announcement.
        var h = new Harness();
        h.Flow.Set(Result(PageStatusKind.Error));
        h.Shows.Clear();
        h.Flow.Set(Result(PageStatusKind.Error));
        Assert.False(Assert.Single(h.Shows).IsNew);

        // A read that ends within the delay as the summary it had: the summary is not a result, it stays quiet.
        h.Flow.Set(PageStatus.Idle("12 objects"));
        h.Shows.Clear();
        h.Flow.Set(Working());
        h.Clock.Advance(40);
        h.Flow.Set(PageStatus.Idle("12 objects"));
        Assert.False(Assert.Single(h.Shows).IsNew);
        h.Clock.Advance(5000);
        Assert.Equal(0, h.Announcements);
    }

    [Fact]
    public void AnOperationThatEndsAtOnceAsItsSummary_ChangesTheSummaryAndNeverShowsTheWorkingLook()
    {
        var h = new Harness();
        h.Flow.Set(PageStatus.Idle("12 objects"));
        h.Shows.Clear();

        h.Flow.Set(Working());
        h.Flow.Set(PageStatus.Idle("13 objects"));

        // The summary replaced the summary: the same message, no motion (the count of a read that ended at once).
        var shown = Assert.Single(h.Shows);
        Assert.Equal("13 objects", shown.Status.Message);
        Assert.False(shown.IsNew);
        Assert.False(h.Flow.IsWaiting);
        Assert.Equal(0, h.Announcements);
    }

    [Fact]
    public void ASecondRunningStatusDuringTheDelay_OnlyReplacesTheFirst_AndTheDelayRunsOnFromTheFirst()
    {
        var h = new Harness();
        h.Flow.Set(Working("Reading 1…"));
        h.Clock.Advance(100);
        h.Flow.Set(Working("Reading 500…"));
        Assert.Equal([TimeSpan.FromMilliseconds(150)], h.Clock.Requested);
        Assert.Empty(h.Shows);

        h.Clock.Advance(50);
        var shown = Assert.Single(h.Shows);
        Assert.Equal("Reading 500…", shown.Status.Message);
        Assert.True(shown.IsNew);
    }

    [Fact]
    public void AnOperationThatIsShown_IsNotDelayedAgain_ByANewMessageOrANewCount()
    {
        var h = new Harness();
        h.Flow.Set(Working("Reading…"));
        h.Clock.Advance(150);
        h.Shows.Clear();
        var scheduled = h.Clock.Requested.Count;

        // The stages of one operation: a new text of the same message, then another message.
        h.Flow.Set(Working("Reading 500…"));
        h.Flow.Set(Working("Saving…", Saving));
        Assert.False(h.Flow.IsWaiting);
        Assert.Equal(scheduled, h.Clock.Requested.Count);
        Assert.Equal(2, h.Shows.Count);
        Assert.False(h.Shows[0].IsNew);
        Assert.True(h.Shows[1].IsNew);
        Assert.Equal("Saving…", h.Flow.Shown.Message);
    }

    [Fact]
    public void TheNextOperation_AfterAResult_IsDelayedAgain_AndTheResultStaysMeanwhile()
    {
        var h = new Harness();
        h.Flow.Set(Working());
        h.Clock.Advance(150);
        h.Flow.Set(Done());
        h.Shows.Clear();

        h.Flow.Set(Working());
        Assert.True(h.Flow.IsWaiting);
        Assert.Equal(PageStatusKind.Done, h.Flow.Shown.Kind);
        Assert.Empty(h.Shows);

        h.Clock.Advance(150);
        Assert.Equal(PageStatusKind.Working, h.Flow.Shown.Kind);
    }

    [Fact]
    public void WithoutADelayToken_ThePage_IsShownAtOnce()
    {
        foreach (TimeSpan? delay in new TimeSpan?[] { null, TimeSpan.Zero })
        {
            var h = new Harness { WorkingDelay = delay };
            h.Flow.Set(Working());
            Assert.False(h.Flow.IsWaiting);
            Assert.Equal(PageStatusKind.Working, h.Flow.Shown.Kind);
            Assert.Empty(h.Clock.Requested);
        }
    }

    [Fact]
    public void CancelDropsWhatWaits_AndTheLineKeepsWhatItShows()
    {
        var h = new Harness();
        h.Flow.Set(PageStatus.Idle("1 found"));
        h.Flow.Set(PageStatus.Idle("2 found"));
        h.Flow.Set(Working());
        Assert.True(h.Flow.IsWaiting);
        Assert.True(h.Flow.IsAnnouncing);

        h.Flow.Cancel();
        Assert.False(h.Flow.IsWaiting);
        Assert.False(h.Flow.IsAnnouncing);
        Assert.Equal(0, h.Clock.Pending);
        Assert.Equal("2 found", h.Flow.Shown.Message);
    }

    // ---- The announcement of a summary ----------------------------------------------------------------------------

    [Fact]
    public void AChangingSummary_IsAnnouncedOnce_AfterTheQuietTime_ForTheLastText()
    {
        var h = new Harness();
        h.Flow.Set(PageStatus.Idle("12 settings found"));
        h.Shows.Clear();

        // The operator types: a new count after each letter, 300 ms apart - nothing is announced while it goes on.
        h.Flow.Set(PageStatus.Idle("4 settings found"));
        h.Clock.Advance(300);
        h.Flow.Set(PageStatus.Idle("3 settings found"));
        h.Clock.Advance(300);
        h.Flow.Set(PageStatus.Idle("2 settings found"));
        Assert.Equal(0, h.Announcements);
        Assert.True(h.Flow.IsAnnouncing);

        // The text is on the screen at once, the name a screen reader reads waits for the announcement.
        Assert.Equal(3, h.Shows.Count);
        Assert.All(h.Shows, s => Assert.False(s.IsNew));
        Assert.All(h.Shows, s => Assert.False(s.NameNow));
        Assert.Equal(0, h.NamesSettled);

        h.Clock.Advance(799);
        Assert.Equal(0, h.Announcements);

        // 800 ms after the last change: one announcement, the name follows it.
        h.Clock.Advance(1);
        Assert.Equal(1, h.Announcements);
        Assert.Equal(1, h.NamesSettled);
        Assert.Equal("2 settings found", h.Flow.Shown.Message);
        Assert.False(h.Flow.IsAnnouncing);

        // Nothing more comes of it.
        h.Clock.Advance(5000);
        Assert.Equal(1, h.Announcements);
        Assert.Equal(0, h.Clock.Pending);
    }

    [Fact]
    public void EveryChangeStartsTheQuietTimeAgain()
    {
        var h = new Harness();
        h.Flow.Set(PageStatus.Idle("12 settings found"));
        h.Flow.Set(PageStatus.Idle("4 settings found"));
        h.Clock.Advance(700);
        h.Flow.Set(PageStatus.Idle("3 settings found"));
        Assert.Equal(1, h.Clock.Pending);
        h.Clock.Advance(700);
        Assert.Equal(0, h.Announcements);
        h.Clock.Advance(100);
        Assert.Equal(1, h.Announcements);
        Assert.Equal([TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(800)], h.Clock.Requested);
    }

    [Fact]
    public void TheVeryFirstSummaryOfAPage_IsNotAnnounced_AndEmptyOnesNeverAre()
    {
        var h = new Harness();

        // A page opens: nothing, then its summary - that is what the page says, not a change.
        h.Flow.Set(null);
        h.Flow.Set(PageStatus.Idle("2 314 objects"));
        Assert.False(h.Flow.IsAnnouncing);
        Assert.Empty(h.Clock.Requested);
        Assert.True(h.Shows[^1].NameNow);
        h.Clock.Advance(5000);
        Assert.Equal(0, h.Announcements);

        // A summary that goes empty is not told either (and the name follows at once).
        h.Flow.Set(PageStatus.Empty);
        Assert.False(h.Flow.IsAnnouncing);
        Assert.True(h.Shows[^1].NameNow);
        h.Clock.Advance(5000);
        Assert.Equal(0, h.Announcements);

        // The next real change is.
        h.Flow.Set(PageStatus.Idle("2 315 objects"));
        h.Clock.Advance(800);
        Assert.Equal(1, h.Announcements);
    }

    [Fact]
    public void ASummaryThatComesBackToWhatWasHeard_IsNotAnnounced()
    {
        var h = new Harness();
        h.Flow.Set(PageStatus.Idle("12 settings found"));
        h.Flow.Set(PageStatus.Idle("4 settings found"));
        Assert.True(h.Flow.IsAnnouncing);

        h.Flow.Set(PageStatus.Idle("12 settings found"));
        Assert.False(h.Flow.IsAnnouncing);
        Assert.True(h.Shows[^1].NameNow);
        h.Clock.Advance(5000);
        Assert.Equal(0, h.Announcements);

        // The same text again, as a new object: no change.
        h.Flow.Set(PageStatus.Idle("12 settings found"));
        Assert.Equal(0, h.Clock.Pending);
    }

    [Fact]
    public void AResultThatArrivesWhileASummaryWaits_IsAnnouncedByTheCaller_AndTheSummaryIsDropped()
    {
        var h = new Harness();
        h.Flow.Set(PageStatus.Idle("12 settings found"));
        h.Flow.Set(PageStatus.Idle("4 settings found"));
        Assert.True(h.Flow.IsAnnouncing);

        h.Flow.Set(PageStatus.Error("Could not read the index.", Finished));
        Assert.False(h.Flow.IsAnnouncing);
        var shown = h.Shows[^1];
        Assert.True(shown.IsNew);
        Assert.True(shown.NameNow);
        h.Clock.Advance(5000);
        Assert.Equal(0, h.Announcements);
    }

    [Fact]
    public void ASummaryIsNotAnnounced_WhenThePageIsNotInFrontOrTheWindowIsNotActive_AndItDoesNotComeBackAsNews()
    {
        var h = new Harness { MayAnnounce = false };
        h.Flow.Set(PageStatus.Idle("12 settings found"));
        h.Flow.Set(PageStatus.Idle("4 settings found"));

        h.Clock.Advance(800);
        Assert.Equal(0, h.Announcements);
        Assert.Equal(1, h.NamesSettled);

        // The window is active again: the same text is not news; the next change is.
        h.MayAnnounce = true;
        h.Flow.Set(PageStatus.Idle("4 settings found"));
        Assert.Equal(0, h.Clock.Pending);
        h.Flow.Set(PageStatus.Idle("3 settings found"));
        h.Clock.Advance(800);
        Assert.Equal(1, h.Announcements);
    }

    [Fact]
    public void WithoutAThrottleToken_ASummaryIsAnnouncedAtOnce()
    {
        var h = new Harness { SummaryThrottle = null };
        h.Flow.Set(PageStatus.Idle("12 settings found"));
        h.Flow.Set(PageStatus.Idle("4 settings found"));
        Assert.Equal(1, h.Announcements);
        Assert.True(h.Shows[^1].NameNow);
        Assert.Empty(h.Clock.Requested);
    }

    [Fact]
    public void TheSummaryAfterAShownOperation_IsANewMessage_AndIsTheBaselineOfTheNextChange()
    {
        var h = new Harness();
        h.Flow.Set(Working());
        h.Clock.Advance(150);
        h.Flow.Set(PageStatus.Idle("2 314 objects"));

        // The end of an operation the operator has heard is announced at once by the caller, as before.
        var ended = h.Shows[^1];
        Assert.True(ended.IsNew);
        Assert.Equal(0, h.Announcements);
        Assert.False(h.Flow.IsAnnouncing);

        // A later change of the count is a change of the summary.
        h.Flow.Set(PageStatus.Idle("2 315 objects"));
        h.Clock.Advance(800);
        Assert.Equal(1, h.Announcements);
    }

    [Fact]
    public void AnOperationThatHasNotBeenShown_LeavesTheSummaryOfItsPageUntouched()
    {
        // Typing in a search box that reads on every letter: the read ends within the delay each time, so "Working"
        // never shows - and the count is announced once, after the last letter.
        var h = new Harness();
        h.Flow.Set(PageStatus.Idle("12 settings found"));
        foreach (var count in new[] { 9, 5, 3 })
        {
            h.Flow.Set(Working("Searching…"));
            h.Clock.Advance(40);
            h.Flow.Set(PageStatus.Idle($"{count} settings found"));
            h.Clock.Advance(100);
        }

        Assert.DoesNotContain(h.Shows, s => s.Status.IsWorking);
        Assert.Equal(0, h.Announcements);
        h.Clock.Advance(800);
        Assert.Equal(1, h.Announcements);
        Assert.Equal("3 settings found", h.Flow.Shown.Message);
    }

    // ---- The harness ----------------------------------------------------------------------------------------------

    private sealed record Shown(PageStatus Status, bool IsNew, bool NameNow);

    private sealed class Harness
    {
        public Harness()
        {
            Flow = new StatusFlow(
                Clock,
                () => WorkingDelay,
                () => SummaryThrottle,
                () => MayAnnounce,
                (status, isNew, nameNow) => Shows.Add(new Shown(status, isNew, nameNow)),
                () => NamesSettled++,
                () => Announcements++);
        }

        public ManualStatusScheduler Clock { get; } = new();

        public StatusFlow Flow { get; }

        public List<Shown> Shows { get; } = [];

        public int Announcements { get; private set; }

        public int NamesSettled { get; private set; }

        public TimeSpan? WorkingDelay { get; init; } = TimeSpan.FromMilliseconds(150);

        public TimeSpan? SummaryThrottle { get; init; } = TimeSpan.FromMilliseconds(800);

        public bool MayAnnounce { get; set; } = true;
    }
}
