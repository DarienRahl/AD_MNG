using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;

namespace AdMng.Directory.IntegrationTests.Infrastructure;

/// <summary>
/// The Samba tools of the lab for the GPO tests (README U4c): <c>samba-tool gpo create/setlink/setinheritance/del</c> and
/// <c>smbclient</c> for the test's own SYSVOL edits, as the administrator over NTLM (the lab's documented password; the
/// password goes to smbclient through <c>PASSWD</c>, to samba-tool on its command line - lab only). The lab DC's DNS name
/// does not have to resolve: smbclient connects to <see cref="LabSettings.Server"/> with <c>-I</c>. Temporary files live in
/// a folder of this object, removed on dispose.
/// </summary>
internal sealed partial class SambaTools : IDisposable
{
    private const string SmbClient = "/usr/bin/smbclient";
    private const string SambaTool = "/usr/bin/samba-tool";
    private static readonly Lazy<string?> Python = new(FindSambaPython);

    private SambaTools(string directory) => Directory = directory;

    public string Directory { get; }

    /// <summary>The lab address (smbclient <c>-I</c>, samba-tool <c>-H ldap://</c>).</summary>
    public static string Address => LabSettings.Server ?? "127.0.0.1";

    /// <summary>Skips the test unless samba-tool and smbclient are usable here.</summary>
    public static void Require()
    {
        LabSettings.RequireLab();
        Assert.SkipUnless(OperatingSystem.IsLinux(), "samba-tool and smbclient are the Linux lab tools.");
        Assert.SkipUnless(File.Exists(SmbClient), "smbclient is not installed (apt-get install smbclient).");
        Assert.SkipUnless(File.Exists(SambaTool) && Python.Value is not null, "samba-tool is not usable (no Python with the samba module).");
    }

    public static SambaTools Create() => new(System.IO.Directory.CreateTempSubdirectory("admng-u4c-").FullName);

    /// <summary><c>samba-tool gpo create</c>; returns the GPO GUID.</summary>
    public async Task<Guid> CreateGpoAsync(string displayName, CancellationToken cancellationToken)
    {
        var result = await SambaToolAsync(["gpo", "create", displayName], cancellationToken);
        var match = GpoCreated().Match(result.Output);
        Assert.True(result.Exit == 0 && match.Success, "samba-tool gpo create failed: " + result.Output + result.Error);
        return Guid.Parse(match.Groups[1].Value);
    }

    /// <summary><c>samba-tool gpo setlink</c> (Samba puts the new link first: it gets the highest link order number).</summary>
    public async Task LinkAsync(string containerDn, Guid gpo, bool enforced, CancellationToken cancellationToken)
    {
        var result = await SambaToolAsync(["gpo", "setlink", containerDn, SysvolPaths.FolderName(gpo), .. enforced ? new[] { "--enforce" } : []], cancellationToken);
        Assert.True(result.Exit == 0, "samba-tool gpo setlink failed: " + result.Output + result.Error);
    }

    /// <summary><c>samba-tool gpo setinheritance … block</c>.</summary>
    public async Task BlockInheritanceAsync(string containerDn, CancellationToken cancellationToken)
    {
        var result = await SambaToolAsync(["gpo", "setinheritance", containerDn, "block"], cancellationToken);
        Assert.True(result.Exit == 0, "samba-tool gpo setinheritance failed: " + result.Output + result.Error);
    }

    /// <summary><c>samba-tool dsacl set</c>: adds one ACE (SDDL) to the DACL of an object.</summary>
    public async Task AddAceAsync(string objectDn, string sddlAce, CancellationToken cancellationToken)
    {
        var result = await SambaToolAsync(["dsacl", "set", "--objectdn=" + objectDn, "--sddl=" + sddlAce], cancellationToken);
        Assert.True(result.Exit == 0, "samba-tool dsacl set failed: " + result.Output + result.Error);
    }

