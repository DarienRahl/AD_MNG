using AdMng.Core.PowerShell;
using AdMng.Presentation.PowerShell;

namespace AdMng.Presentation.Tests.PowerShell;

/// <summary>
/// The rules of <see cref="PowerShellPolicyReader"/> (track R, decision D7): which key wins, when the Windows PowerShell
/// policies count, how a config file is read and that a policy that sets "off" is a decision too. The registry and the
/// files are in memory, so every combination runs here; the rules taken from the engine's code are in
/// <see cref="PowerShellPolicyEngineRulesTests"/>.
/// </summary>
public sealed class PowerShellPolicyReaderTests
{
    private const PowerShellPolicyScope Computer = PowerShellPolicyScope.Computer;
    private const PowerShellPolicyScope User = PowerShellPolicyScope.User;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PowerShellPolicies Read(FakePowerShellPolicySource source) => new PowerShellPolicyReader(source).Read();

    [Fact]
    public void NothingIsSet_NothingIsRecorded()
    {
        var policies = Read(new FakePowerShellPolicySource());

        Assert.False(policies.IsRecording);
        Assert.Equal(PowerShellTranscriptionPolicy.NotSet, policies.Transcription);
        Assert.Equal(PowerShellScriptBlockLoggingPolicy.NotSet, policies.ScriptBlockLogging);
        Assert.Equal(PowerShellModuleLoggingPolicy.NotSet.Enabled, policies.ModuleLogging.Enabled);
        Assert.Null(policies.ModuleLogging.Origin);
        Assert.Null(policies.ExecutionPolicy.Policy);
        Assert.Equal(PowerShellLockdownState.None, policies.Lockdown);
    }

    [Theory]
    [InlineData(PowerShellLockdownState.None)]
    [InlineData(PowerShellLockdownState.Audit)]
    [InlineData(PowerShellLockdownState.Enforce)]
    public void TheLockdownIsPassedOn_AndIsNoRecording(PowerShellLockdownState state)
    {
        var policies = Read(new FakePowerShellPolicySource { Lockdown = state });

        Assert.Equal(state, policies.Lockdown);
        Assert.False(policies.IsRecording);
    }

    [Theory]
    [InlineData(PowerShellPolicyScope.Computer)]
    [InlineData(PowerShellPolicyScope.User)]
    public void TranscriptionOfPowerShell7_NamesTheHiveTheDirectoryAndTheHeader(PowerShellPolicyScope scope)
    {
        var source = new FakePowerShellPolicySource()
            .Core(scope, PowerShellPolicyKeys.TranscriptionKey, PowerShellPolicyKeys.EnableTranscripting, 1)
            .Core(scope, PowerShellPolicyKeys.TranscriptionKey, PowerShellPolicyKeys.EnableInvocationHeader, 1)
            .SetString(scope, PowerShellPolicyKeys.CoreRoot + @"\Transcription", PowerShellPolicyKeys.OutputDirectory, @"\\logs\transcripts");

        var transcription = Read(source).Transcription;

        Assert.True(transcription.Enabled);
        Assert.Equal(@"\\logs\transcripts", transcription.OutputDirectory);
        Assert.True(transcription.InvocationHeader);
        Assert.Equal(new PowerShellPolicyOrigin(PowerShellPolicyStore.PowerShellCore, scope), transcription.Origin);
    }

    [Fact]
    public void TranscriptionWithoutADirectory_HasNoDirectory_AndABlankOneCountsAsNone()
    {
        var source = new FakePowerShellPolicySource()
            .Core(Computer, PowerShellPolicyKeys.TranscriptionKey, PowerShellPolicyKeys.EnableTranscripting, 1)
            .SetString(Computer, PowerShellPolicyKeys.CoreRoot + @"\Transcription", PowerShellPolicyKeys.OutputDirectory, "   ");

        var transcription = Read(source).Transcription;

        Assert.True(transcription.Enabled);
        Assert.Null(transcription.OutputDirectory);
        Assert.False(transcription.InvocationHeader);
    }

