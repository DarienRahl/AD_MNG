namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// Bootstrap <c>Watchdog:Sysvol</c> (Linux, stage B design 8.3, 14.1): how GPO folders are read from SYSVOL. The read limits
/// are runtime settings (<c>Directory.GpoContent</c>); this section holds the host side only.
/// </summary>
public sealed record SysvolBootstrapOptions
{
    public const string DefaultSmbClientPath = "/usr/bin/smbclient";

    public const string DefaultConfigFile = "/etc/admng-watchdog/smb-client.conf";

    /// <summary>False disables SYSVOL reading on this host regardless of <c>Directory.GpoContent.Enabled</c>.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The smbclient executable (started without a shell, arguments as a list).</summary>
    public string SmbClientPath { get; init; } = DefaultSmbClientPath;

    /// <summary>The private smb.conf passed with <c>-s</c>.</summary>
    public string ConfigFile { get; init; } = DefaultConfigFile;

    /// <summary>One smbclient invocation at most this long.</summary>
    public int InvocationTimeoutSeconds { get; init; } = 60;
}
