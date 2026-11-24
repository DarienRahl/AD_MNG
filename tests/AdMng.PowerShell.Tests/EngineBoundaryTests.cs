using System.Text.Json;
using System.Text.RegularExpressions;

namespace AdMng.PowerShell.Tests;

/// <summary>
/// Where the engine is and is not (ADR-0010, CLAUDE.md layout): AdMng.App is the only program that references it - no Watchdog project,
/// no Presentation, no Core, so the Watchdog package can never contain it - and the one configuration file that ships beside the program
/// turns off the transcription and the module logging a user's own file could switch on, and leaves script block logging to Windows and
/// Group Policy (decision D-B, audit PS-04).
/// </summary>
public sealed partial class EngineBoundaryTests
{
    private static string Root()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AdMng.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }

    private static string[] Projects(string folder) =>
        [.. Directory.GetFiles(Path.Combine(Root(), folder), "*.csproj", SearchOption.AllDirectories).Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];

    [Fact]
    public void OnlyTheProgramAndTheTestsOfTheEngine_ReferenceTheEngineProject()
    {
        var referencing = Projects("src").Concat(Projects("tests"))
            .Where(file => File.ReadAllText(file).Contains("AdMng.PowerShell.csproj", StringComparison.Ordinal))
            .Select(file => Path.GetFileNameWithoutExtension(file))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // The program, the tests of the engine (and the child process they start), the lab tests that drive the real session.
        Assert.Equal(["AdMng.App", "AdMng.Directory.IntegrationTests", "AdMng.PowerShell.Probe", "AdMng.PowerShell.Tests"], referencing);
    }

    [Fact]
    public void TheSdkPackage_IsReferencedByTheEngineProjectOnly_AndNoWatchdogProjectNamesPowerShell()
    {
        var withPackage = Projects("src").Concat(Projects("tests"))
            .Where(file => File.ReadAllText(file).Contains("Microsoft.PowerShell.SDK", StringComparison.Ordinal))
            .Select(file => Path.GetFileNameWithoutExtension(file))
            .ToArray();
        Assert.Equal(["AdMng.PowerShell"], withPackage);

        foreach (var project in Projects("src").Concat(Projects("tests")).Where(file => Path.GetFileName(file).StartsWith("AdMng.Watchdog", StringComparison.Ordinal)))
        {
            // The comments of a project file may talk about Windows PowerShell 5.1 (the deploy scripts); an item or a reference may not name the engine.
            var text = Regex.Replace(File.ReadAllText(project), "<!--.*?-->", string.Empty, RegexOptions.Singleline);
            Assert.DoesNotContain("PowerShell", text, StringComparison.Ordinal);
        }

        // The scripts that build the Watchdog package and the CI step that publishes it know nothing of the engine.
        foreach (var file in new[] { "tools/packaging/build-deb.sh", "tools/packaging/test-deb-install.sh" })
        {
            var text = File.ReadAllText(Path.Combine(Root(), file));
            Assert.DoesNotContain("AdMng.PowerShell", text, StringComparison.Ordinal);
            Assert.DoesNotContain("System.Management.Automation", text, StringComparison.Ordinal);
        }
    }

    /// <summary>The program's all-users configuration file of the engine (published beside <c>AdMng.exe</c>, <c>$PSHOME</c>).</summary>
    internal static string ShippedConfigFile => Path.Combine(Root(), "src", "AdMng.App", "powershell.config.json");

    [Fact]
    public void TheConfigurationFileBesideTheProgram_TurnsOffTheTranscriptionAndModuleLoggingAUsersFileCouldSwitchOn_AndIsPublishedNotBundled()
    {
        var path = ShippedConfigFile;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var policies = document.RootElement.GetProperty("PowerShellPolicies");

        Assert.False(policies.GetProperty("Transcription").GetProperty("EnableTranscripting").GetBoolean());
        Assert.False(policies.GetProperty("ModuleLogging").GetProperty("EnableModuleLogging").GetBoolean());
        Assert.True(document.RootElement.GetProperty("DisableImplicitWinCompat").GetBoolean());

        // Script block logging is not the program's to decide (D-B): with the section in this file, set to anything, it would decide the
        // policy over the user's file and, set to false, switch off the engine's own logging of suspicious script blocks
        // (ScriptBlockLoggingTests). The engine matches the names without regard to case.
        Assert.DoesNotContain(policies.EnumerateObject(), policy => string.Equals(policy.Name, "ScriptBlockLogging", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("ScriptBlock", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);

        // Nothing in it turns anything on, and it names no directory.
        Assert.DoesNotContain("true", Regex.Replace(File.ReadAllText(path), "\"DisableImplicitWinCompat\": true", string.Empty), StringComparison.Ordinal);
        var project = File.ReadAllText(Path.Combine(Root(), "src", "AdMng.App", "AdMng.App.csproj"));
        Assert.Matches(Pattern(), project);
    }

    [GeneratedRegex("""<None Include="powershell\.config\.json"[^>]*CopyToPublishDirectory="PreserveNewest"[^>]*/>""")]
    private static partial Regex Pattern();
}
