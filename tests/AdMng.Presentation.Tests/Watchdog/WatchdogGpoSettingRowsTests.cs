using System.Globalization;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Testing.Client;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>
/// The Watchdog tab's GPO change rows in the operator's language (K6, design note 20.1): GPMC's path and name, readable
/// values, preference items named after their <c>@name</c> rows, and domain SIDs named through the connected session.
/// </summary>
public sealed class WatchdogGpoSettingRowsTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    private static readonly ObjectRef Policy = new()
    {
        ObjectGuid = Guid.Parse("4b6c1d2e-3f40-4a5b-8c6d-7e8f9a0b1c2d"),
        Dn = "CN={6AC1786C-016F-11D2-945F-00C04FB984F9},CN=Policies,CN=System," + SamplePrincipals.DomainDn,
        Name = "{6AC1786C-016F-11D2-945F-00C04FB984F9}",
        DisplayName = "Serwery – Zabezpieczenia",
        Kind = WatchObjectKind.GroupPolicy,
    };

    [Fact]
    public async Task GpoRows_NameDomainSidsThroughTheSession_AndItemsByTheirNames()
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        var it = h.Ad.Builder.SidOf(h.Ad.Company.GroupIt);
        const string Item = "Computer/Preferences/Groups/{9A8B7C6D-5E4F-4A3B-2C1D-0E9F8A7B6C5D}";
        var stored = h.Client.AddEvent(new WatchEvent
        {
            EventId = Guid.Parse("01929a3c-7b10-7cc0-9a51-6e0b2f4c8d20"),
            Kind = WatchEventKind.GroupPolicySettingsChanged,
            Severity = Severity.High,
            Object = Policy,
            DetectedAt = SampleEvents.Now,
            Source = DetectionSource.IncrementalPoll,
            Changes =
            [
                new AttributeChange
                {
                    Attribute = "Computer/Security/Privilege Rights/SeRemoteInteractiveLogonRight",
                    OldValues = ["S-1-5-32-544"],
                    NewValues = ["S-1-5-32-544", it],
                    Added = [it],
                },
                new AttributeChange { Attribute = "Computer/Security/System Access/LockoutBadCount", OldValues = ["5"], NewValues = ["0"] },
                new AttributeChange { Attribute = Item + "@name", NewValues = ["Administrators (built-in)"] },
                new AttributeChange
                {
                    Attribute = Item + "/Members",
                    NewValues = [$"Member action=ADD name=CONTOSO\\GG_IT sid={it}", "Member action=REMOVE name=CONTOSO\\Goście sid="],
                    Added = [$"Member action=ADD name=CONTOSO\\GG_IT sid={it}", "Member action=REMOVE name=CONTOSO\\Goście sid="],
                },
            ],
        });

        await h.Ui.RunAsync(async () =>
        {
            var details = new WatchdogEventDetailsViewModel(h.Services);
            await details.ShowAsync(new WatchdogEventRowViewModel(stored, h.Ad.Localizer, TimeZoneInfo.Utc));
            var rows = details.Changes;

            Assert.EndsWith("Przypisywanie praw użytkownika › Zezwalaj na logowanie za pomocą usług pulpitu zdalnego", rows[0].Attribute, StringComparison.Ordinal);
            Assert.Equal("Administratorzy", rows[0].OldText);
            Assert.Equal("Administratorzy, CONTOSO\\GG_IT", rows[0].NewText);
            Assert.Equal("+CONTOSO\\GG_IT", rows[0].ChangeText);
            Assert.Equal("Computer/Security/Privilege Rights/SeRemoteInteractiveLogonRight", rows[0].LdapName);
            Assert.EndsWith("Zasady blokady konta › Próg blokady konta", rows[1].Attribute, StringComparison.Ordinal);
            Assert.Equal(("5 nieudanych prób logowania", "Konto nie jest blokowane"), (rows[1].OldText, rows[1].NewText));
            Assert.EndsWith("Lokalni użytkownicy i grupy › Administrators (built-in) – Członkowie", rows[3].Attribute, StringComparison.Ordinal);
            Assert.Equal("+CONTOSO\\GG_IT, +CONTOSO\\Goście (Usuń)", rows[3].ChangeText);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public void WithoutASession_SidsStay_AndNothingThrows()
    {
        var localizer = new Presentation.Localization.ResxLocalizer(Polish);
        var row = WatchdogChangeRowViewModel.Create(
            new AttributeChange { Attribute = "Computer/Security/Privilege Rights/SeDebugPrivilege", NewValues = ["S-1-5-21-1-2-3-1500"], Added = ["S-1-5-21-1-2-3-1500"] },
            localizer, null, new WatchdogChangeContext(UseSchema: false, GpoSettings: true));

        Assert.Equal("S-1-5-21-1-2-3-1500", row.NewText);
        Assert.Equal("(puste)", row.OldText);
        Assert.Equal("+S-1-5-21-1-2-3-1500", row.ChangeText);
    }
}
