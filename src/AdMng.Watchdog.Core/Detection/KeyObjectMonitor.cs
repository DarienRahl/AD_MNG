using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// The per-cycle checks of the key objects (stage B design 9.2 and 9.4), after the object passes:
/// <list type="bullet">
/// <item>health <c>domain.machineAccountQuota</c> – Warning while <c>ms-DS-MachineAccountQuota</c> of the domain head is
/// above 0 (any authenticated principal, also the service account with a stolen keytab, can create computers); read in the
/// first cycle and in every cycle with changes (an idle cycle, H = W, cannot have changed it);</item>
/// <item>audit settings (SACL) – the service cannot read a SACL, but every write of <c>nTSecurityDescriptor</c> bumps its
/// replication metadata. For the domain head, <c>CN=AdminSDHolder,CN=System</c>, <c>OU=Domain Controllers</c>,
/// <c>CN=Public Key Services,CN=Services,{config}</c> and <c>CN=Sites,{config}</c> the metadata entry is read every cycle
/// with changes; a write inside this cycle's USN window (W &lt; local USN ≤ the DC's highest committed USN at the start
/// of the cycle) for which no SecurityDescriptorChanged of the owner/DACL summary was emitted is reported as
/// SecurityDescriptorChanged (Critical, rule <c>tier0-security</c>) with tag <c>auditSettings</c> and one change row
/// without values (the formatters show „audit list (SACL) or another security setting changed”); a rewrite of an
/// unchanged descriptor bumps the version too and is reported the same way. The stored watermark W is the state: the
/// event is committed with the new W, so a failed cycle repeats the window and a committed one never reports the write
/// again (UsnOverlap is not applied – it would re-report writes before W). Baseline and Resync (another DC's USN space)
/// report nothing; the two configuration containers have no stored owner/DACL summary, so any write there is reported
/// this way.</item>
/// </list>
/// The commit that stores the event raises EventsStored, which also runs the attribution engine's coverage check at once.
/// </summary>
internal sealed class KeyObjectMonitor
{
    private readonly IHealthRegistry _health;
    private readonly TimeProvider _time;
    private bool _quotaRead;

    public KeyObjectMonitor(IHealthRegistry health, TimeProvider time)
    {
        _health = health;
        _time = time;
    }

    /// <summary>The key objects of 9.4 with the object kind used when the store has no snapshot of them.</summary>
    public static IReadOnlyList<(string Dn, WatchObjectKind Kind)> KeyObjects(DirectoryFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var domain = DistinguishedName.Parse(facts.DomainDn);
        var configuration = DistinguishedName.Parse(facts.ConfigurationDn);
        return
        [
            (domain.ToString(), WatchObjectKind.Domain),
            (domain.Child("CN", "System").Child("CN", "AdminSDHolder").ToString(), WatchObjectKind.Container),
            (domain.Child("OU", "Domain Controllers").ToString(), WatchObjectKind.OrganizationalUnit),
            (configuration.Child("CN", "Services").Child("CN", "Public Key Services").ToString(), WatchObjectKind.PublicKeyInfrastructure),
            (configuration.Child("CN", "Sites").ToString(), WatchObjectKind.Configuration),
        ];
    }

    public async Task RunAsync(ObjectProcessor processor, DetectionCycle cycle, IDetectionStore store, CancellationToken cancellationToken)
    {
        if (!_quotaRead || cycle.Mode != DetectionMode.NoChange)
        {
            _quotaRead = await ReportMachineAccountQuotaAsync(cycle, cancellationToken).ConfigureAwait(false);
        }

        if (cycle.MetadataWatermark is not { } watermark || cycle.Mode == DetectionMode.NoChange)
        {
            return;
        }

        foreach (var (dn, kind) in KeyObjects(cycle.Settings.Facts))
        {
            var metadata = await processor.ReadMetadataAsync(dn, cancellationToken).ConfigureAwait(false);
            if (metadata.Get(Attr.NtSecurityDescriptor) is not { } descriptor
                || descriptor.UsnLocal <= watermark
                || descriptor.UsnLocal > cycle.Dc.HighestUsn
                || DescriptorChangeEmitted(cycle, dn))
            {
                continue;                     // no write in this window, or an owner/DACL change reported by the object pass
            }

            var subject = await store.FindByDnAsync(dn, cancellationToken).ConfigureAwait(false);
            var objectRef = subject is { IsDeleted: false } ? subject.ToRef() : await ReferenceAsync(cycle, dn, kind, cancellationToken).ConfigureAwait(false);
            var draft = new EventDraft
            {
                Kind = WatchEventKind.SecurityDescriptorChanged,
                Changes = [new AttributeChange { Attribute = Attr.NtSecurityDescriptor, ValueKind = AttributeValueKind.Text }],
                ExtraTags = [EventTags.AuditSettings],
                MetadataAttributes = [Attr.NtSecurityDescriptor],
            };
            processor.Emit([draft], subject is { IsDeleted: false } ? subject : null, objectRef, metadata, descriptor.OriginatingTime);
        }
    }

    /// <summary>True when this cycle already emitted a SecurityDescriptorChanged (owner/DACL) for <paramref name="dn"/>.</summary>
    private static bool DescriptorChangeEmitted(DetectionCycle cycle, string dn) =>
        cycle.StoredEvents.Concat(cycle.Chunk.Events)
            .Any(e => e.Kind == WatchEventKind.SecurityDescriptorChanged && DistinguishedName.AreEqual(e.Object.Dn, dn));

    private static async Task<ObjectRef> ReferenceAsync(DetectionCycle cycle, string dn, WatchObjectKind kind, CancellationToken cancellationToken)
    {
        Guid? guid = null;
        try
        {
            guid = (await cycle.Session.GetAsync(dn, [Attr.ObjectGuid], cancellationToken).ConfigureAwait(false))?.ObjectGuid;
        }
        catch (DirectoryException ex) when (!ex.IsTransient)
        {
            // the reference stays without a GUID
        }

        var parsed = DistinguishedName.Parse(dn);
        return new ObjectRef { ObjectGuid = guid, Dn = dn, Name = parsed.IsRoot ? dn : parsed.Name, Kind = kind };
    }

    /// <summary>Reports the quota health; false when it could not be read.</summary>
    private async Task<bool> ReportMachineAccountQuotaAsync(DetectionCycle cycle, CancellationToken cancellationToken)
    {
        int? quota;
        try
        {
            var head = await cycle.Session.GetAsync(cycle.Settings.Facts.DomainDn, [WatchdogAttributes.MachineAccountQuota], cancellationToken).ConfigureAwait(false);
            quota = head?.GetInt32(WatchdogAttributes.MachineAccountQuota);
        }
        catch (DirectoryException ex) when (!ex.IsTransient)
        {
            return false;
        }

        if (quota is not { } value)
        {
            return false;
        }

        _health.Report(new HealthCheckDto
        {
            Code = HealthCheckCodes.DomainMachineAccountQuota,
            State = value > 0 ? HealthState.Warning : HealthState.Ok,
            Detail = value > 0
                ? "ms-DS-MachineAccountQuota is above 0: every authenticated principal can create computer accounts."
                : "ms-DS-MachineAccountQuota is 0.",
            Args = [value.ToString(CultureInfo.InvariantCulture)],
            CheckedAt = _time.GetUtcNow(),
        });
        return true;
    }
}
