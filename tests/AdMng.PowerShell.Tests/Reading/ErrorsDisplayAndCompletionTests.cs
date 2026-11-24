using AdMng.Core.Directory;
using AdMng.Core.PowerShell;
using AdMng.Testing;

namespace AdMng.PowerShell.Tests.Reading;

/// <summary>The errors of the read commands, the default display of their objects and the completion of their parameters (R1 part 2).</summary>
public sealed class ErrorsDisplayAndCompletionTests
{
    [Fact]
    public async Task An_object_that_is_not_found_is_a_non_terminating_error_that_names_it()
    {
        await using var harness = await ReadHarness.CreateAsync();
        const string Ghost = "CN=Duch,OU=IT,OU=Pracownicy,OU=Firma,DC=contoso,DC=test";

        var result = await harness.RunAsync(
            $"Get-AdmObject -Identity '{Ghost}' -ErrorVariable e -ErrorAction SilentlyContinue; $e[0].CategoryInfo.Category; $e[0].FullyQualifiedErrorId.Split(',')[0]; $e[0].TargetObject; 'next statement ran'");

        Assert.Equal(["ObjectNotFound", "AdmObjectNotFound", Ghost, "next statement ran"], result.Objects.Select(o => o.ToString()).ToArray());
        Assert.Equal(PowerShellRunState.Completed, result.End.State);
    }

    [Fact]
    public async Task The_message_of_a_not_found_error_has_the_dn_and_a_failing_command_does_not_end_the_script()
    {
        await using var harness = await ReadHarness.CreateAsync();

        var result = await harness.RunAsync("Get-AdmUser -Identity 'CN=Duch,DC=contoso,DC=test'; Get-AdmUser -Identity jan.kowalski");

        Assert.Contains("CN=Duch,DC=contoso,DC=test", result.Errors, StringComparison.Ordinal);
        Assert.Equal(["jan.kowalski"], result.Texts("sAMAccountName"));
        Assert.True(result.End.HadErrors);
    }

