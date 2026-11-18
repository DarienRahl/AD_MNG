using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.PowerShell;
using AdMng.Testing;

namespace AdMng.PowerShell.Tests.Reading;

/// <summary>Get-AdmGroupMember and Search-AdmObject (R1 part 2).</summary>
public sealed class GroupMemberAndSearchTests
{
    /// <summary>Values that are special in an LDAP filter, with the escaped form RFC 4515 gives (the tests' own table, not the code's).</summary>
    private static readonly (string Value, string Escaped)[] HostileTable =
    [
        ("Łukasz (Kraków)*\\", @"Łukasz \28Kraków\29\2a\5c"),
        ("(", @"\28"),
        (")", @"\29"),
        ("*", @"\2a"),
        ("\\", @"\5c"),
        ("a\0b", @"a\00b"),
        ("x)(objectClass=*", @"x\29\28objectClass=\2a"),
        ("*)(|(cn=*", @"\2a\29\28|\28cn=\2a"),
        ("ąćęłńóśźż ĄĆĘŁŃÓŚŹŻ", "ąćęłńóśźż ĄĆĘŁŃÓŚŹŻ"),
    ];

    public static TheoryData<string, string> HostileValues { get; } = [.. HostileTable.Select(row => (row.Value, row.Escaped))];

    private static string SearchFilter(FakeDirectorySession directory) =>
        directory.Operations.Last(o => o.Kind == FakeOperationKind.Search).Search!.Filter;

    [Fact]
    public async Task Get_AdmGroupMember_lists_the_direct_members_with_their_kinds()
    {
        await using var harness = await ReadHarness.CreateAsync();

        var members = await harness.RunAsync("Get-AdmGroupMember -Identity GG_IT");
        var nestedGroups = await harness.RunAsync("Get-AdmGroupMember -Identity DL_Wszyscy");
        var identityByDn = await harness.RunAsync($"Get-AdmGroupMember -Identity '{harness.Company.GroupAccounting}'");

        Assert.Equal(["jan.kowalski", "lukasz.wisniewski"], members.Texts("sAMAccountName").Order().ToArray());
        Assert.All(members.Objects, o => Assert.Contains("AdMng.User", o.TypeNames));
        Assert.Equal(["GG_IT", "GG_Ksiegowosc", "GG_Sprzedaz"], nestedGroups.Texts("Name").Order().ToArray());
        Assert.All(nestedGroups.Objects, o => Assert.Contains("AdMng.Group", o.TypeNames));
        Assert.Equal(3, identityByDn.Objects.Count);
    }

    [Fact]
    public async Task Recursive_returns_the_nested_members_that_the_programs_all_nested_list_shows()
    {
        await using var harness = await ReadHarness.CreateAsync();

        var all = await harness.RunAsync("Get-AdmGroupMember -Identity DL_Wszyscy -Recursive");
        var leaves = await harness.RunAsync("Get-AdmGroupMember -Identity DL_Wszyscy -Recursive -ExcludeGroups");
        var directLeaves = await harness.RunAsync("Get-AdmGroupMember -Identity DL_Wszyscy -ExcludeGroups");

        Assert.Equal(11, all.Objects.Count);
        Assert.Equal(3, all.Objects.Count(o => o.TypeNames.Contains("AdMng.Group")));
        Assert.Equal(8, leaves.Objects.Count);
        Assert.All(leaves.Objects, o => Assert.Contains("AdMng.User", o.TypeNames));
        Assert.Empty(directLeaves.Objects);
    }

    [Fact]
    public async Task Recursive_sends_the_in_chain_search_the_program_sends_and_nothing_else()
    {
        await using var harness = await ReadHarness.CreateAsync();
        harness.Directory.ClearOperations();

        await harness.RunAsync("Get-AdmGroupMember -Identity DL_Wszyscy -Recursive");

        var searches = harness.Directory.Operations.Where(o => o.Kind == FakeOperationKind.Search).Select(o => o.Search!).ToList();
        Assert.Equal(2, searches.Count);
        Assert.StartsWith("(&(objectCategory=group)(sAMAccountName=DL_Wszyscy))", searches[0].Filter, StringComparison.Ordinal);
        Assert.Equal(LdapFilter.InChain(Attr.MemberOf, harness.Company.GroupAll), searches[1].Filter);
        Assert.Equal(harness.Directory.Info.DefaultNamingContext, searches[1].BaseDn);
        Assert.Equal(SearchScope.Subtree, searches[1].Scope);
    }

    [Fact]
    public async Task Recursive_ends_on_a_cycle_of_groups_and_lists_each_member_once()
    {
        await using var harness = await ReadHarness.CreateAsync((builder, company) =>
        {
            // GG_IT contains DL_Wszyscy, which contains GG_IT: a cycle through two groups.
            builder.AddMembers(company.GroupIt, company.GroupAll);
        });

        var result = await harness.RunAsync("Get-AdmGroupMember -Identity GG_IT -Recursive -ExcludeGroups");

        Assert.Equal(PowerShellRunState.Completed, result.End.State);
        var names = result.Texts("sAMAccountName");
        Assert.Equal(names.Distinct(StringComparer.OrdinalIgnoreCase).Count(), names.Length);
        Assert.Contains("jan.kowalski", names);
        Assert.Contains("pawel.lewandowski", names);
    }

