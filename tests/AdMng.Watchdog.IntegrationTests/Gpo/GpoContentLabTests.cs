using System.Globalization;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Gpo;
using AdMng.Watchdog.IntegrationTests.Infrastructure;
using AdMng.Watchdog.Service.Secrets;
using AdMng.Watchdog.Service.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdMng.Watchdog.IntegrationTests.Gpo;

/// <summary>
/// B5 acceptance on the Samba lab (stage B design 8, 21): the real detection engine (Administrator) finds the GPC changes,
/// <see cref="GpoContentJob"/> reads SYSVOL of the session DC through smbclient with the Kerberos TGT of a fresh computer
/// account (keytab from <c>admng-watchdog-keytab init</c>) and stores its snapshots and events in a SQLite store under
/// test. The test GPO is created with <c>samba-tool</c>, its files are written over SMB as the administrator the way GPMC
/// writes them (files, then GPT.INI and the GPC <c>versionNumber</c>) and it is deleted at the end.
/// </summary>
/// <remarks>The engine's events (the triggers) are in the engine's in-memory store; the content events go to the SQLite
/// store whose database is scanned for the GPP <c>cpassword</c>.</remarks>
[SupportedOSPlatform("linux")]
public sealed class GpoContentLabTests(AdministratorEngineFixture engine, LabFixture lab) : IClassFixture<AdministratorEngineFixture>
{
    /// <summary>A made-up GPP cpassword (MS14-025 shape) that must never reach the database.</summary>
    private const string CPassword = "TGFiLUI1Yi1jcGFzc3dvcmQtbmllLXByYXdkeml3ZS16YcW8w7PFgsSHLTIwMjY=";

