using System.Globalization;
using AdMng.Core.PowerShell;
using AdMng.Presentation.Localization;
using AdMng.Presentation.PowerShell;

namespace AdMng.Presentation.Tests.PowerShell;

/// <summary>
/// The lasting note about the policies (track R, decision D7): what is recorded and where, in both languages, and nothing of
/// the workstation's directory in the copy for the log.
/// </summary>
public sealed class PowerShellPolicyNoteTests
{
    private const string Directory = @"\\fileserver\transcripts";

    private static readonly PowerShellPolicyOrigin CoreComputer = new(PowerShellPolicyStore.PowerShellCore, PowerShellPolicyScope.Computer);

    private static ResxLocalizer English => new(CultureInfo.GetCultureInfo("en-US"));

    private static ResxLocalizer Polish => new(CultureInfo.GetCultureInfo("pl-PL"));

    private static PowerShellPolicies Transcribing(string? directory = Directory) =>
        new() { Transcription = new PowerShellTranscriptionPolicy(true, directory, false, CoreComputer) };

    [Fact]
    public void NothingRecords_NoNote_EvenWithAnExecutionPolicyAndALockdown()
    {
        var policies = new PowerShellPolicies
        {
            ExecutionPolicy = new PowerShellExecutionPolicyPolicy("AllSigned", CoreComputer),
            Lockdown = PowerShellLockdownState.Enforce,
            UseWindowsPowerShellPolicy = true,
            Transcription = new PowerShellTranscriptionPolicy(false, Directory, true, CoreComputer),
        };

        Assert.Null(PowerShellPolicyNotes.For(policies, English));
        Assert.Null(PowerShellPolicyNotes.For(PowerShellPolicies.None, English));
    }

    [Fact]
    public void Transcription_SaysWhatIsWrittenWhereAndByWhichPolicy_AndThatTheProgramDoesNotGetAroundIt()
    {
        var note = PowerShellPolicyNotes.For(Transcribing(), English)!;

        Assert.Equal(
            "Transcription is on (computer policy): the commands you run here and their results, which can include directory data, are written to \\\\fileserver\\transcripts. "
            + "AD Manager does not change these policies and does not get around them.",
            note.Message);
        Assert.Equal(L.PowerShellPolicy.Note, note.Log.Key);
        Assert.Equal([PowerShellPolicyNotes.TranscriptionBit], note.Log.Counts);
    }

