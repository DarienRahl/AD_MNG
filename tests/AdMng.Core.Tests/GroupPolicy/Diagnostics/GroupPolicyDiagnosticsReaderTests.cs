using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Diagnostics;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy.Diagnostics;

/// <summary>
/// The diagnostics reads on the fake directory and SYSVOL with the GPO management sample (Polish names), put through the
/// rules the way the document does: Quick, Full, one GPO, progress, cancellation.
/// </summary>
public sealed class GroupPolicyDiagnosticsReaderTests : GroupPolicyTestBed
{
    private const string LengthKey = "Computer/Security/System Access/MinimumPasswordLength";

    [Fact]
    public async Task Quick_ReadsLdapAndGptIniOnly_AndFindsTheVersionMismatch()
    {
        await using var sysvol = await OpenSysvolAsync();
        Sysvol.ClearCalls();

        var snapshot = await GroupPolicyDiagnosticsReader.ReadAsync(Session, sysvol, new GpoDiagnosticsRequest(), null, Ct);
        var result = GroupPolicyDiagnostics.Analyze(snapshot);

        Assert.Equal(7, snapshot.Gpos.Count);
        Assert.True(snapshot.SysvolVersionsRead);
        Assert.Equal((false, 0), (snapshot.FoldersRead, snapshot.Folders.Count));
        Assert.Null(snapshot.ObjectCounts);
        Assert.Null(snapshot.ExistingSids);
        Assert.Equal(Builder.DomainSid, snapshot.DomainSid);
        Assert.Equal(Builder.DomainControllersDn, snapshot.DomainControllersDn);
        Assert.All(Sysvol.Calls, c => Assert.Equal("Read", c.Operation));
        Assert.All(Sysvol.Calls, c => Assert.Equal(GptIni.RelativePath, c.Path));

        var mismatch = Findings.Single(result, GpoFindingCodes.VersionMismatch);
        Assert.Equal((Sample.DriveMappings, "Użytkownicy – Mapowanie dysków"), (mismatch.GpoId!.Value, mismatch.GpoName));
        Assert.Equal(Sample.WorkstationRemoteDesktop, Findings.Single(result, GpoFindingCodes.GpoLinksAllDisabled).GpoId);
        Findings.None(result, GpoFindingCodes.AccountPolicyBelowDomain);
        Assert.Equal([GpoRuleGroup.Folders, GpoRuleGroup.ObjectCounts, GpoRuleGroup.Principals, GpoRuleGroup.Conflicts], result.NotEvaluated);
    }

    [Fact]
    public async Task Quick_WithoutSysvol_LdapRulesOnly()
    {
        var snapshot = await GroupPolicyDiagnosticsReader.ReadAsync(Session, null, new GpoDiagnosticsRequest(), null, Ct);
        var result = GroupPolicyDiagnostics.Analyze(snapshot);

        Assert.False(snapshot.SysvolVersionsRead);
        Assert.All(snapshot.Gpos, g => Assert.Null(g.SysvolVersion));
        Findings.None(result, GpoFindingCodes.VersionMismatch);
        Assert.Contains(GpoRuleGroup.SysvolVersions, result.NotEvaluated);
        Findings.Single(result, GpoFindingCodes.GpoLinksAllDisabled);
    }

