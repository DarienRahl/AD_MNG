using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Objects outside the default NC (sections 9.1, 10.11), with the same watermark (USNs are per DC across all its NCs):
/// domain controllers (nTDSDSA – added / removed, other-domain DCs tagged), the forest singletons (Partitions,
/// Directory Service, schema head – FSMO owners, Recycle Bin, dSHeuristics, tombstoneLifetime), PKI objects
/// (templates, enrollment services, CAs/NTAuth) and schema increments (one SchemaChanged per cycle with counts and up
/// to 50 names). Incremental: changes with <c>W' ≤ uSNChanged ≤ H</c> (later writes belong to the next cycle); full sync:
/// everything, plus deletions of known DCs and PKI objects. No events at Baseline. SchemaChanged has no snapshot row that
/// would make a repeated cycle harmless, so it is emitted in the cycle's final commit, together with W.
/// </summary>
internal sealed class ConfigurationPartitionWatcher
{
    private const int MaxSchemaNames = 50;

    private static readonly string[] PkiClasses =
    [
        WatchdogObjectClasses.CertificateTemplate, WatchdogObjectClasses.EnrollmentService, WatchdogObjectClasses.CertificationAuthority,
    ];

    private readonly DetectionServices _services;

    public ConfigurationPartitionWatcher(DetectionServices services)
    {
        _services = services;
    }

    /// <summary>Runs the watcher for this cycle (<paramref name="fullSync"/> at Baseline, Resync and Reconciliation).</summary>
    public async Task RunAsync(ObjectProcessor processor, DetectionCycle cycle, bool fullSync, CancellationToken cancellationToken)
    {
        var directory = cycle.Settings.Directory;
        if (directory.WatchConfigurationPartition)
        {
            await DomainControllersAsync(processor, cycle, fullSync, cancellationToken).ConfigureAwait(false);
            await SingletonsAsync(processor, cycle, fullSync, cancellationToken).ConfigureAwait(false);
            await PublicKeyInfrastructureAsync(processor, cycle, fullSync, cancellationToken).ConfigureAwait(false);
        }

        if (directory.WatchSchemaPartition && cycle.Mode is not (DetectionMode.Baseline or DetectionMode.Resync))
        {
            await SchemaAsync(processor, cycle, cancellationToken).ConfigureAwait(false);
        }

        await cycle.CommitChunkAsync(_services.Store, cancellationToken).ConfigureAwait(false);
    }

