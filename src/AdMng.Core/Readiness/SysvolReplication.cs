using AdMng.Core.Ldap;

namespace AdMng.Core.Readiness;

/// <summary>How SYSVOL of the domain is replicated, as far as the directory tells.</summary>
public enum SysvolReplicationState
{
    /// <summary>DFSR only: the migration ended in "Eliminated" (msDFSR-Flags 48), or the domain uses DFSR natively.</summary>
    Dfsr,

    /// <summary>"Redirected" (32): DFSR serves SYSVOL, FRS still replicates the old copy – the migration is not finished.</summary>
    MigrationRedirected,

    /// <summary>"Prepared" (16): DFSR copies SYSVOL, FRS still serves it.</summary>
    MigrationPrepared,

    /// <summary>"Start" (0): the migration was started or reset; FRS serves SYSVOL.</summary>
    MigrationStarted,

    /// <summary>FRS: the FRS replica set of SYSVOL exists and no DFSR migration state is set.</summary>
    Frs,

    /// <summary>Neither can be told (no such objects – e.g. Samba, which replicates no SYSVOL – or they are not readable).</summary>
    Unknown,
}

/// <summary>
/// DFSR or FRS for SYSVOL (readiness check, design note docs/dev/s-pilot-readiness.md 2), from three objects of the
/// domain's <c>CN=System</c>: <c>msDFSR-Flags</c> of <c>CN=DFSR-GlobalSettings</c> (the global state of <c>dfsrmig</c>:
/// 0 Start, 16 Prepared, 32 Redirected, 48 Eliminated; absent in a domain that never migrated), the DFSR replication group
/// <c>CN=Domain System Volume,CN=DFSR-GlobalSettings</c> (a domain created at the Windows Server 2008 functional level or
/// later replicates SYSVOL with DFSR from the start and may have no migration state) and the FRS replica set
/// <c>CN=Domain System Volume (SYSVOL share),CN=File Replication Service</c> (deleted in the Eliminated state). Pure.
/// </summary>
public static class SysvolReplication
{
    /// <summary>The attribute that holds the global migration state.</summary>
    public const string FlagsAttribute = "msDFSR-Flags";

    public const int StartFlags = 0;
    public const int PreparedFlags = 16;
    public const int RedirectedFlags = 32;
    public const int EliminatedFlags = 48;

    /// <summary><c>CN=DFSR-GlobalSettings,CN=System,&lt;domain&gt;</c>.</summary>
    public static string GlobalSettingsDn(string domainDn) =>
        DistinguishedName.Parse(domainDn).Child("CN", "System").Child("CN", "DFSR-GlobalSettings").ToString();

    /// <summary><c>CN=Domain System Volume,CN=DFSR-GlobalSettings,CN=System,&lt;domain&gt;</c> – the DFSR group of SYSVOL.</summary>
    public static string DfsrGroupDn(string domainDn) =>
        DistinguishedName.Parse(GlobalSettingsDn(domainDn)).Child("CN", "Domain System Volume").ToString();

    /// <summary><c>CN=Domain System Volume (SYSVOL share),CN=File Replication Service,CN=System,&lt;domain&gt;</c> – the FRS replica set.</summary>
    public static string FrsReplicaSetDn(string domainDn) =>
        DistinguishedName.Parse(domainDn).Child("CN", "System").Child("CN", "File Replication Service").Child("CN", "Domain System Volume (SYSVOL share)").ToString();

    /// <summary>The state from what was read.</summary>
    /// <param name="flags">msDFSR-Flags of the global settings (null: absent or not readable).</param>
    /// <param name="hasDfsrGroup">The DFSR replication group of SYSVOL exists.</param>
    /// <param name="hasFrsReplicaSet">The FRS replica set of SYSVOL exists.</param>
    public static SysvolReplicationState Classify(int? flags, bool hasDfsrGroup, bool hasFrsReplicaSet) => flags switch
    {
        EliminatedFlags => SysvolReplicationState.Dfsr,
        RedirectedFlags => SysvolReplicationState.MigrationRedirected,
        PreparedFlags => SysvolReplicationState.MigrationPrepared,
        StartFlags => hasFrsReplicaSet || !hasDfsrGroup ? SysvolReplicationState.MigrationStarted : SysvolReplicationState.Dfsr,
        null when hasFrsReplicaSet => SysvolReplicationState.Frs,
        null when hasDfsrGroup => SysvolReplicationState.Dfsr,
        _ => SysvolReplicationState.Unknown,
    };
}
