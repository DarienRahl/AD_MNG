using AdMng.Core.Directory;
using AdMng.Core.PowerShell;
using AdMng.PowerShell.Commands;
using AdMng.Testing;

namespace AdMng.PowerShell.Tests;

/// <summary>A window context over a fake directory, built the way the program builds it (the reader is read-only).</summary>
internal sealed class FakeWindowContext : IAdmWindowContext
{
    private readonly AdmWindowContext _inner;

    public FakeWindowContext(FakeDirectorySession? session = null)
    {
        Session = session ?? new FakeDirectorySession();
        _inner = new AdmWindowContext(Session, "1.2.3-test");
    }

    public FakeDirectorySession Session { get; }

    public IDirectoryReader Reader => _inner.Reader;

    public string ApplicationVersion => _inner.ApplicationVersion;
}

/// <summary>A lockdown probe that reports what a test says (and counts how often it was asked).</summary>
internal sealed class FakeLockdownProbe : IPowerShellLockdownProbe
{
    public PowerShellLockdownState State { get; set; }

    public int Calls { get; private set; }

    public PowerShellLockdownState Probe()
    {
        Calls++;
        return State;
    }
}

internal static class TestSessions
{
    public static Task<PowerShellSession> CreateAsync(
        IAdmWindowContext? context = null,
        bool withoutWindow = false,
        IPowerShellLockdownProbe? probe = null,
        TimeProvider? time = null,
        IReadOnlyList<System.Management.Automation.Runspaces.SessionStateCommandEntry>? extra = null) =>
        PowerShellSession.CreateAsync(
            new PowerShellSessionOptions
            {
                Context = withoutWindow ? null : context ?? new FakeWindowContext(),
                LockdownProbe = probe ?? new FakeLockdownProbe(),
                TimeProvider = time ?? TimeProvider.System,
                ExtraCommands = extra ?? [],
                StopGracePeriod = TimeSpan.FromSeconds(5),
            },
            TestContext.Current.CancellationToken);

    /// <summary>Runs a script and collects every item up to and including the end item.</summary>
    public static async Task<List<PowerShellOutputItem>> RunAsync(IPowerShellSession session, string script, Func<PowerShellPromptItem, Task>? answer = null)
    {
        var items = new List<PowerShellOutputItem>();
        await foreach (var item in session.RunAsync(script, TestContext.Current.CancellationToken))
        {
            items.Add(item);
            if (item is PowerShellPromptItem prompt && answer is not null)
            {
                await answer(prompt);
            }
        }

        return items;
    }

    public static string Text(IEnumerable<PowerShellOutputItem> items, PowerShellStream stream = PowerShellStream.Output) =>
        string.Concat(items.OfType<PowerShellTextItem>().Where(t => t.Stream == stream).Select(t => t.Text));

    public static PowerShellRunEndItem End(IEnumerable<PowerShellOutputItem> items) => items.OfType<PowerShellRunEndItem>().Single();

    public static string Dump(IEnumerable<PowerShellOutputItem> items) =>
        string.Join(" | ", items.Select(i => i switch
        {
            PowerShellTextItem t => $"{t.Stream}:{t.Text.Replace("\n", "\\n", StringComparison.Ordinal)}",
            PowerShellRunEndItem e => $"END:{e.State}/{e.HadErrors}",
            _ => i.ToString() ?? string.Empty,
        }));
}
