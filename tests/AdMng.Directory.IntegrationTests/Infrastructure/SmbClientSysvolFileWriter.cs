using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.Security;

namespace AdMng.Directory.IntegrationTests.Infrastructure;

/// <summary>
/// A Linux test implementation of <see cref="ISysvolFileWriter"/> (README K2 lab tests) over smbclient and smbcacls:
/// <c>//&lt;session DC&gt;/SYSVOL/&lt;domain&gt;/Policies/{GUID}</c> with <c>-I</c> giving smbclient the lab's address for
/// the session's DC (CLAUDE.md rule 4). Content goes in through <c>put /dev/stdin</c> and out through <c>get … -</c>, never
/// through a local file (rule 1); the password only in the <c>PASSWD</c> environment of the child process (rule 2).
/// smbcacls has no address option, so the folder's DACL is read and written on the lab's address. One process per
/// operation, so that every failure has its own NT status. Paths are validated (<see cref="SysvolPaths"/>, no ';').
/// </summary>
internal sealed partial class SmbClientSysvolFileWriter(string address, string userName, string password) : ISysvolFileWriter
{
    private const string SmbClient = "/usr/bin/smbclient";
    private const string SmbCacls = "/usr/bin/smbcacls";
    private readonly List<string> _servers = [];
    private int? _failReplace;
    private int _replaces;

    /// <summary>The servers opened (the session DCs).</summary>
    public IReadOnlyList<string> OpenedOn
    {
        get
        {
            lock (_servers)
            {
                return [.. _servers];
            }
        }
    }

    /// <summary>
    /// The interrupted write of the lab tests (design note 10): the <paramref name="number"/>-th replace from now on fails with
    /// <see cref="SysvolWriteError.Failed"/> before it reaches the server (null: no failure).
    /// </summary>
    public void FailReplace(int? number)
    {
        _failReplace = number;
        Volatile.Write(ref _replaces, 0);
    }

    private bool ShouldFailReplace() => _failReplace is { } number && Interlocked.Increment(ref _replaces) == number;

