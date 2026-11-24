namespace AdMng.Presentation.PowerShell;

/// <summary>What of the workstation's PowerShell policies could not be read.</summary>
public enum PowerShellPolicyPlace
{
    /// <summary>A registry key (access denied, a damaged hive): <see cref="PowerShellPolicyUnreadableException.Location"/> is <c>HKLM\…</c> or <c>HKCU\…</c>.</summary>
    Registry = 0,

    /// <summary>A <c>powershell.config.json</c>: <see cref="PowerShellPolicyUnreadableException.Location"/> is its path.</summary>
    ConfigFile = 1,

    /// <summary>
    /// The value <c>UseWindowsPowerShellPolicySetting</c> of the key in <see cref="PowerShellPolicyUnreadableException.Location"/> is
    /// not a <c>REG_DWORD</c>: the engine casts it to a number, so what it records is not known.
    /// </summary>
    SwitchValue = 2,
}

/// <summary>
/// The policies of the workstation could not be read, so whether PowerShell records anything is not known (track R, decision D7):
/// the window says so with <see cref="Place"/> and <see cref="Location"/> through the error translator, never as silence that would
/// read as "nothing is recorded". The location is a key or a file of the workstation, never directory data.
/// </summary>
public sealed class PowerShellPolicyUnreadableException : Exception
{
    public PowerShellPolicyUnreadableException(PowerShellPolicyPlace place, string location, Exception? innerException = null)
        : base($"The PowerShell policy {place} {location} could not be read.", innerException)
    {
        Place = place;
        Location = location ?? throw new ArgumentNullException(nameof(location));
    }

    public PowerShellPolicyPlace Place { get; }

    /// <summary><c>HKLM\Software\Policies\…</c>, <c>HKCU\…</c> or the path of the file.</summary>
    public string Location { get; }
}
