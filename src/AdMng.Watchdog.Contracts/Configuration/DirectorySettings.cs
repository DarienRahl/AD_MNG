using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Contracts.Configuration;

public enum MembershipTracking { AllGroups, PrivilegedAndWatchListOnly }

/// <summary>Change detection (sections 9 and 10).</summary>
public sealed record DirectorySettings
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(15);        // RootDSE check; 5 s .. 5 min
    public TimeOnly FullReconciliationTime { get; init; } = new(2, 30);            // local time, daily
    public long UsnOverlap { get; init; }                                          // re-read USNs [W+1-overlap ..]
    public bool TrackDeletedObjects { get; init; } = true;
    public MembershipTracking MembershipTracking { get; init; } = MembershipTracking.AllGroups;
    public bool WatchConfigurationPartition { get; init; } = true;                 // new DCs (nTDSDSA)
    public bool WatchSchemaPartition { get; init; } = true;
    public IReadOnlyList<string> WakeUpContainers { get; init; } = [];             // change-notification wake-up (≤ 4)
    public IReadOnlyList<string> ExcludedSubtrees { get; init; } = [];             // never snapshotted, no events
    public IReadOnlyList<string> CriticalObjects { get; init; } =
        ["{domain}", "CN=AdminSDHolder,CN=System,{domain}", "OU=Domain Controllers,{domain}"];  // SD tracked
    public IReadOnlyList<string> ServiceAccountSubtrees { get; init; } = [];       // tag serviceAccount
    public IReadOnlyList<string> DisabledAccountsSubtrees { get; init; } = [];     // tag disabledOu
    public IReadOnlyList<string> AdditionalWatchedAttributes { get; init; } = [];  // appended to user/computer sets
    public IReadOnlyList<string> IgnoredAttributes { get; init; } = [];            // removed from watched sets
    public IReadOnlyList<WatchEventKind> EnabledOptionalKinds { get; init; } = []; // kinds with RecordedByDefault=false
    public IReadOnlyList<WatchEventKind> DisabledKinds { get; init; } = [];        // never recorded (health and Tier-0 kinds rejected, 8.3)
    public bool TrackComputerPasswordChanges { get; init; }                        // machine pwdLastSet rotation
    public bool TrackManagedPasswordRollover { get; init; }                        // gMSA/MSA/dMSA automatic password rollover
    public bool TrackComputerOsChanges { get; init; }                              // operatingSystem*, dNSHostName by SELF
    public int MaxValuesPerAttribute { get; init; } = 100;                         // stored values per side of a change
    public TimeSpan FailoverAfter { get; init; } = TimeSpan.FromMinutes(10);       // pinned DC down → another DC
    /// <summary>Changes to an object younger than this (whenCreated) are folded into its Created event (10.6.3).</summary>
    public TimeSpan CreationSettleWindow { get; init; } = TimeSpan.FromMinutes(2);
}
