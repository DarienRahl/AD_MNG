using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AdMng.Core.PowerShell;
using AdMng.PowerShell.Commands;
using AdMng.PowerShell.Formats;
using AdMng.PowerShell.Guards;
using AdMng.PowerShell.Hosting;
using AdMng.PowerShell.Input;
using SmaPowerShell = System.Management.Automation.PowerShell;

namespace AdMng.PowerShell;

/// <summary>
/// One window's PowerShell session (docs/dev/r1-powershell-window.md 4.1): one runspace made from the AD Manager initial
/// session state - an allow-list of commands (<see cref="CommandGuards"/>) - never shared with another window, with the custom host
/// and AD Manager's commands. It refuses what the text of a command would do past that list (<see cref="InputGuard"/>), runs one
/// pipeline at a time and streams what the pipeline produces as <see cref="PowerShellOutputItem"/>s, in memory only. No job
/// outlives its command: a job left in the session's job repository is stopped when the command ends, on Stop and on dispose. A
/// command that Stop cannot end (stuck in .NET code) is left to its thread after the grace period, and the session goes on in a new
/// runspace (<see cref="PowerShellRunState.Abandoned"/>). Turning the trusted mode off also gives it a new runspace, in ConstrainedLanguage
/// (<see cref="SetLanguageModeAsync"/>, audit PS-02): nothing the trusted mode defined runs in FullLanguage after it. Every runspace has a
/// host and a raw user interface of its own. Disposing it stops the pipeline and closes the runspace.
/// </summary>
public sealed class PowerShellSession : IPowerShellSession
{
    private const int ChannelCapacity = 4096;

    private const int RunActive = 0;
    private const int RunFinished = 1;
    private const int RunLeft = 2;

    private readonly PowerShellSessionOptions _options;
    private readonly Version _version;
    private readonly CultureInfo _culture;
    private readonly IPowerShellLockdownProbe _probe;
    private readonly TimeProvider _time;
    private readonly TimeSpan _grace;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private ActiveRun? _run;
    private bool _running;
    private bool _disposed;

    /// <summary>The width of the formatting: a new runspace starts with it.</summary>
    private int _width;

    /// <summary>How many runspaces the session made (the test seam <see cref="PowerShellSessionOptions.RunspaceCreating"/> is told).</summary>
    private int _runspaces;

    /// <summary>The runspace the next command runs in; replaced when a run is left; null once disposed or when a new one could not be made.</summary>
    private Engine? _engine;

    /// <summary>Why the session has no runspace after it left a run (the next command says so).</summary>
    private Exception? _restartFailure;
    private PowerShellLanguageMode _mode = PowerShellLanguageMode.Constrained;
    private PowerShellLockdownState _lockdown;

    private PowerShellSession(PowerShellSessionOptions options)
    {
        _options = options;
        _probe = options.LockdownProbe;
        _time = options.TimeProvider;
        _grace = options.StopGracePeriod;
        _width = WindowHostRawUserInterface.Clamp(options.OutputWidth);
        _version = ResolveVersion(options.Context);
        _culture = options.Culture ?? CultureInfo.CurrentCulture;
        _lockdown = _probe.Probe();
        _engine = CreateEngine(PSLanguageMode.ConstrainedLanguage);
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
        get => CurrentEngine()?.Raw.Width ?? Volatile.Read(ref _width);
        set
        {
            Volatile.Write(ref _width, WindowHostRawUserInterface.Clamp(value));
            if (CurrentEngine() is { } engine)
            {
                engine.Raw.Width = value;
            }
        }
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
            EndJobs(CurrentEngine()?.Jobs);
            return;
        }

        RequestStop(run);
        await run.Idle.Task.ConfigureAwait(false);
        EndJobs(CurrentEngine()?.Jobs);
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
            return CurrentEngine() is { } engine
                ? await Task.Run(() => Complete(engine.Runspace, input, cursorIndex), cancellationToken).ConfigureAwait(false)
                : PowerShellCompletionResult.Empty;
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
    /// <remarks>
    /// Turning the trusted mode on sets the language mode of the runspace: its variables stay (the engine itself drops the functions that
    /// were defined in ConstrainedLanguage). Turning it off does not set the mode of the same runspace back - a function, an alias, a
    /// script block in a variable or a member the trusted mode defined would go on running in FullLanguage (audit PS-02) - but makes a
    /// new runspace in ConstrainedLanguage, on a pool thread, with a new host; the old one is closed on a pool thread, its jobs ended,
    /// and the lockdown is asked again. When the new runspace cannot be made, the session has none any more: the method throws
    /// <see cref="PowerShellSessionUnavailableException"/> and so does every later command; the trusted runspace is closed all the same.
    /// </remarks>
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

