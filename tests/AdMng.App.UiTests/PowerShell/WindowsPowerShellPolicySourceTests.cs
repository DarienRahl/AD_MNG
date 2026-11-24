using System.IO;
using AdMng.App.PowerShell;
using AdMng.Presentation.PowerShell;
using Microsoft.Win32;

namespace AdMng.App.UiTests.PowerShell;

/// <summary>
/// The Windows source of the policy note (audit W-01) against the real registry: both hives are a throwaway key of the current user
/// (<c>HKCU\Software\AdMng.Tests-&lt;guid&gt;\HKLM</c> and <c>…\HKCU</c>), written by the test and deleted after it. The reader before
/// the fix asked <c>GetValueKind</c>, which throws for an absent value, so a policy key with one value of its policy made the window
/// say "could not be read" on every workstation with a PowerShell 7 policy; the CI runner has no such key, so nothing showed it.
/// </summary>
public sealed class WindowsPowerShellPolicySourceTests : IDisposable
{
    private static readonly string CoreTranscription = PowerShellPolicyKeys.Sub(PowerShellPolicyKeys.CoreRoot, PowerShellPolicyKeys.TranscriptionKey);
    private static readonly string WindowsTranscription = PowerShellPolicyKeys.Sub(PowerShellPolicyKeys.WindowsRoot, PowerShellPolicyKeys.TranscriptionKey);
    private static readonly string CoreModuleNames = PowerShellPolicyKeys.Sub(
        PowerShellPolicyKeys.Sub(PowerShellPolicyKeys.CoreRoot, PowerShellPolicyKeys.ModuleLoggingKey), PowerShellPolicyKeys.ModuleNamesKey);

