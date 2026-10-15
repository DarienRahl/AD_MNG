using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.IntegrationTests.Infrastructure;

namespace AdMng.Watchdog.IntegrationTests.Detection;

/// <summary>
/// Account changes made as Administrator, detected by a read-only engine (spec 22.4): creation with initial values,
/// attribute changes old → new, disable, password age reset, computers, moves and OU renames.
/// </summary>
public sealed class AccountChangeTests(ReadOnlyEngineFixture engine, LabFixture lab) : IClassFixture<ReadOnlyEngineFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private EngineHost Host => engine.Host;

    [Fact]
    public async Task NewUser_IsOneUserCreatedWithItsInitialValues()
    {
        WatchdogLab.RequireLab();
        var ou = await lab.CreateTestOuAsync("Nowe konta", Ct);
        var mark = await Host.MarkAsync(Ct);

        // Added disabled, password set, enabled – before the cycle, so one event with the final state.
        var zofia = await lab.Writer.CreateUserAsync(ou, "Zofia", "Żak", TestNames.Password(), new Dictionary<string, string>
        {
            [Attr.Department] = "Księgowość",
            [Attr.Title] = "Główna księgowa",
        }, Ct);
        await Host.RunDetectionAsync(Ct);

        var events = await Host.EventsAsync(ou, mark, Ct);
        var created = EventAssert.Single(events, WatchEventKind.UserCreated, zofia.Dn);
        Assert.Equal(WatchObjectKind.User, created.Object.Kind);
        Assert.Equal(zofia.SamAccountName, created.Object.SamAccountName);
        Assert.NotNull(created.Object.ObjectGuid);
        Assert.Equal(DetectionSource.IncrementalPoll, created.Source);
        Assert.Equal(["Księgowość"], EventAssert.Change(created, Attr.Department).NewValues);
        Assert.Equal(["Główna księgowa"], EventAssert.Change(created, Attr.Title).NewValues);
        Assert.DoesNotContain(EventAssert.Of(events, zofia.Dn), e => e.Kind is WatchEventKind.AccountEnabled or WatchEventKind.PasswordChanged);
        EventAssert.Single(events, WatchEventKind.OrganizationalUnitCreated, ou);
    }

    [Fact]
    public async Task DepartmentAndTitle_AreAttributesChangedOldToNew()
    {
        WatchdogLab.RequireLab();
        var ou = await lab.CreateTestOuAsync("Zmiany", Ct);
        var lukasz = await lab.Writer.CreateUserAsync(ou, "Łukasz", "Wiśniewski", TestNames.Password(), new Dictionary<string, string>
        {
            [Attr.Department] = "Sprzedaż",
            [Attr.Title] = "Handlowiec",
        }, Ct);
        await Host.RunDetectionAsync(Ct);
        var mark = await Host.MarkAsync(Ct);

        await lab.Writer.ReplaceAsync(lukasz.Dn, Attr.Department, "Księgowość", Ct);
        await lab.Writer.ReplaceAsync(lukasz.Dn, Attr.Title, "Księgowy", Ct);
        await Host.RunDetectionAsync(Ct);

        var changed = EventAssert.Single(await Host.EventsAsync(ou, mark, Ct), WatchEventKind.AttributesChanged, lukasz.Dn);
        var department = EventAssert.Change(changed, Attr.Department);
        Assert.Equal(["Sprzedaż"], department.OldValues);
        Assert.Equal(["Księgowość"], department.NewValues);
        var title = EventAssert.Change(changed, Attr.Title);
        Assert.Equal(["Handlowiec"], title.OldValues);
        Assert.Equal(["Księgowy"], title.NewValues);
    }

    [Fact]
    public async Task DisabledAccount_IsAccountDisabled()
    {
        WatchdogLab.RequireLab();
        var ou = await lab.CreateTestOuAsync("Wyłączenia", Ct);
        var user = await lab.Writer.CreateUserAsync(ou, "Bartłomiej", "Kozłowski", TestNames.Password(), cancellationToken: Ct);
        await Host.RunDetectionAsync(Ct);
        var mark = await Host.MarkAsync(Ct);

        await lab.Writer.SetAccountControlAsync(user.Dn, UserAccountControl.NormalAccount | UserAccountControl.AccountDisable, Ct);
        await Host.RunDetectionAsync(Ct);

        var disabled = EventAssert.Single(await Host.EventsAsync(ou, mark, Ct), WatchEventKind.AccountDisabled, user.Dn);
        Assert.Contains("ACCOUNTDISABLE", EventAssert.Change(disabled, Attr.UserAccountControl).FlagsAdded);
    }

    [Fact]
    public async Task PwdLastSetZeroThenMinusOne_IsPasswordAgeReset()
    {
        WatchdogLab.RequireLab();
        var ou = await lab.CreateTestOuAsync("Hasła", Ct);
        var user = await lab.Writer.CreateUserAsync(ou, "Grażyna", "Dąbrowska", TestNames.Password(), cancellationToken: Ct);
        await Host.RunDetectionAsync(Ct);
        var mark = await Host.MarkAsync(Ct);

        // unicodePwd is not written: the replication metadata shows pwdLastSet only.
        await lab.Writer.ReplaceAsync(user.Dn, Attr.PwdLastSet, "0", Ct);
        await lab.Writer.ReplaceAsync(user.Dn, Attr.PwdLastSet, "-1", Ct);
        await Host.RunDetectionAsync(Ct);

        var events = await Host.EventsAsync(ou, mark, Ct);
        EventAssert.Single(events, WatchEventKind.PasswordAgeReset, user.Dn);
        Assert.DoesNotContain(EventAssert.Of(events, user.Dn), e => e.Kind == WatchEventKind.PasswordChanged);
    }

    [Fact]
    public async Task NewComputer_IsComputerCreated()
    {
        WatchdogLab.RequireLab();
        var ou = await lab.CreateTestOuAsync("Komputery", Ct);
        var mark = await Host.MarkAsync(Ct);

        var name = "WDIT-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var computer = await lab.Writer.CreateComputerAsync(ou, name, Ct);
        await Host.RunDetectionAsync(Ct);

        var created = EventAssert.Single(await Host.EventsAsync(ou, mark, Ct), WatchEventKind.ComputerCreated, computer);
        Assert.Equal(WatchObjectKind.Computer, created.Object.Kind);
        Assert.Equal(name + "$", created.Object.SamAccountName, ignoreCase: true);
    }

    [Fact]
    public async Task MovedUser_IsObjectMovedWithThePreviousDn()
    {
        WatchdogLab.RequireLab();
        var ou = await lab.CreateTestOuAsync("Przeniesienia", Ct);
        var from = await lab.Writer.CreateOuAsync(ou, "Kraków", null, Ct);
        var to = await lab.Writer.CreateOuAsync(ou, "Gdańsk", null, Ct);
        var user = await lab.Writer.CreateUserAsync(from, "Paweł", "Lewandowski", TestNames.Password(), cancellationToken: Ct);
        await Host.RunDetectionAsync(Ct);
        var mark = await Host.MarkAsync(Ct);

        var moved = await lab.Writer.MoveAsync(user.Dn, to, null, Ct);
        await Host.RunDetectionAsync(Ct);

        var events = await Host.EventsAsync(ou, mark, Ct);
        var move = EventAssert.Single(events, WatchEventKind.ObjectMoved, moved);
        Assert.True(DistinguishedName.AreEqual(user.Dn, move.PreviousDn), $"previous DN {move.PreviousDn}");
        Assert.Single(events);
    }

    [Fact]
    public async Task RenamedOu_IsOneObjectRenamed_ChildrenAreNotReported()
    {
        WatchdogLab.RequireLab();
        var ou = await lab.CreateTestOuAsync("Zmiana nazwy", Ct);
        var team = await lab.Writer.CreateOuAsync(ou, "Zespół Północ", null, Ct);
        var users = new List<LabUser>();
        foreach (var (given, surname) in new[] { ("Agnieszka", "Wójcik"), ("Michał", "Kamiński"), ("Ewa", "Szymańska") })
        {
            users.Add(await lab.Writer.CreateUserAsync(team, given, surname, TestNames.Password(), cancellationToken: Ct));
        }

        var nested = await lab.Writer.CreateOuAsync(team, "Podzespół", null, Ct);
        await Host.RunDetectionAsync(Ct);
        var mark = await Host.MarkAsync(Ct);

        var renamed = await lab.Writer.MoveAsync(team, null, "OU=Zespół Południe", Ct);
        await Host.RunDetectionAsync(Ct);

        var events = await Host.EventsAsync(ou, mark, Ct);
        var rename = EventAssert.Single(events, WatchEventKind.ObjectRenamed, renamed);
        Assert.True(DistinguishedName.AreEqual(team, rename.PreviousDn), $"previous DN {rename.PreviousDn}");
        Assert.True(events.Count == 1, $"only the OU rename is reported: {EventAssert.Describe(events)}");

        // …while the snapshots of the children follow the new DN (no "moved" events on the next cycle either).
        foreach (var child in users.Select(u => u.Dn).Append(nested))
        {
            var rdn = DistinguishedName.Parse(child).Leaf;
            var newDn = DistinguishedName.Parse(renamed).Child(rdn.Type, rdn.Value).ToString();
            Assert.NotNull(await Host.Snapshots.FindByDnAsync(newDn, Ct));
        }

        await Host.RunDetectionAsync(Ct);
        Assert.Single(await Host.EventsAsync(ou, mark, Ct));
    }
}
