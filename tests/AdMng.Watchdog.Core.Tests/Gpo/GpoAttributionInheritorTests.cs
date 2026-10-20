using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Gpo;
using AdMng.Watchdog.Testing.Stores;
using Microsoft.Extensions.Time.Testing;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Tests.Gpo;

/// <summary>Stage B design 8.7: content events inherit the GroupPolicyChanged attribution of the same versionNumber
/// transition; out-of-band content is SYSVOL_NOT_AUDITED; SYSVOL lag.</summary>
public sealed class GpoAttributionInheritorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 30, 0, TimeSpan.Zero);

    private static readonly EventAttribution Resolved = new()
    {
        Status = AttributionStatus.Resolved,
        Confidence = AttributionConfidence.High,
        Actor = new PrincipalRef { Sid = "S-1-5-21-1004336348-1177238915-682003330-1105", Account = "CONTOSO\\józef.wąsik" },
        Workstation = "PAW01",
    };

    [Fact]
    public async Task CauseFromTheChangeSet()
    {
        var store = Store();
        var changeSet = Guid.CreateVersion7(Now);
        await store.AppendAsync(GpcChanged(Now.AddMinutes(-3), "131074", "131075") with { ChangeSetId = changeSet }, Ct);
        var content = GpoContentEvents.SettingsChanged(GpoContentEventsTests.Context() with { ChangeSetId = changeSet }, new GpoSettingsDiff { Modified = 1 });

        var cause = await new GpoAttributionInheritor(store).FindCauseAsync(content, 131075, Ct);

        Assert.NotNull(cause);
        Assert.Equal(WatchEventKind.GroupPolicyChanged, cause.Kind);
        var inherited = GpoAttributionInheritor.Inherit(cause.Attribution, Now);
        Assert.Equal((AttributionStatus.Resolved, "CONTOSO\\józef.wąsik", Now), (inherited.Status, inherited.Actor?.Account, inherited.ResolvedAt));
    }

    [Fact]
    public async Task CauseByVersionTransitionWithinTheWindow()
    {
        var store = Store();
        await store.AppendAsync(GpcChanged(Now.AddHours(-2), "131073", "131074"), Ct);
        await store.AppendAsync(GpcChanged(Now.AddHours(-1), "131074", "131075"), Ct);
        var content = GpoContentEvents.SettingsChanged(GpoContentEventsTests.Context(), new GpoSettingsDiff { Modified = 1 });
        var inheritor = new GpoAttributionInheritor(store);

        var cause = await inheritor.FindCauseAsync(content, 131075, Ct);

        Assert.NotNull(cause);
        Assert.Equal(["131075"], cause.Changes.Single().NewValues);
        Assert.Null(await inheritor.FindCauseAsync(content, 131076, Ct));     // no event of that transition
        Assert.Null(await inheritor.FindCauseAsync(content, null, Ct));       // never guessed without the version
        Assert.Null(await inheritor.FindCauseAsync(content with { DetectedAt = Now.AddHours(4) }, 131075, Ct)); // outside the window
    }

    [Fact]
    public async Task PathRedirectionInheritsTheGpcFileSysPathChange()
    {
        var store = Store();
        var gpcChange = GpcChanged(Now.AddMinutes(-1), "131075", "131075") with
        {
            Changes = [new AttributeChange { Attribute = "gPCFileSysPath", OldValues = [@"\\contoso.com\SysVol\x"], NewValues = [@"\\evil\x"] }],
        };
        await store.AppendAsync(gpcChange, Ct);
        var redirected = GpoContentEvents.PathRedirected(GpoContentEventsTests.Context(), @"\\evil\x");

        var cause = await new GpoAttributionInheritor(store).FindCauseAsync(redirected, null, Ct);

        Assert.NotNull(cause);
        Assert.Equal("gPCFileSysPath", cause.Changes.Single().Attribute);
    }

    [Fact]
    public async Task OutOfBandIsNeverInherited()
    {
        var store = Store();
        await store.AppendAsync(GpcChanged(Now.AddMinutes(-1), "131074", "131075"), Ct);
        var outOfBand = GpoContentEvents.ContentOutOfBand(GpoContentEventsTests.Context(), new GpoSettingsDiff { Modified = 1 });

        Assert.Null(await new GpoAttributionInheritor(store).FindCauseAsync(outOfBand, 131075, Ct));
        Assert.Equal((AttributionStatus.Unknown, AttributionReasons.SysvolNotAudited, Now),
            (outOfBand.Attribution.Status, outOfBand.Attribution.Reason, outOfBand.Attribution.ResolvedAt));
    }

    [Theory]
    [InlineData(131075, 0, SysvolLagDecision.Read)]
    [InlineData(131076, 0, SysvolLagDecision.Read)]       // GPT.INI ahead (SYSVOL replicated first)
    [InlineData(131074, 0, SysvolLagDecision.Retry)]
    [InlineData(131074, 119, SysvolLagDecision.Retry)]
    [InlineData(131074, 120, SysvolLagDecision.ReadWithLagWarning)]
    [InlineData(65539, 10, SysvolLagDecision.Retry)]      // user half behind (1 < 2), computer half equal
    public void SysvolLag(int gptVersion, int minutesWaited, SysvolLagDecision expected)
    {
        var first = Now.AddMinutes(-minutesWaited);

        Assert.Equal(expected, SysvolLagPolicy.Decide(gptVersion, 131075, first, Now, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void MissingGptIniCountsAsLag()
    {
        Assert.Equal(SysvolLagDecision.Retry, SysvolLagPolicy.Decide(null, 131075, Now, Now, TimeSpan.FromHours(2)));
        Assert.Equal("gpo.6ac1786c-016f-11d2-945f-00c04fb984f9.sysvolLag", SysvolLagPolicy.HealthCode(GpoFixtures.Gpo));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static InMemoryWatchdogStore Store() => new(new FakeTimeProvider(Now));

    private static WatchEvent GpcChanged(DateTimeOffset at, string oldVersion, string newVersion) => new()
    {
        EventId = Guid.CreateVersion7(at),
        ChangeSetId = Guid.CreateVersion7(at),
        Kind = WatchEventKind.GroupPolicyChanged,
        Object = GpoContentEventsTests.Context().Gpo,
        Changes = [new AttributeChange { Attribute = "versionNumber", ValueKind = AttributeValueKind.Number, OldValues = [oldVersion], NewValues = [newVersion] }],
        DetectedAt = at,
        Source = DetectionSource.IncrementalPoll,
        Attribution = Resolved,
    };
}
