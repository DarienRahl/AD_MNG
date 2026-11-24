using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AdMng.Core.PowerShell;
using AdMng.Presentation.PowerShell;

namespace AdMng.Presentation.Tests.PowerShell;

/// <summary>What a scripted run of the fake session can wait for.</summary>
internal sealed class FakeRun(string script, CancellationToken token)
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Script { get; } = script;

    /// <summary>The token of the run: cancelled by Stop.</summary>
    public CancellationToken Token { get; } = token;

    /// <summary>Completes when the run reached a <see cref="WaitAsync"/>.</summary>
    public Task Reached => _reached.Task;

    /// <summary>Lets the run go on.</summary>
    public void Release() => _release.TrySetResult();

    /// <summary>Waits for the test (<see cref="Release"/>) or for Stop; true when it was Stop.</summary>
    public async Task<bool> WaitAsync()
    {
        _reached.TrySetResult();
        try
        {
            await _release.Task.WaitAsync(Token).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }
}

/// <summary>
/// An engine session that does what the test scripts: runs stream the items the test queued, the language mode follows the rules
/// of the real one (refused while a run is active and under a lockdown), completion and highlighting are scripted, and the
/// history policy is a predicate. It has no PowerShell in it.
/// </summary>
internal sealed class FakePowerShellSession : IPowerShellSession
{
    private readonly Queue<Func<FakeRun, IAsyncEnumerable<PowerShellOutputItem>>> _runs = new();

    public PowerShellLanguageMode LanguageMode { get; private set; }

    /// <summary>What the real engine does when it leaves a run under a lockdown that came into force meanwhile: its new runspace is constrained.</summary>
    public void EnforceConstrainedFromTheEngine()
    {
        LanguageMode = PowerShellLanguageMode.Constrained;
        TrustedModeBlock = PowerShellTrustedModeBlock.SystemLockdown;
    }

    public PowerShellTrustedModeBlock TrustedModeBlock { get; set; }

    public bool IsRunning { get; private set; }

    public int OutputWidth { get; set; } = 120;

    /// <summary>The scripts that were run, in order.</summary>
    public List<string> Scripts { get; } = [];

    /// <summary>What the history policy says; the default refuses a plain-text password.</summary>
    public Func<string, bool> Remember { get; set; } = line => !line.Contains("-AsPlainText", StringComparison.OrdinalIgnoreCase);

    /// <summary>What completion answers; the default is nothing.</summary>
    public Func<string, int, PowerShellCompletionResult> Completion { get; set; } = (_, _) => PowerShellCompletionResult.Empty;

    /// <summary>Completes a completion request when the test says so (null: at once).</summary>
    public Func<Task>? BeforeCompletion { get; set; }

    public List<(string Input, int Cursor)> CompletionRequests { get; } = [];

    public int Disposals { get; private set; }

    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the window closed the session.</summary>
    public Task Disposed => _disposed.Task;

    public List<PowerShellLanguageMode> ModeRequests { get; } = [];

    /// <summary>The last run, for the test that holds it.</summary>
    public FakeRun? LastRun { get; private set; }

    private TaskCompletionSource<FakeRun>? _awaitingStart;

    /// <summary>Completes with the next run when it starts: the test starts a command and waits for the engine to have it.</summary>
    public Task<FakeRun> NextRunStarted()
    {
        _awaitingStart = new TaskCompletionSource<FakeRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _awaitingStart.Task;
    }

    /// <summary>The next run streams <paramref name="items"/> and then ends as completed (unless the last item is an end item).</summary>
    public FakePowerShellSession Queue(params PowerShellOutputItem[] items)
    {
        _runs.Enqueue(_ => Stream(items));
        return this;
    }

    /// <summary>The next run is what <paramref name="script"/> says (it may wait on the run).</summary>
    public FakePowerShellSession Queue(Func<FakeRun, IAsyncEnumerable<PowerShellOutputItem>> script)
    {
        _runs.Enqueue(script);
        return this;
    }

