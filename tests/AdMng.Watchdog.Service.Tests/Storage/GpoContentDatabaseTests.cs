using System.IO.Compression;
using System.Text;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Gpo;
using AdMng.Watchdog.Service.Secrets;
using AdMng.Watchdog.Service.Storage;
using AdMng.Watchdog.Testing.Gpo;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdMng.Watchdog.Service.Tests.Storage;

/// <summary>
/// Migration 0005 and the GPO content store on SQLite (stage B design 8.8, ADR-0008/0009): a GPO carrying GPP
/// <c>cpassword</c> values (MS14-025) and password-like registry values goes through the real parsers, the differ, the
/// event and the store – and no table, column or deflated blob of the database holds the secrets, their Base64-decoded
/// bytes or their SHA-256, only the presence.
/// </summary>
public sealed class GpoContentDatabaseTests : IAsyncDisposable
{
    private const string CPassword = "Zm9vYmFyLW5vdC1hLXJlYWwtY3Bhc3N3b3JkLUI1Yi0yMDI2";
    private const string TaskPassword = "UHJ6eWvFgmFkb3dlLWhhc8WCby1CNWI=";
    private const string RegistryPassword = "Hasło-Rejestru-B5b!";
    private static readonly Guid Gpo = Guid.Parse("6AC1786C-016F-11D2-945F-00C04FB984F9");
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    private readonly string _directory = System.IO.Directory.CreateTempSubdirectory("admng-gpo-db-").FullName;
    private readonly SqliteWatchdogStore _store;

