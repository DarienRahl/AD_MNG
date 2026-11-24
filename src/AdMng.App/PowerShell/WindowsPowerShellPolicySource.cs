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
/// created, no value written - and it reads the keys the engine reads (<see cref="PowerShellPolicyKeys"/>); which of them decides is
/// <see cref="PowerShellPolicyReader"/>'s rule. A key or a value that is absent is null ("not set"); a key or a file that cannot be read
/// (access denied, a damaged hive) is a <see cref="PowerShellPolicyUnreadableException"/> and the window says it could not tell -
/// never silence, which would read as "nothing is recorded".
/// </summary>
public sealed class WindowsPowerShellPolicySource : IPowerShellPolicySource
{
    /// <summary>The name of the file the engine reads its policies and settings from.</summary>
    public const string ConfigFileName = "powershell.config.json";

    private readonly RegistryKey _computer;
    private readonly RegistryKey _user;
    private readonly string _engineConfigFile;
    private readonly string _userConfigFile;

    /// <summary>The workstation's own policies: <c>HKLM</c>, <c>HKCU</c>, the engine's file beside the program and the user's file.</summary>
    public WindowsPowerShellPolicySource()
        : this(
            Registry.LocalMachine,
            Registry.CurrentUser,
            Path.Combine(PowerShellEnvironment.EngineHome, ConfigFileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PowerShell", ConfigFileName))
    {
    }

    /// <summary>
    /// Over other roots and files: a test points both hives at a throwaway key of its own (<c>HKCU\Software\AdMng.Tests-…</c>) and the
    /// files at paths it chose. The keys are opened read-only below the roots; the roots are not disposed.
    /// </summary>
    public WindowsPowerShellPolicySource(RegistryKey computerRoot, RegistryKey userRoot, string engineConfigFile, string userConfigFile)
    {
        _computer = computerRoot ?? throw new ArgumentNullException(nameof(computerRoot));
        _user = userRoot ?? throw new ArgumentNullException(nameof(userRoot));
        _engineConfigFile = engineConfigFile ?? throw new ArgumentNullException(nameof(engineConfigFile));
        _userConfigFile = userConfigFile ?? throw new ArgumentNullException(nameof(userConfigFile));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>GetValue</c> with a default, never <c>GetValueKind</c>: the latter throws for a value that is absent (audit W-01), and an absent
    /// value is the common case - a policy key holds only the values its Group Policy setting wrote.
    /// </remarks>
    public object? GetValue(PowerShellPolicyScope scope, string subKey, string valueName) =>
        Read(scope, subKey, key => key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames));

    /// <inheritdoc />
    public IReadOnlyList<string>? GetValueNames(PowerShellPolicyScope scope, string subKey) =>
        Read<IReadOnlyList<string>?>(scope, subKey, key => key.GetValueNames());

    /// <inheritdoc />
    public string? ReadConfigFile(PowerShellPolicyScope scope)
    {
        var path = scope == PowerShellPolicyScope.Computer ? _engineConfigFile : _userConfigFile;
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new PowerShellPolicyUnreadableException(PowerShellPolicyPlace.ConfigFile, path, ex);
        }
    }

    /// <inheritdoc />
    public PowerShellLockdownState GetLockdownState() => SystemPolicyLockdownProbe.Instance.Probe();

    private T? Read<T>(PowerShellPolicyScope scope, string subKey, Func<RegistryKey, T> read)
    {
        try
        {
            using var key = (scope == PowerShellPolicyScope.Computer ? _computer : _user).OpenSubKey(subKey, writable: false);
            return key is null ? default : read(key);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // The one thing this class must not do is turn "I could not look" into "nothing is recorded".
            throw new PowerShellPolicyUnreadableException(
                PowerShellPolicyPlace.Registry, (scope == PowerShellPolicyScope.Computer ? "HKLM" : "HKCU") + "\\" + subKey, ex);
        }
    }
}
