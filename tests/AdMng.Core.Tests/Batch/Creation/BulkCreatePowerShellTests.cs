using AdMng.Core.Batch;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Core.Security;
using AdMng.Testing;
using AdMng.Testing.BulkCreate;

namespace AdMng.Core.Tests.Batch.Creation;

/// <summary>
/// The PowerShell equivalent of created objects (docs/dev/phase3-bulk-create.md 2.2, 2.11) and the exporter's two value
/// kinds it needs (<see cref="PowerShellValueKind.SecurePrompt"/>, <see cref="PowerShellValueKind.AttributeTable"/>): the
/// plans are shaped as <c>BuildPlan</c> makes them (2.5).
/// </summary>
public sealed class BulkCreatePowerShellTests
{
    private const string Container = "OU=Nowi pracownicy,OU=Pracownicy,OU=Firma,DC=contoso,DC=test";
    private const string UserDn = "CN=Łucja Żak," + Container;
    private const string ManagerDn = "CN=Piotr Zieliński,OU=Pracownicy,OU=Firma,DC=contoso,DC=test";
    private const string GroupNowi = "CN=GG_Nowi,OU=Grupy,OU=Firma,DC=contoso,DC=test";
    private const string GroupPrinters = "CN=DL_Drukarki,OU=Grupy,OU=Firma,DC=contoso,DC=test";
    private const string Password = "Start!2026-Łucja-Sekret";
    private const string Server = "dc01.contoso.test";

    private static readonly ISchemaCatalog Schema = FakeSchema.Create(bulkCreate: true);

    private static AttributeChange Value(string attribute, params string[] values) => new(attribute, AttributeChangeKind.Replace, [], values);

    private static ObjectPlan UserPlan(bool enabled = true, bool mustChange = true, bool password = true) => new()
    {
        DistinguishedName = UserDn,
        DisplayName = "Łucja Żak",
        Kind = ObjectKind.User,
        ObjectClasses = CreationNames.ClassChainOf(ObjectKind.User),
        SourceRow = 1,
        Changes =
        [
            new PlannedChange
            {
                Action = ChangeAction.Create,
                Attributes =
                [
                    Value("objectClass", "user"), Value("sAMAccountName", "kd208"), Value("userPrincipalName", "kd208@contoso.com"),
                    Value("givenName", "Łucja"), Value("sn", "Żak"), Value("displayName", "Łucja Żak"), Value("mail", "kd208@contoso.com"),
                    Value("mobile", "+48 600 100 201"), Value("title", "Księgowa"), Value("department", "Księgowość"),
                    Value("description", "Konto utworzone 2026-11-09 – zlecenie"), Value("extensionAttribute5", "O'Neill"),
                    Value("msDS-cloudExtensionAttribute1", "Kraków"), Value("userAccountControl", "514"), Value("accountExpires", "134116020000000000"),
                    Value("proxyAddresses", "SMTP:kd208@contoso.com"), Value("c", "PL"), Value("co", "Polska"), Value("countryCode", "616"),
                    Value("otherTelephone", "+48 22 555 01 01", "+48 22 555 01 02"),
                ],
            },
            .. password ? [new PlannedChange { Action = ChangeAction.ResetPassword, Password = Password }] : Array.Empty<PlannedChange>(),
            .. password && (enabled || mustChange)
                ? [new PlannedChange
                {
                    Action = ChangeAction.Modify,
                    Attributes =
                    [
                        .. enabled ? [new AttributeChange("userAccountControl", AttributeChangeKind.Replace, ["514"], ["512"])] : Array.Empty<AttributeChange>(),
                        .. mustChange ? [new AttributeChange("pwdLastSet", AttributeChangeKind.Replace, [], ["0"])] : Array.Empty<AttributeChange>(),
                    ],
                }]
                : Array.Empty<PlannedChange>(),
            new PlannedChange { Action = ChangeAction.Modify, Attributes = [new AttributeChange("manager", AttributeChangeKind.Replace, [], [ManagerDn])] },
            new PlannedChange { Action = ChangeAction.AddMember, TargetDn = GroupNowi },
            new PlannedChange { Action = ChangeAction.AddMember, TargetDn = GroupPrinters },
        ],
    };

