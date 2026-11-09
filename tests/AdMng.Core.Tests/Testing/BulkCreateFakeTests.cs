using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Correspondence.Import;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Messaging.Html;
using AdMng.Testing;
using AdMng.Testing.BulkCreate;

namespace AdMng.Core.Tests.Testing;

/// <summary>
/// The shared F3 test files of the contract commit (docs/dev/phase3-bulk-create.md 2.13): the SAM rules and the channel
/// encryption of the fake, the bulk-create sample, the opt-in schema, the fake global catalog and the Excel samples.
/// </summary>
public sealed class BulkCreateFakeTests
{
    private const string UserClass = "user";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (FakeDirectoryBuilder Builder, BulkCreateSample Sample) Create(bool samRules = true)
    {
        var builder = new FakeDirectoryBuilder(schema: FakeSchema.Create(bulkCreate: true));
        var company = builder.AddSampleCompany();
        return (builder, builder.AddBulkCreateSample(company, samRules));
    }

    private static Dictionary<string, IReadOnlyList<object>> User(string sam, string? upn = null, int uac = 514, params (string Name, object Value)[] extra)
    {
        var attributes = new Dictionary<string, IReadOnlyList<object>>(StringComparer.OrdinalIgnoreCase)
        {
            [Attr.ObjectClass] = [UserClass],
            [Attr.SamAccountName] = [sam],
            [Attr.UserAccountControl] = [uac.ToString(System.Globalization.CultureInfo.InvariantCulture)],
        };
        if (upn is not null)
        {
            attributes[Attr.UserPrincipalName] = [upn];
        }

        foreach (var (name, value) in extra)
        {
            attributes[name] = [value];
        }

        return attributes;
    }

    [Fact]
    public async Task SamRules_RefuseADuplicateLogonName_OnAddAndModify()
    {
        var (builder, sample) = Create();
        var session = builder.Session;

        var add = await Assert.ThrowsAsync<DirectoryException>(() =>
            session.AddAsync($"CN=Ktoś Nowy,{sample.NewEmployeesOu}", User("KD201"), Ct));
        Assert.Equal(DirectoryErrorKind.AlreadyExists, add.Kind);
        Assert.Equal(FakeDirectorySession.Win32UserExists, add.Win32Error);

        await session.AddAsync($"CN=Łucja Żak,{sample.NewEmployeesOu}", User("kd208", "kd208@contoso.com"), Ct);
        var modify = await Assert.ThrowsAsync<DirectoryException>(() => session.ModifyAsync(
            $"CN=Łucja Żak,{sample.NewEmployeesOu}",
            [AttributeModification.Replace(Attr.SamAccountName, "kd203")],
            null,
            Ct));
        Assert.Equal(DirectoryErrorKind.AlreadyExists, modify.Kind);
    }

    [Fact]
    public async Task SamRules_IgnoreDeletedObjects_ButRefuseADuplicateUpn()
    {
        var (builder, sample) = Create();
        var session = builder.Session;

        // kd207 is in the Recycle Bin: its logon name is free for AD.
        await session.AddAsync($"CN=Łucja Żak,{sample.NewEmployeesOu}", User("kd207"), Ct);

        var error = await Assert.ThrowsAsync<DirectoryException>(() =>
            session.AddAsync($"CN=Grzegorz Bąk,{sample.NewEmployeesOu}", User("kd209", "KD201@contoso.com"), Ct));
        Assert.Equal(DirectoryErrorKind.ConstraintViolation, error.Kind);
        Assert.Equal(FakeDirectorySession.Win32UpnNotUnique, error.Win32Error);
    }

