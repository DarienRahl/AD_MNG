using AdMng.Core.PowerShell;
using AdMng.Testing;

namespace AdMng.PowerShell.Tests;

/// <summary>
/// The ways out of the window that the audit of R1 found by running them (PS-01, PS-03, ps-commands-5): each probe script is run
/// against the real engine and must be refused - in ConstrainedLanguage, and in the trusted mode where the finding applies there
/// too (the trusted mode is FullLanguage by the operator's choice, but it adds no command: no job, no parallel runspace, no module).
/// </summary>
public sealed class EscapeTests : IDisposable
{
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("admng-ps-escape-");

    public static TheoryData<PowerShellLanguageMode> BothModes => [PowerShellLanguageMode.Constrained, PowerShellLanguageMode.Full];

    public void Dispose() => _folder.Delete(recursive: true);

    /// <summary>
    /// The probe of PS-01 (audit/ps-engine/out-escape.txt): a "Constrained" window ran this script in FullLanguage, reached the
    /// window's directory session through <c>$using:Host</c> and deleted an object around the change plan.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task The_parallel_escape_of_the_audit_is_refused_and_touches_neither_the_directory_nor_the_disk(PowerShellLanguageMode mode)
    {
        var builder = new FakeDirectoryBuilder();
        builder.AddSampleCompany();
        var file = Path.Combine(_folder.FullName, "escape.txt");
        await using var session = await SessionAsync(mode, new FakeWindowContext(builder.Session));
        var script = """
            1 | ForEach-Object -Parallel {
              $f = [System.Reflection.BindingFlags]'NonPublic,Instance'
              $pd = ($using:Host).PrivateData.psobject.BaseObject
              $ctx = $pd.GetType().GetProperty('Context', $f).GetValue($pd)
              $r = $ctx.Reader
              $sess = $r.GetType().GetField('_inner', $f).GetValue($r)
              'language mode inside: ' + $ExecutionContext.SessionState.LanguageMode
              'reached: ' + $sess.GetType().FullName
              $sess.DeleteAsync('GUESTDN', $false, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
              'deleted GUESTDN directly, around the change plan'
              [System.IO.File]::WriteAllText('OUTFILE', 'written from a constrained window')
            }
            """.Replace("GUESTDN", builder.GuestDn, StringComparison.Ordinal).Replace("OUTFILE", file, StringComparison.Ordinal);

        var items = await TestSessions.RunAsync(session, script);

        Assert.Equal(string.Empty, TestSessions.Text(items));
        Assert.Contains("Parallel", TestSessions.Text(items, PowerShellStream.Error), StringComparison.Ordinal);
        Assert.NotNull(await builder.Session.GetAsync(builder.GuestDn, ["cn"], TestContext.Current.CancellationToken));
        Assert.False(File.Exists(file));
    }

    /// <summary>The job variant of PS-01: <c>-AsJob</c> gave a background job that outlived the run, Stop and the window.</summary>
    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task The_parallel_job_variant_is_refused(PowerShellLanguageMode mode)
    {
        await using var session = await SessionAsync(mode);

        var items = await TestSessions.RunAsync(
            session,
            "$j = 1 | ForEach-Object -Parallel { 'from a background job: ' + $ExecutionContext.SessionState.LanguageMode } -AsJob; $j | Wait-Job | Receive-Job; (Get-Job | Measure-Object).Count");

        Assert.Equal(string.Empty, TestSessions.Text(items));
        Assert.Contains("-AsJob", TestSessions.Text(items, PowerShellStream.Error), StringComparison.Ordinal);
        Assert.Equal(PowerShellRunState.Failed, TestSessions.End(items).State);
    }

