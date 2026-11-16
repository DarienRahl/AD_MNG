using System.Management.Automation;
using System.Reflection;
using AdMng.Core.Directory;
using AdMng.Core.PowerShell;
using AdMng.PowerShell.Commands;
using AdMng.Testing;

namespace AdMng.PowerShell.Tests;

/// <summary>D9: the registered AD Manager commands are exactly the read commands; the mechanism of the window context.</summary>
public sealed class CommandCatalogTests
{
    /// <summary>R1 builds the read commands of ADR-0010 decision 4 in part 2; part 1 has only Get-AdmVersion. R2 changes this list.</summary>
    private static readonly string[] Expected = ["Get-AdmVersion"];

    /// <summary>Verbs that change something or send something; no command of R1 may have one.</summary>
    private static readonly string[] WriteVerbs =
    [
        "Set", "New", "Remove", "Move", "Rename", "Add", "Enable", "Disable", "Unlock", "Reset", "Clear", "Restore", "Update",
        "Write", "Invoke", "Grant", "Revoke", "Copy", "Import", "Install", "Uninstall", "Register", "Unregister", "Send",
        "Start", "Stop", "Suspend", "Resume", "Publish", "Block", "Unblock", "Protect", "Unprotect", "Deny", "Approve",
    ];

    [Fact]
    public void The_catalog_lists_exactly_the_expected_read_commands()
    {
        Assert.Equal(Expected, AdmCommands.Names.ToArray());
        Assert.Equal(AdmCommands.Names.Count, AdmCommands.Names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(AdmCommands.Catalog, entry => Assert.Contains("-Adm", entry.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void Every_command_class_of_the_assembly_is_in_the_catalog_and_none_has_a_write_verb()
    {
        var found = typeof(AdmCommands).Assembly.GetTypes()
            .Select(type => (Type: type, Attribute: type.GetCustomAttribute<CmdletAttribute>()))
            .Where(entry => entry.Attribute is not null)
            .ToList();

        Assert.Equal(
            AdmCommands.Catalog.Select(entry => entry.Name).Order(StringComparer.Ordinal).ToArray(),
            found.Select(entry => $"{entry.Attribute!.VerbName}-{entry.Attribute.NounName}").Order(StringComparer.Ordinal).ToArray());
        foreach (var (type, attribute) in found)
        {
            Assert.DoesNotContain(attribute!.VerbName, WriteVerbs);
            Assert.Contains(AdmCommands.Catalog, entry => entry.Type == type);
            Assert.True(type.IsSubclassOf(typeof(AdmCmdlet)), $"{type.Name} is not an AdmCmdlet");
        }
    }

    [Fact]
    public async Task A_live_session_has_exactly_the_catalog_and_no_ad_drive_and_no_rsat()
    {
        await using var session = await TestSessions.CreateAsync();

        var commands = await TestSessions.RunAsync(session, "Get-Command -Name *-Adm* | ForEach-Object { $_.Name }");
        var adCommands = await TestSessions.RunAsync(session, "(Get-Command -Name *-AD* -CommandType Cmdlet,Function | Where-Object { $_.Name -notlike '*-Adm*' } | Measure-Object).Count");
        var drives = await TestSessions.RunAsync(session, "(Get-PSDrive | ForEach-Object { $_.Name }) -join ','");

        Assert.Equal(AdmCommands.Names.Order(StringComparer.Ordinal).ToArray(), TestSessions.Text(commands).Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("0\n", TestSessions.Text(adCommands));
        Assert.DoesNotContain("AD", TestSessions.Text(drives).Trim().Split(','));
    }

    [Fact]
    public async Task Get_AdmVersion_reads_the_window_through_the_host_and_reports_what_the_session_is()
    {
        var context = new FakeWindowContext();
        await using var session = await TestSessions.CreateAsync(context);

        var info = await ReadVersionAsync(session);

        Assert.Equal("1.2.3-test", info.ApplicationVersion);
        Assert.StartsWith("7.", info.EngineVersion, StringComparison.Ordinal);
        Assert.Equal(PowerShellLanguageMode.Constrained, info.LanguageMode);
        Assert.Equal(context.Session.Info.DomainDnsName, info.Domain);
        Assert.Equal(context.Session.Info.Server, info.DomainController);
        Assert.Equal(context.Session.Info.BoundIdentity, info.Account);
    }

    [Fact]
    public async Task Get_AdmVersion_follows_the_trusted_mode()
    {
        await using var session = await TestSessions.CreateAsync();
        await session.SetLanguageModeAsync(PowerShellLanguageMode.Full, TestContext.Current.CancellationToken);

        var info = await ReadVersionAsync(session);

        Assert.Equal(PowerShellLanguageMode.Full, info.LanguageMode);
    }

    [Fact]
    public async Task Without_a_window_Get_AdmVersion_still_answers_and_a_command_that_needs_the_window_says_so()
    {
        await using var session = await TestSessions.CreateAsync(withoutWindow: true, extra: TestCommands.All);

        var info = await ReadVersionAsync(session);
        var needs = await TestSessions.RunAsync(session, "Test-AdmContext");

        Assert.Equal(string.Empty, info.ApplicationVersion);
        Assert.Null(info.Domain);
        Assert.Contains("AD Manager PowerShell window", TestSessions.Text(needs, PowerShellStream.Error), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_command_gets_the_windows_directory_as_a_reader_that_is_not_the_session()
    {
        var context = new FakeWindowContext();
        await using var session = await TestSessions.CreateAsync(context, extra: TestCommands.All);

        var items = await TestSessions.RunAsync(session, "Test-AdmContext");

        Assert.Equal(context.Session.Info.DomainDnsName + "\nFalse\n", TestSessions.Text(items));
        Assert.IsNotAssignableFrom<IDirectorySession>(context.Reader);
    }

    [Fact]
    public async Task Two_sessions_give_each_command_its_own_windows_services()
    {
        var one = new FakeDirectorySession(new FakeDirectorySession().Info with { DomainDnsName = "one.test" });
        var two = new FakeDirectorySession(new FakeDirectorySession().Info with { DomainDnsName = "two.test" });
        await using var sessionOne = await TestSessions.CreateAsync(new FakeWindowContext(one));
        await using var sessionTwo = await TestSessions.CreateAsync(new FakeWindowContext(two));

        var first = await ReadVersionAsync(sessionOne);
        var second = await ReadVersionAsync(sessionTwo);

        Assert.Equal("one.test", first.Domain);
        Assert.Equal("two.test", second.Domain);
    }

    [Fact]
    public async Task A_script_finds_nothing_to_read_the_context_from()
    {
        await using var session = await TestSessions.CreateAsync();

        var items = await TestSessions.RunAsync(session, "$Host.PrivateData | Get-Member -MemberType Properties,Methods | Where-Object { $_.Name -ne 'ToString' -and $_.Name -ne 'GetType' -and $_.Name -ne 'Equals' -and $_.Name -ne 'GetHashCode' } | Measure-Object | ForEach-Object { $_.Count }");

        Assert.Equal("0\n", TestSessions.Text(items));
    }

    private static async Task<AdmVersionInfo> ReadVersionAsync(PowerShellSession session)
    {
        var found = new List<PowerShellOutputItem>();
        await foreach (var item in session.RunObjectsAsync("Get-AdmVersion", TestContext.Current.CancellationToken))
        {
            found.Add(item);
        }

        var value = Assert.Single(found.OfType<PowerShellObjectItem>()).Value;
        return Assert.IsType<AdmVersionInfo>(((PSObject)value!).BaseObject);
    }
}