    [Fact]
    public async Task SamRules_RefuseAnRdnValueOfAnotherRdnType()
    {
        var (builder, sample) = Create();

        var error = await Assert.ThrowsAsync<DirectoryException>(() => builder.Session.AddAsync(
            $"CN=Księgowość,{sample.NewEmployeesOu}",
            new Dictionary<string, IReadOnlyList<object>>
            {
                [Attr.ObjectClass] = [ObjectClasses.Group],
                [Attr.SamAccountName] = ["Ksiegowosc-Nowi"],
                [Attr.GroupType] = [GroupTypes.Compose(GroupScope.Global, security: true).ToString(System.Globalization.CultureInfo.InvariantCulture)],
            },
            Ct));

        Assert.Equal(DirectoryErrorKind.AlreadyExists, error.Kind);
        Assert.Equal(FakeDirectorySession.Win32NameExists, error.Win32Error);
    }

    [Fact]
    public async Task SamRules_RefuseAnEnabledUserWithoutAPassword_ADoubleDollar_AndASecondDescription()
    {
        var (builder, sample) = Create();
        var session = builder.Session;

        var enabled = await Assert.ThrowsAsync<DirectoryException>(() => session.AddAsync($"CN=A,{sample.NewEmployeesOu}", User("kd300", uac: 512), Ct));
        Assert.Equal(DirectoryErrorKind.PasswordPolicy, enabled.Kind);
        Assert.Equal(FakeDirectorySession.Win32PasswordRestriction, enabled.Win32Error);

        var computer = new Dictionary<string, IReadOnlyList<object>>
        {
            [Attr.ObjectClass] = [ObjectClasses.Computer],
            [Attr.SamAccountName] = ["PC01$$"],
            [Attr.UserAccountControl] = ["4098"],
        };
        var dollar = await Assert.ThrowsAsync<DirectoryException>(() => session.AddAsync($"CN=PC01,{sample.NewEmployeesOu}", computer, Ct));
        Assert.Equal(DirectoryErrorKind.ConstraintViolation, dollar.Kind);

        var twoDescriptions = User("kd301");
        twoDescriptions["description"] = ["pierwszy", "drugi"];
        var description = await Assert.ThrowsAsync<DirectoryException>(() => session.AddAsync($"CN=B,{sample.NewEmployeesOu}", twoDescriptions, Ct));
        Assert.Equal(DirectoryErrorKind.ConstraintViolation, description.Kind);

        // A disabled user, a computer with one '$' and one description are fine.
        await session.AddAsync($"CN=C,{sample.NewEmployeesOu}", User("kd302"), Ct);
        await session.AddAsync($"CN=PC02,{sample.NewEmployeesOu}", new Dictionary<string, IReadOnlyList<object>>(computer) { [Attr.SamAccountName] = ["PC02$"] }, Ct);
    }

    [Fact]
    public async Task SamRules_AreOffByDefault()
    {
        var builder = new FakeDirectoryBuilder();
        builder.AddSampleCompany();

        await builder.Session.AddAsync($"CN=Ktoś,{builder.UsersDn}", User("jan.kowalski", uac: 512), Ct);

        Assert.False(builder.Session.SamRules);
        Assert.False(Create(samRules: false).Builder.Session.SamRules);
    }

    [Fact]
    public async Task UnencryptedChannel_RefusesPasswords_AndIsReported()
    {
        var (builder, sample) = Create();
        var session = builder.Session;
        session.IsChannelEncrypted = false;

        Assert.False(session.Info.IsChannelEncrypted);
        var reset = await Assert.ThrowsAsync<DirectoryException>(() => session.ResetPasswordAsync(sample.Kd201, "Zażółć!Gęślą2026", Ct));
        Assert.Equal(DirectoryErrorKind.StrongAuthRequired, reset.Kind);
        var withPassword = User("kd310");
        withPassword[Attr.UnicodePwd] = [Encoding.Unicode.GetBytes("\"Zażółć!Gęślą2026\"")];
        var add = await Assert.ThrowsAsync<DirectoryException>(() => session.AddAsync($"CN=D,{sample.NewEmployeesOu}", withPassword, Ct));
        Assert.Equal(DirectoryErrorKind.StrongAuthRequired, add.Kind);

        session.IsChannelEncrypted = true;
        await session.ResetPasswordAsync(sample.Kd201, "Zażółć!Gęślą2026", Ct);
        Assert.True(session.Info.IsChannelEncrypted);
    }

