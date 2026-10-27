using System.Text;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy;

public class GpoTransactionTests
{
    private static readonly Guid Gpo = new("5D2F1A6B-0C3E-4E8B-9A7D-1B2C3D4E5F60");
    private static readonly byte[] OldPol = Encoding.UTF8.GetBytes("PReg stare zasady – Łódź");
    private static readonly byte[] NewPol = Encoding.UTF8.GetBytes("PReg nowe zasady – Gdańsk");
    private static readonly byte[] Script = Encoding.UTF8.GetBytes("echo Zażółć gęślą jaźń");

    private readonly FakeSysvol _sysvol = new();
    private readonly FakeVersions _versions = new() { Value = new GpoVersion(0, 3).ToInt32() };

    public GpoTransactionTests()
    {
        _sysvol.AddGpoFolder(Gpo, version: new GpoVersion(0, 3).ToInt32());
        _sysvol.Put(Gpo, "Machine/Registry.pol", OldPol);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PlannedChange Write(string path, byte[]? old, byte[] content) => new()
    {
        Action = ChangeAction.WriteGpoFile,
        Sysvol = new SysvolChange { GpoId = Gpo, RelativePath = path, OldContent = old, NewContent = content },
    };

    private static PlannedChange Delete(string path, byte[] old) => new()
    {
        Action = ChangeAction.DeleteGpoFile,
        Sysvol = new SysvolChange { GpoId = Gpo, RelativePath = path, OldContent = old },
    };

    private static PlannedChange Version(GpoVersion old, GpoVersion @new) => new()
    {
        Action = ChangeAction.SetGpoVersion,
        GpoVersion = new GpoVersionChange(old, old, @new),
    };

    private async Task<GpoTransactionResult> RunAsync(params PlannedChange[] steps)
    {
        var session = new FakeDirectorySession();
        await using var connection = await _sysvol.OpenForWriteAsync(session, Ct);
        return await GpoTransaction.RunAsync(connection, Gpo, steps, _versions, Ct);
    }

    private IReadOnlyList<string> Leftovers => [.. _sysvol.Files(Gpo).Where(GpoTransaction.IsTempName)];

    [Fact]
    public async Task Commit_WritesFilesThenGptIniThenVersionNumber()
    {
        var result = await RunAsync(
            Write("Machine/Registry.pol", OldPol, NewPol),
            Write("Machine/Scripts/Startup/start.cmd", null, Script),
            Version(new GpoVersion(0, 3), new GpoVersion(0, 4)));

        Assert.True(result.Succeeded);
        Assert.Equal(NewPol, _sysvol.Get(Gpo, "Machine/Registry.pol"));
        Assert.Equal(Script, _sysvol.Get(Gpo, "Machine/Scripts/Startup/start.cmd"));
        Assert.Equal("[General]\r\nVersion=4\r\n", _sysvol.GetText(Gpo, "GPT.INI"));
        Assert.Equal(4, _versions.Value);
        Assert.Empty(Leftovers);

        // Order on the wire: content renames, then GPT.INI, then the directory.
        var replaces = _sysvol.Writes.Where(w => w.Operation == "Replace").Select(w => w.Path).ToList();
        Assert.Equal(["Machine/Registry.pol", "Machine/Scripts/Startup/start.cmd", "GPT.INI"], replaces);
        Assert.Equal(1, _versions.Writes);
    }

    [Fact]
    public async Task StageFailure_ChangesNothing_AndDeletesTheTemps()
    {
        _sysvol.Fail("WriteTemp", SysvolWriteError.DiskFull, pathPart: "start.cmd");

        var result = await RunAsync(
            Write("Machine/Registry.pol", OldPol, NewPol),
            Write("Machine/Scripts/Startup/start.cmd", null, Script),
            Version(new GpoVersion(0, 3), new GpoVersion(0, 4)));

        Assert.Equal(GpoTransactionOutcome.Reverted, result.Outcome);
        Assert.Equal(DirectoryErrorKind.UnwillingToPerform, result.ErrorKind);
        Assert.StartsWith(GpoTransaction.RevertedMessage, result.Message);
        Assert.Equal(OldPol, _sysvol.Get(Gpo, "Machine/Registry.pol"));
        Assert.Empty(Leftovers);
        Assert.Equal(3, GpoVersion.FromInt32(_versions.Value!.Value).Computer);
    }

    [Fact]
    public async Task FileChangedAfterThePreview_IsAConflict_AndNothingChanges()
    {
        _sysvol.Put(Gpo, "Machine/Registry.pol", Encoding.UTF8.GetBytes("zmienione przez kogoś innego"));

        var result = await RunAsync(Write("Machine/Registry.pol", OldPol, NewPol), Version(new GpoVersion(0, 3), new GpoVersion(0, 4)));

        Assert.Equal(GpoTransactionOutcome.Conflict, result.Outcome);
        Assert.StartsWith(BatchExecutor.ConflictMessagePrefix, result.Message);
        Assert.Equal("zmienione przez kogoś innego", _sysvol.GetText(Gpo, "Machine/Registry.pol"));
        Assert.Equal("[General]\r\nVersion=3\r\n", _sysvol.GetText(Gpo, "GPT.INI"));
        Assert.Empty(Leftovers);
    }

    [Fact]
    public async Task NewFileThatAppearedMeanwhile_IsAConflict()
    {
        _sysvol.Put(Gpo, "Machine/Scripts/Startup/start.cmd", Script);

        var result = await RunAsync(Write("Machine/Scripts/Startup/start.cmd", null, NewPol), Version(new GpoVersion(0, 3), new GpoVersion(0, 4)));

        Assert.Equal(GpoTransactionOutcome.Conflict, result.Outcome);
        Assert.Equal(Script, _sysvol.Get(Gpo, "Machine/Scripts/Startup/start.cmd"));
    }

    [Fact]
    public async Task VersionNumberChangedBehindThePlan_IsAConflict_AndNoFileChanges()
    {
        _versions.Value = new GpoVersion(0, 5).ToInt32();

        var result = await RunAsync(Write("Machine/Registry.pol", OldPol, NewPol), Version(new GpoVersion(0, 3), new GpoVersion(0, 4)));

        Assert.Equal(GpoTransactionOutcome.Conflict, result.Outcome);
        Assert.Equal(OldPol, _sysvol.Get(Gpo, "Machine/Registry.pol"));
        Assert.Equal(0, _versions.Writes);
        Assert.Empty(Leftovers);
    }

    [Fact]
    public async Task SwitchFailure_PutsTheOldFilesBack()
    {
        _sysvol.Fail("Replace", SysvolWriteError.AccessDenied, pathPart: "start.cmd");

        var result = await RunAsync(
            Write("Machine/Registry.pol", OldPol, NewPol),
            Write("Machine/Scripts/Startup/start.cmd", null, Script),
            Version(new GpoVersion(0, 3), new GpoVersion(0, 4)));

        Assert.Equal(GpoTransactionOutcome.Reverted, result.Outcome);
        Assert.Equal(DirectoryErrorKind.AccessDenied, result.ErrorKind);
        Assert.Equal(OldPol, _sysvol.Get(Gpo, "Machine/Registry.pol"));
        Assert.Null(_sysvol.Get(Gpo, "Machine/Scripts/Startup/start.cmd"));
        Assert.Equal("[General]\r\nVersion=3\r\n", _sysvol.GetText(Gpo, "GPT.INI"));
        Assert.Empty(Leftovers);
    }

    [Fact]
    public async Task VersionNumberCasFailure_RestoresGptIniAndFiles()
    {
        _versions.FailNext = new DirectoryException(DirectoryErrorKind.NoSuchAttribute, "versionNumber changed (test).");

        var result = await RunAsync(
            Delete("Machine/Registry.pol", OldPol),
            Version(new GpoVersion(0, 3), new GpoVersion(0, 4)));

        Assert.Equal(GpoTransactionOutcome.Conflict, result.Outcome);
        Assert.Equal(OldPol, _sysvol.Get(Gpo, "Machine/Registry.pol"));
        Assert.Equal("[General]\r\nVersion=3\r\n", _sysvol.GetText(Gpo, "GPT.INI"));
        Assert.Empty(Leftovers);
    }

    [Fact]
    public async Task CompensationFailure_IsReportedAsInconsistent()
    {
        // The content rename succeeds, the version write fails and writing the old file back fails too.
        var writes = 0;
        _sysvol.BeforeCall = (operation, _, path) =>
        {
            if (operation == "WriteTemp" && path?.Contains("Registry.pol", StringComparison.Ordinal) == true && ++writes == 2)
            {
                throw new SysvolWriteException(SysvolWriteError.Unreachable, "gone (test)");
            }
        };
        _versions.FailNext = new DirectoryException(DirectoryErrorKind.AccessDenied, "denied (test).");

        var result = await RunAsync(Write("Machine/Registry.pol", OldPol, NewPol), Version(new GpoVersion(0, 3), new GpoVersion(0, 4)));

        Assert.Equal(GpoTransactionOutcome.Inconsistent, result.Outcome);
        Assert.StartsWith(GpoTransaction.InconsistentMessage, result.Message);
        Assert.Equal(NewPol, _sysvol.Get(Gpo, "Machine/Registry.pol"));

        // GPT.INI was put back although the file could not be.
        Assert.Equal("[General]\r\nVersion=3\r\n", _sysvol.GetText(Gpo, "GPT.INI"));
    }

    [Fact]
    public async Task Cancellation_BeforeTheSwitch_ChangesNothing()
    {
        using var cancel = new CancellationTokenSource();
        _sysvol.BeforeCall = (operation, _, _) =>
        {
            if (operation == "Read")
            {
                cancel.Cancel();
            }
        };
        await using var connection = await _sysvol.OpenForWriteAsync(new FakeDirectorySession(), Ct);

        var result = await GpoTransaction.RunAsync(connection, Gpo,
            [Write("Machine/Registry.pol", OldPol, NewPol), Version(new GpoVersion(0, 3), new GpoVersion(0, 4))], _versions, cancel.Token);

        Assert.Equal(GpoTransactionOutcome.Cancelled, result.Outcome);
        Assert.Equal(OldPol, _sysvol.Get(Gpo, "Machine/Registry.pol"));
        Assert.Empty(Leftovers);
    }

    [Fact]
    public async Task InvalidSteps_AreRefusedWithoutAnyCall()
    {
        var result = await RunAsync(Write("../evil", null, NewPol));

        Assert.False(result.Succeeded);
        Assert.Empty(_sysvol.Writes);
    }

    [Theory]
    [InlineData("Machine/Registry.pol.admng-0123456789abcdef0123456789abcdef.tmp", true)]
    [InlineData("GPT.INI.admng-0123456789ABCDEF0123456789ABCDEF.tmp", true)]
    [InlineData("Machine/Registry.pol.admng-xyz.tmp", false)]
    [InlineData("Machine/Registry.pol", false)]
    [InlineData("notes.tmp", false)]
    public void IsTempName_MatchesOnlyOwnLeftovers(string path, bool expected) => Assert.Equal(expected, GpoTransaction.IsTempName(path));

    [Fact]
    public void TempName_IsInTheTargetsFolder()
    {
        var temp = GpoTransaction.TempName("Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf", Guid.Empty);

        Assert.StartsWith("Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf.admng-", temp);
        Assert.True(GpoTransaction.IsTempName(temp));
    }

    private sealed class FakeVersions : IGpoVersionStore
    {
        public int? Value { get; set; }

        public int Writes { get; private set; }

        public DirectoryException? FailNext { get; set; }

        public Task<int?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Value);

        public Task WriteAsync(int? oldValue, int newValue, CancellationToken cancellationToken)
        {
            if (FailNext is { } failure)
            {
                FailNext = null;
                throw failure;
            }

            if (oldValue != Value)
            {
                throw new DirectoryException(DirectoryErrorKind.NoSuchAttribute, "CAS (test).");
            }

            Writes++;
            Value = newValue;
            return Task.CompletedTask;
        }
    }
}