    [Fact]
    public async Task Full_ReadsFoldersCountsAndSids_AndFindsWhatTheSampleHolds()
    {
        var orphan = Builder.AddManagedGroupPolicy(Sysvol, "Serwis – Stare delegowanie", 0, 1,
            extraAces: [new FakeAce(FakeAce.AccessAllowed, 0x02, 0x000200BF, Builder.DomainSid + "-4321"), new FakeAce(FakeAce.AccessAllowed, 0x02, 0x00020094, "S-1-5-11"),
                new FakeAce(FakeAce.AccessAllowedObject, 0x02, 0x100, "S-1-5-11", GroupPolicySecurityFilter.ApplyGroupPolicyRight)]);
        Builder.SetGroupPolicyLinks(Company.GroupsOu, [(orphan, 0)]);
        Sysvol.SetFolderSecurity(Sample.WorkstationPasswords, GpoFolderAcl.Build([new GpoFolderAcl.FileAce("S-1-1-0", 0x03, GpoFolderAcl.ReadAndExecute)]));
        await using var sysvol = await OpenSysvolAsync();
        var progress = new RecordingProgress();

        var snapshot = await GroupPolicyDiagnosticsReader.ReadAsync(Session, sysvol, new GpoDiagnosticsRequest(Depth: GpoDiagnosticsDepth.Full), progress, Ct);
        var result = GroupPolicyDiagnostics.Analyze(snapshot);

        // The snapshot: every folder with its ACL, the objects by parent, the domain SIDs that exist.
        Assert.True(snapshot.FoldersRead);
        Assert.Equal(8, snapshot.Folders.Count);
        Assert.All(snapshot.Folders.Values, f => Assert.Equal((GpoFolderReadStatus.Read, true), (f.Status, f.SecurityRead)));
        Assert.Equal(new GpoObjectCount(0, 3), snapshot.ObjectCounts![Company.ComputersOu]);
        Assert.Equal(new GpoObjectCount(2, 0), snapshot.ObjectCounts[Company.ItOu]);
        Assert.Contains(Builder.SidOf(Company.LukaszWisniewski), snapshot.ExistingSids!);
        Assert.Contains(Builder.SidOf(Company.GroupIt), snapshot.ExistingSids!);
        Assert.DoesNotContain(Builder.DomainSid + "-4321", snapshot.ExistingSids!);
        Assert.Empty(result.NotEvaluated);
        Assert.Equal(
            [GpoDiagnosticsStage.Directory, GpoDiagnosticsStage.Versions, GpoDiagnosticsStage.Folders, GpoDiagnosticsStage.Objects, GpoDiagnosticsStage.Principals, GpoDiagnosticsStage.Completed],
            progress.Reports.Select(p => p.Stage).Distinct());
        Assert.Equal((8, 8), progress.Reports.Where(p => p.Stage == GpoDiagnosticsStage.Folders).Select(p => (p.Done, p.Total)).Max());

        // The findings of the sample.
        Assert.Equal(Sample.WorkstationPasswords, Findings.Single(result, GpoFindingCodes.AccountPolicyBelowDomain).GpoId);
        Assert.Equal(Sample.WorkstationPasswords, Findings.Single(result, GpoFindingCodes.FolderSecurityMismatch).GpoId);
        Assert.Equal(
            new[] { Sample.DefaultDomainControllersPolicy, Sample.WorkstationRemoteDesktop, Sample.ServerSecurity }.Order(),
            Findings.All(result, GpoFindingCodes.ExtensionNamesMissing).Select(f => f.GpoId!.Value).Order());
        Assert.Equal(orphan, Findings.Single(result, GpoFindingCodes.ComputerSettingsWithoutComputers).GpoId); // OU=Grupy holds no computer
        var weak = Findings.Single(result, GpoFindingCodes.WeakDomainPasswordPolicy);
        Assert.Equal((Sample.DefaultDomainPolicy, "MinimumPasswordLength", "7"), (weak.GpoId!.Value, weak.Arguments[0], weak.Arguments[1]));
        var conflict = Assert.Single(Findings.All(result, GpoFindingCodes.ConflictingSetting), f => f.SettingKey == LengthKey);
        Assert.Equal((Sample.WorkstationPasswords, Company.ComputersOu), (conflict.GpoId!.Value, conflict.ContainerDn!));
        Assert.Equal([Sample.DefaultDomainPolicy], conflict.RelatedGpoIds);
        Assert.Equal(["Stacje robocze – Zasady haseł", "Default Domain Policy", "12", "7"], conflict.Arguments);
        var orphaned = Findings.Single(result, GpoFindingCodes.OrphanedTrustee);
        Assert.Equal((orphan, Builder.DomainSid + "-4321"), (orphaned.GpoId!.Value, orphaned.Arguments[0]));
        Assert.Equal(new GpoQuickFix.RemoveTrustee(orphan, Builder.DomainSid + "-4321"), orphaned.Fix);
        Findings.None(result, GpoFindingCodes.UserSettingsWithoutUsers);
        Findings.None(result, GpoFindingCodes.UnreadableFile);
        Findings.None(result, GpoFindingCodes.UnresolvedPrincipal);
    }

