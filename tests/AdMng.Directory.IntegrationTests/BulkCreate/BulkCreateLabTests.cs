using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Security;
using AdMng.Directory.IntegrationTests.Infrastructure;
using AdMng.Directory.IntegrationTests.Integration;

namespace AdMng.Directory.IntegrationTests.BulkCreate;

/// <summary>
/// The bulk creation wizard's planner against the Samba lab (docs/dev/phase3-bulk-create.md 2.11): twenty users with a counter
/// pattern (a pre-created number skipped), their attributes, enabled with must-change and in a lab group, then the undo; a
/// value taken between the computation and the execution; a group, a contact and a computer; a shared password the DC
/// rejects (the one-object canary stops, the retry with a new password succeeds); the implicit UPN and the Recycle Bin seen
/// by the planner. Every object lives in an OU <c>F3T-…</c> below this run's OU; the session is LDAPS (passwords).
/// Skipped without <c>ADMNG_TEST_LDAP_SERVER</c>.
/// </summary>
public sealed class BulkCreateLabTests(LabFixture lab)
{
    private static readonly BatchOptions Options = new() { InitialRetryDelay = TimeSpan.FromMilliseconds(50), MaxDegreeOfParallelism = 2 };

    private static readonly (string GivenName, string Surname)[] People =
    [
        ("Łucja", "Żak"), ("Grzegorz", "Bąk"), ("Zofia", "Wiśniewska"), ("Paweł", "Kaźmierczak"), ("Małgorzata", "Wójcik"),
        ("Jędrzej", "Gołąb"), ("Anna", "Ślęzak"), ("Michał", "Dąbrowski"), ("Ewa", "Źródłowska"), ("Bartłomiej", "Kozłowski"),
        ("Żaneta", "Szymańska"), ("Łukasz", "Wiśniewski"), ("Agnieszka", "Mączyńska"), ("Tomasz", "Pęczek"), ("Katarzyna", "Łęcka"),
        ("Piotr", "Zieliński"), ("Joanna", "Bielecka"), ("Krzysztof", "Ćwik"), ("Magdalena", "Nowak-Kowalska"), ("Wojciech", "Śliwa"),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BulkCreatePlanner Planner() => new(new PasswordGenerator(), TimeProvider.System);

    private static InputTable Table(IEnumerable<string[]> rows)
    {
        var list = rows.ToList();
        var width = list.Max(r => r.Length);
        var columns = Enumerable.Range(0, width).Select(i => new TableColumn(i, TableTextReader.ColumnLetter(i), string.Empty)).ToList();
        return new InputTable(columns, [.. list.Select((r, i) => new TableRow(i + 1, r))], HasHeader: false, TableSource.Typed, []);
    }

    private static IReadOnlyList<ColumnMapping> NameMappings { get; } =
    [
        new(0, ColumnTargetKind.Attribute, Attr.GivenName),
        new(1, ColumnTargetKind.Attribute, Attr.Sn),
    ];

    private static string Prefix() => "kd" + ComputerLabTests.Suffix()[..4].ToLowerInvariant();

    private static BulkCreateInput Input(string ou, InputTable table, IReadOnlyList<AttributeRule> rules, IReadOnlyList<ColumnMapping>? mappings = null,
        BulkAccountOptions? options = null, ObjectKind kind = ObjectKind.User) => new()
    {
        ContainerDn = ou,
        Table = table,
        Mappings = mappings ?? NameMappings,
        Rules = new BulkCreateRules
        {
            Kind = kind,
            Attributes = rules,
            UpnSuffix = LabSettings.DomainDns,
            MailDomain = LabSettings.DomainDns,
            CounterMode = CounterMode.FillGaps,
        },
        Options = options ?? new BulkAccountOptions { MatchAttributes = [] },
    };

    private static IReadOnlyList<AttributeRule> CounterRules(string prefix) =>
    [
        new(Attr.SamAccountName, prefix + "{$nr:1:2}"),
        new(Attr.UserPrincipalName, "{sAMAccountName}"),
        new(Attr.Mail, "{sAMAccountName}@{$mailDomain}"),
        new(Attr.Cn, "{givenName} {sn}"),
        new(Attr.DisplayName, "{givenName} {sn}"),
    ];

    private static Task<BatchResult> ExecuteAsync(IDirectorySession session, ChangePlan plan) =>
        new BatchExecutor(new InMemoryAuditLog(), TimeProvider.System).ExecuteAsync(session, plan, Options, null, Ct);

    private static async Task<string> CreateGroupAsync(IDirectorySession session, string ou, string name, GroupScope scope)
    {
        var dn = DistinguishedName.Parse(ou).Child("CN", name).ToString();
        await session.AddAsync(dn, new Dictionary<string, IReadOnlyList<object>>
        {
            [Attr.ObjectClass] = ["top", ObjectClasses.Group],
            [Attr.SamAccountName] = [name],
            [Attr.GroupType] = [GroupTypes.Compose(scope, security: true).ToString(CultureInfo.InvariantCulture)],
        }, Ct);
        return dn;
    }

    private static async Task UndoAsync(IDirectorySession session, BulkCreatePlanner planner, params BatchResult[] results)
    {
        var created = new List<CreatedObject>();
        foreach (var result in results)
        {
            created.AddRange(await planner.ReadCreatedAsync(session, result, Ct));
        }

        var undo = await planner.PlanUndoAsync(session, created, "Cofnięcie", Ct);
        Assert.Equal(created.Count, undo.ExecutableCount);
        var undone = await ExecuteAsync(session, undo);
        Assert.Equal(created.Count, undone.SucceededCount);
        foreach (var item in created)
        {
            Assert.Null(await session.GetAsync(item.DistinguishedName, [Attr.ObjectClass], Ct));
        }
    }

    [Fact]
    public async Task TwentyUsers_WithACounter_AreCreated_Verified_AndUndone()
    {
        LabSettings.RequireLab();
        await using var secure = await LabFixture.ConnectAsync(LabSettings.Tls(LabSettings.AdminUser), LabSettings.AdminPassword, cancellationToken: Ct);
        var ou = await lab.CreateOuAsync(TestNames.Unique("F3T-Konta"));
        var prefix = Prefix();
        await lab.CreateUserAsync(ou, "Zajęty Numer", new Dictionary<string, string> { [Attr.SamAccountName] = prefix + "03" });
        var group = await CreateGroupAsync(secure, ou, "GG_F3T_" + prefix, GroupScope.Global);
        var planner = Planner();
        var options = new BulkAccountOptions { MatchAttributes = [], GroupDns = [group] };

        var computation = await planner.ComputeAsync(secure, Input(ou, Table(People.Select(p => new[] { p.GivenName, p.Surname })), CounterRules(prefix), options: options),
            new BulkCreateLookups(), null, Ct);

        Assert.False(computation.HasRunErrors, string.Join(", ", computation.Issues.Select(i => i.Code)));
        Assert.Equal(UniquenessScope.ForestSingleDomain, computation.Scope);
        var expected = Enumerable.Range(1, 21).Where(n => n != 3).Select(n => prefix + n.ToString("D2", CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(expected, computation.Rows.Select(r => r.Value(Attr.SamAccountName)));
        Assert.DoesNotContain(computation.Rows, r => r.Status == BulkRowStatus.Error);

        var plan = planner.BuildPlan(computation, null, "Utworzenie kont (20)");
        var result = await ExecuteAsync(secure, plan);

        Assert.Equal(20, result.SucceededCount);
        for (var i = 0; i < People.Length; i++)
        {
            var row = computation.Rows[i];
            var created = (await secure.GetAsync(row.DistinguishedName!,
                [Attr.SamAccountName, Attr.UserPrincipalName, Attr.Mail, Attr.GivenName, Attr.Sn, Attr.DisplayName, Attr.UserAccountControl, Attr.PwdLastSet, Attr.MemberOf], Ct))!;
            Assert.Equal(expected[i], created.GetString(Attr.SamAccountName), ignoreCase: true);
            Assert.Equal($"{expected[i]}@{LabSettings.DomainDns}", created.GetString(Attr.UserPrincipalName), ignoreCase: true);
            Assert.Equal($"{expected[i]}@{LabSettings.DomainDns}", created.GetString(Attr.Mail), ignoreCase: true);
            Assert.Equal(People[i].GivenName, created.GetString(Attr.GivenName));
            Assert.Equal(People[i].Surname, created.GetString(Attr.Sn));
            Assert.Equal($"{People[i].GivenName} {People[i].Surname}", created.GetString(Attr.DisplayName));
            Assert.Equal(512, created.GetInt32(Attr.UserAccountControl)! & 0x2FF);
            Assert.Equal(0, created.GetInt64(Attr.PwdLastSet));
            Assert.Contains(group, created.GetStrings(Attr.MemberOf), StringComparer.OrdinalIgnoreCase);
        }

        await UndoAsync(secure, planner, result);
    }

    [Fact]
    public async Task AValueTakenAfterTheComputation_FailsThatObjectOnly()
    {
        LabSettings.RequireLab();
        await using var secure = await LabFixture.ConnectAsync(LabSettings.Tls(LabSettings.AdminUser), LabSettings.AdminPassword, cancellationToken: Ct);
        var ou = await lab.CreateOuAsync(TestNames.Unique("F3T-Wyscig"));
        var other = await lab.CreateOuAsync(TestNames.Unique("F3T-Inne"));
        var prefix = Prefix();
        var planner = Planner();
        var computation = await planner.ComputeAsync(secure, Input(ou, Table(People.Take(3).Select(p => new[] { p.GivenName, p.Surname })), CounterRules(prefix)),
            new BulkCreateLookups(), null, Ct);
        var plan = planner.BuildPlan(computation, null, "Utworzenie kont (3)");
        await lab.CreateUserAsync(other, "Szybszy Użytkownik", new Dictionary<string, string> { [Attr.SamAccountName] = computation.Rows[1].Value(Attr.SamAccountName)! });

        var result = await ExecuteAsync(secure, plan);

        Assert.Equal([ObjectResultStatus.Succeeded, ObjectResultStatus.Failed, ObjectResultStatus.Succeeded], result.Objects.Select(o => o.Status));
        Assert.Contains(result.Objects[1].Changes[0].ErrorKind, new DirectoryErrorKind?[] { DirectoryErrorKind.AlreadyExists, DirectoryErrorKind.ConstraintViolation });
        await UndoAsync(secure, planner, result);
    }

    [Fact]
    public async Task AGroupAContactAndAComputer()
    {
        LabSettings.RequireLab();
        await using var secure = await LabFixture.ConnectAsync(LabSettings.Tls(LabSettings.AdminUser), LabSettings.AdminPassword, cancellationToken: Ct);
        var ou = await lab.CreateOuAsync(TestNames.Unique("F3T-Rodzaje"));
        var suffix = ComputerLabTests.Suffix()[..4];
        var global = await CreateGroupAsync(secure, ou, "GG_F3T_" + suffix, GroupScope.Global);
        var universal = await CreateGroupAsync(secure, ou, "UG_F3T_" + suffix, GroupScope.Universal);
        var planner = Planner();
        var results = new List<BatchResult>();

        // A Domain Local group: the Global option group refuses it (left out), the Universal one is not allowed either.
        var groups = await planner.ComputeAsync(secure, Input(ou, Table([["DL_F3T_" + suffix]]), [new(Attr.SamAccountName, "{cn}")],
            [new ColumnMapping(0, ColumnTargetKind.Attribute, Attr.Cn)],
            new BulkAccountOptions { MatchAttributes = [], GroupScope = GroupScope.DomainLocal, GroupDns = [global, universal] }, ObjectKind.Group), new BulkCreateLookups(), null, Ct);
        Assert.Empty(groups.Rows[0].GroupDns);
        Assert.Equal(2, groups.Rows[0].Issues.Count(i => i.Code == BulkCreateIssueCodes.GroupScopeNotAllowed));
        results.Add(await ExecuteAsync(secure, planner.BuildPlan(groups, null, "Utworzenie grup")));

        var contacts = await planner.ComputeAsync(secure, Input(ou, Table([["Anna", "Kontaktowa"]]),
            [new(Attr.Cn, "{givenName} {sn}"), new(Attr.Mail, "anna.kontaktowa." + suffix.ToLowerInvariant() + "@firma.pl")], kind: ObjectKind.Contact), new BulkCreateLookups(), null, Ct);
        results.Add(await ExecuteAsync(secure, planner.BuildPlan(contacts, null, "Utworzenie kontaktów")));

        var name = "F3T-PC-" + suffix;
        var computers = await planner.ComputeAsync(secure, Input(ou, Table([[name.ToLowerInvariant()]]), [new(Attr.SamAccountName, "{cn}")],
            [new ColumnMapping(0, ColumnTargetKind.Attribute, Attr.Cn)], kind: ObjectKind.Computer), new BulkCreateLookups(), null, Ct);
        results.Add(await ExecuteAsync(secure, planner.BuildPlan(computers, null, "Utworzenie komputerów")));

        Assert.All(results, r => Assert.Equal(1, r.SucceededCount));
        var group = (await secure.GetAsync(groups.Rows[0].DistinguishedName!, [Attr.GroupType], Ct))!;
        Assert.Equal(GroupScope.DomainLocal, GroupTypes.GetScope(group.GetInt32(Attr.GroupType)!.Value));
        var computer = (await secure.GetAsync(computers.Rows[0].DistinguishedName!, [Attr.SamAccountName, Attr.UserAccountControl, Attr.Cn], Ct))!;
        Assert.Equal(name + "$", computer.GetString(Attr.SamAccountName), ignoreCase: true);
        Assert.Equal(name, computer.GetString(Attr.Cn), ignoreCase: true);
        Assert.Equal(4096, computer.GetInt32(Attr.UserAccountControl)! & 0x11FF);
        Assert.NotNull(await secure.GetAsync(contacts.Rows[0].DistinguishedName!, [Attr.Mail], Ct));

        await UndoAsync(secure, planner, [.. results]);
    }

    [Fact]
    public async Task ASharedPasswordTheDcRejects_TheCanaryStops_TheRetryWithANewPasswordSucceeds()
    {
        LabSettings.RequireLab();
        await using var secure = await LabFixture.ConnectAsync(LabSettings.Tls(LabSettings.AdminUser), LabSettings.AdminPassword, cancellationToken: Ct);
        var ou = await lab.CreateOuAsync(TestNames.Unique("F3T-Haslo"));
        var group = await CreateGroupAsync(secure, ou, "GG_F3T_" + ComputerLabTests.Suffix()[..4], GroupScope.Global);
        var prefix = Prefix();
        var planner = Planner();
        var options = new BulkAccountOptions { MatchAttributes = [], PasswordMode = InitialPasswordMode.SameForAll, GroupDns = [group] };
        var computation = await planner.ComputeAsync(secure, Input(ou, Table(People.Take(3).Select(p => new[] { p.GivenName, p.Surname })), CounterRules(prefix), options: options),
            new BulkCreateLookups(), null, Ct);

        // Too short for the domain: blocked before anything is sent.
        var tooShort = planner.BuildPlan(computation, "a1", "t");
        Assert.Equal(0, tooShort.ExecutableCount);

        // The canary (the first object alone) with a password that contains the account name: the DC's complexity refuses it.
        var plan = planner.BuildPlan(computation, computation.Rows[0].Value(Attr.SamAccountName) + "Aa1!xyz", "Utworzenie kont (3)");
        var canary = await ExecuteAsync(secure, plan with { Objects = [plan.Objects[0]] });
        var first = Assert.Single(canary.Objects);
        Assert.SkipWhen(first.Status == ObjectResultStatus.Succeeded, "The lab DC accepted a password with the account name (no complexity check).");
        Assert.Equal(ObjectResultStatus.PartiallySucceeded, first.Status);
        Assert.Equal(DirectoryErrorKind.PasswordPolicy, first.Changes[1].ErrorKind);
        var disabled = (await secure.GetAsync(first.FinalDn, [Attr.UserAccountControl, Attr.MemberOf], Ct))!;
        Assert.Equal(2, disabled.GetInt32(Attr.UserAccountControl)! & 2);
        Assert.Empty(disabled.GetStrings(Attr.MemberOf));

        var accumulated = BulkCreateResults.Merge(null, canary);
        var retry = planner.BuildRetryPlan(computation, accumulated, TestNames.Password(), "Ponowienie");
        var second = await ExecuteAsync(secure, retry);

        Assert.Equal(ObjectResultStatus.Succeeded, Assert.Single(second.Objects).Status);
        Assert.DoesNotContain(second.Objects[0].Changes, c => c.Change.Action == ChangeAction.Create);
        var enabled = (await secure.GetAsync(first.FinalDn, [Attr.UserAccountControl, Attr.MemberOf], Ct))!;
        Assert.Equal(0, enabled.GetInt32(Attr.UserAccountControl)! & 2);
        Assert.Contains(group, enabled.GetStrings(Attr.MemberOf), StringComparer.OrdinalIgnoreCase);
        await UndoAsync(secure, planner, canary, second);
    }

    [Fact]
    public async Task ThePlanner_SeesTheImplicitUpn_AndTheRecycleBin()
    {
        LabSettings.RequireLab();
        await using var secure = await LabFixture.ConnectAsync(LabSettings.Tls(LabSettings.AdminUser), LabSettings.AdminPassword, cancellationToken: Ct);
        var ou = await lab.CreateOuAsync(TestNames.Unique("F3T-Nazwy"));
        var login = "f3t" + ComputerLabTests.Suffix().ToLowerInvariant();
        await lab.CreateUserAsync(ou, "Właściciel Upn", new Dictionary<string, string> { [Attr.UserPrincipalName] = $"{login}@{LabSettings.DomainDns}" });
        var deleted = await lab.CreateUserAsync(ou, "Usunięta Osoba");
        await secure.DeleteAsync(deleted, subtree: false, Ct);
        var rules = new AttributeRule[] { new(Attr.SamAccountName, "{$column:C}"), new(Attr.Cn, "{givenName} {sn}"), new(Attr.UserPrincipalName, "{sAMAccountName}") };
        var input = Input(ou, Table([["Usunięta", "Osoba", login]]), rules) with { Rules = Input(ou, Table([["x"]]), rules).Rules with { Collisions = CollisionStrategy.MarkError } };

        var computation = await Planner().ComputeAsync(secure, input, new BulkCreateLookups(), null, Ct);

        var row = Assert.Single(computation.Rows);
        Assert.Contains(row.Issues, i => i.Code == BulkCreateIssueCodes.ImplicitUpnClash && i.Attribute == Attr.SamAccountName);
        if (secure.Info.RecycleBinEnabled)
        {
            Assert.Contains(row.Issues, i => i.Code == BulkCreateIssueCodes.NameInRecycleBin && i.Attribute == Attr.Cn);
        }
    }
}
