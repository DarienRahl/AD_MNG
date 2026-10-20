using System.Diagnostics;
using System.Text;

namespace AdMng.Watchdog.Service.Gpo;

/// <summary>How smbclient is started (stage B design 8.3).</summary>
public sealed record SmbClientProcessOptions
{
    public required string ExecutablePath { get; init; }

    /// <summary>One invocation at most this long (bootstrap <c>Watchdog:Sysvol:InvocationTimeoutSeconds</c>, 60 s).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Environment variables set for the child (<c>KRB5CCNAME</c>, <c>KRB5_CONFIG</c> …).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
}

/// <summary>Exit code and stderr of one smbclient run (stdout went to the consumer).</summary>
public sealed record SmbClientRun(int ExitCode, string StandardError, bool TimedOut, bool OutputTooLarge);

/// <summary>
/// Starts smbclient WITHOUT a shell: <see cref="ProcessStartInfo.ArgumentList"/> only, stdin closed (no prompt can block),
/// a clean locale and <c>TZ=UTC</c> (listing times in UTC, names in UTF-8), the variables smbclient would take a password
/// or a replacement transport from (<c>PASSWD</c>, <c>PASSWD_FD</c>, <c>PASSWD_FILE</c>, <c>LIBSMB_PROG</c>) removed. stdout
/// is handed to the caller's consumer as a stream (never a file), stderr is collected up to <see cref="MaxStandardError"/>
/// characters for the <c>NT_STATUS_*</c> codes only. The process tree is killed at the timeout or cancellation.
/// </summary>
public sealed class SmbClientProcess(SmbClientProcessOptions options)
{
    public const int MaxStandardError = 64 * 1024;

    private static readonly string[] RemovedVariables = ["PASSWD", "PASSWD_FD", "PASSWD_FILE", "LIBSMB_PROG", "USER", "LOGNAME"];

    public SmbClientProcessOptions Options { get; } = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Runs smbclient with <paramref name="arguments"/>; <paramref name="consumeOutput"/> reads stdout until it
    /// returns. Returns false from the consumer to signal oversized output (the process is killed).</summary>
    /// <exception cref="Core.Gpo.SysvolReadException">smbclient could not be started.</exception>
    public async Task<SmbClientRun> RunAsync(IReadOnlyList<string> arguments, Func<Stream, CancellationToken, Task<bool>> consumeOutput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(consumeOutput);
        var start = new ProcessStartInfo(Options.ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var name in RemovedVariables)
        {
            start.Environment.Remove(name);
        }

        start.Environment["TZ"] = "UTC";
        start.Environment["LC_ALL"] = "C.UTF-8";
        start.Environment["LANG"] = "C.UTF-8";
        foreach (var (name, value) in Options.Environment)
        {
            start.Environment[name] = value;
        }

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                throw new Core.Gpo.SysvolReadException("smbclient could not be started.");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new Core.Gpo.SysvolReadException("smbclient could not be started.", ex);
        }

        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Options.Timeout);
        var stderr = ReadErrorAsync(process.StandardError, timeout.Token);
        var tooLarge = false;
        var timedOut = false;
        try
        {
            tooLarge = !await consumeOutput(process.StandardOutput.BaseStream, timeout.Token).ConfigureAwait(false);
            if (tooLarge)
            {
                Kill(process);
            }

            // Drain what is left (a consumer may stop early) so the child can exit.
            await process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            Kill(process);
        }
        catch
        {
            Kill(process);
            throw;
        }

        var error = await stderr.ConfigureAwait(false);
        if (timedOut || tooLarge)
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return new SmbClientRun(-1, error, timedOut, tooLarge);
        }

        return new SmbClientRun(process.ExitCode, error, TimedOut: false, OutputTooLarge: false);
    }

    private static async Task<string> ReadErrorAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (text.Length < MaxStandardError)
                {
                    text.Append(buffer, 0, Math.Min(read, MaxStandardError - text.Length));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }

        return text.ToString();
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