    /// <summary>The next run writes <paramref name="first"/>, waits for the test or Stop, and then writes <paramref name="rest"/> - or ends as stopped.</summary>
    public FakePowerShellSession QueueHeld(PowerShellOutputItem[] first, params PowerShellOutputItem[] rest)
    {
        _runs.Enqueue(run => Held(run, first, rest));
        return this;
    }

    public static PowerShellTextItem Text(string text, PowerShellStream stream = PowerShellStream.Output) => new(stream, text);

    public static PowerShellRunEndItem End(PowerShellRunState state = PowerShellRunState.Completed, bool hadErrors = false) => new(state, hadErrors);

    public IAsyncEnumerable<PowerShellOutputItem> RunAsync(string script, CancellationToken cancellationToken) => Run(script, cancellationToken);

    public Task StopAsync() => Task.CompletedTask;

    public ValueTask<PowerShellCompletionResult> CompleteAsync(string input, int cursorIndex, CancellationToken cancellationToken)
    {
        CompletionRequests.Add((input, cursorIndex));
        return Complete(input, cursorIndex);
    }

    /// <summary>Called at the start of every <see cref="Tokenize"/> (a test that holds the highlighting of a long text on its pool thread).</summary>
    public Action<string>? TokenizeHook { get; set; }

    public IReadOnlyList<PowerShellSyntaxSpan> Tokenize(string input)
    {
        TokenizeHook?.Invoke(input);
        // The first word is a command, a word that starts with '-' a parameter: enough to see that the spans follow the text.
        var spans = new List<PowerShellSyntaxSpan>();
        var index = 0;
        var first = true;
        while (index < input.Length)
        {
            if (char.IsWhiteSpace(input[index]))
            {
                index++;
                continue;
            }

            var start = index;
            while (index < input.Length && !char.IsWhiteSpace(input[index]))
            {
                index++;
            }

            spans.Add(new PowerShellSyntaxSpan(start, index - start, first ? PowerShellSyntaxKind.Command : input[start] == '-' ? PowerShellSyntaxKind.Parameter : PowerShellSyntaxKind.Other));
            first = false;
        }

        return spans;
    }

    public bool IsInputComplete(string input)
    {
        var quotes = input.Count(c => c == '\'');
        var doubles = input.Count(c => c == '"');
        var open = input.Count(c => c is '(' or '{' or '[');
        var close = input.Count(c => c is ')' or '}' or ']');
        return quotes % 2 == 0 && doubles % 2 == 0 && open <= close && !input.TrimEnd().EndsWith('|');
    }

    public bool ShouldRemember(string line) => Remember(line);

    public Task SetLanguageModeAsync(PowerShellLanguageMode mode, CancellationToken cancellationToken)
    {
        ModeRequests.Add(mode);
        if (IsRunning)
        {
            throw new InvalidOperationException("The language mode cannot be changed while a command is running.");
        }

        if (mode == PowerShellLanguageMode.Full && TrustedModeBlock == PowerShellTrustedModeBlock.SystemLockdown)
        {
            throw new InvalidOperationException("A system lockdown enforces ConstrainedLanguage.");
        }

        LanguageMode = mode;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposals++;
        _disposed.TrySetResult();
        return ValueTask.CompletedTask;
    }

    private async ValueTask<PowerShellCompletionResult> Complete(string input, int cursorIndex)
    {
        if (BeforeCompletion is { } wait)
        {
            await wait().ConfigureAwait(false);
        }

        return Completion(input, cursorIndex);
    }

    private async IAsyncEnumerable<PowerShellOutputItem> Run(string script, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("A run is already active.");
        }