            var engine = CurrentEngine() ?? throw NoRunspace();
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

                engine.Runspace.SessionStateProxy.LanguageMode = PSLanguageMode.FullLanguage;
                lock (_sync)
                {
                    _mode = PowerShellLanguageMode.Full;
                }

                return;
            }

            if (LanguageMode == PowerShellLanguageMode.Full)
            {
                await EndTrustedModeAsync(engine).ConfigureAwait(false);
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

        if (run is not null)
        {
            // A run that does not end within the grace period is left (Leave), and no new runspace is made for a disposed session.
            RequestStop(run);
            await run.Idle.Task.ConfigureAwait(false);
        }

        Engine? engine;
        lock (_sync)
        {
            engine = _engine;
            _engine = null;
        }

        if (engine is null)
        {
            return;
        }

        EndJobs(engine.Jobs);
        WindowRegistry.Remove(engine.Runspace);
        await Task.Run(() => DisposeRunspace(engine.Runspace)).ConfigureAwait(false);
    }

    /// <summary>
    /// The trusted mode ends in a new runspace (<see cref="SetLanguageModeAsync"/>): made on a pool thread - the window's caller is its
    /// UI thread - while the session's gate is held, so no command and no completion reaches either runspace meanwhile. The session is
    /// constrained from here on whatever happens; a session disposed meanwhile closes the new runspace too.
    /// </summary>
    private async Task EndTrustedModeAsync(Engine trusted)
    {
        Engine? fresh = null;
        Exception? failure = null;
        var lockdown = PowerShellLockdownState.None;
        try
        {
            (fresh, lockdown) = await Task.Run(() => (CreateEngine(PSLanguageMode.ConstrainedLanguage), _probe.Probe()), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failure = ex;
        }

        bool disposed;
        bool ours;
        lock (_sync)
        {
            disposed = _disposed;

            // DisposeAsync takes the runspace it finds; the trusted one is retired here only when it did not.
            ours = ReferenceEquals(_engine, trusted);
            _mode = PowerShellLanguageMode.Constrained;
            if (fresh is not null)
            {
                _lockdown = lockdown;
            }

            if (!disposed)
            {
                _engine = fresh;
                _restartFailure = failure;
            }
        }

        if (ours)
        {
            Retire(trusted);
        }

        if (disposed)
        {
            if (fresh is not null)
            {
                Retire(fresh);
            }

            throw new ObjectDisposedException(nameof(PowerShellSession));
        }

        if (failure is not null)
        {
            throw NoRunspace();
        }
    }

    /// <summary>A runspace the session no longer uses: out of the registry, its jobs ended, closed on a pool thread (the caller does not wait).</summary>
    private static void Retire(Engine engine)
    {
        engine.Host.Output.Attach(null);
        WindowRegistry.Remove(engine.Runspace);
        EndJobs(engine.Jobs);
        _ = Task.Run(() => DisposeRunspace(engine.Runspace));
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

    /// <summary>
    /// The engine's default state (it brings the formatting and type data, the providers and the built-in variables) reduced to the
    /// window's allow-list of commands (<see cref="CommandGuards.Apply"/>), plus AD Manager's commands.
    /// </summary>
    private static InitialSessionState CreateInitialState(PowerShellSessionOptions options)
    {
        var state = InitialSessionState.CreateDefault();
        state.LanguageMode = PSLanguageMode.ConstrainedLanguage;
        state.ThrowOnRunspaceOpenError = true;

        // No .ps1 file runs in R1 (ScriptFileGuard): the window's authorization manager refuses every script file on Windows, whatever
        // the execution policy says. The policy itself exists on Windows only (setting it elsewhere throws) and stays Restricted.
        state.AuthorizationManager = new WindowAuthorizationManager();
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
        AdmFormats.Apply(state);
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

    /// <summary>
    /// A runspace of the window with its own host: made when the session starts and again when the session leaves a run that Stop could not
    /// end, so that the next command runs at once and nothing the left run still writes reaches it. The window's entry (context and bridge)
    /// goes into <see cref="WindowRegistry"/>; <paramref name="mode"/> is set last, after the set-up.
    /// </summary>
    private Engine CreateEngine(PSLanguageMode mode)
    {
        _options.RunspaceCreating?.Invoke(Interlocked.Increment(ref _runspaces));
        var raw = new WindowHostRawUserInterface(Volatile.Read(ref _width));
        var host = new WindowHost(_version, _culture, raw);
        var runspace = RunspaceFactory.CreateRunspace(host, CreateInitialState(_options));
        runspace.ThreadOptions = PSThreadOptions.ReuseThread;
        if (OperatingSystem.IsWindows())
        {
            runspace.ApartmentState = ApartmentState.STA;
        }

        runspace.Open();
        try
        {
            runspace.SessionStateProxy.InvokeCommand.PostCommandLookupAction = ScriptFileGuard.DropScriptFiles;
            var setup = SetUp(runspace);
            WindowRegistry.Register(runspace, new WindowEntry(_options.Context, setup.Bridge));
            runspace.SessionStateProxy.LanguageMode = mode;
            return new Engine(runspace, host, raw, setup.Jobs);
        }
        catch
        {
            WindowRegistry.Remove(runspace);
            runspace.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs <see cref="WindowSetupCommand"/> once, before anything of the operator runs, with the runspace briefly in FullLanguage: the
    /// bridge of the replacement commands takes that mode, which lets them build a steppable pipeline in a constrained session (their
    /// cmdlet itself then runs in the caller's mode). The runspace is constrained again before this returns, also when it fails.
    /// </summary>
    private static WindowSetup SetUp(Runspace runspace)
    {
        runspace.SessionStateProxy.LanguageMode = PSLanguageMode.FullLanguage;
        try
        {
            using var ps = SmaPowerShell.Create();
            ps.Runspace = runspace;
            ps.AddCommand(new CmdletInfo("Initialize-AdmWindowSession", typeof(WindowSetupCommand)));
            var result = ps.Invoke();
            return result.Count == 1 && result[0].BaseObject is WindowSetup setup
                ? setup
                : throw new InvalidOperationException("The PowerShell session of the window could not be set up.");
        }
        finally
        {
            runspace.SessionStateProxy.LanguageMode = PSLanguageMode.ConstrainedLanguage;
        }
    }

    private Engine? CurrentEngine()
    {
        lock (_sync)
        {
            return _engine;
        }
    }

    private PowerShellSessionUnavailableException NoRunspace()
    {
        Exception? failure;
        lock (_sync)
        {
            failure = _restartFailure;
        }

        return new PowerShellSessionUnavailableException(PowerShellSessionUnavailableException.DefaultMessage, failure);
    }

    private static void DisposeRunspace(Runspace runspace)
    {
        try
        {
            runspace.Dispose();
        }
        catch (InvalidRunspaceStateException)
        {
            // Already closed.
        }
    }

    /// <summary>
    /// Stops and forgets every job in a job repository of the session (no command of the window creates one; .NET code of the trusted
    /// mode can, e.g. ForEach-Object -Parallel -AsJob through the engine's cmdlet object). <c>Jobs</c> is a copy. A job is stopped on a
    /// pool thread: a job stuck in .NET code cannot be interrupted, and the window does not wait for it.
    /// </summary>
    private static void EndJobs(JobRepository? jobs)
    {
        if (jobs is null)
        {
            return;
        }

        foreach (var job in jobs.Jobs)
        {
            try
            {
                jobs.Remove(job);
            }
            catch (ArgumentException)
            {
                // Already removed.
            }

            _ = Task.Run(() =>
            {
                try
                {
                    job.StopJob();
                    job.Dispose();
                }
                catch (Exception)
                {
                    // A job that fails to stop has nothing left for the window to do; the window is not told.
                }
            });
        }
    }

    private static PowerShellCompletionResult Complete(Runspace runspace, string input, int cursorIndex)
    {
        // The engine's completion parses the whole input on this thread: nothing is completed in a text too long or too deep to walk.
        if (InputParser.Parse(input).Ast is null)
        {
            return PowerShellCompletionResult.Empty;
        }

        using var ps = SmaPowerShell.Create();
        ps.Runspace = runspace;
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

        string? refusal;
        try
        {
            refusal = InputGuard.Refusal(script);
        }
        catch
        {
            // Nothing started: a check that fails must not leave the session counted as running (every later command was refused).
            lock (_sync)
            {
                _running = false;
            }

            throw;
        }

        if (refusal is not null)
        {
            // Refused before it runs, like a command that does not parse: the error, then the end of a failed run.
            lock (_sync)
            {
                _running = false;
            }

            yield return new PowerShellTextItem(PowerShellStream.Error, refusal + "\n");
            yield return new PowerShellRunEndItem(PowerShellRunState.Failed, HadErrors: true);
            yield break;
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
        var reader = run.Channel.Reader;
        try
        {
            while (!run.Left.Task.IsCompleted)
            {
                var read = reader.WaitToReadAsync(CancellationToken.None).AsTask();
                await Task.WhenAny(read, run.Left.Task).ConfigureAwait(false);
                if (!read.IsCompleted)
                {
                    break;
                }

                if (!await read.ConfigureAwait(false))
                {
                    if (Volatile.Read(ref run.State) != RunLeft)
                    {
                        yield break;
                    }

                    // The thread of a run being left returned while the session was making its new runspace.
                    await run.Left.Task.ConfigureAwait(false);
                    break;
                }

                while (!run.Left.Task.IsCompleted && reader.TryRead(out var item))
                {
                    yield return item;
                }
            }

            // The run was stopped and did not end within the grace period: the session left it and runs the next command in a new
            // runspace. What it wrote before is shown (at most one channel's worth), then the end of a left run.
            for (var i = 0; i < ChannelCapacity && reader.TryRead(out var early); i++)
            {
                yield return early;
            }

            yield return new PowerShellRunEndItem(PowerShellRunState.Abandoned, HadErrors: false);
        }
        finally
        {
            if (!run.Idle.Task.IsCompleted)
            {
                // The consumer left before the end item (it broke out of its loop): stop the run and wait until the session is idle.
                RequestStop(run);
                await run.Idle.Task.ConfigureAwait(false);
            }

            if (run.Left.Task.IsCompleted)
            {
                // Nothing reads a left run's channel any more; what its thread may still write is dropped.
                run.Channel.Writer.TryComplete();
                while (reader.TryRead(out _))
                {
                }
            }
        }
    }

    private ActiveRun StartRun(string script, bool objects)
    {
        var engine = CurrentEngine() ?? throw NoRunspace();
        var channel = Channel.CreateBounded<PowerShellOutputItem>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var stop = new CancellationTokenSource();
        var sink = new OutputSink(channel.Writer, stop.Token);
        var ps = SmaPowerShell.Create();
        ps.Runspace = engine.Runspace;
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

        var run = new ActiveRun(engine, ps, channel, stop, sink);
        lock (_sync)
        {
            _run = run;
        }

        engine.Host.Output.Attach(sink);
        try
        {
            var started = ps.BeginInvoke<PSObject, PSObject>(null, output);
            var invoke = Task.Factory.FromAsync(started, ps.EndInvoke);
            _ = invoke.ContinueWith(task => FinishRun(run, task), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
        catch
        {
            engine.Host.Output.Attach(null);
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
        if (Interlocked.CompareExchange(ref run.State, RunFinished, RunActive) != RunActive)
        {
            FinishLeftRun(run, invoke);
            return;
        }

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

        // No job the command left in the session outlives it.
        EndJobs(run.Engine.Jobs);

        // The session is idle before the consumer sees the end item, so it can run the next command at once.
        run.Engine.Host.Output.Attach(null);
        lock (_sync)
        {
            _run = null;
            _running = false;
        }

        run.Ps.Dispose();
        _gate.Release();
        run.Sink.Post(new PowerShellRunEndItem(state, hadErrors));

        // Idle first: a consumer that sees the channel end must also see the run as finished.
        run.Idle.TrySetResult();
        run.Channel.Writer.TryComplete();
    }

    /// <summary>
    /// The thread of a run the session left (<see cref="Leave"/>) returned at last: the pipeline ends in its old runspace, which nothing
    /// else uses any more, and the session it belonged to is not touched.
    /// </summary>
    private static void FinishLeftRun(ActiveRun run, Task<PSDataCollection<PSObject>> invoke)
    {
        _ = invoke.Exception;
        run.Channel.Writer.TryComplete();
        try
        {
            run.Ps.Dispose();
        }
        catch (Exception)
        {
            // The runspace of a left run has nothing left for the window to do; the window is not told.
        }

        DisposeRunspace(run.Engine.Runspace);
    }

    private void RequestStop(ActiveRun run)
    {
        if (Interlocked.Exchange(ref run.StopRequested, 1) == 1)
        {
            return;
        }

        run.Stop.Cancel();

        // The engine's stop waits until the pipeline stopped. BeginStop waits on a foreground thread of its own, which for a pipeline
        // that never returns would keep the process alive after its last window closed; the window stops on a background thread.
        var stopper = new Thread(() => StopPipeline(run.Ps)) { IsBackground = true, Name = "AD Manager PowerShell stop" };
        stopper.Start();
        _ = Task.Delay(_grace, _time).ContinueWith(_ => Leave(run), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private static void StopPipeline(SmaPowerShell ps)
    {
        try
        {
            ps.Stop();
        }
        catch (Exception)
        {
            // The run just ended or its pipeline is not running (any more); an exception here would end the process.
        }
    }

    /// <summary>
    /// The grace period of a stop passed. A run that has not ended by then is stuck in .NET code that Stop cannot interrupt (a
    /// <c>[regex]</c> made with a long time limit of its own is enough in ConstrainedLanguage, red team of R1): the session leaves it to
    /// its thread - its runspace leaves the window's registry, its host is detached, its jobs are ended - and goes on in a new runspace in
    /// the same language mode (constrained again when a lockdown is now enforced), so that the next command runs at once. Its thread runs
    /// on until it returns (<see cref="FinishLeftRun"/>) or the process ends. A disposed session makes no new runspace.
    /// </summary>
    private void Leave(ActiveRun run)
    {
        if (Interlocked.CompareExchange(ref run.State, RunLeft, RunActive) != RunActive)
        {
            return;
        }

        Engine? fresh = null;
        Exception? failure = null;
        try
        {
            run.Engine.Host.Output.Attach(null);
            WindowRegistry.Remove(run.Engine.Runspace);
            EndJobs(run.Engine.Jobs);
            bool disposed;
            PowerShellLanguageMode mode;
            lock (_sync)
            {
                disposed = _disposed;
                mode = _mode;
            }

            if (!disposed)
            {
                if (mode == PowerShellLanguageMode.Full && _probe.Probe() == PowerShellLockdownState.Enforce)
                {
                    mode = PowerShellLanguageMode.Constrained;
                    lock (_sync)
                    {
                        _lockdown = PowerShellLockdownState.Enforce;
                        _mode = mode;
                    }
                }

                fresh = CreateEngine(mode == PowerShellLanguageMode.Full ? PSLanguageMode.FullLanguage : PSLanguageMode.ConstrainedLanguage);
            }
        }
        catch (Exception ex)
        {
            // The window cannot run another command; the next one says so.
            failure = ex;
        }
        finally
        {
            lock (_sync)
            {
                _engine = fresh;
                _restartFailure = failure;
                _run = null;
                _running = false;
            }

            _gate.Release();
            run.Left.TrySetResult();
            run.Idle.TrySetResult();
        }
    }

    /// <summary>A runspace of the window, its host, the host's raw user interface and its job repository.</summary>
    private sealed record Engine(Runspace Runspace, WindowHost Host, WindowHostRawUserInterface Raw, JobRepository Jobs);

    private sealed class ActiveRun
    {
        public ActiveRun(Engine engine, SmaPowerShell ps, Channel<PowerShellOutputItem> channel, CancellationTokenSource stop, OutputSink sink)
        {
            Engine = engine;
            Ps = ps;
            Channel = channel;
            Stop = stop;
            Sink = sink;
        }

        /// <summary>The runspace the run runs in.</summary>
        public Engine Engine { get; }

        public SmaPowerShell Ps { get; }

        public Channel<PowerShellOutputItem> Channel { get; }

        public CancellationTokenSource Stop { get; }

        public OutputSink Sink { get; }

        /// <summary>Set (to 1) by the first stop request.</summary>
        public int StopRequested;

        /// <summary><see cref="RunActive"/>, then <see cref="RunFinished"/> (the pipeline ended) or <see cref="RunLeft"/> (the session left it), whichever comes first.</summary>
        public int State;

        /// <summary>Completed when the session is idle again: the run ended, or the session left it.</summary>
        public TaskCompletionSource Idle { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completed when the session left the run (<see cref="Leave"/>), before <see cref="Idle"/>.</summary>
        public TaskCompletionSource Left { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
