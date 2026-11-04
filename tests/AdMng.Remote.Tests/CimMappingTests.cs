using AdMng.Core.Computers.Remote;

namespace AdMng.Remote.Tests;

public class CimMappingTests
{
    private static Dictionary<string, object?> Bag(params (string Name, object? Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void OperatingSystem_TakesTheTypedValues_AndTheUbr()
    {
        var boot = new DateTime(2026, 10, 26, 7, 15, 0, DateTimeKind.Utc);

        var os = CimMapping.OperatingSystem(Bag(("Caption", "Microsoft Windows 11 Pro "), ("Version", "10.0.26100"), ("BuildNumber", "26100"), ("LastBootUpTime", boot)), 4652u);

        Assert.Equal(new RemoteOperatingSystem("Microsoft Windows 11 Pro", "10.0.26100", 26100, 4652, new DateTimeOffset(boot)), os);
        Assert.Equal("10.0.26100.4652", os.FullBuild);
    }

    [Fact]
    public void OperatingSystem_WithDmtfText_AndWithoutUbr()
    {
        var os = CimMapping.OperatingSystem(Bag(("caption", "Microsoft Windows Server 2016 Standard"), ("version", "10.0.14393"), ("buildnumber", "14393"), ("lastbootuptime", "20261026081500.000000+060")), null);

        Assert.Equal(14393, os.BuildNumber);
        Assert.Null(os.UpdateBuildRevision);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 8, 15, 0, TimeSpan.FromHours(1)), os.LastBoot);
        Assert.Equal("10.0.14393", os.FullBuild);
    }

