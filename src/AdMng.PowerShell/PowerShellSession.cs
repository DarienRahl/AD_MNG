using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AdMng.Core.PowerShell;
using AdMng.PowerShell.Commands;
using AdMng.PowerShell.Guards;
using AdMng.PowerShell.Hosting;
using AdMng.PowerShell.Input;
using SmaPowerShell = System.Management.Automation.PowerShell;

namespace AdMng.PowerShell;

/// <summary>
/// One window's PowerShell session (docs/dev/r1-powershell-window.md 4.1): one runspace made from the AD Manager initial
/// session state, never shared with another window, with the custom host, AD Manager's commands and the guards. It runs one
/// pipeline at a time and streams what the pipeline produces as <see cref="PowerShellOutputItem"/>s, in memory only.
/// Disposing it stops the pipeline and closes the runspace.
/// </summary>
public sealed class PowerShellSession : IPowerShellSession
{
    private const int ChannelCapacity = 4096;

    private readonly Runspace _runspace;
    private readonly WindowHost _host;
    private readonly WindowHostRawUserInterface _raw;
    private readonly IPowerShellLockdownProbe _probe;
    private readonly TimeProvider _time;
    private readonly TimeSpan _grace;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private ActiveRun? _run;
    private bool _running;
    private bool _disposed;
    private PowerShellLanguageMode _mode = PowerShellLanguageMode.Constrained;
    private PowerShellLockdownState _lockdown;

    private PowerShellSession(PowerShellSessionOptions options)
    {
        _probe = options.LockdownProbe;
        _time = options.TimeProvider;
        _grace = options.StopGracePeriod;
        _raw = new WindowHostRawUserInterface(options.OutputWidth);
        _host = new WindowHost(options.Context, ResolveVersion(options.Context), options.Culture ?? CultureInfo.CurrentCulture, _raw);
        _lockdown = _probe.Probe();
        _runspace = RunspaceFactory.CreateRunspace(_host, CreateInitialState(options));
        _runspace.ThreadOptions = PSThreadOptions.ReuseThread;
        if (OperatingSystem.IsWindows())
        {
            _runspace.ApartmentState = ApartmentState.STA;
        }

        _runspace.Open();
    }

    /// <summary>
    /// Creates the session on a pool thread: the first runspace of a process takes about half a second. The engine's
    /// environment (<see cref="PowerShellEnvironment"/>) is set before anything of the engine runs.
    /// </summary>
    public static Task<PowerShellSession> CreateAsync(PowerShellSessionOptions? options = null, CancellationToken cancellationToken = default)
    {
        PowerShellEnvironment.Apply();
        var resolved = options ?? new PowerShellSessionOptions();
        return Task.Run(() => new PowerShellSession(resolved), cancellationToken);
    }

    /// <inheritdoc />
    public PowerShellLanguageMode LanguageMode
    {
        get
        {
            lock (_sync)
            {
                return _mode;
            }
        }
    }

    /// <inheritdoc />
    public PowerShellTrustedModeBlock TrustedModeBlock
    {
        get
        {
            lock (_sync)
            {
                return _lockdown == PowerShellLockdownState.Enforce ? PowerShellTrustedModeBlock.SystemLockdown : PowerShellTrustedModeBlock.None;
            }
        }
    }

