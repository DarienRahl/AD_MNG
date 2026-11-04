using AdMng.Core.EventLogs;
using static AdMng.Testing.EventLogs.EventLogSamples;

namespace AdMng.Core.Tests.EventLogs;

public class EventLogMatcherTests
{
    private static readonly IReadOnlyList<EventLogRecord> Records = [.. LockoutScenario(), .. FailedLogonScenario()];

    [Fact]
    public void LockoutQuery_SelectsTheFailuresOfTheAccount_NewestFirst()
    {
        var query = new EventLogQuery
        {
            Channel = SecurityChannel,
            EventIds = [4740, 4771, 4776, 4625],
            Data = [new EventDataCondition("TargetUserName", [JanKowalski])],
        };

        var selected = EventLogMatcher.Apply(query, Records);

        Assert.Equal([4740, 4771, 4776, 4771, 4771, 4771, 4771], selected.Select(r => r.EventId));
        Assert.Equal([9_004L, 9_003, 5_002, 9_002, 5_001, 9_001], selected.Select(r => r.RecordId).Take(6));
        Assert.All(selected.Zip(selected.Skip(1)), pair => Assert.True(pair.First.TimeCreated >= pair.Second.TimeCreated));
    }

    [Fact]
    public void OldestFirst_AndMaxRecords()
    {
        var query = new EventLogQuery { Channel = SecurityChannel, EventIds = [4771], NewestFirst = false, MaxRecords = 3 };

        var selected = EventLogMatcher.Apply(query, Records);

        Assert.Equal(3, selected.Count);
        Assert.Equal(Records.Where(r => r.EventId == 4771).Min(r => r.TimeCreated), selected[0].TimeCreated);
        Assert.Empty(EventLogMatcher.Apply(query with { MaxRecords = 0 }, Records));
    }

    [Fact]
    public void DataValuesCompareCaseSensitively_LikeTheEventLogService()
    {
        var exact = new EventLogQuery { Channel = SecurityChannel, Data = [new EventDataCondition("TargetUserName", [ZofiaZak])] };
        var upper = exact with { Data = [new EventDataCondition("TargetUserName", ["Zofia.Zak"])] };
        var name = exact with { Data = [new EventDataCondition("targetusername", [ZofiaZak])] };

        Assert.NotEmpty(EventLogMatcher.Apply(exact, Records));
        Assert.Empty(EventLogMatcher.Apply(upper, Records));
        Assert.Empty(EventLogMatcher.Apply(name, Records));
    }

    [Fact]
    public void ValuesOfOneConditionAreOred_ConditionsAnded()
    {
        var query = new EventLogQuery
        {
            Channel = SecurityChannel,
            EventIds = [4771],
            Data =
            [
                new EventDataCondition("TargetUserName", [JanKowalski, ZofiaZak, LukaszWisniewski]),
                new EventDataCondition("IpAddress", ["::ffff:" + VpnIp]),
            ],
        };

        var selected = EventLogMatcher.Apply(query, Records);

        Assert.Equal([LukaszWisniewski, ZofiaZak, JanKowalski], selected.Select(r => r.Get("TargetUserName")));
    }

    [Fact]
    public void ADataConditionWithoutValues_MatchesNothing() =>
        Assert.Empty(EventLogMatcher.Apply(new EventLogQuery { Channel = SecurityChannel, Data = [new EventDataCondition("TargetUserName", [])] }, Records));

    [Fact]
    public void Outcome_UsesTheAuditKeywords()
    {
        var success = EventLogMatcher.Apply(new EventLogQuery { Channel = SecurityChannel, Outcome = EventAuditOutcome.Success }, Records);
        var failure = EventLogMatcher.Apply(new EventLogQuery { Channel = SecurityChannel, Outcome = EventAuditOutcome.Failure }, Records);

        Assert.All(success, r => Assert.True(r.IsAuditSuccess));
        Assert.All(failure, r => Assert.True(r.IsAuditFailure));
        Assert.Equal(Records.Count, success.Count + failure.Count);
        Assert.Contains(success, r => r.EventId == 4740);
    }