    /// <summary><c>samba-tool gpo del</c> (removes the links, the container and the SYSVOL folder; best effort).</summary>
    public async Task DeleteGpoAsync(Guid gpo)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var result = await SambaToolAsync(["gpo", "del", SysvolPaths.FolderName(gpo)], timeout.Token);
            if (result.Exit != 0)
            {
                TestContext.Current.SendDiagnosticMessage($"samba-tool gpo del {gpo:B} failed: {result.Output}{result.Error}");
            }
        }
        catch (OperationCanceledException)
        {
            TestContext.Current.SendDiagnosticMessage($"samba-tool gpo del {gpo:B} timed out.");
        }
    }

    /// <summary>Writes <paramref name="content"/> to a file of the GPO folder (parent folders created).</summary>
    public async Task PutAsync(Guid gpo, string relativePath, byte[] content, CancellationToken cancellationToken)
    {
        Assert.True(SysvolPaths.TryNormalize(relativePath, out var path));
        var local = Path.Combine(Directory, "upload-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(local, content, cancellationToken);
        try
        {
            var folder = LabSettings.DomainDns + "/Policies/" + SysvolPaths.FolderName(gpo);
            var parts = path.Split('/');
            for (var i = 1; i < parts.Length; i++)
            {
                await SmbAsync("mkdir \"" + folder + "/" + string.Join('/', parts[..i]) + "\"", cancellationToken); // exists: ignored
            }

            var put = await SmbAsync("put \"" + local + "\" \"" + folder + "/" + path + "\"", cancellationToken);
            Assert.True(put.Exit == 0, "smbclient put failed: " + put.Output + put.Error);
        }
        finally
        {
            File.Delete(local);
        }
    }

    private Task<ProcessResult> SmbAsync(string command, CancellationToken cancellationToken) =>
        RunAsync(SmbClient, ["//" + Address + "/SYSVOL", "-U", LabSettings.AdminUser, "-m", "SMB3", "-c", command],
            new Dictionary<string, string> { ["PASSWD"] = LabSettings.AdminPassword }, cancellationToken);

    private Task<ProcessResult> SambaToolAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        RunAsync(Python.Value!, [SambaTool, .. arguments, "-H", "ldap://" + Address, "-U", LabSettings.AdminUser + "%" + LabSettings.AdminPassword,
            "--tmpdir", Directory], new Dictionary<string, string>(), cancellationToken, acceptsTmpdir: arguments[1] == "create");

    internal static async Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken, bool acceptsTmpdir = true)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var skipNext = false;
        foreach (var argument in arguments)
        {
            if (skipNext)
            {
                skipNext = false;
                continue;
            }

            if (!acceptsTmpdir && argument == "--tmpdir")
            {
                skipNext = true;
                continue;
            }

            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await copy;
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new ProcessResult(process.ExitCode, output.ToArray(), await error);
    }

    private static string? FindSambaPython()
    {
        foreach (var candidate in new[] { "/usr/bin/python3.12", "/usr/bin/python3" })
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var start = new ProcessStartInfo(candidate) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("import samba");
            using var process = Process.Start(start)!;
            process.WaitForExit();
            if (process.ExitCode == 0)
            {
                return candidate;
            }
        }

        return null;
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [GeneratedRegex(@"created as (\{[0-9A-Fa-f-]{36}\})", RegexOptions.CultureInvariant)]
    private static partial Regex GpoCreated();

    internal sealed record ProcessResult(int Exit, byte[] Bytes, string Error)
    {
        public string Output => Encoding.UTF8.GetString(Bytes);
    }
}

/// <summary>
/// A Linux test implementation of <see cref="ISysvolFileReader"/> through smbclient (README U4c lab test): reads
/// <c>//&lt;session DC&gt;/SYSVOL/&lt;domain&gt;/Policies/{GUID}/…</c> with <c>get … -</c> into memory (stdout, never a
/// file). The server name is the session's DC (CLAUDE.md rule 4); in the lab, where that name need not resolve,
/// <c>-I</c> gives smbclient the lab's address for it. Names and paths are validated before they reach the command line.
/// </summary>
internal sealed class SmbClientSysvolFileReader(string address, string userName, string password) : ISysvolFileReader
{
    private readonly List<string> _servers = [];

    /// <summary>The servers opened (the session DCs).</summary>
    public IReadOnlyList<string> OpenedOn => _servers;

    public Task<ISysvolConnection> OpenAsync(IDirectorySession session, CancellationToken cancellationToken)
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

        return Task.FromResult<ISysvolConnection>(new Connection(server, domain, ip, userName, password));
    }

    private sealed class Connection(string server, string domain, string address, string userName, string password) : ISysvolConnection
    {
        public string Server { get; } = server;

        public async Task<SysvolFileResult> ReadGpoFileAsync(Guid gpoId, string relativePath, long maxBytes, CancellationToken cancellationToken)
        {
            if (!SysvolPaths.TryNormalize(relativePath, out var path))
            {
                throw new ArgumentException("Invalid relative path.", nameof(relativePath));
            }

            var remote = domain + "/Policies/" + SysvolPaths.FolderName(gpoId) + "/" + path;
            var result = await SambaTools.RunAsync(
                "/usr/bin/smbclient",
                ["//" + Server + "/SYSVOL", "-I", address, "-U", userName, "-m", "SMB3", "-c", "get \"" + remote + "\" -"],
                new Dictionary<string, string> { ["PASSWD"] = password },
                cancellationToken);
            if (result.Exit == 0)
            {
                return result.Bytes.LongLength > maxBytes ? SysvolFileResult.TooLarge : SysvolFileResult.Of(result.Bytes);
            }

            var text = result.Output + result.Error;
            return text.Contains("NT_STATUS_OBJECT_NAME_NOT_FOUND", StringComparison.Ordinal)
                   || text.Contains("NT_STATUS_OBJECT_PATH_NOT_FOUND", StringComparison.Ordinal)
                   || text.Contains("NT_STATUS_NO_SUCH_FILE", StringComparison.Ordinal)
                ? SysvolFileResult.NotFound
                : text.Contains("NT_STATUS_ACCESS_DENIED", StringComparison.Ordinal) ? SysvolFileResult.AccessDenied : SysvolFileResult.Failed;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"//{Server}/SYSVOL");
    }
}
