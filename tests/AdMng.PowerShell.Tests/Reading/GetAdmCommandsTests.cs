using AdMng.Core.Ldap;
using AdMng.Core.PowerShell;

namespace AdMng.PowerShell.Tests.Reading;

/// <summary>Get-AdmObject, Get-AdmUser, Get-AdmGroup, Get-AdmComputer: identities, filters, properties, errors (R1 part 2).</summary>
public sealed class GetAdmCommandsTests
{
    private static bool SameDn(string? left, string? right) => DistinguishedName.AreEqual(left, right);

    [Fact]
    public async Task Get_AdmUser_by_identity_finds_the_user_by_every_kind_of_identity()
    {
        await using var harness = await ReadHarness.CreateAsync();
        var jan = harness.Company.JanKowalski;
        var guid = harness.Directory.GetValues(jan, "objectGUID").Single();
        var sid = harness.Directory.GetValues(jan, "objectSid").Single();

        foreach (var identity in new[] { "jan.kowalski", "jan.kowalski@contoso.test", $"'{jan}'", guid, $"<GUID={guid}>", sid })
        {
            var result = await harness.RunAsync($"Get-AdmUser -Identity {(identity.StartsWith('\'') ? identity : "'" + identity + "'")}");

            Assert.True(result.Errors.Length == 0, $"{identity}: {result.Errors}");
            var user = result.Single();
            Assert.True(SameDn(jan, (string)user.Properties["DistinguishedName"].Value!), identity);
            Assert.Contains("AdMng.User", user.TypeNames);
            Assert.Equal("User", user.Properties["ObjectKind"].Value);
            Assert.Equal("Jan Kowalski", user.Properties["Name"].Value);
        }
    }

    [Fact]
    public async Task Get_AdmUser_returns_typed_values_and_the_facts_a_script_wants()
    {
        await using var harness = await ReadHarness.CreateAsync();

        var user = (await harness.RunAsync("Get-AdmUser -Identity zaneta.szymanska")).Single();

        Assert.Equal("Żaneta Szymańska", user.Properties["displayName"].Value);
        Assert.Equal("Sprzedaż", user.Properties["department"].Value);
        Assert.IsType<int>(user.Properties["userAccountControl"].Value);
        Assert.Equal(512, user.Properties["userAccountControl"].Value);
        Assert.Equal(true, user.Properties["Enabled"].Value);
        Assert.Equal(true, user.Properties["LockedOut"].Value);
        Assert.Equal(new[] { "NORMAL_ACCOUNT" }, (string[])user.Properties["UserAccountControlFlags"].Value!);
        Assert.IsType<Guid>(user.Properties["objectGUID"].Value);
        Assert.StartsWith("S-1-5-21-", (string)user.Properties["objectSid"].Value!, StringComparison.Ordinal);
        Assert.IsType<string[]>(user.Properties["objectClass"].Value);
        Assert.Contains("user", (string[])user.Properties["objectClass"].Value!);
    }

    [Fact]
    public async Task Get_AdmUser_filters_combine_and_star_is_the_only_wildcard()
    {
        await using var harness = await ReadHarness.CreateAsync();

        var byDepartment = await harness.RunAsync("Get-AdmUser -Department Księgowość");
        var byName = await harness.RunAsync("Get-AdmUser -Name 'Jan*'");
        var disabled = await harness.RunAsync("Get-AdmUser -Enabled $false");
        var enabledAccountants = await harness.RunAsync("Get-AdmUser -Department Księgowość -Enabled $true");
        var byMail = await harness.RunAsync("Get-AdmUser -Mail 'lukasz.*'");
        var literalStar = await harness.RunAsync("Get-AdmUser -Name 'Jan?Kowalski'");

        Assert.Equal(["grazyna.dabrowska", "malgorzata.wojcik", "zbigniew.kazmierczak"], byDepartment.Texts("sAMAccountName").Order().ToArray());
        Assert.Equal(["jan.kowalski"], byName.Texts("sAMAccountName"));
        Assert.Equal(["Guest", "krbtgt", "zbigniew.kazmierczak"], disabled.Texts("sAMAccountName").Order().ToArray());
        Assert.Equal(["grazyna.dabrowska", "malgorzata.wojcik"], enabledAccountants.Texts("sAMAccountName").Order().ToArray());
        Assert.Equal(["lukasz.wisniewski"], byMail.Texts("sAMAccountName"));
        Assert.Empty(literalStar.Objects);
    }

