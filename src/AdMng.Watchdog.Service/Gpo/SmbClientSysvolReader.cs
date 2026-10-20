using System.Formats.Tar;
using System.Text;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Gpo;

namespace AdMng.Watchdog.Service.Gpo;

/// <summary>Host settings of <see cref="SmbClientSysvolReader"/>.</summary>
public sealed record SmbClientSysvolOptions
{
    /// <summary>The private smb.conf passed with <c>-s</c> (bootstrap <c>Watchdog:Sysvol:ConfigFile</c>).</summary>
    public required string ConfigFile { get; init; }

    /// <summary>Samba's lock/state/cache/private directory of that file (tmpfs in the runtime directory); emptied after
    /// every run (gencache may hold name/SID mappings). Null = not managed (tests).</summary>
    public string? StateDirectory { get; init; }
}

/// <summary>
/// <see cref="ISysvolReader"/> over the <c>smbclient</c> process (stage B design 8.3) with Kerberos (<c>--use-kerberos=required</c>,
/// TGT ensured before every run by <see cref="ISmbTicketSource"/>), SMB3 with the signing/encryption of the private
/// <c>smb-client.conf</c>, <c>-N</c> (never a password prompt) and the exit code checked:
/// <list type="bullet">
/// <item>The folder is checked BEFORE any SMB connection: its DC must be the DC of the live directory session and its
/// domain that session's domain (<see cref="SysvolGpoFolder"/> itself refuses IPv4 literals and anything but DNS names) –
/// a host taken from AD data (a redirected <c>gPCFileSysPath</c>) is never contacted.</item>
/// <item>Listing: <c>-D &lt;domain&gt;/Policies/{GUID} -c 'recurse ON; ls'</c> WITHOUT <c>-E</c> (with it the listing goes
/// to stderr), parsed by <see cref="SmbClientListingParser"/>.</item>
/// <item>Content: <c>-E -D … -Tc - &lt;file&gt; …</c> – tar of the requested files to stdout, read with
/// <see cref="TarReader"/> straight from the pipe into memory; file names are separate arguments (no smbclient command
/// line is ever built from a name, so <c>;</c> or quotes in a name cannot inject commands); names starting with <c>-</c> are
/// refused (they would be read as options).</item>
/// </list>
/// Only <c>NT_STATUS_*</c> codes of the output are evaluated; nothing of it is logged. Nothing is written to disk.
/// </summary>
public sealed class SmbClientSysvolReader : ISysvolReader
{
    /// <summary>Tar headers and padding allowed on top of the requested bytes.</summary>
    private const long TarOverheadPerFile = 4096;

