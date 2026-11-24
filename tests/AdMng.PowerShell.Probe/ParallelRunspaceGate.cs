using System.Diagnostics;
using System.Management.Automation;
using SmaPowerShell = System.Management.Automation.PowerShell;

namespace AdMng.PowerShell.Probe;

/// <summary>
/// Holds the thread that opens a runspace for a <c>ForEach-Object -Parallel</c> job at the one point where closing that runspace
/// ends the process. PowerShell 7.6 opens the runspace of each child of such a job on a pool thread of its own
/// (<c>PSTaskJob.Start</c> → <c>PSTaskPool.Add</c> → <c>GetRunspace</c>), where nothing catches an exception; <c>LocalRunspace.DoOpenHelper</c>
/// marks the runspace Opened, traces "runspace opened successfully" through the <c>RunspaceInit</c> trace source and only then binds the
/// initial session state (<c>InitialSessionState.BindRunspace</c>). A close of the runspace in between - what stopping or disposing the
/// job does to the runspaces of its pool - takes the engine away, and <c>BindRunspace</c> then fails with a NullReferenceException on that
/// pool thread. The gate is a trace listener on that line: armed, it holds such a thread there - the first, or a later one - until
/// <see cref="Release"/>.
/// </summary>
public sealed class ParallelRunspaceGate : TraceListener
{
    private const string OpenedLine = "runspace opened successfully";

    private readonly ManualResetEventSlim _held = new(false);
    private readonly ManualResetEventSlim _release = new(false);
    private readonly PSTraceSource _source;
    private readonly PSTraceSourceOptions _options;
    private readonly SourceLevels _level;
    /// <summary>How many more runspaces of parallel jobs open until the one that is held; 0 while the gate is not armed.</summary>
    private int _remaining;

    private ParallelRunspaceGate(PSTraceSource source)
    {
        _source = source;
        _options = source.Options;
        _level = source.Switch.Level;
    }

    /// <summary>The installed gate, for <see cref="WaitHeld"/> from a script.</summary>
    public static ParallelRunspaceGate? Current { get; private set; }

    /// <inheritdoc />
    public override bool IsThreadSafe => true;

    /// <summary>Turns the engine's <c>RunspaceInit</c> trace source on with this listener (the probe process only).</summary>
    public static ParallelRunspaceGate Install()
    {
        PSTraceSource source;
        using (var ps = SmaPowerShell.Create())
        {
            var found = ps.AddCommand("Get-TraceSource").AddParameter("Name", "RunspaceInit").Invoke();
            source = found.Count == 1 && found[0].BaseObject is PSTraceSource s
                ? s
                : throw new InvalidOperationException("The engine's RunspaceInit trace source was not found.");
        }

        // A listener that blocks must not hold the global lock of System.Diagnostics.Trace for every other trace of the process.
        Trace.UseGlobalLock = false;
        var gate = new ParallelRunspaceGate(source);
        source.Options = PSTraceSourceOptions.WriteLine;
        source.Switch.Level = SourceLevels.All;
        source.Listeners.Add(gate);
        Current = gate;
        return gate;
    }

    /// <summary>Whether a runspace is held now (and not yet released).</summary>
    public bool Held => _held.IsSet && !_release.IsSet;

    /// <summary>Holds a runspace that a parallel job opens: the next one, or the one after <paramref name="skip"/> others have opened.</summary>
    public void Arm(int skip = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        _held.Reset();
        _release.Reset();
        Volatile.Write(ref _remaining, skip + 1);
    }

    /// <summary>Waits until a runspace of a parallel job is held; called by the probe's script before its command ends.</summary>
    public static void WaitHeld()
    {
        var gate = Current ?? throw new InvalidOperationException("No gate is installed.");
        if (!gate._held.Wait(TimeSpan.FromSeconds(60)))
        {
            throw new TimeoutException("No runspace of a parallel job reached the gate.");
        }
    }

    /// <summary>Lets the held thread go on.</summary>
    public void Release() => _release.Set();

    /// <inheritdoc />
    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message) => Hold(message);

    /// <inheritdoc />
    /// <remarks><c>TraceSource.TraceInformation</c>, which the engine's trace source writes with, calls this overload.</remarks>
    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args) =>
        Hold(format);

    /// <inheritdoc />
    public override void Write(string? message)
    {
    }

    /// <inheritdoc />
    public override void WriteLine(string? message)
    {
    }

    private void Hold(string? message)
    {
        if (message is null
            || !message.Contains(OpenedLine, StringComparison.Ordinal)
            || Volatile.Read(ref _remaining) == 0
            || !new StackTrace().ToString().Contains("PSTaskPool.GetRunspace", StringComparison.Ordinal)
            || Interlocked.Decrement(ref _remaining) != 0)
        {
            return;
        }

        _held.Set();
        _release.Wait();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _release.Set();
            _source.Listeners.Remove(this);
            _source.Options = _options;
            _source.Switch.Level = _level;
            Current = null;
        }

        base.Dispose(disposing);
    }
}
