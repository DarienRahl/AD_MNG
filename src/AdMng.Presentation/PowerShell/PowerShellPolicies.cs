using AdMng.Core.PowerShell;

namespace AdMng.Presentation.PowerShell;

/// <summary>Where a policy was set: the computer (HKLM, or the file next to the engine) or the user (HKCU, or the user's file).</summary>
public enum PowerShellPolicyScope
{
    /// <summary><c>HKEY_LOCAL_MACHINE</c>, or the <c>powershell.config.json</c> in the folder of the engine (all users).</summary>
    Computer = 0,

    /// <summary><c>HKEY_CURRENT_USER</c>, or the <c>powershell.config.json</c> in the user's PowerShell folder.</summary>
    User = 1,
}

/// <summary>Which store a policy was read from.</summary>
public enum PowerShellPolicyStore
{
    /// <summary>The PowerShell 7 policy key, <c>Software\Policies\Microsoft\PowerShellCore</c>.</summary>
    PowerShellCore = 0,

    /// <summary>
    /// The Windows PowerShell policy key, <c>Software\Policies\Microsoft\Windows\PowerShell</c> (or its sub key of the policy):
    /// PowerShell 7 reads it for a policy only while that policy's "Use Windows PowerShell Policy setting"
    /// (<c>UseWindowsPowerShellPolicySetting</c> in the policy's own PowerShell 7 key of the same hive) is on.
    /// </summary>
    WindowsPowerShell = 1,

    /// <summary>A <c>powershell.config.json</c> (<c>PowerShellPolicies</c>), the policy file of the engine; nothing but a file.</summary>
    ConfigFile = 2,
}

/// <summary>
/// The store and the scope a policy came from (which hive and which policy source): each policy has its own, since the engine takes
/// each from the first place that sets any of its values, and "Use Windows PowerShell Policy setting" is a switch of each policy.
/// </summary>
public readonly record struct PowerShellPolicyOrigin(PowerShellPolicyStore Store, PowerShellPolicyScope Scope);

/// <summary>Transcription (<c>Transcription</c>): the engine writes every command and its output to a file in <see cref="OutputDirectory"/>.</summary>
/// <param name="Enabled">Transcription is on.</param>
/// <param name="OutputDirectory">The directory the policy names; null when it names none (the engine then uses the user's Documents folder).</param>
/// <param name="InvocationHeader">The transcript carries a header with the time of every command.</param>
/// <param name="Origin">Where the policy was set; null when no policy sets it.</param>
public sealed record PowerShellTranscriptionPolicy(bool Enabled, string? OutputDirectory, bool InvocationHeader, PowerShellPolicyOrigin? Origin)
{
    public static PowerShellTranscriptionPolicy NotSet { get; } = new(false, null, false, null);
}

/// <summary>Script block logging (<c>ScriptBlockLogging</c>): the text of every script block goes to the PowerShell event log (event 4104).</summary>
/// <param name="Enabled">Script block logging is on.</param>
/// <param name="LogsInvocations">The start and stop of every script block are logged too (event 4105, 4106).</param>
/// <param name="Origin">Where the policy was set; null when no policy sets it.</param>
public sealed record PowerShellScriptBlockLoggingPolicy(bool Enabled, bool LogsInvocations, PowerShellPolicyOrigin? Origin)
{
    public static PowerShellScriptBlockLoggingPolicy NotSet { get; } = new(false, false, null);
}

/// <summary>Module logging (<c>ModuleLogging</c>): the pipeline execution details of the listed modules go to the event log (event 4103).</summary>
/// <param name="Enabled">Module logging is on.</param>
/// <param name="ModuleNames">The modules the policy names ("*" is all of them).</param>
/// <param name="Origin">Where the policy was set; null when no policy sets it.</param>
public sealed record PowerShellModuleLoggingPolicy(bool Enabled, IReadOnlyList<string> ModuleNames, PowerShellPolicyOrigin? Origin)
{
    public static PowerShellModuleLoggingPolicy NotSet { get; } = new(false, [], null);
}

/// <summary>The execution policy a GPO sets (<c>EnableScripts</c>, <c>ExecutionPolicy</c>). The window runs no script file in R1, so it is information only.</summary>
/// <param name="Policy">The name (<c>AllSigned</c>, <c>RemoteSigned</c>, <c>Unrestricted</c>…), "Restricted" when the policy turns scripts off, null when none sets it.</param>
/// <param name="Origin">Where the policy was set; null when no policy sets it.</param>
public sealed record PowerShellExecutionPolicyPolicy(string? Policy, PowerShellPolicyOrigin? Origin)
{
    public static PowerShellExecutionPolicyPolicy NotSet { get; } = new(null, null);
}

/// <summary>
/// What the policies of the workstation say about recording PowerShell, read when the window opens (track R, decision D7;
/// docs/dev/r1-powershell-window.md). It names facts, never the content of a log: a directory the policy names, the
/// modules it lists, where it was set. The window shows the facts and never tries to get around a policy.
/// </summary>
public sealed record PowerShellPolicies
{
    /// <summary>Nothing is set and nothing is enforced.</summary>
    public static PowerShellPolicies None { get; } = new();

    public PowerShellTranscriptionPolicy Transcription { get; init; } = PowerShellTranscriptionPolicy.NotSet;

    public PowerShellScriptBlockLoggingPolicy ScriptBlockLogging { get; init; } = PowerShellScriptBlockLoggingPolicy.NotSet;

    public PowerShellModuleLoggingPolicy ModuleLogging { get; init; } = PowerShellModuleLoggingPolicy.NotSet;

    public PowerShellExecutionPolicyPolicy ExecutionPolicy { get; init; } = PowerShellExecutionPolicyPolicy.NotSet;

    /// <summary>WDAC / AppLocker (<c>SystemPolicy.GetSystemLockdownPolicy</c>).</summary>
    public PowerShellLockdownState Lockdown { get; init; }

    /// <summary>Something records what the operator types or what the commands return.</summary>
    public bool IsRecording => Transcription.Enabled || ScriptBlockLogging.Enabled || ModuleLogging.Enabled;
}
