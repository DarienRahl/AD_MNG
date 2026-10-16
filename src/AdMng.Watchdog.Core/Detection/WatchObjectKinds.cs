using AdMng.Core.ActiveDirectory;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>Maps objectClass values (the full chain AD returns) to the <see cref="WatchObjectKind"/> of section 9.1.</summary>
public static class WatchObjectKinds
{
    /// <summary>The kind of an object in the default naming context; null for classes the watchdog does not watch.</summary>
    public static WatchObjectKind? FromObjectClasses(IEnumerable<string> objectClasses)
    {
        ArgumentNullException.ThrowIfNull(objectClasses);
        var set = new HashSet<string>(objectClasses, StringComparer.OrdinalIgnoreCase);
        if (set.Contains(ObjectClasses.DomainDns))
        {
            return WatchObjectKind.Domain;
        }

        if (set.Contains(ObjectClasses.ManagedServiceAccount) || set.Contains(ObjectClasses.GroupManagedServiceAccount)
            || set.Contains(ObjectClasses.DelegatedManagedServiceAccount))
        {
            return WatchObjectKind.ServiceAccount;
        }

        if (set.Contains(ObjectClasses.Computer))
        {
            return WatchObjectKind.Computer;
        }

        if (set.Contains(ObjectClasses.User))
        {
            return WatchObjectKind.User;
        }

        if (set.Contains(ObjectClasses.Group))
        {
            return WatchObjectKind.Group;
        }

        if (set.Contains(ObjectClasses.Contact))
        {
            return WatchObjectKind.Contact;
        }

        if (set.Contains(ObjectClasses.OrganizationalUnit))
        {
            return WatchObjectKind.OrganizationalUnit;
        }

        if (set.Contains(WatchdogObjectClasses.GroupPolicyContainer))
        {
            return WatchObjectKind.GroupPolicy;
        }

        if (set.Contains(WatchdogObjectClasses.TrustedDomain))
        {
            return WatchObjectKind.Trust;
        }

        if (set.Contains(WatchdogObjectClasses.PasswordSettings))
        {
            return WatchObjectKind.PasswordSettings;
        }

        if (set.Contains(WatchdogObjectClasses.RidManager) || set.Contains(ObjectClasses.InfrastructureUpdate))
        {
            return WatchObjectKind.Configuration;
        }

        return set.Contains(ObjectClasses.Container) ? WatchObjectKind.Container : null;
    }

    /// <summary>True for security principals that can carry privileges (users, computers, service accounts, groups).</summary>
    public static bool IsPrincipal(WatchObjectKind kind) =>
        kind is WatchObjectKind.User or WatchObjectKind.Computer or WatchObjectKind.ServiceAccount or WatchObjectKind.Group;

    /// <summary>True for account kinds with userAccountControl.</summary>
    public static bool IsAccount(WatchObjectKind kind) =>
        kind is WatchObjectKind.User or WatchObjectKind.Computer or WatchObjectKind.ServiceAccount;

    /// <summary>True for the kinds processed in the container pass (10.5) – their moves rewrite descendants.</summary>
    public static bool IsContainer(WatchObjectKind kind) =>
        kind is WatchObjectKind.OrganizationalUnit or WatchObjectKind.Container or WatchObjectKind.Domain;
}