    [Fact]
    public async Task UnencryptedChannel_RefusesWhatTheRealSessionRefuses()
    {
        var (builder, sample) = Create();
        var session = builder.Session;
        session.IsChannelEncrypted = false;

        // A Modify of either password attribute (before the fake's own "use ResetPasswordAsync" refusal).
        var modify = await Assert.ThrowsAsync<DirectoryException>(() => session.ModifyAsync(
            sample.Kd201, [AttributeModification.Replace(Attr.UnicodePwd, "x")], null, Ct));
        Assert.Equal(DirectoryErrorKind.StrongAuthRequired, modify.Kind);
        var userPassword = await Assert.ThrowsAsync<DirectoryException>(() => session.ModifyAsync(
            sample.Kd201, [AttributeModification.Replace("userPassword", "x")], null, Ct));
        Assert.Equal(DirectoryErrorKind.StrongAuthRequired, userPassword.Kind);

        // An Add with userPassword values is refused; an empty unicodePwd list is not sent, so it is not refused.
        var withUserPassword = User("kd311");
        withUserPassword["userPassword"] = ["Zażółć!Gęślą2026"];
        var add = await Assert.ThrowsAsync<DirectoryException>(() => session.AddAsync($"CN=E,{sample.NewEmployeesOu}", withUserPassword, Ct));
        Assert.Equal(DirectoryErrorKind.StrongAuthRequired, add.Kind);
        var emptyPassword = User("kd312");
        emptyPassword[Attr.UnicodePwd] = [];
        await session.AddAsync($"CN=F,{sample.NewEmployeesOu}", emptyPassword, Ct);

        // An enabled user whose unicodePwd list is empty has no password (SAM rule, not the channel).
        var enabledEmpty = User("kd313", uac: 512);
        enabledEmpty[Attr.UnicodePwd] = [];
        var enabled = await Assert.ThrowsAsync<DirectoryException>(() => session.AddAsync($"CN=G,{sample.NewEmployeesOu}", enabledEmpty, Ct));
        Assert.Equal(DirectoryErrorKind.PasswordPolicy, enabled.Kind);
    }

    [Fact]
    public async Task SamRules_ReadTheAttributesCaseInsensitively_AndCoverSubclassesOfUser()
    {
        var (builder, sample) = Create();
        var session = builder.Session;

        // Keys that differ only by case: ApplyAdd keeps the last non-empty list, the rules read the same value.
        var duplicate = new Dictionary<string, IReadOnlyList<object>>(StringComparer.Ordinal)
        {
            [Attr.ObjectClass] = [UserClass],
            ["samaccountname"] = ["kd320"],
            ["sAMAccountName"] = ["KD201"],
            [Attr.UserAccountControl] = ["514"],
        };
        var error = await Assert.ThrowsAsync<DirectoryException>(() => session.AddAsync($"CN=H,{sample.NewEmployeesOu}", duplicate, Ct));
        Assert.Equal(FakeDirectorySession.Win32UserExists, error.Win32Error);

        // inetOrgPerson is a user: an enabled one without a password and a second description are refused.
        var person = User("kd321", uac: 512);
        person[Attr.ObjectClass] = ["inetOrgPerson"];
        var enabled = await Assert.ThrowsAsync<DirectoryException>(() => session.AddAsync($"CN=I,{sample.NewEmployeesOu}", person, Ct));
        Assert.Equal(DirectoryErrorKind.PasswordPolicy, enabled.Kind);
        var described = User("kd322");
        described[Attr.ObjectClass] = ["inetOrgPerson"];
        described["description"] = ["pierwszy", "drugi"];
        var description = await Assert.ThrowsAsync<DirectoryException>(() => session.AddAsync($"CN=J,{sample.NewEmployeesOu}", described, Ct));
        Assert.Equal(DirectoryErrorKind.ConstraintViolation, description.Kind);
    }

