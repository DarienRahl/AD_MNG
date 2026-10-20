using System.Formats.Tar;
using System.Text;
using AdMng.Testing;
using AdMng.Watchdog.Core.Gpo;
using AdMng.Watchdog.Service.Gpo;
using AdMng.Watchdog.Testing.Scenarios;

namespace AdMng.Watchdog.Service.Tests.Gpo;

/// <summary>
/// <see cref="SmbClientSysvolReader"/> against a fake smbclient (a shell script that records its arguments and environment
/// and plays back canned stdout, stderr and exit code): the argument lists of 8.3 (no shell, Kerberos, <c>-N</c>, listing
/// without <c>-E</c>, tar with <c>-E</c> and one argument per file), the TGT check before every run, tar from the pipe,
/// <c>NT_STATUS_*</c> classification, the time limit, and the refusal of foreign hosts before anything is started.
/// </summary>
public sealed class SmbClientSysvolReaderTests : IDisposable
{
    private const string Dc = "dc01.contoso.com";
    private const string Domain = "contoso.com";
    private static readonly Guid Gpo = Guid.Parse("6AC1786C-016F-11D2-945F-00C04FB984F9");

    private readonly string _directory = System.IO.Directory.CreateTempSubdirectory("admng-smbclient-test-").FullName;
    private readonly CountingTickets _tickets = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ArgsFile => Path.Combine(_directory, "args");

    private string EnvFile => Path.Combine(_directory, "env");

    private string StateDirectory => Path.Combine(_directory, "samba");

    private static SysvolGpoFolder Folder => SysvolGpoFolder.Create(Dc, Domain, Gpo);

    public void Dispose() => System.IO.Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task List_StartsSmbclientWithKerberosAndNoPrompt_WithoutE_AfterTheTgtCheck()
    {
        SkipUnlessUnix();
        var reader = Reader(stdout: Encoding.UTF8.GetBytes(Listing("  GPT.INI                             A       22  Tue Oct 20 18:01:15 2026")));
        System.IO.Directory.CreateDirectory(StateDirectory);
        await File.WriteAllTextAsync(Path.Combine(StateDirectory, "gencache.tdb"), "x", Ct);

        var listing = await reader.ListAsync(Folder, Ct);

        Assert.Equal("GPT.INI", Assert.Single(listing.Entries).RelativePath);
        Assert.Equal(
            ["//dc01.contoso.com/SYSVOL", "-s", "/etc/admng-watchdog/smb-client.conf", "--use-kerberos=required", "-N", "-m", "SMB3",
                "-D", "contoso.com/Policies/{6AC1786C-016F-11D2-945F-00C04FB984F9}", "-c", "recurse ON; ls"],
            Arguments());
        var environment = await File.ReadAllLinesAsync(EnvFile, Ct);
        Assert.Contains("KRB5CCNAME=FILE:/run/admng-watchdog/krb5cc", environment);
        Assert.Contains("TZ=UTC", environment);
        Assert.Contains("PASSWD=", environment);
        Assert.Equal(1, _tickets.Calls);
        Assert.Empty(System.IO.Directory.EnumerateFileSystemEntries(StateDirectory)); // gencache emptied after the run
    }