        IsRunning = true;
        Scripts.Add(script);
        var run = new FakeRun(script, cancellationToken);
        LastRun = run;
        _awaitingStart?.TrySetResult(run);
        try
        {
            // Like the real engine, the "pipeline" has a thread of its own and hands its items over through a channel: a command that
            // blocks on a question (Read-Host) blocks that thread, not the one that reads the items.
            var handler = _runs.Count > 0 ? _runs.Dequeue() : (_ => Stream([]));
            var channel = Channel.CreateUnbounded<PowerShellOutputItem>();
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var item in handler(run).ConfigureAwait(false))
                    {
                        await channel.Writer.WriteAsync(item, CancellationToken.None).ConfigureAwait(false);
                    }

                    channel.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    channel.Writer.TryComplete(ex);
                }
            }, CancellationToken.None);
            await foreach (var item in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            IsRunning = false;
        }
    }

    private static async IAsyncEnumerable<PowerShellOutputItem> Stream(PowerShellOutputItem[] items)
    {
        foreach (var item in items)
        {
            yield return item;
        }

        if (items.Length == 0 || items[^1] is not PowerShellRunEndItem)
        {
            yield return End();
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<PowerShellOutputItem> Held(FakeRun run, PowerShellOutputItem[] first, PowerShellOutputItem[] rest)
    {
        foreach (var item in first)
        {
            yield return item;
        }

        if (await run.WaitAsync().ConfigureAwait(false))
        {
            yield return End(PowerShellRunState.Stopped);
            yield break;
        }

        foreach (var item in rest)
        {
            yield return item;
        }

        if (rest.Length == 0 || rest[^1] is not PowerShellRunEndItem)
        {
            yield return End();
        }
    }
}

/// <summary>The factory of the window: hands out the fake session (or fails, or waits) and records what it was asked for.</summary>
internal sealed class FakePowerShellSessionFactory(FakePowerShellSession? session = null) : IPowerShellSessionFactory
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _held;

    public FakePowerShellSession Session { get; } = session ?? new FakePowerShellSession();

    public List<PowerShellSessionRequest> Requests { get; } = [];

    public Exception? Failure { get; set; }

    /// <summary>False: the creation does not notice that the window gave up (an engine that is busy loading its files).</summary>
    public bool HonorsCancellation { get; set; } = true;

    /// <summary>The next session is created when <see cref="Release"/> says so (the engine takes half a second).</summary>
    public void Hold() => _held = true;

    public void Release() => _gate.TrySetResult();

    public async Task<IPowerShellSession> CreateAsync(PowerShellSessionRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (_held)
        {
            await _gate.Task.WaitAsync(HonorsCancellation ? cancellationToken : CancellationToken.None).ConfigureAwait(false);
        }

        return Failure is { } failure ? throw failure : Session;
    }
}

/// <summary>The window actions of a host: records the calls, answers as the test says.</summary>
internal sealed class FakePowerShellWindowActions : IPowerShellWindowActions
{
    public List<PowerShellGridTable> Grids { get; } = [];

    public List<string> Opened { get; } = [];

    public List<(PowerShellGridTable Table, string? Path, char? Delimiter)> Exports { get; } = [];

    /// <summary>What <see cref="OpenObjectAsync"/> says for an identity (default: opened).</summary>
    public Func<string, PowerShellOpenOutcome> Open { get; set; } = _ => PowerShellOpenOutcome.Opened;

    /// <summary>What the operator chooses in Save As when the command gave no path (null: cancelled).</summary>
    public string? ChosenPath { get; set; }

    public Task ShowInGridAsync(PowerShellGridTable table, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Grids.Add(table);
        return Task.CompletedTask;
    }

    public Task<PowerShellOpenOutcome> OpenObjectAsync(string identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Opened.Add(identity);
        return Task.FromResult(Open(identity));
    }

    public Task<string?> ExportListAsync(PowerShellGridTable table, string? path, char? delimiter, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Exports.Add((table, path, delimiter));
        return Task.FromResult(path ?? ChosenPath);
    }
}
