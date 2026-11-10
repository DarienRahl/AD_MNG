using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Diagnostics;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy.Search;

/// <summary>
/// The K7 folder reader (docs/dev/k-computers-and-gpo.md 21.1): both modes, every status, the budgets, the streaming
/// overload and the global gate of 4 SYSVOL requests, and the K6 diagnostics read that now runs through it unchanged.
/// </summary>
public sealed class GpoFolderReaderTests : GroupPolicyTestBed
{
    private static readonly byte[] Template = FakeSysvolFileReader.Utf16(FakeDirectoryBuilder.Template("[System Access]", "MinimumPasswordLength = 14"));

    [Fact]
    public async Task Full_ReadsEveryFileAndTheAcl_AsTheK6Read()
    {
        var id = Sample.WorkstationPasswords;
        Sysvol.SetFolderSecurity(id, [1, 2, 3]);
        await using var sysvol = await OpenSysvolAsync();

        var results = await GpoFolderReader.ReadAsync(sysvol, [new GpoFolderRequest(id, WithSecurity: true), new GpoFolderRequest(Sample.ServerSecurity)],
            GpoFolderReadOptions.Default, null, Ct);
        var expected = await GroupPolicyReader.ReadFolderAsync(sysvol, id, Ct);

        var read = results[id];
        Assert.Equal(GpoFolderReadStatus.Read, read.Status);
        Assert.Equal(expected.Files.Keys.Order(), read.Content!.Files.Keys.Order());
        Assert.All(expected.Files, f => Assert.Equal(f.Value, read.Content.Files[f.Key]));
        Assert.Equal([1, 2, 3], read.Security!);
        Assert.True(read.SecurityRead);
        Assert.Equal((null, false), (results[Sample.ServerSecurity].Security, results[Sample.ServerSecurity].SecurityRead));
        Assert.Contains("GPT.INI", read.Content.Files.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Full_Statuses_NotFoundAccessDeniedTooLargeFailed()
    {
        var missing = Guid.NewGuid();
        var denied = Builder.AddManagedGroupPolicy(Sysvol, "Odmowa dostępu", 0, 1);
        var large = Builder.AddManagedGroupPolicy(Sysvol, "Za duży", 0, 1);
        var broken = Builder.AddManagedGroupPolicy(Sysvol, "Uszkodzony", 0, 1);
        for (var i = 0; i <= GpoTransaction.MaxEntriesPerGpo; i++)
        {
            Sysvol.PutText(large, $"Machine/plik{i}.txt", "ż");
        }

        Sysvol.PutText(broken, "Machine/Registry.pol", "PReg");
        Sysvol.Fail("List", SysvolWriteError.AccessDenied);
        Sysvol.Fail("Read", SysvolWriteError.Failed, "Registry.pol");
        await using var sysvol = await OpenSysvolAsync();

        // One worker: the AccessDenied failure of the first listing hits the first GPO.
        var options = GpoFolderReadOptions.Default with { Parallelism = 1 };
        var results = await GpoFolderReader.ReadAsync(sysvol,
            [new GpoFolderRequest(denied), new GpoFolderRequest(missing), new GpoFolderRequest(large), new GpoFolderRequest(broken)], options, null, Ct);

        Assert.Equal(GpoFolderReadStatus.AccessDenied, results[denied].Status);
        Assert.Equal(GpoFolderReadStatus.NotFound, results[missing].Status);
        Assert.Equal(GpoFolderReadStatus.TooLarge, results[large].Status);
        Assert.Equal(GpoFolderReadStatus.Failed, results[broken].Status);
        Assert.All(results.Values, r => Assert.Null(r.Content));
        Assert.All(results.Values, r => Assert.Equal(new GpoFolderState { Status = r.Status }, r.ToState()));
    }

    [Fact]
    public async Task ToState_OfARead_IsTheK6State()
    {
        await using var sysvol = await OpenSysvolAsync();
        var result = (await GpoFolderReader.ReadAsync(sysvol, [new GpoFolderRequest(Sample.DriveMappings, true)], GpoFolderReadOptions.Default, null, Ct))[Sample.DriveMappings];

        var state = result.ToState();

        Assert.Equal(GpoFolderReadStatus.Read, state.Status);
        Assert.Same(result.Content, state.Content);
        Assert.True(state.SecurityRead);
    }

    [Fact]
    public async Task Diagnostics_FullSnapshotFolders_EqualThePreK7Read()
    {
        Sysvol.SetFolderSecurity(Sample.WorkstationPasswords, [7, 7, 7]);
        await using var sysvol = await OpenSysvolAsync();

        var snapshot = await GroupPolicyDiagnosticsReader.ReadAsync(Session, sysvol, new GpoDiagnosticsRequest(Depth: GpoDiagnosticsDepth.Full), null, Ct);

        // The pre-K7 diagnostics read, per GPO: ReadFolderAsync, then the folder ACL.
        Assert.Equal(snapshot.Gpos.Count, snapshot.Folders.Count);
        foreach (var gpo in snapshot.Gpos)
        {
            var expected = await GroupPolicyReader.ReadFolderAsync(sysvol, gpo.Id, Ct);
            var security = await sysvol.GetGpoFolderSecurityAsync(gpo.Id, Ct);
            var state = snapshot.Folders[gpo.Id];
            Assert.Equal((GpoFolderReadStatus.Read, true), (state.Status, state.SecurityRead));
            Assert.Equal(security, state.Security);
            Assert.Equal(expected.Files.Keys.Order(StringComparer.OrdinalIgnoreCase), state.Content!.Files.Keys.Order(StringComparer.OrdinalIgnoreCase));
            Assert.Equal(expected.Entries.Count, state.Content.Entries.Count);
        }
    }

    [Fact]
    public async Task Diagnostics_OneGpo_ReadsTheContextFoldersWithoutAcl_AndReportsFolderProgress()
    {
        await using var sysvol = await OpenSysvolAsync();
        var progress = new List<GpoDiagnosticsProgress>();

        var snapshot = await GroupPolicyDiagnosticsReader.ReadAsync(Session, sysvol, new GpoDiagnosticsRequest(Sample.WorkstationPasswords, GpoDiagnosticsDepth.Full),
            new SyncProgress<GpoDiagnosticsProgress>(progress.Add), Ct);

        Assert.True(snapshot.Folders[Sample.WorkstationPasswords].SecurityRead);
        Assert.All(snapshot.Folders.Where(f => f.Key != Sample.WorkstationPasswords), f => Assert.False(f.Value.SecurityRead));
        var folders = progress.Where(p => p.Stage == GpoDiagnosticsStage.Folders).ToList();

        // 0, 1, 2 ... of the total, in the order the reports arrive: the folders are read in parallel, and a report of
        // one worker must not overtake the report of another (the progress would end below the total).
        Assert.Equal(Enumerable.Range(0, snapshot.Folders.Count + 1), folders.Select(f => f.Done));
        Assert.All(folders, f => Assert.Equal(snapshot.Folders.Count, f.Total));
    }

    [Fact]
    public async Task ContentOnly_ReadsOnlyParsedFiles_ListsOtherFilesAndLeftovers()
    {
        var id = Builder.AddManagedGroupPolicy(Sysvol, "Treść – Księgowość", 1, 1);
        Sysvol.Put(id, SecurityTemplate.RelativePath, Template);
        Sysvol.PutText(id, "Machine/Scripts/scripts.ini", "[Startup]\r\n0CmdLine=porządki.cmd\r\n0Parameters=\r\n");
        Sysvol.PutText(id, "Machine/Scripts/Startup/porządki.cmd", "echo Zażółć");
        Sysvol.PutText(id, "User/Documents & Settings/fdeploy.ini", "[FolderStatus]");
        Sysvol.PutText(id, "Machine/Applications/aplikacja.aas", "x");
        Sysvol.PutText(id, "notatki.txt", "luźne");
        Sysvol.PutText(id, "GPO.cmt", "Komentarz – Łódź");
        var leftover = GpoTransaction.TempName("Machine/Registry.pol", Guid.NewGuid());
        Sysvol.PutText(id, leftover, "tmp");
        await using var sysvol = await OpenSysvolAsync();
        Sysvol.ClearCalls();

        var result = (await GpoFolderReader.ReadAsync(sysvol, [new GpoFolderRequest(id)], GpoFolderReadOptions.Settings, null, Ct))[id];

        Assert.Equal(GpoFolderReadStatus.Read, result.Status);
        Assert.Equal(["GPO.cmt", "Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf", "Machine/Scripts/scripts.ini"],
            result.Content!.Files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["Machine/Applications/aplikacja.aas", "User/Documents & Settings/fdeploy.ini"], result.OtherFiles.Order(StringComparer.Ordinal));
        Assert.Equal([leftover], result.Content.Leftovers);
        Assert.Empty(result.NotRead);
        Assert.Equal(1, Sysvol.Calls.Count(c => c.Operation == "List"));
        Assert.DoesNotContain(Sysvol.Calls, c => c.Operation == "Read" && (c.Path!.EndsWith("GPT.INI", StringComparison.OrdinalIgnoreCase) || c.Path.EndsWith(".cmd", StringComparison.Ordinal)));
        Assert.Equal(new GpoFolderState { Status = GpoFolderReadStatus.Read, Content = result.Content }, result.ToState());
    }

    [Fact]
    public async Task ContentOnly_FileAndGpoBudgets_KeepFilesOut_InPriorityOrder()
    {
        var id = Builder.AddManagedGroupPolicy(Sysvol, "Budżet", 1, 1);
        Sysvol.Put(id, SecurityTemplate.RelativePath, Template); // priority 1
        Sysvol.Put(id, "Machine/Registry.pol", new byte[300]); // priority 3
        Sysvol.PutText(id, "User/Preferences/Drives/Drives.xml", new string('x', 40)); // priority 5
        Sysvol.PutText(id, "GPO.cmt", new string('y', 5000)); // above the file budget
        await using var sysvol = await OpenSysvolAsync();
        var options = GpoFolderReadOptions.Settings with { MaxFileBytes = 1000, MaxGpoBytes = Template.Length + 100 };

        var result = (await GpoFolderReader.ReadAsync(sysvol, [new GpoFolderRequest(id)], options, null, Ct))[id];

        Assert.Equal(GpoFolderReadStatus.Read, result.Status);
        Assert.Equal(["Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf"], result.Content!.Files.Keys);
        Assert.Equal(["GPO.cmt", "Machine/Registry.pol", "User/Preferences/Drives/Drives.xml"], result.NotRead.Order(StringComparer.Ordinal));
        Assert.True(result.Content.TotalBytes <= options.MaxGpoBytes);
    }

    [Fact]
    public async Task ContentOnly_RunBudget_SkipsTheGposNotStarted()
    {
        var first = Builder.AddManagedGroupPolicy(Sysvol, "Pierwszy", 0, 1);
        var second = Builder.AddManagedGroupPolicy(Sysvol, "Drugi", 0, 1);
        Sysvol.Put(first, SecurityTemplate.RelativePath, Template);
        Sysvol.Put(second, SecurityTemplate.RelativePath, Template);
        await using var sysvol = await OpenSysvolAsync();
        Sysvol.ClearCalls();
        var options = GpoFolderReadOptions.Settings with { Parallelism = 1, MaxTotalBytes = 10 };

        var results = await GpoFolderReader.ReadAsync(sysvol, [new GpoFolderRequest(first), new GpoFolderRequest(second)], options, null, Ct);

        Assert.Equal((GpoFolderReadStatus.Read, false), (results[first].Status, results[first].Skipped));
        Assert.True(results[second].Skipped);
        Assert.Null(results[second].Content);
        Assert.DoesNotContain(Sysvol.Calls, c => c.GpoId == second);
    }

    [Fact]
    public async Task ContentOnly_Statuses_NotFoundAccessDeniedTooLargeFailed()
    {
        var missing = Guid.NewGuid();
        var denied = Builder.AddManagedGroupPolicy(Sysvol, "Bez dostępu", 0, 1);
        var large = Builder.AddManagedGroupPolicy(Sysvol, "Ogromny", 0, 1);
        var gone = Builder.AddManagedGroupPolicy(Sysvol, "Znika", 0, 1);
        for (var i = 0; i <= GpoTransaction.MaxEntriesPerGpo; i++)
        {
            Sysvol.PutText(large, $"User/plik{i}.txt", "ą");
        }

        Sysvol.Put(denied, SecurityTemplate.RelativePath, Template);
        Sysvol.Put(gone, SecurityTemplate.RelativePath, Template);
        Sysvol.Fail("Read", SysvolWriteError.AccessDenied, "GptTmpl.inf");
        Sysvol.Fail("Read", SysvolWriteError.Failed, "GptTmpl.inf");
        await using var sysvol = await OpenSysvolAsync();
        var options = GpoFolderReadOptions.Settings with { Parallelism = 1 };

        var results = await GpoFolderReader.ReadAsync(sysvol,
            [new GpoFolderRequest(denied), new GpoFolderRequest(gone), new GpoFolderRequest(missing), new GpoFolderRequest(large)], options, null, Ct);

        Assert.Equal(GpoFolderReadStatus.AccessDenied, results[denied].Status);
        Assert.Equal(GpoFolderReadStatus.Failed, results[gone].Status);
        Assert.Equal(GpoFolderReadStatus.NotFound, results[missing].Status);
        Assert.Equal(GpoFolderReadStatus.TooLarge, results[large].Status);
    }

    [Theory]
    [InlineData(GpoFolderReadMode.Full)]
    [InlineData(GpoFolderReadMode.ContentOnly)]
    public async Task ManyFolders_AtMostFourSysvolCallsAtATime(GpoFolderReadMode mode)
    {
        var ids = Enumerable.Range(0, 16).Select(i => Builder.AddManagedGroupPolicy(Sysvol, $"Oddział {i} – Kraków", 0, 1)).ToList();
        foreach (var id in ids)
        {
            Sysvol.Put(id, SecurityTemplate.RelativePath, Template);
            Sysvol.PutText(id, "Machine/Scripts/scripts.ini", "[Startup]\r\n0CmdLine=a.cmd\r\n");
            Sysvol.PutText(id, "GPO.cmt", "Ł");
        }

        await using var inner = await OpenSysvolAsync();
        var counting = new CountingSysvolConnection(inner);

        var results = await GpoFolderReader.ReadAsync(counting, [.. ids.Select(i => new GpoFolderRequest(i, true))],
            GpoFolderReadOptions.Default with { Mode = mode }, null, Ct);

        Assert.Equal(16, results.Count);
        Assert.All(results.Values, r => Assert.Equal(GpoFolderReadStatus.Read, r.Status));
        Assert.InRange(counting.MaxConcurrent, 2, 4);
    }

    [Fact]
    public async Task Streaming_AtMostParallelismFoldersAliveAtATime_AndProgressCounts()
    {
        var ids = Enumerable.Range(0, 12).Select(i => Builder.AddManagedGroupPolicy(Sysvol, $"Strumień {i}", 0, 1)).ToList();
        foreach (var id in ids)
        {
            Sysvol.Put(id, SecurityTemplate.RelativePath, Template);
        }

        await using var sysvol = await OpenSysvolAsync();
        var alive = 0;
        var maxAlive = 0;
        var seen = new List<Guid>();
        var progress = new List<GpoFolderReadProgress>();
        var options = GpoFolderReadOptions.Settings with { Parallelism = 3 };

        await GpoFolderReader.ReadAsync(sysvol, [.. ids.Select(i => new GpoFolderRequest(i))], options, async (result, token) =>
        {
            Assert.NotNull(result.Content);
            var now = Interlocked.Increment(ref alive);
            lock (seen)
            {
                maxAlive = Math.Max(maxAlive, now);
                seen.Add(result.GpoId);
            }

            await Task.Delay(5, token);
            Interlocked.Decrement(ref alive);
        }, new SyncProgress<GpoFolderReadProgress>(p => { lock (progress) { progress.Add(p); } }), Ct);

        Assert.Equal(ids.Order(), seen.Order());
        Assert.InRange(maxAlive, 1, 3);
        Assert.Equal(13, progress.Count);
        Assert.Equal(new GpoFolderReadProgress(12, 12), progress.MaxBy(p => p.Done));
    }

    [Fact]
    public async Task Cancellation_StopsTheRun()
    {
        var ids = Enumerable.Range(0, 8).Select(i => Builder.AddManagedGroupPolicy(Sysvol, $"Przerwany {i}", 0, 1)).ToList();
        await using var sysvol = await OpenSysvolAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var calls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GpoFolderReader.ReadAsync(sysvol, [.. ids.Select(i => new GpoFolderRequest(i))],
            GpoFolderReadOptions.Settings with { Parallelism = 1 }, (_, _) =>
            {
                calls++;
                cts.Cancel();
                return ValueTask.CompletedTask;
            }, null, cts.Token));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Result_ToString_HasNoPaths()
    {
        var result = new GpoFolderReadResult
        {
            GpoId = Guid.Empty,
            Status = GpoFolderReadStatus.Read,
            NotRead = ["Machine/Tajne – Żółw.txt"],
            OtherFiles = ["User/Księgowość.aas"],
        };

        Assert.DoesNotContain("Żółw", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Księgowość", result.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A progress that reports on the calling thread (Progress&lt;T&gt; posts to the context).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
