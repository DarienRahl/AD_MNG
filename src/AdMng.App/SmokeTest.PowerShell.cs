using System.Globalization;
using System.Text;
using AdMng.Core.PowerShell;
using AdMng.PowerShell;
using AdMng.PowerShell.Commands;

namespace AdMng.App;

/// <summary>Track R, stage R1 (docs/dev/r1-powershell-window.md): the PowerShell engine and window in the smoke test.</summary>
internal static partial class SmokeTest
{
    /// <summary>
    /// Proves that the published <c>AdMng.exe</c> - a single file, self-contained - starts the embedded PowerShell engine (ADR-0010
    /// decision 1): a runspace is created, <c>1 + 1</c> and <c>Get-AdmVersion</c> run, the window starts in ConstrainedLanguage with the
    /// execution policy Restricted, the ten commands of AD Manager are registered, no write command exists, and the help of a
    /// command (the file beside the program) is found. No directory is involved: the commands that need one are covered by the UI tests.
    /// What it found goes to the report (the version, <c>$PSHOME</c>, the time of the first runspace), so that the log of a CI run says
    /// how the single-file layout behaves.
    /// </summary>
    private static async Task CheckPowerShellEngineAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await using var session = await PowerShellSession.CreateAsync(new PowerShellSessionOptions { Culture = CultureInfo.InvariantCulture }, CancellationToken.None).ConfigureAwait(true);
        var first = clock.Elapsed;

        var sum = await RunPowerShellAsync(session, "1 + 1").ConfigureAwait(true);
        Expect(sum == "2", $"'1 + 1' gave '{sum}'.");

        var mode = await RunPowerShellAsync(session, "$ExecutionContext.SessionState.LanguageMode").ConfigureAwait(true);
        Expect(mode == "ConstrainedLanguage", $"The window's language mode is '{mode}', not ConstrainedLanguage.");

        var policy = await RunPowerShellAsync(session, "Get-ExecutionPolicy").ConfigureAwait(true);
        Expect(policy == "Restricted", $"The execution policy is '{policy}', not Restricted.");

        var version = await RunPowerShellAsync(session, "Get-AdmVersion | Out-String").ConfigureAwait(true);
        Expect(version.Contains("EngineVersion", StringComparison.Ordinal) && version.Contains("Constrained", StringComparison.Ordinal), $"Get-AdmVersion gave '{version}'.");

        var commands = await RunPowerShellAsync(session, "(Get-Command -Name *-Adm* | Sort-Object Name | ForEach-Object { $_.Name }) -join ' '").ConfigureAwait(true);
        var expected = string.Join(' ', AdmCommands.Names.Order(StringComparer.OrdinalIgnoreCase));
        Expect(string.Equals(commands, expected, StringComparison.OrdinalIgnoreCase), $"The commands of AD Manager are '{commands}', expected '{expected}'.");

        var help = await RunPowerShellAsync(session, "Get-Help Get-AdmUser -Examples | Out-String").ConfigureAwait(true);
        Expect(help.Contains("Get-AdmUser -Department", StringComparison.Ordinal), "The help of Get-AdmUser has no example: its file is not beside the program.");

        var refused = await RunPowerShellAsync(session, "Import-Module Microsoft.PowerShell.Utility 2>&1 | Out-String").ConfigureAwait(true);
        Expect(refused.Contains("limited", StringComparison.OrdinalIgnoreCase), $"Import-Module was not refused: '{refused}'.");

        var home = await RunPowerShellAsync(session, "$PSHOME").ConfigureAwait(true);
        var second = await TimeASecondRunspaceAsync().ConfigureAwait(true);
        Console.Error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"smoke test: the PowerShell engine {PowerShellEnvironment.EngineHome} ($PSHOME '{home}'), the first runspace {first.TotalMilliseconds:0} ms, the second {second.TotalMilliseconds:0} ms"));
    }

    private static async Task<TimeSpan> TimeASecondRunspaceAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await using var session = await PowerShellSession.CreateAsync(new PowerShellSessionOptions { Culture = CultureInfo.InvariantCulture }, CancellationToken.None).ConfigureAwait(true);
        return clock.Elapsed;
    }

    private static async Task<string> RunPowerShellAsync(IPowerShellSession session, string script)
    {
        var text = new StringBuilder();
        await foreach (var item in session.RunAsync(script, CancellationToken.None).ConfigureAwait(true))
        {
            if (item is PowerShellTextItem output)
            {
                text.Append(output.Text);
            }
            else if (item is PowerShellRunEndItem { State: not PowerShellRunState.Completed } end)
            {
                throw new InvalidOperationException($"'{script}' ended as {end.State}: {text}");
            }
        }

        return text.ToString().Trim();
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("PowerShell engine: " + message);
        }
    }
}
