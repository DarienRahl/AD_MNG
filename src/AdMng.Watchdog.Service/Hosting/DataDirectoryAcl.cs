using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// Windows ACL of the data folder (spec 19.5): protected DACL with SYSTEM Full, Administrators Full and the service
/// identity Modify (owner Administrators when the process may set it); verification compares every allow ACE of the
/// folder and the database files – inherited or explicit, any access mask – with the allow-list {SYSTEM, Administrators,
/// service identity}, and the owner with {SYSTEM, Administrators, service identity}. (The service identity is accepted
/// as owner because a gMSA cannot assign ownership to Administrators when it creates the folder itself.)
/// </summary>
[SupportedOSPlatform("windows")]
public static class DataDirectoryAcl
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>Creates <paramref name="path"/> with the protected DACL.</summary>
    public static void CreateProtected(string path)
    {
        var identity = ServiceIdentity();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.Modify, inherit, PropagationFlags.None, AccessControlType.Allow));
        try
        {
            security.SetOwner(Administrators);
            new DirectoryInfo(path).Create(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException)
        {
            // Not allowed to assign ownership (gMSA): keep the default owner (this identity), same DACL.
            var withoutOwner = new DirectorySecurity();
            withoutOwner.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            {
                withoutOwner.AddAccessRule(rule);
            }

            new DirectoryInfo(path).Create(withoutOwner);
        }
    }

    /// <summary>The ACL deviations of the existing <paramref name="paths"/> (empty = compliant). Each entry names the
    /// file name and the offending SID – never a directory value.</summary>
    public static IReadOnlyList<string> Verify(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var allowed = AllowedSids();
        var problems = new List<string>();
        foreach (var path in paths)
        {
            FileSystemSecurity? security = System.IO.Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
                : File.Exists(path) ? new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner) : null;
            if (security is null)
            {
                continue;
            }

            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            if (security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && !allowed.Contains(owner))
            {
                problems.Add($"{name}: owner {owner.Value}");
            }

            foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference is SecurityIdentifier sid && !allowed.Contains(sid))
                {
                    problems.Add($"{name}: {sid.Value} {rule.FileSystemRights}");
                }
            }
        }

        return problems;
    }

    /// <summary>SYSTEM, Administrators, the process identity and – when it exists – the virtual service account
    /// <c>NT SERVICE\AdMngWatchdog</c>.</summary>
    public static IReadOnlySet<SecurityIdentifier> AllowedSids()
    {
        var allowed = new HashSet<SecurityIdentifier> { LocalSystem, Administrators, ServiceIdentity() };
        try
        {
            if (new NTAccount("NT SERVICE", WatchdogHost.ServiceName).Translate(typeof(SecurityIdentifier)) is SecurityIdentifier serviceSid)
            {
                allowed.Add(serviceSid);
            }
        }
        catch (IdentityNotMappedException)
        {
            // The service is not installed (console / CI run).
        }
        catch (SystemException)
        {
            // Translation unavailable – the virtual account is simply not allowed.
        }

        return allowed;
    }

    private static SecurityIdentifier ServiceIdentity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("The process identity has no SID.");
    }
}