    [Fact]
    public async Task A_read_the_account_may_not_do_is_a_permission_error_that_names_where()
    {
        await using var harness = await ReadHarness.CreateAsync();
        harness.Directory.FailNext(FakeOperationKind.Search, harness.Company.ItOu, DirectoryErrorKind.AccessDenied);

        var result = await harness.RunAsync($"Get-AdmUser -SearchBase '{harness.Company.ItOu}' -ErrorVariable e -ErrorAction SilentlyContinue; $e[0].CategoryInfo.Category; $e[0].Exception.Message");

        var shown = result.Objects.Select(o => o.ToString()).ToArray();
        Assert.Equal("PermissionDenied", shown[0]);
        Assert.StartsWith($"Access denied reading '{harness.Company.ItOu}': the account of this window may not read it.", shown[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_directory_that_does_not_answer_is_one_error_and_the_next_command_works()
    {
        await using var harness = await ReadHarness.CreateAsync();
        harness.Directory.FailNext(FakeOperationKind.Search, null, DirectoryErrorKind.Timeout);

        var failed = await harness.RunAsync("Get-AdmUser");
        var next = await harness.RunAsync("Get-AdmUser -Identity jan.kowalski");

        Assert.Contains("did not answer the read", failed.Errors, StringComparison.Ordinal);
        Assert.Empty(failed.Objects);
        Assert.Equal(["jan.kowalski"], next.Texts("sAMAccountName"));
    }

    /// <summary>
    /// Audit ps-commands-1: a read by identity that the directory failed (a timeout, a refusal) also said the object does not exist, so a
    /// script that goes on at "not found" was told an existing account is missing. It is one error, the directory's.
    /// </summary>
    [Theory]
    [InlineData(DirectoryErrorKind.Timeout, "AdmTimeout", "Get-AdmUser")]
    [InlineData(DirectoryErrorKind.AccessDenied, "AdmAccessDenied", "Get-AdmUser")]
    [InlineData(DirectoryErrorKind.Timeout, "AdmTimeout", "Get-AdmObject")]
    [InlineData(DirectoryErrorKind.ServerUnavailable, "AdmServerUnavailable", "Get-AdmGroup")]
    [InlineData(DirectoryErrorKind.Timeout, "AdmTimeout", "Get-AdmComputer")]
    public async Task A_read_by_identity_that_the_directory_failed_is_one_error_of_the_directory_not_also_not_found(DirectoryErrorKind kind, string id, string command)
    {
        await using var harness = await ReadHarness.CreateAsync();
        harness.Directory.FailNext(FakeOperationKind.Search, null, kind);

        var result = await harness.RunAsync($"{command} -Identity jan.kowalski -ErrorVariable e -ErrorAction SilentlyContinue; $e.Count; $e | ForEach-Object {{ $_.FullyQualifiedErrorId.Split(',')[0] }}");

        Assert.Equal(["1", id], result.Objects.Select(o => o.ToString()).ToArray());
    }

    [Fact]
    public async Task An_identity_that_does_not_exist_is_still_one_not_found_error()
    {
        await using var harness = await ReadHarness.CreateAsync();

        var result = await harness.RunAsync("Get-AdmUser -Identity nobody.here -ErrorVariable e -ErrorAction SilentlyContinue; $e.Count; $e | ForEach-Object { $_.FullyQualifiedErrorId.Split(',')[0] }");

        Assert.Equal(["1", "AdmObjectNotFound"], result.Objects.Select(o => o.ToString()).ToArray());
    }

    /// <summary>
    /// Audit ps-commands-7: the message says it is shown once, but it was shown once per identity - 5,000 members piped into Get-AdmUser
    /// made 5,000 such errors. It is shown once per command, whether the identities come as a list or through the pipeline.
    /// </summary>
    [Theory]
    [InlineData("Get-AdmUser -Identity '{0}','{1}'")]
    [InlineData("'{0}','{1}' | Get-AdmUser")]
    [InlineData("'{0}','{1}' | Get-AdmObject")]
    public async Task A_hidden_mandatory_attribute_is_reported_once_per_command_not_once_per_identity(string script)
    {
        await using var harness = await ReadHarness.CreateAsync();
        harness.Directory.HideAttributes(harness.Company.JanKowalski, "sAMAccountName");
        harness.Directory.HideAttributes(harness.Company.LukaszWisniewski, "sAMAccountName");
        var command = string.Format(System.Globalization.CultureInfo.InvariantCulture, script, harness.Company.JanKowalski, harness.Company.LukaszWisniewski);

        var result = await harness.RunAsync(command + " -ErrorVariable e -ErrorAction SilentlyContinue; $e.Count");

        Assert.Equal(2, result.Objects.Count(o => o.Properties["DistinguishedName"] is not null));
        Assert.Equal("1", result.Objects[^1].ToString());
    }

    [Fact]
    public async Task A_mandatory_attribute_the_account_may_not_read_is_reported_once_with_its_name_and_the_objects_still_arrive()
    {
        await using var harness = await ReadHarness.CreateAsync();
        harness.Directory.HideAttributes(harness.Company.JanKowalski, "sAMAccountName");
        harness.Directory.HideAttributes(harness.Company.LukaszWisniewski, "sAMAccountName");

        var result = await harness.RunAsync("Get-AdmUser -Department IT -ErrorVariable e -ErrorAction SilentlyContinue; $e.Count; $e[0].CategoryInfo.Category; $e[0].FullyQualifiedErrorId.Split(',')[0]");

        Assert.Equal(2, result.Objects.Count(o => o.TypeNames.Contains("AdMng.User")));
        var rest = result.Objects.Where(o => !o.TypeNames.Contains("AdMng.User")).Select(o => o.ToString()).ToArray();
        Assert.Equal(["1", "PermissionDenied", "AdmAttributeHidden"], rest);
    }

    [Fact]
    public async Task An_attribute_that_is_simply_not_set_is_no_error()
    {
        await using var harness = await ReadHarness.CreateAsync();

        var result = await harness.RunAsync("Get-AdmUser -Identity jan.kowalski -Properties mobile,manager,thumbnailPhoto");

        Assert.Equal(string.Empty, result.Errors);
        Assert.Null(result.Single().Properties["mobile"].Value);
    }

    [Fact]
    public async Task The_default_display_is_a_table_without_escape_sequences_with_the_distinguished_name_last_and_whole()
    {
        await using var harness = await ReadHarness.CreateAsync();
        harness.Session.OutputWidth = 160;

        var users = TestSessions.Text(await TestSessions.RunAsync(harness.Session, "Get-AdmUser -SearchBase 'OU=Firma,DC=contoso,DC=test'"));
        var groups = TestSessions.Text(await TestSessions.RunAsync(harness.Session, "Get-AdmGroup"));
        var computers = TestSessions.Text(await TestSessions.RunAsync(harness.Session, "Get-AdmComputer"));
        var objects = TestSessions.Text(await TestSessions.RunAsync(harness.Session, "Get-AdmObject -LdapFilter '(objectClass=organizationalUnit)'"));

        Assert.DoesNotContain('\u001b', users + groups + computers + objects);
        Assert.Matches(@"Name\s+sAMAccountName\s+Enabled\s+DistinguishedName", users);
        Assert.Matches(@"Name\s+GroupScope\s+GroupCategory\s+DistinguishedName", groups);
        Assert.Matches(@"Name\s+Enabled\s+operatingSystem\s+DistinguishedName", computers);
        Assert.Matches(@"ObjectKind\s+Name\s+DistinguishedName", objects);
        foreach (var dn in new[] { harness.Company.ZbigniewKazmierczak, harness.Company.JanKowalski, harness.Company.ZanetaSzymanska })
        {
            Assert.Contains(dn, users, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains(harness.Company.GroupAll, groups, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(harness.Company.Computer1, computers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(harness.Company.AccountingOu, objects, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('…', users + groups + objects);
    }

    [Fact]
    public async Task At_the_default_width_a_usual_distinguished_name_is_whole_and_a_long_one_is_cut_with_an_ellipsis_not_wrapped()
    {
        await using var harness = await ReadHarness.CreateAsync();

        var users = TestSessions.Text(await TestSessions.RunAsync(harness.Session, "Get-AdmUser -SearchBase 'OU=Firma,DC=contoso,DC=test'"));

        Assert.Contains(harness.Company.JanKowalski, users, StringComparison.OrdinalIgnoreCase);
        var rows = users.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(2).ToList();
        Assert.Equal(8, rows.Count);
        Assert.All(rows, row => Assert.True(row.Length <= 120, row));
        Assert.Contains(rows, row => row.EndsWith('…'));
    }

    [Fact]
    public async Task Format_List_shows_every_property_and_the_distinguished_name_first()
    {
        await using var harness = await ReadHarness.CreateAsync();

        var text = TestSessions.Text(await TestSessions.RunAsync(harness.Session, "Get-AdmUser -Identity jan.kowalski | Format-List"));

        Assert.StartsWith("DistinguishedName ", text.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("userAccountControl", text, StringComparison.Ordinal);
        Assert.Contains("UserAccountControlFlags", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', text);
    }

    [Fact]
    public async Task Properties_completes_attribute_names_from_the_schema_in_memory_without_reading_the_directory()
    {
        await using var harness = await ReadHarness.CreateAsync();
        harness.Directory.ClearOperations();

        const string Script = "Get-AdmUser -Properties dep";
        var completion = await harness.Session.CompleteAsync(Script, Script.Length, TestContext.Current.CancellationToken);
        const string Both = "Get-AdmUser -Properties mail,";
        var afterComma = await harness.Session.CompleteAsync(Both, Both.Length, TestContext.Current.CancellationToken);
        const string Search = "Search-AdmObject -Attribute sAM";
        var searchCompletion = await harness.Session.CompleteAsync(Search, Search.Length, TestContext.Current.CancellationToken);

        Assert.Contains(completion.Matches, m => m.Text == "department");
        Assert.All(completion.Matches, m => Assert.StartsWith("dep", m.Text, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(afterComma.Matches, m => m.Text == "department");
        Assert.Contains(searchCompletion.Matches, m => m.Text == "sAMAccountName");
        Assert.Empty(harness.Directory.Operations);
    }

    [Fact]
    public async Task A_function_of_the_operator_named_like_a_command_the_completer_uses_does_not_stand_in_for_it()
    {
        await using var harness = await ReadHarness.CreateAsync();
        await harness.RunAsync("function Get-Variable { throw 'hijacked' }");

        const string Script = "Get-AdmUser -Properties dep";
        var completion = await harness.Session.CompleteAsync(Script, Script.Length, TestContext.Current.CancellationToken);

        Assert.Contains(completion.Matches, m => m.Text == "department");
    }

    [Fact]
    public async Task Search_base_completes_the_naming_contexts_and_the_containers_the_window_already_showed_and_quotes_them()
    {
        await using var harness = await ReadHarness.CreateAsync();
        const string Script = "Get-AdmUser -SearchBase ";

        var before = await harness.Session.CompleteAsync(Script, Script.Length, TestContext.Current.CancellationToken);
        await harness.RunAsync($"Get-AdmObject -Identity '{harness.Company.AccountingOu}'");
        harness.Directory.ClearOperations();
        var after = await harness.Session.CompleteAsync(Script, Script.Length, TestContext.Current.CancellationToken);
        const string Prefix = "Get-AdmUser -SearchBase 'OU=Ksi";
        var typed = await harness.Session.CompleteAsync(Prefix, Prefix.Length, TestContext.Current.CancellationToken);

        Assert.Contains(before.Matches, m => m.Text == "DC=contoso,DC=test" || m.Text == "'DC=contoso,DC=test'");
        Assert.DoesNotContain(before.Matches, m => m.ListText.Contains("OU=Księgowość", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(after.Matches, m => m.ListText.Equals(harness.Company.AccountingOu, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(after.Matches, m => m.Text == $"'{harness.Company.AccountingOu}'");
        Assert.Contains(typed.Matches, m => m.ListText.Equals(harness.Company.AccountingOu, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(harness.Directory.Operations);
    }

    [Fact]
    public async Task The_command_names_and_their_parameters_complete()
    {
        await using var harness = await ReadHarness.CreateAsync();

        const string Command = "Get-Adm";
        var commands = await harness.Session.CompleteAsync(Command, Command.Length, TestContext.Current.CancellationToken);
        const string Parameter = "Get-AdmUser -Sear";
        var parameters = await harness.Session.CompleteAsync(Parameter, Parameter.Length, TestContext.Current.CancellationToken);
        const string Enum = "Get-AdmGroup -GroupScope ";
        var values = await harness.Session.CompleteAsync(Enum, Enum.Length, TestContext.Current.CancellationToken);

        Assert.Equal(
            ["Get-AdmComputer", "Get-AdmGroup", "Get-AdmGroupMember", "Get-AdmObject", "Get-AdmUser", "Get-AdmVersion"],
            commands.Matches.Select(m => m.Text).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["-SearchBase", "-SearchScope"], parameters.Matches.Select(m => m.Text).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["DomainLocal", "Global", "Universal"], values.Matches.Select(m => m.Text).Order(StringComparer.Ordinal).ToArray());
    }
}
