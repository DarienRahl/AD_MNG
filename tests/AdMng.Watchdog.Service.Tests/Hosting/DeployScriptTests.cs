using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using AdMng.Watchdog.Service.Tests.Infrastructure;

namespace AdMng.Watchdog.Service.Tests.Hosting;

/// <summary>
/// The install/uninstall scripts (spec 19.6). Windows PowerShell 5.1 – the default shell of Server 2019/2022 – reads a
/// script without BOM in the ANSI code page, where the UTF-8 bytes of "–" contain a typographic quote that ends a
/// double-quoted string early: the scripts must be pure ASCII. On Windows the scripts are parsed by powershell.exe and
/// the bootstrap merge of the install module is run against a file with manual settings.
/// </summary>
public sealed class DeployScriptTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Scripts => new(DeployFiles().Select(Path.GetFileName)!);

    [Theory]
    [MemberData(nameof(Scripts))]
    public void DeployScripts_ArePureAscii(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(DeployFolder(), name));

        var offending = bytes.Select((b, i) => (Byte: b, Index: i)).FirstOrDefault(x => x.Byte > 0x7F);
        Assert.True(
            offending == default,
            $"{name}: byte 0x{offending.Byte:X2} at offset {offending.Index} – Windows PowerShell 5.1 would decode it in the ANSI code page.");
    }

    [Fact]
    public void TheInstallModule_IsPublishedWithTheScripts()
    {
        var project = File.ReadAllText(Path.Combine(DeployFolder(), "..", "AdMng.Watchdog.Service.csproj"));

        Assert.Contains("AdMngWatchdog.Install.psm1", DeployFiles().Select(Path.GetFileName));
        Assert.Contains(@"Deploy\*.psm1", project, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    [SupportedOSPlatform("windows")]
    public async Task WindowsPowerShell51_ParsesTheScript(string name)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "powershell.exe (Windows PowerShell 5.1) exists on Windows only.");
        var path = Path.Combine(DeployFolder(), name);

        var output = await PowerShellAsync(
            "$errors = $null; [void][System.Management.Automation.Language.Parser]::ParseFile($args[0], [ref]$null, [ref]$errors); " +
            "$errors | ForEach-Object { 'line ' + $_.Extent.StartLineNumber + ': ' + $_.Message }; 'errors=' + @($errors).Count",
            path);

        Assert.Contains("errors=0", output, StringComparison.Ordinal);
    }

    /// <summary>Re-running the "install or update" script keeps every setting it was not given – above all the emergency
    /// revocation DeniedSids – and changes only what its parameters say.</summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Update_KeepsManualBootstrapSettings()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "powershell.exe (Windows PowerShell 5.1) exists on Windows only.");
        using var directory = new TestDataDirectory();
        var file = directory.File("appsettings.json");
        await File.WriteAllTextAsync(file, """
            {
              "Watchdog": {
                "DataDirectory": "D:\\AdMng\\dane",
                "Hosting": { "Server": "HttpSys", "Urls": "https://stary.contoso.test:8443", "Certificate": { "Subject": "stary.contoso.test", "Store": "WebHosting" } },
                "Authorization": {
                  "AdminSids": [ "S-1-5-21-1-2-3-1302" ], "ViewerSids": [ "S-1-5-21-1-2-3-1301" ],
                  "DeniedSids": [ "S-1-5-21-1-2-3-2101" ], "ViewerDetailLevel": "Minimal"
                },
                "Notifications": { "OAuthSmtpHosts": [ "smtp.office365.us" ] },
                "Directory": { "Server": "dc01.contoso.test" }
              }
            }
            """, Ct);

        var output = await PowerShellAsync(
            "Import-Module $args[0] -Force; " +
            "$tree = Read-AdMngBootstrap -Path $args[1]; " +
            "$set = [ordered]@{ 'Watchdog.Hosting.Urls' = 'https://admng-wd.contoso.test:8443'; 'Watchdog.Authorization.AdminSids' = @('S-1-5-21-1-2-3-1399') }; " +
            "$defaults = [ordered]@{ 'Watchdog.DataDirectory' = 'C:\\ProgramData\\AdMng\\Watchdog\\data'; 'Watchdog.Hosting.Server' = 'Kestrel'; 'Watchdog.Hosting.RequireKerberos' = $true; 'Watchdog.Hosting.MonitoringSubnets' = @() }; " +
            "$merged = Merge-AdMngBootstrap -Tree $tree -Set $set -Defaults $defaults; " +
            "'JSON=' + ($merged | ConvertTo-Json -Depth 10 -Compress)",
            Path.Combine(DeployFolder(), "AdMngWatchdog.Install.psm1"),
            file);

        var json = output[(output.IndexOf("JSON=", StringComparison.Ordinal) + 5)..].Trim();
        using var document = JsonDocument.Parse(json);
        var watchdog = document.RootElement.GetProperty("Watchdog");
        Assert.Equal(@"D:\AdMng\dane", watchdog.GetProperty("DataDirectory").GetString());
        Assert.Equal("HttpSys", watchdog.GetProperty("Hosting").GetProperty("Server").GetString());
        Assert.Equal("https://admng-wd.contoso.test:8443", watchdog.GetProperty("Hosting").GetProperty("Urls").GetString());
        Assert.Equal("WebHosting", watchdog.GetProperty("Hosting").GetProperty("Certificate").GetProperty("Store").GetString());
        Assert.True(watchdog.GetProperty("Hosting").GetProperty("RequireKerberos").GetBoolean());
        Assert.Equal(JsonValueKind.Array, watchdog.GetProperty("Hosting").GetProperty("MonitoringSubnets").ValueKind);
        var authorization = watchdog.GetProperty("Authorization");
        Assert.Equal(["S-1-5-21-1-2-3-1399"], authorization.GetProperty("AdminSids").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["S-1-5-21-1-2-3-1301"], authorization.GetProperty("ViewerSids").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["S-1-5-21-1-2-3-2101"], authorization.GetProperty("DeniedSids").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("Minimal", authorization.GetProperty("ViewerDetailLevel").GetString());
        Assert.Equal(["smtp.office365.us"], watchdog.GetProperty("Notifications").GetProperty("OAuthSmtpHosts").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("dc01.contoso.test", watchdog.GetProperty("Directory").GetProperty("Server").GetString());
    }

    private static IEnumerable<string> DeployFiles() =>
        System.IO.Directory.EnumerateFiles(DeployFolder()).Where(f => f.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".psm1", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal);

    private static string DeployFolder()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "AdMng.Watchdog.Service", "Deploy");
            if (System.IO.Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("src/AdMng.Watchdog.Service/Deploy was not found above the test output folder.");
    }

    [SupportedOSPlatform("windows")]
    private static async Task<string> PowerShellAsync(string script, params string[] arguments)
    {
        using var scratch = new TestDataDirectory();
        var file = scratch.File("probe.ps1");
        await File.WriteAllTextAsync(file, "[Console]::OutputEncoding = [Text.Encoding]::UTF8" + Environment.NewLine + script, Encoding.ASCII, Ct);
        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in (string[])["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file, .. arguments])
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("powershell.exe did not start.");
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        Assert.True(process.ExitCode == 0, $"powershell.exe exited with {process.ExitCode}: {await error}");
        return await output;
    }
}