    [Fact]
    public void TheComputerPolicy_BeatsTheUserPolicy_AlsoWhenItSaysOff()
    {
        var source = new FakePowerShellPolicySource()
            .Core(Computer, PowerShellPolicyKeys.TranscriptionKey, PowerShellPolicyKeys.EnableTranscripting, 0)
            .Core(User, PowerShellPolicyKeys.TranscriptionKey, PowerShellPolicyKeys.EnableTranscripting, 1);

        var transcription = Read(source).Transcription;

        Assert.False(transcription.Enabled);
        Assert.Equal(Computer, transcription.Origin!.Value.Scope);
    }

    [Fact]
    public void TheUserPolicy_IsUsedWhenTheComputerPolicyDoesNotSetIt()
    {
        var source = new FakePowerShellPolicySource()
            .Core(Computer, PowerShellPolicyKeys.ScriptBlockLoggingKey, PowerShellPolicyKeys.EnableScriptBlockLogging, 0)
            .Core(User, PowerShellPolicyKeys.TranscriptionKey, PowerShellPolicyKeys.EnableTranscripting, 1);

        var policies = Read(source);

        Assert.True(policies.Transcription.Enabled);
        Assert.Equal(User, policies.Transcription.Origin!.Value.Scope);
        Assert.False(policies.ScriptBlockLogging.Enabled);
    }

    [Fact]
    public void AValueOfAnotherTypeIsNotASetting()
    {
        // A string where a DWORD belongs reads as null in the source, so the policy is not set.
        var source = new FakePowerShellPolicySource()
            .SetString(Computer, PowerShellPolicyKeys.CoreRoot + @"\Transcription", PowerShellPolicyKeys.EnableTranscripting, "1");

        Assert.False(Read(source).Transcription.Enabled);
        Assert.Null(Read(source).Transcription.Origin);
    }

    [Fact]
    public void ScriptBlockLogging_AndItsInvocationLogging()
    {
        var source = new FakePowerShellPolicySource()
            .Core(User, PowerShellPolicyKeys.ScriptBlockLoggingKey, PowerShellPolicyKeys.EnableScriptBlockLogging, 1)
            .Core(User, PowerShellPolicyKeys.ScriptBlockLoggingKey, PowerShellPolicyKeys.EnableScriptBlockInvocationLogging, 1);

        var logging = Read(source).ScriptBlockLogging;

        Assert.True(logging.Enabled);
        Assert.True(logging.LogsInvocations);
        Assert.Equal(new PowerShellPolicyOrigin(PowerShellPolicyStore.PowerShellCore, User), logging.Origin);
        Assert.True(Read(source).IsRecording);
    }

    [Fact]
    public void ModuleLogging_ListsTheModulesByTheNamesOfTheValues()
    {
        var source = new FakePowerShellPolicySource()
            .Core(Computer, PowerShellPolicyKeys.ModuleLoggingKey, PowerShellPolicyKeys.EnableModuleLogging, 1)
            .SetNames(Computer, PowerShellPolicyKeys.CoreRoot + @"\ModuleLogging\ModuleNames", "ActiveDirectory", "*", " ");

        var logging = Read(source).ModuleLogging;

        Assert.True(logging.Enabled);
        Assert.Equal(["ActiveDirectory", "*"], logging.ModuleNames);
        Assert.Equal(Computer, logging.Origin!.Value.Scope);
    }

    [Theory]
    [InlineData(0, null, "Restricted")]
    [InlineData(0, "AllSigned", "Restricted")]
    [InlineData(1, "AllSigned", "AllSigned")]
    public void TheExecutionPolicy_ScriptsOffIsRestricted(int enableScripts, string? name, string expected)
    {
        var source = new FakePowerShellPolicySource().SetDword(Computer, PowerShellPolicyKeys.CoreRoot, PowerShellPolicyKeys.EnableScripts, enableScripts);
        if (name is not null)
        {
            source.SetString(Computer, PowerShellPolicyKeys.CoreRoot, PowerShellPolicyKeys.ExecutionPolicy, name);
        }

        var execution = Read(source).ExecutionPolicy;

        Assert.Equal(expected, execution.Policy);
        Assert.Equal(new PowerShellPolicyOrigin(PowerShellPolicyStore.PowerShellCore, Computer), execution.Origin);
        Assert.False(Read(source).IsRecording);
    }

