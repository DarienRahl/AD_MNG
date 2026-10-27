using System.Globalization;
using AdMng.Core.Csv;
using AdMng.Core.GroupPolicy;
using AdMng.Core.PrivilegedAccess;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.AccountReview;

/// <summary>
/// The "Uprawnienia" tab with the U4c source (local rights from GPOs) handed in by the factory: the real engines and the
/// fake DC with <see cref="FakeDirectoryBuilder.AddGroupPolicySample"/>, SYSVOL in memory (<see cref="FakeSysvolFileReader"/>).
/// </summary>
public class PrivilegedAccessGroupPolicyTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public async Task LocalRights_FromGpos_InPolish_WithGpoComputersAndConditions()
    {
        var (harness, _, sysvol) = await CreateAsync(Polish);
        using var review = await RunAsync(harness, sysvol);
        var tab = review.Privileges;

        Assert.Null(tab.Error);
        Assert.Contains(tab.SourceOptions, o => o.Source == PrivilegeSource.GroupPolicy && o.Title == "Uprawnienia lokalne z GPO");

        // Restricted Groups on OU=Serwery: the local group by its Windows name, the GPO, the computers and the path.
        tab.SelectedItem = Row(tab, "Ewa Błaszczyk");
        var admin = Assert.Single(tab.Findings, f => f.Finding.GroupPolicy?.Right == LocalRightsCatalog.AdministratorsSid
            && f.Finding.GroupPolicy.Mechanism == LocalRightMechanism.RestrictedGroupMembers);
        Assert.Equal("Uprawnienie lokalne z zasad grupy: Administratorzy", admin.Title);
        Assert.Equal("Uprawnienia lokalne z GPO", admin.SourceText);
        Assert.Equal("Wysoki", admin.LevelText);
        Assert.Equal("Ewa Błaszczyk → GG_Administratorzy_Serwerów", admin.PathText);
        Assert.Equal("Komputery w contoso.test/Firma/Serwery", admin.ScopeText);
        Assert.Equal(
            "Pełna kontrola nad komputerem: instaluje oprogramowanie i usługi, odczytuje pamięć i poświadczenia zalogowanych tam użytkowników. "
            + "Źródło: Grupy z ograniczeniami (członkowie grupy) w GPO „Serwery – Administratorzy lokalni”; "
            + "komputery: 2 (kontrolery domeny: 0, serwery: 2, stacje robocze: 0).",
            admin.GrantsText);
        Assert.Equal("GPO „Serwery – Administratorzy lokalni”: Administratorzy", admin.ValuesText);
        Assert.True(admin.CanRemoveFromGroup);

        // A user right on the DCs is Tier 0, named as the security policy editor names it.
        tab.SelectedItem = Row(tab, "Łukasz Wiśniewski");
        Assert.Equal("Tier 0", tab.SelectedItem![PrivilegedAccessColumns.Level]);
        var debug = tab.Findings[0];
        Assert.Equal("Uprawnienie lokalne z zasad grupy: Debugowanie programów (SeDebugPrivilege)", debug.Title);
        Assert.Equal("Komputery w contoso.test/Domain Controllers", debug.ScopeText);
        Assert.StartsWith("Odczytuje i zmienia pamięć dowolnego procesu", debug.GrantsText, StringComparison.Ordinal);
        Assert.Contains("Przypisywanie praw użytkownika w GPO „Default Domain Controllers Policy”", debug.GrantsText, StringComparison.Ordinal);
        Assert.False(debug.CanRemoveFromGroup);

        // Item-level targeting and an enforced link are spelled out.
        tab.SelectedItem = Row(tab, "Dorota Łukasiewicz");
        var targeted = Assert.Single(tab.Findings, f => f.Finding.GroupPolicy?.ItemLevelTargeting == true);
        Assert.EndsWith("Warunkowo: element preferencji ma określanie elementów docelowych – dotyczy tylko pasujących komputerów.", targeted.GrantsText, StringComparison.Ordinal);
        tab.SelectedItem = Row(tab, "Zbigniew Kaźmierczak");
        var enforced = Assert.Single(tab.Findings, f => f.Finding.Kind == PrivilegeFindingKind.LocalRight);
        Assert.Contains("Połączenie GPO jest wymuszone.", enforced.GrantsText, StringComparison.Ordinal);
        Assert.Contains("komputery: 6 (kontrolery domeny: 1, serwery: 2, stacje robocze: 3)", enforced.GrantsText, StringComparison.Ordinal);
        Assert.Equal("Drobny", enforced.LevelText);

        // The source toggle: only GPO rights.
        foreach (var option in tab.SourceOptions.Where(o => o.Source != PrivilegeSource.GroupPolicy))
        {
            option.IsActive = false;
        }

        Assert.Equal(
            ["Dorota Łukasiewicz", "Ewa Błaszczyk", "Henryk Żuławski", "Łukasz Wiśniewski", "Małgorzata Wójcik", "Stanisław Łęcki", "Zbigniew Kaźmierczak"],
            Names(tab));
        Assert.Equal(7, tab.SourceOptions.Single(o => o.Source == PrivilegeSource.GroupPolicy).Count);

        // The unknown name of a GPP item is reported in the tab.
        Assert.True(tab.HasNotices);
        Assert.Equal("Pominięto nazwy z zasad grupy, które nie są kontami ani grupami tej domeny (1): CONTOSO\\nieistniejący.użytkownik.", tab.NoticeText);

        // The UI language changes: the notices follow.
        harness.Localizer.SetCulture(CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal("Names in group policies that are no account or group of this domain were skipped (1): CONTOSO\\nieistniejący.użytkownik.", tab.NoticeText);
    }

    [Fact]
    public async Task CsvExport_ListsTheLocalRights_WithTheGpo()
    {
        var (harness, _, sysvol) = await CreateAsync();
        using var review = await RunAsync(harness, sysvol);
        var path = Path.Combine(Path.GetTempPath(), $"admng-u4c-{Guid.NewGuid():N}.csv");
        try
        {
            harness.Files.NextSavePath = path;
            await review.Privileges.ExportCsvCommand.ExecuteAsync(null);
            var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
            var line = Assert.Single(lines, l => l.Contains("Stanisław Łęcki", StringComparison.Ordinal));
            Assert.Contains("High: Local right from a group policy: Backup Operators: GPO \"\"Serwery – Administratorzy lokalni\"\": Backup Operators (Stanisław Łęcki → GG_Kopie_Zapasowe)", line, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SysvolUnavailable_TheTabSaysWhy_TheOtherSourcesStillRun()
    {
        var (harness, _, sysvol) = await CreateAsync(Polish);
        sysvol.Unavailable = SysvolUnavailableReason.DistinguishedNameLogon;
        using var review = await RunAsync(harness, sysvol);
        var tab = review.Privileges;

        Assert.Null(tab.Error);
        Assert.Equal(0, tab.SourceOptions.Single(o => o.Source == PrivilegeSource.GroupPolicy).Count);
        Assert.Contains(tab.Items, r => r.Name == "Henryk Żuławski" && r.Level == PrivilegeLevel.Tier0);
        Assert.Equal(
            "Uprawnienia lokalne z GPO nie zostały sprawdzone: sesja używa prostego wiązania z nazwą wyróżniającą (DN), której nie można użyć "
            + "do zalogowania się do udziału SYSVOL na dc01.contoso.test. Połącz się kontem Windows albo nazwą w postaci DOMENA\\użytkownik lub UPN.",
            tab.NoticeText);
        Assert.Empty(sysvol.Reads);

        // The next run with SYSVOL available clears the notice.
        sysvol.Unavailable = null;
        await review.RunCommand.ExecuteAsync(null);
        Assert.DoesNotContain("SYSVOL", tab.NoticeText, StringComparison.Ordinal);
        Assert.Equal(7, tab.SourceOptions.Single(o => o.Source == PrivilegeSource.GroupPolicy).Count);
    }

    [Fact]
    public async Task Sysvol_IsReadOnTheDcOfTheWindowsSession()
    {
        var harness = await B2Harness.CreateOnDomainControllerAsync("dc02.contoso.test");
        var sample = harness.Builder.AddPrivilegedAccessSample(harness.Company);
        var sysvol = new FakeSysvolFileReader();
        harness.Builder.AddGroupPolicySample(harness.Company, sample, sysvol);
        using var review = await RunAsync(harness, sysvol);

        Assert.Null(review.Privileges.Error);
        Assert.Equal(["dc02.contoso.test"], sysvol.OpenedOn);
        Assert.Equal(0, sysvol.OpenConnections);
    }

    private static async Task<(B2Harness Harness, GroupPolicySample Gpo, FakeSysvolFileReader Sysvol)> CreateAsync(CultureInfo? culture = null)
    {
        var harness = await B2Harness.CreateAsync(culture);
        var sample = harness.Builder.AddPrivilegedAccessSample(harness.Company);
        var sysvol = new FakeSysvolFileReader();
        var gpo = harness.Builder.AddGroupPolicySample(harness.Company, sample, sysvol);
        return (harness, gpo, sysvol);
    }

    private static async Task<AccountReviewViewModel> RunAsync(B2Harness harness, FakeSysvolFileReader sysvol)
    {
        var factory = new DialogViewModelFactory(
            harness.Services, harness.Engines, harness.Views, harness.AuditLog, new Core.Audit.AuditLogExporter(new CsvCodec()), harness.PowerShell, harness.Theme,
            grantSources: [new GroupPolicyGrantSource(sysvol)]);
        var review = factory.CreateAccountReview();
        await review.InitializeAsync();
        review.SelectedTabIndex = AccountReviewViewModel.PrivilegesTab;
        await review.RunCommand.ExecuteAsync(null);
        return review;
    }

    private static PrivilegedAccountRowViewModel Row(PrivilegedAccessViewModel tab, string name) => Assert.Single(tab.Items, r => r.Name == name);

    private static IReadOnlyList<string> Names(PrivilegedAccessViewModel tab) =>
        [.. tab.Items.Select(r => r.Name).Order(StringComparer.Create(Polish, ignoreCase: true))];
}
