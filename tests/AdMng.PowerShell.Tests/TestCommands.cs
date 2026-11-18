using System.Management.Automation;
using System.Management.Automation.Runspaces;
using AdMng.PowerShell.Commands;

namespace AdMng.PowerShell.Tests;

/// <summary>
/// Test-only commands, registered through <c>PowerShellSessionOptions.ExtraCommands</c> (they are not in the program's
/// catalog, so the command list test does not see them).
/// </summary>
internal static class TestCommands
{
    public static IReadOnlyList<SessionStateCommandEntry> All { get; } =
    [
        new SessionStateCmdletEntry("Wait-AdmTest", typeof(WaitAdmTestCommand), null),
        new SessionStateCmdletEntry("Block-AdmTest", typeof(BlockAdmTestCommand), null),
        new SessionStateCmdletEntry("Test-AdmContext", typeof(TestAdmContextCommand), null),
    ];

    /// <summary>Set by a test to let <c>Block-AdmTest</c> return.</summary>
    public static ManualResetEventSlim Release { get; } = new(false);
}

/// <summary>Waits until Stop: the pattern of a command that reads the directory with <c>StopToken</c> (no real sleep).</summary>
[Cmdlet(VerbsLifecycle.Wait, "AdmTest")]
public sealed class WaitAdmTestCommand : AdmCmdlet
{
    protected override void ProcessRecord()
    {
        WriteObject("waiting");
        Wait(Task.Delay(Timeout.Infinite, StopToken).ContinueWith(_ => 0, TaskScheduler.Default));
        StopToken.ThrowIfCancellationRequested();
    }
}

/// <summary>Blocks in .NET code that ignores Stop: what the grace period is for.</summary>
[Cmdlet(VerbsSecurity.Block, "AdmTest")]
public sealed class BlockAdmTestCommand : AdmCmdlet
{
    protected override void ProcessRecord()
    {
        WriteObject("blocking");
        TestCommands.Release.Wait();
    }
}

/// <summary>A command that needs the window: what a command of part 2 does through <c>RequireContext</c>.</summary>
[Cmdlet(VerbsDiagnostic.Test, "AdmContext")]
public sealed class TestAdmContextCommand : AdmCmdlet
{
    protected override void ProcessRecord()
    {
        var context = RequireContext();
        WriteObject(context.Reader.Info.DomainDnsName);
        WriteObject(context.Reader is AdMng.Core.Directory.IDirectorySession);
    }
}