    /// <summary>
    /// The message above comes from the text of the command; the protection is that the window's ForEach-Object has none of the
    /// parallel parameters, so a splatted one fails in parameter binding and the help shows no such parameter.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task The_windows_ForEach_Object_has_no_parallel_parameter(PowerShellLanguageMode mode)
    {
        await using var session = await SessionAsync(mode);

        var splatted = await TestSessions.RunAsync(session, "$p = @{ Parallel = { 'ran in parallel' }; AsJob = $true }; 1 | ForEach-Object @p");
        var syntax = await TestSessions.RunAsync(session, "Get-Command ForEach-Object -Syntax; (Get-Command ForEach-Object).Parameters.Keys -join ','");

        Assert.Equal(string.Empty, TestSessions.Text(splatted));
        Assert.True(TestSessions.End(splatted).HadErrors);
        foreach (var name in new[] { "Parallel", "AsJob", "ThrottleLimit", "TimeoutSeconds", "UseNewRunspace" })
        {
            Assert.DoesNotContain(name, TestSessions.Text(syntax), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A job the trusted mode creates with .NET (the engine's own cmdlet object, past every name the window knows) still ends with the
    /// command that started it (audit/ps-engine/out-more.txt: the file appeared 2.5 s after the run ended and Stop was pressed).
    /// </summary>
    [Fact]
    public async Task A_job_that_a_command_started_ends_with_the_command()
    {
        var file = Path.Combine(_folder.FullName, "job.txt");
        await using var session = await SessionAsync(PowerShellLanguageMode.Full);

        var items = await TestSessions.RunAsync(session, ParallelJobScript(file) + "; 'started'");
        await session.StopAsync();
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Equal("started\n", TestSessions.Text(items));
        Assert.False(File.Exists(file), "The job kept running after the command that started it had ended.");
    }

    /// <summary>The other half of out-more.txt: the job file appeared after the session was disposed.</summary>
    [Fact]
    public async Task A_job_ends_when_the_window_closes_while_its_command_waits()
    {
        var file = Path.Combine(_folder.FullName, "job-after-dispose.txt");
        var session = await SessionAsync(PowerShellLanguageMode.Full);
        await using (var run = session.RunAsync(ParallelJobScript(file) + "; Read-Host 'waiting'", TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            Assert.True(await run.MoveNextAsync());
            Assert.IsType<PowerShellPromptItem>(run.Current);

            await session.DisposeAsync();
            while (await run.MoveNextAsync())
            {
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.False(File.Exists(file), "The job kept running after the window's session was disposed.");
    }

    /// <summary>PS-03: <c>using module</c> loaded a script module past the Import-Module guard (out-more.txt).</summary>
    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task Using_module_of_a_script_module_is_refused_before_anything_runs(PowerShellLanguageMode mode)
    {
        var module = Path.Combine(_folder.FullName, "FakeMod");
        Directory.CreateDirectory(module);
        await File.WriteAllTextAsync(Path.Combine(module, "FakeMod.psd1"), "@{ ModuleVersion = '1.0'; RootModule = 'FakeMod.psm1'; FunctionsToExport = @('Get-FakeThing'); GUID = '11111111-2222-3333-4444-555555555555' }", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(module, "FakeMod.psm1"), "function Get-FakeThing { 'fake module loaded' }", TestContext.Current.CancellationToken);
        await using var session = await SessionAsync(mode);

        var items = await TestSessions.RunAsync(session, "using module '" + module + "'\nGet-FakeThing");
        var after = await TestSessions.RunAsync(session, "Get-Command Get-FakeThing -ErrorAction SilentlyContinue | Measure-Object | ForEach-Object Count");

        Assert.Equal(string.Empty, TestSessions.Text(items));
        Assert.Contains("using module", TestSessions.Text(items, PowerShellStream.Error), StringComparison.Ordinal);
        Assert.Equal(PowerShellRunState.Failed, TestSessions.End(items).State);
        Assert.Equal("0\n", TestSessions.Text(after));
    }

    /// <summary>A binary module loads .NET code: through <c>using module</c> it ran even in ConstrainedLanguage.</summary>
    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task Using_module_of_a_binary_module_and_using_assembly_are_refused(PowerShellLanguageMode mode)
    {
        var assembly = typeof(EscapeTests).Assembly.Location;
        await using var session = await SessionAsync(mode);

        var module = await TestSessions.RunAsync(session, "using module '" + assembly + "'\n'loaded'");
        var load = await TestSessions.RunAsync(session, "using assembly '" + assembly + "'\n'loaded'");
        var nested = await TestSessions.RunAsync(session, "$x = 'ran'; & { 'inner' }; Get-Command Wait-AdmTest -ErrorAction SilentlyContinue | Measure-Object | ForEach-Object Count");

        Assert.Equal(string.Empty, TestSessions.Text(module) + TestSessions.Text(load));
        Assert.Contains("using module", TestSessions.Text(module, PowerShellStream.Error), StringComparison.Ordinal);
        Assert.Contains("using assembly", TestSessions.Text(load, PowerShellStream.Error), StringComparison.Ordinal);
        Assert.Equal("inner\n0\n", TestSessions.Text(nested));
    }

    /// <summary>
    /// The background operator started the real Start-Job past the guard function (out-cl.txt: it failed only because no pwsh sat
    /// beside the engine). It is refused before the script runs, also inside a script block.
    /// </summary>
    [Theory]
    [InlineData(PowerShellLanguageMode.Constrained, "$x = 'ran'; Get-Date &")]
    [InlineData(PowerShellLanguageMode.Constrained, "$x = 'ran'; $j = Get-Date &; $j | Receive-Job -Wait")]
    [InlineData(PowerShellLanguageMode.Constrained, "$x = 'ran'; & { Get-Date & }")]
    [InlineData(PowerShellLanguageMode.Constrained, "$x = 'ran'; Get-Date && Get-Date &")]
    [InlineData(PowerShellLanguageMode.Full, "$x = 'ran'; Get-Date &")]
    [InlineData(PowerShellLanguageMode.Full, "$x = 'ran'; & { Get-Date & }")]
    public async Task The_background_operator_is_refused_before_anything_runs(PowerShellLanguageMode mode, string script)
    {
        await using var session = await SessionAsync(mode);

        var items = await TestSessions.RunAsync(session, script);
        var after = await TestSessions.RunAsync(session, "\"[$x]\"");

        Assert.Equal(string.Empty, TestSessions.Text(items));
        Assert.Contains("background operator", TestSessions.Text(items, PowerShellStream.Error), StringComparison.Ordinal);
        Assert.DoesNotContain("pwsh", TestSessions.Text(items, PowerShellStream.Error), StringComparison.Ordinal);
        Assert.Equal(PowerShellRunState.Failed, TestSessions.End(items).State);
        Assert.Equal("[]\n", TestSessions.Text(after));
    }

    /// <summary>The file writers of out-cl.txt (all of them wrote in a "read-only" window): none writes now.</summary>
    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task No_command_or_redirection_writes_a_file(PowerShellLanguageMode mode)
    {
        var dir = _folder.FullName;
        await using var session = await SessionAsync(mode);
        string[] scripts =
        [
            "'data' | Set-Content -Path '" + dir + "/setcontent.txt'",
            "'data' | Add-Content -Path '" + dir + "/addcontent.txt'",
            "'data' | Out-File '" + dir + "/outfile.txt'",
            "'data' > '" + dir + "/redirect.txt'",
            "'data' >> '" + dir + "/append.txt'",
            "Get-Item '" + dir + "/nothing' 2> '" + dir + "/errors.txt'",
            "'data' *> '" + dir + "/all.txt'",
            "Get-AdmVersion | Export-Csv '" + dir + "/export.csv'",
            "Get-AdmVersion | Export-Clixml '" + dir + "/export.xml'",
            "'data' | Tee-Object -FilePath '" + dir + "/tee.txt'",
            "New-Item -ItemType File -Path '" + dir + "/newitem.txt'",
            "Copy-Item -Path '" + typeof(EscapeTests).Assembly.Location + "' -Destination '" + dir + "/copied.dll'",
            "Microsoft.PowerShell.Utility\\Export-Csv -Path '" + dir + "/qualified.csv' -InputObject 1",
            "Microsoft.PowerShell.Utility\\Out-File -FilePath '" + dir + "/qualified.txt' -InputObject 1",
            "Microsoft.PowerShell.Management\\Set-Content -Path '" + dir + "/qualified-content.txt' -Value 1",
            "Start-Process -FilePath /bin/sh -ArgumentList '-c','echo x > " + dir + "/spawn.txt' -Wait",
        ];

        foreach (var script in scripts)
        {
            var items = await TestSessions.RunAsync(session, script);
            Assert.True(TestSessions.End(items).HadErrors, $"'{script}' ran without an error.");
        }

        Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
    }

    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task Output_can_still_be_discarded_and_merged(PowerShellLanguageMode mode)
    {
        await using var session = await SessionAsync(mode);

        var items = await TestSessions.RunAsync(session, "'x' > $null; Get-Item '/nothing-here' 2> $null; Get-Item '/nothing-here' 2>&1 | ForEach-Object { 'merged' }; 'after'");

        Assert.Equal("merged\nafter\n", TestSessions.Text(items));
        Assert.Equal(PowerShellRunState.Completed, TestSessions.End(items).State);
    }

    /// <summary>
    /// ps-commands-5: <c>Start-Transcript</c> without a path wrote the commands and the directory values to a file in the home
    /// folder. The README's promise: transcription only to a file the operator names.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task Start_Transcript_without_a_named_file_is_refused_and_records_nothing(PowerShellLanguageMode mode)
    {
        await using var session = await SessionAsync(mode);

        var bare = await TestSessions.RunAsync(session, "Start-Transcript; 'x'");
        var directory = await TestSessions.RunAsync(session, "Start-Transcript -OutputDirectory '" + _folder.FullName + "'");
        var stop = await TestSessions.RunAsync(session, "Stop-Transcript");

        Assert.Contains("Start-Transcript", TestSessions.Text(bare, PowerShellStream.Error), StringComparison.Ordinal);
        Assert.Contains("-Path", TestSessions.Text(bare, PowerShellStream.Error), StringComparison.Ordinal);
        Assert.DoesNotContain("Transcript started", TestSessions.Text(bare), StringComparison.Ordinal);
        Assert.Contains("OutputDirectory", TestSessions.Text(directory, PowerShellStream.Error), StringComparison.Ordinal);
        Assert.True(TestSessions.End(stop).HadErrors, "A transcript was running.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(_folder.FullName));
    }

    [Fact]
    public async Task Start_Transcript_to_a_named_file_records_the_session_there()
    {
        var file = Path.Combine(_folder.FullName, "transcript.txt");
        await using var session = await SessionAsync(PowerShellLanguageMode.Constrained);

        await TestSessions.RunAsync(session, "Start-Transcript -Path '" + file + "'");
        await TestSessions.RunAsync(session, "'in the transcript'");
        var stop = await TestSessions.RunAsync(session, "Stop-Transcript");

        Assert.False(TestSessions.End(stop).HadErrors);
        Assert.Contains("in the transcript", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    /// <summary>
    /// Commands that create runspaces, jobs, remote sessions or processes, load code, change the policy or write help files: not one is
    /// in the window. A few answer with a message (a function of the same name), the rest are not recognized.
    /// </summary>
    [Theory]
    [InlineData("Start-Job { 'ran' }")]
    [InlineData("sajb { 'ran' }")]
    [InlineData("Start-ThreadJob { 'ran' }")]
    [InlineData("Invoke-Command -ScriptBlock { 'ran' }")]
    [InlineData("Invoke-Command -ScriptBlock { 'ran' } -AsJob")]
    [InlineData("icm { 'ran' }")]
    [InlineData("New-PSSession")]
    [InlineData("Enter-PSSession -ComputerName localhost")]
    [InlineData("Enter-PSHostProcess -Id 1")]
    [InlineData("Debug-Runspace -Id 1")]
    [InlineData("Get-Runspace")]
    [InlineData("Get-Job")]
    [InlineData("Receive-Job -Id 1")]
    [InlineData("Set-ExecutionPolicy Bypass -Scope Process -Force")]
    [InlineData("Update-Help")]
    [InlineData("Save-Help -DestinationPath .")]
    [InlineData("Add-Type -TypeDefinition 'public class X {}'")]
    [InlineData("Register-EngineEvent -SourceIdentifier T -Action { 'ran' }")]
    [InlineData("Register-ObjectEvent -InputObject 1 -EventName x -Action { 'ran' }")]
    [InlineData("New-Module -ScriptBlock { 'ran' }")]
    [InlineData("Invoke-Expression \"'ran'\"")]
    [InlineData("iex \"'ran'\"")]
    [InlineData("Import-Module Microsoft.PowerShell.Utility")]
    [InlineData("& (Get-Command Import-Module) Microsoft.PowerShell.Utility")]
    [InlineData("Microsoft.PowerShell.Core\\Import-Module Foo")]
    [InlineData("Get-Credential")]
    [InlineData("Start-Process -FilePath echo -ArgumentList ran")]
    [InlineData("Out-GridView")]
    public async Task Commands_that_run_code_elsewhere_or_load_it_are_not_in_the_window(string script)
    {
        foreach (var mode in new[] { PowerShellLanguageMode.Constrained, PowerShellLanguageMode.Full })
        {
            await using var session = await SessionAsync(mode);

            var items = await TestSessions.RunAsync(session, script);

            Assert.DoesNotContain("ran", TestSessions.Text(items), StringComparison.Ordinal);
            Assert.True(TestSessions.End(items).HadErrors, $"'{script}' in {mode} ended without an error.");
        }
    }

    /// <summary>
    /// The host hands a script nothing: <c>$Host.PrivateData</c> is empty in both modes, so the reflection chain of the audit (from the
    /// host's private data to the window's context, its reader and the session behind it) finds nothing even in the trusted mode.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task The_host_hands_a_script_no_context(PowerShellLanguageMode mode)
    {
        await using var session = await SessionAsync(mode, new FakeWindowContext());

        var data = await TestSessions.RunAsync(session, "$null -eq $Host.PrivateData; $null -eq $Host.Runspace");
        var chain = await TestSessions.RunAsync(
            session,
            "$pd = $Host.PrivateData.psobject.BaseObject; $pd.GetType().GetProperty('Context', [System.Reflection.BindingFlags]'NonPublic,Instance').GetValue($pd).Reader");

        Assert.Equal("True\nTrue\n", TestSessions.Text(data));
        Assert.Equal(string.Empty, TestSessions.Text(chain));
        Assert.True(TestSessions.End(chain).HadErrors);
    }

    /// <summary>
    /// The other ways a constrained script could try to reach the engine, the session or the disk without a command: the runspace object
    /// and its language mode, the engine's provider and command intrinsics (they create files and script blocks), a method object of a
    /// type that is not a core type, reflection from the window's own replacement command to the engine's cmdlet it wraps, and an
    /// advanced function's <c>$PSCmdlet</c>. ConstrainedLanguage refuses each; none writes, none prints FullLanguage.
    /// </summary>
    [Theory]
    [InlineData("[runspace]::DefaultRunspace.SessionStateProxy.LanguageMode = 'FullLanguage'; $ExecutionContext.SessionState.LanguageMode")]
    [InlineData("function Set-Mode { [CmdletBinding()] param() $PSCmdlet.SessionState.LanguageMode = 'FullLanguage'; $ExecutionContext.SessionState.LanguageMode }; Set-Mode")]
    [InlineData("$ExecutionContext.InvokeProvider.Item.New('DIR', 'provider.txt', 'File', 'data')")]
    [InlineData("$w = $ExecutionContext.InvokeProvider.Content.GetWriter('DIR/writer.txt'); $w.Write(@('data')); $w.Close()")]
    [InlineData("& ($ExecutionContext.SessionState.InvokeCommand.NewScriptBlock(\"[System.IO.File]::WriteAllText('DIR/sb.txt', 'x'); 'FullLanguage'\"))")]
    [InlineData("(Get-Item 'DIR').psobject.Methods['CreateSubdirectory'].Invoke('made')")]
    [InlineData("(Get-Command ForEach-Object).ImplementingType.BaseType.GetField('EngineCmdlet', [System.Reflection.BindingFlags]'NonPublic,Static').GetValue($null)")]
    [InlineData("[type]::GetType('AdMng.PowerShell.Hosting.WindowRegistry, AdMng.PowerShell')")]
    public async Task A_constrained_script_reaches_neither_the_engine_nor_the_disk_by_other_paths(string script)
    {
        var builder = new FakeDirectoryBuilder();
        builder.AddSampleCompany();
        await using var session = await SessionAsync(PowerShellLanguageMode.Constrained, new FakeWindowContext(builder.Session));

        var items = await TestSessions.RunAsync(session, script.Replace("DIR", _folder.FullName, StringComparison.Ordinal));

        Assert.DoesNotContain("FullLanguage", TestSessions.Text(items), StringComparison.Ordinal);
        Assert.Contains("language mode", TestSessions.Text(items, PowerShellStream.Error), StringComparison.Ordinal);
        Assert.True(TestSessions.End(items).HadErrors);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_folder.FullName));
        Assert.Equal(PowerShellLanguageMode.Constrained, session.LanguageMode);
    }

    /// <summary>
    /// The bridge of the replacement commands binds the engine's cmdlet and its parameters, but leaves nothing behind in the caller's
    /// scope: a script block of ForEach-Object sees no variable that holds the engine's cmdlet (with it, <c>&amp; $command -Parallel</c>
    /// would be the audit's escape again), and the engine's cmdlets are not reachable by their module-qualified names.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task The_engine_cmdlet_behind_a_replacement_is_not_reachable(PowerShellLanguageMode mode)
    {
        await using var session = await SessionAsync(mode);

        var variables = await TestSessions.RunAsync(
            session,
            "1 | ForEach-Object { \"[$command][$parameters]\"; Get-Variable | Where-Object { $_.Value -is [System.Management.Automation.CommandInfo] } | ForEach-Object Name }");
        var qualified = await TestSessions.RunAsync(session, "Microsoft.PowerShell.Core\\ForEach-Object -InputObject 1 -Parallel { 'ran' }");

        Assert.Equal("[][]\n", TestSessions.Text(variables));
        Assert.Equal(string.Empty, TestSessions.Text(qualified));
        Assert.Contains("not recognized", TestSessions.Text(qualified, PowerShellStream.Error), StringComparison.Ordinal);
    }

    /// <summary>The window's ForEach-Object keeps ConstrainedLanguage: -MemberName cannot call a method of a type that is not a core type.</summary>
    [Fact]
    public async Task ForEach_Object_MemberName_keeps_the_language_mode()
    {
        await using var session = await SessionAsync(PowerShellLanguageMode.Constrained);

        var constrained = await TestSessions.RunAsync(session, "(Get-Item /) | ForEach-Object -MemberName GetDirectories | Measure-Object | ForEach-Object Count");
        var core = await TestSessions.RunAsync(session, "'a', 'bb' | ForEach-Object -MemberName ToUpper; 'a', 'bb' | ForEach-Object Length");
        await session.SetLanguageModeAsync(PowerShellLanguageMode.Full, TestContext.Current.CancellationToken);
        var trusted = await TestSessions.RunAsync(session, "((Get-Item /) | ForEach-Object -MemberName GetDirectories | Measure-Object).Count -gt 0");

        Assert.Equal("0\n", TestSessions.Text(constrained));
        Assert.Contains("language mode", TestSessions.Text(constrained, PowerShellStream.Error), StringComparison.Ordinal);
        Assert.Equal("A\nBB\n1\n2\n", TestSessions.Text(core));
        Assert.Equal("True\n", TestSessions.Text(trusted));
    }

    [Fact]
    public async Task New_Object_keeps_the_language_mode_and_has_no_com_objects()
    {
        await using var session = await SessionAsync(PowerShellLanguageMode.Constrained);

        var allowed = await TestSessions.RunAsync(session, "(New-Object PSObject -Property @{ a = 1 }).a");
        var refused = await TestSessions.RunAsync(session, "New-Object System.IO.FileInfo -ArgumentList 'x.txt'");
        var syntax = await TestSessions.RunAsync(session, "Get-Command New-Object -Syntax");

        Assert.Equal("1\n", TestSessions.Text(allowed));
        Assert.True(TestSessions.End(refused).HadErrors);
        Assert.DoesNotContain("ComObject", TestSessions.Text(syntax), StringComparison.Ordinal);
    }

    private static string ParallelJobScript(string file) =>
        "$null = & ([System.Management.Automation.CmdletInfo]::new('ForEach-Object', [Microsoft.PowerShell.Commands.ForEachObjectCommand])) -InputObject 1 -AsJob -Parallel { Start-Sleep -Milliseconds 1500; [System.IO.File]::WriteAllText('"
        + file + "', 'job') }";

    private static async Task<PowerShellSession> SessionAsync(PowerShellLanguageMode mode, FakeWindowContext? context = null)
    {
        var session = await TestSessions.CreateAsync(context);
        if (mode == PowerShellLanguageMode.Full)
        {
            await session.SetLanguageModeAsync(PowerShellLanguageMode.Full, TestContext.Current.CancellationToken);
        }

        return session;
    }
}