    private async Task DomainControllersAsync(ObjectProcessor processor, DetectionCycle cycle, bool fullSync, CancellationToken cancellationToken)
    {
        var known = (await _services.Store.GetByKindAsync(WatchObjectKind.DomainController, cancellationToken).ConfigureAwait(false))
            .ToDictionary(s => s.ObjectGuid);
        var sites = DistinguishedName.Parse(cycle.Settings.Facts.ConfigurationDn).Child("CN", "Sites").ToString();
        var filter = fullSync
            ? LdapFilter.Equal(Attr.ObjectClass, WatchdogObjectClasses.NtdsDsa)
            : LdapFilter.And(LdapFilter.Equal(Attr.ObjectClass, WatchdogObjectClasses.NtdsDsa), Changed(cycle));
        var changed = new List<DirectoryObject>();
        await foreach (var entry in cycle.Session.SearchAsync(new DirectorySearch
        {
            BaseDn = sites,
            Filter = filter,
            Attributes = [Attr.ObjectGuid, Attr.ObjectClass, Attr.UsnCreated, Attr.UsnChanged, Attr.WhenChanged, WatchdogAttributes.InvocationId, Attr.ObjectCategory, WatchdogAttributes.HasDomainNcs],
            PageSize = 1000,
        }, cancellationToken).ConfigureAwait(false))
        {
            if (entry.ObjectGuid is not null)
            {
                changed.Add(entry);
            }
        }

        var refreshedBefore = _services.DomainControllers.RefreshedAt;
        if (fullSync || changed.Count > 0 || refreshedBefore is null)
        {
            await _services.DomainControllers.RefreshAsync(cycle.Session, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _services.DomainControllers.EnsureFreshAsync(cycle.Session, cancellationToken).ConfigureAwait(false);
        }

        var all = _services.DomainControllers.All;
        foreach (var entry in changed)
        {
            var guid = entry.ObjectGuid!.Value;
            var dc = all.FirstOrDefault(d => d.ObjectGuid == guid);
            var row = DcRow(entry, dc, processor);
            if (!known.ContainsKey(guid))
            {
                var tags = dc is { IsThisDomain: false } ? [EventTags.OtherDomain] : Array.Empty<string>();
                var draft = new EventDraft { Kind = WatchEventKind.DomainControllerAdded, ExtraTags = tags, MetadataAttributes = [Attr.ObjectClass] };
                processor.Emit([draft], row, DcRef(row, dc), ReplicationMetadata.Empty, entry.GetGeneralizedTime(Attr.WhenChanged));
            }

            known[guid] = row;
            cycle.Chunk.Upsert(row);
        }

        if (_services.DomainControllers.RefreshedAt != refreshedBefore || fullSync)
        {
            var present = all.Select(d => d.ObjectGuid).ToHashSet();
            foreach (var row in known.Values.Where(r => !present.Contains(r.ObjectGuid)))
            {
                var otherDomain = row.Attributes.TryGetValue(WatchdogAttributes.HasDomainNcs, out var ncs)
                    && !AttributeChangeBuilder.Display(ncs).Any(nc => DistinguishedName.AreEqual(nc, cycle.Settings.Facts.DomainDn));
                var draft = new EventDraft { Kind = WatchEventKind.DomainControllerRemoved, ExtraTags = otherDomain ? [EventTags.OtherDomain] : [] };
                processor.Emit([draft], row, DcRef(row, null), ReplicationMetadata.Empty, null);
                cycle.Chunk.Remove(row.ObjectGuid);
            }
        }
    }

    private SnapshotObject DcRow(DirectoryObject entry, KnownDomainController? dc, ObjectProcessor processor)
    {
        var attributes = new Dictionary<string, SnapshotAttribute>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { WatchdogAttributes.InvocationId, Attr.ObjectCategory, WatchdogAttributes.HasDomainNcs })
        {
            if (entry.TryGet(name, out var values) && processor.Canonicalizer.Canonicalize(name, values) is { } attribute)
            {
                attributes[name] = attribute;
            }
        }

        if (dc?.HostName is { } host)
        {
            attributes[Attr.DnsHostName] = new SnapshotAttribute { Values = [host], Kind = AttributeValueKind.Text };
        }

        return new SnapshotObject
        {
            ObjectGuid = entry.ObjectGuid!.Value,
            Dn = entry.DistinguishedName,
            Kind = WatchObjectKind.DomainController,
            ObjectClasses = entry.GetStrings(Attr.ObjectClass),
            Name = dc?.HostName ?? entry.Dn.Parent?.Name,
            UsnCreated = entry.GetInt64(Attr.UsnCreated) ?? 0,
            UsnChanged = entry.GetInt64(Attr.UsnChanged) ?? 0,
            Attributes = attributes,
        };
    }

    private static ObjectRef DcRef(SnapshotObject row, KnownDomainController? dc)
    {
        var server = DistinguishedName.TryParse(row.Dn, out var dn) && dn.Parent is { } parent ? parent.ToString() : row.Dn;
        return new ObjectRef
        {
            ObjectGuid = row.ObjectGuid,
            Dn = server,
            Name = dc?.HostName ?? row.Name,
            Kind = WatchObjectKind.DomainController,
        };
    }

