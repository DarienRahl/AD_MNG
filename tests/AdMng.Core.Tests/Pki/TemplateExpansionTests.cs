using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Pki;
using AdMng.Core.PrivilegedAccess;
using AdMng.Core.Schema;
using AdMng.Testing;

namespace AdMng.Core.Tests.Pki;

/// <summary>
/// 2.3 of docs/dev/p-certificate-services.md: the trustee level, then the one account pass – counts per right and trustee
/// (enabled and disabled apart), the first 20 accounts, memoisation per distinct token, Tier 0 accounts, other-domain
/// trustees unresolved, progress, cancellation and 100 000 generated accounts within a time budget; the trustee level of
/// the PKI sample.
/// </summary>
public class TemplateExpansionTests
{
    private const string ForeignDomain = "S-1-5-21-1111111111-2222222222-3333333333";
    private static readonly Guid Enroll = FakeDirectoryBuilder.AclGuids.CertificateEnrollment;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheAccountPass_CountsTheAccountsOfEachRight_ThroughEachTrustee_HonouringDenies()
    {
        var scenario = await Scenario.CreateAsync();
        var s = scenario.Builder;
        var company = scenario.Company;
        var templates = new[]
        {
            // Łukasz is denied before GG_IT is allowed: only Jan enrolls.
            scenario.Template("Szablon-Działu-IT", s.SidOf(company.GroupIt),
                Ace(FakeAce.AccessDeniedObject, s.SidOf(company.LukaszWisniewski)), Ace(FakeAce.AccessAllowedObject, s.SidOf(company.GroupIt))),
            scenario.Template("Szablon-Księgowości", s.SidOf(company.GroupIt), Ace(FakeAce.AccessAllowedObject, s.SidOf(company.GroupAccounting))),
        };

        var expansion = await scenario.ExpandAsync(templates);

        var it = expansion.Templates["szablon-działu-it"];
        var enroll = Total(it, TemplateRight.Enroll);
        Assert.Equal((1, 0, PrincipalClass.Other), (enroll.AccountCount, enroll.DisabledCount, enroll.HighestClass));
        Assert.Equal(["Jan Kowalski"], enroll.FirstAccounts.Select(a => a.Name));
        var trustee = Assert.Single(it.Trustees, t => t.Right == TemplateRight.Enroll);
        Assert.Equal((s.SidOf(company.GroupIt), "GG_IT", 1, 0, true), (trustee.Sid, trustee.Name, trustee.AccountCount, trustee.DisabledCount, trustee.IsResolved));
        Assert.Null(Total(it, TemplateRight.WriteDacl).HighestClass);
        Assert.Equal(0, Total(it, TemplateRight.WriteDacl).AccountCount);

        // Zbigniew is disabled: counted apart and named after the enabled accounts.
        var accounting = Total(expansion.Templates["Szablon-Księgowości"], TemplateRight.Enroll);
        Assert.Equal((2, 1), (accounting.AccountCount, accounting.DisabledCount));
        Assert.Equal(["Małgorzata Wójcik", "Grażyna Dąbrowska", "Zbigniew Kaźmierczak"], accounting.FirstAccounts.Select(a => a.Name));
        Assert.Equal([false, false, true], accounting.FirstAccounts.Select(a => a.IsDisabled));

        // The owner (GG_IT) holds Owner through its members; every right of the enum has a total.
        var owner = Total(it, TemplateRight.Owner);
        Assert.Equal((2, PrincipalClass.Other), (owner.AccountCount, owner.HighestClass));
        Assert.Equal(Enum.GetValues<TemplateRight>(), it.Totals.Select(t => t.Right));
        Assert.Empty(expansion.MappedAccounts);
        Assert.Empty(expansion.Notices);
    }

    [Fact]
    public async Task TheFirstAccounts_AreCappedAt20_EnabledOnesFirst_TheDisabledCountedApart()
    {
        var scenario = await Scenario.CreateAsync(extraUsers: 25);
        var template = scenario.Template("Użytkownik-Domeny", scenario.DomainAdmins, Ace(FakeAce.AccessAllowedObject, scenario.DomainUsers));

        var expansion = await scenario.ExpandAsync([template]);

        var total = Total(expansion.Templates[template.Cn], TemplateRight.Enroll);
        Assert.Equal(20, total.FirstAccounts.Count);
        Assert.DoesNotContain(total.FirstAccounts, a => a.IsDisabled);
        Assert.Equal(PrincipalClass.Broad, total.HighestClass);

        // Administrator, the eight employees (Zbigniew disabled), Guest and krbtgt (disabled), the 25 extra users.
        Assert.Equal((33, 3), (total.AccountCount, total.DisabledCount));
        var users = Assert.Single(expansion.Templates[template.Cn].Trustees, t => t.Right == TemplateRight.Enroll);
        Assert.Equal((33, 3, 20), (users.AccountCount, users.DisabledCount, users.FirstAccounts.Count));
    }

