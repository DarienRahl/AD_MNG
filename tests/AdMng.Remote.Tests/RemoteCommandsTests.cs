using System.Collections;
using System.Xml.Linq;
using AdMng.Core.Computers.Remote;

namespace AdMng.Remote.Tests;

public class RemoteCommandsTests
{
    [Theory]
    [InlineData(LocalTool.RemoteDesktop, "mstsc.exe", "/v:krk-pc101.contoso.test")]
    [InlineData(LocalTool.ComputerManagement, "mmc.exe", "compmgmt.msc /computer:krk-pc101.contoso.test")]
    [InlineData(LocalTool.EventViewer, "eventvwr.exe", "krk-pc101.contoso.test")]
    public void Tool_CommandLines(LocalTool tool, string program, string arguments)
    {
        var (fileName, args) = RemoteCommands.Tool(tool, "KRK-PC101.contoso.test.", Path.Combine("C:", "Windows", "System32"));

        Assert.Equal(Path.Combine("C:", "Windows", "System32", program), fileName);
        Assert.Equal(arguments, args);
        Assert.Equal($"\"{fileName}\" {arguments}", RemoteCommands.CommandLine(fileName, args));
    }

    [Theory]
    [InlineData("10.20.5.17")]
    [InlineData("fe80::1")]
    [InlineData("pc01.contoso.test /s")]
    [InlineData("pc01.contoso.test&calc")]
    [InlineData("")]
    public void Tool_RefusesAddressesAndAnythingButAHostName(string host)
    {
        var ex = Assert.Throws<RemoteActionException>(() => RemoteCommands.Tool(LocalTool.ComputerManagement, host, "C:"));
        Assert.Equal(RemoteErrorKind.NotSupported, ex.Kind);
    }

    [Fact]
    public void ConstantCommands()
    {
        Assert.Equal("gpupdate.exe /target:computer /force /wait:0", RemoteCommands.GpUpdateComputer);
        Assert.Equal("shutdown.exe /a", RemoteCommands.AbortShutdown);
        Assert.Equal(2, RemoteCommands.RestartFlags(force: false));
        Assert.Equal(6, RemoteCommands.RestartFlags(force: true));
        Assert.Equal(600u, RemoteCommands.RestartTimeout(TimeSpan.FromMinutes(10)));
        Assert.Equal(0u, RemoteCommands.RestartTimeout(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => RemoteCommands.RestartTimeout(TimeSpan.FromMinutes(1441)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RemoteCommands.RestartTimeout(TimeSpan.FromMinutes(-1)));
        Assert.Equal(0x80000000u, RemoteCommands.PlannedOtherReason);
    }

    [Fact]
    public void UserPolicyTask_RunsGpupdateForInteractiveUsers_AndExpires()
    {
        var now = new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.FromHours(1));

        var xml = XDocument.Parse(RemoteCommands.UserPolicyTaskXml(now));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.Equal("S-1-5-4", xml.Descendants(ns + "GroupId").Single().Value);
        Assert.Equal("gpupdate.exe", xml.Descendants(ns + "Command").Single().Value);
        Assert.Equal("/target:user /force", xml.Descendants(ns + "Arguments").Single().Value);
        // Started by its registration, not at a time of the workstation's clock the computer's clock may already have passed.
        var trigger = Assert.Single(xml.Descendants(ns + "Triggers").Single().Elements());
        Assert.Equal(ns + "RegistrationTrigger", trigger.Name);
        Assert.Equal("PT5S", trigger.Element(ns + "Delay")?.Value);
        Assert.Empty(xml.Descendants(ns + "StartBoundary"));
        Assert.Equal("2026-10-27T09:10:00Z", trigger.Element(ns + "EndBoundary")?.Value);
        Assert.Equal("PT0S", xml.Descendants(ns + "DeleteExpiredTaskAfter").Single().Value);
        Assert.Equal("LeastPrivilege", xml.Descendants(ns + "RunLevel").Single().Value);
        Assert.Matches("^AdMng-UserPolicyRefresh-[0-9a-f]{32}$", RemoteCommands.UserPolicyTaskName(Guid.NewGuid()));
    }

    [Fact]
    public void EnvironmentBlock_AddsRunAsInvoker_SortedAndNulTerminated()
    {
        var environment = new Hashtable
        {
            ["PATH"] = @"C:\Windows\System32",
            ["__COMPAT_LAYER"] = "RunAsAdmin",
            ["Bad=Name"] = "x",
            ["APPDATA"] = @"C:\Users\łukasz\AppData\Roaming",
        };

        var block = RemoteCommands.EnvironmentBlock(environment);

        Assert.Equal("APPDATA=C:\\Users\\łukasz\\AppData\\Roaming\0PATH=C:\\Windows\\System32\0__COMPAT_LAYER=RunAsInvoker\0\0", block);
    }
}