    private async Task SingletonsAsync(ObjectProcessor processor, DetectionCycle cycle, bool fullSync, CancellationToken cancellationToken)
    {
        var configuration = DistinguishedName.Parse(cycle.Settings.Facts.ConfigurationDn);
        string[] dns =
        [
            configuration.Child("CN", "Partitions").ToString(),
            configuration.Child("CN", "Services").Child("CN", "Windows NT").Child("CN", "Directory Service").ToString(),
            cycle.Settings.Facts.SchemaDn,
        ];
        var watched = cycle.Settings.Watched(WatchObjectKind.Configuration);
        var attributes = WatchedAttributes.Identity.Concat(watched).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var dn in dns)
        {
            var entry = await TryGetAsync(cycle, dn, attributes, cancellationToken).ConfigureAwait(false);
            if (entry?.ObjectGuid is not { } guid || (!fullSync && (entry.GetInt64(Attr.UsnChanged) ?? 0) < cycle.SearchFrom))
            {
                continue;
            }

            var stored = await _services.Store.GetSnapshotsAsync([guid], cancellationToken).ConfigureAwait(false);
            await DiffAndEmitAsync(processor, cycle, entry, WatchObjectKind.Configuration, watched, stored.GetValueOrDefault(guid), reportNew: false, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task PublicKeyInfrastructureAsync(ObjectProcessor processor, DetectionCycle cycle, bool fullSync, CancellationToken cancellationToken)
    {
        var services = DistinguishedName.Parse(cycle.Settings.Facts.ConfigurationDn).Child("CN", "Services").Child("CN", "Public Key Services").ToString();
        var classes = LdapFilter.Or(PkiClasses.Select(c => LdapFilter.Equal(Attr.ObjectClass, c)));
        var watched = cycle.Settings.Watched(WatchObjectKind.PublicKeyInfrastructure);
        var known = (await _services.Store.GetByKindAsync(WatchObjectKind.PublicKeyInfrastructure, cancellationToken).ConfigureAwait(false))
            .ToDictionary(s => s.ObjectGuid);
        var seen = new HashSet<Guid>();
        try
        {
            await foreach (var entry in cycle.Session.SearchAsync(new DirectorySearch
            {
                BaseDn = services,
                Filter = fullSync ? classes : LdapFilter.And(Changed(cycle), classes),
                Attributes = [.. WatchedAttributes.Identity, .. watched],
                ReadSecurityDescriptor = true,
                ExtendedDn = true,
                PageSize = 1000,
            }, cancellationToken).ConfigureAwait(false))
            {
                if (entry.ObjectGuid is not { } guid)
                {
                    continue;
                }

                seen.Add(guid);
                await DiffAndEmitAsync(processor, cycle, entry, WatchObjectKind.PublicKeyInfrastructure, watched, known.GetValueOrDefault(guid), reportNew: true, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (DirectoryException ex) when (ex.Kind is DirectoryErrorKind.NoSuchObject or DirectoryErrorKind.AccessDenied)
        {
            return;
        }

        if (!fullSync)
        {
            return;
        }

        foreach (var row in known.Values.Where(r => !seen.Contains(r.ObjectGuid)))
        {
            var draft = new EventDraft { Kind = WatchEventKind.CertificateTemplateChanged, Changes = [processor.Changes.Text("operation", ["deleted"])] };
            processor.Emit([draft], row, row.ToRef(), ReplicationMetadata.Empty, null);
            cycle.Chunk.Remove(row.ObjectGuid);
        }
    }

    private async Task DiffAndEmitAsync(ObjectProcessor processor, DetectionCycle cycle, DirectoryObject entry, WatchObjectKind kind, IReadOnlySet<string> watched,
        SnapshotObject? old, bool reportNew, CancellationToken cancellationToken)
    {
        var current = processor.Builder.Build(entry, kind, watched, old, cycle.Now)!;
        if (old is null)
        {
            cycle.Chunk.Upsert(current);
            if (reportNew)
            {
                var changes = new List<AttributeChange> { processor.Changes.Text("operation", ["created"]) };
                changes.AddRange(current.Attributes.Where(a => !WatchSignature.IsReserved(a.Key))
                    .OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(a => processor.Changes.Build(a.Key, null, a.Value)));
                var draft = new EventDraft { Kind = WatchEventKind.CertificateTemplateChanged, Changes = changes, MetadataAttributes = [Attr.ObjectClass] };
                processor.Emit([draft], current, current.ToRef(), ReplicationMetadata.Empty, entry.GetGeneralizedTime(Attr.WhenChanged));
            }

            return;
        }

        var diff = _services.Differ.Diff(old, old.Dn, current, watched);
        if (diff.IsEmpty)
        {
            return;
        }

        var metadata = await processor.ReadMetadataAsync(entry.DistinguishedName, cancellationToken).ConfigureAwait(false);
        current = SnapshotBuilder.ApplyMetadata(current, metadata);
        cycle.Chunk.Upsert(current);
        IReadOnlyList<EventDraft> drafts = kind == WatchObjectKind.PublicKeyInfrastructure
            ? [new EventDraft
            {
                Kind = WatchEventKind.CertificateTemplateChanged,
                Changes = diff.Attributes.Select(processor.Changes.FromDelta).ToList(),
                MetadataAttributes = diff.Attributes.Select(a => a.Attribute).ToList(),
            }]
            : processor.Classifier.Classify(new ClassificationInput
            {
                Diff = diff with { Moved = false, Renamed = false },
                Directory = cycle.Settings.Directory,
                Now = cycle.Now,
                Metadata = metadata,
                Watermark = cycle.MetadataWatermark,
                DsaHostName = _services.DomainControllers.ResolveHostByDsaDn,
            });
        processor.Emit(drafts, current, current.ToRef(), metadata, entry.GetGeneralizedTime(Attr.WhenChanged));
    }

    private async Task SchemaAsync(ObjectProcessor processor, DetectionCycle cycle, CancellationToken cancellationToken)
    {
        var created = new List<string>();
        var modified = new List<string>();
        try
        {
            await foreach (var entry in cycle.Session.SearchAsync(new DirectorySearch
            {
                BaseDn = cycle.Settings.Facts.SchemaDn,
                Scope = SearchScope.OneLevel,
                Filter = LdapFilter.And(
                    Changed(cycle),
                    LdapFilter.Or(LdapFilter.Equal(Attr.ObjectClass, WatchdogObjectClasses.AttributeSchema), LdapFilter.Equal(Attr.ObjectClass, WatchdogObjectClasses.ClassSchema))),
                Attributes = [WatchdogAttributes.LdapDisplayName, Attr.UsnCreated, Attr.UsnChanged, Attr.ObjectClass],
                PageSize = 1000,
            }, cancellationToken).ConfigureAwait(false))
            {
                // The UsnOverlap re-read (W' ≤ uSNChanged ≤ W) was reported by the previous cycle: no snapshot row makes
                // a second look harmless here.
                if ((entry.GetInt64(Attr.UsnChanged) ?? long.MaxValue) <= cycle.Decision.Watermark)
                {
                    continue;
                }

                var name = entry.GetString(WatchdogAttributes.LdapDisplayName) ?? entry.Dn.Name;
                ((entry.GetInt64(Attr.UsnCreated) ?? 0) > cycle.Decision.Watermark ? created : modified).Add(name);
            }
        }
        catch (DirectoryException ex) when (ex.Kind is DirectoryErrorKind.NoSuchObject or DirectoryErrorKind.AccessDenied)
        {
            return;
        }

        if (created.Count + modified.Count == 0)
        {
            return;
        }

        var names = created.Concat(modified).Order(StringComparer.OrdinalIgnoreCase).Take(MaxSchemaNames).ToList();
        var draft = new EventDraft
        {
            Kind = WatchEventKind.SchemaChanged,
            Changes =
            [
                processor.Changes.Text("created", [created.Count.ToString(CultureInfo.InvariantCulture)]) with { ValueKind = AttributeValueKind.Number },
                processor.Changes.Text("modified", [modified.Count.ToString(CultureInfo.InvariantCulture)]) with { ValueKind = AttributeValueKind.Number },
                processor.Changes.Text(WatchdogAttributes.LdapDisplayName, names) with { Truncated = created.Count + modified.Count > names.Count },
            ],
        };
        var schema = new ObjectRef { Dn = cycle.Settings.Facts.SchemaDn, Name = "Schema", Kind = WatchObjectKind.Schema };
        cycle.FinalDrafts.Add((draft, schema));
    }

    /// <summary><c>(&amp;(uSNChanged&gt;=W')(uSNChanged&lt;=H))</c>: the USN range of this cycle.</summary>
    private static string Changed(DetectionCycle cycle) =>
        LdapFilter.And(LdapFilter.GreaterOrEqual(Attr.UsnChanged, cycle.SearchFrom), LdapFilter.LessOrEqual(Attr.UsnChanged, cycle.Dc.HighestUsn));

    private static async Task<DirectoryObject?> TryGetAsync(DetectionCycle cycle, string dn, IReadOnlyList<string> attributes, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var entry in cycle.Session.SearchAsync(new DirectorySearch
            {
                BaseDn = dn,
                Scope = SearchScope.Base,
                Attributes = attributes,
                ExtendedDn = true,
            }, cancellationToken).ConfigureAwait(false))
            {
                return entry;
            }
        }
        catch (DirectoryException ex) when (ex.Kind is DirectoryErrorKind.NoSuchObject or DirectoryErrorKind.AccessDenied)
        {
            return null;
        }

        return null;
    }
}