    [Theory]
    [InlineData("20261027093000.123456+000", "2026-10-27T09:30:00.1234560+00:00")]
    [InlineData("20260115120000.000000-300", "2026-01-15T12:00:00.0000000-05:00")]
    [InlineData("20261027093000.123456+999", null)]
    [InlineData("2026102709300", null)]
    [InlineData("not a date at all, sorry!", null)]
    public void ParseDmtf(string text, string? expected) =>
        Assert.Equal(expected, CimMapping.ParseDmtf(text)?.ToString("o", System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void LoggedOnUsers_OnePerAccountAndSession_ConsoleMarked_FailedOwnersSkipped()
    {
        var early = new DateTime(2026, 10, 27, 6, 55, 0, DateTimeKind.Utc);
        var late = new DateTime(2026, 10, 27, 8, 0, 0, DateTimeKind.Utc);
        var users = CimMapping.LoggedOnUsers(
        [
            (Bag(("SessionId", 1u), ("CreationDate", late)), Bag(("Domain", "CONTOSO"), ("User", "zaneta.szymanska"), ("ReturnValue", 0u))),
            (Bag(("SessionId", 1u), ("CreationDate", early)), Bag(("Domain", "CONTOSO"), ("User", "zaneta.szymanska"), ("ReturnValue", 0u))),
            (Bag(("SessionId", 2u), ("CreationDate", late)), Bag(("Domain", "CONTOSO"), ("User", "jan.kowalski"), ("ReturnValue", 0u))),
            (Bag(("SessionId", 3u)), Bag(("ReturnValue", 2u))),
            (Bag(("SessionId", 4u)), null),
        ],
        @"contoso\ZANETA.SZYMANSKA");

        Assert.Equal(2, users.Count);
        var zaneta = Assert.Single(users, u => u.Account == @"CONTOSO\zaneta.szymanska");
        Assert.True(zaneta.IsConsole);
        Assert.Equal(1, zaneta.SessionId);
        Assert.Equal(new DateTimeOffset(early), zaneta.Since);
        Assert.False(Assert.Single(users, u => u.Account == @"CONTOSO\jan.kowalski").IsConsole);
    }

    [Fact]
    public void LoggedOnUsers_ConsoleUserWithoutDesktop_IsListed()
    {
        var users = CimMapping.LoggedOnUsers([], @"CONTOSO\łukasz.wiśniewski");

        var user = Assert.Single(users);
        Assert.Equal(@"CONTOSO\łukasz.wiśniewski", user.Account);
        Assert.True(user.IsConsole);
        Assert.Null(user.SessionId);
    }

    [Fact]
    public void Service_MapsTheProperties_AndTheStoppedAutomaticFilter()
    {
        var bits = CimMapping.Service(Bag(("Name", "BITS"), ("DisplayName", "Usługa inteligentnego transferu w tle (BITS)"), ("State", "Stopped"), ("StartMode", "Auto"), ("DelayedAutoStart", true), ("StartName", "LocalSystem")));
        var spooler = CimMapping.Service(Bag(("Name", "Spooler"), ("State", "Running"), ("StartMode", "Auto")));

        Assert.Equal(new RemoteService("BITS", "Usługa inteligentnego transferu w tle (BITS)", "Stopped", "Auto", true, "LocalSystem"), bits);
        Assert.True(bits.IsAutomaticAndStopped);
        Assert.False(spooler.DelayedAutoStart);
        Assert.False(spooler.IsAutomaticAndStopped);
    }

    [Fact]
    public void ScheduledTask_MapsThePrincipalStateAndSettings()
    {
        var task = CimMapping.ScheduledTask(Bag(
            ("TaskPath", @"\Księgowość\"),
            ("TaskName", "Eksport faktur – Łódź"),
            ("State", 3u),
            ("Principal", Bag(("UserId", @"CONTOSO\svc.ksiegowosc"), ("LogonType", 1u))),
            ("Settings", Bag(("Enabled", true)))));

        Assert.Equal(new RemoteScheduledTask(@"\Księgowość\", "Eksport faktur – Łódź", @"CONTOSO\svc.ksiegowosc", RemoteTaskLogonType.Password, RemoteTaskState.Ready, true), task);
        Assert.Equal(@"\Księgowość\Eksport faktur – Łódź", task.FullName);
        Assert.True(task.StoresPassword);
        Assert.DoesNotContain("svc.ksiegowosc", task.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Eksport", task.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScheduledTask_AGroupPrincipal_NoSettings_EnabledFromTheState()
    {
        var disabled = CimMapping.ScheduledTask(Bag(
            ("taskpath", @"\Microsoft\Windows\Defrag\"),
            ("taskname", "ScheduledDefrag"),
            ("state", 1u),
            ("principal", Bag(("UserId", " "), ("LogonType", 4u)))));
        var running = CimMapping.ScheduledTask(Bag(("TaskName", "Kopia zapasowa"), ("State", "Running"), ("Principal", Bag(("UserId", "SYSTEM"), ("LogonType", 5)))));

        Assert.Null(disabled.UserId);
        Assert.Equal(RemoteTaskLogonType.Group, disabled.LogonType);
        Assert.Equal(RemoteTaskState.Disabled, disabled.State);
        Assert.False(disabled.Enabled);
        Assert.Equal(@"\Microsoft\Windows\Defrag\ScheduledDefrag", disabled.FullName);
        Assert.False(disabled.StoresPassword);

        Assert.Equal(@"\", running.Path);
        Assert.Equal(@"\Kopia zapasowa", running.FullName);
        Assert.Equal("SYSTEM", running.UserId);
        Assert.Equal(RemoteTaskLogonType.ServiceAccount, running.LogonType);
        Assert.Equal(RemoteTaskState.Running, running.State);
        Assert.True(running.Enabled);
    }

    [Fact]
    public void ScheduledTask_UnknownValues_AreNull()
    {
        var task = CimMapping.ScheduledTask(Bag(("TaskPath", @"\"), ("TaskName", "Raport"), ("State", 42u), ("Principal", null)));

        Assert.Null(task.UserId);
        Assert.Null(task.LogonType);
        Assert.Null(task.State);
        Assert.Null(task.Enabled);
    }

    [Theory]
    [InlineData(6u, RemoteTaskLogonType.InteractiveOrPassword)]
    [InlineData("S4U", RemoteTaskLogonType.S4U)]
    [InlineData("interactive", RemoteTaskLogonType.Interactive)]
    [InlineData("3", RemoteTaskLogonType.Interactive)]
    [InlineData(7u, null)]
    [InlineData("Password, Group", null)]
    [InlineData("99999999999", null)]
    [InlineData("", null)]
    public void EnumValue_NumbersAndNames(object value, RemoteTaskLogonType? expected) =>
        Assert.Equal(expected, CimMapping.EnumValue<RemoteTaskLogonType>(value));

    [Fact]
    public void ScheduledTaskProperties_NeverCopyActionsOrTriggers()
    {
        Assert.DoesNotContain("Actions", RemoteCommands.ScheduledTaskProperties.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Triggers", RemoteCommands.ScheduledTaskProperties.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["UserId", "LogonType"], RemoteCommands.ScheduledTaskProperties["Principal"]!);
    }

    [Fact]
    public void GroupPolicyEnd_IsTheFileTimeOfBothHalves()
    {
        var end = new DateTimeOffset(2026, 10, 27, 9, 1, 0, TimeSpan.Zero);
        var fileTime = end.ToFileTime();

        Assert.Equal(end, CimMapping.GroupPolicyEnd((uint)(fileTime >> 32), (uint)(fileTime & 0xFFFFFFFF)));
        Assert.Null(CimMapping.GroupPolicyEnd(null, 5u));
        Assert.Null(CimMapping.GroupPolicyEnd(0u, 0u));
    }

    [Theory]
    [InlineData((byte)7, 7)]
    [InlineData(7u, 7)]
    [InlineData(7L, 7)]
    [InlineData(" 26100 ", 26100)]
    [InlineData("26100a", null)]
    [InlineData(4294967295u, null)]
    public void ToInt(object value, int? expected) => Assert.Equal(expected, CimMapping.ToInt(value));
}
