using AdMng.Core.PowerShell;

namespace AdMng.Presentation.PowerShell;

/// <summary>
/// What the window asks when it opens (track R, decision D7): which policies of this workstation record PowerShell, and is a
/// lockdown (WDAC / AppLocker) in force. Reads only the machine's own settings - never the directory. The default
/// implementation is <see cref="PowerShellPolicyReader"/> over an <see cref="IPowerShellPolicySource"/>; tests use a fake.
/// </summary>
public interface IPowerShellPolicyReader
{
    /// <summary>Reads the policies now. Runs off the UI thread. A failure is an exception (the window then says it could not tell).</summary>
    Task<PowerShellPolicies> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The raw reads behind <see cref="PowerShellPolicyReader"/>: the one thing the Windows implementation (AdMng.App, unit 4 of
/// stage R1) has to provide, so that every rule - which key wins, when Windows PowerShell policies count, how a config file is
/// read - is plain code that is tested on any system. <b>The exact keys and values are in <see cref="PowerShellPolicyKeys"/>.</b>
/// An absent key, an absent value or a value of another type is <c>null</c> / empty, never an exception; a real failure (the
/// registry cannot be opened, the policy probe throws) is an exception.
/// </summary>
public interface IPowerShellPolicySource
{
    /// <summary>
    /// A <c>REG_DWORD</c> value under <c>HKLM\</c> (<see cref="PowerShellPolicyScope.Computer"/>) or <c>HKCU\</c>
    /// (<see cref="PowerShellPolicyScope.User"/>) + <paramref name="subKey"/>, or null.
    /// </summary>
    int? GetDword(PowerShellPolicyScope scope, string subKey, string valueName);

    /// <summary>A <c>REG_SZ</c> value (also <c>REG_EXPAND_SZ</c>, not expanded), or null.</summary>
    string? GetString(PowerShellPolicyScope scope, string subKey, string valueName);

    /// <summary>The names of the values of a key (the module names of <c>ModuleLogging\ModuleNames</c> are value names); empty when the key is absent.</summary>
    IReadOnlyList<string> GetValueNames(PowerShellPolicyScope scope, string subKey);

    /// <summary>
    /// The text of a <c>powershell.config.json</c>, or null when there is none: <see cref="PowerShellPolicyScope.Computer"/> is the
    /// file in the folder of <c>System.Management.Automation.dll</c> (the engine's all-users file, the one AD Manager ships),
    /// <see cref="PowerShellPolicyScope.User"/> the file of the user (<c>Documents\PowerShell\powershell.config.json</c>).
    /// </summary>
    string? ReadConfigFile(PowerShellPolicyScope scope);

    /// <summary>The system lockdown state (<c>SystemPolicy.GetSystemLockdownPolicy()</c>: WDAC and AppLocker).</summary>
    PowerShellLockdownState GetLockdownState();
}

/// <summary>
/// The registry keys, value names and file sections <see cref="PowerShellPolicyReader"/> reads. Taken from what the engine
/// itself reads (<c>System.Management.Automation.dll</c> 7.6: the key paths and the property names of its policy classes) and
/// from the Group Policy templates of PowerShell. The Windows implementation of <see cref="IPowerShellPolicySource"/> only
/// reads these.
/// </summary>
public static class PowerShellPolicyKeys
{
    /// <summary>The PowerShell 7 policy key, under both <c>HKLM</c> and <c>HKCU</c>.</summary>
    public const string CoreRoot = @"Software\Policies\Microsoft\PowerShellCore";

    /// <summary>The Windows PowerShell policy key, under both <c>HKLM</c> and <c>HKCU</c>; read only while <see cref="UseWindowsPowerShellPolicy"/> is on.</summary>
    public const string WindowsRoot = @"Software\Policies\Microsoft\Windows\PowerShell";

    /// <summary><c>REG_DWORD</c> 1 in <see cref="CoreRoot"/>: "Use Windows PowerShell Policy setting" (the Windows PowerShell policies stand in for the PowerShell 7 ones).</summary>
    public const string UseWindowsPowerShellPolicy = "UseWindowsPowerShellPolicySetting";

    // The execution policy: values of the root key itself.

    /// <summary><c>REG_DWORD</c> in the root key: 0 = scripts are turned off, 1 = the policy named by <see cref="ExecutionPolicy"/> applies.</summary>
    public const string EnableScripts = "EnableScripts";

    /// <summary><c>REG_SZ</c> in the root key: <c>AllSigned</c>, <c>RemoteSigned</c>, <c>Unrestricted</c>…</summary>
    public const string ExecutionPolicy = "ExecutionPolicy";

    // Transcription: key <root>\Transcription.
    public const string TranscriptionKey = "Transcription";

    /// <summary><c>REG_DWORD</c>.</summary>
    public const string EnableTranscripting = "EnableTranscripting";

    /// <summary><c>REG_DWORD</c>.</summary>
    public const string EnableInvocationHeader = "EnableInvocationHeader";

    /// <summary><c>REG_SZ</c>: the directory of the transcripts (absent or empty: the user's Documents folder).</summary>
    public const string OutputDirectory = "OutputDirectory";

    // Script block logging: key <root>\ScriptBlockLogging.
    public const string ScriptBlockLoggingKey = "ScriptBlockLogging";

    /// <summary><c>REG_DWORD</c>.</summary>
    public const string EnableScriptBlockLogging = "EnableScriptBlockLogging";

    /// <summary><c>REG_DWORD</c>.</summary>
    public const string EnableScriptBlockInvocationLogging = "EnableScriptBlockInvocationLogging";

    // Module logging: key <root>\ModuleLogging, the module names are the value NAMES of <root>\ModuleLogging\ModuleNames.
    public const string ModuleLoggingKey = "ModuleLogging";

    /// <summary><c>REG_DWORD</c>.</summary>
    public const string EnableModuleLogging = "EnableModuleLogging";

    /// <summary>Sub key of <see cref="ModuleLoggingKey"/> whose value names are the logged modules ("*" is all).</summary>
    public const string ModuleNamesKey = "ModuleNames";

    // powershell.config.json: { "PowerShellPolicies": { "<section>": { "<name>": value } } } with the same value names; booleans are true / false.
    public const string ConfigPolicies = "PowerShellPolicies";

    /// <summary>The section of the execution policy in the config file (<c>EnableScripts</c>, <c>ExecutionPolicy</c>).</summary>
    public const string ConfigScriptExecution = "ScriptExecution";

    /// <summary>The key of a policy section: the registry sub key and the JSON section have the same names except the execution policy.</summary>
    public static string Sub(string root, string key) => root + "\\" + key;
}
