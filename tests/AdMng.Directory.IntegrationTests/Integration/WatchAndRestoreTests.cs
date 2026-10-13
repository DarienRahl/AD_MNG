using System.Collections.Concurrent;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Directory.IntegrationTests.Infrastructure;

namespace AdMng.Directory.IntegrationTests.Integration;

public sealed class WatchAndRestoreTests(LabFixture lab)
{
    [Fact]
    public async Task Watch_ReportsChangesBelowTheContainer()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var ou = await lab.CreateOuAsync(TestNames.Unique("Obserwacja"));
        var changes = new ConcurrentQueue<string>();
        var signal = new SemaphoreSlim(0);

        var watch = await lab.Admin.WatchAsync(ou, dn => { changes.Enqueue(dn); signal.Release(); }, ct);
        if (watch is null)
        {
            Assert.False(lab.Admin.Info.Supports(LdapControls.Notification));
            Assert.Skip("The server does not support change notifications.");
        }

        string contact;
        await using (watch)
        {
            contact = await lab.CreateContactAsync(ou, "Nowy Kontakt Ąę");
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!changes.Any(dn => DistinguishedName.AreEqual(dn, contact)) && DateTime.UtcNow < deadline)
            {
                await signal.WaitAsync(TimeSpan.FromMilliseconds(500), ct);
            }

            if (!changes.Any(dn => DistinguishedName.AreEqual(dn, contact)))
            {
                Assert.Skip("The server accepted the notification request but delivered no change within 15 s.");
            }
        }

        // Disposed: further changes are not reported.
        var countAfterDispose = changes.Count;
        await lab.Admin.ModifyAsync(contact, [AttributeModification.Replace(Attr.Description, "po zakończeniu")], null, ct);
        await Task.Delay(1000, ct);
        Assert.Equal(countAfterDispose, changes.Count);
    }

    [Fact]
    public async Task Watch_CallbackFailures_DoNotStopNotifications()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var ou = await lab.CreateOuAsync(TestNames.Unique("Wyjatki"));
        var calls = 0;
        await using var watch = await lab.Admin.WatchAsync(
            ou,
            _ =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("subscriber bug");
            },
            ct);
        Assert.SkipWhen(watch is null, "The server does not support change notifications.");

        // First change: the callback throws.
        await lab.CreateContactAsync(ou, "Pierwszy");
        await WaitForCallsAsync(1);
        Assert.SkipWhen(Volatile.Read(ref calls) == 0, "The server delivered no change notifications.");

        // A change made only after the failing callback must still be reported (two changes made back to back may be
        // delivered as one notification, so they are made one after another).
        var before = Volatile.Read(ref calls);
        await lab.CreateContactAsync(ou, "Drugi");
        await WaitForCallsAsync(before + 1);
        Assert.True(Volatile.Read(ref calls) > before, "No notification after a callback that threw.");

        async Task WaitForCallsAsync(int expected)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (Volatile.Read(ref calls) < expected && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200, ct);
            }
        }
    }

    [Fact]
    public async Task Watch_DisposingAQuietSubscription_IsPrompt_AndOthersKeepWorking()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var quietOu = await lab.CreateOuAsync(TestNames.Unique("Cisza"));
        var busyOu = await lab.CreateOuAsync(TestNames.Unique("Ruch"));
        var quiet = await lab.Admin.WatchAsync(quietOu, _ => { }, ct);
        Assert.SkipWhen(quiet is null, "The server does not support change notifications.");

        // Nothing changes below the quiet OU: S.DS.P's shared results thread ends up polling its connection.
        await Task.Delay(1000, ct);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await quiet.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20), ct);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Dispose took {stopwatch.Elapsed}.");

        // The results thread was not left stuck on the disposed subscription.
        var changes = new ConcurrentQueue<string>();
        await using var busy = await lab.Admin.WatchAsync(busyOu, changes.Enqueue, ct);
        var contact = await lab.CreateContactAsync(busyOu, "Po Ciszy");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!changes.Any(dn => DistinguishedName.AreEqual(dn, contact)) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, ct);
        }

        Assert.Contains(changes, dn => DistinguishedName.AreEqual(dn, contact));
    }

    [Fact]
    public async Task RestoreDeleted_BringsATombstoneBack()
    {
        LabSettings.RequireLab();
        var ct = TestContext.Current.CancellationToken;
        var ou = await lab.CreateOuAsync(TestNames.Unique("Kosz"));
        var dn = await lab.CreateUserAsync(ou, "Usunięty Przypadkiem");
        var guid = (await lab.Admin.GetAsync(dn, [Attr.ObjectGuid], ct))!.ObjectGuid!.Value;
        await lab.Admin.DeleteAsync(dn, subtree: false, ct);

        var deleted = new DirectorySearch
        {
            BaseDn = $"CN=Deleted Objects,{LabSettings.DomainDn}",
            Scope = SearchScope.OneLevel,
            Filter = LdapFilter.And(LdapFilter.EqualGuid(Attr.ObjectGuid, guid), LdapFilter.Equal(Attr.IsDeleted, "TRUE")),
            Attributes = [Attr.IsDeleted, Attr.LastKnownParent, Attr.MsDsLastKnownRdn, Attr.ObjectGuid],
            IncludeDeleted = true,
        };
        DirectoryObject? tombstone = null;
        await foreach (var entry in lab.Admin.SearchAsync(deleted, ct))
        {
            tombstone = entry;
        }

        Assert.NotNull(tombstone);
        Assert.Equal(true, tombstone.GetBoolean(Attr.IsDeleted));
        Assert.Equal(guid, tombstone.ObjectGuid);

        // Without the Show Deleted control the tombstone is invisible. AD hides the (deleted) Deleted Objects
        // container itself and answers noSuchObject; Samba returns no entries.
        var hidden = 0;
        try
        {
            await foreach (var _ in lab.Admin.SearchAsync(deleted with { IncludeDeleted = false }, ct))
            {
                hidden++;
            }
        }
        catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.NoSuchObject)
        {
            // AD behaviour - nothing was returned.
        }

        Assert.Equal(0, hidden);

        await lab.Admin.RestoreDeletedAsync(tombstone.DistinguishedName, dn, ct);
        var restored = await lab.Admin.GetAsync(dn, [Attr.ObjectGuid], ct);
        Assert.NotNull(restored);
        Assert.Equal(guid, restored.ObjectGuid);
    }
}