    public Task<ISysvolWriteConnection> OpenForWriteAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!SysvolPaths.TryNormalizeHost(session.Info.Server, out var server) || !SysvolPaths.TryNormalizeHost(session.Info.DomainDnsName, out var domain)
            || !SysvolPaths.TryNormalizeHost(address, out var ip))
        {
            throw new SysvolUnavailableException(SysvolUnavailableReason.Failed, "Invalid host name.");
        }

        lock (_servers)
        {
            _servers.Add(server);
        }

        return Task.FromResult<ISysvolWriteConnection>(new Connection(server, domain, ip, userName, password, ShouldFailReplace));
    }

    private sealed partial class Connection(string server, string domain, string address, string userName, string password, Func<bool> failReplace)
        : ISysvolWriteConnection
    {
        public string Server { get; } = server;

        public async Task<SysvolFileResult> ReadGpoFileAsync(Guid gpoId, string relativePath, long maxBytes, CancellationToken cancellationToken)
        {
            var result = await SmbAsync("get \"" + Remote(gpoId, relativePath) + "\" -", null, cancellationToken);
            if (result.Exit == 0)
            {
                return result.Bytes.LongLength > maxBytes ? SysvolFileResult.TooLarge : SysvolFileResult.Of(result.Bytes);
            }

            return ErrorOf(result) switch
            {
                SysvolWriteError.NotFound => SysvolFileResult.NotFound,
                SysvolWriteError.AccessDenied => SysvolFileResult.AccessDenied,
                _ => SysvolFileResult.Failed,
            };
        }

        public async Task<IReadOnlyList<SysvolEntry>> ListGpoFolderAsync(Guid gpoId, SysvolListOptions options, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
            var folder = Folder(gpoId);
            var result = await SmbAsync((options.Recursive ? "recurse; " : string.Empty) + "ls \"" + folder + "/*\"", null, cancellationToken);
            Check(result, "list");
            var prefix = "\\" + folder.Replace('/', '\\') + "\\";
            var entries = new List<SysvolEntry>();
            var current = string.Empty;
            foreach (var line in result.Output.Split('\n'))
            {
                var text = line.TrimEnd('\r');
                if (text.StartsWith('\\'))
                {
                    current = text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? text[prefix.Length..].Replace('\\', '/') + "/" : string.Empty;
                    continue;
                }

                var match = ListLine().Match(text);
                if (!match.Success || match.Groups["name"].Value is "." or "..")
                {
                    continue;
                }

                var isFolder = match.Groups["attributes"].Value.Contains('D', StringComparison.Ordinal);
                var date = string.Join(' ', match.Groups["date"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                var lastWrite = DateTime.TryParseExact(date, "ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
                    ? new DateTimeOffset(parsed)
                    : DateTimeOffset.MinValue;
                entries.Add(new SysvolEntry(current + match.Groups["name"].Value, isFolder,
                    isFolder ? 0 : long.Parse(match.Groups["size"].Value, CultureInfo.InvariantCulture), lastWrite));
                if (entries.Count >= options.MaxEntries)
                {
                    break;
                }
            }

            return entries;
        }

        public async Task<bool> GpoFolderExistsAsync(Guid gpoId, CancellationToken cancellationToken)
        {
            var result = await SmbAsync("ls \"" + Folder(gpoId) + "\"", null, cancellationToken);
            if (result.Exit == 0)
            {
                return result.Output.Contains(SysvolPaths.FolderName(gpoId), StringComparison.OrdinalIgnoreCase);
            }

            return ErrorOf(result) == SysvolWriteError.NotFound ? false : throw Error(result, "exists");
        }

        public async Task CreateGpoFolderAsync(Guid gpoId, CancellationToken cancellationToken) =>
            Check(await SmbAsync("mkdir \"" + Folder(gpoId) + "\"", null, cancellationToken), "create folder");

        public async Task CreateFolderAsync(Guid gpoId, string relativeFolder, CancellationToken cancellationToken)
        {
            var parts = Normalize(relativeFolder).Split('/');
            for (var i = 1; i <= parts.Length; i++)
            {
                var result = await SmbAsync("mkdir \"" + Folder(gpoId) + "/" + string.Join('/', parts[..i]) + "\"", null, cancellationToken);
                if (result.Exit != 0 && ErrorOf(result) != SysvolWriteError.AlreadyExists)
                {
                    throw Error(result, "create folder");
                }
            }
        }

        public async Task WriteTempFileAsync(Guid gpoId, string tempRelativePath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            var path = Normalize(tempRelativePath);
            if (path.LastIndexOf('/') is var slash and > 0)
            {
                await CreateFolderAsync(gpoId, path[..slash], cancellationToken);
            }

            Check(await SmbAsync("put /dev/stdin \"" + Remote(gpoId, path) + "\"", content.ToArray(), cancellationToken), "write");
        }

        public async Task ReplaceAsync(Guid gpoId, string tempRelativePath, string targetRelativePath, CancellationToken cancellationToken)
        {
            if (failReplace())
            {
                throw new SysvolWriteException(SysvolWriteError.Failed, "The lab test interrupted this replace.");
            }

            Check(await SmbAsync("rename \"" + Remote(gpoId, tempRelativePath) + "\" \"" + Remote(gpoId, targetRelativePath) + "\" -f", null, cancellationToken), "replace");
        }

        public async Task DeleteFileAsync(Guid gpoId, string relativePath, CancellationToken cancellationToken) =>
            Check(await SmbAsync("del \"" + Remote(gpoId, relativePath) + "\"", null, cancellationToken), "delete");

        public async Task DeleteEmptyFolderAsync(Guid gpoId, string relativeFolder, CancellationToken cancellationToken) =>
            Check(await SmbAsync("rmdir \"" + Remote(gpoId, relativeFolder) + "\"", null, cancellationToken), "delete folder");

        public async Task DeleteGpoFolderAsync(Guid gpoId, CancellationToken cancellationToken) =>
            Check(await SmbAsync("deltree \"" + Folder(gpoId) + "\"", null, cancellationToken), "delete GPO folder");

        public async Task<byte[]?> GetGpoFolderSecurityAsync(Guid gpoId, CancellationToken cancellationToken)
        {
            var result = await CaclsAsync(gpoId, ["--numeric", "--query-security-info=4"], cancellationToken);
            return result.Exit == 0 ? FromNumeric(result.Output) : null;
        }

        public async Task SetGpoFolderSecurityAsync(Guid gpoId, byte[] descriptor, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            // smbcacls refuses to propagate a set on a folder that still inherits ("Inheritance enabled … can't apply set
            // operation"): the protected DACL is set on the folder first, then set again with propagation to its children.
            var sddl = ToSddl(descriptor);
            Check(await CaclsAsync(gpoId, ["--sddl", "--set", sddl], cancellationToken), "set security " + sddl);
            Check(await CaclsAsync(gpoId, ["--sddl", "--propagate-inheritance", "--set", sddl], cancellationToken), "propagate security " + sddl);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"//{Server}/SYSVOL");

        private string Folder(Guid gpoId) => domain + "/Policies/" + SysvolPaths.FolderName(gpoId);

        private string Remote(Guid gpoId, string relativePath) => Folder(gpoId) + "/" + Normalize(relativePath);

        private static string Normalize(string relativePath) =>
            SysvolPaths.TryNormalize(relativePath, out var path) && !path.Contains(';', StringComparison.Ordinal)
                ? path
                : throw new ArgumentException("Invalid relative path.", nameof(relativePath));

        private Task<SambaTools.ProcessResult> SmbAsync(string command, byte[]? input, CancellationToken cancellationToken) =>
            RunAsync(SmbClient, ["//" + Server + "/SYSVOL", "-I", address, "-U", userName, "-m", "SMB3", "-c", command], input, cancellationToken);

        private Task<SambaTools.ProcessResult> CaclsAsync(Guid gpoId, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
            RunAsync(SmbCacls, ["//" + address + "/SYSVOL", Folder(gpoId), "-U", userName, .. arguments], null, cancellationToken);

        private async Task<SambaTools.ProcessResult> RunAsync(string file, IReadOnlyList<string> arguments, byte[]? input, CancellationToken cancellationToken)
        {
            var start = new ProcessStartInfo(file)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment["PASSWD"] = password;
            using var process = Process.Start(start)!;
            var output = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                if (input is not null)
                {
                    await process.StandardInput.BaseStream.WriteAsync(input, cancellationToken);
                }

                process.StandardInput.Close();
                await process.WaitForExitAsync(cancellationToken);
                await copy;
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            return new SambaTools.ProcessResult(process.ExitCode, output.ToArray(), await error);
        }

        private static void Check(SambaTools.ProcessResult result, string operation)
        {
            if (result.Exit != 0)
            {
                throw Error(result, operation);
            }
        }

        // Test infrastructure: the tool's output goes into the message for the test log (the application's writer keeps
        // its messages value-free).
        private static SysvolWriteException Error(SambaTools.ProcessResult result, string operation) =>
            new(ErrorOf(result), "SYSVOL " + operation + " failed: " + (result.Output + result.Error).Trim());

        private static SysvolWriteError ErrorOf(SambaTools.ProcessResult result)
        {
            var text = result.Output + result.Error;
            return text switch
            {
                _ when text.Contains("NT_STATUS_ACCESS_DENIED", StringComparison.Ordinal) => SysvolWriteError.AccessDenied,
                _ when text.Contains("NT_STATUS_OBJECT_NAME_COLLISION", StringComparison.Ordinal) => SysvolWriteError.AlreadyExists,
                _ when text.Contains("NT_STATUS_OBJECT_NAME_NOT_FOUND", StringComparison.Ordinal)
                       || text.Contains("NT_STATUS_OBJECT_PATH_NOT_FOUND", StringComparison.Ordinal)
                       || text.Contains("NT_STATUS_NO_SUCH_FILE", StringComparison.Ordinal) => SysvolWriteError.NotFound,
                _ when text.Contains("NT_STATUS_LOGON_FAILURE", StringComparison.Ordinal) => SysvolWriteError.LogonFailure,
                _ when text.Contains("NT_STATUS_DISK_FULL", StringComparison.Ordinal) => SysvolWriteError.DiskFull,
                _ when text.Contains("NT_STATUS_CONNECTION_REFUSED", StringComparison.Ordinal)
                       || text.Contains("NT_STATUS_HOST_UNREACHABLE", StringComparison.Ordinal)
                       || text.Contains("NT_STATUS_IO_TIMEOUT", StringComparison.Ordinal) => SysvolWriteError.Unreachable,
                _ => SysvolWriteError.Failed,
            };
        }

        /// <summary>A self-relative descriptor (DACL only) from smbcacls' numeric listing (<c>ACL:sid:type/flags/mask</c>).</summary>
        private static byte[]? FromNumeric(string listing)
        {
            ushort control = 0;
            var aces = new List<byte[]>();
            foreach (var raw in listing.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("CONTROL:0x", StringComparison.Ordinal))
                {
                    control = ushort.Parse(line["CONTROL:0x".Length..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                }
                else if (line.StartsWith("ACL:", StringComparison.Ordinal))
                {
                    var colon = line.LastIndexOf(':');
                    var sid = line[4..colon];
                    var parts = line[(colon + 1)..].Split('/');
                    var type = byte.Parse(parts[0], CultureInfo.InvariantCulture);
                    var flags = Convert.ToByte(parts[1], 16);
                    var mask = Convert.ToUInt32(parts[2], 16);
                    var sidBytes = AdConvert.SidToBytes(sid);
                    var objectAce = type is 5 or 6;
                    var ace = new byte[8 + (objectAce ? 4 : 0) + sidBytes.Length];
                    ace[0] = type;
                    ace[1] = flags;
                    BinaryPrimitives.WriteUInt16LittleEndian(ace.AsSpan(2), (ushort)ace.Length);
                    BinaryPrimitives.WriteUInt32LittleEndian(ace.AsSpan(4), mask);
                    sidBytes.CopyTo(ace, objectAce ? 12 : 8);
                    aces.Add(ace);
                }
            }

            var aclSize = 8 + aces.Sum(a => a.Length);
            var descriptor = new byte[20 + aclSize];
            descriptor[0] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(2), (ushort)((control & 0x1000) | 0x0004 | 0x8000));
            BinaryPrimitives.WriteInt32LittleEndian(descriptor.AsSpan(16), 20);
            var acl = descriptor.AsSpan(20);
            acl[0] = 2;
            BinaryPrimitives.WriteUInt16LittleEndian(acl[2..], (ushort)aclSize);
            BinaryPrimitives.WriteUInt16LittleEndian(acl[4..], (ushort)aces.Count);
            var offset = 8;
            foreach (var ace in aces)
            {
                ace.CopyTo(acl[offset..]);
                offset += ace.Length;
            }

            return descriptor;
        }

        /// <summary>The DACL of a self-relative descriptor as SDDL (explicit allow and deny ACEs).</summary>
        private static string ToSddl(byte[] descriptor)
        {
            if (!SecurityDescriptorSummary.TryParse(descriptor, out var summary) || summary is null)
            {
                throw new ArgumentException("Unreadable descriptor.", nameof(descriptor));
            }

            var control = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(2));
            var sddl = new StringBuilder("D:");
            if ((control & 0x1000) != 0)
            {
                sddl.Append('P');
            }

            foreach (var ace in summary.Aces.Where(a => !a.IsInherited))
            {
                var type = ace.Type switch
                {
                    0x00 => "A",
                    0x01 => "D",
                    0x05 => "OA",
                    0x06 => "OD",
                    _ => throw new ArgumentException("Unsupported ACE type.", nameof(descriptor)),
                };
                var flags = new StringBuilder();
                foreach (var (bit, name) in new[] { (0x01, "OI"), (0x02, "CI"), (0x04, "NP"), (0x08, "IO") })
                {
                    if ((ace.Flags & bit) != 0)
                    {
                        flags.Append(name);
                    }
                }

                sddl.Append(CultureInfo.InvariantCulture, $"({type};{flags};0x{ace.Mask:x8};{ace.ObjectType?.ToString("D") ?? string.Empty};;{ace.Sid})");
            }

            return sddl.ToString();
        }

        [GeneratedRegex(@"^  (?<name>.+?)\s+(?<attributes>[A-Z]*)\s+(?<size>\d+)\s+(?<date>\w{3} \w{3} +\d{1,2} \d{2}:\d{2}:\d{2} \d{4})$", RegexOptions.CultureInvariant)]
        private static partial Regex ListLine();
    }
}