    [Fact]
    public async Task CaseDuplicateKeys_StillAddWithTheSamRulesOff()
    {
        var builder = new FakeDirectoryBuilder();
        builder.AddSampleCompany();
        var attributes = new Dictionary<string, IReadOnlyList<object>>(StringComparer.Ordinal)
        {
            [Attr.ObjectClass] = [UserClass],
            [Attr.SamAccountName] = ["ktos"],
            ["department"] = ["Kadry"],
            ["Department"] = ["Księgowość"],
        };

        await builder.Session.AddAsync($"CN=Ktoś,{builder.UsersDn}", attributes, Ct);

        Assert.Equal(["Księgowość"], builder.Session.GetValues($"CN=Ktoś,{builder.UsersDn}", "department"));
    }

    [Fact]
    public async Task Sample_HasTheObjectsOf2_13()
    {
        var (builder, sample) = Create();
        var session = builder.Session;

        Assert.Equal(["kd201"], session.GetValues(sample.Kd201, Attr.SamAccountName));
        Assert.Equal(["kd207"], session.GetValues(sample.DeletedKd207, Attr.SamAccountName));
        Assert.Equal([sample.NewEmployeesOu], session.GetValues(sample.DeletedKd207, Attr.LastKnownParent));
        Assert.Equal(["Jan Kowalski"], session.GetValues(sample.DeletedJanKowalski, "msDS-LastKnownRDN"));
        Assert.Contains("smtp:jan.kowalski@contoso.test", session.GetValues(sample.ExternalContact, Attr.ProxyAddresses));
        Assert.Equal(["Piotr Zieliński"], session.GetValues(sample.PiotrZielinski2, Attr.DisplayName));
        Assert.Equal(["E-1001"], session.GetValues(sample.EwaWisniewska, Attr.EmployeeId));
        Assert.Equal([sample.PasswordSettings], session.GetValues(sample.GroupWithPso, "msDS-PSOApplied"));
        Assert.Equal(FakeDirectoryBuilder.BulkCreateUpnSuffixes, session.GetValues(builder.PartitionsDn, "uPNSuffixes"));
        Assert.Equal(["contoso.test"], session.GetValues(sample.DomainCrossRef, "dnsRoot"));
        Assert.Equal(["7"], session.GetValues(builder.DomainDn, "minPwdLength"));
        Assert.Equal([sample.RidSet], session.GetValues(builder.DomainControllerDn, "rIDSetReferences"));
        Assert.Empty(session.Operations);
        Assert.True(session.SamRules);

        var permissions = await session.GetEffectivePermissionsAsync(sample.BlockedOu, Ct);
        Assert.False(permissions.CanCreate(UserClass));
        Assert.True(permissions.CanCreate(ObjectClasses.Contact));
    }

    [Fact]
    public void Sample_AlsoWorksWithTheDefaultSchema()
    {
        var builder = new FakeDirectoryBuilder();
        var sample = builder.AddBulkCreateSample(builder.AddSampleCompany());

        Assert.Equal([sample.PasswordSettings], builder.Session.GetValues(sample.GroupWithPso, "msDS-PSOApplied"));
    }

    [Fact]
    public void BulkCreateSchema_IsOptIn()
    {
        var plain = FakeSchema.Create();
        var bulk = FakeSchema.Create(bulkCreate: true);

        Assert.Null(plain.FindAttribute("extensionAttribute5"));
        Assert.Null(plain.FindAttribute(FakeSchema.CustomBadgeAttribute));
        Assert.NotNull(bulk.FindAttribute("extensionAttribute15"));
        Assert.Contains(FakeSchema.CustomBadgeAttribute, bulk.GetAllowedAttributes(["top", "person", "organizationalPerson", UserClass]));
        Assert.Contains("ipPhone", bulk.GetAllowedAttributes(["top", "person", "organizationalPerson", ObjectClasses.Contact]));
        Assert.True(bulk.FindAttribute("msDS-PSOApplied")!.IsBackLink);
        Assert.Equal(plain.Attributes.Count + 15 + 14, bulk.Attributes.Count);
    }