    [Fact]
    public async Task TheAccountPass_IsOneSearch_AndDecidesOncePerDistinctToken()
    {
        var scenario = await Scenario.CreateAsync(extraUsers: 25);
        var s = scenario.Builder;
        var company = scenario.Company;
        var templates = new[]
        {
            scenario.Template("Szablon-Działu-IT", s.SidOf(company.GroupSales),
                Ace(FakeAce.AccessDeniedObject, s.SidOf(company.LukaszWisniewski)), Ace(FakeAce.AccessAllowedObject, s.SidOf(company.GroupIt))),
            scenario.Template("Szablon-Księgowości", s.SidOf(company.GroupSales), Ace(FakeAce.AccessAllowedObject, s.SidOf(company.GroupAccounting))),
            scenario.Template("Użytkownik-Domeny", s.SidOf(company.GroupSales), Ace(FakeAce.AccessAllowedObject, scenario.DomainUsers)),
        };
        s.Session.ClearOperations();

        var expansion = await scenario.ExpandAsync(templates);

        var search = Assert.Single(s.Session.Operations, o => o.Kind == FakeOperationKind.Search).Search!;
        Assert.Equal(PrivilegedAccessScanner.AccountFilter, search.Filter);
        Assert.Equal(PkiAttributes.AccountAttributes, search.Attributes);
        Assert.Equal(SearchScope.Subtree, search.Scope);

        // Keys = token ∩ {GG_Sprzedaz, Łukasz, GG_IT, GG_Ksiegowosc, Domain Users}: {IT, DU}, {IT, Łukasz, DU},
        // {Księgowość, DU}, {Sprzedaż, DU}, {DU} (Administrator, Guest, krbtgt, the extra users), {} (computers).
        Assert.Equal(40, expansion.AccountsScanned);
        Assert.Equal(6, expansion.DistinctTokens);
    }

    [Fact]
    public async Task AUserNamedDirectly_WhoReachesTier0_IsTier0_AfterThePass()
    {
        var scenario = await Scenario.CreateAsync(janIsDomainAdmin: true);
        var jan = scenario.Builder.SidOf(scenario.Company.JanKowalski);
        var template = scenario.Template("Logowanie-Administratorów", scenario.DomainAdmins, Ace(FakeAce.AccessAllowedObject, jan));

        Assert.Equal(PrincipalClass.Other, Assert.Single(TemplateRightsEvaluator.TrusteeLevel([template], scenario.Types, scenario.Classifier)[0].Trustees,
            t => t.Right == TemplateRight.Enroll).Class);
        var expansion = await scenario.ExpandAsync([template]);

        var trustee = Assert.Single(expansion.Templates[template.Cn].Trustees, t => t.Right == TemplateRight.Enroll);
        Assert.Equal((PrincipalClass.Tier0, "Jan Kowalski", 1), (trustee.Class, trustee.Name, trustee.AccountCount));
        Assert.Equal(PrincipalClass.Tier0, expansion.Classifier!.Classify(jan));
        Assert.Contains(scenario.Builder.SidOf(scenario.Builder.AdministratorDn), expansion.Classifier.Tier0Accounts);
        Assert.DoesNotContain(scenario.Builder.SidOf(scenario.Company.LukaszWisniewski), expansion.Classifier.Tier0Accounts);
    }

