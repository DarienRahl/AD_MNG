using System.Globalization;
using AdMng.Core.Audit;
using AdMng.Core.Csv;
using AdMng.Core.PrivilegedAccess;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.AccountReview;

/// <summary>
/// The "Uprawnienia" tab with the ACL delegations source (README U4b) registered: the source offered and counted, the
/// findings in Polish (the object whose permissions hold the right, the rights, the path, the scope), the deep scan
/// setting saved and used by the next run.
/// </summary>
public class PrivilegedAccessAclTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public async Task AclSource_IsOffered_AndItsFindingsAreShownInPolish()
    {
        var scenario = await AclScenario.CreateAsync(Polish);
        using var review = await scenario.RunAsync();
        var tab = review.Privileges;

        Assert.Null(tab.Error);
        Assert.True(tab.HasAclSource);
        var source = Assert.Single(tab.SourceOptions, o => o.Source == PrivilegeSource.DirectoryAcl);
        Assert.Equal("Delegacje w AD", source.Title);
        Assert.True(source.Count >= 10);

        // Henryk: reset password on the users of OU=Firma through GG_Helpdesk (the group he can be removed from).
        var henryk = Row(tab, "Henryk Żuławski");
        Assert.Contains("Delegacje w AD", henryk[PrivilegedAccessColumns.Sources], StringComparison.Ordinal);
        tab.SelectedItem = henryk;
        var reset = Assert.Single(tab.Findings, f => f.Finding.Values.Contains("ResetPassword:user"));
        Assert.Equal("Uprawnienie w zabezpieczeniach obiektu Firma", reset.Title);
        Assert.Equal("Resetowanie hasła (user)", reset.ValuesText);
        Assert.Equal("Henryk Żuławski → GG_Helpdesk", reset.PathText);
        Assert.Equal("contoso.test/Firma", reset.ScopeText);
        Assert.Equal("Wysoki", reset.LevelText);
        Assert.Equal("Delegacje w AD", reset.SourceText);
        Assert.True(reset.CanRemoveFromGroup);
        var members = Assert.Single(tab.Findings, f => f.Finding.Values.Contains("WriteMember:group"));
        Assert.Equal("Henryk Żuławski → GG_Helpdesk → GG_Wsparcie_IT", members.PathText);
        Assert.Equal("Zmiana członków grup (group)", members.ValuesText);

        // A GPO is named by its display name; DCSync on the domain head by the domain's DNS name.
        tab.SelectedItem = Row(tab, "Ewa Błaszczyk");
        var gpo = Assert.Single(tab.Findings, f => f.Finding.ScopeDn == scenario.Acl.DefaultDomainPolicy);
        Assert.Equal("Uprawnienie w zabezpieczeniach obiektu Default Domain Policy", gpo.Title);
        Assert.Equal("Zapis wszystkich właściwości", gpo.ValuesText);
        Assert.Equal("Tier 0", gpo.LevelText);
        tab.SelectedItem = Row(tab, "Usługa SQL");
        var dcsync = Assert.Single(tab.Findings, f => f.Finding.Source == PrivilegeSource.DirectoryAcl);
        Assert.Equal("Uprawnienie w zabezpieczeniach obiektu contoso.test", dcsync.Title);
        Assert.Equal("Replikowanie wszystkich zmian katalogu (DCSync), Replikowanie zmian katalogu", dcsync.ValuesText);
        Assert.Equal(PrivilegeLevel.Tier0, Row(tab, "Usługa SQL").Level);

        // Halina has no group and no setting: the right of CN=System, found where it arrives (CN=Policies), lists her.
        var halina = Row(tab, "Halina Źrebiec");
        Assert.Equal("Wysoki", halina[PrivilegedAccessColumns.Level]);
        Assert.Equal("Delegacje w AD", halina[PrivilegedAccessColumns.Sources]);
        Assert.Equal(
            "Wysoki: Uprawnienie w zabezpieczeniach obiektu Policies: Pełna kontrola",
            halina[PrivilegedAccessColumns.Details]);

        // The source filter: only accounts with a delegation.
        foreach (var option in tab.SourceOptions.Where(o => o.Source != PrivilegeSource.DirectoryAcl))
        {
            option.IsActive = false;
        }

        Assert.Equal(source.Count, tab.Items.Count);
        Assert.All(tab.Items, r => Assert.Contains(r.Facts.Findings, f => f.Source == PrivilegeSource.DirectoryAcl));
        Assert.DoesNotContain(tab.Items, r => r.Name == "Krystyna Ślusarczyk");
    }

    [Fact]
    public async Task DeepScan_IsASavedSetting_AndAddsTheAclsOfSingleAccounts()
    {
        var scenario = await AclScenario.CreateAsync();
        using var review = scenario.Factory.CreateAccountReview();
        await review.InitializeAsync();
        review.SelectedTabIndex = AccountReviewViewModel.PrivilegesTab;
        var tab = review.Privileges;
        Assert.False(tab.DeepAclScan);

        await review.RunCommand.ExecuteAsync(null);
        Assert.DoesNotContain(tab.Items, r => r.Name == "Krystyna Ślusarczyk");

        tab.DeepAclScan = true;
        Assert.True(scenario.Harness.Settings.Current.AccountReview.Privileges.DeepAclScan);
        await review.RunCommand.ExecuteAsync(null);

        var krystyna = Row(tab, "Krystyna Ślusarczyk");
        Assert.Equal(PrivilegeLevel.Tier0, krystyna.Level);
        tab.SelectedItem = krystyna;
        var reset = Assert.Single(tab.Findings, f => f.Finding.Source == PrivilegeSource.DirectoryAcl);
        Assert.Equal("Right in the permissions of Jan Kowalski", reset.Title);
        Assert.Equal("Reset password", reset.ValuesText);
        Assert.Equal("contoso.test/Firma/Pracownicy/IT/Jan Kowalski", reset.ScopeText);

        // Saved without any directory data; a new window starts with it.
        Assert.DoesNotContain("Krystyna", JsonSettingsStore.Serialize(scenario.Harness.Settings.Current), StringComparison.Ordinal);
        using var next = scenario.Factory.CreateAccountReview();
        Assert.True(next.Privileges.DeepAclScan);
    }

    [Fact]
    public async Task WithoutTheAclSource_ItsSettingIsHidden_AndTheSourceIsNotOffered()
    {
        var harness = await B2Harness.CreateAsync();
        using var review = harness.Factory.CreateAccountReview();

        Assert.False(review.Privileges.HasAclSource);
        Assert.DoesNotContain(review.Privileges.SourceOptions, o => o.Source == PrivilegeSource.DirectoryAcl);
    }

    [Fact]
    public void Formatter_ShowsOtherValuesAsTheyAre()
    {
        var formatter = new PrivilegedAccessFormatter(new ResxLocalizer(Polish), "contoso.test");

        Assert.Equal("Tworzenie obiektów (computer)", formatter.AclRightText("CreateChild:computer"));
        Assert.Equal("Odczyt hasła LAPS (ms-Mcs-AdmPwd)", formatter.AclRightText("ReadLapsPassword:ms-Mcs-AdmPwd"));
        Assert.Equal("Właściciel", formatter.AclRightText("Owner"));
        Assert.Equal("S-1-5-21-1-2-3-1105", formatter.AclRightText("S-1-5-21-1-2-3-1105"));
    }

    private static PrivilegedAccountRowViewModel Row(PrivilegedAccessViewModel tab, string name) => Assert.Single(tab.Items, r => r.Name == name);

    /// <summary>The sample company with the privileged and the ACL samples; a factory with the ACL source registered.</summary>
    private sealed class AclScenario
    {
        private AclScenario(B2Harness harness)
        {
            Harness = harness;
            Privileged = harness.Builder.AddPrivilegedAccessSample(harness.Company);
            Acl = harness.Builder.AddAclDelegationSample(harness.Company, Privileged);
            Factory = new DialogViewModelFactory(
                harness.Services, harness.Engines, harness.Views, harness.AuditLog, new AuditLogExporter(new CsvCodec()), harness.PowerShell, harness.Theme,
                grantSources: [new DirectoryAclGrantSource()]);
        }

        public B2Harness Harness { get; }

        public PrivilegedSample Privileged { get; }

        public AclSample Acl { get; }

        public DialogViewModelFactory Factory { get; }

        public static async Task<AclScenario> CreateAsync(CultureInfo? culture = null) => new(await B2Harness.CreateAsync(culture));

        public async Task<AccountReviewViewModel> RunAsync()
        {
            var review = Factory.CreateAccountReview();
            await review.InitializeAsync();
            review.SelectedTabIndex = AccountReviewViewModel.PrivilegesTab;
            await review.RunCommand.ExecuteAsync(null);
            return review;
        }
    }
}
