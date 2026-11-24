using System.Globalization;
using AdMng.Presentation.Localization;
using AdMng.Presentation.PowerShell;

namespace AdMng.Presentation.Tests.PowerShell;

/// <summary>
/// The rules of the engine the reader mirrors (audit W-02: <c>System.Management.Automation.dll</c> 7.6, <c>Utils.GetPolicySetting</c>,
/// decompiled): "Use Windows PowerShell Policy setting" is a value of each policy's own PowerShell 7 key in each hive, the first hive
/// whose key sets any value of a policy decides the whole policy, a switch is the DWORD 0 or 1, the config files come after every
/// hive and the first file that has the section decides it. Scenarios A-D are the auditor's: the reader before the fix said
/// "nothing is recorded" for A and B while the engine records, named the wrong directory for C and a transcription that does not
/// happen for D.
/// </summary>
public sealed class PowerShellPolicyEngineRulesTests
{
    private const PowerShellPolicyScope Computer = PowerShellPolicyScope.Computer;
    private const PowerShellPolicyScope User = PowerShellPolicyScope.User;

    private static readonly string CoreTranscription = PowerShellPolicyKeys.Sub(PowerShellPolicyKeys.CoreRoot, PowerShellPolicyKeys.TranscriptionKey);
    private static readonly string WindowsTranscription = PowerShellPolicyKeys.Sub(PowerShellPolicyKeys.WindowsRoot, PowerShellPolicyKeys.TranscriptionKey);
    private static readonly string CoreScriptBlockLogging = PowerShellPolicyKeys.Sub(PowerShellPolicyKeys.CoreRoot, PowerShellPolicyKeys.ScriptBlockLoggingKey);

