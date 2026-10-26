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

    // stage B (docs/dev/watchdog-linux-design.md 8.5, 9.1–9.4)
    public const string BadSuccessor = "badSuccessor";         // dMSA created, or a dMSA migration link / state changed (9.1)
    public const string MachineAccountQuota = "machineAccountQuota"; // DomainPolicyChanged of ms-DS-MachineAccountQuota (9.2)
    public const string WatchdogAccount = "watchdogAccount";   // object (or principal) is the service's own account ADMNG-WD$ (9.3)
    public const string GppPassword = "gppPassword";           // GPO preferences carry a cpassword (MS14-025; value never stored, 8.5)
    public const string AuditSettings = "auditSettings";       // SecurityDescriptorChanged from an nTSecurityDescriptor version bump with an unchanged owner/DACL – a SACL change (9.4)
}
