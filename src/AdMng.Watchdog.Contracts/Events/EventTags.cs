namespace AdMng.Watchdog.Contracts.Events;

/// <summary>Tag values of <see cref="WatchEvent.Tags"/>.</summary>
public static class EventTags
{
    public const string Privileged = "privileged";             // principal (or a member) in the current PrivilegeState only
    public const string AdminCountOrphan = "adminCountOrphan"; // adminCount=1 but not in PrivilegeState (digest hygiene, no bump)
    public const string ServiceAccount = "serviceAccount";     // MSA/gMSA/dMSA, user with SPN, or under ServiceAccountSubtrees
    public const string DefaultContainer = "defaultContainer"; // created/moved into the (possibly redirected) default users/computers container
    public const string UnexpectedContainer = "unexpectedContainer"; // default container is redirected, yet created in literal CN=Users / CN=Computers
    public const string CreatedByNonAdmin = "createdByNonAdmin"; // computer with mS-DS-CreatorSID (MachineAccountQuota)
    public const string Server = "server";                     // computer whose operatingSystem contains "Server"
    public const string DomainController = "domainController";
    public const string OtherDomain = "otherDomain";           // DC of another domain of the forest
    public const string Reconciliation = "reconciliation";     // detected by a full resync / reconciliation
    public const string Restored = "restored";                 // privileges/memberships that came back with a restored object
    public const string LongDisabled = "longDisabled";         // enabled after being disabled (or unused) ≥ 30 days
    public const string DisabledOu = "disabledOu";             // object is/was under Directory.DisabledAccountsSubtrees
    public const string Krbtgt = "krbtgt";
    public const string BuiltInAdministrator = "builtinAdministrator"; // RID 500
    public const string SelfChange = "selfChange";             // actor == target (computer self-updates, own password change)
    public const string PasswordReset = "passwordReset";       // password set by another account (4724) – added by attribution
    public const string SyncAccount = "syncAccount";           // actor is a detected Entra Connect / sync account
    public const string OutOfScope = "outOfScope";             // moved into/out of Directory.ExcludedSubtrees
}