    /// <inheritdoc />
    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _running;
            }
        }
    }

    /// <inheritdoc />
    public int OutputWidth
    {
        get => _raw.Width;
        set => _raw.Width = value;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<PowerShellOutputItem> RunAsync(string script, CancellationToken cancellationToken) =>
        RunCoreAsync(script, objects: false, cancellationToken);

    /// <summary>
    /// Like <see cref="RunAsync"/>, but the pipeline's objects are not formatted: each arrives as a
    /// <see cref="PowerShellObjectItem"/> holding the <c>PSObject</c>. For tests and for programmatic callers; the window
    /// uses <see cref="RunAsync"/>.
    /// </summary>
    public IAsyncEnumerable<PowerShellOutputItem> RunObjectsAsync(string script, CancellationToken cancellationToken = default) =>
        RunCoreAsync(script, objects: true, cancellationToken);

    /// <inheritdoc />
    public async Task StopAsync()
    {
        ActiveRun? run;
        lock (_sync)
        {
            run = _run;
        }

        if (run is null)
        {
            return;
        }

        RequestStop(run);
        await Task.WhenAny(run.Completed.Task, run.Grace.Task).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<PowerShellCompletionResult> CompleteAsync(string input, int cursorIndex, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (_sync)
        {
            if (_disposed || _running)
            {
                return PowerShellCompletionResult.Empty;
            }
        }

        if (!_gate.Wait(0, CancellationToken.None))
        {
            return PowerShellCompletionResult.Empty;
        }

        try
        {
            return await Task.Run(() => Complete(input, cursorIndex), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<PowerShellSyntaxSpan> Tokenize(string input) => PowerShellSyntax.Tokenize(input);

    /// <inheritdoc />
    public bool IsInputComplete(string input) => PowerShellSyntax.IsComplete(input);

    /// <inheritdoc />
    public bool ShouldRemember(string line) => PowerShellHistoryPolicy.ShouldRemember(line);

    /// <inheritdoc />
    public async Task SetLanguageModeAsync(PowerShellLanguageMode mode, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The language mode cannot be changed while a command runs.");
        }

        try
        {
            lock (_sync)
            {
                if (_running)
                {
                    throw new InvalidOperationException("The language mode cannot be changed while a command runs.");
                }
            }

            if (mode == PowerShellLanguageMode.Full)
            {
                var state = _probe.Probe();
                lock (_sync)
                {
                    _lockdown = state;
                }

                if (state == PowerShellLockdownState.Enforce)
                {
                    throw new InvalidOperationException("A system lockdown (WDAC or AppLocker) enforces ConstrainedLanguage: the trusted mode is not available.");
                }
            }

            _runspace.SessionStateProxy.LanguageMode = mode == PowerShellLanguageMode.Full ? PSLanguageMode.FullLanguage : PSLanguageMode.ConstrainedLanguage;
            lock (_sync)
            {
                _mode = mode;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        ActiveRun? run;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            run = _run;
        }

        var abandoned = false;
        if (run is not null)
        {
            RequestStop(run);
            var finished = await Task.WhenAny(run.Completed.Task, run.Grace.Task).ConfigureAwait(false);
            abandoned = finished != run.Completed.Task;
        }

        if (abandoned)
        {
            // A command stuck in .NET code cannot be interrupted; Dispose would wait for it, so only ask the runspace to close.
            _runspace.CloseAsync();
            return;
        }

        await Task.Run(() =>
        {
            try
            {
                _runspace.Dispose();
            }
            catch (InvalidRunspaceStateException)
            {
                // Already closed.
            }
        }).ConfigureAwait(false);
    }

    private static Version ResolveVersion(IAdmWindowContext? context)
    {
        var text = context?.ApplicationVersion;
        if (!string.IsNullOrEmpty(text))
        {
            var core = text.Split('-', '+')[0];
            if (Version.TryParse(core, out var parsed))
            {
                return parsed;
            }
        }

        return typeof(PowerShellSession).Assembly.GetName().Version ?? new Version(0, 0);
    }

    private static InitialSessionState CreateInitialState(PowerShellSessionOptions options)
    {
        var state = InitialSessionState.CreateDefault();
        state.LanguageMode = PSLanguageMode.ConstrainedLanguage;
        state.ThrowOnRunspaceOpenError = true;

        // No .ps1 file runs in R1. The execution policy exists on Windows only (setting it elsewhere throws).
        if (OperatingSystem.IsWindows())
        {
            state.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Restricted;
        }

        // Module auto-loading off: a command that is not in the session is not found, and discovery writes no analysis cache.
        state.Variables.Add(new SessionStateVariableEntry(
            "PSModuleAutoLoadingPreference",
            PSModuleAutoLoadingPreference.None,
            "Modules are not loaded automatically in the AD Manager PowerShell window."));
        CommandGuards.Apply(state);
        foreach (var entry in AdmCommands.Entries())
        {
            state.Commands.Add(entry);
        }

        foreach (var entry in options.ExtraCommands)
        {
            state.Commands.Add(entry);
        }

        return state;
    }

    private PowerShellCompletionResult Complete(string input, int cursorIndex)
    {
        using var ps = SmaPowerShell.Create();
        ps.Runspace = _runspace;
        var result = CommandCompletion.CompleteInput(input, Math.Clamp(cursorIndex, 0, input.Length), options: null, ps);
        var matches = new List<PowerShellCompletionMatch>(result.CompletionMatches.Count);
        foreach (var match in result.CompletionMatches)
        {
            if (KindOf(match.ResultType) is { } kind)
            {
                matches.Add(new PowerShellCompletionMatch(match.CompletionText, match.ListItemText, kind, match.ToolTip));
            }
        }

        return matches.Count == 0
            ? PowerShellCompletionResult.Empty
            : new PowerShellCompletionResult(result.ReplacementIndex, result.ReplacementLength, matches);
    }

    /// <summary>The kind of a match, or null for the file system kinds that R1 does not offer.</summary>
    private static PowerShellCompletionKind? KindOf(CompletionResultType type) => type switch
    {
        CompletionResultType.ProviderItem or CompletionResultType.ProviderContainer => null,
        CompletionResultType.Command => PowerShellCompletionKind.Command,
        CompletionResultType.ParameterName => PowerShellCompletionKind.Parameter,
        CompletionResultType.ParameterValue => PowerShellCompletionKind.ParameterValue,
        CompletionResultType.Variable => PowerShellCompletionKind.Variable,
        CompletionResultType.Property => PowerShellCompletionKind.Property,
        CompletionResultType.Method => PowerShellCompletionKind.Method,
        CompletionResultType.Type or CompletionResultType.Namespace => PowerShellCompletionKind.Type,
        CompletionResultType.Keyword or CompletionResultType.DynamicKeyword => PowerShellCompletionKind.Keyword,
        CompletionResultType.History => PowerShellCompletionKind.History,
        _ => PowerShellCompletionKind.Other,
    };

    private async IAsyncEnumerable<PowerShellOutputItem> RunCoreAsync(
        string script,
        bool objects,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(script);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
            {
                throw new InvalidOperationException("A command is already running in this window.");
            }

            _running = true;
        }

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_sync)
            {
                _running = false;
            }

            throw;
        }

        ActiveRun run;
        try
        {
            run = StartRun(script, objects);
        }
        catch
        {
            lock (_sync)
            {
                _running = false;
            }

            _gate.Release();
            throw;
        }

        using var registration = cancellationToken.Register(() => RequestStop(run));
        try
        {
            var reader = run.Channel.Reader;
            while (true)
            {
                var read = reader.WaitToReadAsync(CancellationToken.None).AsTask();
                var first = await Task.WhenAny(read, run.Grace.Task).ConfigureAwait(false);
                if (first != read)
                {
                    // The run was stopped and did not end within the grace period: the window stops waiting for it.
                    yield return new PowerShellRunEndItem(PowerShellRunState.Stopped, HadErrors: false);
                    yield break;
                }

                if (!await read.ConfigureAwait(false))
                {
                    yield break;
                }

                while (reader.TryRead(out var item))
                {
                    yield return item;
                }
            }
        }
        finally
        {
            if (!run.Completed.Task.IsCompleted)
            {
                // The consumer left before the end item (it broke out of its loop): stop the run and wait for it.
                RequestStop(run);
                await Task.WhenAny(run.Completed.Task, run.Grace.Task).ConfigureAwait(false);
            }
        }
    }

    private ActiveRun StartRun(string script, bool objects)
    {
        var channel = Channel.CreateBounded<PowerShellOutputItem>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var stop = new CancellationTokenSource();
        var sink = new OutputSink(channel.Writer, stop.Token);
        var ps = SmaPowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript(script, useLocalScope: false);
        var output = new PSDataCollection<PSObject>();
        if (objects)
        {
            output.DataAdded += (_, e) => sink.Post(new PowerShellObjectItem(output[e.Index]));
            ps.Streams.Error.DataAdded += (_, e) => sink.Post(new PowerShellTextItem(PowerShellStream.Error, ps.Streams.Error[e.Index].ToString() + "\n"));
        }
        else
        {
            // The way the console host runs a command: the pipeline ends in Out-Default, which formats with the engine's own
            // formatting and writes to the host; errors are merged into it so they are formatted the same way.
            ps.AddCommand("Out-Default");
            ps.Commands.Commands[^1].MergeUnclaimedPreviousCommandResults = PipelineResultTypes.Error | PipelineResultTypes.Output;
        }

        var run = new ActiveRun(ps, channel, stop, sink);
        lock (_sync)
        {
            _run = run;
        }

        _host.Output.Attach(sink);
        try
        {
            var started = ps.BeginInvoke<PSObject, PSObject>(null, output);
            var invoke = Task.Factory.FromAsync(started, ps.EndInvoke);
            _ = invoke.ContinueWith(task => FinishRun(run, task), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
        catch
        {
            _host.Output.Attach(null);
            lock (_sync)
            {
                _run = null;
            }

            ps.Dispose();
            throw;
        }

        return run;
    }

    private void FinishRun(ActiveRun run, Task<PSDataCollection<PSObject>> invoke)
    {
        var stopped = Volatile.Read(ref run.StopRequested) == 1;
        var state = PowerShellRunState.Completed;
        var hadErrors = run.Sink.HadErrorText || run.Ps.HadErrors;
        if (invoke.IsFaulted)
        {
            var error = invoke.Exception!.GetBaseException();
            if (error is PipelineStoppedException || stopped)
            {
                state = PowerShellRunState.Stopped;
            }
            else
            {
                state = PowerShellRunState.Failed;
                hadErrors = true;
                run.Sink.Post(new PowerShellTextItem(PowerShellStream.Error, ErrorText.Describe(error) + "\n"));
            }
        }
        else if (stopped || run.Ps.InvocationStateInfo.State == PSInvocationState.Stopped)
        {
            state = PowerShellRunState.Stopped;
        }

        // The session is idle before the consumer sees the end item, so it can run the next command at once.
        _host.Output.Attach(null);
        lock (_sync)
        {
            _run = null;
            _running = false;
        }

        run.Ps.Dispose();
        _gate.Release();
        run.Sink.Post(new PowerShellRunEndItem(state, hadErrors));

        // Completed first: a consumer that sees the channel end must also see the run as finished.
        run.Completed.TrySetResult();
        run.Channel.Writer.TryComplete();
    }

    private void RequestStop(ActiveRun run)
    {
        if (Interlocked.Exchange(ref run.StopRequested, 1) == 1)
        {
            return;
        }

        run.Stop.Cancel();
        try
        {
            run.Ps.BeginStop(null, null);
        }
        catch (ObjectDisposedException)
        {
            // The run just ended.
        }
        catch (InvalidOperationException)
        {
            // The pipeline is not running (any more).
        }

        _ = Task.Delay(_grace, _time).ContinueWith(_ => run.Grace.TrySetResult(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private sealed class ActiveRun
    {
        public ActiveRun(SmaPowerShell ps, Channel<PowerShellOutputItem> channel, CancellationTokenSource stop, OutputSink sink)
        {
            Ps = ps;
            Channel = channel;
            Stop = stop;
            Sink = sink;
        }

        public SmaPowerShell Ps { get; }

        public Channel<PowerShellOutputItem> Channel { get; }

        public CancellationTokenSource Stop { get; }

        public OutputSink Sink { get; }

        /// <summary>Set (to 1) by the first stop request.</summary>
        public int StopRequested;

        /// <summary>Completed when the run ended and the session is idle again.</summary>
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completed when a stop was requested and the grace period passed.</summary>
        public TaskCompletionSource Grace { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