    private readonly string _root = @"Software\AdMng.Tests-" + Guid.NewGuid().ToString("N");
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "admng-policy-" + Guid.NewGuid().ToString("N"));
    private readonly RegistryKey _computer;
    private readonly RegistryKey _user;

    public WindowsPowerShellPolicySourceTests()
    {
        _computer = Registry.CurrentUser.CreateSubKey(_root + @"\HKLM");
        _user = Registry.CurrentUser.CreateSubKey(_root + @"\HKCU");
        System.IO.Directory.CreateDirectory(_folder);
    }

    private string EngineFile => Path.Combine(_folder, "engine", WindowsPowerShellPolicySource.ConfigFileName);

    private string UserFile => Path.Combine(_folder, "user", WindowsPowerShellPolicySource.ConfigFileName);

    public void Dispose()
    {
        _computer.Dispose();
        _user.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
        System.IO.Directory.Delete(_folder, recursive: true);
    }

    private WindowsPowerShellPolicySource Source() => new(_computer, _user, EngineFile, UserFile);

    private static void Write(RegistryKey hive, string subKey, string name, object value, RegistryValueKind kind)
    {
        using var key = hive.CreateSubKey(subKey);
        key.SetValue(name, value, kind);
    }

    [Fact]
    public void AKeyWithoutTheValue_IsNotSet_NeverAnException()
    {
        Write(_computer, CoreTranscription, PowerShellPolicyKeys.EnableTranscripting, 1, RegistryValueKind.DWord);
        var source = Source();

        Assert.Equal(1, source.GetValue(PowerShellPolicyScope.Computer, CoreTranscription, PowerShellPolicyKeys.EnableTranscripting));
        Assert.Null(source.GetValue(PowerShellPolicyScope.Computer, CoreTranscription, PowerShellPolicyKeys.OutputDirectory));
        Assert.Null(source.GetValue(PowerShellPolicyScope.Computer, CoreTranscription, PowerShellPolicyKeys.UseWindowsPowerShellPolicy));
        Assert.Null(source.GetValue(PowerShellPolicyScope.User, CoreTranscription, PowerShellPolicyKeys.EnableTranscripting));
        Assert.Null(source.GetValueNames(PowerShellPolicyScope.Computer, CoreModuleNames));

        // The whole read works: the note says what this key turns on.
        var policies = new PowerShellPolicyReader(source).Read();
        Assert.Equal(new PowerShellTranscriptionPolicy(true, null, false, new(PowerShellPolicyStore.PowerShellCore, PowerShellPolicyScope.Computer)), policies.Transcription);
    }

    [Fact]
    public void AValueOfAnotherType_ComesAsTheRegistryHoldsIt_AndSetsNoSwitch()
    {
        Write(_computer, CoreTranscription, PowerShellPolicyKeys.EnableTranscripting, "1", RegistryValueKind.String);
        Write(_computer, CoreTranscription, PowerShellPolicyKeys.EnableInvocationHeader, 1L, RegistryValueKind.QWord);
        var source = Source();

        Assert.Equal("1", source.GetValue(PowerShellPolicyScope.Computer, CoreTranscription, PowerShellPolicyKeys.EnableTranscripting));
        Assert.Equal(1L, source.GetValue(PowerShellPolicyScope.Computer, CoreTranscription, PowerShellPolicyKeys.EnableInvocationHeader));
        Assert.Equal(PowerShellTranscriptionPolicy.NotSet, new PowerShellPolicyReader(source).Read().Transcription);
    }

    [Fact]
    public void AnExpandableString_IsNotExpanded()
    {
        Write(_computer, CoreTranscription, PowerShellPolicyKeys.EnableTranscripting, 1, RegistryValueKind.DWord);
        Write(_computer, CoreTranscription, PowerShellPolicyKeys.OutputDirectory, @"%USERPROFILE%\PSLogs", RegistryValueKind.ExpandString);
        var source = Source();

        Assert.Equal(@"%USERPROFILE%\PSLogs", source.GetValue(PowerShellPolicyScope.Computer, CoreTranscription, PowerShellPolicyKeys.OutputDirectory));
        Assert.Equal(@"%USERPROFILE%\PSLogs", new PowerShellPolicyReader(source).Read().Transcription.OutputDirectory);
    }

    [Fact]
    public void TheModuleNames_AreTheValueNamesOfTheirKey_AnEmptyKeyIsAnEmptyList_AndAMultiStringIsAnArray()
    {
        Write(_computer, CoreModuleNames, "ActiveDirectory", "ActiveDirectory", RegistryValueKind.String);
        Write(_computer, CoreModuleNames, "*", "*", RegistryValueKind.String);
        using (_user.CreateSubKey(CoreModuleNames))
        {
        }

        var moduleLogging = PowerShellPolicyKeys.Sub(PowerShellPolicyKeys.CoreRoot, PowerShellPolicyKeys.ModuleLoggingKey);
        Write(_user, moduleLogging, PowerShellPolicyKeys.ModuleNamesKey, new[] { "AdMng", "Microsoft.PowerShell.*" }, RegistryValueKind.MultiString);
        var source = Source();

        Assert.Equal(["*", "ActiveDirectory"], source.GetValueNames(PowerShellPolicyScope.Computer, CoreModuleNames)!.Order());
        Assert.Empty(source.GetValueNames(PowerShellPolicyScope.User, CoreModuleNames)!);
        Assert.Equal(["AdMng", "Microsoft.PowerShell.*"], Assert.IsType<string[]>(source.GetValue(PowerShellPolicyScope.User, moduleLogging, PowerShellPolicyKeys.ModuleNamesKey)));
    }

    [Fact]
    public void WhatTheGpoEditorWritesWithTheCheckBox_IsReadFromTheWindowsPowerShellKey()
    {
        // Audit W-02, scenario C, through the real registry: the switch is in the policy's own key of the same hive.
        Write(_user, CoreTranscription, PowerShellPolicyKeys.EnableTranscripting, 1, RegistryValueKind.DWord);
        Write(_user, CoreTranscription, PowerShellPolicyKeys.UseWindowsPowerShellPolicy, 1, RegistryValueKind.DWord);
        Write(_user, WindowsTranscription, PowerShellPolicyKeys.EnableTranscripting, 1, RegistryValueKind.DWord);
        Write(_user, WindowsTranscription, PowerShellPolicyKeys.OutputDirectory, @"\\srv\transcripts$", RegistryValueKind.String);

        var transcription = new PowerShellPolicyReader(Source()).Read().Transcription;

        Assert.True(transcription.Enabled);
        Assert.Equal(@"\\srv\transcripts$", transcription.OutputDirectory);
        Assert.Equal(new PowerShellPolicyOrigin(PowerShellPolicyStore.WindowsPowerShell, PowerShellPolicyScope.User), transcription.Origin);
    }

    [Fact]
    public void TheConfigFiles_AreTheTwoPathsItWasGiven_AndAMissingOneIsNull()
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(EngineFile)!);
        File.WriteAllText(EngineFile, """{ "PowerShellPolicies": { "Transcription": { "EnableTranscripting": false } } }""");
        var source = Source();

        Assert.Contains("EnableTranscripting", source.ReadConfigFile(PowerShellPolicyScope.Computer), StringComparison.Ordinal);
        Assert.Null(source.ReadConfigFile(PowerShellPolicyScope.User));
        Assert.Equal(new PowerShellPolicyOrigin(PowerShellPolicyStore.ConfigFile, PowerShellPolicyScope.Computer), new PowerShellPolicyReader(source).Read().Transcription.Origin);
    }
}
