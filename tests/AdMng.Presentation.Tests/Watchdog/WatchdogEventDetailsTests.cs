using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Testing.Client;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>
/// Event details: before → after, who and from where, lockout sources, related events and deliveries, acknowledge,
/// "Pokaż w AD Manager" by objectGUID (moved and deleted objects), unlock through the change pipeline, copy.
/// </summary>
public sealed class WatchdogEventDetailsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lockout_ShowsTheAttributionAndTheSourcesOfTheBadPasswords()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var details = await ShowAsync(h, SampleEvents.LockoutId);
            var localizer = h.Ad.Localizer;

            Assert.True(details.HasEvent);
            Assert.Null(details.Error);
            Assert.Equal(localizer.KindName(WatchEventKind.AccountLockedOut), details.Title);
            Assert.Equal("Żaneta Szymańska", details.ObjectName);
            Assert.Equal(localizer.WatchObjectKindName(WatchObjectKind.User), details.ObjectKindText);
            Assert.Equal("contoso.test/Firma/Pracownicy/Sprzedaż/Żaneta Szymańska", details.ObjectPath);
            Assert.Equal(SamplePrincipals.ZanetaSzymanska.Dn, details.ObjectDn);
            Assert.Equal(SamplePrincipals.ZanetaSzymanska.ObjectGuid!.Value.ToString("D"), details.ObjectGuidText);
            Assert.Equal("dc01.contoso.test · 10/13/2026 12:29:48", details.OriginText);
            Assert.Equal("lockouts", details.RulesText);

            Assert.Equal(localizer.AttributionStatusName(AttributionStatus.Resolved), details.AttributionStatusText);
            Assert.Equal("10.20.4.17 · WRO-LT014 (" + localizer.SourceOriginText("4740") + ")", details.SourceText);
            Assert.Equal(localizer.ConfidenceName(AttributionConfidence.Low), details.SourceConfidenceText);
            Assert.Equal(localizer.ConfidenceName(AttributionConfidence.Medium), details.ConfidenceText);
            var evidence = Assert.Single(details.Evidence);
            Assert.Equal("dc01.contoso.test", evidence.Dc);
            Assert.Equal(4740, evidence.EventCode);
            Assert.Equal("1884201", evidence.RecordText);

            Assert.True(details.HasLockout);
            Assert.Equal(
                localizer.Format(L.Watchdog.DetailsLockout, "10/13/2026 12:30:00", "10/13/2026 13:00:00", 5, "WRO-LT014"),
                details.LockoutText);
            Assert.Equal(2, details.LockoutSources.Count);
            Assert.Equal(("10.20.4.17", "WRO-LT014", 4, 4771), (details.LockoutSources[0].Address, details.LockoutSources[0].Workstation, details.LockoutSources[0].Count, details.LockoutSources[0].EventCode));
            Assert.Equal(string.Empty, details.LockoutSources[1].Workstation);

            var delivery = Assert.Single(details.Deliveries);
            Assert.Equal(localizer.ChannelTypeName(DeliveryChannelType.Email), delivery.ChannelText);
            Assert.Equal(localizer.OutboxStatusName(OutboxStatus.Dead), delivery.StatusText);
            Assert.Equal("smtp.550", delivery.ErrorCode);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task Changes_UseTheSchemaDisplayNames_HideSecrets_AndListFlags()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var localizer = h.Ad.Localizer;
            var details = await ShowAsync(h, SampleEvents.AttributesChangedId);
            var department = details.Changes.Single(c => c.LdapName == "department");
            Assert.Equal(localizer.AttributeName("department", h.Ad.Session.Schema), department.Attribute);
            Assert.NotEqual("department", department.Attribute);
            Assert.Equal("Księgowość", department.OldText);
            Assert.Equal("Finanse i księgowość", department.NewText);
            Assert.Equal(string.Empty, department.ChangeText);

            details = await ShowAsync(h, 7);
            var password = details.Changes.Single(c => c.LdapName == "unicodePwd");
            Assert.True(password.IsRedacted);
            Assert.Equal(localizer[L.Watchdog.ValueHidden], password.OldText);
            Assert.Equal(localizer[L.Watchdog.ValueHidden], password.NewText);

            details = await ShowAsync(h, 10);
            Assert.Equal("+DONT_EXPIRE_PASSWORD", Assert.Single(details.Changes).ChangeText);

            details = await ShowAsync(h, SampleEvents.PendingAttributionId);
            var key = Assert.Single(details.Changes);
            Assert.True(key.IsTruncated);
            Assert.Equal(localizer[L.Watchdog.ValueEmpty], key.OldText);
            Assert.EndsWith(localizer[L.Watchdog.ValueTruncated], key.NewText, StringComparison.Ordinal);
            Assert.Equal(localizer[L.Watchdog.ActorPending], details.ActorText);

            // The watchdog's own configuration changes: paths, not attributes.
            details = await ShowAsync(h, 28);
            Assert.Equal(["rules[lockouts].exclusions", "channels[teams-it].url"], details.Changes.Select(c => c.Attribute));
            Assert.Equal("Watchdog", details.ObjectName);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task UnresolvedAndAmbiguous_ShowTheReasonRemedyAndCandidates()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var localizer = h.Ad.Localizer;
            var details = await ShowAsync(h, 25);
            Assert.Equal(localizer.AttributionStatusName(AttributionStatus.Unknown), details.AttributionStatusText);
            Assert.Equal(localizer.ReasonText(AttributionReasons.DcUnreachable), details.ReasonText);
            Assert.Equal(localizer.RemedyText(AttributionReasons.DcUnreachable), details.RemedyText);
            Assert.False(string.IsNullOrEmpty(details.RemedyText));
            Assert.Equal(localizer.Format(L.Watchdog.ActorUnknown, details.ReasonText), details.ActorText);
            Assert.Equal(localizer.TagName(EventTags.Server), details.TagsText);

            details = await ShowAsync(h, SampleEvents.AmbiguousId);
            Assert.Equal(
                [@"Jan Kowalski (CONTOSO\jan.kowalski, S-1-5-21-1004336348-1177238915-682003330-1105)", @"Jan Kowalski (konto administracyjne) (CONTOSO\adm.kowalski, S-1-5-21-1004336348-1177238915-682003330-1106)"],
                details.Candidates);
            Assert.Equal(2, details.Evidence.Count);

            details = await ShowAsync(h, SampleEvents.PrivilegeGrantedId);
            Assert.Equal(@"Jan Kowalski (CONTOSO\jan.kowalski, S-1-5-21-1004336348-1177238915-682003330-1105)", details.ActorText);
            Assert.Equal("Domain Admins", details.MemberText);
            Assert.Equal("Łukasz Wiśniewski → GG_Administratorzy_Serwerów → Domain Admins", details.ViaText);

            details = await ShowAsync(h, 12);
            Assert.Equal(
                localizer.Format(L.Watchdog.DetailsExpiry, localizer[WatchdogDisplay.ExpiryKindKey(ExpiryKind.Account)], "10/20/2026", 7),
                details.ExpiryText);
            Assert.Equal(string.Empty, details.ConfidenceText);

            details = await ShowAsync(h, 14);
            Assert.StartsWith(localizer.Format(L.Watchdog.DetailsBurst, 7, 10, string.Empty).TrimEnd(), details.BurstText, StringComparison.Ordinal);
            Assert.Contains("Żaneta Szymańska", details.BurstText, StringComparison.Ordinal);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task RelatedEvents_OfTheSameChangeSet()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var details = await ShowAsync(h, 1);
            var related = Assert.Single(details.RelatedEvents);
            Assert.Equal(2, related.Id);
            Assert.Equal(h.Ad.Localizer.KindName(WatchEventKind.AccountEnabled), related.KindText);
        });
    }

    [Fact]
    public async Task Acknowledge_WithAComment_UpdatesTheDetailsAndTheRow()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            var row = tab.Events.Items.Single(r => r.Id == SampleEvents.PrivilegeGrantedId);
            tab.Events.SelectedItem = row;
            await tab.Events.Details.Loading;
            var details = tab.Events.Details;
            Assert.True(details.AcknowledgeCommand.CanExecute(null));
            Assert.Equal(string.Empty, details.AcknowledgedText);

            details.AcknowledgeComment = "Zgłoszenie INC-4711, zmiana zatwierdzona.";
            await details.AcknowledgeCommand.ExecuteAsync(null);

            var (id, request) = Assert.Single(h.Client.Acknowledgements);
            Assert.Equal(SampleEvents.PrivilegeGrantedId, id);
            Assert.Equal("Zgłoszenie INC-4711, zmiana zatwierdzona.", request.Comment);
            Assert.Equal(string.Empty, details.AcknowledgeComment);
            Assert.False(details.AcknowledgeCommand.CanExecute(null));
            Assert.Equal(
                h.Ad.Localizer.Format(L.Watchdog.DetailsAcknowledgedBy, h.Ad.Localizer.FormatTime(SampleEvents.Now, TimeZoneInfo.Utc), @"CONTOSO\jan.kowalski") + ": Zgłoszenie INC-4711, zmiana zatwierdzona.",
                details.AcknowledgedText);
            Assert.True(row.IsAcknowledged);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task OpenInAdMng_FindsTheCurrentDnByObjectGuid_AfterAMove()
    {
        using var h = await WatchdogHarness.CreateAsync();
        var jan = await h.Ad.Session.GetAsync(h.Ad.Company.JanKowalski, [Attr.ObjectGuid], Ct);
        var stored = h.Client.AddEvent(SampleEvents.NewUser("Jan Kowalski", SampleEvents.Now) with
        {
            Object = new ObjectRef
            {
                ObjectGuid = jan!.ObjectGuid,
                Dn = "CN=Jan Kowalski,OU=Stary dział,OU=Firma,DC=contoso,DC=test",
                Name = "Jan Kowalski",
                Kind = WatchObjectKind.User,
            },
        });

        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            var located = new List<string>();
            tab.LocateRequested += (_, dn) => located.Add(dn);
            tab.Events.SelectedItem = tab.Events.Items.Single(r => r.Id == stored.Id);
            await tab.Events.Details.Loading;
            var details = tab.Events.Details;

            Assert.True(details.OpenInAdMngCommand.CanExecute(null));
            await details.OpenInAdMngCommand.ExecuteAsync(null);

            Assert.Equal([h.Ad.Company.JanKowalski], located);
            Assert.Null(details.ActionMessage);
            Assert.Contains(h.Ad.Session.Operations, o => o.Kind == Testing.FakeOperationKind.Search && o.Search!.Filter.Contains("objectGUID", StringComparison.OrdinalIgnoreCase));
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task OpenInAdMng_OfADeletedObject_OpensTheRecycleBinOnIt()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var details = await ShowAsync(h, SampleEvents.DeletedComputerId);
            await details.OpenInAdMngCommand.ExecuteAsync(null);

            var guid = SampleEvents.Create().Single(e => e.Id == SampleEvents.DeletedComputerId).Object.ObjectGuid!.Value;
            Assert.Contains($"recycleBin:{guid:D}", h.Ad.Dialogs.Shown);
            Assert.Equal(h.Ad.Localizer[L.Watchdog.DetailsObjectDeleted], details.ActionMessage);
        });
    }

    [Fact]
    public async Task OpenInAdMng_NeedsASessionToTheSameDomain()
    {
        using var h = await WatchdogHarness.CreateAsync(adSession: false);
        var foreign = h.Client.AddEvent(SampleEvents.NewUser("Obcy Użytkownik", SampleEvents.Now) with { Domain = "fabrikam.test" });
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            var details = tab.Events.Details;
            tab.Events.SelectedItem = tab.Events.Items.Single(r => r.Id == SampleEvents.LockoutId);
            await details.Loading;
            Assert.False(details.OpenInAdMngCommand.CanExecute(null));
            Assert.False(details.CanUnlock);

            await h.Ad.Sessions.ConnectAsync(new Core.Directory.ConnectionOptions { Domain = "contoso.test" }, null, Ct);
            await WatchdogTestWait.UntilAsync(h, () => details.OpenInAdMngCommand.CanExecute(null), "open after connecting");

            tab.Events.SelectedItem = tab.Events.Items.Single(r => r.Id == foreign.Id);
            await details.Loading;
            Assert.False(details.OpenInAdMngCommand.CanExecute(null));
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task Unlock_OfAStillLockedAccount_GoesThroughTheChangePipelineWithPreview()
    {
        using var h = await WatchdogHarness.CreateAsync();
        var zaneta = h.Ad.Company.ZanetaSzymanska;
        var obj = await h.Ad.Session.GetAsync(zaneta, [Attr.ObjectGuid], Ct);
        var lockout = h.Client.AddEvent(SampleEvents.Create().Single(e => e.Id == SampleEvents.LockoutId) with
        {
            Object = SamplePrincipals.ZanetaSzymanska with { ObjectGuid = obj!.ObjectGuid, Dn = zaneta },
        });
        var lockoutTime = h.Ad.Value(zaneta, Attr.LockoutTime);

        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            tab.Events.SelectedItem = tab.Events.Items.Single(r => r.Id == lockout.Id);
            await tab.Events.Details.Loading;
            var details = tab.Events.Details;
            Assert.True(details.CanUnlock);
            Assert.True(details.UnlockAccountCommand.CanExecute(null));

            await details.UnlockAccountCommand.ExecuteAsync(null);

            // The batch editor shows the plan (preview) – the view model never writes to the directory itself.
            var request = Assert.Single(h.Ad.Dialogs.BatchEditorRequests);
            Assert.Equal([zaneta], request.TargetDns);
            Assert.IsType<UnlockAccountOperation>(Assert.Single(request.Operations));
            Assert.True(request.OpenPreview);
            Assert.Equal(lockoutTime, h.Ad.Value(zaneta, Attr.LockoutTime));
            Assert.Null(details.ActionMessage);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task Unlock_IsNotOfferedForAccountsThatAreNoLongerLocked()
    {
        using var h = await WatchdogHarness.CreateAsync();
        var jan = await h.Ad.Session.GetAsync(h.Ad.Company.JanKowalski, [Attr.ObjectGuid], Ct);
        var lockout = h.Client.AddEvent(SampleEvents.Create().Single(e => e.Id == SampleEvents.LockoutId) with
        {
            Object = SamplePrincipals.ZanetaSzymanska with { ObjectGuid = jan!.ObjectGuid, Dn = h.Ad.Company.JanKowalski },
        });

        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            tab.Events.SelectedItem = tab.Events.Items.Single(r => r.Id == lockout.Id);
            await tab.Events.Details.Loading;
            Assert.False(tab.Events.Details.CanUnlock);
            Assert.False(tab.Events.Details.UnlockAccountCommand.CanExecute(null));
        });
    }

    [Fact]
    public async Task CopyDetails_PutsAReadableSummaryOnTheClipboard()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var details = await ShowAsync(h, SampleEvents.AttributesChangedId);
            details.CopyDetailsCommand.Execute(null);

            var text = h.Ad.Clipboard.Text!;
            Assert.False(h.Ad.Clipboard.IsSecret);
            Assert.StartsWith(details.Title + " (", text, StringComparison.Ordinal);
            Assert.Contains("Małgorzata Wójcik", text, StringComparison.Ordinal);
            Assert.Contains(SamplePrincipals.MalgorzataWojcik.Dn, text, StringComparison.Ordinal);
            Assert.Contains(@"CONTOSO\svc-hr-sync", text, StringComparison.Ordinal);
            Assert.Contains("Główna księgowa → Dyrektor finansowa", text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task LoadFailure_IsShownInTheDetails_AndClearingTheSelectionEmptiesThem()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            h.Client.FailNext(nameof(IWatchdogClient.GetEventAsync), new WatchdogClientException(WatchdogClientErrorKind.NotFound, "gone") { StatusCode = 404 });
            tab.Events.SelectedItem = tab.Events.Items[0];
            await tab.Events.Details.Loading;
            var details = tab.Events.Details;

            Assert.Equal(h.Ad.Localizer[L.Watchdog.ErrorNotFound], details.Error);
            Assert.True(details.HasEvent);
            Assert.Equal(tab.Events.Items[0].KindText, details.Title);

            tab.Events.SelectedItem = null;
            await details.Loading;
            Assert.False(details.HasEvent);
            Assert.Null(details.Error);
            Assert.Empty(details.Changes);
            Assert.Equal(string.Empty, details.Title);
        });
    }

    private static async Task<WatchdogEventDetailsViewModel> ShowAsync(WatchdogHarness h, long id)
    {
        var details = new WatchdogEventDetailsViewModel(h.Services);
        var watchEvent = h.Client.Events.Single(e => e.Id == id);
        await details.ShowAsync(new WatchdogEventRowViewModel(watchEvent, h.Ad.Localizer, TimeZoneInfo.Utc));
        return details;
    }
}