    [Fact]
    public void TimeBoundsAreInclusive_AndRoundedLikeTheQueryXml()
    {
        var lockout = Records.Single(r => r.EventId == 4740);
        var at = lockout.TimeCreated;

        Assert.Single(EventLogMatcher.Apply(new EventLogQuery { Channel = SecurityChannel, EventIds = [4740], From = at, To = at }, Records));
        Assert.Single(EventLogMatcher.Apply(new EventLogQuery { Channel = SecurityChannel, EventIds = [4740], From = at.AddTicks(1), To = at.AddTicks(2) }, Records));
        Assert.Empty(EventLogMatcher.Apply(new EventLogQuery { Channel = SecurityChannel, EventIds = [4740], From = at.AddMilliseconds(1) }, Records));
        Assert.Empty(EventLogMatcher.Apply(new EventLogQuery { Channel = SecurityChannel, EventIds = [4740], To = at.AddMilliseconds(-1) }, Records));
        Assert.Single(EventLogMatcher.Apply(new EventLogQuery { Channel = SecurityChannel, EventIds = [4740], From = at.ToOffset(TimeSpan.FromHours(1)) }, Records));
    }

    [Fact]
    public void LevelsAndProviders()
    {
        var gp = Record("WAW-PC001.contoso.test", GroupPolicyChannel, "Microsoft-Windows-GroupPolicy", 7016, 1, Morning, EventLogLevels.Error,
            [("CSEExtensionName", "Ustawienia zabezpieczeń"), ("ErrorCode", "1332")]);
        var info = gp with { RecordId = 2, EventId = 8001, Level = EventLogLevels.Information };
        var noLevel = gp with { RecordId = 3, Level = null, Provider = null };
        IReadOnlyList<EventLogRecord> records = [gp, info, noLevel];

        Assert.Equal([1L], EventLogMatcher.Apply(new EventLogQuery { Channel = GroupPolicyChannel, Levels = [EventLogLevels.Error] }, records).Select(r => r.RecordId));
        Assert.Equal([2L, 1L], EventLogMatcher.Apply(new EventLogQuery { Channel = GroupPolicyChannel, Providers = ["Microsoft-Windows-GroupPolicy"] }, records)
            .Select(r => r.RecordId));
        Assert.Empty(EventLogMatcher.Apply(new EventLogQuery { Channel = GroupPolicyChannel, Providers = ["microsoft-windows-grouppolicy"] }, records));
    }

    [Fact]
    public void DecodedRecordValuesMatchTheRawConditionValue()
    {
        var record = Record("DC01.contoso.test", SecurityChannel, SecurityAuditing, 4738, 1, Morning, EventLogLevels.LogAlways,
            [("TargetUserName", ZofiaZak), ("PasswordLastSet", "%%1794")]);

        Assert.Equal(InsertionStringDecoder.Never, record.Get("PasswordLastSet"));
        Assert.True(EventLogMatcher.Matches(new EventLogQuery { Channel = SecurityChannel, Data = [new EventDataCondition("PasswordLastSet", ["%%1794"])] }, record));
        Assert.True(EventLogMatcher.Matches(new EventLogQuery { Channel = SecurityChannel, Data = [new EventDataCondition("PasswordLastSet", ["<never>"])] }, record));
    }

    [Fact]
    public void RawXPathIsNotEvaluated_EveryRecordOfTheLogIsSelected_OrderAndLimitStillApply()
    {
        var query = new EventLogQuery
        {
            Channel = SecurityChannel,
            EventIds = [4740],
            From = Morning.AddYears(1),
            RawXPath = "*[System[EventID=4740]]",
            MaxRecords = 5,
        };

        var selected = EventLogMatcher.Apply(query, Records);

        Assert.Equal(5, selected.Count);
        Assert.Equal(Records.Max(r => r.TimeCreated), selected[0].TimeCreated);
    }

    [Fact]
    public void ChannelIsNotCompared()
    {
        var query = new EventLogQuery { Channel = "Application", EventIds = [4740] };

        Assert.Single(EventLogMatcher.Apply(query, Records));
    }

    [Fact]
    public void TheMatcherAgreesWithTheQueryChunking()
    {
        var ids = Enumerable.Range(4600, 200).ToList();
        var query = new EventLogQuery { Channel = SecurityChannel, EventIds = ids };

        _ = EventQueryXml.Build(query);   // valid: 10 Selects
        Assert.Equal(Records.Count(r => ids.Contains(r.EventId)), EventLogMatcher.Apply(query, Records).Count);
    }
}