    [Theory]
    [InlineData("tree connect failed: NT_STATUS_ACCESS_DENIED", SysvolErrorKind.AccessDenied)]
    [InlineData("session setup failed: NT_STATUS_LOGON_FAILURE", SysvolErrorKind.Authentication)]
    [InlineData("gensec_spnego_client_negTokenInit_step: Could not find a suitable mechtype in NEG_TOKEN_INIT\nsession setup failed: NT_STATUS_INVALID_PARAMETER", SysvolErrorKind.Authentication)]
    [InlineData("do_connect: Connection to dc01.contoso.com failed (Error NT_STATUS_HOST_UNREACHABLE)", SysvolErrorKind.Unreachable)]
    [InlineData("cd \\contoso.com\\Policies\\{6AC1786C-016F-11D2-945F-00C04FB984F9}\\: NT_STATUS_OBJECT_NAME_NOT_FOUND", SysvolErrorKind.NotFound)]
    public async Task List_Failure_IsClassifiedByItsNtStatus(string stderr, SysvolErrorKind expected)
    {
        SkipUnlessUnix();
        var reader = Reader(stdout: [], stderr: stderr, exitCode: 1);

        var error = await Assert.ThrowsAsync<SysvolReadException>(() => reader.ListAsync(Folder, Ct));

        Assert.Equal(expected, error.Kind);
        Assert.StartsWith("NT_STATUS_", error.NtStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_NonZeroExitWithoutStatus_Fails()
    {
        SkipUnlessUnix();
        var error = await Assert.ThrowsAsync<SysvolReadException>(() => Reader(stdout: [], exitCode: 2).ListAsync(Folder, Ct));
        Assert.Equal(SysvolErrorKind.Failed, error.Kind);
    }

    [Fact]
    public async Task Read_StreamsTheTarFromStdout_OneArgumentPerFile_MissingFilesByTheirStatus()
    {
        SkipUnlessUnix();
        var registry = new byte[] { 0x50, 0x52, 0x65, 0x67, 1, 0, 0, 0 };
        var tar = Tar(("GPT.INI", "[General]\r\nVersion=65537\r\n"u8.ToArray()), ("Machine/Registry.pol", registry), ("Machine/extra.txt", [1]));
        var stderr = @"NT_STATUS_NO_SUCH_FILE listing \contoso.com\Policies\{6AC1786C-016F-11D2-945F-00C04FB984F9}\Machine\missing; file.txt"
                     + "\ntar:814  do_list failed on Machine\\missing; file.txt (NT_STATUS_NO_SUCH_FILE)";
        var reader = Reader(stdout: tar, stderr: stderr, exitCode: 1);

        var files = await reader.ReadAsync(Folder, ["GPT.INI", "machine/registry.pol", "Machine/missing; file.txt", "-c.txt"], 1024, Ct);

        Assert.Equal(["-E", "-Tc", "-", "GPT.INI", "machine/registry.pol", "Machine/missing; file.txt"], Arguments().Skip(9));
        Assert.Equal("[General]\r\nVersion=65537\r\n", Encoding.ASCII.GetString(files[0].Content!));
        Assert.Equal(registry, files[1].Content);
        Assert.Equal("machine/registry.pol", files[1].RelativePath);
        Assert.Equal((SysvolErrorKind.NotFound, "NT_STATUS_NO_SUCH_FILE"), (files[2].Error!.Value, files[2].NtStatus));
        Assert.Equal(SysvolErrorKind.Failed, files[3].Error); // would be read as an option: never passed to smbclient
        Assert.All(files.Take(2), f => Assert.Null(f.Error));
    }

    [Fact]
    public async Task Read_FileAboveTheLimit_IsTooLarge_OthersAreRead()
    {
        SkipUnlessUnix();
        var reader = Reader(stdout: Tar(("GPT.INI", new byte[10]), ("Machine/Registry.pol", new byte[2000])));

        var files = await reader.ReadAsync(Folder, ["Machine/Registry.pol", "GPT.INI"], 1024, Ct);

        Assert.Equal(SysvolErrorKind.TooLarge, files[0].Error);
        Assert.Null(files[0].Content);
        Assert.Equal(10, files[1].Content!.Length);
    }

    [Fact]
    public async Task Read_FolderMissing_FailsAsAWhole()
    {
        SkipUnlessUnix();
        var reader = Reader(stdout: [], stderr: @"cd \contoso.com\Policies\{6AC1786C-016F-11D2-945F-00C04FB984F9}\: NT_STATUS_OBJECT_NAME_NOT_FOUND", exitCode: 1);

        var error = await Assert.ThrowsAsync<SysvolReadException>(() => reader.ReadAsync(Folder, ["GPT.INI"], 1024, Ct));

        Assert.Equal(SysvolErrorKind.NotFound, error.Kind);
    }

    [Fact]
    public async Task ForeignHost_IsRefused_BeforeATicketOrAProcess()
    {
        SkipUnlessUnix();
        var reader = Reader(stdout: []);

        foreach (var folder in new[] { SysvolGpoFolder.Create("evil.example", Domain, Gpo), SysvolGpoFolder.Create(Dc, "evil.example", Gpo) })
        {
            var list = await Assert.ThrowsAsync<SysvolReadException>(() => reader.ListAsync(folder, Ct));
            await Assert.ThrowsAsync<SysvolReadException>(() => reader.ReadAsync(folder, ["GPT.INI"], 1024, Ct));
            Assert.Contains("Refused", list.Message, StringComparison.Ordinal);
        }

        Assert.False(File.Exists(ArgsFile));
        Assert.Equal(0, _tickets.Calls);
        Assert.Throws<ArgumentException>(() => SysvolGpoFolder.Create("10.0.1.5", Domain, Gpo));
    }

    [Fact]
    public async Task TicketFailure_StopsBeforeSmbclient()
    {
        SkipUnlessUnix();
        _tickets.Failure = new SysvolReadException(SysvolErrorKind.Authentication, null, "no TGT");
        var error = await Assert.ThrowsAsync<SysvolReadException>(() => Reader(stdout: []).ListAsync(Folder, Ct));
        Assert.Equal(SysvolErrorKind.Authentication, error.Kind);
        Assert.False(File.Exists(ArgsFile));
    }

    [Fact]
    public async Task Invocation_AboveTheTimeLimit_IsKilled()
    {
        SkipUnlessUnix();
        var reader = Reader(stdout: [], sleepSeconds: 30, timeout: TimeSpan.FromSeconds(1));
        var started = DateTimeOffset.UtcNow;

        var error = await Assert.ThrowsAsync<SysvolReadException>(() => reader.ListAsync(Folder, Ct));

        Assert.Equal(SysvolErrorKind.Timeout, error.Kind);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task MissingExecutable_IsAReadFailure()
    {
        SkipUnlessUnix();
        var reader = new SmbClientSysvolReader(
            new SmbClientProcess(new SmbClientProcessOptions { ExecutablePath = Path.Combine(_directory, "no-such-smbclient") }),
            new SmbClientSysvolOptions { ConfigFile = "/etc/admng-watchdog/smb-client.conf" },
            _tickets,
            Sessions());

        await Assert.ThrowsAsync<SysvolReadException>(() => reader.ListAsync(Folder, Ct));
    }

    private static void SkipUnlessUnix() => Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake smbclient is a POSIX shell script.");

    private static ScenarioSessionProvider Sessions() => new(new FakeDirectorySession(FakeDirectorySession.CreateInfo(Domain, Dc)));

    private SmbClientSysvolReader Reader(byte[] stdout, string stderr = "", int exitCode = 0, int sleepSeconds = 0, TimeSpan? timeout = null)
    {
        File.WriteAllBytes(Path.Combine(_directory, "stdout"), stdout);
        File.WriteAllText(Path.Combine(_directory, "stderr"), stderr);
        var script = Path.Combine(_directory, "smbclient");
        File.WriteAllText(script, $"""
            #!/bin/sh
            for a in "$@"; do printf '%s\n' "$a"; done > '{ArgsFile}'
            printf 'KRB5CCNAME=%s\nTZ=%s\nPASSWD=%s\n' "$KRB5CCNAME" "$TZ" "$PASSWD" > '{EnvFile}'
            if [ {sleepSeconds} -gt 0 ]; then sleep {sleepSeconds}; fi
            cat '{_directory}/stdout'
            cat '{_directory}/stderr' >&2
            exit {exitCode}

            """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return new SmbClientSysvolReader(
            new SmbClientProcess(new SmbClientProcessOptions
            {
                ExecutablePath = script,
                Timeout = timeout ?? TimeSpan.FromSeconds(30),
                Environment = new Dictionary<string, string> { ["KRB5CCNAME"] = "FILE:/run/admng-watchdog/krb5cc" },
            }),
            new SmbClientSysvolOptions { ConfigFile = "/etc/admng-watchdog/smb-client.conf", StateDirectory = StateDirectory },
            _tickets,
            Sessions());
    }

    private string[] Arguments() => File.ReadAllLines(ArgsFile);

    private static string Listing(params string[] entries) =>
        "  .                                   D        0  Tue Oct 20 18:01:15 2026\n" + string.Join('\n', entries)
        + "\n\n\t\t264212084 blocks of size 1024. 14151244 blocks available\n";

    /// <summary>The tar smbclient writes: names <c>./&lt;domain&gt;/Policies/{GUID}/&lt;path&gt;</c>.</summary>
    private static byte[] Tar(params (string Path, byte[] Content)[] files)
    {
        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, TarEntryFormat.Ustar, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "./" + Domain + "/Policies/{6AC1786C-016F-11D2-945F-00C04FB984F9}/" + path)
                {
                    DataStream = new MemoryStream(content),
                });
            }
        }

        return output.ToArray();
    }

    private sealed class CountingTickets : ISmbTicketSource
    {
        public int Calls { get; private set; }

        public SysvolReadException? Failure { get; set; }

        public Task EnsureAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }
}
