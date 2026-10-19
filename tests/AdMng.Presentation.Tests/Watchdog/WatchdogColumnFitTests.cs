using AdMng.Presentation.Infrastructure;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>
/// <see cref="ColumnFit"/> with the columns of the Watchdog lists (the same widths as <c>WatchdogEventsView.xaml</c> and
/// <c>WatchdogRulesView.xaml</c>): at the screenshot size 1440 × 900 every shown column fits the list (no horizontal
/// scrolling), the title takes what the others leave without starving them, optional columns appear in a fixed order as
/// the list widens and the operator's widths are kept.
/// </summary>
public sealed class WatchdogColumnFitTests
{
    // Width left for the columns of the feed at 1440 × 900 with the details pane open (730 px list minus the row's border
    // and padding, the vertical scroll bar and a pixel for rounding) and with the pane dragged to its 300 px minimum.
    private const double FeedAt1440 = 711;
    private const double FeedWithNarrowDetails = 810;

    // Width left for the columns of the rule list at 1440 × 900 (5/9 of the tab next to the editor).
    private const double RulesAt1440 = 606;

    private const int Severity = 0, Time = 1, Title = 2, Object = 3, Path = 4, Actor = 5, Source = 6, Lockouts = 7, Acknowledged = 8;

    /// <summary>The feed: severity, time, the title (star), object, then the optional location, actor, source, lockouts and ✓.</summary>
    private static readonly FitColumn[] FeedColumns =
    [
        new(110, 100),
        new(165, 140),
        new(260, 260, Star: 1),
        new(200, 140),
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

        Assert.Equal([100d, 140, 260, 140, null, null, null, null, null], widths);
    }

    [Fact]
    public void OperatorsWidth_IsKept_TheTitleGivesWay()
    {
        FitColumn[] columns = [.. FeedColumns];
        columns[Object] = new(320, 0, IsFixed: true);

        var widths = ColumnFit.Fit(FeedWithNarrowDetails, columns);

        Assert.Equal(320, widths[Object]);
        AssertFits(FeedWithNarrowDetails, columns, widths);
        Assert.True(widths[Title] >= 260);

        // Wider than the list allows: the operator's width still wins (the list scrolls), the title keeps its minimum.
        columns[Object] = new(700, 0, IsFixed: true);
        widths = ColumnFit.Fit(FeedAt1440, columns);
        Assert.Equal(700, widths[Object]);
        Assert.Equal(260, widths[Title]);
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
    public void Fit_IsDeterministic_InWholePixels_AndHandlesDegenerateInput()
    {
        Assert.Equal(ColumnFit.Fit(733.7, FeedColumns), ColumnFit.Fit(733.7, FeedColumns));
        Assert.All(Shown(ColumnFit.Fit(733.7, FeedColumns)), w => Assert.Equal(Math.Floor(w), w));
        Assert.Equal(733, Shown(ColumnFit.Fit(733.7, FeedColumns)).Sum());

        Assert.Empty(ColumnFit.Fit(800, []));
        Assert.Equal([100d, 140, 260, 140, null, null, null, null, null], ColumnFit.Fit(double.NaN, FeedColumns));
        Assert.Equal([100d, 140, 260, 140, null, null, null, null, null], ColumnFit.Fit(-20, FeedColumns));
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
