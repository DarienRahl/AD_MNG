using System.Net;
using System.Reflection;
using AdMng.Core.Batch;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Core.Security;
using AdMng.Testing;
using AdMng.Testing.BulkCreate;

namespace AdMng.Core.Tests.Batch.Creation;

/// <summary>
/// The F3 contract commit (docs/dev/phase3-bulk-create.md 2.11, 2.12) on the Core side: every record of
/// <c>AdMng.Core.Batch.Creation</c> prints no value, the issue codes, the complete members (<see cref="InputTable"/>,
/// <see cref="CreationNames"/> limits and class chains, <see cref="TableTextReader.ColumnLetter"/>, the named blocked
/// attributes), the value-free PowerShell records and the Directory contract (the GC connect default, the session facts).
/// </summary>
public sealed class BulkCreateContractTests
{
    private static readonly Assembly Core = typeof(InputTable).Assembly;

    public static TheoryData<string> RecordNames()
    {
        var data = new TheoryData<string>();
        foreach (var type in BulkCreateRecordSamples.Records(Core, "AdMng.Core.Batch.Creation"))
        {
            data.Add(type.FullName!);
        }

        return data;
    }

    [Fact]
    public void TheContractRecords_AreFound()
    {
        var records = BulkCreateRecordSamples.Records(Core, "AdMng.Core.Batch.Creation").Select(t => t.Name).ToList();

        Assert.Contains(nameof(InputTable), records);
        Assert.Contains(nameof(TableRow), records);
        Assert.Contains(nameof(ColumnMapping), records);
        Assert.Contains(nameof(BulkCreateInput), records);
        Assert.Contains(nameof(BulkCreateComputation), records);
        Assert.Contains(nameof(PatternRow), records);
        Assert.Contains(nameof(CounterSpec), records);
        Assert.Contains(nameof(CreatedObject), records);
        Assert.True(records.Count >= 25, $"{records.Count} records");
    }

    [Theory]
    [MemberData(nameof(RecordNames))]
    public void EveryRecord_DeclaresAValueFreeToString(string typeName)
    {
        var type = Core.GetType(typeName, throwOnError: true)!;

        Assert.True(BulkCreateRecordSamples.DeclaresOwnToString(type), $"{type.Name} uses the compiler-generated ToString (it prints every member).");
        Assert.Equal([], BulkCreateRecordSamples.LeakedValues(type));
    }

    [Fact]
    public void TheChecks_CatchACompilerToString_AndALeakedValue()
    {
        Assert.True(BulkCreateRecordSamples.IsRecord(typeof(GeneratedToString)));
        Assert.False(BulkCreateRecordSamples.DeclaresOwnToString(typeof(GeneratedToString)));
        Assert.NotEmpty(BulkCreateRecordSamples.LeakedValues(typeof(GeneratedToString)));
        Assert.True(BulkCreateRecordSamples.DeclaresOwnToString(typeof(LeakingToString)));
        Assert.NotEmpty(BulkCreateRecordSamples.LeakedValues(typeof(LeakingToString)));
        Assert.False(BulkCreateRecordSamples.IsRecord(typeof(BulkCreatePlanner)));
    }