    [Fact]
    public async Task Get_AdmUser_without_a_filter_returns_users_only_and_honours_search_base_scope_and_size()
    {
        await using var harness = await ReadHarness.CreateAsync();

        var all = await harness.RunAsync("Get-AdmUser");
        var inIt = await harness.RunAsync($"Get-AdmUser -SearchBase '{harness.Company.ItOu}'");
        var oneLevelOfEmployees = await harness.RunAsync($"Get-AdmUser -SearchBase '{harness.Company.EmployeesOu}' -SearchScope OneLevel");
        var limited = await harness.RunAsync("Get-AdmUser -ResultSetSize 2");

        Assert.Equal(11, all.Objects.Count);
        Assert.All(all.Objects, o => Assert.Contains("AdMng.User", o.TypeNames));
        Assert.DoesNotContain("WAW-PC001$", all.Texts("sAMAccountName"));
        Assert.Equal(["jan.kowalski", "lukasz.wisniewski"], inIt.Texts("sAMAccountName").Order().ToArray());
        Assert.Empty(oneLevelOfEmployees.Objects);
        Assert.Equal(2, limited.Objects.Count);
    }

    [Fact]
    public async Task Properties_adds_attributes_by_their_schema_name_and_star_returns_what_the_server_returned()
    {
        await using var harness = await ReadHarness.CreateAsync();

        var extra = (await harness.RunAsync("Get-AdmUser -Identity jan.kowalski -Properties MANAGER,whenCreated,l,otherTelephone")).Single();
        var star = (await harness.RunAsync("Get-AdmUser -Identity jan.kowalski -Properties *")).Single();
        var plain = (await harness.RunAsync("Get-AdmUser -Identity jan.kowalski")).Single();

        Assert.NotNull(extra.Properties["manager"]);
        Assert.Null(extra.Properties["manager"].Value);
        Assert.Contains(extra.Properties, p => p.Name == "manager");
        Assert.IsType<DateTime>(extra.Properties["whenCreated"].Value);
        Assert.Equal("Warszawa", extra.Properties["l"].Value);
        Assert.Empty((string[])extra.Properties["otherTelephone"].Value!);
        Assert.Contains(star.Properties, p => p.Name == "pwdLastSet");
        Assert.Contains(star.Properties, p => p.Name == "primaryGroupID");
        Assert.DoesNotContain(plain.Properties, p => p.Name == "pwdLastSet");
    }

    [Fact]
    public async Task An_unknown_or_malformed_property_name_ends_the_command_before_anything_is_read()
    {
        await using var harness = await ReadHarness.CreateAsync();
        harness.Directory.ClearOperations();

        var unknown = await harness.RunAsync("try { Get-AdmUser -Identity jan.kowalski -Properties nonExistingAttribute } catch { $_.FullyQualifiedErrorId.Split(',')[0] + '|' + $_.Exception.Message }");
        var malformed = await harness.RunAsync("try { Get-AdmUser -Properties 'a)(b' } catch { $_.FullyQualifiedErrorId.Split(',')[0] }");

        Assert.StartsWith("AdmInvalidProperty|The schema of this domain has no attribute 'nonExistingAttribute'.", unknown.Single().ToString(), StringComparison.Ordinal);
        Assert.Equal("AdmInvalidProperty", malformed.Single().ToString());
        Assert.Empty(harness.Directory.Operations);
    }

    [Fact]
    public async Task Get_AdmUser_for_a_group_or_a_computer_is_not_found_and_says_which_identity()
    {
        await using var harness = await ReadHarness.CreateAsync();

        var group = await harness.RunAsync("Get-AdmUser -Identity GG_IT");
        var computer = await harness.RunAsync($"Get-AdmUser -Identity '{harness.Company.Computer1}'");

        Assert.Empty(group.Objects);
        Assert.Contains("Cannot find a user with identity 'GG_IT'", group.Errors, StringComparison.Ordinal);
        Assert.Empty(computer.Objects);
        Assert.Contains(harness.Company.Computer1, computer.Errors, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PowerShellRunState.Completed, group.End.State);
        Assert.True(group.End.HadErrors);
    }

