using AdMng.Presentation.Infrastructure;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>
/// <see cref="ColumnFit"/> with the columns of the Watchdog lists (the same widths as <c>WatchdogEventsView.xaml</c> and
/// <c>WatchdogRulesView.xaml</c>): at the screenshot size 1440 × 900 and in the main window's default size 1360 × 840
/// every shown column fits the list (no horizontal scrolling), the title takes what the others leave without starving them,
/// optional columns appear in a fixed order as the list widens and the operator's widths are kept.
/// </summary>
public sealed class WatchdogColumnFitTests
{
    // Width left for the columns of the feed at 1440 × 900 with the details pane open (730 px list minus the row's border
    // and padding, the vertical scroll bar and a pixel for rounding) and with the pane dragged to its 300 px minimum.
    private const double FeedAt1440 = 711;
    private const double FeedWithNarrowDetails = 810;

    // The same in the main window's default size 1360 × 840 (MainWindow.xaml; a client area of 1344 px inside the frame):
    // 96 px less than at 1440 – a 634 px list. The second value allows for a 17 px scroll bar (the system default width).
    private const double FeedAtDefaultWindow = 615;
    private const double FeedAtDefaultWindowWideScrollBar = 610;

    // Width left for the columns of the rule list at 1440 × 900 (5/9 of the tab next to the editor).
    private const double RulesAt1440 = 606;

    // The rule list in the default window: 5/9 of the 96 px less (553 px).
    private const double RulesAtDefaultWindow = 553;

    // Width left for the columns of the delivery log on the Status tab at 1440 × 900 (the card's 1109 px inside its border
    // and padding, minus the list border, the row's border and padding, the vertical scroll bar and a pixel for rounding).
    private const double DeliveryLogAt1440 = 1088;

    // The delivery log in the default window: the card is 96 px narrower.
    private const double DeliveryLogAtDefaultWindow = 992;

    private const int Severity = 0, Time = 1, Title = 2, Object = 3, Path = 4, Actor = 5, Source = 6, Lockouts = 7, Acknowledged = 8;

    /// <summary>The feed: severity, time, the title (star), object, then the optional location, actor, source, lockouts and ✓.</summary>
    private static readonly FitColumn[] FeedColumns =
    [
        new(104, 100),
        new(144, 132),
        new(244, 244, Star: 1),
        new(150, 124),
        new(240, 160, Priority: 4),
        new(180, 120, Priority: 2),
        new(170, 130, Priority: 3),
        new(80, 80, Priority: 5),
        new(44, 40, Priority: 1),
    ];

    /// <summary>The rules: enabled, name, kinds and delivery (stars 4 : 3 : 3).</summary>
    private static readonly FitColumn[] RuleColumns =
    [
        new(44, 40),
        new(250, 200, Star: 4),
        new(210, 140, Star: 3),
        new(260, 150, Star: 3),
    ];

    /// <summary>
    /// The delivery log (<c>WatchdogStatusView.xaml</c>): created, channel, state, attempts, sent / next, subject and error
    /// (stars 3 : 2) and the retry button.
    /// </summary>
    private static readonly FitColumn[] DeliveryLogColumns =
    [
        new(150, 140),
        new(150, 120),
        new(140, 130),
        new(60, 56),
        new(180, 170),
        new(260, 160, Star: 3),
        new(220, 120, Star: 2),
        new(100, 90),
    ];

    [Fact]
    public void Feed_At1440WithTheDetailsPane_FitsTheList_TitleTakesTheRest_ObjectKeepsItsMinimum()
    {
        var widths = ColumnFit.Fit(FeedAt1440, FeedColumns);

        AssertFits(FeedAt1440, FeedColumns, widths);
        Assert.Equal(FeedAt1440, Shown(widths).Sum());
        Assert.True(widths[Title] >= 260, $"The title column is {widths[Title]} px wide.");
        Assert.True(widths[Object] >= 150, $"The object column is {widths[Object]} px wide.");
        Assert.NotNull(widths[Acknowledged]);

        // Not enough room for the actor (120 px): it and every later optional column wait for a wider list.
        Assert.Null(widths[Actor]);
        Assert.Null(widths[Source]);
        Assert.Null(widths[Path]);
        Assert.Null(widths[Lockouts]);
    }

