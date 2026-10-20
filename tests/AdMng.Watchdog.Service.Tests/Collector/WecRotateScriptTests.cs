using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using AdMng.Watchdog.Service.Attribution;
using AdMng.Watchdog.Service.Tests.Infrastructure;
using AdMng.Watchdog.Testing.Fixtures.OpenWec;

namespace AdMng.Watchdog.Service.Tests.Collector;

/// <summary>
/// <c>packaging/admng-watchdog/bin/wec-rotate</c> (stage B design 7.4, 12.4) run with bash against a temporary output folder
/// – the decisions of the script; the real unit under systemd is the lab script <c>tools/testlab/watchdog/wec-units-lab.sh</c>:
/// rotation of non-empty active files, deletion only of files the service's progress file lists complete (same identity
/// and size, idle 5 minutes, rotated in an earlier run), MAX_UNCONSUMED_DAYS and MAX_UNCONSUMED_MB, no progress file,
/// symbolic links and foreign names left alone, and the tailer's own progress file as input.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class WecRotateScriptTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly TestDataDirectory _root = new();

    public WecRotateScriptTests()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists("/bin/bash"), "wec-rotate is a bash script of the Linux package.");
        Output = Path.Combine(_root.Path, "out");
        Progress = Path.Combine(_root.Path, "run", "wec-progress.json");
        System.IO.Directory.CreateDirectory(Output);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Progress)!);
    }

    private string Output { get; }

    private string Progress { get; }

    public void Dispose() => _root.Dispose();

    private string Client(string name = "DC01@CONTOSO.TEST")
    {
        var path = Path.Combine(Output, name);
        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    private static string Write(string path, string content, DateTimeOffset modified)
    {
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
        return path;
    }

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>A progress entry for <paramref name="path"/> as the tailer writes it.</summary>
    private static WecProgressEntry Entry(string path, bool complete = true, long? offset = null)
    {
        var info = WecFiles.Describe(path, Path.GetFileName(path), Now)!;
        return new WecProgressEntry(Path.GetFileName(Path.GetDirectoryName(path)!), info.Name, info.Device, info.Inode, info.FirstLineSha256!,
            offset ?? info.Size, info.Size, complete);
    }

    private (int Exit, string Output) Run(IDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo("/bin/bash", [RepositoryFiles.Path("packaging/admng-watchdog/bin/wec-rotate")])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["WEC_OUT_DIR"] = Output;
        start.Environment["WEC_PROGRESS_FILE"] = Progress;
        start.Environment["WEC_ROTATE_NOW"] = Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[key] = value;
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    [Fact]
    public void ActiveFiles_AreRenamedInPlace_EmptyOnesAndLinksAreNot()
    {
        var dc1 = Client();
        var dc2 = Client("DC02@CONTOSO.TEST");
        var active = Write(Path.Combine(dc1, "security.jsonl"), "{\"meta\":{}}\n", Now.AddMinutes(-1));
        var inode = WecFiles.Describe(active, "security.jsonl", null)!.Inode;
        Write(Path.Combine(dc2, "security.jsonl"), string.Empty, Now.AddMinutes(-1));
        var target = Write(Path.Combine(_root.Path, "elsewhere.txt"), "nie ruszać\n", Now.AddDays(-40));
        var linked = Client("LINK@CONTOSO.TEST");
        File.CreateSymbolicLink(Path.Combine(linked, "security.jsonl"), target);
        File.CreateSymbolicLink(Path.Combine(linked, "security.jsonl.20260101T000000Z"), target);

        var (exit, output) = Run();

        Assert.Equal(0, exit);
        var rotated = Path.Combine(dc1, "security.jsonl." + Stamp(Now));
        Assert.Equal(inode, WecFiles.Describe(rotated, Path.GetFileName(rotated), Now)!.Inode);   // same inode: the tailer keeps its cursor
        Assert.False(File.Exists(active));
        Assert.True(File.Exists(Path.Combine(dc2, "security.jsonl")));
        Assert.True(File.Exists(target));
        Assert.True(File.Exists(Path.Combine(linked, "security.jsonl")));
        Assert.Contains("no progress file", output, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyCompleteIdleFilesOfEarlierRuns_AreDeleted()
    {
        var dc = Client();
        var consumed = Write(Path.Combine(dc, "security.jsonl." + Stamp(Now.AddHours(-2))), "a\nb\n", Now.AddHours(-2));
        var partly = Write(Path.Combine(dc, "security.jsonl." + Stamp(Now.AddHours(-1))), "c\nd\n", Now.AddHours(-1));
        var recent = Write(Path.Combine(dc, "security.jsonl." + Stamp(Now.AddMinutes(-20))), "e\n", Now.AddMinutes(-2));   // written 2 min ago
        var grown = Write(Path.Combine(dc, "security.jsonl." + Stamp(Now.AddMinutes(-90))), "f\n", Now.AddHours(-1));
        var active = Write(Path.Combine(dc, "security.jsonl"), "g\n", Now.AddHours(-1));
        var entries = new[] { Entry(consumed), Entry(partly, complete: false, offset: 2), Entry(recent), Entry(grown), Entry(active, complete: true) };
        File.AppendAllText(grown, "f2\n");                       // appended after the progress was written
        File.SetLastWriteTimeUtc(grown, Now.AddHours(-1).UtcDateTime);
        WecProgressFile.Write(Progress, entries, Now);

        var (exit, _) = Run();

        Assert.Equal(0, exit);
        Assert.False(File.Exists(consumed));
        Assert.True(File.Exists(partly));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(grown));
        Assert.True(File.Exists(Path.Combine(dc, "security.jsonl." + Stamp(Now))));   // the active file: rotated, kept this run
    }

    [Fact]
    public void AFileWithAnotherFirstLine_IsNotTheConfirmedOne()
    {
        var dc = Client();
        var file = Write(Path.Combine(dc, "security.jsonl." + Stamp(Now.AddHours(-2))), "a\nb\n", Now.AddHours(-2));
        var entry = Entry(file) with { FirstLineSha256 = new string('0', 64) };
        WecProgressFile.Write(Progress, [entry], Now);

        Run();

        Assert.True(File.Exists(file));
    }

    [Fact]
    public void UnconsumedFiles_AreDeletedAfterMaxUnconsumedDays_WithAWarning()
    {
        var dc = Client();
        var old = Write(Path.Combine(dc, "security.jsonl." + Stamp(Now.AddDays(-8))), "a\n", Now.AddDays(-8));
        var young = Write(Path.Combine(dc, "security.jsonl." + Stamp(Now.AddDays(-6))), "b\n", Now.AddDays(-6));

        var (_, output) = Run();
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(young));
        Assert.Contains("<4>wec-rotate: deleted DC01@CONTOSO.TEST/security.jsonl." + Stamp(Now.AddDays(-8)) + " unread: older than MAX_UNCONSUMED_DAYS=7", output,
            StringComparison.Ordinal);

        Run(new Dictionary<string, string> { ["MAX_UNCONSUMED_DAYS"] = "5" });
        Assert.False(File.Exists(young));

        var tooLong = Write(Path.Combine(dc, "security.jsonl." + Stamp(Now.AddDays(-31))), "c\n", Now.AddDays(-31));
        var (_, capped) = Run(new Dictionary<string, string> { ["MAX_UNCONSUMED_DAYS"] = "90" });
        Assert.False(File.Exists(tooLong));
        Assert.Contains("above the maximum of 30", capped, StringComparison.Ordinal);
    }

    [Fact]
    public void OverMaxUnconsumedMb_TheOldestFilesOfAllDcsGoFirst()
    {
        var megabyte = new string('x', 1024 * 1024 - 1) + "\n";
        var a = Write(Path.Combine(Client(), "security.jsonl." + Stamp(Now.AddHours(-5))), string.Concat(Enumerable.Repeat(megabyte, 40)), Now.AddHours(-5));
        var b = Write(Path.Combine(Client("DC02@CONTOSO.TEST"), "security.jsonl." + Stamp(Now.AddHours(-4))), string.Concat(Enumerable.Repeat(megabyte, 40)), Now.AddHours(-4));
        var c = Write(Path.Combine(Client(), "security.jsonl." + Stamp(Now.AddHours(-3))), string.Concat(Enumerable.Repeat(megabyte, 40)), Now.AddHours(-3));

        var (_, output) = Run(new Dictionary<string, string> { ["MAX_UNCONSUMED_MB"] = "64" });

        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.True(File.Exists(c));
        Assert.Contains("exceed MAX_UNCONSUMED_MB=64", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTailersProgress_LetsTheRotationDeleteWhatItConsumed()
    {
        await using var f = new TailerFixture();
        f.Append(f.Active(TailerFixture.Client1) + ".20261014T060000Z", TailerFixture.Line(f.Change(TailerFixture.Dc1, 1)));
        f.Append(f.Active(TailerFixture.Client1), TailerFixture.Line(f.Change(TailerFixture.Dc1, 2)));
        File.SetLastWriteTimeUtc(f.Active(TailerFixture.Client1) + ".20261014T060000Z", Now.AddHours(-1).UtcDateTime);
        await f.RunUntilIdleAsync(f.NewJob(), TestContext.Current.CancellationToken);

        var start = new ProcessStartInfo("/bin/bash", [RepositoryFiles.Path("packaging/admng-watchdog/bin/wec-rotate")]) { RedirectStandardOutput = true };
        start.Environment["WEC_OUT_DIR"] = f.Output;
        start.Environment["WEC_PROGRESS_FILE"] = f.ProgressFile;
        start.Environment["WEC_ROTATE_NOW"] = Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        using (var process = Process.Start(start)!)
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
        }

        Assert.False(File.Exists(f.Active(TailerFixture.Client1) + ".20261014T060000Z"));   // consumed and complete: deleted
        Assert.True(File.Exists(f.Active(TailerFixture.Client1) + "." + Stamp(Now)));         // the active one: rotated now, kept
        Assert.Equal(WecRawJson.ClientDirectory(TailerFixture.Client1), Path.GetFileName(f.Directory(TailerFixture.Client1)));
    }
}
