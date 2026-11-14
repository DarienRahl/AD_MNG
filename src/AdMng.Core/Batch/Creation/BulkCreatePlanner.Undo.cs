using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;

namespace AdMng.Core.Batch.Creation;

// ReadCreatedAsync and PlanUndoAsync (docs/dev/phase3-bulk-create.md 2.5): what this wizard created (GUIDs in memory only) and
// the undo of all of it – every object checked by its objectGUID first, deleted where it is now, never another object.
public sealed partial class BulkCreatePlanner
{
    public async Task<IReadOnlyList<CreatedObject>> ReadCreatedAsync(IDirectorySession session, BatchResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(result);
        var created = result.Objects
            .Where(o => o.Changes.Any(c => c.Succeeded && c.Change.Action == ChangeAction.Create))
            .ToList();
        var found = new Dictionary<string, DirectoryObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var byParent in created.GroupBy(o => ParentOf(o.FinalDn), StringComparer.OrdinalIgnoreCase))
        {
            if (byParent.Key.Length == 0)
            {
                continue;
            }

            foreach (var batch in byParent.Chunk(LookupBatchSize))
            {
                var search = new DirectorySearch
                {
                    BaseDn = byParent.Key,
                    Scope = SearchScope.OneLevel,
                    Filter = LdapFilter.Or(batch.Select(o => LdapFilter.Equal(Attr.DistinguishedName, o.FinalDn))),
                    Attributes = [Attr.ObjectGuid, Attr.WhenCreated],
                };
                await foreach (var entry in session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
                {
                    found[DnKey(entry.DistinguishedName)] = entry;
                }
            }
        }

        return [.. created.Select(o =>
        {
            found.TryGetValue(DnKey(o.FinalDn), out var entry);
            var mustChange = o.Changes.Any(c => c.Succeeded && c.Change.Action == ChangeAction.Modify
                && c.Change.Attributes.Any(a => Is(a.Attribute, Attr.PwdLastSet) && a.NewValues is ["0"]));
            return new CreatedObject(o.Plan.SourceRow ?? 0, entry?.DistinguishedName ?? o.FinalDn, o.Plan.Kind, entry?.ObjectGuid,
                entry?.GetGeneralizedTime(Attr.WhenCreated), mustChange);
        })];
    }

    public async Task<ChangePlan> PlanUndoAsync(IDirectorySession session, IReadOnlyList<CreatedObject> created, string title, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(created);
        ArgumentNullException.ThrowIfNull(title);
        var current = await FindByGuidAsync(session, created, cancellationToken).ConfigureAwait(false);
        var withChildren = await FindParentsOfChildrenAsync(session, created, current, cancellationToken).ConfigureAwait(false);
        var objects = new List<ObjectPlan>(created.Count);
        foreach (var item in created)
        {
            var entry = item.ObjectGuid is { } guid && current.TryGetValue(guid, out var now) ? now : null;
            var issues = new List<ValidationIssue>();
            if (entry is null)
            {
                issues.Add(Error(BulkCreateIssueCodes.UndoObjectReplaced, "The created object is gone or another object holds its name; it is left alone."));
            }
            else
            {
                if (IsInUse(item, entry))
                {
                    issues.Add(Warning(BulkCreateIssueCodes.UndoAccountInUse, "The account was used since it was created."));
                }

                if (withChildren.Contains(DnKey(entry.DistinguishedName)))
                {
                    issues.Add(Warning(BulkCreateIssueCodes.UndoHasChildren, "The object has child objects; deleting it will fail."));
                }
            }

            var dn = entry?.DistinguishedName ?? item.DistinguishedName;
            objects.Add(new ObjectPlan
            {
                DistinguishedName = dn,
                DisplayName = Ldap.DistinguishedName.TryParse(dn, out var parsed) && !parsed.IsRoot ? parsed.Name : dn,
                Kind = item.Kind,
                ObjectClasses = CreationNames.ClassChainOf(item.Kind),
                ObjectGuid = item.ObjectGuid,
                SourceRow = item.Row,
                Changes = [new PlannedChange { Action = ChangeAction.Delete }],
                Issues = issues,
            });
        }

        return new ChangePlan
        {
            Objects = objects,
            Title = title,
            CreatedAt = _time.GetUtcNow(),
            Server = session.Info.Server,
            Sequential = false,
        };
    }

