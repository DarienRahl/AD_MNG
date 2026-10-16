namespace AdMng.Watchdog.Contracts.Events;

public enum WatchObjectKind
{
    Unknown = 0, User = 1, Computer = 2, Group = 3, OrganizationalUnit = 4, Container = 5, Contact = 6,
    ServiceAccount = 7,      // msDS-ManagedServiceAccount, msDS-GroupManagedServiceAccount, msDS-DelegatedManagedServiceAccount
    Domain = 8, GroupPolicy = 9, Trust = 10, DomainController = 11, PasswordSettings = 12, Schema = 13, Watchdog = 14,
    PublicKeyInfrastructure = 15,   // certificate templates, enrollment services, CAs, NTAuth (Configuration NC)
    Configuration = 16,             // other forest singletons: Partitions, Directory Service, FSMO holder objects
}