    [Fact]
    public async Task The_commands_work_in_constrained_language_the_default_and_give_the_same_result_in_the_trusted_mode()
    {
        await using var harness = await ReadHarness.CreateAsync();

        var constrained = await harness.RunAsync("Get-AdmUser -Department IT | Sort-Object sAMAccountName | ForEach-Object { $_.sAMAccountName + '|' + (Get-AdmVersion).LanguageMode }");
        await harness.Session.SetLanguageModeAsync(PowerShellLanguageMode.Full, TestContext.Current.CancellationToken);
        var full = await harness.RunAsync("Get-AdmUser -Department IT | Sort-Object sAMAccountName | ForEach-Object { $_.sAMAccountName + '|' + (Get-AdmVersion).LanguageMode }");

        Assert.Equal(["jan.kowalski|Constrained", "lukasz.wisniewski|Constrained"], constrained.Objects.Select(o => o.ToString()).ToArray());
        Assert.Equal(["jan.kowalski|Full", "lukasz.wisniewski|Full"], full.Objects.Select(o => o.ToString()).ToArray());
    }

    [Fact]
    public async Task Get_AdmObject_finds_one_object_by_dn_guid_sam_and_sid_of_any_kind()
    {
        await using var harness = await ReadHarness.CreateAsync();
        var ou = harness.Company.ItOu;
        var group = harness.Company.GroupIt;
        var groupGuid = harness.Directory.GetValues(group, "objectGUID").Single();

        var organizationalUnit = (await harness.RunAsync($"Get-AdmObject -Identity '{ou}'")).Single();
        var byGuid = (await harness.RunAsync($"Get-AdmObject -Identity '{groupGuid}'")).Single();
        var bySam = (await harness.RunAsync("Get-AdmObject -Identity GG_IT")).Single();

        Assert.Contains("AdMng.Object", organizationalUnit.TypeNames);
        Assert.Equal("OrganizationalUnit", organizationalUnit.Properties["ObjectKind"].Value);
        Assert.Equal("IT", organizationalUnit.Properties["Name"].Value);
        // Get-AdmObject returns objects of every kind: one type name first for all of them, the kind's behind it (audit ps-commands-2).
        Assert.Equal(["AdMng.Object", "AdMng.Group"], byGuid.TypeNames.ToArray());
        Assert.True(SameDn(group, (string)byGuid.Properties["DistinguishedName"].Value!));
        Assert.True(SameDn(group, (string)bySam.Properties["DistinguishedName"].Value!));
        Assert.Equal("Global", bySam.Properties["GroupScope"].Value);
        Assert.Equal("Security", bySam.Properties["GroupCategory"].Value);
    }

    [Fact]
    public async Task Get_AdmObject_takes_identities_from_the_pipeline_by_value_and_by_property_name()
    {
        await using var harness = await ReadHarness.CreateAsync();

        var byValue = await harness.RunAsync($"'{harness.Company.JanKowalski}','{harness.Company.ItOu}' | Get-AdmObject");
        var byProperty = await harness.RunAsync("Get-AdmUser -Department IT | Get-AdmObject -Properties title");

        Assert.Equal(["User", "OrganizationalUnit"], byValue.Texts("ObjectKind"));
        Assert.Equal(["Administrator systemów", "Programista"], byProperty.Texts("title").Order().ToArray());
    }

    [Fact]
    public async Task Get_AdmObject_with_a_raw_filter_searches_below_the_base_and_checks_the_syntax_first()
    {
        await using var harness = await ReadHarness.CreateAsync();

        var found = await harness.RunAsync($"Get-AdmObject -LdapFilter '(&(objectCategory=person)(department=IT))' -SearchBase '{harness.Company.CompanyOu}'");
        var unwrapped = await harness.RunAsync("Get-AdmObject -LdapFilter 'sAMAccountName=jan.kowalski'");
        harness.Directory.ClearOperations();
        var broken = await harness.RunAsync("try { Get-AdmObject -LdapFilter '(&(a=1)' } catch { $_.FullyQualifiedErrorId.Split(',')[0] + '|' + $_.Exception.Message }");

        Assert.Equal(["jan.kowalski", "lukasz.wisniewski"], found.Texts("sAMAccountName").Order().ToArray());
        Assert.Equal(["jan.kowalski"], unwrapped.Texts("sAMAccountName"));
        var message = broken.Single().ToString();
        Assert.StartsWith("AdmInvalidLdapFilter|The LDAP filter is not valid: Unbalanced parentheses.", message, StringComparison.Ordinal);
        Assert.Empty(harness.Directory.Operations);
    }