    [Fact]
    public async Task Direct_members_beyond_the_range_of_one_read_are_all_listed()
    {
        await using var harness = await ReadHarness.CreateAsync();
        harness.Directory.MaxValueRange = 2;

        var result = await harness.RunAsync("Get-AdmGroupMember -Identity GG_Ksiegowosc");

        Assert.Equal(["grazyna.dabrowska", "malgorzata.wojcik", "zbigniew.kazmierczak"], result.Texts("sAMAccountName").Order().ToArray());
    }

    [Fact]
    public async Task A_member_that_cannot_be_read_is_one_warning_with_the_count_and_the_others_are_returned()
    {
        await using var harness = await ReadHarness.CreateAsync((builder, company) =>
        {
            builder.AddMembers(company.GroupIt, "CN=Gone,OU=Elsewhere,DC=other,DC=test", "CN=Gone Too,DC=other,DC=test");
        });

        var result = await harness.RunAsync("Get-AdmGroupMember -Identity GG_IT");

        Assert.Equal(2, result.Objects.Count);
        Assert.StartsWith("2 member(s) of ", result.Warnings, StringComparison.Ordinal);
        Assert.Contains("CN=Gone,OU=Elsewhere,DC=other,DC=test", result.Warnings, StringComparison.Ordinal);
        Assert.False(result.End.HadErrors);
    }

    [Fact]
    public async Task The_result_set_size_limits_the_direct_and_the_nested_members()
    {
        await using var harness = await ReadHarness.CreateAsync();

        var direct = await harness.RunAsync("Get-AdmGroupMember -Identity GG_Ksiegowosc -ResultSetSize 2");
        var nested = await harness.RunAsync("Get-AdmGroupMember -Identity DL_Wszyscy -Recursive -ResultSetSize 4");

        Assert.Equal(2, direct.Objects.Count);
        Assert.Equal(4, nested.Objects.Count);
    }

