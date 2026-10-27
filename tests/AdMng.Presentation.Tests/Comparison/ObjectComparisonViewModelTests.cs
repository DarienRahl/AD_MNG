using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Comparison;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.Schema;
using AdMng.Presentation.Comparison;
using AdMng.Presentation.Connections;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Presentation.Tests.Comparison;

/// <summary>
/// "Porównaj z obiektem w innym połączeniu…" on two connection windows (two DI scopes): contoso.test and fabrikam.test
/// (design note docs/dev/multi-window.md 8 and 10.2).
/// </summary>
public sealed class ObjectComparisonViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LukaszInContosoAgainstFabrikam_ShowsTitleAndCity_AndTheMembershipDifferences()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, b) = await domains.OpenAsync();
        await using var _ = host;

        using var comparison = await a.Compare(domains.Contoso.Company.LukaszWisniewski).RunAsync();

        // The only other connection is preselected and compared at once (automatic match by sAMAccountName).
        Assert.Equal(b.Context.Id, comparison.SelectedTarget?.Id);
        Assert.True(comparison.HasResult);
        Assert.False(comparison.IsSameObject);
        Assert.Equal(ObjectMatchRule.Automatic, comparison.SelectedRule?.Value);
        Assert.Contains(domains.FabrikamDomain.Company.LukaszWisniewski, comparison.StatusText, StringComparison.Ordinal);
        Assert.Equal("Comparison: Łukasz Wiśniewski – contoso.test ↔ fabrikam.test", comparison.WindowTitle);

        // "Tylko różnice" and hidden volatile attributes: title and city.
        Assert.Equal([Attr.City, Attr.Title], comparison.AttributeRows.Select(r => r.LdapName).Order(StringComparer.Ordinal));
        var title = comparison.Row(Attr.Title);
        Assert.Equal(AttributeComparisonState.Different, title.State);
        Assert.Equal("Programista", title.ValueTextA);
        Assert.Equal("Starszy programista", title.ValueTextB);
        Assert.Equal("≠", title.Glyph);

        // Membership: GG_IT only in A, GG_Sprzedaz only in B; DL_Wszyscy nested on both sides, Domain Users by RID.
        Assert.Equal(
            [("GG_IT", MembershipComparisonState.OnlyA), ("GG_Sprzedaz", MembershipComparisonState.OnlyB)],
            comparison.MembershipRows.Select(r => (r.Name, r.State)).OrderBy(r => r.Name, StringComparer.Ordinal));
        comparison.OnlyMembershipDifferences = false;
        Assert.Contains(comparison.MembershipRows, r => r is { Name: "DL_Wszyscy", State: MembershipComparisonState.InBoth, KindTextA: "nested" });
        Assert.Contains(comparison.MembershipRows, r => r is { Name: "Domain Users / Użytkownicy domeny", State: MembershipComparisonState.InBoth, KindTextB: "primary group" });
        Assert.Equal("Attributes (2)", comparison.AttributesTabHeader);
        Assert.Equal("Membership (2)", comparison.MembershipTabHeader);
    }

    [Fact]
    public async Task Toggles_ShowEqualAndVolatileAttributes_AndAddressesWithTheirDomain()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, _) = await domains.OpenAsync();
        await using var _ = host;
        using var comparison = await a.Compare(domains.Contoso.Company.LukaszWisniewski).RunAsync();
        Assert.DoesNotContain(comparison.AttributeRows, r => r.IsVolatile);

        comparison.OnlyDifferences = false;
        Assert.Equal(AttributeComparisonState.SameOtherDomain, comparison.Row(Attr.Mail).State);
        Assert.Equal(AttributeComparisonState.Same, comparison.Row(Attr.Manager).State);
        Assert.Equal(AttributeComparisonState.Same, comparison.Row(Attr.Department).State);
        Assert.DoesNotContain(comparison.AttributeRows, r => r.LdapName == Attr.WhenCreated);

        comparison.ShowVolatile = true;
        Assert.True(comparison.Row(Attr.ObjectGuid).IsVolatile);
        Assert.True(comparison.Row(Attr.WhenChanged).IsVolatile);

        comparison.CompareAddressesWithoutDomain = false;
        Assert.Equal(AttributeComparisonState.Different, comparison.Row(Attr.Mail).State);
        Assert.Equal(AttributeComparisonState.Different, comparison.Row(Attr.UserPrincipalName).State);

        comparison.FilterText = "mail";
        Assert.Equal([Attr.Mail], comparison.AttributeRows.Select(r => r.LdapName));

        // The switches are remembered (names and booleans only).
        var remembered = a.Get<ISettingsStore>().Current.Comparison;
        Assert.False(remembered.OnlyDifferences);
        Assert.True(remembered.ShowVolatile);
        Assert.False(remembered.CompareAddressesWithoutDomain);
    }

    [Fact]
    public async Task TheSameObjectThroughTwoAccounts_IsRecognized()
    {
        var builder = new FakeDirectoryBuilder();
        var company = builder.AddSampleCompany();
        await using var host = new Connections.ConnectionScopeHost(domains: [builder.Build()]);
        await using var a = host.CreateConnection();
        await using var b = host.CreateConnection();
        await a.ConnectAsync(user: "jan.kowalski");
        await b.ConnectAsync(user: "odczyt.test");

        using var comparison = await a.Compare(company.LukaszWisniewski).RunAsync();

        Assert.True(comparison.IsSameObject);
        Assert.Empty(comparison.AttributeRows);
        Assert.Contains("CONTOSO\\odczyt.test", comparison.SideB.Label, StringComparison.Ordinal);
        Assert.Contains("CONTOSO\\jan.kowalski", comparison.SideA.Label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SideB_IsFormattedWithItsOwnFormatterAndSchema()
    {
        // employeeType is a string in contoso's schema and an integer in fabrikam's.
        var fabrikamSchema = SchemaWith(d => d.LdapName == "employeeType" ? d with { Syntax = AttributeSyntax.Integer } : d);
        var domains = await ComparisonDomains.CreateAsync(
            fabrikamSchema,
            contosoExtra: b => b.Session.SetValues(b.Session.DistinguishedNames.Single(d => d.StartsWith("CN=Łukasz", StringComparison.Ordinal)), "employeeType", "1"),
            fabrikamExtra: b => b.Session.SetValues(b.Session.DistinguishedNames.Single(d => d.StartsWith("CN=Łukasz", StringComparison.Ordinal)), "employeeType", "2"));
        var (host, a, _) = await domains.OpenAsync(s => s.AddAttributeFormatter(schema => new SchemaTaggingFormatter(schema)));
        await using var _ = host;

        using var comparison = await a.Compare(domains.Contoso.Company.LukaszWisniewski).RunAsync();

        var row = comparison.Row("employeeType");
        Assert.Equal("UnicodeString:1", row.ValueTextA);
        Assert.Equal("Integer:2", row.ValueTextB);
        Assert.Equal("UnicodeString:Programista", comparison.Row(Attr.Title).ValueTextA);
    }

    [Fact]
    public async Task AnAttributeConfidentialOnlyInB_IsMaskedInBothColumns_AndInTheCsv()
    {
        var fabrikamSchema = SchemaWith(d => d.LdapName == Attr.EmployeeId ? d with { IsConfidential = true } : d);
        var domains = await ComparisonDomains.CreateAsync(
            fabrikamSchema,
            contosoExtra: b => b.Session.SetValues(b.Session.DistinguishedNames.Single(d => d.StartsWith("CN=Łukasz", StringComparison.Ordinal)), Attr.EmployeeId, "PL-0042"),
            fabrikamExtra: b => b.Session.SetValues(b.Session.DistinguishedNames.Single(d => d.StartsWith("CN=Łukasz", StringComparison.Ordinal)), Attr.EmployeeId, "PL-9999"));
        var (host, a, _) = await domains.OpenAsync(s => s.AddSingleton<ICsvCodec, CsvCodec>());
        await using var _ = host;
        using var comparison = await a.Compare(domains.Contoso.Company.LukaszWisniewski).RunAsync();
        comparison.OnlyDifferences = false;

        var row = comparison.Row(Attr.EmployeeId);
        var path = Path.Combine(Path.GetTempPath(), $"admng-compare-{Guid.NewGuid():N}.csv");
        ((FakeFileDialogService)a.Get<IFileDialogService>()).NextSavePath = path;
        try
        {
            await comparison.ExportCsvCommand.ExecuteAsync(null);
            var csv = await File.ReadAllTextAsync(path, Ct);

            Assert.True(row.IsSecret);
            Assert.DoesNotContain("PL-0042", row.ValueTextA, StringComparison.Ordinal);
            Assert.DoesNotContain("PL-9999", row.ValueTextB, StringComparison.Ordinal);
            Assert.All(row.Details, d => Assert.Equal(SecretAttributes.Mask, d.Text));
            Assert.DoesNotContain("PL-0042", csv, StringComparison.Ordinal);
            Assert.DoesNotContain("PL-9999", csv, StringComparison.Ordinal);
            Assert.Contains(SecretAttributes.Mask, csv, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CsvExport_OnDemand_WithTheConnectionLabelsTheDnsAndFormulaProtection()
    {
        var domains = await ComparisonDomains.CreateAsync(fabrikamExtra: b =>
            b.Session.SetValues(b.Session.DistinguishedNames.Single(d => d.StartsWith("CN=Łukasz", StringComparison.Ordinal)), Attr.Description, "=SUMA(A1:A2) i ąęł"));
        var (host, a, b) = await domains.OpenAsync(s => s.AddSingleton<ICsvCodec, CsvCodec>());
        await using var _ = host;
        using var comparison = await a.Compare(domains.Contoso.Company.LukaszWisniewski).RunAsync();
        var files = ((FakeFileDialogService)a.Get<IFileDialogService>());
        var path = Path.Combine(Path.GetTempPath(), $"admng-compare-{Guid.NewGuid():N}.csv");
        files.NextSavePath = path;
        try
        {
            await comparison.ExportCsvCommand.ExecuteAsync(null);

            var text = await File.ReadAllTextAsync(path, Ct);
            var separator = CsvFiles.ListSeparator(a.Get<IRegionalSettings>().Culture);
            var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None);
            Assert.Equal(
                string.Join(separator, "Section", "Attribute", "LDAP name", "State", "contoso.test · CONTOSO\\jan.kowalski", "fabrikam.test · FABRIKAM\\anna.zielinska"),
                lines[0]);
            Assert.StartsWith(string.Join(separator, "Attributes", "Distinguished name", "distinguishedName", string.Empty, string.Empty), lines[1] + separator, StringComparison.Ordinal);
            Assert.Contains(domains.Contoso.Company.LukaszWisniewski, text, StringComparison.Ordinal);
            Assert.Contains(domains.FabrikamDomain.Company.LukaszWisniewski, text, StringComparison.Ordinal);
            Assert.Contains("'=SUMA(A1:A2) i ąęł", text, StringComparison.Ordinal);
            Assert.Contains("GG_Sprzedaz", text, StringComparison.Ordinal);
            Assert.Equal("comparison-Łukasz Wiśniewski-contoso.test-fabrikam.test.csv", files.LastDefaultFileName);
            Assert.Contains(a.Notifications.Items, n => n.Severity == NotificationSeverity.Success);
            Assert.Empty(b.Notifications.Items);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ACancelledSaveDialog_WritesNothing()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, _) = await domains.OpenAsync();
        await using var _ = host;
        using var comparison = await a.Compare(domains.Contoso.Company.LukaszWisniewski).RunAsync();
        ((FakeFileDialogService)a.Get<IFileDialogService>()).NextSavePath = null;

        await comparison.ExportCsvCommand.ExecuteAsync(null);

        Assert.Empty(a.Notifications.Items);
        Assert.Empty(a.Dialogs.Errors);
    }

    [Fact]
    public async Task TheComparisonNeverWrites()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, _) = await domains.OpenAsync(s => s.AddSingleton<ICsvCodec, CsvCodec>());
        await using var _ = host;
        using var comparison = await a.Compare(domains.Contoso.Company.LukaszWisniewski).RunAsync();
        comparison.ShowNestedMembers = true;
        await comparison.RefreshCommand.ExecuteAsync(null);
        await comparison.Pending;
        var path = Path.Combine(Path.GetTempPath(), $"admng-compare-{Guid.NewGuid():N}.csv");
        ((FakeFileDialogService)a.Get<IFileDialogService>()).NextSavePath = path;
        await comparison.ExportCsvCommand.ExecuteAsync(null);
        File.Delete(path);

        Assert.NotEmpty(domains.Contoso.Session.Operations);
        Assert.NotEmpty(domains.FabrikamDomain.Session.Operations);
        Assert.Empty(domains.Contoso.Session.Writes);
        Assert.Empty(domains.FabrikamDomain.Session.Writes);
        Assert.Empty(domains.FabrikamDomain.Session.PasswordResets);
    }

    [Fact]
    public async Task AtMostTwoRequestsAtATime_ReachTheOtherConnection()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, b) = await domains.OpenAsync();
        await using var _ = host;
        await b.Sessions.GetDomainPolicyAsync(Ct);
        var fabrikam = domains.FabrikamDomain.Session;
        fabrikam.Latency = TimeSpan.FromMilliseconds(15);
        fabrikam.ResetConcurrencyStatistics();

        using var comparison = await a.Compare(domains.Contoso.Company.GroupAll).RunAsync();
        comparison.ShowNestedMembers = true;
        await comparison.Pending;

        Assert.True(comparison.IsGroup);
        Assert.True(comparison.HasResult);
        Assert.InRange(fabrikam.MaxObservedConcurrency, 1, ConnectionReadContext.MaxConcurrentRequests);
    }

    [Fact]
    public async Task Groups_CompareTheirMembers()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, _) = await domains.OpenAsync();
        await using var _ = host;

        using var comparison = await a.Compare(domains.Contoso.Company.GroupIt).RunAsync();

        Assert.True(comparison.IsGroup);
        var lukasz = Assert.Single(comparison.MemberRows);
        Assert.Equal(("Łukasz Wiśniewski", MembershipComparisonState.OnlyA), (lukasz.Name, lukasz.State));
        Assert.Equal("Members (1)", comparison.MembersTabHeader);
        Assert.DoesNotContain(comparison.AttributeRows, r => r.LdapName is Attr.Member or Attr.MemberOf);

        comparison.OnlyMemberDifferences = false;
        Assert.Contains(comparison.MemberRows, r => r is { Name: "Jan Kowalski", State: MembershipComparisonState.InBoth });
    }

    [Fact]
    public async Task AnAmbiguousMatch_ListsCandidates_AndChoosingOneCompares()
    {
        var domains = await ComparisonDomains.CreateAsync(fabrikamExtra: b =>
            b.AddUser("Łukasz", "Wiśniewski", b.AddOu("Oddział Gdańsk"), new Dictionary<string, object?> { [Attr.SamAccountName] = "lwisniewski2" }));
        var (host, a, _) = await domains.OpenAsync();
        await using var _ = host;
        var comparison = a.Compare(domains.Contoso.Company.LukaszWisniewski);
        using var disposeComparison = comparison;
        await comparison.InitializeAsync();
        comparison.SelectedRule = comparison.MatchRules.Single(r => r.Value == ObjectMatchRule.Name);

        await comparison.CompareCommand.ExecuteAsync(null);

        Assert.False(comparison.HasResult);
        Assert.Equal(2, comparison.Candidates.Count);
        Assert.Equal("Found 2 objects – choose one", comparison.StatusText);
        Assert.Equal(ObjectMatchRule.Name, a.Get<ISettingsStore>().Current.Comparison.RuleFor(Core.Model.ObjectKind.User));

        comparison.SelectedCandidate = comparison.Candidates.Single(c => c.Path.Contains("Gdańsk", StringComparison.Ordinal));
        await comparison.Pending;

        Assert.True(comparison.HasResult);
        Assert.Contains("Oddział Gdańsk", comparison.SideB.Summary?.DistinguishedName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoOtherConnection_SaysHowToOpenOne()
    {
        var domains = await ComparisonDomains.CreateAsync();
        await using var host = new Connections.ConnectionScopeHost(domains: [domains.Contoso.Session, domains.FabrikamDomain.Session]);
        await using var a = host.CreateConnection();
        await a.ConnectAsync(user: "jan.kowalski");
        using var comparison = a.Compare(domains.Contoso.Company.LukaszWisniewski);

        await comparison.InitializeAsync();

        Assert.False(comparison.HasTargets);
        Assert.Equal("No other connection is open. Open a second connection (Ctrl+Shift+N) to compare.", comparison.NoTargetsMessage);
        Assert.False(comparison.CompareCommand.CanExecute(null));

        // Side A is read meanwhile: its summary and the rules of a user account.
        Assert.Equal("Łukasz Wiśniewski", comparison.SideA.Summary?.Name);
        Assert.Equal(ObjectMatcher.Choices(Core.Model.ObjectKind.User), comparison.MatchRules.Select(r => r.Value));
        Assert.Equal(ObjectMatchRule.Automatic, comparison.SelectedRule?.Value);
        Assert.Null(comparison.SideB.Summary);

        // A second window that connects now appears and is preselected.
        await using var b = host.CreateConnection();
        await b.ConnectAsync(ComparisonDomains.Fabrikam, "anna.zielinska");
        Assert.True(comparison.HasTargets);
        Assert.Equal(b.Context.Id, comparison.SelectedTarget?.Id);
        Assert.True(comparison.CompareCommand.CanExecute(null));
    }

    [Fact]
    public async Task BClosed_MarksSideBEnded_AndKeepsItsResults()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, b) = await domains.OpenAsync();
        await using var _ = host;
        using var comparison = await a.Compare(domains.Contoso.Company.LukaszWisniewski).RunAsync();

        await b.DisposeAsync();

        Assert.True(comparison.SideB.IsEnded);
        Assert.Equal("The connection fabrikam.test · FABRIKAM\\anna.zielinska was closed – its results stay visible.", comparison.SideB.EndedMessage);
        Assert.NotEmpty(comparison.AttributeRows);
        Assert.Null(comparison.SelectedTarget);
        Assert.False(comparison.HasTargets);
    }

    [Fact]
    public async Task BStartsClosing_SideBIsClosedAtOnce_AndNoLongerOffered()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, b) = await domains.OpenAsync();
        await using var _ = host;
        using var comparison = await a.Compare(domains.Contoso.Company.LukaszWisniewski).RunAsync();
        var target = comparison.SelectedTarget;

        // B's main window is gone; its close pipeline still waits for its activities and its session.
        b.Context.EndLifetime();

        Assert.True(comparison.SideB.IsEnded);
        Assert.Equal("The connection fabrikam.test · FABRIKAM\\anna.zielinska was closed – its results stay visible.", comparison.SideB.EndedMessage);
        Assert.False(comparison.HasTargets);
        Assert.Null(host.Registry.Find(b.Context.Id));
        Assert.NotNull(b.Sessions.Current);

        // A comparison opened now does not offer B, and B pins nothing for anyone.
        using var another = await a.Compare(domains.Contoso.Company.LukaszWisniewski).RunAsync();
        Assert.False(another.HasTargets);
        Assert.False(another.HasResult);
        Assert.Null(b.Context.Pin());
        Assert.NotNull(target);
    }

    [Fact]
    public async Task BSwitchesItsDomainController_SideBEnds_AndRefreshReadsThroughTheNewSession()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, b) = await domains.OpenAsync();
        await using var _ = host;
        using var comparison = await a.Compare(domains.Contoso.Company.LukaszWisniewski).RunAsync();

        await b.Sessions.SwitchDomainControllerAsync("dc02.fabrikam.test", null, Ct);

        Assert.True(comparison.SideB.IsEnded);
        Assert.Contains("Refresh", comparison.SideB.EndedMessage, StringComparison.Ordinal);

        domains.FabrikamDomain.Session.SetValues(domains.FabrikamDomain.Company.LukaszWisniewski, Attr.City, "Kraków");
        await comparison.RefreshCommand.ExecuteAsync(null);
        await comparison.Pending;

        Assert.False(comparison.SideB.IsEnded);
        Assert.DoesNotContain(comparison.AttributeRows, r => r.LdapName == Attr.City);
        Assert.Contains(comparison.Targets, t => t.Server == "dc02.fabrikam.test");
    }

    [Fact]
    public async Task BDisconnectsDuringTheRead_SideBEnds_WithoutReadingThroughAnotherSession()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, b) = await domains.OpenAsync();
        await using var _ = host;
        var fabrikam = domains.FabrikamDomain.Session;
        var disconnected = false;
        fabrikam.BeforeOperation = op =>
        {
            if (!disconnected && op.Kind == FakeOperationKind.Get && op.DistinguishedName.Contains("Łukasz", StringComparison.Ordinal))
            {
                disconnected = true;
                b.Sessions.DisconnectAsync().GetAwaiter().GetResult();
            }
        };

        using var comparison = await a.Compare(domains.Contoso.Company.LukaszWisniewski).RunAsync();

        // The read already sent completes; nothing more goes to the closed session, and no error is shown: side B ended.
        Assert.True(disconnected);
        Assert.True(comparison.SideB.IsEnded);
        Assert.False(comparison.IsRunning);
        Assert.Null(comparison.ErrorText);
        Assert.Null(b.Sessions.Current);
    }

    [Fact]
    public async Task BReconnectsToAnotherDomainMidRun_TheRunKeepsItsPinnedContext()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, b) = await domains.OpenAsync();
        await using var _ = host;
        var target = b.Context.Pin()!;
        var formatter = target.Formatter;
        var policy = await target.Policy;

        await b.ConnectAsync(user: "jan.kowalski");

        // The pinned context still names fabrikam and keeps its formatter and policy; it reads nothing any more.
        Assert.Equal(ComparisonDomains.Fabrikam, target.Info.DomainDnsName);
        Assert.Same(formatter, target.Formatter);
        Assert.Same(policy, await target.Policy);
        Assert.True(target.IsEnded);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => target.Reader.GetAsync(domains.FabrikamDomain.Company.LukaszWisniewski, [Attr.Name], Ct));
        Assert.Equal("contoso.test", b.Context.Identity?.DomainDnsName);
    }

    [Fact]
    public async Task AFailedRunAgainstAnotherConnection_DropsThePreviousResults_InsteadOfRelabellingThem()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, b) = await domains.OpenAsync(s => s.AddSingleton<ICsvCodec, CsvCodec>());
        await using var _ = host;
        await using var c = host.CreateConnection();
        await c.ConnectAsync(ComparisonDomains.Fabrikam, "piotr.nowak");
        using var comparison = a.Compare(domains.Contoso.Company.LukaszWisniewski);
        await comparison.InitializeAsync();
        comparison.SelectedTarget = comparison.Targets.Single(t => t.Id == b.Context.Id);
        await comparison.CompareCommand.ExecuteAsync(null);
        await comparison.Pending;
        Assert.True(comparison.HasResult);
        Assert.Equal("Wrocław", comparison.Row(Attr.City).ValueTextB);

        // Window C: its DC times out reading the matched object.
        domains.FabrikamDomain.Session.FailNext(FakeOperationKind.Get, domains.FabrikamDomain.Company.LukaszWisniewski, DirectoryErrorKind.Timeout);
        comparison.SelectedTarget = comparison.Targets.Single(t => t.Id == c.Context.Id);
        await comparison.CompareCommand.ExecuteAsync(null);
        await comparison.Pending;

        // Side B names window C now: nothing read through window B is left under that label, in the tabs or the CSV.
        Assert.NotNull(comparison.ErrorText);
        Assert.Contains("FABRIKAM\\piotr.nowak", comparison.SideB.Label, StringComparison.Ordinal);
        Assert.False(comparison.HasResult);
        Assert.Null(comparison.SideB.Summary);
        Assert.Empty(comparison.AttributeRows);
        Assert.Empty(comparison.MembershipRows);
        Assert.Equal("Attributes", comparison.AttributesTabHeader);
        Assert.Equal("Membership", comparison.MembershipTabHeader);
        Assert.False(comparison.ExportCsvCommand.CanExecute(null));
    }

    [Fact]
    public async Task BReconnectsToAnotherDomain_RefreshMatchesThere_AndNeverShowsFabrikamUnderItsLabel()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, b) = await domains.OpenAsync();
        await using var _ = host;
        using var comparison = await a.Compare(domains.Contoso.Company.LukaszWisniewski).RunAsync();
        Assert.Equal("Wrocław", comparison.Row(Attr.City).ValueTextB);

        await b.ConnectAsync(user: "anna.zielinska");
        Assert.True(comparison.SideB.IsEnded);
        Assert.Contains("fabrikam.test", comparison.SideB.Label, StringComparison.Ordinal);

        await comparison.RefreshCommand.ExecuteAsync(null);
        await comparison.Pending;

        // The fabrikam DN means nothing in contoso.test: the rule matches again there (the same object, here).
        Assert.False(comparison.SideB.IsEnded);
        Assert.Contains("contoso.test", comparison.SideB.Label, StringComparison.Ordinal);
        Assert.Equal(domains.Contoso.Company.LukaszWisniewski, comparison.SideB.Summary?.DistinguishedName);
        Assert.True(comparison.IsSameObject);
        Assert.Empty(comparison.AttributeRows);
        Assert.Equal("Comparison: Łukasz Wiśniewski – contoso.test ↔ contoso.test", comparison.WindowTitle);
    }

    [Fact]
    public async Task AnotherObjectWhoseMembershipReadFails_ShowsNoGroupsOfThePreviousObject()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, _) = await domains.OpenAsync();
        await using var _ = host;
        using var comparison = await a.Compare(domains.Contoso.Company.LukaszWisniewski).RunAsync();
        Assert.Contains(comparison.MembershipRows, r => r.Name == "GG_Sprzedaz");

        // Explicitly Jan Kowalski in fabrikam; the DC times out reading his groups.
        comparison.SelectedRule = comparison.MatchRules.Single(r => r.Value == ObjectMatchRule.Explicit);
        comparison.ExplicitText = domains.FabrikamDomain.Company.JanKowalski;
        domains.FabrikamDomain.Session.FailNext(FakeOperationKind.Search, null, DirectoryErrorKind.Timeout);
        await comparison.CompareCommand.ExecuteAsync(null);
        await comparison.Pending;

        Assert.NotNull(comparison.ErrorText);
        Assert.True(comparison.HasResult);
        Assert.Equal(domains.FabrikamDomain.Company.JanKowalski, comparison.SideB.Summary?.DistinguishedName);
        Assert.Empty(comparison.MembershipRows);
        Assert.Equal("Membership", comparison.MembershipTabHeader);
    }

    [Fact]
    public async Task ClosingTheWindow_CancelsTheRun()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, _) = await domains.OpenAsync();
        await using var _ = host;
        domains.FabrikamDomain.Session.Latency = TimeSpan.FromMilliseconds(200);
        var comparison = a.Compare(domains.Contoso.Company.LukaszWisniewski);

        var run = comparison.InitializeAsync();
        comparison.Dispose();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await comparison.Pending.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.False(comparison.HasResult);
        Assert.Empty(comparison.AttributeRows);
    }

    [Fact]
    public async Task ThePolishWindow_SpeaksPolish()
    {
        var domains = await ComparisonDomains.CreateAsync();
        var (host, a, _) = await domains.OpenAsync();
        await using var _ = host;
        a.Get<ILocalizer>().SetCulture(CultureInfo.GetCultureInfo("pl-PL"));
        try
        {
            using var comparison = await a.Compare(domains.Contoso.Company.LukaszWisniewski).RunAsync();

            Assert.Equal("Porównanie: Łukasz Wiśniewski – contoso.test ↔ fabrikam.test", comparison.WindowTitle);
            Assert.Equal("Różne", comparison.Row(Attr.Title).StateText);
            Assert.Equal("Atrybuty (2)", comparison.AttributesTabHeader);
            Assert.Contains(comparison.MembershipRows, r => r.StateText == "tylko A");
            Assert.Equal("Automatycznie", comparison.SelectedRule?.Text);
        }
        finally
        {
            a.Get<ILocalizer>().SetCulture(CultureInfo.InvariantCulture);
        }
    }

    private static SchemaCatalog SchemaWith(Func<AttributeDefinition, AttributeDefinition> change)
    {
        var schema = FakeSchema.Create("CN=Schema,CN=Configuration,DC=fabrikam,DC=test");
        return new SchemaCatalog(schema.Attributes.Select(change), schema.Classes);
    }
}
