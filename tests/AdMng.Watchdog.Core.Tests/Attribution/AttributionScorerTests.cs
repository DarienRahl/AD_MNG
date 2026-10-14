using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Attribution;
using static AdMng.Watchdog.Core.Tests.Attribution.Records;
using static AdMng.Watchdog.Core.Tests.Rules.TestEvents;

namespace AdMng.Watchdog.Core.Tests.Attribution;

public class AttributionScorerTests
{
    private static readonly DateTimeOffset T = Now.AddMinutes(-1);

    private static AttributionSearchPlan Plan(string? originatingDc = Dc1) => new()
    {
        DomainControllers = [Dc1, Dc2],
        From = T.AddSeconds(-3),
        To = T.AddSeconds(3),
        ReferenceTime = T,
        OriginatingDc = originatingDc,
        HasOriginatingTime = true,
    };

    [Fact]
    public void StrongConfirmedSameDcClose_Scores100()
    {
        var reset = Account(4724, T.AddSeconds(1), Admin);

        var scored = AttributionScorer.Score(reset, Create(WatchEventKind.PasswordChanged), strongKey: true, Plan())!;

        Assert.Equal(50 + 30 + 10 + 10, scored.Score);
        Assert.Equal(AttributionConfidence.High, AttributionScorer.Confidence(scored));
    }

    [Theory]
    [InlineData(2, 10)]
    [InlineData(3, 5)]
    [InlineData(30, 5)]
    [InlineData(31, 0)]
    public void TimeDistance(int seconds, int bonus)
    {
        var reset = Account(4724, T.AddSeconds(-seconds), Admin);

        Assert.Equal(50 + 30 + 10 + bonus, AttributionScorer.Score(reset, Create(WatchEventKind.PasswordChanged), true, Plan())!.Score);
    }

    [Fact]
    public void OtherDcAndWeakKey_ScoreLess()
    {
        var reset = Account(4724, T, Admin, computer: Dc2);

        Assert.Equal(50 + 30 + 10, AttributionScorer.Score(reset, Create(WatchEventKind.PasswordChanged), true, Plan())!.Score);
        var weak = AttributionScorer.Score(reset, Create(WatchEventKind.PasswordChanged), false, Plan())!;
        Assert.Equal(20 + 30 + 10, weak.Score);
        Assert.Equal(AttributionConfidence.Low, AttributionScorer.Confidence(weak));
    }

    [Fact]
    public void UnconfirmedStrongKey_IsMediumConfidence()
    {
        // 4738 with every SAM field "-" (department change through ADUC).
        var changed = Account(4738, T, Admin);

        var scored = AttributionScorer.Score(changed, Create(WatchEventKind.AttributesChanged, changedAttributes: ["department"]), true, Plan())!;

        Assert.Equal(50 + 10 + 10, scored.Score);
        Assert.False(scored.Confirmed);
        Assert.Equal(AttributionConfidence.Medium, AttributionScorer.Confidence(scored));
    }

    [Fact]
    public void BelowFifty_IsDiscarded()
    {
        var far = Account(4738, T.AddMinutes(-5), Admin, computer: Dc2);

        Assert.Null(AttributionScorer.Score(far, Create(WatchEventKind.AttributesChanged, changedAttributes: ["department"]), false, Plan()));
    }

    [Fact]
    public void ContradictingRecords_AreNoCandidates()
    {
        var otherAttribute = Modified(T, Admin, "description", "Konto testowe", added: true);

        Assert.Null(AttributionScorer.Score(otherAttribute, Create(WatchEventKind.AttributesChanged, changedAttributes: ["department"]), true, Plan()));
    }
}