    private static readonly string[] UndoAttributes =
        [Attr.ObjectGuid, Attr.ObjectClass, Attr.LastLogonTimestamp, Attr.LogonCount, Attr.PwdLastSet];

    /// <summary>
    /// The created objects by objectGUID: batched in the container they were created in, then a domain subtree search for those
    /// not found there (renamed or moved objects are deleted where they are now).
    /// </summary>
    private static async Task<Dictionary<Guid, DirectoryObject>> FindByGuidAsync(IDirectorySession session, IReadOnlyList<CreatedObject> created, CancellationToken cancellationToken)
    {
        var found = new Dictionary<Guid, DirectoryObject>();
        foreach (var byParent in created.Where(c => c.ObjectGuid is not null).GroupBy(c => ParentOf(c.DistinguishedName), StringComparer.OrdinalIgnoreCase))
        {
            if (byParent.Key.Length == 0)
            {
                continue;
            }

            foreach (var batch in byParent.Chunk(LookupBatchSize))
            {
                await SearchGuidsAsync(session, byParent.Key, SearchScope.OneLevel, batch, found, cancellationToken).ConfigureAwait(false);
            }
        }

        var missing = created.Where(c => c.ObjectGuid is { } guid && !found.ContainsKey(guid)).ToList();
        foreach (var batch in missing.Chunk(LookupBatchSize))
        {
            await SearchGuidsAsync(session, session.Info.DefaultNamingContext, SearchScope.Subtree, batch, found, cancellationToken).ConfigureAwait(false);
        }

        return found;
    }

    private static async Task SearchGuidsAsync(
        IDirectorySession session,
        string baseDn,
        SearchScope scope,
        IEnumerable<CreatedObject> batch,
        Dictionary<Guid, DirectoryObject> found,
        CancellationToken cancellationToken)
    {
        var search = new DirectorySearch
        {
            BaseDn = baseDn,
            Scope = scope,
            Filter = LdapFilter.Or(batch.Select(c => LdapFilter.EqualGuid(Attr.ObjectGuid, c.ObjectGuid!.Value))),
            Attributes = UndoAttributes,
        };
        try
        {
            await foreach (var entry in session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
            {
                if (entry.ObjectGuid is { } guid)
                {
                    found[guid] = entry;
                }
            }
        }
        catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.NoSuchObject)
        {
            // The container is gone: the objects are looked for in the whole domain.
        }
    }

    /// <summary>
    /// The objects that have children: one subtree search of each container the objects are in now (DNs only), so 5 000
    /// objects do not need 5 000 searches (2.5).
    /// </summary>
    private static async Task<HashSet<string>> FindParentsOfChildrenAsync(
        IDirectorySession session,
        IReadOnlyList<CreatedObject> created,
        Dictionary<Guid, DirectoryObject> current,
        CancellationToken cancellationToken)
    {
        var objects = created.Where(c => c.ObjectGuid is { } guid && current.ContainsKey(guid))
            .Select(c => current[c.ObjectGuid!.Value].DistinguishedName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keys = objects.Select(DnKey).ToHashSet(StringComparer.Ordinal);
        var parents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var container in objects.Select(ParentOf).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var search = new DirectorySearch { BaseDn = container, Scope = SearchScope.Subtree, Attributes = [Attr.DistinguishedName], PageSize = 1000 };
            await foreach (var entry in session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
            {
                if (ParentOf(entry.DistinguishedName) is { Length: > 0 } parent && keys.Contains(DnKey(parent)))
                {
                    parents.Add(DnKey(parent));
                }
            }
        }

        return parents;
    }

    /// <summary>A logon since the creation, or a password that is no longer the must-change 0 (2.5).</summary>
    private static bool IsInUse(CreatedObject item, DirectoryObject entry) =>
        entry.GetInt64(Attr.LastLogonTimestamp) is > 0
        || entry.GetInt64(Attr.LogonCount) is > 0
        || (item.MustChangeSet && entry.GetInt64(Attr.PwdLastSet) is { } pwdLastSet && pwdLastSet != 0);

    private static string ParentOf(string dn) =>
        Ldap.DistinguishedName.TryParse(dn, out var parsed) && parsed.Parent is { IsRoot: false } parent ? parent.ToString() : string.Empty;

    private static string DnKey(string dn) => Planning.PlanningContext.Key(dn).ToUpperInvariant();
}