    /// <summary>
    /// The app opens at 1360 × 840 (no window placement is restored): severity, time, title and object fit next to the
    /// details pane at their minimum, so the feed does not scroll sideways and the object column is not under the scroll bar.
    /// The widths before (title 260, time 140 and object 140 at least: 640 px) did not fit there.
    /// </summary>
    [Theory]
    [InlineData(FeedAtDefaultWindow)]
    [InlineData(FeedAtDefaultWindowWideScrollBar)]
    public void Feed_AtTheDefaultWindowSize_TheRequiredColumnsFit_NoSidewaysScrolling(double available)
    {
        Assert.True(100 + 140 + 260 + 140 > available, "The test no longer covers the old minimum widths.");

        var widths = ColumnFit.Fit(available, FeedColumns);

        AssertFits(available, FeedColumns, widths);
        Assert.Equal(Math.Floor(available), Shown(widths).Sum());
        Assert.InRange(widths[Title]!.Value, FeedColumns[Title].MinWidth, FeedColumns[Title].MinWidth + 3);
        Assert.True(widths[Time] >= 134, $"The time column is {widths[Time]} px wide.");
        Assert.True(widths[Object] >= FeedColumns[Object].MinWidth);
        Assert.All(Enumerable.Range(0, FeedColumns.Length).Where(i => FeedColumns[i].Priority > 0), i => Assert.Null(widths[i]));
    }

    [Fact]
    public void RulesAndDeliveryLog_AtTheDefaultWindowSize_FitTheirLists()
    {
        var rules = ColumnFit.Fit(RulesAtDefaultWindow, RuleColumns);
        AssertFits(RulesAtDefaultWindow, RuleColumns, rules);
        Assert.Equal(RulesAtDefaultWindow, Shown(rules).Sum());

        var log = ColumnFit.Fit(DeliveryLogAtDefaultWindow, DeliveryLogColumns);
        AssertFits(DeliveryLogAtDefaultWindow, DeliveryLogColumns, log);
        Assert.All(log, w => Assert.NotNull(w));
        Assert.Equal(DeliveryLogAtDefaultWindow, Shown(log).Sum());
    }

    [Fact]
    public void Feed_DetailsPaneNarrowed_ShowsTheActorToo()
    {
        var widths = ColumnFit.Fit(FeedWithNarrowDetails, FeedColumns);

        AssertFits(FeedWithNarrowDetails, FeedColumns, widths);
        Assert.NotNull(widths[Actor]);
        Assert.NotNull(widths[Acknowledged]);
        Assert.Null(widths[Source]);
    }

    [Fact]
    public void Feed_OnAWideScreen_ShowsEveryColumn_AtLeastAtItsWidth_TitleTakesTheRest()
    {
        var widths = ColumnFit.Fit(1500, FeedColumns);

        AssertFits(1500, FeedColumns, widths);
        Assert.All(Enumerable.Range(0, FeedColumns.Length).Where(i => i != Title), i => Assert.Equal(FeedColumns[i].Width, widths[i]));
        Assert.Equal(1500 - FeedColumns.Where(c => c.Star == 0).Sum(c => c.Width), widths[Title]);
    }

    [Fact]
    public void Feed_OptionalColumnsAppearInTheirOrder_AsTheListWidens_AndNeverDisappear()
    {
        HashSet<int> shownBefore = [];
        for (var available = 640d; available <= 1600; available += 5)
        {
            var widths = ColumnFit.Fit(available, FeedColumns);
            AssertFits(available, FeedColumns, widths);
            var shown = Enumerable.Range(0, widths.Count).Where(i => widths[i] is not null).ToHashSet();
            Assert.Superset(shownBefore, shown);
            shownBefore = shown;

            // Optional columns form a prefix of the priority order.
            var optional = Enumerable.Range(0, FeedColumns.Length).Where(i => FeedColumns[i].Priority > 0).OrderBy(i => FeedColumns[i].Priority).ToList();
            var visible = optional.Select(i => widths[i] is not null).ToList();
            Assert.Equal(visible.OrderByDescending(v => v), visible);
        }
    }

    [Fact]
    public void Feed_TooNarrowForTheRequiredColumns_KeepsThemAtTheirMinimum_AndHidesTheRest()
    {
        var widths = ColumnFit.Fit(500, FeedColumns);

        Assert.Equal([100d, 132, 244, 124, null, null, null, null, null], widths);
    }

    [Fact]
    public void OperatorsWidth_IsKept_TheTitleGivesWay()
    {
        FitColumn[] columns = [.. FeedColumns];
        columns[Object] = new(320, 0, IsFixed: true);

        var widths = ColumnFit.Fit(FeedWithNarrowDetails, columns);

        Assert.Equal(320, widths[Object]);
        AssertFits(FeedWithNarrowDetails, columns, widths);
        Assert.True(widths[Title] >= FeedColumns[Title].MinWidth);
        Assert.Equal(FeedWithNarrowDetails, Shown(widths).Sum());

        // Wider than the list allows: the operator's width still wins (the list scrolls), the title keeps its minimum.
        columns[Object] = new(700, 0, IsFixed: true);
        widths = ColumnFit.Fit(FeedAt1440, columns);
        Assert.Equal(700, widths[Object]);
        Assert.Equal(FeedColumns[Title].MinWidth, widths[Title]);
        Assert.Null(widths[Acknowledged]);
    }