    [Fact]
    public async Task Get_AdmGroupMember_for_a_missing_group_or_a_user_is_not_found_and_names_it()
    {
        await using var harness = await ReadHarness.CreateAsync();

        var missing = await harness.RunAsync("Get-AdmGroupMember -Identity NoSuchGroup");
        var user = await harness.RunAsync("Get-AdmGroupMember -Identity jan.kowalski");

        Assert.Empty(missing.Objects);
        Assert.Contains("Cannot find a group with identity 'NoSuchGroup'", missing.Errors, StringComparison.Ordinal);
        Assert.Contains("'jan.kowalski'", user.Errors, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_AdmObject_by_name_attribute_and_filter()
    {
        await using var harness = await ReadHarness.CreateAsync();

        var exact = await harness.RunAsync("Search-AdmObject -Name 'Jan Kowalski'");
        var startsWith = await harness.RunAsync("Search-AdmObject -Name Ża -Match StartsWith");
        var contains = await harness.RunAsync("Search-AdmObject -Attribute department -Value 'ięgowo' -Match Contains");
        var endsWith = await harness.RunAsync("Search-AdmObject -Attribute sAMAccountName -Value '.kowalski','.wojcik' -Match EndsWith");
        var severalAttributes = await harness.RunAsync("Search-AdmObject -Attribute title,department -Value IT");
        var raw = await harness.RunAsync("Search-AdmObject -Filter '(&(objectCategory=person)(department=Sprzedaż))'");
        var byClass = await harness.RunAsync("Search-AdmObject -Name 'GG_' -Match StartsWith -ObjectClass group");
        var scoped = await harness.RunAsync($"Search-AdmObject -Attribute department -Value IT -SearchBase '{harness.Company.AccountingOu}'");

        Assert.Equal(["Jan Kowalski"], exact.Texts("Name"));
        Assert.Equal(["Żaneta Szymańska"], startsWith.Texts("Name"));
        Assert.Equal(["Grażyna Dąbrowska", "Małgorzata Wójcik", "Zbigniew Kaźmierczak"], contains.Texts("Name").Order().ToArray());
        Assert.Equal(["Jan Kowalski", "Małgorzata Wójcik"], endsWith.Texts("Name").Order().ToArray());
        Assert.Equal(["Jan Kowalski", "Łukasz Wiśniewski"], severalAttributes.Texts("Name").Order().ToArray());
        Assert.Equal(3, raw.Objects.Count);
        Assert.Equal(["GG_IT", "GG_Ksiegowosc", "GG_Sprzedaz"], byClass.Texts("Name").Order().ToArray());
        Assert.Empty(scoped.Objects);
    }

    [Theory]
    [MemberData(nameof(HostileValues))]
    public async Task A_hostile_value_is_escaped_per_rfc_4515_and_matches_only_itself(string value, string escaped)
    {
        await using var harness = await ReadHarness.CreateAsync(AddObjectsWithHostileDescriptions);
        var quoted = "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        harness.Directory.ClearOperations();

        var exact = await harness.RunAsync($"Search-AdmObject -Attribute description -Value {quoted}");
        var sentExact = SearchFilter(harness.Directory);
        var startsWith = await harness.RunAsync($"Search-AdmObject -Attribute description -Value {quoted} -Match StartsWith");
        var sentStartsWith = SearchFilter(harness.Directory);

        Assert.Equal($"(description={escaped})", sentExact);
        Assert.Equal($"(description={escaped}*)", sentStartsWith);
        Assert.Equal(string.Empty, exact.Errors);
        Assert.Equal([value], exact.Flat("description"));
        Assert.Equal([value], startsWith.Flat("description").Where(d => d == value).ToArray());
        Assert.All(startsWith.Flat("description"), d => Assert.StartsWith(value, d, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_name_with_a_star_a_bracket_and_a_backslash_finds_only_that_object_and_the_filter_is_escaped()
    {
        await using var harness = await ReadHarness.CreateAsync(AddObjectsWithHostileNames);
        harness.Directory.ClearOperations();

        var result = await harness.RunAsync("Search-AdmObject -Name 'Łukasz (Kraków)*\\'");

        Assert.Equal([@"Łukasz (Kraków)*\"], result.Texts("Name"));
        Assert.Equal(@"(name=Łukasz \28Kraków\29\2a\5c)", SearchFilter(harness.Directory));
    }

    [Fact]
    public async Task A_value_that_looks_like_a_filter_adds_no_results()
    {
        await using var harness = await ReadHarness.CreateAsync();

        var result = await harness.RunAsync("Search-AdmObject -Name 'Jan Kowalski)(objectClass=*'");
        var star = await harness.RunAsync("Search-AdmObject -Name '*'");
        var wildcardInGet = await harness.RunAsync("Get-AdmUser -Name 'Jan Kowalski)(sAMAccountName=*'");

        Assert.Empty(result.Objects);
        Assert.Empty(star.Objects);
        Assert.Empty(wildcardInGet.Objects);
        Assert.Equal(string.Empty, result.Errors + star.Errors + wildcardInGet.Errors);
    }

    [Fact]
    public async Task Search_AdmObject_refuses_a_broken_filter_a_secret_attribute_and_an_unknown_attribute_before_reading()
    {
        await using var harness = await ReadHarness.CreateAsync();
        harness.Directory.ClearOperations();

        var broken = await harness.RunAsync("try { Search-AdmObject -Filter '(cn=a' } catch { $_.FullyQualifiedErrorId.Split(',')[0] + '|' + $_.Exception.Message }");
        var secret = await harness.RunAsync("try { Search-AdmObject -Attribute unicodePwd -Value x } catch { $_.FullyQualifiedErrorId.Split(',')[0] }");
        var unknown = await harness.RunAsync("try { Search-AdmObject -Attribute noSuchAttribute -Value x } catch { $_.FullyQualifiedErrorId.Split(',')[0] }");
        var empty = await harness.RunAsync("try { Search-AdmObject -Attribute cn -Value '' } catch { $_.FullyQualifiedErrorId.Split(',')[0] }");
        var notAnAttribute = await harness.RunAsync("try { Search-AdmObject -Attribute 'cn=x' -Value a } catch { $_.FullyQualifiedErrorId.Split(',')[0] }");

        Assert.Equal("AdmInvalidLdapFilter|The LDAP filter is not valid: Unbalanced parentheses.", broken.Single().ToString());
        Assert.Equal("AdmSecretAttribute", secret.Single().ToString());
        Assert.Equal("AdmInvalidAttribute", unknown.Single().ToString());
        Assert.Contains("ParameterArgumentValidationError", empty.Single().ToString(), StringComparison.Ordinal);
        Assert.Equal("AdmInvalidAttribute", notAnAttribute.Single().ToString());
        Assert.Empty(harness.Directory.Operations);
    }

    private static void AddObjectsWithHostileDescriptions(FakeDirectoryBuilder builder, SampleCompany company)
    {
        // One group per hostile value (its description), and a neighbour of each that a leaked wildcard would also match.
        foreach (var (value, _) in HostileTable)
        {
            var id = Math.Abs(value.GetHashCode(StringComparison.Ordinal));
            builder.AddGroup($"G{id}", company.GroupsOu, attributes: new Dictionary<string, object?> { [Attr.Description] = value });
            builder.AddGroup($"G{id}x", company.GroupsOu, attributes: new Dictionary<string, object?> { [Attr.Description] = value + "-longer" });
        }
    }

    private static void AddObjectsWithHostileNames(FakeDirectoryBuilder builder, SampleCompany company)
    {
        foreach (var name in new[] { @"Łukasz (Kraków)*\", "Łukasz (Kraków)", "Łukasz (Kraków) x", @"Łukasz (Kraków)*\x", "Łukasz Kraków" })
        {
            builder.AddGroup(name, company.GroupsOu);
        }
    }
}