    /// <summary>
    /// The program's own all-users file as it ships (src/AdMng.App/powershell.config.json): transcription and module logging off, script
    /// block logging not set (decision D-B) - read from the repository, so that these scenarios follow the file.
    /// </summary>
    private static readonly string ShippedFile = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "AdMng.App", "powershell.config.json"));

    /// <summary>A user's own powershell.config.json that turns on all three recordings.</summary>
    private const string UserFileThatRecords =
        """{ "PowerShellPolicies": { "Transcription": { "EnableTranscripting": true }, "ScriptBlockLogging": { "EnableScriptBlockLogging": true }, "ModuleLogging": { "EnableModuleLogging": true } } }""";

    private static PowerShellPolicies Read(FakePowerShellPolicySource source) => new PowerShellPolicyReader(source).Read();

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AdMng.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }

    [Fact]
    public void TheProgramsFileAlone_DecidesTheTranscriptionAndTheModuleLogging_AndLeavesScriptBlockLoggingNotSet()
    {
        var policies = Read(new FakePowerShellPolicySource().SetFile(Computer, ShippedFile));

        Assert.False(policies.IsRecording);
        Assert.Equal(new PowerShellTranscriptionPolicy(false, null, false, new(PowerShellPolicyStore.ConfigFile, Computer)), policies.Transcription);
        Assert.False(policies.ModuleLogging.Enabled);
        Assert.Equal(new PowerShellPolicyOrigin(PowerShellPolicyStore.ConfigFile, Computer), policies.ModuleLogging.Origin);

        // D-B: no policy and no file sets it - the engine's default, which still logs the script blocks it finds suspicious
        // (ScriptBlockLoggingTests). The window shows no note then: the note says what the policies record and makes no sentence
        // of that default (docs/dev/r1-powershell-window.md 12.12); the guide, chapter 8, says it.
        Assert.Equal(PowerShellScriptBlockLoggingPolicy.NotSet, policies.ScriptBlockLogging);
        Assert.Null(PowerShellPolicyNotes.For(policies, new ResxLocalizer(CultureInfo.GetCultureInfo("en-US"))));
    }

    [Fact]
    public void AUsersFile_TurnsOnScriptBlockLoggingAsInAPlainPwsh_ButNotTheTranscriptionOrTheModuleLogging()
    {
        var policies = Read(new FakePowerShellPolicySource().SetFile(Computer, ShippedFile).SetFile(User, UserFileThatRecords));

        Assert.True(policies.IsRecording);
        Assert.Equal(new PowerShellScriptBlockLoggingPolicy(true, false, new(PowerShellPolicyStore.ConfigFile, User)), policies.ScriptBlockLogging);
        Assert.False(policies.Transcription.Enabled);
        Assert.Equal(new PowerShellPolicyOrigin(PowerShellPolicyStore.ConfigFile, Computer), policies.Transcription.Origin);
        Assert.False(policies.ModuleLogging.Enabled);
        Assert.Equal(new PowerShellPolicyOrigin(PowerShellPolicyStore.ConfigFile, Computer), policies.ModuleLogging.Origin);
    }

    [Fact]
    public void AGpoThatTurnsScriptBlockLoggingOff_DecidesItOverTheUsersFile()
    {
        var source = new FakePowerShellPolicySource()
            .Core(Computer, PowerShellPolicyKeys.ScriptBlockLoggingKey, PowerShellPolicyKeys.EnableScriptBlockLogging, 0)
            .SetFile(Computer, ShippedFile)
            .SetFile(User, UserFileThatRecords);

        var logging = Read(source).ScriptBlockLogging;

        Assert.Equal(new PowerShellScriptBlockLoggingPolicy(false, false, new(PowerShellPolicyStore.PowerShellCore, Computer)), logging);
    }

    [Fact]
    public void A_TheSwitchOfTheExecutionPolicy_DoesNotHideThePowerShell7TranscriptionAndScriptBlockLogging()
    {
        // "Turn on Script Execution" with "Use Windows PowerShell Policy setting": the switch is in the root key, the one of that policy.
        var source = new FakePowerShellPolicySource()
            .SetDword(Computer, PowerShellPolicyKeys.CoreRoot, PowerShellPolicyKeys.UseWindowsPowerShellPolicy, 1)
            .Core(Computer, PowerShellPolicyKeys.TranscriptionKey, PowerShellPolicyKeys.EnableTranscripting, 1)
            .SetString(Computer, CoreTranscription, PowerShellPolicyKeys.OutputDirectory, @"D:\PSLogs")
            .Core(Computer, PowerShellPolicyKeys.ScriptBlockLoggingKey, PowerShellPolicyKeys.EnableScriptBlockLogging, 1)
            .SetFile(Computer, ShippedFile);

        var policies = Read(source);

        Assert.True(policies.IsRecording);
        Assert.Equal(new PowerShellTranscriptionPolicy(true, @"D:\PSLogs", false, new(PowerShellPolicyStore.PowerShellCore, Computer)), policies.Transcription);
        Assert.Equal(new PowerShellScriptBlockLoggingPolicy(true, false, new(PowerShellPolicyStore.PowerShellCore, Computer)), policies.ScriptBlockLogging);
    }

    [Fact]
    public void B_TheSwitchOfScriptBlockLogging_ReadsTheWindowsPowerShellScriptBlockLogging_AndOnlyIt()
    {
        var source = new FakePowerShellPolicySource()
            .SetDword(Computer, CoreScriptBlockLogging, PowerShellPolicyKeys.UseWindowsPowerShellPolicy, 1)
            .Windows(Computer, PowerShellPolicyKeys.ScriptBlockLoggingKey, PowerShellPolicyKeys.EnableScriptBlockLogging, 1)
            .Windows(Computer, PowerShellPolicyKeys.TranscriptionKey, PowerShellPolicyKeys.EnableTranscripting, 1)
            .SetFile(Computer, ShippedFile);

        var policies = Read(source);

        Assert.True(policies.IsRecording);
        Assert.Equal(new PowerShellScriptBlockLoggingPolicy(true, false, new(PowerShellPolicyStore.WindowsPowerShell, Computer)), policies.ScriptBlockLogging);

        // The transcription has no switch of its own: the Windows PowerShell one does not count, the program's file decides it.
        Assert.False(policies.Transcription.Enabled);
        Assert.Equal(new PowerShellPolicyOrigin(PowerShellPolicyStore.ConfigFile, Computer), policies.Transcription.Origin);
    }

    [Fact]
    public void C_WhatTheGpoEditorWritesWithTheCheckBox_IsTheWindowsPowerShellTranscription_WithItsDirectory()
    {
        // "Turn on PowerShell Transcription" (PowerShell Core) enabled with "Use Windows PowerShell Policy setting" ticked.
        var source = new FakePowerShellPolicySource()
            .SetDword(Computer, CoreTranscription, PowerShellPolicyKeys.EnableTranscripting, 1)
            .SetDword(Computer, CoreTranscription, PowerShellPolicyKeys.UseWindowsPowerShellPolicy, 1)
            .SetDword(Computer, WindowsTranscription, PowerShellPolicyKeys.EnableTranscripting, 1)
            .SetString(Computer, WindowsTranscription, PowerShellPolicyKeys.OutputDirectory, @"\\srv\transcripts$");

        var transcription = Read(source).Transcription;

        Assert.True(transcription.Enabled);
        Assert.Equal(@"\\srv\transcripts$", transcription.OutputDirectory);
        Assert.Equal(new PowerShellPolicyOrigin(PowerShellPolicyStore.WindowsPowerShell, Computer), transcription.Origin);
    }

    [Fact]
    public void D_TheCheckBoxWithoutAWindowsPowerShellPolicy_SetsNothingInThatHive()
    {
        var source = new FakePowerShellPolicySource()
            .SetDword(Computer, CoreTranscription, PowerShellPolicyKeys.EnableTranscripting, 1)
            .SetDword(Computer, CoreTranscription, PowerShellPolicyKeys.UseWindowsPowerShellPolicy, 1);

        var policies = Read(source);

        Assert.False(policies.IsRecording);
        Assert.Equal(PowerShellTranscriptionPolicy.NotSet, policies.Transcription);
    }

    [Fact]
    public void TheSwitchOfOneHive_DoesNotTurnTheOtherHiveToWindowsPowerShell()
    {
        var source = new FakePowerShellPolicySource()
            .SetDword(User, CoreTranscription, PowerShellPolicyKeys.UseWindowsPowerShellPolicy, 1)
            .Core(Computer, PowerShellPolicyKeys.TranscriptionKey, PowerShellPolicyKeys.EnableTranscripting, 1)
            .Windows(Computer, PowerShellPolicyKeys.TranscriptionKey, PowerShellPolicyKeys.EnableTranscripting, 0);

        var transcription = Read(source).Transcription;

        Assert.True(transcription.Enabled);
        Assert.Equal(new PowerShellPolicyOrigin(PowerShellPolicyStore.PowerShellCore, Computer), transcription.Origin);
    }

    [Fact]
    public void TheFirstHiveThatSetsAnyValue_DecidesTheWholePolicy_TheHivesAreNotMixedValueByValue()
    {
        // The computer policy names only a directory; the user policy turns the transcription on. The engine takes the computer's.
        var source = new FakePowerShellPolicySource()
            .SetString(Computer, CoreTranscription, PowerShellPolicyKeys.OutputDirectory, @"\\srv\transcripts")
            .Core(User, PowerShellPolicyKeys.TranscriptionKey, PowerShellPolicyKeys.EnableTranscripting, 1);

        var transcription = Read(source).Transcription;

        Assert.False(transcription.Enabled);
        Assert.Equal(new PowerShellPolicyOrigin(PowerShellPolicyStore.PowerShellCore, Computer), transcription.Origin);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    public void ASwitchIsTheDwordZeroOrOne_AnotherNumberSetsNothing(int value)
    {
        var source = new FakePowerShellPolicySource()
            .Core(Computer, PowerShellPolicyKeys.ScriptBlockLoggingKey, PowerShellPolicyKeys.EnableScriptBlockLogging, value)
            .Core(User, PowerShellPolicyKeys.ScriptBlockLoggingKey, PowerShellPolicyKeys.EnableScriptBlockLogging, 1);

        var logging = Read(source).ScriptBlockLogging;

        // The computer's key sets nothing the engine takes, so the user's decides.
        Assert.True(logging.Enabled);
        Assert.Equal(User, logging.Origin!.Value.Scope);
    }

    [Fact]
    public void TheMachinesExecutionPolicy_IsTheComputerKeyThenTheAllUsersFile_BeforeTheUsersKey()
    {
        var source = new FakePowerShellPolicySource()
            .SetFile(Computer, """{ "PowerShellPolicies": { "ScriptExecution": { "EnableScripts": true, "ExecutionPolicy": "AllSigned" } } }""")
            .SetDword(User, PowerShellPolicyKeys.CoreRoot, PowerShellPolicyKeys.EnableScripts, 1)
            .SetString(User, PowerShellPolicyKeys.CoreRoot, PowerShellPolicyKeys.ExecutionPolicy, "RemoteSigned");

        var execution = Read(source).ExecutionPolicy;

        Assert.Equal("AllSigned", execution.Policy);
        Assert.Equal(new PowerShellPolicyOrigin(PowerShellPolicyStore.ConfigFile, Computer), execution.Origin);
    }

    [Fact]
    public void AnExecutionPolicyWithoutTheSwitchOfScripts_DefinesNone_SoTheUserPolicyApplies()
    {
        var source = new FakePowerShellPolicySource()
            .SetString(Computer, PowerShellPolicyKeys.CoreRoot, PowerShellPolicyKeys.ExecutionPolicy, "Unrestricted")
            .SetDword(User, PowerShellPolicyKeys.CoreRoot, PowerShellPolicyKeys.EnableScripts, 1)
            .SetString(User, PowerShellPolicyKeys.CoreRoot, PowerShellPolicyKeys.ExecutionPolicy, "RemoteSigned");

        var execution = Read(source).ExecutionPolicy;

        Assert.Equal("RemoteSigned", execution.Policy);
        Assert.Equal(new PowerShellPolicyOrigin(PowerShellPolicyStore.PowerShellCore, User), execution.Origin);
    }

    [Fact]
    public void AnEmptySectionOfTheAllUsersFile_DecidesThePolicy_TheUsersFileIsNotRead()
    {
        var source = new FakePowerShellPolicySource()
            .SetFile(Computer, """{ "PowerShellPolicies": { "Transcription": { } } }""")
            .SetFile(User, """{ "PowerShellPolicies": { "Transcription": { "EnableTranscripting": true } } }""");

        var transcription = Read(source).Transcription;

        Assert.False(transcription.Enabled);
        Assert.Equal(new PowerShellPolicyOrigin(PowerShellPolicyStore.ConfigFile, Computer), transcription.Origin);
    }

    [Theory]
    [InlineData("""{ "PowerShellPolicies": { "Transcription": { "EnableTranscripting": true }, "ScriptBlockLogging": "yes" } }""")]
    [InlineData("""{ "PowerShellPolicies": { "Transcription": { "EnableTranscripting": true }, "UpdatableHelp": 5 } }""")]
    [InlineData("""{ "PowerShellPolicies": { "Transcription": { "EnableTranscripting": true }, "ModuleLogging": { "ModuleNames": "ActiveDirectory" } } }""")]
    [InlineData("""{ "PowerShellPolicies": { "Transcription": { "EnableTranscripting": true, "OutputDirectory": { } } } }""")]
    public void AValueTheEngineCannotConvert_AnywhereInTheFile_MakesTheWholeFileSetNothing(string text)
    {
        var policies = Read(new FakePowerShellPolicySource().SetFile(User, text));

        Assert.Equal(PowerShellTranscriptionPolicy.NotSet, policies.Transcription);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("2", true)]
    [InlineData("0.5", true)]
    [InlineData("\"True\"", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("\"false\"", false)]
    public void ASwitchInAFile_IsWhatTheEngineConvertsToABoolean(string value, bool expected)
    {
        var source = new FakePowerShellPolicySource().SetFile(User, $$"""{ "PowerShellPolicies": { "ScriptBlockLogging": { "EnableScriptBlockLogging": {{value}} } } }""");

        var logging = Read(source).ScriptBlockLogging;

        Assert.Equal(expected, logging.Enabled);
        Assert.Equal(new PowerShellPolicyOrigin(PowerShellPolicyStore.ConfigFile, User), logging.Origin);
    }

    [Theory]
    [InlineData("\"1\"")]
    [InlineData("\"yes\"")]
    [InlineData("[true]")]
    public void ASwitchTheEngineCannotConvert_MakesTheFileSetNothing(string value)
    {
        var source = new FakePowerShellPolicySource().SetFile(User, $$"""{ "PowerShellPolicies": { "ScriptBlockLogging": { "EnableScriptBlockLogging": {{value}} } } }""");

        Assert.Equal(PowerShellScriptBlockLoggingPolicy.NotSet, Read(source).ScriptBlockLogging);
    }

    [Fact]
    public void TheLastValueOfTheSameNameInAFile_Wins()
    {
        var source = new FakePowerShellPolicySource().SetFile(User, """{ "PowerShellPolicies": { "Transcription": { "EnableTranscripting": true, "enabletranscripting": false } } }""");

        Assert.False(Read(source).Transcription.Enabled);
    }
}
