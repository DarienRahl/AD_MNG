using AdMng.Core.ActiveDirectory;
using AdMng.Core.Computers;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Computers;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.AccountReview;
using AdMng.Testing;
using static AdMng.Presentation.Tests.Computers.ComputerScenario;

namespace AdMng.Presentation.Tests.Computers;

/// <summary>
/// The computer review window (K1.2) end to end: the real planner, executor and coordinator over the fake DC with the
/// computer inventory sample (27 October 2026).
/// </summary>
public class ComputerReviewViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>9:00 in the short time format of en-US (ICU puts a narrow no-break space before AM).</summary>
    private static string Nine => new DateTime(2026, 10, 27, 9, 0, 0).ToString("t", System.Globalization.CultureInfo.GetCultureInfo("en-US"));

    [Fact]
    public async Task Run_FindsTheComputersOfEveryReport()
    {
        var scenario = await CreateAsync();
        using var review = await scenario.RunAsync();

        Assert.Equal("Computer review – contoso.test", review.WindowTitle);
        Assert.Null(review.Error);
        Assert.False(review.IsRunning);
        Assert.Equal($"As of {Nine} · 23 computers read · 23 computers in the reports", review.StatusText);
        Assert.Equal(
            ["GDA-PC103", "GDA-PC104", "KRK-PC101", "KRK-PC102", "LOD-PC107", "POZ-PC106", "RODC-GDA01", "SRV-SQL03", "SRV-STARY04"],
            Names(review, ComputerReport.EndOfSupport));
        Assert.Equal(["LOD-PC107", "WAW-PC108"], Names(review, ComputerReport.Inactive));
        Assert.Equal(["PREST-PC201"], Names(review, ComputerReport.NeverLoggedOn));
        Assert.Equal(["SRV-APP02", "SRV-SQL03", "SRV-ZLY05"], Names(review, ComputerReport.ServersWithoutOwner));
        Assert.Equal(["NEW-PC301", "SRV-ZLY05"], Names(review, ComputerReport.UnexpectedContainer));
        Assert.Equal(["OLD-PC401"], Names(review, ComputerReport.Disabled));
        Assert.Equal(["PREST-PC201"], Names(review, ComputerReport.WithoutOperatingSystem));
        Assert.All(review.Tiles, t => Assert.True(t.IsComplete));
        Assert.Equal("9", review.Tiles.Single(t => t.Report == ComputerReport.EndOfSupport).CountText);
    }

    [Fact]
    public async Task Rows_ShowTheReleaseSupportOwnerAndPlacement()
    {
        var scenario = await CreateAsync();
        using var review = await scenario.RunAsync();

        // Windows 11 24H2 Pro ended on 13 October 2026; Enterprise 23H2 ends in 14 days; 22000 reported as "Windows 10" is 11 21H2.
        var pro = Row(review, ComputerReport.EndOfSupport, "KRK-PC101");
        Assert.Equal("Windows 11 24H2", pro[ComputerReviewColumns.Release]);
        Assert.Equal("ended 10/13/2026", pro[ComputerReviewColumns.Support]);
        Assert.Equal("ends 11/10/2026 (in 14 days)", Row(review, ComputerReport.EndOfSupport, "KRK-PC102")[ComputerReviewColumns.Support]);
        Assert.Equal("Windows 11 21H2", Row(review, ComputerReport.EndOfSupport, "POZ-PC106")[ComputerReviewColumns.Release]);
        Assert.Equal("Read-only domain controller", Row(review, ComputerReport.EndOfSupport, "RODC-GDA01")[ComputerReviewColumns.Type]);

        // The owner: missing, disabled; the creator of a prestaged account.
        Assert.Equal("no owner", Row(review, ComputerReport.ServersWithoutOwner, "SRV-APP02")[ComputerReviewColumns.Owner]);
        Assert.Equal("Zbigniew Kaźmierczak (owner's account disabled)", Row(review, ComputerReport.ServersWithoutOwner, "SRV-SQL03")[ComputerReviewColumns.Owner]);
        var prestaged = Row(review, ComputerReport.NeverLoggedOn, "PREST-PC201");
        Assert.Equal("CONTOSO\\lukasz.wisniewski", prestaged[ComputerReviewColumns.Creator]);
        Assert.Equal("Laptop dla nowej księgowej – Żaneta", prestaged[ComputerReviewColumns.Description]);

        // Placement: never moved out of CN=Computers; a member server in the DCs' OU.
        Assert.Equal("not moved after the join (default Computers container)", Row(review, ComputerReport.UnexpectedContainer, "NEW-PC301")[ComputerReviewColumns.Placement]);
        Assert.Equal("not a domain controller, but in OU=Domain Controllers", Row(review, ComputerReport.UnexpectedContainer, "SRV-ZLY05")[ComputerReviewColumns.Placement]);

        // The details pane of the selected computer.
        review.SelectedItem = Row(review, ComputerReport.ServersWithoutOwner, "SRV-SQL03");
        Assert.Contains(review.Details, d => d.Label == "Support" && d.Value == "ends 1/12/2027 (in 77 days)");
        Assert.Contains(review.Details, d => d.Label == "Location" && d.Value == "Łódź");
        Assert.Contains(review.Details, d => d.Label == "Reports" && d.Value.Contains("Servers without owner", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ServersWithoutOwner_AnOwnerInAnotherDomainOfTheForest_IsNotReportedAsDeleted()
    {
        var scenario = await CreateAsync();
        var session = scenario.Harness.Session;
        // A child domain (its DN ends with this domain's) and a deleted account of this domain.
        session.SetValues(scenario.Sample.AppServer, Attr.ManagedBy, "CN=Wiesława Żak,OU=Użytkownicy,DC=oddział,DC=contoso,DC=test");
        session.SetValues(scenario.Sample.MisplacedServer, Attr.ManagedBy, "CN=Józef Usunięty,OU=Użytkownicy,OU=Firma,DC=contoso,DC=test");
        using var review = await scenario.RunAsync();

        Assert.Equal(["SRV-SQL03", "SRV-ZLY05"], Names(review, ComputerReport.ServersWithoutOwner));
        Assert.Equal("Józef Usunięty (owner deleted)", Row(review, ComputerReport.ServersWithoutOwner, "SRV-ZLY05")[ComputerReviewColumns.Owner]);
    }

    [Fact]
    public async Task Releases_GetOneTilePerRelease_MostComputersFirst()
    {
        var scenario = await CreateAsync();
        using var review = await scenario.RunAsync();

        var releases = review.Tiles.Where(t => t.Report == ComputerReport.Releases).ToList();
        Assert.Equal("Windows 11 24H2", releases[0].Release);
        Assert.Equal(4, releases[0].Count);
        Assert.Equal("Windows 11 25H2", releases[1].Release);
        Assert.Contains(releases, t => t.Release == "Windows Server 2025" && t.Count == 2);
        Assert.Contains(releases, t => t.Release == "Ubuntu 24.04 LTS" && t.Count == 1);
        Assert.Equal(["KRK-LT001", "KRK-PC101", "WAW-PC001", "WAW-PC002"], Names(review, ComputerReport.Releases, "Windows 11 24H2"));

        // Enterprise of the same release is still supported (edition decides the date).
        var enterprise = Assert.Single(review.Items, r => r.Name == "WAW-PC001");
        Assert.Equal("supported until 10/12/2027", enterprise[ComputerReviewColumns.Support]);
        Assert.Equal(["LNX-SRV01"], Names(review, ComputerReport.Releases, "Ubuntu 24.04 LTS"));
        Assert.Equal("not Windows", review.Items[0][ComputerReviewColumns.Support]);
        Assert.Equal("Release catalogue as of 10/27/2026", review.CatalogText);
        Assert.False(review.IsCatalogStale);
    }

    [Fact]
    public async Task Parameters_ChangeTheReports_AndAreRemembered()
    {
        var scenario = await CreateAsync();
        var harness = scenario.Harness;
        using (var review = await scenario.RunAsync(r =>
        {
            r.EndingSoonDays = 30;
            r.InactiveDays = 160;
        }))
        {
            Assert.Equal(["GDA-PC103", "KRK-PC101", "KRK-PC102", "LOD-PC107", "POZ-PC106", "SRV-STARY04"], Names(review, ComputerReport.EndOfSupport));
            Assert.Equal(["LOD-PC107"], Names(review, ComputerReport.Inactive));
            Assert.Equal(["PREST-PC201"], Names(review, ComputerReport.NeverLoggedOn));
        }

        Assert.Equal(30, harness.Settings.Current.Computers.Parameters.EndingSoonDays);
        Assert.Equal(160, harness.Settings.Current.Computers.Parameters.InactiveDays);

        using var next = scenario.Factory.CreateReview();
        await next.InitializeAsync();
        Assert.Equal(30, next.EndingSoonDays);
        Assert.Equal(160, next.InactiveDays);
        next.InactiveDays = 250;
        await next.RunCommand.ExecuteAsync(null);
        Assert.Empty(Names(next, ComputerReport.NeverLoggedOn));
    }

    [Fact]
    public async Task ReportsSwitchedOff_AreNotShown_AndRememberedWithoutNames()
    {
        var scenario = await CreateAsync();
        var harness = scenario.Harness;
        using (var review = await scenario.RunAsync(r =>
        {
            r.ReportOptions.Single(o => o.Report == ComputerReport.Releases).IsEnabled = false;
            r.ReportOptions.Single(o => o.Report == ComputerReport.Disabled).IsEnabled = false;
        }))
        {
            Assert.DoesNotContain(review.Tiles, t => t.Report is ComputerReport.Releases or ComputerReport.Disabled);

            // SRV-APP02 is only a server without owner: kept until its owner was resolved.
            Assert.Equal(["SRV-APP02", "SRV-SQL03", "SRV-ZLY05"], Names(review, ComputerReport.ServersWithoutOwner));
            Assert.Equal($"As of {Nine} · 23 computers read · 14 computers in the reports", review.StatusText);
        }

        var settings = harness.Settings.Current.Computers;
        Assert.Equal([ComputerReport.Releases, ComputerReport.Disabled], settings.DisabledReports);
        using var next = scenario.Factory.CreateReview();
        Assert.False(next.ReportOptions.Single(o => o.Report == ComputerReport.Releases).IsEnabled);

        // Nothing switched on: no run.
        foreach (var option in next.ReportOptions)
        {
            option.IsEnabled = false;
        }

        Assert.False(next.RunCommand.CanExecute(null));
    }

    [Fact]
    public async Task Scope_OrganizationalUnit_ReadsOnlyItsSubtree_AndIsRememberedPortably()
    {
        var scenario = await CreateAsync();
        var harness = scenario.Harness;
        using var review = scenario.Factory.CreateReview();
        await review.InitializeAsync();
        review.ScopeKind = review.ScopeKinds.Single(k => k.Value == AccountReviewScopeKind.OrganizationalUnit);
        Assert.False(review.RunCommand.CanExecute(null));
        harness.Dialogs.PickedContainers.Enqueue(scenario.Sample.ServersOu);
        await review.BrowseScopeCommand.ExecuteAsync(null);
        Assert.Equal(scenario.Sample.ServersOu, review.ScopeDn);

        await review.RunCommand.ExecuteAsync(null);

        Assert.Equal(["SRV-APP02", "SRV-SQL03"], Names(review, ComputerReport.ServersWithoutOwner));
        Assert.Equal($"As of {Nine} · 5 computers read · 5 computers in the reports", review.StatusText);
        var scope = harness.Settings.Current.Computers.Scope;
        Assert.Equal(AccountReviewScopeKind.OrganizationalUnit, scope.Kind);
        Assert.Equal("OU=Serwery,OU=Firma,{domain}", scope.Dn);
    }

    [Fact]
    public async Task Scope_GroupMembers_ReadsTheComputersOfTheGroup()
    {
        var scenario = await CreateAsync();
        var harness = scenario.Harness;
        var group = harness.Builder.AddGroup("GG_Serwery_Krytyczne", scenario.Company.GroupsOu, members: [scenario.Sample.SqlServer, scenario.Sample.FileServer]);
        using var review = await scenario.RunAsync(r =>
        {
            r.ScopeKind = r.ScopeKinds.Single(k => k.Value == AccountReviewScopeKind.GroupMembers);
            r.ScopeDn = group;
        });

        Assert.Null(review.Error);
        Assert.Equal(["SRV-SQL03"], Names(review, ComputerReport.ServersWithoutOwner));
        Assert.Equal($"As of {Nine} · 2 computers read · 2 computers in the reports", review.StatusText);
    }

    [Fact]
    public async Task Stop_CancelsTheStream_AndKeepsWhatWasRead()
    {
        var scenario = await CreateOnDomainControllerAsync();
        var harness = scenario.Harness;
        var hooked = new HookedSession(new FakeDomainControllerSession(harness.Session, "dc01.contoso.test")) { HoldAfter = 3, HoldMarker = "805306369" };
        await harness.ReconnectAsync(hooked);
        using var review = scenario.Factory.CreateReview();
        await review.InitializeAsync();

        var run = review.RunCommand.ExecuteAsync(null);
        await hooked.Held.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(review.IsRunning);
        Assert.True(review.StopCommand.CanExecute(null));
        review.StopCommand.Execute(null);
        await run;

        Assert.False(review.IsRunning);
        Assert.Null(review.Error);
        Assert.StartsWith($"Stopped at {Nine} · 3 computers read · ", review.StatusText, StringComparison.Ordinal);
        Assert.All(review.Tiles, t => Assert.False(t.IsComplete));
        Assert.True(review.RunCommand.CanExecute(null));
    }

    [Fact]
    public async Task ExactLastLogon_ReadsEveryDomainController_AndDropsTheComputersThatAreActiveAfterAll()
    {
        var scenario = await CreateAsync();
        var harness = scenario.Harness;
        var now = ComputerInventorySample.Now;
        var opened = new List<HookedSession>();
        harness.ConnectToServer = (current, server, _) =>
        {
            var other = new HookedSession(new FakeDomainControllerSession(harness.Session, server));
            other.Extra[scenario.Sample.Windows7] = (Attr.LastLogon, AdConvert.ToFileTime(now.AddDays(-1)).ToString(System.Globalization.CultureInfo.InvariantCulture));
            opened.Add(other);
            return Task.FromResult<IDirectorySession>(other);
        };
        using var review = await scenario.RunAsync();
        review.SelectedTile = review.Tiles.Single(t => t.Report == ComputerReport.Inactive);

        Assert.True(review.ReadExactLastLogonCommand.CanExecute(null));
        await review.ReadExactLastLogonCommand.ExecuteAsync(null);

        Assert.Empty(harness.Dialogs.Errors);
        Assert.Equal(["WAW-PC108"], [.. review.Items.Select(r => r.Name)]);
        var (severity, message) = harness.Notifications.Items[^1];
        Assert.Equal(NotificationSeverity.Success, severity);
        Assert.Equal("2 of 2 domain controllers answered; 1 computers were active after all.", message);
        Assert.All(opened, s => Assert.True(s.IsDisposed));

        // LOD-PC107 stays in the end-of-support report, now with the exact value and its DC.
        var win7 = Row(review, ComputerReport.EndOfSupport, "LOD-PC107");
        Assert.Equal("dc02.contoso.test", win7.Facts.ExactLastLogonServer);
        Assert.StartsWith("10/26/2026", win7.Facts.ExactLastLogon is { } exact ? exact.ToString("d", System.Globalization.CultureInfo.GetCultureInfo("en-US")) : null, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExactLastLogon_AComputerDeletedOrMovedDuringTheRead_IsNotBroughtBack()
    {
        var scenario = await CreateAsync();
        var harness = scenario.Harness;
        var published = 0;
        harness.ConnectToServer = (current, server, _) =>
        {
            if (Interlocked.Exchange(ref published, 1) == 0)
            {
                // Another action of the window deletes one computer and moves the other while the DCs are read.
                harness.Changes.Publish(
                [
                    new DirectoryChange(DirectoryChangeKind.Deleted, scenario.Sample.Inactive, ObjectKind: ObjectKind.Computer),
                    new DirectoryChange(DirectoryChangeKind.Moved, scenario.Sample.Windows7, "CN=LOD-PC107,OU=Do utylizacji,OU=Firma,DC=contoso,DC=test", ObjectKind.Computer),
                ]);
            }

            return Task.FromResult<IDirectorySession>(new FakeDomainControllerSession(harness.Session, server));
        };
        using var review = await scenario.RunAsync();
        review.SelectedTile = review.Tiles.Single(t => t.Report == ComputerReport.Inactive);

        await review.ReadExactLastLogonCommand.ExecuteAsync(null);

        Assert.Empty(harness.Dialogs.Errors);
        Assert.Empty(Names(review, ComputerReport.Inactive));
        Assert.DoesNotContain("LOD-PC107", Names(review, ComputerReport.EndOfSupport));
    }

    [Fact]
    public async Task ResolveSites_MatchesTheAddressWithTheLongestSubnet_AndDomainControllersWithTheirServerObject()
    {
        var scenario = await CreateAsync();
        scenario.Resolver
            .Add("krk-pc101.contoso.test", "10.20.5.17")
            .Add("krk-pc102.contoso.test", "10.20.1.4")
            .Add("gda-pc103.contoso.test", "192.168.7.3");
        using var review = await scenario.RunAsync();
        review.SelectedTile = review.Tiles.Single(t => t.Report == ComputerReport.EndOfSupport);
        Assert.False(review.HasSites);

        await review.ResolveSitesCommand.ExecuteAsync(null);

        Assert.True(review.HasSites);
        Assert.Contains(review.Columns, c => c.Id == ComputerReviewColumns.Site);
        Assert.Equal("Kraków (10.20.5.0/24)", Row(review, ComputerReport.EndOfSupport, "KRK-PC101")[ComputerReviewColumns.Site]);
        Assert.Equal("Kraków (10.20.0.0/16)", Row(review, ComputerReport.EndOfSupport, "KRK-PC102")[ComputerReviewColumns.Site]);
        Assert.Equal("no subnet", Row(review, ComputerReport.EndOfSupport, "GDA-PC103")[ComputerReviewColumns.Site]);
        Assert.Equal("not resolved", Row(review, ComputerReport.EndOfSupport, "SRV-STARY04")[ComputerReviewColumns.Site]);
        Assert.Contains(scenario.Harness.Notifications.Items, n => n.Severity == NotificationSeverity.Warning && n.Message == "Subnets with an invalid name were skipped: 10.99.0.0/33");

        // A DC's site is its server object's, without DNS.
        review.SelectedTile = review.Tiles.Single(t => t.Release == "Windows Server 2022");
        await review.ResolveSitesCommand.ExecuteAsync(null);
        Assert.Equal("Kraków", Assert.Single(review.Items, r => r.Name == "DC02")[ComputerReviewColumns.Site]);
        Assert.DoesNotContain("dc02.contoso.test", scenario.Resolver.Lookups);
        Assert.Single(scenario.Harness.Notifications.Items, n => n.Message.StartsWith("Subnets with an invalid name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolveSites_ForMoreThan500Computers_AsksFirst()
    {
        var scenario = await CreateAsync();
        var harness = scenario.Harness;
        for (var i = 0; i < ComputerReviewViewModel.SiteConfirmationThreshold; i++)
        {
            harness.Builder.AddComputer($"MASS-PC{i:000}", scenario.Company.ComputersOu, new Dictionary<string, object?>
            {
                [Attr.OperatingSystem] = "Windows 11 Pro",
                [Attr.OperatingSystemVersion] = "10.0 (26100)",
            });
        }

        using var review = await scenario.RunAsync();
        review.SelectedTile = review.Tiles.Single(t => t.Release == "Windows 11 24H2");
        Assert.True(review.Items.Count > ComputerReviewViewModel.SiteConfirmationThreshold);

        await review.ResolveSitesCommand.ExecuteAsync(null);

        Assert.StartsWith("confirm:Resolve the DNS names of all 504 computers of the list?", harness.Dialogs.Shown[^1], StringComparison.Ordinal);
        Assert.Empty(scenario.Resolver.Lookups);
        Assert.False(review.HasSites);
    }

    [Fact]
    public async Task ExportCsv_WritesTheListWithItsColumns_OnlyToTheChosenFile()
    {
        var scenario = await CreateAsync("pl-PL");
        var harness = scenario.Harness;
        using var review = await scenario.RunAsync();
        review.SelectedTile = review.Tiles.Single(t => t.Report == ComputerReport.ServersWithoutOwner);
        var path = Path.Combine(Path.GetTempPath(), $"admng-computers-{Guid.NewGuid():N}.csv");
        harness.Files.NextSavePath = path;
        try
        {
            await review.ExportCsvCommand.ExecuteAsync(null);

            Assert.StartsWith("Serwery bez właściciela_", harness.Files.LastDefaultFileName, StringComparison.Ordinal);
            var lines = await File.ReadAllLinesAsync(path, Ct);
            Assert.Equal("Nazwa;Nazwa DNS;System operacyjny;Właściciel;Ostatnie logowanie;Opis;Kontener;Nazwa wyróżniająca", lines[0]);
            Assert.Equal(4, lines.Length);
            Assert.Contains(lines, l => l.StartsWith("SRV-SQL03;srv-sql03.contoso.test;", StringComparison.Ordinal) && l.EndsWith($";{scenario.Sample.SqlServer}", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Disable_GoesThroughThePipeline_AndTheRowIsReRead()
    {
        var scenario = await CreateAsync();
        var harness = scenario.Harness;
        using var review = await scenario.RunAsync();
        review.SelectedItem = Row(review, ComputerReport.Inactive, "WAW-PC108");
        harness.Dialogs.ConfirmAnswers.Enqueue(true);

        await review.DisableCommand.ExecuteAsync(null);
        await review.Refreshing;

        Assert.Equal("Disable the computer account WAW-PC108 in the domain contoso.test? The computer can no longer log on to the domain.", harness.Dialogs.Shown.Single(s => s.StartsWith("confirm:", StringComparison.Ordinal))["confirm:".Length..]);
        Assert.True((int.Parse(harness.Value(scenario.Sample.Inactive, Attr.UserAccountControl)!, System.Globalization.CultureInfo.InvariantCulture) & 2) != 0);
        Assert.Contains(harness.AuditLog.Entries, e => e.DistinguishedName == scenario.Sample.Inactive);
        Assert.Equal(["LOD-PC107"], Names(review, ComputerReport.Inactive));
        Assert.Equal(["OLD-PC401", "WAW-PC108"], Names(review, ComputerReport.Disabled));
    }

    [Fact]
    public async Task Disable_OfADomainController_IsRefusedByThePlanner()
    {
        var scenario = await CreateAsync();
        var harness = scenario.Harness;
        using var review = await scenario.RunAsync();
        review.SelectedItem = Row(review, ComputerReport.EndOfSupport, "RODC-GDA01");
        harness.Dialogs.ConfirmAnswers.Enqueue(true);

        await review.DisableCommand.ExecuteAsync(null);

        var uac = int.Parse(harness.Value(scenario.Sample.Rodc, Attr.UserAccountControl)!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(0, uac & (int)UserAccountControl.AccountDisable);
        Assert.DoesNotContain(harness.AuditLog.Entries, e => e.DistinguishedName == scenario.Sample.Rodc);
        var (_, error) = Assert.Single(harness.Dialogs.Errors);
        Assert.Contains("domain controller", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Delete_AsksForTheDomain_AndDeletesTheSubtreeWithBitLockerRecovery()
    {
        var scenario = await CreateAsync();
        var harness = scenario.Harness;
        var recovery = harness.Session.Seed($"CN=2026-01-12T10:00:00-00:00{{6A7B8C9D-0000-4000-8000-000000000001}},{scenario.Sample.Windows7}", new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "msFVE-RecoveryInformation" },
        });
        using var review = await scenario.RunAsync();
        review.SelectedItem = Row(review, ComputerReport.Inactive, "LOD-PC107");
        harness.Dialogs.TypedConfirmAnswers.Enqueue(true);
        harness.Dialogs.ConfirmAnswers.Enqueue(true);

        await review.DeleteCommand.ExecuteAsync(null);
        await review.Refreshing;

        var (_, message, expected) = Assert.Single(harness.Dialogs.TypedConfirmations);
        Assert.Equal("contoso.test", expected);
        Assert.Contains("BitLocker", message, StringComparison.Ordinal);
        Assert.False(harness.Session.Exists(scenario.Sample.Windows7));
        Assert.False(harness.Session.Exists(recovery));
        Assert.Equal(["WAW-PC108"], Names(review, ComputerReport.Inactive));
    }

    [Fact]
    public async Task Move_AndSetOwner_GoThroughThePipeline()
    {
        var scenario = await CreateAsync();
        var harness = scenario.Harness;
        using var review = await scenario.RunAsync();
        review.SelectedItem = Row(review, ComputerReport.UnexpectedContainer, "NEW-PC301");
        harness.Dialogs.PickedContainers.Enqueue(scenario.Company.ComputersOu);
        harness.Dialogs.ConfirmAnswers.Enqueue(true);

        await review.MoveCommand.ExecuteAsync(null);
        await review.Refreshing;

        Assert.True(harness.Session.Exists($"CN=NEW-PC301,{scenario.Company.ComputersOu}"));
        Assert.Equal(["SRV-ZLY05"], Names(review, ComputerReport.UnexpectedContainer));

        review.SelectedItem = Row(review, ComputerReport.ServersWithoutOwner, "SRV-APP02");
        harness.Dialogs.PickedObjects.Enqueue([scenario.Company.JanKowalski]);
        harness.Dialogs.ConfirmAnswers.Enqueue(true);
        await review.SetOwnerCommand.ExecuteAsync(null);
        await review.Refreshing;

        Assert.Equal(scenario.Company.JanKowalski, harness.Value(scenario.Sample.AppServer, Attr.ManagedBy));
        Assert.Equal(["SRV-SQL03", "SRV-ZLY05"], Names(review, ComputerReport.ServersWithoutOwner));
    }

    [Fact]
    public async Task RemoteActions_PingAndRemoteDesktop_UseTheSelectedComputer()
    {
        var scenario = await CreateAsync();
        scenario.Ping.Answering["srv-app02.contoso.test"] = TimeSpan.FromMilliseconds(3);
        using var review = await scenario.RunAsync();
        review.SelectedItem = Row(review, ComputerReport.ServersWithoutOwner, "SRV-APP02");
        Assert.True(review.CanUseRemote);
        Assert.True(review.CanUseTools);

        await review.RemoteActionsCommand.ExecuteAsync(null);
        await review.PingCommand.ExecuteAsync(null);
        await review.RemoteDesktopCommand.ExecuteAsync(null);

        Assert.Equal([scenario.Sample.AppServer], scenario.Dialogs.RemoteWindows);
        Assert.Equal(["srv-app02.contoso.test"], scenario.Ping.Pinged);
        var launch = Assert.Single(scenario.Launcher.Launches);
        Assert.Equal(Core.Computers.Remote.LocalTool.RemoteDesktop, launch.Tool);
        Assert.Equal("srv-app02.contoso.test", launch.Host);
        Assert.False(launch.WithWindowCredential);
    }

    [Fact]
    public async Task SessionEnded_KeepsTheResults_ButRefusesRunsAndChanges()
    {
        var scenario = await CreateAsync();
        var harness = scenario.Harness;
        using var review = await scenario.RunAsync();
        review.SelectedItem = Row(review, ComputerReport.Inactive, "WAW-PC108");

        await harness.ReconnectAsync(new FakeDirectoryBuilder(timeProvider: harness.Time).Session);

        Assert.True(review.IsSessionEnded);
        Assert.Single(review.Items, r => r.Name == "WAW-PC108");
        Assert.False(review.RunCommand.CanExecute(null));
        Assert.False(review.DisableCommand.CanExecute(null));
        Assert.False(review.ReadExactLastLogonCommand.CanExecute(null));
        Assert.True(review.ExportCsvCommand.CanExecute(null));
    }

    [Fact]
    public async Task Polish_TextsAndDates()
    {
        var scenario = await CreateAsync("pl-PL");
        using var review = await scenario.RunAsync();

        Assert.Equal("Przegląd komputerów – contoso.test", review.WindowTitle);
        var pro = Row(review, ComputerReport.EndOfSupport, "KRK-PC101");
        Assert.Equal("zakończone 13.10.2026", pro[ComputerReviewColumns.Support]);
        Assert.Equal("Stacja robocza", pro[ComputerReviewColumns.Type]);
        Assert.Equal("Kontroler domeny tylko do odczytu", Row(review, ComputerReport.EndOfSupport, "RODC-GDA01")[ComputerReviewColumns.Type]);
    }
}