    [Fact]
    public void Transcription_WithoutADirectory_SaysTheDocumentsFolder()
    {
        var note = PowerShellPolicyNotes.For(Transcribing(directory: null), English)!;

        Assert.Contains("Documents folder of your account", note.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("{", note.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptBlockLogging_SaysTheTextOfTheCommandsGoesToTheEventLog()
    {
        var policies = new PowerShellPolicies { ScriptBlockLogging = new PowerShellScriptBlockLoggingPolicy(true, false, new PowerShellPolicyOrigin(PowerShellPolicyStore.WindowsPowerShell, PowerShellPolicyScope.User)) };

        var note = PowerShellPolicyNotes.For(policies, English)!;

        Assert.Equal(
            "Script block logging is on (user policy of Windows PowerShell): the text of the commands you run here is written to the Windows event log (PowerShellCore/Operational). "
            + "AD Manager does not change these policies and does not get around them.",
            note.Message);
        Assert.Equal([PowerShellPolicyNotes.ScriptBlockLoggingBit], note.Log.Counts);
    }

    [Theory]
    [InlineData(new[] { "ActiveDirectory", "*" }, "modules: ActiveDirectory, *")]
    [InlineData(new string[0], "modules: none listed")]
    public void ModuleLogging_NamesTheModules(string[] modules, string expected)
    {
        var policies = new PowerShellPolicies { ModuleLogging = new PowerShellModuleLoggingPolicy(true, modules, new PowerShellPolicyOrigin(PowerShellPolicyStore.ConfigFile, PowerShellPolicyScope.User)) };

        var note = PowerShellPolicyNotes.For(policies, English)!;

        Assert.StartsWith("Module logging is on (your powershell.config.json, " + expected + "):", note.Message, StringComparison.Ordinal);
        Assert.Equal([PowerShellPolicyNotes.ModuleLoggingBit], note.Log.Counts);
    }

    [Fact]
    public void Several_AreOneNote_InOrder_WithOneClosingSentence_AndTheMaskOfAll()
    {
        var policies = Transcribing() with
        {
            ScriptBlockLogging = new PowerShellScriptBlockLoggingPolicy(true, true, CoreComputer),
            ModuleLogging = new PowerShellModuleLoggingPolicy(true, ["*"], CoreComputer),
        };

        var note = PowerShellPolicyNotes.For(policies, English)!;

        var transcription = note.Message.IndexOf("Transcription is on", StringComparison.Ordinal);
        var scriptBlock = note.Message.IndexOf("Script block logging is on", StringComparison.Ordinal);
        var module = note.Message.IndexOf("Module logging is on", StringComparison.Ordinal);
        Assert.True(transcription >= 0 && transcription < scriptBlock && scriptBlock < module);
        Assert.Equal(1, CountOf(note.Message, "does not change these policies"));
        Assert.Equal([7L], note.Log.Counts);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("pl-PL")]
    public void TheLogCopy_IsAKeyAndAMask_NeverThePathOrTheModules(string culture)
    {
        var localizer = new ResxLocalizer(CultureInfo.GetCultureInfo(culture));
        var policies = Transcribing() with { ModuleLogging = new PowerShellModuleLoggingPolicy(true, ["SecretModule"], CoreComputer) };

        var note = PowerShellPolicyNotes.For(policies, localizer)!;

        Assert.Contains(Directory, note.Message, StringComparison.Ordinal);
        Assert.Contains("SecretModule", note.Message, StringComparison.Ordinal);
        var log = note.Log.ToString();
        Assert.DoesNotContain("fileserver", log, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretModule", log, StringComparison.Ordinal);
        Assert.Contains(L.PowerShellPolicy.Note, log, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePolishNote_IsPolish_AndFormatsEveryPlaceholder()
    {
        var policies = Transcribing() with
        {
            ScriptBlockLogging = new PowerShellScriptBlockLoggingPolicy(true, false, CoreComputer),
            ModuleLogging = new PowerShellModuleLoggingPolicy(true, [], new PowerShellPolicyOrigin(PowerShellPolicyStore.ConfigFile, PowerShellPolicyScope.Computer)),
        };

        var note = PowerShellPolicyNotes.For(policies, Polish)!;

        Assert.Contains("Transkrypcja jest włączona (zasady komputera)", note.Message, StringComparison.Ordinal);
        Assert.Contains("Rejestrowanie bloków skryptów jest włączone", note.Message, StringComparison.Ordinal);
        Assert.Contains("(plik powershell.config.json silnika, moduły: brak na liście)", note.Message, StringComparison.Ordinal);
        Assert.EndsWith("AD Manager nie zmienia tych zasad i ich nie omija.", note.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("{", note.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PowerShellPolicyStore.PowerShellCore, PowerShellPolicyScope.Computer, "computer policy", "zasady komputera")]
    [InlineData(PowerShellPolicyStore.PowerShellCore, PowerShellPolicyScope.User, "user policy", "zasady użytkownika")]
    [InlineData(PowerShellPolicyStore.WindowsPowerShell, PowerShellPolicyScope.Computer, "computer policy of Windows PowerShell", "zasady komputera dla Windows PowerShell")]
    [InlineData(PowerShellPolicyStore.WindowsPowerShell, PowerShellPolicyScope.User, "user policy of Windows PowerShell", "zasady użytkownika dla Windows PowerShell")]
    [InlineData(PowerShellPolicyStore.ConfigFile, PowerShellPolicyScope.Computer, "powershell.config.json of the engine", "plik powershell.config.json silnika")]
    [InlineData(PowerShellPolicyStore.ConfigFile, PowerShellPolicyScope.User, "your powershell.config.json", "Twój plik powershell.config.json")]
    public void EveryOrigin_HasItsOwnWords_InBothLanguages(PowerShellPolicyStore store, PowerShellPolicyScope scope, string english, string polish)
    {
        var origin = new PowerShellPolicyOrigin(store, scope);

        Assert.Equal(english, PowerShellPolicyNotes.OriginText(English, origin));
        Assert.Equal(polish, PowerShellPolicyNotes.OriginText(Polish, origin));
    }

    [Fact]
    public void AnUnknownOrigin_IsJustAPolicy()
    {
        Assert.Equal("policy", PowerShellPolicyNotes.OriginText(English, null));
        Assert.Equal("zasady", PowerShellPolicyNotes.OriginText(Polish, null));
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var index = text.IndexOf(part, StringComparison.Ordinal); index >= 0; index = text.IndexOf(part, index + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
