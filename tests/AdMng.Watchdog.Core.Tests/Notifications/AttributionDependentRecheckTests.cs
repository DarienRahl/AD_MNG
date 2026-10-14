using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Notifications;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;
using static AdMng.Watchdog.Core.Tests.Notifications.NotificationHarness;

namespace AdMng.Watchdog.Core.Tests.Notifications;

public sealed class AttributionDependentRecheckTests
{
    private const string ServiceDeskSid = "S-1-5-21-1004336348-1177238915-682003330-3105";

    private readonly AttributionDependentRecheck _matcher = new();
    private readonly WatchdogConfiguration _configuration = ConfigWith();

    [Fact]
    public void StillMatching_UsesTheRuleOverrideOrTheReResolvedSeverity()
    {
        var rule = Rule("password-resets");
        var watchEvent = Event(WatchEventKind.PasswordChanged, Severity.Medium, tags: [EventTags.PasswordReset]);

        Assert.Equal(new PlannedMatch(rule, Severity.Medium), _matcher.Rematch(watchEvent, rule, _configuration, Now, false));
        Assert.Equal(Severity.High, _matcher.Rematch(watchEvent, rule with { Severity = Severity.High }, _configuration, Now, false)!.NotificationSeverity);
    }

    [Fact]
    public void SelfChange_IsDroppedByExcludeSelfChanges()
    {
        var rule = Rule("tier0-password") with { Actors = new ActorFilter { ExcludeSelfChanges = true } };
        var own = Event(WatchEventKind.PasswordChanged, Severity.Medium, tags: [EventTags.SelfChange]);

        Assert.Null(_matcher.Rematch(own, rule, _configuration, Now, false));
        Assert.NotNull(_matcher.Rematch(own with { Tags = [EventTags.PasswordReset] }, rule, _configuration, Now, false));
    }

    [Fact]
    public void ReResolvedSeverityBelowMinSeverity_IsDropped()
    {
        var rule = Rule("privileged") with { MinSeverity = Severity.High };

        Assert.Null(_matcher.Rematch(Event(severity: Severity.Medium), rule, _configuration, Now, true));
        Assert.NotNull(_matcher.Rematch(Event(severity: Severity.High), rule, _configuration, Now, true));
    }

    [Fact]
    public void Tags_AreCheckedAgain()
    {
        var required = Rule("resets") with { RequiredTags = [EventTags.PasswordReset] };
        var any = Rule("any") with { AnyTags = [EventTags.PasswordReset, EventTags.SelfChange] };

        Assert.Null(_matcher.Rematch(Event(), required, _configuration, Now, false));
        Assert.Null(_matcher.Rematch(Event(), any, _configuration, Now, false));
        Assert.NotNull(_matcher.Rematch(Event(tags: [EventTags.SelfChange]), any, _configuration, Now, false));
    }

    [Fact]
    public void ExcludedActor_IsDroppedBySidOnly()
    {
        var rule = Rule("resets") with { Actors = new ActorFilter { ExcludeActors = [ServiceDeskSid, "{serviceDesk}"] } };

        Assert.Null(_matcher.Rematch(Event(attribution: Resolved(ServiceDeskSid)), rule, _configuration, Now, false));
        Assert.NotNull(_matcher.Rematch(Event(attribution: Resolved()), rule, _configuration, Now, false));
    }

    [Fact]
    public void ActorExclusion_RespectsExpiryObjectAndSubtree()
    {
        var objectGuid = Guid.NewGuid();
        RuleExclusion Exclusion(Guid? guid = null, string? subtree = null, DateTimeOffset? expires = null) => new()
        {
            Id = "ex-1",
            Actor = ServiceDeskSid,
            ObjectGuid = guid,
            SubtreeDn = subtree,
            ExpiresAt = expires,
            Reason = "Konto serwisowe działu wsparcia",
            CreatedBy = @"CONTOSO\adm.wójcik",
            CreatedAt = Now.AddDays(-1),
        };
        var watchEvent = Event(attribution: Resolved(ServiceDeskSid), objectGuid: objectGuid);
        var rule = Rule("resets");

        Assert.Null(_matcher.Rematch(watchEvent, rule with { Exclusions = [Exclusion()] }, _configuration, Now, false));
        Assert.NotNull(_matcher.Rematch(watchEvent, rule with { Exclusions = [Exclusion(expires: Now.AddMinutes(-1))] }, _configuration, Now, false));
        Assert.NotNull(_matcher.Rematch(watchEvent, rule with { Exclusions = [Exclusion(guid: Guid.NewGuid())] }, _configuration, Now, false));
        Assert.Null(_matcher.Rematch(watchEvent, rule with { Exclusions = [Exclusion(guid: objectGuid)] }, _configuration, Now, false));
        Assert.Null(_matcher.Rematch(watchEvent, rule with { Exclusions = [Exclusion(subtree: "OU=Firma,DC=contoso,DC=com")] }, _configuration, Now, false));
        Assert.NotNull(_matcher.Rematch(watchEvent, rule with { Exclusions = [Exclusion(subtree: "OU=Zarząd,DC=contoso,DC=com")] }, _configuration, Now, false));
    }

    [Fact]
    public void PendingActor_WithActorConditions_IsProvisionalUntilTheGraceEnds()
    {
        var rule = Rule("tier0-password") with { Actors = new ActorFilter { ExcludeSelfChanges = true } };
        var pending = Event(WatchEventKind.PasswordChanged, Severity.Critical, EventAttribution.Pending);

        Assert.True(_matcher.Rematch(pending, rule, _configuration, Now, false)!.IsProvisional);
        Assert.False(_matcher.Rematch(pending, rule, _configuration, Now, true)!.IsProvisional);
        Assert.False(_matcher.Rematch(pending, Rule("plain"), _configuration, Now, false)!.IsProvisional);
    }

    [Fact]
    public void DisabledRule_IsDropped() =>
        Assert.Null(_matcher.Rematch(Event(), Rule("off") with { Enabled = false }, _configuration, Now, false));
}