    private readonly SmbClientProcess _process;
    private readonly SmbClientSysvolOptions _options;
    private readonly ISmbTicketSource _tickets;
    private readonly IDirectorySessionProvider _sessions;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public SmbClientSysvolReader(SmbClientProcess process, SmbClientSysvolOptions options, ISmbTicketSource tickets, IDirectorySessionProvider sessions)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(sessions);
        _process = process;
        _options = options;
        _tickets = tickets;
        _sessions = sessions;
    }

    public async Task<SysvolListing> ListAsync(SysvolGpoFolder folder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);
        await RefuseForeignAsync(folder, cancellationToken).ConfigureAwait(false);
        string output = string.Empty;
        var run = await RunAsync(
            [.. Common(folder, quiet: false), "-c", "recurse ON; ls"],
            async (stdout, ct) =>
            {
                var (text, complete) = await ReadTextAsync(stdout, SmbClientListingParser.MaxOutputChars, ct).ConfigureAwait(false);
                output = text;
                return complete;
            },
            cancellationToken).ConfigureAwait(false);
        ThrowIfIncomplete(run);
        var result = SmbClientListingParser.Parse(output, folder);
        var statuses = result.Errors.Concat(Statuses(run.StandardError)).ToList();
        if (run.ExitCode != 0 || statuses.Count > 0)
        {
            throw Failure(statuses, run.ExitCode, "The GPO folder could not be listed");
        }

        return result.Listing;
    }

    public async Task<IReadOnlyList<SysvolFile>> ReadAsync(SysvolGpoFolder folder, IReadOnlyList<string> relativePaths, long maxBytesPerFile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(relativePaths);
        await RefuseForeignAsync(folder, cancellationToken).ConfigureAwait(false);
        var results = new Dictionary<string, SysvolFile>(StringComparer.OrdinalIgnoreCase);
        var requested = new List<string>();
        foreach (var path in relativePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!GpoRelativePath.TryNormalize(path, out var normalized) || normalized[0] == '-')
            {
                results[path] = new SysvolFile { RelativePath = path, Error = SysvolErrorKind.Failed };
            }
            else
            {
                requested.Add(normalized);
            }
        }

        if (requested.Count > 0)
        {
            var prefix = "./" + folder.Directory + "/";
            var budget = requested.Count * (Math.Max(0, maxBytesPerFile) + TarOverheadPerFile);
            var run = await RunAsync(
                [.. Common(folder, quiet: true), "-Tc", "-", .. requested],
                (stdout, ct) => ReadTarAsync(stdout, prefix, requested, maxBytesPerFile, budget, results, ct),
                cancellationToken).ConfigureAwait(false);
            ThrowIfIncomplete(run);
            var arrived = requested.Count(p => results.ContainsKey(p));
            var global = GlobalStatuses(run.StandardError, folder);
            if (run.ExitCode != 0 && arrived == 0 && (global.Count > 0 || Statuses(run.StandardError).Count == 0))
            {
                // Nothing arrived: the session, the share or the folder failed as a whole.
                throw Failure(global, run.ExitCode, "The GPO folder could not be read");
            }

            foreach (var path in requested.Where(p => !results.ContainsKey(p)))
            {
                var status = StatusFor(run.StandardError, folder, path);
                results[path] = new SysvolFile { RelativePath = path, Error = status is null ? SysvolErrorKind.NotFound : Classify(status), NtStatus = status };
            }
        }

        return [.. relativePaths.Select(p => results.TryGetValue(p, out var file) ? file with { RelativePath = p }
            : new SysvolFile { RelativePath = p, Error = SysvolErrorKind.NotFound })];
    }

    /// <summary>Maps an <c>NT_STATUS_*</c> code to the reader's error kind (8.3).</summary>
    public static SysvolErrorKind Classify(string ntStatus) => ntStatus switch
    {
        "NT_STATUS_ACCESS_DENIED" or "NT_STATUS_NETWORK_ACCESS_DENIED" => SysvolErrorKind.AccessDenied,
        "NT_STATUS_OBJECT_NAME_NOT_FOUND" or "NT_STATUS_OBJECT_PATH_NOT_FOUND" or "NT_STATUS_NO_SUCH_FILE" or "NT_STATUS_NOT_FOUND"
            or "NT_STATUS_OBJECT_NAME_INVALID" => SysvolErrorKind.NotFound,
        "NT_STATUS_LOGON_FAILURE" or "NT_STATUS_INVALID_PARAMETER" or "NT_STATUS_NO_LOGON_SERVERS" or "NT_STATUS_TIME_DIFFERENCE_AT_DC"
            or "NT_STATUS_WRONG_PASSWORD" or "NT_STATUS_ACCOUNT_DISABLED" or "NT_STATUS_KDC_UNKNOWN_ETYPE" or "NT_STATUS_PASSWORD_EXPIRED"
            or "NT_STATUS_NO_TRUST_SAM_ACCOUNT" or "NT_STATUS_INVALID_ACCOUNT_NAME" => SysvolErrorKind.Authentication,
        "NT_STATUS_IO_TIMEOUT" => SysvolErrorKind.Timeout,
        "NT_STATUS_BAD_NETWORK_NAME" or "NT_STATUS_HOST_UNREACHABLE" or "NT_STATUS_NETWORK_UNREACHABLE" or "NT_STATUS_CONNECTION_REFUSED"
            or "NT_STATUS_CONNECTION_RESET" or "NT_STATUS_CONNECTION_DISCONNECTED" or "NT_STATUS_UNSUCCESSFUL" or "NT_STATUS_PORT_UNREACHABLE"
            or "NT_STATUS_INVALID_NETWORK_RESPONSE" or "NT_STATUS_NOT_SUPPORTED" => SysvolErrorKind.Unreachable,
        _ => SysvolErrorKind.Failed,
    };

    /// <summary>
    /// 8.2/8.3: only the GPO folder of the pinned DC of the live session, in that session's domain. A folder of any other host
    /// (a host taken from AD data) is refused here, before a ticket is requested or smbclient is started.
    /// </summary>
    private async Task RefuseForeignAsync(SysvolGpoFolder folder, CancellationToken cancellationToken)
    {
        var session = await _sessions.GetSessionAsync(cancellationToken).ConfigureAwait(false);
        if (!SysvolGpoFolder.TryNormalizeDnsName(session.Info.Server, out var pinned) || !string.Equals(folder.Dc, pinned, StringComparison.Ordinal)
            || !SysvolGpoFolder.TryNormalizeDnsName(session.Info.DomainDnsName, out var domain) || !string.Equals(folder.DomainDns, domain, StringComparison.Ordinal))
        {
            throw new SysvolReadException(SysvolErrorKind.Failed, null, "Refused: SYSVOL is read only from the DC of the directory session.");
        }
    }

    private List<string> Common(SysvolGpoFolder folder, bool quiet)
    {
        var arguments = new List<string>
        {
            folder.Service, "-s", _options.ConfigFile, "--use-kerberos=required", "-N", "-m", "SMB3", "-D", folder.Directory,
        };
        if (quiet)
        {
            arguments.Add("-E");
        }

        return arguments;
    }

    private async Task<SmbClientRun> RunAsync(IReadOnlyList<string> arguments, Func<Stream, CancellationToken, Task<bool>> consume,
        CancellationToken cancellationToken)
    {
        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureStateDirectory();
            await _tickets.EnsureAsync(cancellationToken).ConfigureAwait(false);
            return await _process.RunAsync(arguments, consume, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            EmptyStateDirectory();
            _oneAtATime.Release();
        }
    }

    private void EnsureStateDirectory()
    {
        if (_options.StateDirectory is { Length: > 0 } directory && !System.IO.Directory.Exists(directory))
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    System.IO.Directory.CreateDirectory(directory);
                }
                else
                {
                    System.IO.Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new SysvolReadException("The Samba state directory could not be created.", ex);
            }
        }
    }

    private void EmptyStateDirectory()
    {
        if (_options.StateDirectory is not { Length: > 0 } directory || !System.IO.Directory.Exists(directory))
        {
            return;
        }

        try
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo { LinkTarget: null } sub)
                {
                    sub.Delete(recursive: true);
                }
                else
                {
                    entry.Delete();
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void ThrowIfIncomplete(SmbClientRun run)
    {
        if (run.TimedOut)
        {
            throw new SysvolReadException(SysvolErrorKind.Timeout, null, "smbclient exceeded its time limit.");
        }

        if (run.OutputTooLarge)
        {
            throw new SysvolReadException(SysvolErrorKind.Failed, null, "smbclient returned more data than requested.");
        }
    }

    private static SysvolReadException Failure(IReadOnlyList<string> statuses, int exitCode, string message)
    {
        // The most specific code wins: authentication and reachability before access and existence.
        var classified = statuses.Select(s => (Status: s, Kind: Classify(s))).ToList();
        var pick = classified.OrderBy(c => c.Kind switch
        {
            SysvolErrorKind.Authentication => 0,
            SysvolErrorKind.Unreachable => 1,
            SysvolErrorKind.Timeout => 2,
            SysvolErrorKind.AccessDenied => 3,
            SysvolErrorKind.NotFound => 4,
            _ => 5,
        }).FirstOrDefault();
        return pick.Status is null
            ? new SysvolReadException(SysvolErrorKind.Failed, null, message + " (exit code " + exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + ").")
            : new SysvolReadException(pick.Kind, pick.Status, message + " (" + pick.Status + ").");
    }

    /// <summary>Codes of stderr lines that do not name one requested file (session setup, tree connect, <c>cd</c> of the
    /// folder): the run failed as a whole.</summary>
    private static List<string> GlobalStatuses(string standardError, SysvolGpoFolder folder)
    {
        var folderPrefix = ("\\" + folder.Directory + "\\").Replace('/', '\\');
        return
        [
            .. standardError.Split('\n')
                .Where(line => line.StartsWith("cd ", StringComparison.Ordinal)
                               || (!line.Contains(folderPrefix, StringComparison.OrdinalIgnoreCase) && !line.Contains("failed on ", StringComparison.Ordinal)))
                .SelectMany(line => SmbClientListingParser.NtStatus().Matches(line).Select(m => m.Value))
                .Distinct(StringComparer.Ordinal),
        ];
    }

    private static List<string> Statuses(string text) =>
        [.. SmbClientListingParser.NtStatus().Matches(text).Select(m => m.Value).Distinct(StringComparer.Ordinal)];

    /// <summary>The <c>NT_STATUS_*</c> code of the stderr line naming <paramref name="path"/> (<c>… listing \dom\Policies\{G}\path</c>).</summary>
    private static string? StatusFor(string standardError, SysvolGpoFolder folder, string path)
    {
        var name = ("\\" + folder.Directory + "\\" + path).Replace('/', '\\');
        foreach (var line in standardError.Split('\n'))
        {
            if (line.Contains(name, StringComparison.OrdinalIgnoreCase) && SmbClientListingParser.NtStatus().Match(line) is { Success: true } match)
            {
                return match.Value;
            }
        }

        return null;
    }

    private static async Task<(string Text, bool Complete)> ReadTextAsync(Stream stream, int maxChars, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var text = new StringBuilder();
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (text.Length + read > maxChars)
            {
                return (text.ToString(), false);
            }

            text.Append(buffer, 0, read);
        }

        return (text.ToString(), true);
    }

    /// <summary>Reads the tar stream: only regular-file entries below the GPO folder that were requested; each at most
    /// <paramref name="maxBytesPerFile"/> (larger ones are skipped as <see cref="SysvolErrorKind.TooLarge"/>).</summary>
    private static async Task<bool> ReadTarAsync(Stream stdout, string prefix, IReadOnlyList<string> requested, long maxBytesPerFile, long budget,
        Dictionary<string, SysvolFile> results, CancellationToken cancellationToken)
    {
        var wanted = new HashSet<string>(requested, StringComparer.OrdinalIgnoreCase);
        var counting = new CountingStream(stdout, budget);
        try
        {
            await using var tar = new TarReader(counting, leaveOpen: true);
            while (await tar.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                    || !entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var path = entry.Name[prefix.Length..];
                var match = wanted.FirstOrDefault(w => string.Equals(w, path, StringComparison.OrdinalIgnoreCase));
                if (match is null || results.ContainsKey(match))
                {
                    continue;
                }

                if (entry.Length > maxBytesPerFile)
                {
                    results[match] = new SysvolFile { RelativePath = match, Error = SysvolErrorKind.TooLarge };
                    continue;
                }

                var content = new byte[entry.Length];
                if (entry.DataStream is { } data)
                {
                    await data.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
                }

                results[match] = new SysvolFile { RelativePath = match, Content = content };
            }
        }
        catch (InvalidDataException) when (!counting.Exceeded)
        {
            // A truncated or malformed stream (smbclient failed midway): what was complete stays, the rest is missing.
        }
        catch (EndOfStreamException) when (!counting.Exceeded)
        {
        }
        catch (IOException) when (counting.Exceeded)
        {
            return false;
        }

        return !counting.Exceeded;
    }

    /// <summary>A read-only pass-through that stops after <c>limit</c> bytes.</summary>
    private sealed class CountingStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public bool Exceeded { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, Allowed(count)));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer[..Allowed(buffer.Length)], cancellationToken).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Allowed(int count)
        {
            if (_read >= limit)
            {
                Exceeded = true;
                throw new IOException("The tar stream exceeds the requested size.");
            }

            return (int)Math.Min(count, limit - _read);
        }

        private int Count(int read)
        {
            _read += read;
            return read;
        }
    }
}
