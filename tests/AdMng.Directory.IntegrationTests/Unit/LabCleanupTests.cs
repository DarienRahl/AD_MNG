using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Directory.IntegrationTests.Infrastructure;
using AdMng.Testing;
using TestClock = AdMng.Testing.ManualTimeProvider;

namespace AdMng.Directory.IntegrationTests.Unit;

/// <summary>
/// Cleanup of the shared Samba lab, against the in-memory directory (no server needed): Samba gives one LDAP request at
/// most 300 s and runs one write transaction at a time, so test trees with thousands of objects are deleted client-side
/// in short requests; run OUs are unique per run and carry their owner, so a run removes leftovers of dead runs at
/// start-up without touching the OUs of runs still in progress; a busy or missing lab is explained.
/// </summary>
public sealed class LabCleanupTests
{
    private const string Prefix = "AdMngIntegration-";
    private const string Purpose = "AD_MNG integration tests";
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DeleteTree_OfALargeTree_NeverAsksForAServerSideTreeDelete()
    {
        var lab = new FakeDirectoryBuilder(timeProvider: new TestClock(Now));
        var run = lab.AddOu(Prefix + "20261018-230000-0dead0");
        var range = lab.AddOu("Zakres 1a2b3c4d", run);
        var contacts = Enumerable.Range(0, 400).Select(i => lab.AddContact("Kontakt", $"{i:D4} Żółw", range)).ToArray();
        var group = lab.AddGroup("GG_Duża_Grupa", range, members: contacts);
        var deep = lab.AddOu("Głęboko", lab.AddOu("Podrzędne", run));
        lab.AddUser("Zofia", "Żak", deep);
        var session = lab.Build();
        session.BeforeOperation = op =>
        {
            if (op is { Kind: FakeOperationKind.Delete, Subtree: true })
            {
                throw TimeLimitExceeded(op.DistinguishedName);
            }
        };

        var deleted = await LabCleanup.DeleteTreeAsync(session, run, Ct);

        Assert.Null(await session.GetAsync(run, [], Ct));
        Assert.Equal(contacts.Length + 6, deleted);
        var operations = session.Operations.ToList();
        Assert.DoesNotContain(operations, o => o is { Kind: FakeOperationKind.Delete, Subtree: true });

        // The group loses its members in one modify before any member is deleted (Samba rewrites a group's links for
        // every deleted member), and children always go before their parents.
        var clear = operations.FindIndex(o => o.Kind == FakeOperationKind.Modify && DistinguishedName.AreEqual(o.DistinguishedName, group));
        var firstDelete = operations.FindIndex(o => o.Kind == FakeOperationKind.Delete);
        Assert.InRange(clear, 0, firstDelete);
        var deletes = operations.Where(o => o.Kind == FakeOperationKind.Delete).Select(o => DistinguishedName.Parse(o.DistinguishedName)).ToList();
        for (var i = 0; i < deletes.Count; i++)
        {
            Assert.DoesNotContain(deletes.Skip(i + 1), later => later.IsDescendantOf(deletes[i]));
        }
    }

    [Fact]
    public async Task DeleteTree_ChangedMeanwhile_EndsWithTheTreeGone()
    {
        var lab = new FakeDirectoryBuilder(timeProvider: new TestClock(Now));
        var run = lab.AddOu(Prefix + "20261019-080000-c0ffee");
        var sub = lab.AddOu("Konta", run);
        var gone = lab.AddContact("Bożena", "Wcześniejsza", sub);
        lab.AddContact("Cezary", "Zostający", sub);
        var session = lab.Build();

        // Another cleanup deletes one contact first; a test that is still finishing adds one while the OU is deleted.
        session.FailNext(FakeOperationKind.Delete, gone, DirectoryErrorKind.NoSuchObject);
        var added = false;
        session.BeforeOperation = op =>
        {
            if (op.Kind == FakeOperationKind.Delete && DistinguishedName.AreEqual(op.DistinguishedName, sub) && !added)
            {
                added = true;
                lab.AddContact("Daria", "Spóźniona", sub);
            }
        };

        await LabCleanup.DeleteTreeAsync(session, run, Ct);

        Assert.True(added);
        Assert.Null(await session.GetAsync(run, [], Ct));
    }