    [Fact]
    public async Task Full_OneGpo_ItsFolderItsLinksSubtrees_AndTheGposItMeets()
    {
        await using var sysvol = await OpenSysvolAsync();

        var snapshot = await GroupPolicyDiagnosticsReader.ReadAsync(Session, sysvol,
            new GpoDiagnosticsRequest(Sample.WorkstationPasswords, GpoDiagnosticsDepth.Full), null, Ct);
        var result = GroupPolicyDiagnostics.Analyze(snapshot);

        Assert.Equal(Sample.WorkstationPasswords, snapshot.GpoId);
        Assert.Equal(new[] { Sample.DefaultDomainPolicy, Sample.WorkstationPasswords, Sample.ServerSecurity }.Order(), snapshot.Folders.Keys.Order());
        Assert.True(snapshot.Folders[Sample.WorkstationPasswords].SecurityRead);
        Assert.False(snapshot.Folders[Sample.DefaultDomainPolicy].SecurityRead);
        Assert.Equal([Company.ComputersOu], snapshot.ObjectCounts!.Keys);
        Assert.All(result, f => Assert.Equal(Sample.WorkstationPasswords, f.GpoId));
        Findings.Single(result, GpoFindingCodes.AccountPolicyBelowDomain);
        Assert.Contains(Findings.All(result, GpoFindingCodes.ConflictingSetting), f => f.SettingKey == LengthKey);
        Findings.None(result, GpoFindingCodes.WeakDomainPasswordPolicy);
    }

    [Fact]
    public async Task Full_WithAReadOnlySysvol_FolderRulesAreNotEvaluated()
    {
        await using var sysvol = await Sysvol.OpenAsync(Session, Ct);
        var readOnly = new ReadOnlyConnection(sysvol);

        var snapshot = await GroupPolicyDiagnosticsReader.ReadAsync(Session, readOnly, new GpoDiagnosticsRequest(Depth: GpoDiagnosticsDepth.Full), null, Ct);
        var result = GroupPolicyDiagnostics.Analyze(snapshot);

        Assert.True(snapshot.SysvolVersionsRead);
        Assert.False(snapshot.FoldersRead);
        Assert.Equal([GpoRuleGroup.Folders, GpoRuleGroup.Conflicts], result.NotEvaluated);
        Findings.Single(result, GpoFindingCodes.VersionMismatch);
    }

    [Fact]
    public async Task Full_AFolderThatCannotBeRead_IsReportedNotFound_OrNotRead()
    {
        Sysvol.Fail("List", SysvolWriteError.AccessDenied, times: 100);
        await using var sysvol = await OpenSysvolAsync();

        var snapshot = await GroupPolicyDiagnosticsReader.ReadAsync(Session, sysvol, new GpoDiagnosticsRequest(Sample.SitePrinters, GpoDiagnosticsDepth.Full), null, Ct);
        var result = GroupPolicyDiagnostics.Analyze(snapshot);

        Assert.Equal(GpoFolderReadStatus.AccessDenied, snapshot.Folders[Sample.SitePrinters].Status);
        Assert.Equal([Sample.SitePrinters], result.FoldersNotRead);
    }

    [Fact]
    public async Task Full_AMissingFolder_IsSysvolFolderMissing()
    {
        var gpo = Builder.AddGroupPolicyObject("Bez folderu – Kraków", attributes: new Dictionary<string, object?> { ["versionNumber"] = 1 });
        Builder.SetGroupPolicyLinks(Company.SalesOu, [(gpo, 0)]);
        await using var sysvol = await OpenSysvolAsync();

        var snapshot = await GroupPolicyDiagnosticsReader.ReadAsync(Session, sysvol, new GpoDiagnosticsRequest(gpo, GpoDiagnosticsDepth.Full), null, Ct);
        var finding = Findings.Single(GroupPolicyDiagnostics.Analyze(snapshot), GpoFindingCodes.SysvolFolderMissing);

        Assert.Equal((gpo, "Folder"), (finding.GpoId!.Value, finding.Arguments[0]));
    }