    [Fact]
    public async Task A_search_base_that_is_not_a_dn_or_does_not_exist_is_an_error_and_not_an_empty_result()
    {
        await using var harness = await ReadHarness.CreateAsync();

        var notDn = await harness.RunAsync("try { Get-AdmUser -SearchBase 'jan' } catch { $_.FullyQualifiedErrorId.Split(',')[0] }");
        var missing = await harness.RunAsync("Get-AdmUser -SearchBase 'OU=Nope,DC=contoso,DC=test'");

        Assert.Equal("AdmInvalidSearchBase", notDn.Single().ToString());
        Assert.Empty(missing.Objects);
        Assert.Contains("OU=Nope,DC=contoso,DC=test", missing.Errors, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_AdmGroup_filters_by_scope_and_category_and_reads_by_sam_name()
    {
        await using var harness = await ReadHarness.CreateAsync();

        var globalGroups = await harness.RunAsync($"Get-AdmGroup -GroupScope Global -SearchBase '{harness.Company.GroupsOu}'");
        var distribution = await harness.RunAsync("Get-AdmGroup -GroupCategory Distribution");
        var byName = await harness.RunAsync("Get-AdmGroup -Name 'GG_*' -GroupCategory Security");
        var identity = (await harness.RunAsync("Get-AdmGroup -Identity DL_Wszyscy")).Single();

        Assert.Equal(["GG_IT", "GG_Ksiegowosc", "GG_Sprzedaz"], globalGroups.Texts("Name").Order().ToArray());
        Assert.Equal(["DL_Wszyscy"], distribution.Texts("Name"));
        Assert.Equal(3, byName.Objects.Count);
        Assert.Equal("DomainLocal", identity.Properties["GroupScope"].Value);
        Assert.Equal("Distribution", identity.Properties["GroupCategory"].Value);
        Assert.Contains("ACCOUNT_GROUP", (string[])globalGroups.Objects[0].Properties["GroupTypeFlags"].Value!);
    }

    [Fact]
    public async Task Get_AdmComputer_reads_by_name_with_or_without_the_trailing_dollar_and_filters()
    {
        await using var harness = await ReadHarness.CreateAsync();

        var withDollar = (await harness.RunAsync("Get-AdmComputer -Identity 'WAW-PC001$'")).Single();
        var without = (await harness.RunAsync("Get-AdmComputer -Identity WAW-PC001")).Single();
        var windows11 = await harness.RunAsync("Get-AdmComputer -OperatingSystem 'Windows 11*' -SearchBase '" + harness.Company.ComputersOu + "'");
        var byLocation = await harness.RunAsync("Get-AdmComputer -Location 'Warszawa*'");
        var all = await harness.RunAsync("Get-AdmComputer");

        Assert.Contains("AdMng.Computer", withDollar.TypeNames);
        Assert.Equal(withDollar.Properties["DistinguishedName"].Value, without.Properties["DistinguishedName"].Value);
        Assert.Equal("WAW-PC001", withDollar.Properties["Name"].Value);
        Assert.Equal("Windows 11 Enterprise", withDollar.Properties["operatingSystem"].Value);
        Assert.Equal(true, withDollar.Properties["Enabled"].Value);
        Assert.Equal(["KRK-LT001", "WAW-PC001", "WAW-PC002"], windows11.Texts("Name").Order().ToArray());
        Assert.Equal(["WAW-PC001", "WAW-PC002"], byLocation.Texts("Name").Order().ToArray());
        Assert.Equal(4, all.Objects.Count);
    }

    [Fact]
    public async Task Get_AdmObject_asked_for_its_identity_takes_a_typed_distinguished_name_as_one_identity()
    {
        // Audit PS-05: the answer at the -Identity prompt was split at its commas into four identities that do not exist.
        await using var harness = await ReadHarness.CreateAsync();
        var jan = harness.Company.JanKowalski;
        var answers = new Queue<string>([jan, string.Empty]);
        var objects = new List<System.Management.Automation.PSObject>();
        var errors = new System.Text.StringBuilder();

        await foreach (var item in harness.Session.RunObjectsAsync("Get-AdmObject", TestContext.Current.CancellationToken))
        {
            switch (item)
            {
                case PowerShellPromptItem prompt:
                    prompt.Respond(answers.Dequeue());
                    break;
                case PowerShellObjectItem { Value: System.Management.Automation.PSObject value }:
                    objects.Add(value);
                    break;
                case PowerShellTextItem { Stream: PowerShellStream.Error } text:
                    errors.Append(text.Text);
                    break;
            }
        }

        Assert.True(errors.Length == 0, errors.ToString());
        var found = Assert.Single(objects);
        Assert.True(SameDn(jan, (string)found.Properties["DistinguishedName"].Value!));
        Assert.Empty(answers);
    }
}