    public GpoContentDatabaseTests()
    {
        _store = SqliteWatchdogStore.Open(_directory, new SecretProtector(new EphemeralDataProtectionProvider()), TimeProvider.System,
            NullLogger<MigrationRunner>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        System.IO.Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Migration0005_CreatesTheContentAndQueueTables()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'gpo_content%' ORDER BY name;";
        using var reader = command.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        Assert.Equal(["gpo_content", "gpo_content_queue"], tables);
    }

    [Fact]
    public async Task Secrets_NeverReachTheDatabase_OnlyTheirPresence()
    {
        var sysvol = new InMemorySysvolReader()
            .SetFile(Gpo, "GPT.INI", "[General]\r\nVersion=3\r\n"u8.ToArray())
            .SetFile(Gpo, "Machine/Preferences/Groups/Groups.xml", Utf8Bom($$"""
                <?xml version="1.0" encoding="utf-8"?>
                <Groups clsid="{3125E937-EB16-4b4c-9934-544FC6D24D26}">
                  <User clsid="{DF5F1855-51E5-4d24-8B1A-D9BDE98BA1D1}" name="Administrator (built-in)" image="2" changed="2026-10-20 08:00:00" uid="{A1B2C3D4-0000-4000-8000-000000000001}">
                    <Properties action="U" newName="" fullName="" description="Łódź" cpassword="{{CPassword}}" changeLogon="0" noChange="1" neverExpires="1" acctDisabled="0" userName="Administrator (built-in)"/>
                  </User>
                </Groups>
                """))
            .SetFile(Gpo, "Machine/Preferences/ScheduledTasks/ScheduledTasks.xml", Utf8Bom($$"""
                <?xml version="1.0" encoding="utf-8"?>
                <ScheduledTasks clsid="{CC63F200-7309-4ba0-B154-A71CD118DBCC}">
                  <Task clsid="{2DEECB1C-261F-4e13-9B21-16FB83BC03BD}" name="Kopia" image="2" changed="2026-10-20 08:00:00" uid="{B1B2C3D4-0000-4000-8000-000000000002}">
                    <Properties action="U" name="Kopia" appName="C:\kopia.exe" args="" runAs="CONTOSO\svc-kopia" cpassword="{{TaskPassword}}" enabled="1"/>
                  </Task>
                </ScheduledTasks>
                """))
            .SetFile(Gpo, "Machine/Registry.pol", RegistryPol(@"Software\Microsoft\Windows NT\CurrentVersion\Winlogon", "DefaultPassword", RegistryPassword));
        var folder = SysvolGpoFolder.Create("dc01.contoso.com", "contoso.com", Gpo);
        var plan = GpoReadPlanner.Plan(await sysvol.ListAsync(folder, Ct));
        var files = await sysvol.ReadAsync(folder, [.. plan.Read.Select(f => f.Path)], GpoReadLimits.Default.MaxFileBytes, Ct);
        var result = GpoSnapshotBuilder.Build(new GpoSnapshotInput { GpoGuid = Gpo, Dc = folder.Dc, GpcVersion = 3, ReadAt = Now, Plan = plan, Files = files });
        var diff = GpoSettingsDiffer.Diff([], result.Snapshot.Settings);
        var content = GpoContentEvents.SettingsChanged(new GpoEventContext
        {
            Gpo = new ObjectRef { Dn = "CN={" + Gpo + "},CN=Policies,CN=System,DC=contoso,DC=com", Kind = WatchObjectKind.GroupPolicy, ObjectGuid = Guid.NewGuid() },
            DetectedAt = Now,
            ChangeSetId = Guid.NewGuid(),
        }, diff);
        await _store.EnqueueGpoContentAsync(new GpoQueueEntry { GpoGuid = Gpo, Reason = GpoQueueReason.Changed, DueAt = Now, EnqueuedAt = Now }, Ct);

        await _store.CommitGpoContentAsync(result.Snapshot, [content], Ct);

        // Presence is kept (snapshot and event), values are not.
        Assert.Contains(EventTags.GppPassword, content.Tags);
        var stored = (await _store.GetGpoContentAsync(Gpo, Ct))!.Snapshot;
        Assert.Equal(3, stored.Settings.Count(s => s.Kind == GpoSettingKind.Masked));
        Assert.All(stored.Settings.Where(s => s.Kind == GpoSettingKind.Masked), s => Assert.Empty(s.Value));
        AssertNotInDatabase(CPassword, TaskPassword, RegistryPassword);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _store.Database.DatabasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>Every column of every table, as text, blob and inflated blob: none holds a secret in any form.</summary>
    private void AssertNotInDatabase(params string[] secrets)
    {
        var needles = new List<byte[]>();
        foreach (var secret in secrets)
        {
            needles.Add(Encoding.UTF8.GetBytes(secret));
            needles.Add(Encoding.Unicode.GetBytes(secret));
            needles.Add(Encoding.UTF8.GetBytes(GpoValues.Hash(secret)[7..]));
            if (secret.Length % 4 == 0)
            {
                needles.Add(Convert.FromBase64String(secret));
            }
        }

        using var connection = Open();
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

        var cells = 0;
        foreach (var table in tables)
        {
            using var select = connection.CreateCommand();
            select.CommandText = "SELECT * FROM \"" + table + "\";";
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
                    cells++;
                    foreach (var candidate in new[] { bytes, Inflate(bytes) })
                    {
                        foreach (var needle in needles)
                        {
                            Assert.True(candidate.AsSpan().IndexOf(needle) < 0, $"a secret was found in table {table}");
                        }
                    }
                }
            }
        }

        Assert.True(cells > 10);
        // The files that hold pages (the -shm index holds none). The store's pooled connections keep them open: on Windows a
        // plain File.ReadAllBytes (FileShare.Read) fails with a sharing violation.
        var database = _store.Database.DatabasePath;
        foreach (var file in new[] { database, database + "-wal", database + "-journal" }.Where(File.Exists))
        {
            var raw = ReadShared(file);
            Assert.All(needles, needle => Assert.True(raw.AsSpan().IndexOf(needle) < 0, "a secret was found in " + Path.GetFileName(file)));
        }
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static byte[] Inflate(byte[] blob)
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

    private static byte[] Utf8Bom(string text) => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text.Replace("\n", "\r\n", StringComparison.Ordinal))];

    /// <summary>Registry.pol with one REG_SZ value ([MS-GPREG] 2.2.1).</summary>
    private static byte[] RegistryPol(string key, string value, string data)
    {
        var bytes = new List<byte>("PReg"u8.ToArray());
        bytes.AddRange(BitConverter.GetBytes(1u));
        var payload = Encoding.Unicode.GetBytes(data + "\0");
        bytes.AddRange(Encoding.Unicode.GetBytes("[" + key + "\0;" + value + "\0;"));
        bytes.AddRange(BitConverter.GetBytes(1u)); // REG_SZ
        bytes.AddRange(Encoding.Unicode.GetBytes(";"));
        bytes.AddRange(BitConverter.GetBytes((uint)payload.Length));
        bytes.AddRange(Encoding.Unicode.GetBytes(";"));
        bytes.AddRange(payload);
        bytes.AddRange(Encoding.Unicode.GetBytes("]"));
        return [.. bytes];
    }
}
