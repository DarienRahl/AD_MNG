using System.IO;
using System.Security;
using AdMng.Core.PowerShell;
using AdMng.PowerShell;
using AdMng.Presentation.PowerShell;
using Microsoft.Win32;

namespace AdMng.App.PowerShell;

/// <summary>
/// The Windows reads behind the policy note of the PowerShell window (track R, decision D7): the registry keys of the PowerShell
/// Group Policy, the two <c>powershell.config.json</c> files and the system lockdown state. <b>It only reads</b> - no key is
/// created, no value written - and it asks the same questions the engine does (<see cref="PowerShellPolicyKeys"/> has the exact keys),
/// so the note says what Windows records and the program changes and gets around nothing. A key that is absent is "not set"; a key
/// that cannot be read (access denied, a damaged hive) is an error of this class and the window says it could not tell - never
/// silence, which would read as "nothing is recorded".
/// </summary>
public sealed class WindowsPowerShellPolicySource : IPowerShellPolicySource
{
    /// <summary>The name of the file the engine reads its policies and settings from.</summary>
    public const string ConfigFileName = "powershell.config.json";

    /// <inheritdoc />
    public int? GetDword(PowerShellPolicyScope scope, string subKey, string valueName) =>
        Read(scope, subKey, key => key.GetValueKind(valueName) == RegistryValueKind.DWord && key.GetValue(valueName) is int value ? value : (int?)null, defaultValue: null);

    /// <inheritdoc />
    public string? GetString(PowerShellPolicyScope scope, string subKey, string valueName) =>
        Read(
            scope,
            subKey,
            key => key.GetValueKind(valueName) is RegistryValueKind.String or RegistryValueKind.ExpandString
                ? key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string
                : null,
            defaultValue: null);

    /// <inheritdoc />
    public IReadOnlyList<string> GetValueNames(PowerShellPolicyScope scope, string subKey) =>
        Read<IReadOnlyList<string>>(scope, subKey, key => key.GetValueNames(), defaultValue: []);

    /// <inheritdoc />
    public string? ReadConfigFile(PowerShellPolicyScope scope)
    {
        var path = scope == PowerShellPolicyScope.Computer
            ? Path.Combine(PowerShellEnvironment.EngineHome, ConfigFileName)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PowerShell", ConfigFileName);
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new InvalidOperationException($"The file {ConfigFileName} of the {(scope == PowerShellPolicyScope.Computer ? "program" : "user")} could not be read.", ex);
        }
    }

    /// <inheritdoc />
    public PowerShellLockdownState GetLockdownState() => SystemPolicyLockdownProbe.Instance.Probe();

    private static T Read<T>(PowerShellPolicyScope scope, string subKey, Func<RegistryKey, T> read, T defaultValue)
    {
        try
        {
            using var key = (scope == PowerShellPolicyScope.Computer ? Registry.LocalMachine : Registry.CurrentUser).OpenSubKey(subKey, writable: false);
            return key is null ? defaultValue : read(key);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException)
        {
            // The one thing this class must not do is turn "I could not look" into "nothing is recorded".
            throw new InvalidOperationException($"The registry key {(scope == PowerShellPolicyScope.Computer ? "HKLM" : "HKCU")}\\{subKey} could not be read.", ex);
        }
    }
}
