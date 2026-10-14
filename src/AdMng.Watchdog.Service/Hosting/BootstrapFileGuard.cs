using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// The service refuses to start when its own identity can change the bootstrap configuration (spec 19.5): the bootstrap
/// file decides who is Admin and where data lives, so a compromised service process must not be able to rewrite it.
/// Checked when running as a Windows service: an allow ACE with any write, delete, permission or ownership right for the
/// process user SID or one of its group SIDs on <c>config\appsettings.json</c> or its folder.
/// </summary>
[SupportedOSPlatform("windows")]
public static class BootstrapFileGuard
{
    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes
        | FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>The default location: <c>%ProgramData%\AdMng\Watchdog\config\appsettings.json</c>.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AdMng", "Watchdog", "config", "appsettings.json");

    /// <summary>True when the current identity may write <paramref name="file"/> or its folder (missing file: false).</summary>
    public static bool IsWritableByCurrentIdentity(string file)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        if (!File.Exists(file))
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var sids = new HashSet<SecurityIdentifier>();
        if (identity.User is { } user)
        {
            sids.Add(user);
        }

        foreach (var group in identity.Groups ?? [])
        {
            if (group is SecurityIdentifier sid)
            {
                sids.Add(sid);
            }
        }

        var folder = Path.GetDirectoryName(file);
        return GrantsWrite(new FileInfo(file).GetAccessControl(AccessControlSections.Access), sids)
               || (folder is not null && GrantsWrite(new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Access), sids));
    }

    private static bool GrantsWrite(FileSystemSecurity security, HashSet<SecurityIdentifier> sids)
    {
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference is SecurityIdentifier sid
                && sids.Contains(sid) && (rule.FileSystemRights & WriteRights) != 0)
            {
                return true;
            }
        }

        return false;
    }
}