    [Fact]
    public void IssueCodes_AreTheConstantsOf2_5_AndTheAttributeCodesAreASubset()
    {
        var constants = typeof(BulkCreateIssueCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (Name: f.Name, Value: (string)f.GetRawConstantValue()!))
            .ToList();
        var existing = typeof(IssueCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(43, constants.Count);
        Assert.All(constants, c => Assert.Equal(c.Name, c.Value));
        Assert.Equal(constants.Select(c => c.Value), BulkCreateIssueCodes.All);
        Assert.DoesNotContain(BulkCreateIssueCodes.All, existing.Contains);
        Assert.Subset(BulkCreateIssueCodes.All.ToHashSet(StringComparer.Ordinal), BulkCreateIssueCodes.AttributeAndArguments.ToHashSet(StringComparer.Ordinal));
        Assert.Equal(
            [
                BulkCreateIssueCodes.CounterTooLong, BulkCreateIssueCodes.DuplicateInDirectory, BulkCreateIssueCodes.DuplicateRow,
                BulkCreateIssueCodes.DuplicateSourceRow, BulkCreateIssueCodes.EmptyToken, BulkCreateIssueCodes.ImplicitUpnClash,
                BulkCreateIssueCodes.PossibleExistingAccount,
            ],
            BulkCreateIssueCodes.AttributeAndArguments.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(ObjectKind.User, 20)]
    [InlineData(ObjectKind.InetOrgPerson, 20)]
    [InlineData(ObjectKind.Computer, 16)]
    [InlineData(ObjectKind.Group, 256)]
    [InlineData(ObjectKind.Contact, 0)]
    public void CreationNames_SamLimits(ObjectKind kind, int limit) => Assert.Equal(limit, CreationNames.MaxSamAccountName(kind));

    [Fact]
    public void CreationNames_LimitsClassChainsAndForbiddenCharacters()
    {
        Assert.Equal((20, 15, 256, 64, 1024, 64),
            (CreationNames.MaxUserSamAccountName, CreationNames.MaxComputerName, CreationNames.MaxGroupSamAccountName, CreationNames.MaxCn,
                CreationNames.MaxUpn, CreationNames.MaxMailLocalPart));
        Assert.Equal(["top", "person", "organizationalPerson", "user"], CreationNames.ClassChainOf(ObjectKind.User));
        Assert.Equal(["top", "person", "organizationalPerson", "user", "computer"], CreationNames.ClassChainOf(ObjectKind.Computer));
        Assert.Equal("contact", CreationNames.StructuralClassOf(ObjectKind.Contact));
        Assert.Equal("group", CreationNames.StructuralClassOf(ObjectKind.Group));
        Assert.All("\"/\\[]:;|=,+*?<>@\t\u0001", c => Assert.True(CreationNames.IsInvalidSamCharacter(c), $"'{c}'"));
        Assert.All("abcXYZ019.-_ ąłż$", c => Assert.False(CreationNames.IsInvalidSamCharacter(c), $"'{c}'"));
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(51, "AZ")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    public void ColumnLetter_IsTheExcelLetter(int index, string letter) => Assert.Equal(letter, TableTextReader.ColumnLetter(index));

    [Fact]
    public void InputTable_FindsColumnsByHeaderThenLetter_AndKeepsRowNumbers()
    {
        var table = new InputTable(
            [new TableColumn(0, "A", "Imię"), new TableColumn(1, "B", "Telefon komórkowy"), new TableColumn(2, "C", "B")],
            [new TableRow(1, ["Łucja", "+48 600 100 201"]), new TableRow(4, ["Zofia"])],
            HasHeader: true,
            TableSource.ClipboardText,
            []);

        Assert.Equal(0, table.FindColumn("  imię ")!.Index);
        Assert.Equal(0, table.FindColumn("Imie")!.Index);
        Assert.Equal(1, table.FindColumn("TELEFON KOMORKOWY")!.Index);
        Assert.Equal(2, table.FindColumn("B")!.Index); // a header equal to a letter wins
        Assert.Equal(2, table.FindColumn("c")!.Index);
        Assert.Null(table.FindColumn("Nazwisko"));
        Assert.Equal(string.Empty, table.Cell(1, 1));
        Assert.Equal("Łucja", table.Cell(0, 0));
        Assert.Equal(5, table.NextRowNumber);
        Assert.Equal(9, (table with { NextRowNumber = 9 }).NextRowNumber);
        Assert.Equal("C", new TableColumn(2, "C", string.Empty).Name);
        Assert.Equal(1, InputTable.Empty.NextRowNumber);
        Assert.Null(table.HeaderLine);
        Assert.Empty(table.TitleRows);
    }

    [Fact]
    public void FindColumn_PrefersTheExactHeaderOverTheFoldedOne()
    {
        var table = new InputTable([new TableColumn(0, "A", "Dzial"), new TableColumn(1, "B", "Dział")], [], true, TableSource.Typed, []);

        Assert.Equal(1, table.FindColumn("Dział")!.Index);
        Assert.Equal(0, table.FindColumn("Dzial")!.Index);
    }

    [Theory]
    [InlineData("objectClass")]
    [InlineData("unicodePwd")]
    [InlineData("userAccountControl")]
    [InlineData("accountExpires")]
    [InlineData("lockoutTime")]
    [InlineData("proxyAddresses")]
    [InlineData("c")]
    [InlineData("co")]
    [InlineData("countryCode")]
    [InlineData("manager")]
    [InlineData("managedBy")]
    [InlineData("member")]
    [InlineData("msExchRecipientTypeDetails")]
    [InlineData("msRTCSIP-PrimaryUserAddress")]
    [InlineData("mailNickname")]
    [InlineData("servicePrincipalName")]
    [InlineData("msDS-KeyCredentialLink")]
    public void NamedAttributes_AreBlocked(string attribute)
    {
        Assert.True(CreationValues.IsNamedBlockedAttribute(attribute));
        Assert.True(CreationValues.IsBlockedAttribute(attribute, ObjectKind.User, FakeSchema.Create(bulkCreate: true)));
    }

    [Theory]
    [InlineData("cn")]
    [InlineData("givenName")]
    [InlineData("mail")]
    [InlineData("mobile")]
    [InlineData("extensionAttribute5")]
    public void NamingAndPersonAttributes_AreNotNamedBlocked(string attribute) => Assert.False(CreationValues.IsNamedBlockedAttribute(attribute));

    [Fact]
    public void PhoneAndMultiLineAttributes_AreTheListsOf2_2()
    {
        Assert.All(["telephoneNumber", "mobile", "homePhone", "ipPhone", "pager", "facsimileTelephoneNumber", "otherTelephone", "otherMobile", "otherHomePhone"],
            a => Assert.True(CreationValues.IsPhoneAttribute(a), a));
        Assert.False(CreationValues.IsPhoneAttribute("mail"));
        Assert.All(["streetAddress", "postalAddress", "info"], a => Assert.True(CreationValues.IsMultiLine(a), a));
        Assert.False(CreationValues.IsMultiLine("description"));
    }

    [Fact]
    public void PowerShellRecords_PrintNamesAndKindsOnly()
    {
        var prompt = PowerShellParameter.SecurePrompt("AccountPassword", "Hasło początkowe dla Łucja Żak");
        var table = PowerShellParameter.Table("OtherAttributes", [new AttributeChange("extensionAttribute1", AttributeChangeKind.Replace, [], ["Łucja Żak"])]);
        var command = PowerShellCommand.Of("New-ADUser", PowerShellParameter.Literal("Name", "Łucja Żak"), prompt, table, PowerShellParameter.Server);

        Assert.Equal(PowerShellValueKind.SecurePrompt, prompt.Kind);
        Assert.Equal("Hasło początkowe dla Łucja Żak", prompt.Value);
        Assert.Equal(PowerShellValueKind.AttributeTable, table.Kind);
        Assert.Single(table.Attributes);
        Assert.DoesNotContain("Łucja", command.ToString(), StringComparison.Ordinal);
        Assert.Equal("-AccountPassword (SecurePrompt)", prompt.ToString());
        Assert.StartsWith("New-ADUser [-Name (Literal)", command.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectGlobalCatalog_DefaultsToNotSupported()
    {
        IDirectoryConnector connector = new MinimalConnector();
        await using var session = new FakeDirectoryBuilder().Build();

        await Assert.ThrowsAsync<NotSupportedException>(() => connector.ConnectGlobalCatalogAsync(session, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void SessionInfo_CountsAsEncrypted_AndGcReadinessUnknown_ByDefault()
    {
        var info = FakeDirectorySession.CreateInfo();

        Assert.True(info.IsChannelEncrypted);
        Assert.Null(info.IsGlobalCatalogReady);
    }

    [Fact]
    public void PlanValidation_ValidateValue_IsReachableFromCore()
    {
        var method = typeof(ChangePlan).Assembly.GetType("AdMng.Core.Batch.Planning.PlanValidation", throwOnError: true)!
            .GetMethod("ValidateValue", BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(method);
    }

    [Fact]
    public void Lookups_RefreshStartsANewGeneration_AndKeepTheForestReader()
    {
        var reader = new FakeGlobalCatalogReader(new FakeDirectoryBuilder().Build());
        var lookups = new BulkCreateLookups(reader);

        lookups.Refresh();
        lookups.Refresh();

        Assert.Same(reader, lookups.ForestReader);
        Assert.Equal(2, lookups.Generation);
        Assert.Null(new BulkCreateLookups().ForestReader);
        Assert.Equal("Bulk create lookups (generation 2, forest reader=True)", lookups.ToString());
    }

    [Fact]
    public void Planner_IsThePlannerOfTheContract()
    {
        IBulkCreatePlanner planner = new BulkCreatePlanner(new PasswordGenerator(), TimeProvider.System);

        Assert.NotNull(planner);
        Assert.Throws<ArgumentNullException>(() => new BulkCreatePlanner(null!, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new BulkCreatePlanner(new PasswordGenerator(), null!));
    }

    [Fact]
    public void Computation_CountsStatuses_AndExposesTheNumbers()
    {
        var input = new BulkCreateInput
        {
            ContainerDn = "OU=Nowi pracownicy,OU=Pracownicy,OU=Firma,DC=contoso,DC=test",
            Table = InputTable.Empty,
            Mappings = [],
            Rules = new BulkCreateRules(),
            Options = new BulkAccountOptions(),
        };
        var computation = new BulkCreateComputation
        {
            Input = input,
            Rows =
            [
                new BulkCreateRow { Number = 1, Status = BulkRowStatus.Ok, Values = [new BulkValue("sAMAccountName", ["kd208"], BulkValueSource.Pattern)], CounterNumber = 208 },
                new BulkCreateRow { Number = 2, Status = BulkRowStatus.Error, Values = [] },
            ],
            Attributes = ["cn", "sAMAccountName"],
            Server = "dc01.contoso.test",
            Issues = [new ValidationIssue(IssueSeverity.Warning, BulkCreateIssueCodes.CheckedInDomainOnly, "Checked in the domain only.")],
        };

        Assert.False(computation.HasRunErrors);
        Assert.Equal(1, computation.Count(BulkRowStatus.Ok));
        Assert.Equal(new Dictionary<int, int> { [1] = 208 }, computation.Numbers);
        Assert.Equal("kd208", computation.Rows[0].Value("SAMACCOUNTNAME"));
        Assert.Null(computation.Rows[1].Value("cn"));
        Assert.True(new BulkAccountOptions().MustChangeAtLogon);
        Assert.True(new BulkCreateRules().SanitizeIdentifiers);
        Assert.False(new BulkCreateRules().AddPrimarySmtp);
        Assert.Equal(4, input.MaxParallelLookups);
    }

    [Fact]
    public void PatternError_ErrorLevels()
    {
        Assert.True(new PatternError(PatternErrorCode.Cycle, "cn", 0).IsError);
        Assert.False(new PatternError(PatternErrorCode.UnknownAttribute, "cn", 3, "{imie}", "{givenName}").IsError);
        Assert.False(new PatternError(PatternErrorCode.SamInAddress, "mail", 1).IsError);
        Assert.False(new PatternError(PatternErrorCode.InvariantDateNames, "description", 1).IsError);
        Assert.Equal("UnknownAttribute in cn at 3", new PatternError(PatternErrorCode.UnknownAttribute, "cn", 3, "{imie}", "{givenName}").ToString());
        Assert.Equal(new CounterSpec(1, 0, 1), CounterSpec.Default);
        Assert.Equal(["dn", "rdn", "counter"], CreationPatternTokens.Forbidden);
    }

    private sealed record GeneratedToString(string Name);

    private sealed record LeakingToString(string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>A connector with only the required members (the default GC connect applies).</summary>
    private sealed class MinimalConnector : IDirectoryConnector
    {
        public string? GetComputerDomain() => null;

        public Task<IReadOnlyList<DomainControllerInfo>> FindDomainControllersAsync(string domain, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DomainControllerInfo>>([]);

        public Task<IDirectorySession> ConnectAsync(ConnectionOptions options, NetworkCredential? credential, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
