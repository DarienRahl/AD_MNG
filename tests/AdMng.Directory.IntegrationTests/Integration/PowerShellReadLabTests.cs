using System.Management.Automation;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.PowerShell;
using AdMng.Directory.IntegrationTests.Infrastructure;
using AdMng.PowerShell;
using AdMng.PowerShell.Commands;

namespace AdMng.Directory.IntegrationTests.Integration;

/// <summary>
/// The read commands of the PowerShell window (R1 part 2) against the Samba lab through a real session: each test makes its own
/// data in this run's OU and reads it with <c>Get-Adm*</c> / <c>Search-AdmObject</c> as the window does (the session behind the
/// read-only reader of <see cref="AdmWindowContext"/>).
/// </summary>
public sealed class PowerShellReadLabTests(LabFixture lab)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Get_AdmUser_reads_the_administrator_of_the_lab_with_typed_values()
    {
        LabSettings.RequireLab();
        await using var window = await OpenAsync();

        var result = await window.RunAsync("Get-AdmUser -Identity Administrator -Properties memberOf,pwdLastSet,whenCreated,adminCount");

        Assert.True(result.End.State == PowerShellRunState.Completed && !result.End.HadErrors, result.Errors);
        var user = Assert.Single(result.Objects);
        Assert.Contains("AdMng.User", user.TypeNames);
        Assert.Equal("Administrator", user.Properties["sAMAccountName"].Value);
        Assert.Equal(true, user.Properties["Enabled"].Value);
        Assert.EndsWith("-500", (string)user.Properties["objectSid"].Value!, StringComparison.Ordinal);
        Assert.IsType<Guid>(user.Properties["objectGUID"].Value);
        Assert.IsType<DateTime>(user.Properties["whenCreated"].Value);
        Assert.Contains(((string[])user.Properties["memberOf"].Value!), dn => dn.StartsWith("CN=Domain Admins,", StringComparison.OrdinalIgnoreCase));
        Assert.True(DistinguishedName.AreEqual($"CN=Administrator,CN=Users,{LabSettings.DomainDn}", (string)user.Properties["DistinguishedName"].Value!));
    }

    [Fact]
    public async Task Get_AdmUser_filters_give_the_same_users_as_a_search_of_the_session()
    {
        LabSettings.RequireLab();
        await using var window = await OpenAsync();

        var result = await window.RunAsync($"Get-AdmUser -Department 'Księgowość' -SearchBase '{LabSettings.CompanyOu}'");

        var expected = new List<string>();
        await foreach (var user in lab.Admin.SearchAsync(
            new DirectorySearch
            {
                BaseDn = LabSettings.CompanyOu,
                Filter = LdapFilter.And("(sAMAccountType=805306368)", LdapFilter.Equal(Attr.Department, "Księgowość")),
                Attributes = [Attr.SamAccountName],
            },
            Ct))
        {
            expected.Add(user.GetString(Attr.SamAccountName)!);
        }

        Assert.NotEmpty(expected);
        Assert.Equal(expected.Order(StringComparer.OrdinalIgnoreCase).ToArray(), result.Objects.Select(o => (string)o.Properties["sAMAccountName"].Value!).Order(StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.Equal(string.Empty, result.Errors);
    }

    [Fact]
    public async Task Search_AdmObject_with_an_escaped_value_finds_only_that_object_on_a_real_dc()
    {
        LabSettings.RequireLab();
        var ou = await lab.CreateOuAsync(TestNames.Unique("Szukanie"));
        const string Hostile = @"Łukasz (Kraków)*\";
        var wanted = await CreateGroupAsync(ou, Hostile, description: Hostile);
        await CreateGroupAsync(ou, "Łukasz (Kraków)", description: "Łukasz (Kraków)");
        await CreateGroupAsync(ou, @"Łukasz (Kraków)*\x", description: @"Łukasz (Kraków)*\x");
        await CreateGroupAsync(ou, "Łukasz Kraków", description: "Łukasz Kraków");
        await CreateGroupAsync(ou, "Łukasz (Kraków) i inni", description: "x)(objectClass=*");
        await using var window = await OpenAsync();

        var byName = await window.RunAsync($"Search-AdmObject -Name 'Łukasz (Kraków)*\\' -SearchBase '{ou}'");
        var byDescription = await window.RunAsync($"Search-AdmObject -Attribute description -Value 'Łukasz (Kraków)*\\' -SearchBase '{ou}'");
        var starts = await window.RunAsync($"Search-AdmObject -Name 'Łukasz (Kraków)' -Match StartsWith -SearchBase '{ou}'");
        var looksLikeAFilter = await window.RunAsync($"Search-AdmObject -Attribute description -Value 'x)(objectClass=*' -SearchBase '{ou}'");
        var star = await window.RunAsync($"Search-AdmObject -Name '*' -SearchBase '{ou}'");

        Assert.Equal(string.Empty, byName.Errors + byDescription.Errors + starts.Errors + looksLikeAFilter.Errors + star.Errors);
        Assert.True(DistinguishedName.AreEqual(wanted, Assert.Single(byName.Dns())));
        Assert.Equal([Hostile], byName.Texts("Name"));
        Assert.Equal([Hostile], byDescription.Texts("Name"));
        Assert.Equal(4, starts.Objects.Count);
        Assert.Single(looksLikeAFilter.Objects);
        Assert.Empty(star.Objects);
    }

    [Fact]
    public async Task Get_AdmGroupMember_recursive_follows_nested_groups_on_a_real_dc_and_ends_on_a_cycle()
    {
        LabSettings.RequireLab();
        var ou = await lab.CreateOuAsync(TestNames.Unique("Zagnieżdżone"));
        var user = await lab.CreateUserAsync(ou, "Zofia Zagnieżdżona");
        var inner = await CreateGroupAsync(ou, "GG_Wewnetrzna", member: user);
        var middle = await CreateGroupAsync(ou, "GG_Srodkowa", member: inner);
        var outer = await CreateGroupAsync(ou, "GG_Zewnetrzna", member: middle);
        await using var window = await OpenAsync();

        var direct = await window.RunAsync($"Get-AdmGroupMember -Identity '{outer}'");
        var all = await window.RunAsync($"Get-AdmGroupMember -Identity '{outer}' -Recursive");
        var leaves = await window.RunAsync($"Get-AdmGroupMember -Identity '{outer}' -Recursive -ExcludeGroups");

        Assert.True(DistinguishedName.AreEqual(middle, Assert.Single(direct.Dns())));
        Assert.Equal(Normalized(inner, middle, user), Normalized(all.Dns()));
        Assert.Equal(Normalized(user), Normalized(leaves.Dns()));
        Assert.Contains("AdMng.Group", all.Objects.First(o => DistinguishedName.AreEqual((string)o.Properties["DistinguishedName"].Value!, inner)).TypeNames);

        // The outer group becomes a member of the inner one: a cycle. The DC ends the chain; every member is listed once.
        await lab.Admin.ModifyAsync(inner, [AttributeModification.Add(Attr.Member, outer)], null, Ct);
        var cyclic = await window.RunAsync($"Get-AdmGroupMember -Identity '{outer}' -Recursive -ExcludeGroups");
        Assert.Equal(Normalized(user), Normalized(cyclic.Dns()));
        Assert.Equal(string.Empty, all.Errors + leaves.Errors + cyclic.Errors);
    }

    [Fact]
    public async Task Get_AdmObject_reports_a_missing_object_and_a_hidden_container_the_way_a_script_expects()
    {
        LabSettings.RequireLab();
        var ou = await lab.CreateOuAsync(TestNames.Unique("Błędy"));
        var ghost = DistinguishedName.Parse(ou).Child("CN", "Nie istnieje").ToString();
        await using var window = await OpenAsync();

        var missing = await window.RunAsync($"Get-AdmObject -Identity '{ghost}' -ErrorVariable e -ErrorAction SilentlyContinue; $e[0].CategoryInfo.Category; $e[0].Exception.Message");
        var badBase = await window.RunAsync($"Get-AdmUser -SearchBase '{ghost}'");

        var shown = missing.Objects.Select(o => o.ToString()).ToArray();
        Assert.Equal("ObjectNotFound", shown[0]);
        Assert.Contains(ghost, shown[1], StringComparison.Ordinal);
        Assert.Contains(ghost, badBase.Errors, StringComparison.Ordinal);
    }

    /// <summary>Distinguished names in one spelling and one order, to compare lists of them.</summary>
    private static string[] Normalized(params string[] distinguishedNames) =>
        [.. distinguishedNames.Select(dn => DistinguishedName.Parse(dn).ToString().ToUpperInvariant()).Order(StringComparer.Ordinal)];

    private async Task<string> CreateGroupAsync(string ou, string name, string? member = null, string? description = null)
    {
        var dn = DistinguishedName.Parse(ou).Child("CN", name).ToString();
        var attributes = new Dictionary<string, IReadOnlyList<object>>
        {
            [Attr.ObjectClass] = ["top", ObjectClasses.Group],
            [Attr.SamAccountName] = [TestNames.SamAccountName()],
        };
        if (member is not null)
        {
            attributes[Attr.Member] = [member];
        }

        if (description is not null)
        {
            attributes[Attr.Description] = [description];
        }

        await lab.Admin.AddAsync(dn, attributes, Ct);
        return dn;
    }

    private async Task<Window> OpenAsync()
    {
        var session = await PowerShellSession.CreateAsync(
            new PowerShellSessionOptions { Context = new AdmWindowContext(lab.Admin, "lab") },
            Ct);
        return new Window(session);
    }

    /// <summary>A window session of the engine over the lab's administrator session, with the results of a script collected.</summary>
    private sealed class Window(PowerShellSession session) : IAsyncDisposable
    {
        public async Task<Result> RunAsync(string script)
        {
            var objects = new List<PSObject>();
            var errors = new System.Text.StringBuilder();
            PowerShellRunEndItem? end = null;
            await foreach (var item in session.RunObjectsAsync(script, Ct))
            {
                switch (item)
                {
                    case PowerShellObjectItem { Value: PSObject value }:
                        objects.Add(value);
                        break;
                    case PowerShellTextItem { Stream: PowerShellStream.Error } text:
                        errors.Append(text.Text);
                        break;
                    case PowerShellRunEndItem finished:
                        end = finished;
                        break;
                }
            }

            return new Result(objects, errors.ToString(), end ?? throw new InvalidOperationException("The run did not end."));
        }

        public ValueTask DisposeAsync() => session.DisposeAsync();
    }

    private sealed record Result(IReadOnlyList<PSObject> Objects, string Errors, PowerShellRunEndItem End)
    {
        public string[] Texts(string property) => [.. Objects.Select(o => Convert.ToString(o.Properties[property].Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)];

        public string[] Dns() => Texts("DistinguishedName");
    }
}
