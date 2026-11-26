using AdMng.Core.Directory;
using AdMng.Core.PowerShell;
using AdMng.PowerShell.Commands;
using AdMng.Testing;

namespace AdMng.PowerShell.Tests;

/// <summary>A window context over a fake directory, built the way the program builds it (the reader is read-only).</summary>
internal sealed class FakeWindowContext : IAdmWindowContext
{
    private readonly AdmWindowContext _inner;

    public FakeWindowContext(FakeDirectorySession? session = null, IAdmWindowServices? services = null)
    {
        Session = session ?? new FakeDirectorySession();
        _inner = new AdmWindowContext(Session, "1.2.3-test", services);
    }

    public FakeDirectorySession Session { get; }

    public IDirectoryReader Reader => _inner.Reader;

    public string ApplicationVersion => _inner.ApplicationVersion;

    public AdmKnownContainers KnownContainers => _inner.KnownContainers;

    public IAdmWindowServices Services => _inner.Services;
}

/// <summary>What the window's commands asked the program to do: the grids shown, the objects opened, the exports written (none really is).</summary>
internal sealed class RecordingWindowServices : IAdmWindowServices
{
    public List<PowerShellGridTable> Grids { get; } = [];

    public List<string> Opened { get; } = [];

    public List<(PowerShellGridTable Table, string? Path, char? Delimiter)> Exports { get; } = [];

    /// <summary>What <see cref="OpenObjectAsync"/> answers.</summary>
    public bool OpenResult { get; set; } = true;

    /// <summary>What <see cref="ExportListAsync"/> answers when it is given no path (the operator's choice in Save As); null is a cancelled dialog.</summary>
    public string? ChosenPath { get; set; }

    /// <summary>A call waits for this before it answers (a test that stops the command while it waits).</summary>
    public TaskCompletionSource? Hold { get; set; }

    public CancellationToken LastToken { get; private set; }

    /// <summary>Completes when the first call arrives (a test waits for it instead of sleeping, then stops the command).</summary>
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The most grids the program shows (as it does: <c>PowerShellWindowActions.MaxGridWindows</c>); a call past it is refused with
    /// <see cref="PowerShellGridLimitException"/>. Null shows every grid.
    /// </summary>
    public int? GridLimit { get; set; }

    public async Task ShowGridAsync(PowerShellGridTable table, CancellationToken cancellationToken)
    {
        LastToken = cancellationToken;
        Entered.TrySetResult();
        await WaitAsync(cancellationToken);
        if (GridLimit is { } limit && Grids.Count >= limit)
        {
            throw new PowerShellGridLimitException(limit);
        }

        Grids.Add(table);
    }

    public async Task<bool> OpenObjectAsync(string distinguishedName, CancellationToken cancellationToken)
    {
        LastToken = cancellationToken;
        await WaitAsync(cancellationToken);
        Opened.Add(distinguishedName);
        return OpenResult;
    }

    public async Task<string?> ExportListAsync(PowerShellGridTable table, string? path, char? delimiter, CancellationToken cancellationToken)
    {
        LastToken = cancellationToken;
        await WaitAsync(cancellationToken);
        Exports.Add((table, path, delimiter));
        return path ?? ChosenPath;
    }

    private async Task WaitAsync(CancellationToken cancellationToken)
    {
        if (Hold is { } hold)
        {
            await hold.Task.WaitAsync(cancellationToken);
        }
    }
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
        IReadOnlyList<System.Management.Automation.Runspaces.SessionStateCommandEntry>? extra = null,
        System.Globalization.CultureInfo? culture = null) =>
        PowerShellSession.CreateAsync(
            new PowerShellSessionOptions
            {
                Culture = culture,
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