    [Fact]
    public async Task WritableDomainControllers_HoldEnterpriseDomainControllers_AndNetworkIsInEveryToken()
    {
        var scenario = await Scenario.CreateAsync();
        var builder = scenario.Builder;
        builder.AddComputer("RODC01", builder.DomainControllersDn, new Dictionary<string, object?>
        {
            [Attr.UserAccountControl] = (int)(UserAccountControl.WorkstationTrustAccount | UserAccountControl.PartialSecretsAccount),
            [Attr.PrimaryGroupId] = WellKnownRids.ReadOnlyDomainControllers,
        });
        var dcs = scenario.Template("Kontroler-Domeny", scenario.DomainAdmins, Ace(FakeAce.AccessAllowedObject, "S-1-5-9"));
        var rodcs = scenario.Template("Kontroler-Tylko-Do-Odczytu", scenario.DomainAdmins,
            Ace(FakeAce.AccessAllowedObject, $"{builder.DomainSid}-{WellKnownRids.EnterpriseReadOnlyDomainControllers}"));
        var network = scenario.Template("Dowolny-Z-Sieci", scenario.DomainAdmins, Ace(FakeAce.AccessAllowedObject, "S-1-5-2"));

        var expansion = await scenario.ExpandAsync([dcs, rodcs, network]);

        // The writable DC carries Enterprise Domain Controllers, the RODC the root's Enterprise Read-only DCs instead.
        var controllers = Total(expansion.Templates[dcs.Cn], TemplateRight.Enroll);
        Assert.Equal(["DC01"], controllers.FirstAccounts.Select(a => a.Name));
        Assert.Equal(PrincipalClass.Tier0, controllers.HighestClass);
        Assert.Equal(["RODC01"], Total(expansion.Templates[rodcs.Cn], TemplateRight.Enroll).FirstAccounts.Select(a => a.Name));
        Assert.Contains(builder.SidOf(DistinguishedName.Parse(builder.DomainControllersDn).Child("CN", "RODC01").ToString()), expansion.Classifier!.Tier0Accounts);
        var everyone = Total(expansion.Templates[network.Cn], TemplateRight.Enroll);
        Assert.Equal(expansion.AccountsScanned, everyone.AccountCount + everyone.DisabledCount);
        Assert.Equal(PrincipalClass.Broad, everyone.HighestClass);
    }

    [Fact]
    public async Task TrusteesOfAnotherDomain_AreUnresolved_WithANotice_UnlessTheyAreTier0()
    {
        var scenario = await Scenario.CreateAsync();
        var foreign = scenario.Template("Szablon-Partnera", scenario.DomainAdmins, Ace(FakeAce.AccessAllowedObject, ForeignDomain + "-1107"));
        var foreignAdmins = scenario.Template("Szablon-Lasu", scenario.DomainAdmins, Ace(FakeAce.AccessAllowedObject, ForeignDomain + "-519"));
        var unreadable = foreign with { Cn = "Bez-Deskryptora", Dn = foreign.Dn.Replace("Szablon-Partnera", "Bez-Deskryptora", StringComparison.Ordinal), SecurityDescriptor = null };

        var expansion = await scenario.ExpandAsync([foreign, foreignAdmins, unreadable]);

        var other = Assert.Single(expansion.Templates[foreign.Cn].Trustees, t => t.Right == TemplateRight.Enroll);
        Assert.Equal((PrincipalClass.Other, false, -1, -1), (other.Class, other.IsResolved, other.AccountCount, other.DisabledCount));
        Assert.Equal(PrincipalClass.Other, Total(expansion.Templates[foreign.Cn], TemplateRight.Enroll).HighestClass);
        var admins = Assert.Single(expansion.Templates[foreignAdmins.Cn].Trustees, t => t.Right == TemplateRight.Enroll);
        Assert.Equal((PrincipalClass.Tier0, false), (admins.Class, admins.IsResolved));

        var notice = Assert.Single(expansion.Notices);
        Assert.Equal((PkiReadNoticeCode.TrusteeUnresolved, foreign.Dn, foreign.ObjectGuid), (notice.Code, notice.ObjectDn, notice.ObjectGuid));
        Assert.False(expansion.Templates["Bez-Deskryptora"].IsEvaluated);
        Assert.Empty(expansion.Templates["Bez-Deskryptora"].Totals);
    }

    [Fact]
    public async Task Progress_IsReportedEvery500Accounts_AndAtTheEnd()
    {
        var session = new GeneratedAccountsSession(1234);
        var progress = new SynchronousProgress();

        var expansion = await TemplateRightsEvaluator.ExpandAsync(session, session.Templates, session.Context(), progress, Ct);

        Assert.Equal([500, 1000, 1234], progress.Reports);
        Assert.Equal(1234, expansion.AccountsScanned);
        Assert.Equal(1, session.Searches);
    }