    [Fact]
    public async Task GlobalCatalog_SearchesEveryDomain_WithTheEmptyBase()
    {
        var contoso = new FakeDirectoryBuilder();
        contoso.AddSampleCompany();
        var fabrikam = new FakeDirectoryBuilder("fabrikam.test", guidNamespace: 1, domainSid: "S-1-5-21-1004336348-1177238915-682003330");
        fabrikam.AddUser("Jan", "Kowalski", fabrikam.UsersDn);
        var gc = new FakeGlobalCatalogReader(contoso.Session, fabrikam.Session);
        var found = new List<string>();

        await foreach (var item in gc.SearchAsync(new DirectorySearch { BaseDn = string.Empty, Filter = LdapFilter.Equal(Attr.SamAccountName, "jan.kowalski") }, Ct))
        {
            found.Add(item.DistinguishedName);
        }

        Assert.True(gc.Info.IsGlobalCatalog);
        Assert.Equal(2, found.Count);
        Assert.Contains(found, dn => dn.EndsWith("DC=fabrikam,DC=test", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(await gc.GetAsync($"CN=Jan Kowalski,{fabrikam.UsersDn}", [Attr.SamAccountName], Ct));
        Assert.Equal(1, gc.SearchCount);
        Assert.IsNotAssignableFrom<IDirectorySession>(gc);
    }

    [Fact]
    public async Task GlobalCatalog_TheForestRootBase_ReachesTheChildDomains()
    {
        var root = new FakeDirectoryBuilder();
        root.AddSampleCompany();
        var child = new FakeDirectoryBuilder("oddzial.contoso.test", guidNamespace: 2, domainSid: "S-1-5-21-2004336348-1177238915-682003331");
        var childUser = child.AddUser("Jan", "Kowalski", child.UsersDn);
        var gc = new FakeGlobalCatalogReader(root.Session, child.Session);

        var subtree = await Dns(gc, new DirectorySearch { BaseDn = root.DomainDn, Filter = LdapFilter.Equal(Attr.SamAccountName, "jan.kowalski") });
        Assert.Equal(2, subtree.Count);
        Assert.Contains(childUser, subtree, StringComparer.OrdinalIgnoreCase);

        // One level below the root lists the child domain's naming context head; a child-domain DN is read from the child.
        var oneLevel = await Dns(gc, new DirectorySearch { BaseDn = root.DomainDn, Scope = SearchScope.OneLevel });
        Assert.Contains(child.DomainDn, oneLevel, StringComparer.OrdinalIgnoreCase);
        Assert.NotNull(await gc.GetAsync(childUser, [Attr.SamAccountName], Ct));
        Assert.Equal([childUser], await Dns(gc, new DirectorySearch { BaseDn = childUser, Scope = SearchScope.Base }));

        // A base in no domain is NoSuchObject; only the empty base is the whole catalog.
        var error = await Assert.ThrowsAsync<DirectoryException>(() => Dns(gc, new DirectorySearch { BaseDn = "DC=fabrikam,DC=test" }));
        Assert.Equal(DirectoryErrorKind.NoSuchObject, error.Kind);
    }

    [Fact]
    public async Task GlobalCatalog_ABaseScopeSearchOfTheEmptyBase_IsTheRootDse()
    {
        var root = new FakeDirectoryBuilder();
        root.AddSampleCompany();
        var gc = new FakeGlobalCatalogReader(root.Session, new FakeDirectoryBuilder("oddzial.contoso.test", guidNamespace: 2).Session);
        var found = new List<Core.Model.DirectoryObject>();

        await foreach (var item in gc.SearchAsync(new DirectorySearch { BaseDn = string.Empty, Scope = SearchScope.Base, Attributes = [Attr.DefaultNamingContext] }, Ct))
        {
            found.Add(item);
        }

        var rootDse = Assert.Single(found);
        Assert.Equal(string.Empty, rootDse.DistinguishedName);
        Assert.Equal([root.DomainDn], rootDse.GetStrings(Attr.DefaultNamingContext));
        Assert.NotNull(await gc.GetAsync(string.Empty, [Attr.DefaultNamingContext], Ct));
    }

    private static async Task<List<string>> Dns(FakeGlobalCatalogReader gc, DirectorySearch search)
    {
        var found = new List<string>();
        await foreach (var item in gc.SearchAsync(search, Ct))
        {
            found.Add(item.DistinguishedName);
        }

        return found;
    }

    [Fact]
    public void ExcelSamples_HaveTheQuirksTheirDocsName()
    {
        // QuirksTsv: 5 raw cells per line, D and E empty everywhere (header included) – dropped as trailing empty columns.
        var quirkLines = ExcelSamples.QuirksTsv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.All(quirkLines, line => Assert.Equal(5, line.Split('\t').Length));
        Assert.All(quirkLines, line => Assert.True(line.Split('\t')[3].Length == 0 && line.Split('\t')[4].Length == 0, line));
        Assert.Equal(ExcelSamples.QuirksRowCount + 2, quirkLines.Length - 1);   // the empty middle and last rows

        // RaggedTsv: a 5-cell header, data row 2 with 7 cells.
        var ragged = ExcelSamples.RaggedTsv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t')).ToList();
        Assert.Equal(5, ragged[0].Length);
        Assert.Equal(ExcelSamples.RaggedColumnCount, ragged[ExcelSamples.RaggedRowNumber].Length);
        Assert.Equal(ExcelSamples.RaggedColumnCount, ragged.Max(r => r.Length));

        var duplicate = ExcelSamples.DuplicateHeaderTsv.Split("\r\n")[0].Split('\t');
        Assert.Equal(2, duplicate.Count(h => h == "Telefon"));
        Assert.DoesNotContain('\n', ExcelSamples.CrOnlyTsv);
        Assert.Equal(3, ExcelSamples.CrOnlyTsv.Split('\r', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("Hasło startowe", ExcelSamples.SensitiveColumnsTsv, StringComparison.Ordinal);
        Assert.Contains("PESEL", ExcelSamples.SensitiveColumnsTsv, StringComparison.Ordinal);
        Assert.Contains(";'+48 600 100 201;", ExcelSamples.ProtectedCsvText, StringComparison.Ordinal);
        Assert.Contains("rowspan=\"2\"", ExcelSamples.MergedAndNestedHtml, StringComparison.Ordinal);
        Assert.Equal(3, ExcelSamples.MergedAndNestedHtml.Split("<table").Length - 1);
        Assert.DoesNotContain("<td colspan", ExcelSamples.LibreOfficeHtml.Split("\r\n").First(l => l.Contains("Imię", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public void ExcelSamplesRows_PairNamesByGender()
    {
        var rows = ExcelSamples.Rows(400).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l.Split('\t')).ToList();

        // Polish given names here end in -a exactly when they are female; -ski/-cki is male, -ska/-cka female.
        Assert.All(rows, r => Assert.False(r[0].EndsWith('a') && (r[1].EndsWith("ski", StringComparison.Ordinal) || r[1].EndsWith("cki", StringComparison.Ordinal)), $"{r[0]} {r[1]}"));
        Assert.All(rows, r => Assert.False(!r[0].EndsWith('a') && (r[1].EndsWith("ska", StringComparison.Ordinal) || r[1].EndsWith("cka", StringComparison.Ordinal)), $"{r[0]} {r[1]}"));
        Assert.Equal(400, rows.Select(r => $"{r[0]} {r[1]}").Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(400, rows.Select(r => r[2]).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ExcelSamples_AreWellFormed()
    {
        Assert.StartsWith("Telefon komórkowy\tImię\tNazwisko\tE-mail", ExcelSamples.PolishTsv, StringComparison.Ordinal);
        Assert.EndsWith("\r\n", ExcelSamples.PolishTsv, StringComparison.Ordinal);
        Assert.Equal(6, ExcelSamples.PolishTsv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("\"Społem\" PSS", ExcelSamples.QuotesTsv, StringComparison.Ordinal);
        Assert.Contains("#N/D!", ExcelSamples.ErrorValuesTsv, StringComparison.Ordinal);
        Assert.StartsWith("\uFEFF", ExcelSamples.QuirksTsv, StringComparison.Ordinal);
        Assert.Equal("\uFEFF", ExcelSamples.Bom);
        Assert.DoesNotContain('\r', ExcelSamples.LfOnlyTsv);
        Assert.DoesNotContain('\t', ExcelSamples.SingleColumnWebText);
        Assert.Equal(5001, ExcelSamples.Rows(5000).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("ł", ExcelSamples.Rows(30), StringComparison.Ordinal);
        Assert.Equal(ExcelSamples.Cp1250CsvText, Encoding.GetEncoding(1250).GetString(ExcelSamples.Cp1250Csv));
        Assert.Equal([0xFF, 0xFE], ExcelSamples.Utf16UnicodeText.Take(2));
    }

    [Fact]
    public void ScientificCfHtml_CarriesTheFullNumber()
    {
        var document = CfHtmlParser.Parse(ExcelSamples.ScientificCfHtml);

        Assert.Contains($"x:num=\"{ExcelSamples.ScientificFullValue}\"", document.Html, StringComparison.Ordinal);
        Assert.Contains("4,86E+11", ExcelSamples.ScientificTsv, StringComparison.Ordinal);
        Assert.Contains("mailto:", ExcelSamples.ExcelHtml, StringComparison.Ordinal);
        Assert.Contains("google-sheets-html-origin", ExcelSamples.GoogleSheetsHtml, StringComparison.Ordinal);
        Assert.Contains("colspan", ExcelSamples.LibreOfficeHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void MinimalXlsx_IsReadByTheU5Reader()
    {
        var bytes = ExcelSamples.MinimalXlsx([["Imię", "Nazwisko"], ["Łucja", "Żak"], ["Ukryty", "Wiersz"]], hiddenRows: [3], otherSheet: true);

        using var zip = SafeZip.Open(bytes);
        var workbook = XlsxReader.ReadSheets(zip);

        Assert.Equal(2, workbook.Sheets.Count);
        Assert.Equal(["Łucja", "Żak"], workbook.Sheets[0][1]);
        Assert.Equal(1, workbook.HiddenRows);
    }

    [Fact]
    public void MinimalXlsx_HiddenFirstSheet_AndNumberFormats_AreReadAsExcelShowsThem()
    {
        var bytes = ExcelSamples.MinimalXlsx(
            [["Imię", "Telefon komórkowy", "Kod pocztowy"], ["Łucja", "", ""]],
            hiddenFirstSheet: true,
            numbers: [("B2", "48600100201", ExcelSamples.XlsxPhoneFormat), ("C2", "2950", ExcelSamples.XlsxPostalCodeFormat)]);

        using var zip = SafeZip.Open(bytes);
        var workbook = XlsxReader.ReadSheets(zip);

        Assert.Equal(1, workbook.HiddenSheets);
        var sheet = Assert.Single(workbook.Sheets);
        Assert.Equal(["Imię", "Telefon komórkowy", "Kod pocztowy"], sheet[0]);
        Assert.Equal(["Łucja", "+48 600 100 201", "02-950"], sheet[1]);
    }
}