    [Fact]
    public void OperatorResizedTheTitle_NothingFills()
    {
        FitColumn[] columns = [.. FeedColumns];
        columns[Title] = new(300, 0, IsFixed: true);

        var widths = ColumnFit.Fit(1500, columns);

        Assert.Equal(300, widths[Title]);
        Assert.All(Enumerable.Range(0, columns.Length).Where(i => i != Title), i => Assert.Equal(columns[i].Width, widths[i]));
    }

    [Fact]
    public void Rules_At1440_FitTheList_TheTextColumnsShareTheRestByWeight()
    {
        var widths = ColumnFit.Fit(RulesAt1440, RuleColumns);

        AssertFits(RulesAt1440, RuleColumns, widths);
        Assert.All(widths, w => Assert.NotNull(w));
        Assert.Equal(RulesAt1440, Shown(widths).Sum());
        Assert.Equal(44, widths[0]);
        Assert.InRange(widths[1]!.Value, 224, 226);
        Assert.InRange(widths[2]!.Value, 168, 169);
        Assert.InRange(widths[3]!.Value, 168, 169);
    }

    [Fact]
    public void Rules_Narrow_StarColumnsKeepTheirMinimum_OthersShrinkFirst()
    {
        var widths = ColumnFit.Fit(500, RuleColumns);

        Assert.Equal([40d, 200, 140, 150], widths);
    }

    [Fact]
    public void DeliveryLog_At1440_FitsTheStatusCard_SubjectAndErrorShareTheRest()
    {
        // The XAML widths alone (1260 px) are wider than the card.
        Assert.True(DeliveryLogColumns.Sum(c => c.Width) > DeliveryLogAt1440);

        var widths = ColumnFit.Fit(DeliveryLogAt1440, DeliveryLogColumns);

        AssertFits(DeliveryLogAt1440, DeliveryLogColumns, widths);
        Assert.All(widths, w => Assert.NotNull(w));
        Assert.Equal(DeliveryLogAt1440, Shown(widths).Sum());
        Assert.All(Enumerable.Range(0, DeliveryLogColumns.Length).Where(i => DeliveryLogColumns[i].Star == 0), i => Assert.Equal(DeliveryLogColumns[i].Width, widths[i]));
        Assert.InRange(widths[5]!.Value, 184, 186);
        Assert.InRange(widths[6]!.Value, 123, 124);
    }

    [Fact]
    public void Fit_IsDeterministic_InWholePixels_AndHandlesDegenerateInput()
    {
        Assert.Equal(ColumnFit.Fit(733.7, FeedColumns), ColumnFit.Fit(733.7, FeedColumns));
        Assert.All(Shown(ColumnFit.Fit(733.7, FeedColumns)), w => Assert.Equal(Math.Floor(w), w));
        Assert.Equal(733, Shown(ColumnFit.Fit(733.7, FeedColumns)).Sum());

        Assert.Empty(ColumnFit.Fit(800, []));
        Assert.Equal([100d, 132, 244, 124, null, null, null, null, null], ColumnFit.Fit(double.NaN, FeedColumns));
        Assert.Equal([100d, 132, 244, 124, null, null, null, null, null], ColumnFit.Fit(-20, FeedColumns));
    }

    /// <summary>Every shown column is at least its minimum (or exactly the operator's width) and together they fit.</summary>
    private static void AssertFits(double available, IReadOnlyList<FitColumn> columns, IReadOnlyList<double?> widths)
    {
        Assert.Equal(columns.Count, widths.Count);
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            if (column.Priority == 0 || column.IsFixed)
            {
                Assert.NotNull(widths[i]);
            }

            if (widths[i] is { } width)
            {
                Assert.True(column.IsFixed ? width == column.Width : width >= column.MinWidth, $"Column {i} is {width} px wide (minimum {column.MinWidth}).");
            }
        }

        if (columns.Any(c => c.IsFixed))
        {
            return;
        }

        Assert.True(Shown(widths).Sum() <= available, $"The columns need {Shown(widths).Sum()} of {available} px.");
    }

    private static IEnumerable<double> Shown(IReadOnlyList<double?> widths) => widths.Where(w => w is not null).Select(w => w!.Value);
}