    [Fact]
    public async Task Cancellation_StopsThePass()
    {
        var session = new GeneratedAccountsSession(5000);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var progress = new SynchronousProgress(onReport: n =>
        {
            if (n >= 1000)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TemplateRightsEvaluator.ExpandAsync(session, session.Templates, session.Context(), progress, cts.Token));
        Assert.True(session.Streamed < 5000);
        Assert.Equal([500, 1000], progress.Reports);
    }

    [Fact]
    public async Task OneHundredThousandAccounts_AreExpandedWithinTheTimeBudget()
    {
        var session = new GeneratedAccountsSession(100_000);
        var watch = Stopwatch.StartNew();

        var expansion = await TemplateRightsEvaluator.ExpandAsync(session, session.Templates, session.Context(), null, Ct);

        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"{watch.Elapsed.TotalSeconds:F1} s");
        Assert.Equal(100_000, expansion.AccountsScanned);
        Assert.Equal(3, expansion.DistinctTokens);

        // Every tenth account is disabled; group 1 is denied on the team template; group 0 is nested in Domain Admins.
        var all = Total(expansion.Templates["Wszyscy-Użytkownicy"], TemplateRight.Enroll);
        Assert.Equal((90_000, 10_000, 20), (all.AccountCount, all.DisabledCount, all.FirstAccounts.Count));
        // Group 0 (i % 3 == 0: 33 334 accounts, 3 334 of them disabled) enrolls; group 1 is denied first; group 2 has nothing.
        var team = Total(expansion.Templates["Zespoły"], TemplateRight.Enroll);
        Assert.Equal((30_000, 3_334), (team.AccountCount, team.DisabledCount));
        Assert.Contains(GeneratedAccountsSession.Sid(0), expansion.Classifier!.Tier0Accounts);
        Assert.Equal(33_334, expansion.Classifier.Tier0Accounts.Count);
    }

    [Fact]
    public async Task TheTrusteeLevelOfTheSample_FlagsTheBroadAndOtherTrusteesOfTheVulnerableTemplates()
    {
        var sample = await PublicKeyScenario.CreateAsync();

        var rights = TemplateRightsEvaluator.TrusteeLevel(sample.Templates, sample.Types, sample.Classifier).ToDictionary(r => r.TemplateCn, StringComparer.Ordinal);

        Assert.Equal(PublicKeyTemplates.All.Order(StringComparer.Ordinal), rights.Keys.Order(StringComparer.Ordinal));
        Assert.All(rights.Values, r =>
        {
            Assert.True(r.IsEvaluated);
            var owner = Assert.Single(r.Trustees, t => t.Right == TemplateRight.Owner);
            Assert.Equal((sample.DomainAdmins, PrincipalClass.Tier0), (owner.Sid, owner.Class));
        });

        // Only Enroll / AutoEnroll ever reach a non-Tier 0 trustee on the defaults (User: Domain Users, Machine: Domain Computers).
        foreach (var cn in PublicKeyTemplates.Defaults)
        {
            Assert.DoesNotContain(rights[cn].Trustees, t => t.Class != PrincipalClass.Tier0 && t.Right is not (TemplateRight.Enroll or TemplateRight.AutoEnroll));
        }

        Assert.Equal(PrincipalClass.Broad, Enroller(rights[PublicKeyTemplates.User]));
        Assert.Equal(PrincipalClass.Broad, Enroller(rights[PublicKeyTemplates.Machine]));
        Assert.Null(Enroller(rights[PublicKeyTemplates.DomainController]));
        Assert.Null(Enroller(rights[PublicKeyTemplates.KerberosAuthentication]));
        Assert.Null(Enroller(rights[PublicKeyTemplates.SubCa]));

        Assert.Equal(PrincipalClass.Broad, Enroller(rights[PublicKeyTemplates.Esc1]));
        Assert.Equal(PrincipalClass.Other, Enroller(rights[PublicKeyTemplates.Esc2]));
        Assert.Null(Enroller(rights[PublicKeyTemplates.Esc2Safe]));
        Assert.Equal(PrincipalClass.Broad, Enroller(rights[PublicKeyTemplates.Esc17]));
        Assert.Null(Enroller(rights[PublicKeyTemplates.Esc17Safe]));

        // ESC4: Authenticated Users may write the name flag; the look-alike only Enterprise Admins.
        var writer = Assert.Single(rights[PublicKeyTemplates.Esc4].Trustees, t => t.Right == TemplateRight.WriteSensitiveProperty);
        Assert.Equal(("S-1-5-11", PrincipalClass.Broad, PkiAttributes.CertificateNameFlag), (writer.Sid, writer.Class, writer.Attribute));
        var safeWriter = Assert.Single(rights[PublicKeyTemplates.Esc4Safe].Trustees, t => t.Right == TemplateRight.WriteSensitiveProperty);
        Assert.Equal((sample.Pki.EnterpriseAdminsSid, PrincipalClass.Tier0), (safeWriter.Sid, safeWriter.Class));
    }