    [Fact]
    public async Task Full_AtMostFourSysvolRequestsAtATime()
    {
        foreach (var gpo in new[] { Sample.DefaultDomainPolicy, Sample.SitePrinters, Sample.ServerSecurity, Sample.WorkstationPasswords })
        {
            for (var i = 0; i < 6; i++)
            {
                Sysvol.PutText(gpo, $"User/Scripts/Logon/skrypt-{i}.cmd", $"echo Zażółć gęślą jaźń {i}");
            }
        }

        var running = 0;
        var most = 0;
        Sysvol.BeforeCall = (_, _, _) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref most, now);
            Thread.Sleep(5);
            Interlocked.Decrement(ref running);
        };
        await using var sysvol = await OpenSysvolAsync();

        var snapshot = await GroupPolicyDiagnosticsReader.ReadAsync(Session, sysvol, new GpoDiagnosticsRequest(Depth: GpoDiagnosticsDepth.Full), null, Ct);

        Assert.All(snapshot.Folders.Values, f => Assert.Equal(GpoFolderReadStatus.Read, f.Status));
        Assert.InRange(Volatile.Read(ref most), 1, 4);

        static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen)
            {
            }
        }
    }

    [Fact]
    public async Task OneGpo_ThatDoesNotExist_IsNoSuchObject()
    {
        var ex = await Assert.ThrowsAsync<DirectoryException>(() =>
            GroupPolicyDiagnosticsReader.ReadAsync(Session, null, new GpoDiagnosticsRequest(Guid.NewGuid()), null, Ct));

        Assert.Equal(DirectoryErrorKind.NoSuchObject, ex.Kind);
    }

    [Fact]
    public async Task Cancellation_StopsTheRead()
    {
        await using var sysvol = await OpenSysvolAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var progress = new RecordingProgress(p =>
        {
            if (p.Stage == GpoDiagnosticsStage.Folders)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GroupPolicyDiagnosticsReader.ReadAsync(Session, sysvol, new GpoDiagnosticsRequest(Depth: GpoDiagnosticsDepth.Full), progress, cts.Token));
    }

    /// <summary>Records the reports synchronously (Progress&lt;T&gt; would post them later).</summary>
    private sealed class RecordingProgress(Action<GpoDiagnosticsProgress>? onReport = null) : IProgress<GpoDiagnosticsProgress>
    {
        private readonly List<GpoDiagnosticsProgress> _reports = [];

        public IReadOnlyList<GpoDiagnosticsProgress> Reports
        {
            get
            {
                lock (_reports)
                {
                    return [.. _reports];
                }
            }
        }

        public void Report(GpoDiagnosticsProgress value)
        {
            lock (_reports)
            {
                _reports.Add(value);
            }

            onReport?.Invoke(value);
        }
    }

    /// <summary>A SYSVOL connection that only reads (not a write connection).</summary>
    private sealed class ReadOnlyConnection(ISysvolConnection inner) : ISysvolConnection
    {
        public string Server => inner.Server;

        public Task<SysvolFileResult> ReadGpoFileAsync(Guid gpoId, string relativePath, long maxBytes, CancellationToken cancellationToken) =>
            inner.ReadGpoFileAsync(gpoId, relativePath, maxBytes, cancellationToken);

        public Task<IReadOnlyList<SysvolEntry>?> ListPolicyDefinitionsAsync(int maxEntries, CancellationToken cancellationToken) =>
            inner.ListPolicyDefinitionsAsync(maxEntries, cancellationToken);

        public Task<SysvolFileResult> ReadPolicyDefinitionsFileAsync(string relativePath, long maxBytes, CancellationToken cancellationToken) =>
            inner.ReadPolicyDefinitionsFileAsync(relativePath, maxBytes, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