    [Fact]
    public void ScriptsOnWithoutAPolicyName_DefinesNoExecutionPolicy()
    {
        // The engine's GetGroupPolicyValue returns the policy's name, which is none: no execution policy of this scope.
        var source = new FakePowerShellPolicySource().SetDword(Computer, PowerShellPolicyKeys.CoreRoot, PowerShellPolicyKeys.EnableScripts, 1);

        Assert.Equal(PowerShellExecutionPolicyPolicy.NotSet, Read(source).ExecutionPolicy);
    }

    [Fact]
    public void TheWindowsPowerShellPolicies_AreNotRead_WhileNoSwitchIsOn()
    {
        var source = new FakePowerShellPolicySource()
            .Windows(Computer, PowerShellPolicyKeys.TranscriptionKey, PowerShellPolicyKeys.EnableTranscripting, 1);

        var policies = Read(source);

        Assert.False(policies.Transcription.Enabled);
        Assert.DoesNotContain(source.Calls, call => call.Contains(PowerShellPolicyKeys.WindowsRoot, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void AnyNumberButZero_TurnsTheSwitchOn(int value)
    {
        // The engine compares the value with 0; it is no 0/1 switch of a policy class.
        var source = new FakePowerShellPolicySource()
            .Core(User, PowerShellPolicyKeys.ScriptBlockLoggingKey, PowerShellPolicyKeys.UseWindowsPowerShellPolicy, value)
            .Windows(User, PowerShellPolicyKeys.ScriptBlockLoggingKey, PowerShellPolicyKeys.EnableScriptBlockLogging, 1);

        var logging = Read(source).ScriptBlockLogging;

        Assert.True(logging.Enabled);
        Assert.Equal(new PowerShellPolicyOrigin(PowerShellPolicyStore.WindowsPowerShell, User), logging.Origin);
    }

    [Fact]
    public void TheSwitchOffByTheValueZero_IsOff()
    {
        var source = new FakePowerShellPolicySource()
            .Core(Computer, PowerShellPolicyKeys.TranscriptionKey, PowerShellPolicyKeys.UseWindowsPowerShellPolicy, 0)
            .Windows(Computer, PowerShellPolicyKeys.TranscriptionKey, PowerShellPolicyKeys.EnableTranscripting, 1);

        Assert.False(Read(source).Transcription.Enabled);
        Assert.Null(Read(source).Transcription.Origin);
    }

    [Theory]
    [InlineData("1")]
    [InlineData(1L)]
    public void ASwitchOfAnotherType_IsNotKnown_SoTheReadFails(object value)
    {
        // The engine casts the value to an int: a REG_SZ or a REG_QWORD makes it fail, so what it records cannot be said.
        var source = new FakePowerShellPolicySource().Core(Computer, PowerShellPolicyKeys.TranscriptionKey, PowerShellPolicyKeys.UseWindowsPowerShellPolicy, value);

        var ex = Assert.Throws<PowerShellPolicyUnreadableException>(() => Read(source));

        Assert.Equal(PowerShellPolicyPlace.SwitchValue, ex.Place);
        Assert.Equal(@"HKLM\Software\Policies\Microsoft\PowerShellCore\Transcription", ex.Location);
    }

    [Fact]
    public void ModuleNames_AlsoAMultiStringOrAStringValue_ButAValueOfAnotherTypeHidesTheSubKey()
    {
        var multi = new FakePowerShellPolicySource()
            .Core(Computer, PowerShellPolicyKeys.ModuleLoggingKey, PowerShellPolicyKeys.EnableModuleLogging, 1)
            .Core(Computer, PowerShellPolicyKeys.ModuleLoggingKey, PowerShellPolicyKeys.ModuleNamesKey, new[] { "ActiveDirectory", "AdMng" });
        var single = new FakePowerShellPolicySource()
            .Core(Computer, PowerShellPolicyKeys.ModuleLoggingKey, PowerShellPolicyKeys.EnableModuleLogging, 1)
            .Core(Computer, PowerShellPolicyKeys.ModuleLoggingKey, PowerShellPolicyKeys.ModuleNamesKey, "*");
        var number = new FakePowerShellPolicySource()
            .Core(Computer, PowerShellPolicyKeys.ModuleLoggingKey, PowerShellPolicyKeys.EnableModuleLogging, 1)
            .Core(Computer, PowerShellPolicyKeys.ModuleLoggingKey, PowerShellPolicyKeys.ModuleNamesKey, 7)
            .SetNames(Computer, PowerShellPolicyKeys.CoreRoot + @"\ModuleLogging\ModuleNames", "ActiveDirectory");

        Assert.Equal(["ActiveDirectory", "AdMng"], Read(multi).ModuleLogging.ModuleNames);
        Assert.Equal(["*"], Read(single).ModuleLogging.ModuleNames);
        Assert.Empty(Read(number).ModuleLogging.ModuleNames);
    }

    [Fact]
    public void AnEmptyKeyOfModuleNames_IsAValueOfThePolicy_SoItsHiveDecides()
    {
        var source = new FakePowerShellPolicySource()
            .SetNames(Computer, PowerShellPolicyKeys.CoreRoot + @"\ModuleLogging\ModuleNames")
            .Core(User, PowerShellPolicyKeys.ModuleLoggingKey, PowerShellPolicyKeys.EnableModuleLogging, 1);

        var logging = Read(source).ModuleLogging;

        Assert.False(logging.Enabled);
        Assert.Equal(Computer, logging.Origin!.Value.Scope);
    }

    [Fact]
    public void AConfigFile_SetsPoliciesWhereNoGpoDoes_ItsNamesIgnoreCase_AndItsCommentsAreSkipped()
    {
        var source = new FakePowerShellPolicySource().SetFile(User, """
            {
              // the user's file
              "powershellpolicies": {
                "Transcription": { "EnableTranscripting": true, "outputdirectory": "C:\\Temp\\ts", "EnableInvocationHeader": false, },
                "ScriptBlockLogging": { "EnableScriptBlockLogging": true },
                "ModuleLogging": { "EnableModuleLogging": true, "ModuleNames": ["Microsoft.PowerShell.*", ""] },
                "ScriptExecution": { "ExecutionPolicy": "RemoteSigned", "EnableScripts": true }
              }
            }
            """);

        var policies = Read(source);

        var origin = new PowerShellPolicyOrigin(PowerShellPolicyStore.ConfigFile, User);
        Assert.Equal(new PowerShellTranscriptionPolicy(true, @"C:\Temp\ts", false, origin), policies.Transcription);
        Assert.Equal(new PowerShellScriptBlockLoggingPolicy(true, false, origin), policies.ScriptBlockLogging);
        Assert.True(policies.ModuleLogging.Enabled);
        Assert.Equal(["Microsoft.PowerShell.*"], policies.ModuleLogging.ModuleNames);
        Assert.Equal("RemoteSigned", policies.ExecutionPolicy.Policy);
    }

    [Fact]
    public void TheAllUsersConfigFile_BeatsTheUsersFile_AndAFileThatSaysOffIsTheWayToTurnOffWhatTheUsersFileTurnedOn()
    {
        // The file next to the engine (AD Manager ships one that turns the recording off) wins over the user's file.
        var source = new FakePowerShellPolicySource()
            .SetFile(Computer, """{ "PowerShellPolicies": { "Transcription": { "EnableTranscripting": false }, "ScriptBlockLogging": { "EnableScriptBlockLogging": false } } }""")
            .SetFile(User, """{ "PowerShellPolicies": { "Transcription": { "EnableTranscripting": true }, "ScriptBlockLogging": { "EnableScriptBlockLogging": true } } }""");

        var policies = Read(source);

        Assert.False(policies.IsRecording);
        Assert.Equal(new PowerShellPolicyOrigin(PowerShellPolicyStore.ConfigFile, Computer), policies.Transcription.Origin);
    }

    [Fact]
    public void AGpo_BeatsEveryConfigFile_WhetherItSaysOnOrOff()
    {
        var on = new FakePowerShellPolicySource()
            .Core(Computer, PowerShellPolicyKeys.TranscriptionKey, PowerShellPolicyKeys.EnableTranscripting, 1)
            .SetFile(Computer, """{ "PowerShellPolicies": { "Transcription": { "EnableTranscripting": false } } }""");
        var off = new FakePowerShellPolicySource()
            .Core(User, PowerShellPolicyKeys.TranscriptionKey, PowerShellPolicyKeys.EnableTranscripting, 0)
            .SetFile(User, """{ "PowerShellPolicies": { "Transcription": { "EnableTranscripting": true } } }""");

        Assert.True(Read(on).Transcription.Enabled);
        Assert.Equal(PowerShellPolicyStore.PowerShellCore, Read(on).Transcription.Origin!.Value.Store);
        Assert.False(Read(off).Transcription.Enabled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("""{ "PowerShellPolicies": 5 }""")]
    [InlineData("""{ "PowerShellPolicies": { "Transcription": "yes" } }""")]
    [InlineData("""{ "PowerShellPolicies": { "Transcription": { "EnableTranscripting": "maybe" } } }""")]
    public void AFileTheEngineCannotUse_SetsNoPolicy(string text)
    {
        var policies = Read(new FakePowerShellPolicySource().SetFile(Computer, text).SetFile(User, text));

        Assert.False(policies.IsRecording);
        Assert.Null(policies.Transcription.Origin);
    }

    [Fact]
    public async Task ReadAsync_ReadsOffTheCallersThread_AndPassesTheFailureOn()
    {
        var source = new FakePowerShellPolicySource().Core(Computer, PowerShellPolicyKeys.TranscriptionKey, PowerShellPolicyKeys.EnableTranscripting, 1);
        var reader = new PowerShellPolicyReader(source);

        Assert.True((await reader.ReadAsync(Ct)).Transcription.Enabled);

        source.Failure = new UnauthorizedAccessException("denied");
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.ReadAsync(Ct));
        Assert.Equal("denied", ex.Message);
    }

    [Fact]
    public async Task ReadAsync_ACancelledToken_DoesNotRead()
    {
        var source = new FakePowerShellPolicySource();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PowerShellPolicyReader(source).ReadAsync(cancelled.Token));

        Assert.Empty(source.Calls);
    }

    [Fact]
    public void TheReader_ReadsOnlyTheKeysOfTheDocumentation()
    {
        var source = new FakePowerShellPolicySource()
            .Core(Computer, PowerShellPolicyKeys.TranscriptionKey, PowerShellPolicyKeys.UseWindowsPowerShellPolicy, 1);

        Read(source);

        // Every key it asks the registry for is a key of a policy, under one of the two roots; the Windows PowerShell keys only for the
        // policy whose switch is on (here: the transcription of HKLM).
        var keys = source.Calls.Where(call => !call.StartsWith("file", StringComparison.Ordinal) && call != "lockdown").ToList();
        Assert.NotEmpty(keys);
        Assert.All(keys, call => Assert.True(
            call.Contains(PowerShellPolicyKeys.CoreRoot, StringComparison.Ordinal) || call.Contains(PowerShellPolicyKeys.WindowsRoot, StringComparison.Ordinal), call));
        var windows = keys.Where(call => call.Contains(PowerShellPolicyKeys.WindowsRoot, StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(windows);
        Assert.All(windows, call => Assert.StartsWith(@"value Computer Software\Policies\Microsoft\Windows\PowerShell\Transcription ", call, StringComparison.Ordinal));
        Assert.Contains("file Computer", source.Calls);
        Assert.Contains("file User", source.Calls);
    }

    [Fact]
    public void NothingTheRecordsCarry_PrintsAPath()
    {
        var source = new FakePowerShellPolicySource()
            .Core(Computer, PowerShellPolicyKeys.TranscriptionKey, PowerShellPolicyKeys.EnableTranscripting, 1)
            .SetString(Computer, PowerShellPolicyKeys.CoreRoot + @"\Transcription", PowerShellPolicyKeys.OutputDirectory, @"\\fileserver\secret-share");

        // The record is data for the window, not for a log: the log copy of the note never names it (see the note tests), and
        // a record is never written anywhere by the reader.
        Assert.Contains("secret-share", Read(source).Transcription.OutputDirectory, StringComparison.Ordinal);
        Assert.DoesNotContain(source.Calls, call => call.StartsWith("write", StringComparison.Ordinal));
    }
}