    [Fact(Skip = "P1 wave 1 merge")]
    public async Task TheExpansionOfTheSample_CountsTheEnrollees_AndKeepsTheMappedAccounts()
    {
        // Needs AltSecurityIdentity.Parse (C4).
        var sample = await PublicKeyScenario.CreateAsync();

        var expansion = await TemplateRightsEvaluator.ExpandAsync(sample.Builder.Session, sample.Templates,
            new PkiExpansionContext(sample.Groups, sample.Types, sample.Classifier, sample.Options), null, Ct);

        var esc2 = Total(expansion.Templates[PublicKeyTemplates.Esc2], TemplateRight.Enroll);
        Assert.Contains(esc2.FirstAccounts, a => a.Name == "Jan Kowalski");
        Assert.Contains(esc2.FirstAccounts, a => a.Name == "Łukasz Wiśniewski");
        var mapped = expansion.MappedAccounts.ToDictionary(a => a.Dn, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(4, mapped.Count);
        Assert.True(mapped[sample.Builder.AdministratorDn].IsTier0);
        Assert.Equal(MappingStrength.Weak, Assert.Single(mapped[sample.Builder.AdministratorDn].Mappings).Strength);
        Assert.False(mapped[sample.Company.LukaszWisniewski].IsTier0);
        Assert.Equal(MappingStrength.Strong, Assert.Single(mapped[sample.Company.JanKowalski].Mappings).Strength);
        Assert.Equal(MappingStrength.NotCertificate, Assert.Single(mapped[sample.Company.PawelLewandowski].Mappings).Strength);
        Assert.Empty(expansion.Notices);
    }

    private static TemplateRightTotal Total(TemplateRights rights, TemplateRight right) => rights.Totals.Single(t => t.Right == right);

    /// <summary>The most dangerous class of a non-Tier 0 Enroll trustee, or null.</summary>
    private static PrincipalClass? Enroller(TemplateRights rights) =>
        rights.Trustees.Where(t => t.Right == TemplateRight.Enroll && t.Class != PrincipalClass.Tier0)
            .Select(t => (PrincipalClass?)t.Class)
            .OrderBy(c => c == PrincipalClass.Broad ? 0 : 1)
            .FirstOrDefault();

    private static FakeAce Ace(byte type, string sid) => new(type, 0, 0x100, sid, Enroll);

    /// <summary>The sample company with hand-built templates, the graph and the classifier of the window.</summary>
    private sealed class Scenario
    {
        private Scenario(FakeDirectoryBuilder builder, SampleCompany company)
        {
            Builder = builder;
            Company = company;
        }

        public FakeDirectoryBuilder Builder { get; }

        public SampleCompany Company { get; }

        public string DomainUsers => $"{Builder.DomainSid}-{WellKnownRids.DomainUsers}";

        public string DomainAdmins => $"{Builder.DomainSid}-{WellKnownRids.DomainAdmins}";

        public AclObjectTypes Types { get; private set; } = new(null, null);

        public GroupGraph Groups { get; private set; } = new([]);

        public PrivilegedAccessOptions Options { get; private set; } = new();

        public PrincipalClassifier Classifier { get; private set; } = null!;

        public static async Task<Scenario> CreateAsync(int extraUsers = 0, bool janIsDomainAdmin = false)
        {
            var builder = new FakeDirectoryBuilder();
            var company = builder.AddSampleCompany();
            var bulk = extraUsers > 0 ? builder.AddOu("Konta-Masowe", company.CompanyOu) : null;
            for (var i = 1; i <= extraUsers; i++)
            {
                builder.AddUser("Użytkownik", $"Testowy{i:D2}", bulk);
            }

            if (janIsDomainAdmin)
            {
                builder.AddMembers(builder.DomainAdminsDn, company.JanKowalski);
            }

            var scenario = new Scenario(builder, company);
            scenario.Options = new PrivilegedAccessOptions { DomainSid = builder.DomainSid, RootDomainSid = builder.DomainSid };
            scenario.Groups = await GroupGraph.ReadAsync(builder.Session, null, 500, Ct);
            scenario.Types = await AclObjectTypes.ReadAsync(builder.Session, 500, Ct);
            scenario.Classifier = new PrincipalClassifier(builder.DomainSid, builder.DomainSid, scenario.Options, scenario.Groups, []);
            return scenario;
        }

        public CertificateTemplate Template(string cn, string owner, params FakeAce[] aces) => new()
        {
            Dn = DistinguishedName.Parse(Builder.CertificateTemplatesDn).Child("CN", cn).ToString(),
            Cn = cn,
            ObjectGuid = Guid.CreateVersion7(),
            SecurityDescriptor = FakeSecurityDescriptor.Build(owner, owner, aces),
        };

        public Task<PkiEnrollmentExpansion> ExpandAsync(IReadOnlyList<CertificateTemplate> templates) =>
            TemplateRightsEvaluator.ExpandAsync(Builder.Session, templates, new PkiExpansionContext(Groups, Types, Classifier, Options), null, Ct);
    }

    /// <summary>The PKI sample: its templates read like the overview reader reads them, the graph, the classifier.</summary>
    private sealed record PublicKeyScenario(
        FakeDirectoryBuilder Builder,
        SampleCompany Company,
        PublicKeySample Pki,
        IReadOnlyList<CertificateTemplate> Templates,
        AclObjectTypes Types,
        GroupGraph Groups,
        PrivilegedAccessOptions Options,
        PrincipalClassifier Classifier)
    {
        public string DomainAdmins => $"{Builder.DomainSid}-{WellKnownRids.DomainAdmins}";

        public static async Task<PublicKeyScenario> CreateAsync()
        {
            var builder = new FakeDirectoryBuilder();
            var company = builder.AddSampleCompany();
            var pki = builder.AddPublicKeySample(company);
            var templates = new List<CertificateTemplate>();
            await foreach (var obj in builder.Session.SearchAsync(new DirectorySearch
            {
                BaseDn = builder.CertificateTemplatesDn,
                Scope = SearchScope.OneLevel,
                Filter = LdapFilter.Equal(Attr.ObjectClass, PkiClasses.CertificateTemplate),
                Attributes = PkiAttributes.TemplateAttributes,
                ReadSecurityDescriptor = true,
            }, Ct))
            {
                templates.Add(CertificateTemplate.Of(obj));
            }

            var options = new PrivilegedAccessOptions { DomainSid = builder.DomainSid, RootDomainSid = builder.DomainSid };
            var groups = await GroupGraph.ReadAsync(builder.Session, null, 500, Ct);
            var types = await AclObjectTypes.ReadAsync(builder.Session, 500, Ct);
            var classifier = new PrincipalClassifier(builder.DomainSid, builder.DomainSid, options, groups,
                [builder.SidOf(pki.RootCaComputer), builder.SidOf(pki.IssuingCaComputer)]);
            return new PublicKeyScenario(builder, company, pki, templates, types, groups, options, classifier);
        }

        public override string ToString() => nameof(PublicKeyScenario);
    }

    /// <summary>Reports synchronously (<see cref="Progress{T}"/> posts to the thread pool).</summary>
    private sealed class SynchronousProgress(Action<int>? onReport = null) : IProgress<PkiExpansionProgress>
    {
        public List<int> Reports { get; } = [];

        public void Report(PkiExpansionProgress value)
        {
            Reports.Add(value.Accounts);
            onReport?.Invoke(value.Accounts);
        }
    }

    /// <summary>
    /// A session streaming generated user accounts (the account pass only searches): three security groups, the first
    /// nested in Domain Admins; account i is in group i % 3 and disabled when i % 10 == 0.
    /// </summary>
    private sealed class GeneratedAccountsSession(int count) : IDirectorySession
    {
        private const string Domain = FakeDirectorySession.DefaultDomainSid;
        private const string Nc = "DC=contoso,DC=test";
        private static readonly string[] Teams = [.. Enumerable.Range(0, 3).Select(i => $"CN=GG_Zespół_{i},OU=Grupy,{Nc}")];

        public SessionInfo Info { get; } = FakeDirectorySession.CreateInfo();

        public ISchemaCatalog Schema { get; } = FakeSchema.Create();

        public int Searches { get; private set; }

        public int Streamed { get; private set; }

        public IReadOnlyList<CertificateTemplate> Templates { get; } =
        [
            Template("Wszyscy-Użytkownicy", FakeSecurityDescriptor.Build(Domain + "-512", null, Ace(FakeAce.AccessAllowedObject, Domain + "-513"))),
            Template("Zespoły", FakeSecurityDescriptor.Build(Domain + "-512", null,
                Ace(FakeAce.AccessDeniedObject, Domain + "-2001"), Ace(FakeAce.AccessAllowedObject, Domain + "-2000"), Ace(FakeAce.AccessAllowedObject, Domain + "-2001"))),
        ];

        public static string Sid(int index) => string.Create(CultureInfo.InvariantCulture, $"{Domain}-{10_000 + index}");

        public PkiExpansionContext Context()
        {
            var groups = new GroupGraph(
            [
                Group($"CN=Domain Admins,CN=Users,{Nc}", Domain + "-512", null),
                Group(Teams[0], Domain + "-2000", $"CN=Domain Admins,CN=Users,{Nc}"),
                Group(Teams[1], Domain + "-2001", null),
                Group(Teams[2], Domain + "-2002", null),
            ]);
            var options = new PrivilegedAccessOptions { DomainSid = Domain, RootDomainSid = Domain, PageSize = 1000 };
            return new PkiExpansionContext(groups, new AclObjectTypes(null, null), new PrincipalClassifier(Domain, Domain, options, groups, []), options);
        }

        public async IAsyncEnumerable<DirectoryObject> SearchAsync(DirectorySearch search, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Searches++;
            await Task.Yield();
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Streamed++;
                yield return new DirectoryObject(string.Create(CultureInfo.InvariantCulture, $"CN=Użytkownik {i:D6},OU=Masowi,{Nc}"),
                [
                    new AttributeValues(Attr.ObjectClass, ["top", "person", "organizationalPerson", "user"]),
                    new AttributeValues(Attr.ObjectSid, [AdConvert.SidToBytes(Sid(i))]),
                    new AttributeValues(Attr.PrimaryGroupId, ["513"]),
                    new AttributeValues(Attr.MemberOf, [Teams[i % 3]]),
                    new AttributeValues(Attr.UserAccountControl, [(i % 10 == 0 ? 0x202 : 0x200).ToString(CultureInfo.InvariantCulture)]),
                    new AttributeValues(Attr.Name, [string.Create(CultureInfo.InvariantCulture, $"Użytkownik {i:D6}")]),
                ]);
            }
        }

        public Task<DirectoryObject?> GetAsync(string distinguishedName, IReadOnlyList<string> attributes, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncEnumerable<string> GetAllValuesAsync(string distinguishedName, string attribute, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<VirtualListPage> GetVirtualListPageAsync(DirectorySearch search, VirtualListRequest window, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<EffectivePermissions> GetEffectivePermissionsAsync(string distinguishedName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ModifyAsync(string distinguishedName, IReadOnlyList<AttributeModification> modifications, ModifyOptions? options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddAsync(string distinguishedName, IReadOnlyDictionary<string, IReadOnlyList<object>> attributes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string distinguishedName, bool subtree, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> MoveAsync(string distinguishedName, string? newParentDn, string? newRdn, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ResetPasswordAsync(string distinguishedName, string newPassword, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RestoreDeletedAsync(string deletedObjectDn, string targetDn, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAsyncDisposable?> WatchAsync(string containerDn, Action<string> onChanged, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static DirectoryObject Group(string dn, string sid, string? memberOf) => new(dn,
        [
            new AttributeValues(Attr.ObjectClass, ["top", "group"]),
            new AttributeValues(Attr.ObjectSid, [AdConvert.SidToBytes(sid)]),
            new AttributeValues(Attr.GroupType, [unchecked((int)0x80000002).ToString(CultureInfo.InvariantCulture)]),
            new AttributeValues(Attr.SamAccountName, [DistinguishedName.Parse(dn).Name]),
            new AttributeValues(Attr.MemberOf, memberOf is null ? [] : [memberOf]),
        ]);

        private static CertificateTemplate Template(string cn, byte[] descriptor) => new()
        {
            Dn = $"CN={cn},CN=Certificate Templates,CN=Public Key Services,CN=Services,CN=Configuration,{Nc}",
            Cn = cn,
            ObjectGuid = Guid.CreateVersion7(),
            SecurityDescriptor = descriptor,
        };
    }
}
