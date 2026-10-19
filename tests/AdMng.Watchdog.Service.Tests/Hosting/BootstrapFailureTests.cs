using AdMng.Watchdog.Service.Hosting;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace AdMng.Watchdog.Service.Tests.Hosting;

/// <summary>
/// A bootstrap file the host cannot even be built from (malformed JSON, an invalid setting value) must not end as an
/// unhandled crash or a line on the stderr nobody reads under the SCM: it is reported (Application-log event 1010 on
/// Windows) and the process exits with code 2.
/// </summary>
public sealed class BootstrapFailureTests
{
    [Fact]
    public void MalformedBootstrapJson_IsReported_WithExitCode2()
    {
        using var directory = new TestDataDirectory();
        var file = directory.File("appsettings.json");
        File.WriteAllText(file, """{ "Watchdog": { "Authorization": { "DeniedSids": [ "S-1-5-21-1-2-3-1104" ] "ViewerSids": [] } } }""");
        var reported = new List<string>();

        var exitCode = WatchdogHost.TryBuild(
            () => WatchdogHost.Build(["--environment", "Development"], builder => builder.Configuration.AddJsonFile(file, optional: false)),
            reported.Add,
            out var app);

        Assert.Null(app);
        Assert.Equal(StartupExitCodes.InvalidSettings, exitCode);
        var code = Assert.Single(reported);
        Assert.StartsWith("InvalidDataException", code, StringComparison.Ordinal);
        Assert.DoesNotContain("S-1-5-21", code, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInvalidSettingValue_IsReported_WithExitCode2()
    {
        using var directory = new TestDataDirectory();
        var reported = new List<string>();

        var exitCode = WatchdogHost.TryBuild(
            () => WatchdogHost.Build(["--environment", "Development", "--Watchdog:DataDirectory", directory.Path, "--Watchdog:Hosting:Server", "IIS"]),
            reported.Add,
            out var app);

        Assert.Null(app);
        Assert.Equal(StartupExitCodes.InvalidSettings, exitCode);
        Assert.Single(reported);
    }

    [Fact]
    public void TheFailureMessage_NamesTheExitCodeAndTheFile_ButNoValues()
    {
        var message = StartupFailureLog.Message("InvalidDataException");

        Assert.Contains("exit code 2", message, StringComparison.Ordinal);
        Assert.Contains("appsettings.json", message, StringComparison.Ordinal);
        Assert.Contains("InvalidDataException", message, StringComparison.Ordinal);
    }
}