    private static string Export(ObjectPlan objectPlan, string? server = Server)
    {
        var plan = new ChangePlan
        {
            Title = "Zakładanie wielu kont – Nowi pracownicy",
            CreatedAt = new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.FromHours(1)),
            Server = server,
            Objects = [objectPlan with { PowerShell = BulkCreatePowerShell.For(objectPlan) }],
        };
        return new PowerShellExporter().Export(plan, server, Schema);
    }

    private static string LineStarting(string script, string start) =>
        Assert.Single(script.Split("\r\n"), l => l.StartsWith(start, StringComparison.Ordinal));

    [Fact]
    public void User_NewADUser_WithNamedParametersOtherAttributesPromptAndFlags()
    {
        var script = Export(UserPlan());
        var line = LineStarting(script, "New-ADUser ");

        Assert.StartsWith(
            "New-ADUser -Name 'Łucja Żak' -Path '" + Container + "' -SamAccountName 'kd208' -UserPrincipalName 'kd208@contoso.com' -GivenName 'Łucja'"
            + " -Surname 'Żak' -DisplayName 'Łucja Żak' -Description 'Konto utworzone 2026-11-09 – zlecenie' -EmailAddress 'kd208@contoso.com'"
            + " -Title 'Księgowa' -Department 'Księgowość' -MobilePhone '+48 600 100 201' -Country 'PL' -OtherAttributes @{",
            line,
            StringComparison.Ordinal);
        Assert.EndsWith(
            "} -AccountPassword (Read-Host -AsSecureString -Prompt 'Initial password for Łucja Żak') -Enabled $true -ChangePasswordAtLogon $true -Server $server",
            line,
            StringComparison.Ordinal);
        Assert.Contains("extensionAttribute5='O''Neill'", line, StringComparison.Ordinal);
        Assert.Contains("'msDS-cloudExtensionAttribute1'='Kraków'", line, StringComparison.Ordinal);
        Assert.Contains("accountExpires=134116020000000000", line, StringComparison.Ordinal);
        Assert.Contains("proxyAddresses='SMTP:kd208@contoso.com'", line, StringComparison.Ordinal);
        Assert.Contains("co='Polska'", line, StringComparison.Ordinal);
        Assert.Contains("countryCode=616", line, StringComparison.Ordinal);
        Assert.Contains("otherTelephone=@('+48 22 555 01 01','+48 22 555 01 02')", line, StringComparison.Ordinal);
        Assert.DoesNotContain("userAccountControl", line, StringComparison.Ordinal);
        Assert.DoesNotContain("objectClass", line, StringComparison.Ordinal);
        Assert.DoesNotContain("AccountExpirationDate", line, StringComparison.Ordinal);
    }

    [Fact]
    public void User_ManagerAndGroups_FollowTheNewADUser()
    {
        var lines = Export(UserPlan()).Split("\r\n").ToList();
        var create = lines.FindIndex(l => l.StartsWith("New-ADUser ", StringComparison.Ordinal));

        Assert.Equal($"Set-ADUser -Identity '{UserDn}' -Manager '{ManagerDn}' -Server $server", lines[create + 1]);
        Assert.Equal($"Add-ADGroupMember -Identity '{GroupNowi}' -Members '{UserDn}' -Server $server", lines[create + 2]);
        Assert.Equal($"Add-ADGroupMember -Identity '{GroupPrinters}' -Members '{UserDn}' -Server $server", lines[create + 3]);
    }

    [Fact]
    public void User_ThePasswordIsNeverInTheScript()
    {
        var script = Export(UserPlan());

        Assert.DoesNotContain(Password, script, StringComparison.Ordinal);
        Assert.DoesNotContain("Sekret", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-ADAccountPassword", script, StringComparison.Ordinal);
        Assert.Contains("#Requires -Modules ActiveDirectory", script, StringComparison.Ordinal);
        Assert.Contains("# Passwords are not included", script, StringComparison.Ordinal);
    }

    [Fact]
    public void User_DisabledAndWithoutMustChange_AndWithoutPassword()
    {
        var disabled = LineStarting(Export(UserPlan(enabled: false)), "New-ADUser ");
        var noChange = LineStarting(Export(UserPlan(mustChange: false)), "New-ADUser ");
        var noPassword = LineStarting(Export(UserPlan(enabled: false, mustChange: false, password: false)), "New-ADUser ");

        Assert.DoesNotContain("-Enabled", disabled, StringComparison.Ordinal);
        Assert.Contains("-ChangePasswordAtLogon $true", disabled, StringComparison.Ordinal);
        Assert.Contains("-Enabled $true", noChange, StringComparison.Ordinal);
        Assert.DoesNotContain("-ChangePasswordAtLogon", noChange, StringComparison.Ordinal);
        Assert.DoesNotContain("-AccountPassword", noPassword, StringComparison.Ordinal);
        Assert.DoesNotContain("-Enabled", noPassword, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAServer_NoServerParameter()
    {
        var script = Export(UserPlan(), server: null);

        Assert.DoesNotContain("-Server", script, StringComparison.Ordinal);
        Assert.Contains("-ChangePasswordAtLogon $true", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-2147483646, "Global", "Security")]
    [InlineData(-2147483644, "DomainLocal", "Security")]
    [InlineData(8, "Universal", "Distribution")]
    [InlineData(2, "Global", "Distribution")]
    public void Group_NewADGroup_ScopeAndCategory(int groupType, string scope, string category)
    {
        const string GroupDn = "CN=GG_Księgowość,OU=Grupy,OU=Firma,DC=contoso,DC=test";
        var plan = new ObjectPlan
        {
            DistinguishedName = GroupDn,
            DisplayName = "GG_Księgowość",
            Kind = ObjectKind.Group,
            ObjectClasses = CreationNames.ClassChainOf(ObjectKind.Group),
            Changes =
            [
                new PlannedChange
                {
                    Action = ChangeAction.Create,
                    Attributes =
                    [
                        Value("objectClass", "group"), Value("sAMAccountName", "GG_Księgowość"), Value("groupType", groupType.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        Value("description", "Księgowość – Kraków"), Value("mail", "ksiegowosc@contoso.com"), Value("info", "od 1 grudnia"),
                    ],
                },
                new PlannedChange { Action = ChangeAction.Modify, Attributes = [new AttributeChange("managedBy", AttributeChangeKind.Replace, [], [ManagerDn])] },
                new PlannedChange { Action = ChangeAction.AddMember, TargetDn = GroupNowi },
            ],
        };

        var script = Export(plan);

        Assert.Equal(
            $"New-ADGroup -Name 'GG_Księgowość' -GroupScope '{scope}' -GroupCategory '{category}' -Path 'OU=Grupy,OU=Firma,DC=contoso,DC=test'"
            + " -SamAccountName 'GG_Księgowość' -Description 'Księgowość – Kraków' -OtherAttributes @{mail='ksiegowosc@contoso.com'; info='od 1 grudnia'} -Server $server",
            LineStarting(script, "New-ADGroup "));
        Assert.Equal($"Set-ADGroup -Identity '{GroupDn}' -ManagedBy '{ManagerDn}' -Server $server", LineStarting(script, "Set-ADGroup "));
        Assert.Equal($"Add-ADGroupMember -Identity '{GroupNowi}' -Members '{GroupDn}' -Server $server", LineStarting(script, "Add-ADGroupMember "));
        Assert.DoesNotContain("groupType", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Group_WithoutAGroupType_IsGeneric()
    {
        var plan = new ObjectPlan
        {
            DistinguishedName = "CN=GG_X,OU=Grupy,DC=contoso,DC=test",
            DisplayName = "GG_X",
            Kind = ObjectKind.Group,
            Changes = [new PlannedChange { Action = ChangeAction.Create, Attributes = [Value("objectClass", "group"), Value("sAMAccountName", "GG_X")] }],
        };

        Assert.Null(BulkCreatePowerShell.For(plan));
    }

    private static ObjectPlan ComputerPlan(bool joinRight = false) => new()
    {
        DistinguishedName = "CN=PC01," + Container,
        DisplayName = "PC01",
        Kind = ObjectKind.Computer,
        ObjectClasses = CreationNames.ClassChainOf(ObjectKind.Computer),
        Changes =
        [
            new PlannedChange
            {
                Action = ChangeAction.Create,
                Attributes = [Value("objectClass", "computer"), Value("sAMAccountName", "PC01$"), Value("userAccountControl", "4098"), Value("description", "Księgowość, pokój 12"), Value("location", "Kraków")],
            },
            .. joinRight
                ? [new PlannedChange { Action = ChangeAction.ModifySecurity, Acl = new AclChange([], []) }]
                : Array.Empty<PlannedChange>(),
            new PlannedChange { Action = ChangeAction.ResetPassword, Password = new string('x', 120) },
            new PlannedChange { Action = ChangeAction.Modify, Attributes = [new AttributeChange("userAccountControl", AttributeChangeKind.Replace, ["4098"], ["4096"])] },
            new PlannedChange { Action = ChangeAction.Modify, Attributes = [new AttributeChange("managedBy", AttributeChangeKind.Replace, [], [ManagerDn])] },
            new PlannedChange { Action = ChangeAction.AddMember, TargetDn = GroupPrinters },
        ],
    };

    [Fact]
    public void Computer_NewADComputer_WithoutAccountPassword()
    {
        var script = Export(ComputerPlan());

        Assert.Equal(
            $"New-ADComputer -Name 'PC01' -Path '{Container}' -SamAccountName 'PC01$' -Description 'Księgowość, pokój 12' -Location 'Kraków' -Enabled $true -Server $server",
            LineStarting(script, "New-ADComputer "));
        Assert.Equal($"Set-ADComputer -Identity 'CN=PC01,{Container}' -ManagedBy '{ManagerDn}' -Server $server", LineStarting(script, "Set-ADComputer "));
        Assert.Contains($"Add-ADGroupMember -Identity '{GroupPrinters}' -Members 'CN=PC01,{Container}' -Server $server", script, StringComparison.Ordinal);
        Assert.DoesNotContain("AccountPassword", script, StringComparison.Ordinal);
        Assert.DoesNotContain("xxxxxxxx", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Read-Host", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Computer_WithAJoinRight_IsGeneric() => Assert.Null(BulkCreatePowerShell.For(ComputerPlan(joinRight: true)));

    [Fact]
    public void Contact_IsGeneric()
    {
        var contact = new ObjectPlan
        {
            DistinguishedName = "CN=Jan Kowalski (zewnętrzny)," + Container,
            DisplayName = "Jan Kowalski (zewnętrzny)",
            Kind = ObjectKind.Contact,
            ObjectClasses = CreationNames.ClassChainOf(ObjectKind.Contact),
            Changes = [new PlannedChange { Action = ChangeAction.Create, Attributes = [Value("objectClass", "contact"), Value("mail", "jan.kowalski@firma.pl")] }],
        };

        Assert.Null(BulkCreatePowerShell.For(contact));
        Assert.Contains("New-ADObject -Name 'Jan Kowalski (zewnętrzny)' -Type 'contact'", Export(contact), StringComparison.Ordinal);
    }

    [Fact]
    public void ARetryPlan_WithoutItsCreateStep_IsGeneric()
    {
        var retry = UserPlan() with { Changes = UserPlan().Changes.Skip(1).ToList() };

        Assert.Null(BulkCreatePowerShell.For(retry));
        var script = Export(retry);
        Assert.Contains("Set-ADAccountPassword", script, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, script, StringComparison.Ordinal);
    }

    [Fact]
    public void AStepBuildPlanDoesNotMake_IsGeneric()
    {
        var renamed = UserPlan() with { Changes = [.. UserPlan().Changes, new PlannedChange { Action = ChangeAction.Rename, TargetDn = "CN=Ł Żak" }] };
        var modified = UserPlan() with { Changes = [.. UserPlan().Changes, new PlannedChange { Action = ChangeAction.Modify, Attributes = [Value("title", "x")] }] };

        Assert.Null(BulkCreatePowerShell.For(renamed));
        Assert.Null(BulkCreatePowerShell.For(modified));
        Assert.Null(BulkCreatePowerShell.For(UserPlan() with { DistinguishedName = "nie DN" }));
    }

    [Fact]
    public void AnObjectWithErrors_IsSkipped()
    {
        var plan = UserPlan() with { Issues = [new ValidationIssue(IssueSeverity.Error, BulkCreateIssueCodes.DuplicateInDirectory, "Taken.", "sAMAccountName")] };

        var script = Export(plan);

        Assert.DoesNotContain("New-ADUser", script, StringComparison.Ordinal);
        Assert.Contains("# SKIPPED", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Exporter_AttributeTable_PromptsSecretAttributes_AndSecurePromptQuotes()
    {
        var plan = new ObjectPlan
        {
            DistinguishedName = UserDn,
            DisplayName = "Łucja Żak",
            Changes = [new PlannedChange { Action = ChangeAction.Modify, Attributes = [Value("title", "x")] }],
            PowerShell = PowerShellEquivalent.Of(new PowerShellCommand(
                "New-ADUser",
                [
                    PowerShellParameter.Literal("Name", "Łucja Żak"),
                    PowerShellParameter.SecurePrompt("AccountPassword", "Hasło dla O'Neill"),
                    PowerShellParameter.Table("OtherAttributes", [Value("ms-Mcs-AdmPwd", "TajneLAPS!1"), Value("info", "a"), new AttributeChange("pager", AttributeChangeKind.Replace, [], [])]),
                    PowerShellParameter.Table("Empty", []),
                ]) { Module = "ActiveDirectory" }),
        };

        var script = new PowerShellExporter().Export(new ChangePlan { Objects = [plan] }, null, Schema);
        var lines = script.Split("\r\n").ToList();
        var command = lines.FindIndex(l => l.StartsWith("New-ADUser ", StringComparison.Ordinal));

        Assert.DoesNotContain("TajneLAPS!1", script, StringComparison.Ordinal);
        Assert.StartsWith("$secret1 = [System.Net.NetworkCredential]::new('', (Read-Host -AsSecureString -Prompt 'ms-Mcs-AdmPwd for Łucja Żak')).Password", lines[command - 1], StringComparison.Ordinal);
        Assert.Equal("New-ADUser -Name 'Łucja Żak' -AccountPassword (Read-Host -AsSecureString -Prompt 'Hasło dla O''Neill') -OtherAttributes @{'ms-Mcs-AdmPwd'=$secret1; info='a'}", lines[command]);
        Assert.Equal("Remove-Variable secret1", lines[command + 1]);
    }

    [Fact]
    public void Commands_PrintNoValue()
    {
        var equivalent = BulkCreatePowerShell.For(UserPlan())!;

        Assert.All(equivalent.Commands, c => Assert.Equal("ActiveDirectory", c.Module));
        Assert.All(equivalent.Commands, c => Assert.DoesNotContain("Łucja", c.ToString(), StringComparison.Ordinal));
        Assert.All(equivalent.Commands.SelectMany(c => c.Parameters), p => Assert.DoesNotContain("kd208", p.ToString(), StringComparison.Ordinal));
        Assert.Equal(["New-ADUser", "Set-ADUser", "Add-ADGroupMember", "Add-ADGroupMember"], equivalent.Commands.Select(c => c.Cmdlet));
    }

    [Fact]
    public void For_RejectsNull() => Assert.Throws<ArgumentNullException>(() => BulkCreatePowerShell.For(null!));

    /// <summary>
    /// The operator scenario end to end (B1 reader and synonyms, B2 planner, B1 equivalent and exporter): the plan of five
    /// numbered accounts exports as five <c>New-ADUser</c> commands with a password prompt and without any password.
    /// </summary>
    [Fact(Skip = "F3 wave 1 merge")]
    public async Task OperatorScenario_ThePlannersPlan_ExportsAsNewADUser()
    {
        var builder = new FakeDirectoryBuilder(schema: FakeSchema.Create(bulkCreate: true));
        var sample = builder.AddBulkCreateSample(builder.AddSampleCompany());
        await using var session = builder.Build();
        var table = TableTextReader.ReadClipboard(ExcelSamples.OperatorScenarioTsv, null, null, HeaderMode.Auto);
        var mappings = table.Columns
            .Select(c => (Column: c, Suggestion: ColumnSynonyms.Suggest(c.Header, ObjectKind.User, null, [.. table.Rows.Select(r => r.Cell(c.Index))])))
            .Where(c => c.Suggestion is not null)
            .Select(c => new ColumnMapping(c.Column.Index, c.Suggestion!.Kind, c.Suggestion.Attribute, c.Suggestion.Order))
            .ToList();
        var input = new BulkCreateInput
        {
            ContainerDn = sample.NewEmployeesOu,
            Table = table,
            Mappings = mappings,
            Rules = new BulkCreateRules
            {
                Attributes =
                [
                    new AttributeRule("sAMAccountName", "kd{$nr:201}"), new AttributeRule("userPrincipalName", "{sAMAccountName}"),
                    new AttributeRule("cn", "{givenName} {sn}"), new AttributeRule("displayName", "{givenName} {sn}"),
                ],
                UpnSuffix = "contoso.com",
                CounterMode = CounterMode.AfterHighest,
            },
            Options = new BulkAccountOptions(),
        };
        var planner = new BulkCreatePlanner(new PasswordGenerator(), TimeProvider.System);

        var computation = await planner.ComputeAsync(session, input, new BulkCreateLookups(), null, TestContext.Current.CancellationToken);
        var plan = planner.BuildPlan(computation, null, "Zakładanie wielu kont");
        var script = new PowerShellExporter().Export(plan, plan.Server, session.Schema);

        Assert.Equal(5, mappings.Count);
        Assert.Equal(5, script.Split("\r\n").Count(l => l.StartsWith("New-ADUser ", StringComparison.Ordinal)));
        Assert.Contains("-Name 'Łukasz Grzędziński'", script, StringComparison.Ordinal);
        Assert.Contains("-SamAccountName 'kd208'", script, StringComparison.Ordinal);
        Assert.Contains("-MobilePhone '+48 600 200 301'", script, StringComparison.Ordinal);
        Assert.Contains("-AccountPassword (Read-Host -AsSecureString -Prompt 'Initial password for Łukasz Grzędziński')", script, StringComparison.Ordinal);
        Assert.All(plan.Objects.SelectMany(o => o.Changes).Select(c => c.Password).OfType<string>(), p => Assert.DoesNotContain(p, script, StringComparison.Ordinal));
    }
}