    [Fact]
    public async Task DeleteTree_OfAMissingTree_DeletesNothing()
    {
        var session = new FakeDirectoryBuilder(timeProvider: new TestClock(Now)).Build();
        var missing = $"OU={Prefix}20261019-080000-000000,DC=contoso,DC=test";

        Assert.Equal(0, await LabCleanup.DeleteTreeAsync(session, missing, Ct));
        Assert.DoesNotContain(session.Operations, o => o.Kind is FakeOperationKind.Delete or FakeOperationKind.Modify);
    }

    [Fact]
    public async Task StaleRuns_DeadOwnersAndOldOnes_LiveRunsAndOtherOusStay()
    {
        var clock = new TestClock(Now.AddHours(-3));
        var lab = new FakeDirectoryBuilder(timeProvider: clock);
        var self = new LabRunOwner("lab-host:4026531836", 4242, Now.AddMinutes(-1));
        var dead = self with { ProcessId = 1111, StartedAt = Now.AddMinutes(-20) };
        var alive = self with { ProcessId = 2222, StartedAt = Now.AddMinutes(-15) };
        var elsewhere = new LabRunOwner("other-host", 3333, Now.AddMinutes(-10));

        var legacyOld = lab.AddOu("AdMngIntegration", attributes: Describe("AD_MNG integration tests - safe to delete"));
        var oldElsewhere = lab.AddOu(Prefix + "20261019-055900-aaaaaa", attributes: Describe(Purpose + " " + elsewhere.Format()));
        lab.AddOu("Firma");
        clock.SetUtcNow(Now.AddMinutes(-20));
        var deadRun = lab.AddOu(Prefix + "20261019-084000-bbbbbb", attributes: Describe(Purpose + " " + dead.Format()));
        var liveRun = lab.AddOu(Prefix + "20261019-084500-cccccc", attributes: Describe(Purpose + " " + alive.Format()));
        var youngElsewhere = lab.AddOu(Prefix + "20261019-085000-dddddd", attributes: Describe(Purpose + " " + elsewhere.Format()));
        lab.AddOu("AdMngIntegrationArchiwum");
        clock.SetUtcNow(Now);
        var session = lab.Build();
        var runs = new LabRuns("DC=contoso,DC=test", Prefix, Purpose, StaleAfter, "AdMngIntegration")
        {
            Owner = self,
            IsAlive = owner => owner == alive,
            Time = clock,
        };

        var stale = await runs.FindStaleAsync(session, Ct);

        Assert.Equal(new[] { legacyOld, oldElsewhere, deadRun }.Order(StringComparer.OrdinalIgnoreCase), stale.Order(StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(liveRun, stale);
        Assert.DoesNotContain(youngElsewhere, stale);
    }

    [Fact]
    public async Task RemoveStale_ALargeDeadRunIsRemoved_AFailingOneIsReportedNotThrown()
    {
        var clock = new TestClock(Now.AddMinutes(-30));
        var lab = new FakeDirectoryBuilder(timeProvider: clock);
        var self = new LabRunOwner("lab-host", 4242, Now);
        var big = lab.AddOu(Prefix + "20261019-083000-eeeeee", attributes: Describe(Purpose + " " + (self with { ProcessId = 1 }).Format()));
        var contacts = Enumerable.Range(0, 400).Select(i => lab.AddContact("Kontakt", $"{i:D4} Ślimak", big)).ToArray();
        lab.AddGroup("GG_Wielka", big, members: contacts);
        var stuck = lab.AddOu(Prefix + "20261019-083100-ffffff", attributes: Describe(Purpose + " " + (self with { ProcessId = 2 }).Format()));
        var stuckChild = lab.AddContact("Edward", "Zablokowany", stuck);
        clock.SetUtcNow(Now);
        var session = lab.Build();
        session.BeforeOperation = op =>
        {
            if (op is { Kind: FakeOperationKind.Delete, Subtree: true })
            {
                throw TimeLimitExceeded(op.DistinguishedName);
            }
        };
        session.FailNext(FakeOperationKind.Delete, stuckChild, DirectoryErrorKind.Busy, times: 100);
        var runs = new LabRuns("DC=contoso,DC=test", Prefix, Purpose, StaleAfter) { Owner = self, IsAlive = _ => false, Time = clock };

        var failures = await runs.RemoveStaleAsync(session, Ct);

        Assert.Null(await session.GetAsync(big, [], Ct));
        var failure = Assert.Single(failures);
        Assert.Equal(stuck, failure.Dn);
        Assert.Contains("busy", failure.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateRunOu_CarriesTheOwner_AndExplainsABusyLab()
    {
        var lab = new FakeDirectoryBuilder(timeProvider: new TestClock(Now));
        var session = lab.Build();
        var self = new LabRunOwner("lab-host:4026531836", 4242, Now);
        var runs = new LabRuns("DC=contoso,DC=test", Prefix, Purpose, StaleAfter) { Owner = self, Server = "127.0.0.1" };
        var name = runs.NewRunName();
        Assert.Matches(@"^AdMngIntegration-\d{8}-\d{6}-[0-9a-f]{6}$", name);

        var dn = await runs.CreateRunOuAsync(session, name, Ct);
        var created = await session.GetAsync(dn, [Attr.Description], Ct);
        Assert.Equal(self, LabRunOwner.Find(created?.GetString(Attr.Description)));

        var second = DistinguishedName.Parse("DC=contoso,DC=test").Child("OU", runs.NewRunName()).ToString();
        session.FailNext(FakeOperationKind.Add, second, DirectoryErrorKind.Timeout);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runs.CreateRunOuAsync(session, DistinguishedName.Parse(second).Name, Ct));
        Assert.Contains("127.0.0.1", error.Message, StringComparison.Ordinal);
        Assert.Contains("busy", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<DirectoryException>(error.InnerException);
    }

    [Fact]
    public void Explain_NamesTheRemedy_ForAMissingLab()
    {
        var runs = new LabRuns("DC=contoso,DC=test", Prefix, Purpose, StaleAfter) { Server = "127.0.0.1" };

        var missing = runs.Explain(new DirectoryException(DirectoryErrorKind.ServerUnavailable, "Connect failed."), "connecting");
        Assert.Contains("samba -D", missing.Message, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1", missing.Message, StringComparison.Ordinal);

        var busy = runs.Explain(new DirectoryException(DirectoryErrorKind.Busy, "Busy."), "creating the run OU");
        Assert.Contains("busy", busy.Message, StringComparison.OrdinalIgnoreCase);

        var other = new DirectoryException(DirectoryErrorKind.AccessDenied, "Denied.");
        Assert.Same(other, runs.Explain(other, "creating the run OU"));
    }

    [Fact]
    public void Owner_RoundTripsThroughTheDescription_OnlyARunningProcessIsAlive()
    {
        var current = LabRunOwner.Current;
        Assert.Equal(current, LabRunOwner.Find("AD_MNG integration tests – safe to delete " + current.Format()));
        Assert.Null(LabRunOwner.Find("AD_MNG integration tests - safe to delete"));
        Assert.Null(LabRunOwner.Find(null));
        Assert.True(LabRunOwner.IsRunning(current));

        // No such process; a reused process id (same number, another start time); another host or process namespace
        // (unknown here – the age decides).
        Assert.False(LabRunOwner.IsRunning(current with { ProcessId = int.MaxValue }));
        Assert.False(LabRunOwner.IsRunning(current with { StartedAt = current.StartedAt.AddMinutes(-5) }));
        Assert.False(LabRunOwner.IsRunning(current with { Host = "inny-host" }));
    }

    private static Dictionary<string, object?> Describe(string description) => new() { [Attr.Description] = description };

    private static DirectoryException TimeLimitExceeded(string dn) =>
        new(DirectoryErrorKind.Timeout, "Delete failed: TimeLimitExceeded (LDAP 3).") { LdapResultCode = 3, DistinguishedName = dn };
}