    private const string GptTmpl = "Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GpoEdits_ReportWasIs_RedirectionWithoutSmb_OutOfBandByReconciliation_NoCpasswordInTheDatabase()
    {
        GpoLab.Require();
        var host = engine.Host;
        var dc = (await host.Sessions.GetSessionAsync(Ct)).Info.Server;
        using var sysvol = await GpoLab.CreateAsync(dc, Ct);
        var principal = await sysvol.CreateServiceAccountAsync(lab, Ct);
        var (reader, _) = sysvol.CreateReader(principal, host.Sessions);
        var recording = new RecordingReader(reader);
        var data = System.IO.Directory.CreateDirectory(Path.Combine(sysvol.Directory, "data")).FullName;
        await using var store = SqliteWatchdogStore.Open(data, new SecretProtector(new EphemeralDataProtectionProvider()), host.Clock,
            NullLogger<MigrationRunner>.Instance);
        var job = new GpoContentJob(store, host.Events, host.Snapshots, recording, host.Sessions,
            host.Services.GetRequiredService<IWatchdogConfigProvider>(), host.Health, host.Services.GetRequiredService<IWatchdogSignals>(),
            host.Clock, NullLogger<GpoContentJob>.Instance);
        var gpo = await sysvol.CreateGpoAsync("AdMng B5 " + lab.RunOuName, Ct);
        try
        {
            var gpcDn = "CN=" + GpoLab.Folder(gpo) + ",CN=Policies,CN=System," + WatchdogLab.DomainDn;
            var version = 0;

            // Baseline: the new GPO is read (created → its first content: GPT.INI only, nothing to report).
            await host.RunDetectionAsync(Ct);
            await RunJobAsync(job, host);
            await host.RunDetectionAsync(Ct);
            await RunJobAsync(job, host);
            Assert.NotNull(await store.GetGpoContentAsync(gpo, Ct));
            Assert.Empty(await ContentEventsAsync(store, gpo));
            // 1. Security template: account policy and a user right.
            await sysvol.PutAsync(gpo, GptTmpl, SecurityTemplate("8", "*S-1-5-32-544"), Ct);
            await BumpAsync(++version);
            await sysvol.PutAsync(gpo, GptTmpl, SecurityTemplate("12", "*S-1-5-32-544,*S-1-5-32-551"), Ct);
            var security = await EditedAsync(++version);
            Assert.Equal(WatchEventKind.GroupPolicySettingsChanged, security.Kind);
            Assert.Equal(Severity.High, security.Severity);
            var length = Row(security, "Computer/Security/System Access/MinimumPasswordLength");
            Assert.Equal(["8"], length.OldValues);
            Assert.Equal(["12"], length.NewValues);
            Assert.Equal(["S-1-5-32-551"], Row(security, "Computer/Security/Privilege Rights/SeDebugPrivilege").Added);
            // 2. Registry.pol (not a sensitive path, unlinked GPO: Low).
            await sysvol.PutAsync(gpo, "Machine/Registry.pol", RegistryPol(0), Ct);
            await BumpAsync(++version);
            await sysvol.PutAsync(gpo, "Machine/Registry.pol", RegistryPol(1), Ct);
            var registry = await EditedAsync(++version);
            Assert.Equal(Severity.Low, registry.Severity);
            var update = Row(registry, @"Computer/Registry/HKLM\Software\Policies\Microsoft\Windows\WindowsUpdate\AU!NoAutoUpdate");
            Assert.Equal(["REG_DWORD:0"], update.OldValues);
            Assert.Equal(["REG_DWORD:1"], update.NewValues);
            // 3. Startup script: scripts.ini and the script file (content hash).
            await sysvol.PutAsync(gpo, "Machine/Scripts/scripts.ini", Utf16("[Startup]\n0CmdLine=start.cmd\n0Parameters=\n"), Ct);
            await sysvol.PutAsync(gpo, "Machine/Scripts/Startup/start.cmd", Encoding.ASCII.GetBytes("@echo off\r\necho v1\r\n"), Ct);
            await BumpAsync(++version);
            await sysvol.PutAsync(gpo, "Machine/Scripts/scripts.ini", Utf16("[Startup]\n0CmdLine=start.cmd\n0Parameters=/ciche\n"), Ct);
            await sysvol.PutAsync(gpo, "Machine/Scripts/Startup/start.cmd", Encoding.ASCII.GetBytes("@echo off\r\necho v2 – zmienione\r\n"), Ct);
            var scripts = await EditedAsync(++version);
            Assert.Equal(Severity.High, scripts.Severity);
            Assert.Equal(["/ciche"], Row(scripts, "Computer/Scripts/Startup/0/Parameters").NewValues);
            Assert.Equal(AttributeValueKind.Hash, Row(scripts, "Computer/Scripts/Files/Startup/start.cmd").ValueKind);
            // 4. GPP immediate scheduled task: the command.
            await sysvol.PutAsync(gpo, "Machine/Preferences/ScheduledTasks/ScheduledTasks.xml", ScheduledTask("cmd.exe"), Ct);
            await BumpAsync(++version);
            await sysvol.PutAsync(gpo, "Machine/Preferences/ScheduledTasks/ScheduledTasks.xml", ScheduledTask("powershell.exe"), Ct);
            var task = await EditedAsync(++version);
            Assert.Equal(Severity.High, task.Severity);
            var command = Assert.Single(task.Changes, c => c.Attribute.EndsWith("/Task/Actions/Exec/Command", StringComparison.Ordinal));
            Assert.Equal(["cmd.exe"], command.OldValues);
            Assert.Equal(["powershell.exe"], command.NewValues);
            // 5. GPP local users with a cpassword (MS14-025): presence only, Critical, tag gppPassword (every later
            // content event of this GPO too: the MS14-025 exposure lasts while the value is in SYSVOL).
            await sysvol.PutAsync(gpo, "Machine/Preferences/Groups/Groups.xml", Groups(CPassword), Ct);
            var gpp = await EditedAsync(++version);
            Assert.Equal(Severity.Critical, gpp.Severity);
            Assert.Contains(EventTags.GppPassword, gpp.Tags);
            var password = Assert.Single(gpp.Changes, c => c.Attribute.EndsWith("/cpassword", StringComparison.OrdinalIgnoreCase));
            Assert.True(password.Redacted);
            Assert.Empty(password.NewValues);

            // cpassword never reaches the database (snapshots, events, queue, meta), not even hashed or decoded.
            AssertNotInDatabase(store.Database.DatabasePath, CPassword);
            // 6. Out-of-band SYSVOL edit (no versionNumber change): found by a forced reconciliation.
            await sysvol.PutAsync(gpo, GptTmpl, SecurityTemplate("14", "*S-1-5-32-544,*S-1-5-32-551"), Ct);
            await host.RunDetectionAsync(Ct);
            await RunJobAsync(job, host);
            Assert.Empty(await ContentEventsAsync(store, gpo, WatchEventKind.GroupPolicyContentOutOfBand));
            job.ForceReconciliation();
            await RunJobAsync(job, host); // the listing differs → queued
            await RunJobAsync(job, host); // read → 512
            var outOfBand = Assert.Single(await ContentEventsAsync(store, gpo, WatchEventKind.GroupPolicyContentOutOfBand));
            Assert.Equal(Severity.High, outOfBand.Severity); // unlinked; the gppPassword tag does not raise 512
            Assert.Contains(EventTags.GppPassword, outOfBand.Tags);
            Assert.Equal(AttributionReasons.SysvolNotAudited, outOfBand.Attribution.Reason);
            var oob = Row(outOfBand, "Computer/Security/System Access/MinimumPasswordLength");
            Assert.Equal(["12"], oob.OldValues);
            Assert.Equal(["14"], oob.NewValues);
            // 7. Path redirection: Critical, and no SMB connection at all – neither to the foreign host nor the DC.
            recording.Calls.Clear();
            await lab.Writer.ReplaceAsync(gpcDn, "gPCFileSysPath", @"\\evil.example\share\" + GpoLab.Folder(gpo), Ct);
            await host.RunDetectionAsync(Ct);
            await RunJobAsync(job, host);
            var redirected = Assert.Single(await ContentEventsAsync(store, gpo, WatchEventKind.GroupPolicyPathRedirected));
            Assert.Equal(Severity.Critical, redirected.Severity);
            Assert.Equal([@"\\evil.example\share\" + GpoLab.Folder(gpo)], Assert.Single(redirected.Changes).NewValues);
            Assert.Empty(recording.Calls);
            var foreign = await Assert.ThrowsAsync<SysvolReadException>(() =>
                reader.ListAsync(SysvolGpoFolder.Create("evil.example", WatchdogLab.DomainDns, gpo), Ct));
            Assert.Contains("Refused", foreign.Message, StringComparison.Ordinal);
            Assert.Throws<ArgumentException>(() => SysvolGpoFolder.Create("10.0.0.66", WatchdogLab.DomainDns, gpo));
        }
        finally
        {
            // samba-tool deletes the folder named by gPCFileSysPath: point it back to SYSVOL first.
            await lab.Writer.ReplaceAsync("CN=" + GpoLab.Folder(gpo) + ",CN=Policies,CN=System," + WatchdogLab.DomainDn, "gPCFileSysPath",
                GpoPathValidator.ExpectedPath(WatchdogLab.DomainDns, gpo), CancellationToken.None);
            await sysvol.DeleteGpoAsync(gpo, CancellationToken.None);
        }

        async Task BumpAsync(int newVersion)
        {
            await sysvol.PutAsync(gpo, "GPT.INI", Encoding.ASCII.GetBytes("[General]\r\nVersion=" + newVersion.ToString(CultureInfo.InvariantCulture) + "\r\n"), Ct);
            await lab.Writer.ReplaceAsync("CN=" + GpoLab.Folder(gpo) + ",CN=Policies,CN=System," + WatchdogLab.DomainDn, "versionNumber",
                newVersion.ToString(CultureInfo.InvariantCulture), Ct);
            await host.RunDetectionAsync(Ct);
            await RunJobAsync(job, host);
        }

        async Task<WatchEvent> EditedAsync(int newVersion)
        {
            var before = (await ContentEventsAsync(store, gpo)).Count;
            await BumpAsync(newVersion);
            var events = await ContentEventsAsync(store, gpo);
            Assert.Equal(before + 1, events.Count);
            var content = events[^1];
            Assert.Equal(WatchEventKind.GroupPolicySettingsChanged, content.Kind);
            Assert.NotEqual(Guid.Empty, content.ChangeSetId);
            var cause = await host.Events.GetChangeSetAsync(content.ChangeSetId, Ct);
            Assert.Contains(cause, e => e.Kind == WatchEventKind.GroupPolicyChanged && e.Changes.Any(c => c.Attribute == "versionNumber"));
            return content;
        }
    }

    /// <summary>
    /// Stage B design 8.3 "TGT before every invocation", with a 1-minute TGT: smbclient itself fails with it (lab: Samba's
    /// gse_krb5 refuses a TGT with only a few minutes left – NT_STATUS_INVALID_PARAMETER, a 5-minute one works), so the
    /// reader must replace it before it starts smbclient – and keep a TGT that has more than 15 minutes left.
    /// </summary>
    [Fact]
    public async Task OneMinuteTgt_IsReplacedBeforeSmbclientRuns()
    {
        GpoLab.Require();
        var host = engine.Host;
        var dc = (await host.Sessions.GetSessionAsync(Ct)).Info.Server;
        using var sysvol = await GpoLab.CreateAsync(dc, Ct);
        var principal = await sysvol.CreateServiceAccountAsync(lab, Ct);
        var defaultDomainPolicy = Guid.Parse("31B2F340-016D-11D2-945F-00C04FB984F9");
        var folder = SysvolGpoFolder.Create(dc, WatchdogLab.DomainDns, defaultDomainPolicy);

        // A 1-minute TGT of the service account in the ccache smbclient uses.
        var (_, shortTickets) = sysvol.CreateReader(principal, host.Sessions, ticketLifetime: TimeSpan.FromMinutes(1));
        await shortTickets.EnsureAsync(Ct);
        var (exit, output) = await sysvol.ListWithServiceCacheAsync(defaultDomainPolicy, Ct);
        Assert.NotEqual(0, exit);
        Assert.Contains("NT_STATUS_", output, StringComparison.Ordinal);

        var (reader, tickets) = sysvol.CreateReader(principal, host.Sessions);
        Assert.Contains((await reader.ListAsync(folder, Ct)).Entries, e => e.RelativePath.Equals("GPT.INI", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, tickets.Acquisitions); // the 1-minute TGT was replaced
        Assert.NotNull(Assert.Single(await reader.ReadAsync(folder, ["GPT.INI"], 1024 * 1024, Ct)).Content);
        Assert.Equal(1, tickets.Acquisitions); // hours left: reused
        Assert.Equal(0, (await sysvol.ListWithServiceCacheAsync(defaultDomainPolicy, Ct)).Exit);

        // An expired TGT is replaced too.
        await shortTickets.EnsureAsync(Ct); // hours left: kept
        Assert.Equal(1, shortTickets.Acquisitions);
        File.Delete(sysvol.ServiceCache[5..]);
        await shortTickets.EnsureAsync(Ct);
        await Task.Delay(TimeSpan.FromSeconds(65), Ct);
        Assert.NotNull(Assert.Single(await reader.ReadAsync(folder, ["GPT.INI"], 1024 * 1024, Ct)).Content);
        Assert.Equal(2, tickets.Acquisitions);
    }

    private static async Task RunJobAsync(GpoContentJob job, EngineHost host)
    {
        host.CatchUpWithRealTime();
        var result = await job.RunOnceAsync(Ct);
        Assert.False(result.Failed, "gpo-content failed: " + result.FailureCode + " " + string.Join("; ", host.Health.Checks
            .Where(c => c.State != Contracts.Api.HealthState.Ok).Select(c => c.Code + ": " + c.Detail)));
    }

    /// <summary>The content events of the test GPO (a GPO left behind by an aborted run is reported too).</summary>
    private static async Task<IReadOnlyList<WatchEvent>> ContentEventsAsync(SqliteWatchdogStore store, Guid gpo, params WatchEventKind[] kinds)
    {
        var page = await store.QueryAsync(new Contracts.Api.EventQuery
        {
            After = 0,
            Limit = 500,
            Kinds = kinds.Length > 0 ? kinds
                : [WatchEventKind.GroupPolicySettingsChanged, WatchEventKind.GroupPolicyPathRedirected, WatchEventKind.GroupPolicyContentOutOfBand],
        }, Ct);
        return [.. page.Items.Where(e => e.Object.Dn.Contains(GpoLab.Folder(gpo), StringComparison.OrdinalIgnoreCase))];
    }

    private static AttributeChange Row(WatchEvent e, string key) =>
        Assert.Single(e.Changes, c => c.Attribute.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>No column of any table holds the secret – text, blob or deflated blob, plain or as UTF-16.</summary>
    private static void AssertNotInDatabase(string databasePath, string secret)
    {
        var needles = new[] { Encoding.UTF8.GetBytes(secret), Encoding.Unicode.GetBytes(secret), Convert.FromBase64String(secret) };
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        var tables = new List<string>();
        using (var list = connection.CreateCommand())
        {
            list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
            using var reader = list.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        var scanned = 0;
        foreach (var table in tables)
        {
            using var select = connection.CreateCommand();
            select.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\";";
            using var rows = select.ExecuteReader();
            while (rows.Read())
            {
                for (var i = 0; i < rows.FieldCount; i++)
                {
                    var bytes = rows.GetValue(i) switch
                    {
                        byte[] blob => blob,
                        string text => Encoding.UTF8.GetBytes(text),
                        _ => [],
                    };
                    foreach (var candidate in new[] { bytes, TryInflate(bytes) })
                    {
                        scanned += candidate.Length;
                        Assert.DoesNotContain(needles, needle => candidate.AsSpan().IndexOf(needle) >= 0);
                    }
                }
            }
        }

        Assert.True(scanned > 0);
        static byte[] TryInflate(byte[] blob)
        {
            try
            {
                using var input = new DeflateStream(new MemoryStream(blob), CompressionMode.Decompress);
                using var output = new MemoryStream();
                input.CopyTo(output);
                return output.ToArray();
            }
            catch (InvalidDataException)
            {
                return [];
            }
        }
    }

    // ---- file contents as SYSVOL stores them ------------------------------------------------------------------------------

    private static byte[] Utf16(string text) => [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text.Replace("\n", "\r\n", StringComparison.Ordinal))];

    private static byte[] Utf8Bom(string text) => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text.Replace("\n", "\r\n", StringComparison.Ordinal))];

    private static byte[] SecurityTemplate(string minimumPasswordLength, string debugPrivilege) => Utf16($"""
        [Unicode]
        Unicode=yes
        [System Access]
        MinimumPasswordLength = {minimumPasswordLength}
        [Privilege Rights]
        SeDebugPrivilege = {debugPrivilege}
        [Version]
        signature="$CHICAGO$"
        Revision=1

        """);

    private static byte[] RegistryPol(uint noAutoUpdate)
    {
        var bytes = new List<byte>("PReg"u8.ToArray());
        bytes.AddRange(BitConverter.GetBytes(1u));
        bytes.AddRange(Encoding.Unicode.GetBytes("[Software\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU\0;NoAutoUpdate\0;"));
        bytes.AddRange(BitConverter.GetBytes(4u)); // REG_DWORD
        bytes.AddRange(Encoding.Unicode.GetBytes(";"));
        bytes.AddRange(BitConverter.GetBytes(4u));
        bytes.AddRange(Encoding.Unicode.GetBytes(";"));
        bytes.AddRange(BitConverter.GetBytes(noAutoUpdate));
        bytes.AddRange(Encoding.Unicode.GetBytes("]"));
        return [.. bytes];
    }

    private static byte[] Groups(string cpassword) => Utf8Bom($$"""
        <?xml version="1.0" encoding="utf-8"?>
        <Groups clsid="{3125E937-EB16-4b4c-9934-544FC6D24D26}">
          <User clsid="{DF5F1855-51E5-4d24-8B1A-D9BDE98BA1D1}" name="Administrator (built-in)" image="2" changed="2026-10-20 10:00:00" uid="{0B5B0001-0000-4000-8000-000000000001}">
            <Properties action="U" newName="" fullName="" description="Lokalny administrator – laboratorium B5" cpassword="{{cpassword}}" changeLogon="0" noChange="1" neverExpires="1" acctDisabled="0" subAuthority="RID_ADMIN" userName="Administrator (built-in)"/>
          </User>
        </Groups>

        """);

    private static byte[] ScheduledTask(string command) => Utf8Bom($$"""
        <?xml version="1.0" encoding="utf-8"?>
        <ScheduledTasks clsid="{CC63F200-7309-4ba0-B154-A71CD118DBCC}">
          <ImmediateTaskV2 clsid="{9756B581-76EC-4169-9AFC-0CA8D43ADB5F}" name="Zadanie B5" image="0" changed="2026-10-20 10:00:00" uid="{0B5B0002-0000-4000-8000-000000000001}" userContext="0" removePolicy="0">
            <Properties action="C" name="Zadanie B5" runAs="NT AUTHORITY\System" logonType="S4U">
              <Task version="1.3">
                <Actions Context="Author">
                  <Exec>
                    <Command>{{command}}</Command>
                    <Arguments>/c echo zażółć</Arguments>
                  </Exec>
                </Actions>
              </Task>
            </Properties>
          </ImmediateTaskV2>
        </ScheduledTasks>

        """);

    /// <summary>Records every folder the job asks the reader for.</summary>
    private sealed class RecordingReader(ISysvolReader inner) : ISysvolReader
    {
        public List<string> Calls { get; } = [];

        public Task<SysvolListing> ListAsync(SysvolGpoFolder folder, CancellationToken cancellationToken)
        {
            Calls.Add("list " + folder);
            return inner.ListAsync(folder, cancellationToken);
        }

        public Task<IReadOnlyList<SysvolFile>> ReadAsync(SysvolGpoFolder folder, IReadOnlyList<string> relativePaths, long maxBytesPerFile,
            CancellationToken cancellationToken)
        {
            Calls.Add("read " + folder);
            return inner.ReadAsync(folder, relativePaths, maxBytesPerFile, cancellationToken);
        }
    }
}
