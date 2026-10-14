using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;

namespace AdMng.Core.Batch;

/// <summary>
/// A deleted object as read from CN=Deleted Objects: a Recycle Bin deleted-object (all attributes kept) or, in a forest
/// without the AD Recycle Bin, a tombstone (most attributes stripped). In memory only, like every directory object.
/// </summary>
public sealed record DeletedObjectInfo
{
    /// <summary>DN of the deleted object in CN=Deleted Objects (with the mangled RDN, e.g. <c>CN=Jan Kowalski\0ADEL:…</c>).</summary>
    public required string DistinguishedName { get; init; }

    /// <summary>The original RDN value (msDS-LastKnownRDN, else the RDN without the deletion marker).</summary>
    public required string Name { get; init; }

    /// <summary>The naming attribute of the RDN (CN, OU...).</summary>
    public string RdnType { get; init; } = "CN";

    /// <summary>
    /// lastKnownParent: the container the object was deleted from. When that container was deleted as well, AD returns
    /// its current DN in CN=Deleted Objects (references follow the object), see <see cref="IsParentDeleted"/>.
    /// </summary>
    public string? LastKnownParent { get; init; }

    public IReadOnlyList<string> ObjectClasses { get; init; } = [];

    public Guid? ObjectGuid { get; init; }

    /// <summary>objectSid in SDDL form for security principals.</summary>
    public string? ObjectSid { get; init; }

    public string? Description { get; init; }

    /// <summary>whenChanged - the deletion time, unless the deleted object was changed afterwards.</summary>
    public DateTimeOffset? WhenChanged { get; init; }

    /// <summary>isDeleted (false only for an object that is not deleted at all).</summary>
    public bool IsDeleted { get; init; }

    /// <summary>
    /// isRecycled. With the Recycle Bin enabled a recycled object can no longer be restored; without it every deleted object
    /// is a tombstone, which AD (and Samba) mark as recycled although it can still be reanimated.
    /// </summary>
    public bool IsRecycled { get; init; }

    public ObjectKind Kind => ObjectKinds.FromObjectClasses(ObjectClasses);

    /// <summary>True when the original parent container is a deleted object too.</summary>
    public bool IsParentDeleted => RecycleBin.IsDeletedObjectDn(LastKnownParent);

    /// <summary>The DN the object had before it was deleted, when its parent still exists (else null).</summary>
    public string? OriginalDn => !IsParentDeleted && Ldap.DistinguishedName.TryParse(LastKnownParent, out var parent) && !parent.IsRoot
        ? parent.Child(RdnType, Name).ToString()
        : null;

    /// <summary>Reads the attributes of <see cref="RecycleBin.Attributes"/> from a search result.</summary>
    public static DeletedObjectInfo From(DirectoryObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var dn = obj.Dn;
        var name = obj.GetString(Attr.MsDsLastKnownRdn) is { Length: > 0 } lastKnown
            ? lastKnown
            : RecycleBin.RemoveDeletedMarker(obj.GetString(Attr.Name) ?? dn.Name);
        return new DeletedObjectInfo
        {
            DistinguishedName = obj.DistinguishedName,
            Name = name,
            RdnType = dn.IsRoot ? "CN" : dn.Leaf.Type,
            LastKnownParent = obj.GetString(Attr.LastKnownParent),
            ObjectClasses = obj.GetStrings(Attr.ObjectClass),
            ObjectGuid = obj.ObjectGuid,
            ObjectSid = obj.ObjectSid,
            Description = obj.GetString(Attr.Description),
            WhenChanged = obj.GetGeneralizedTime(Attr.WhenChanged),
            IsDeleted = obj.GetBoolean(Attr.IsDeleted) == true,
            IsRecycled = obj.GetBoolean(Attr.IsRecycled) == true,
        };
    }
}

/// <summary>Server side filter of the deleted objects list. Every value is escaped through <see cref="LdapFilter"/>.</summary>
public sealed record DeletedObjectFilter
{
    /// <summary>
    /// Text contained in the original name (msDS-LastKnownRDN), or the start of the name, display name or logon name
    /// (tombstones have no msDS-LastKnownRDN on every DC, but their mangled name starts with the original one).
    /// </summary>
    public string? Name { get; init; }

    /// <summary>Only objects of this kind (objectClass based: deleted objects lose objectCategory and sAMAccountType).</summary>
    public ObjectKind? Kind { get; init; }

    /// <summary>Only objects deleted (whenChanged) at or after this time.</summary>
    public DateTimeOffset? DeletedAfter { get; init; }

    /// <summary>Only objects deleted from this container (lastKnownParent).</summary>
    public string? LastKnownParent { get; init; }
}

/// <summary>
/// Lifetimes of deleted objects in days, as configured on CN=Directory Service (null = not set: AD then uses its default,
/// 180 days for forests created with Windows Server 2003 SP1 or later).
/// </summary>
/// <param name="DeletedObjectLifetimeDays">msDS-deletedObjectLifetime: how long a Recycle Bin deleted-object stays restorable.</param>
/// <param name="TombstoneLifetimeDays">tombstoneLifetime: how long a tombstone (recycled object) is kept.</param>
public sealed record DeletedObjectLifetime(int? DeletedObjectLifetimeDays, int? TombstoneLifetimeDays)
{
    /// <summary>
    /// Days an object stays restorable after its deletion: with the Recycle Bin msDS-deletedObjectLifetime (which defaults
    /// to tombstoneLifetime), without it tombstoneLifetime (tombstone reanimation).
    /// </summary>
    public int? RestorableDays(bool recycleBinEnabled) =>
        recycleBinEnabled ? DeletedObjectLifetimeDays ?? TombstoneLifetimeDays : TombstoneLifetimeDays;
}

/// <summary>Options of <see cref="RecycleBin.PlanRestoreAsync"/>.</summary>
public sealed record RestorePlanningOptions
{
    /// <summary>Operator-facing title of the plan ("Restore 3 objects").</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Parallel reads while planning.</summary>
    public int MaxDegreeOfParallelism { get; init; } = 4;

    /// <summary>Clock of <see cref="ChangePlan.CreatedAt"/>.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>
/// AD Recycle Bin: listing deleted objects (CN=Deleted Objects with the Show Deleted control) and planning their restore
/// through the change pipeline (<see cref="ChangeAction.Restore"/> steps executed by <see cref="IBatchExecutor"/>).
/// </summary>
/// <remarks>
/// Unlike ADAC, a container and its children are restored in one plan: <see cref="PlanRestoreAsync"/> orders the objects
/// parent first (resolved through the lastKnownParent chains among the selected objects) and points the children at the
/// restored parent; the executor starts a child only after its parent was restored.
/// </remarks>
public static class RecycleBin
{
    /// <summary>RDN value of the Deleted Objects container (the same in every language of AD).</summary>
    public const string DeletedObjectsContainerName = "Deleted Objects";

    /// <summary>What AD appends to the RDN value of a deleted object: <c>&lt;name&gt;\nDEL:&lt;objectGUID&gt;</c> (escaped <c>\0ADEL:</c> in DNs).</summary>
    public const string DeletedNameMarker = "\nDEL:";

    /// <summary>Attributes of the deleted objects list (<see cref="DeletedObjectInfo.From"/>).</summary>
    public static IReadOnlyList<string> Attributes { get; } =
    [
        Attr.Name, Attr.MsDsLastKnownRdn, Attr.LastKnownParent, Attr.ObjectClass, Attr.WhenChanged, Attr.IsDeleted,
        Attr.IsRecycled, Attr.ObjectGuid, Attr.ObjectSid, Attr.Description,
    ];

    /// <summary>CN=Deleted Objects of a naming context.</summary>
    public static string GetDeletedObjectsContainer(string namingContextDn)
    {
        ArgumentNullException.ThrowIfNull(namingContextDn);
        return Ldap.DistinguishedName.Parse(namingContextDn).Child("CN", DeletedObjectsContainerName).ToString();
    }

    /// <summary>
    /// Reads msDS-deletedObjectLifetime and tombstoneLifetime from CN=Directory Service,CN=Windows NT,CN=Services of the
    /// configuration naming context. Null when the object cannot be read (missing, access denied, DC error).
    /// </summary>
    public static async Task<DeletedObjectLifetime?> ReadLifetimeAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!Ldap.DistinguishedName.TryParse(session.Info.ConfigurationNamingContext, out var configuration) || configuration.IsRoot)
        {
            return null;
        }

        var directoryService = configuration.Child("CN", "Services").Child("CN", "Windows NT").Child("CN", "Directory Service").ToString();
        try
        {
            var obj = await session.GetAsync(directoryService, [Attr.MsDsDeletedObjectLifetime, Attr.TombstoneLifetime], cancellationToken).ConfigureAwait(false);
            return obj is null
                ? null
                : new DeletedObjectLifetime(Positive(obj.GetInt32(Attr.MsDsDeletedObjectLifetime)), Positive(obj.GetInt32(Attr.TombstoneLifetime)));
        }
        catch (DirectoryException)
        {
            return null;
        }

        static int? Positive(int? days) => days > 0 ? days : null;
    }

    /// <summary>True when <paramref name="dn"/> names a deleted object (a mangled RDN, or below CN=Deleted Objects).</summary>
    public static bool IsDeletedObjectDn(string? dn)
    {
        if (!Ldap.DistinguishedName.TryParse(dn, out var parsed) || parsed.IsRoot)
        {
            return false;
        }

        return parsed.Rdns.Any(r => r.Value.Contains(DeletedNameMarker, StringComparison.Ordinal)
            || (string.Equals(r.Type, "CN", StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.Value, DeletedObjectsContainerName, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The original name in a mangled RDN value (<c>Jan Kowalski\nDEL:…</c> → <c>Jan Kowalski</c>).</summary>
    public static string RemoveDeletedMarker(string rdnValue)
    {
        ArgumentNullException.ThrowIfNull(rdnValue);
        var marker = rdnValue.IndexOf(DeletedNameMarker, StringComparison.Ordinal);
        return marker < 0 ? rdnValue : rdnValue[..marker];
    }

    /// <summary>The LDAP filter of the deleted objects list: <c>(isDeleted=TRUE)</c> plus the conditions of <paramref name="filter"/>.</summary>
    public static string BuildFilter(DeletedObjectFilter? filter)
    {
        var parts = new List<string> { LdapFilter.Equal(Attr.IsDeleted, "TRUE") };
        if (filter?.Kind is { } kind and not ObjectKind.Unknown)
        {
            parts.Add(KindFilter(kind));
        }

        if (filter?.Name?.Trim() is { Length: > 0 } name)
        {
            parts.Add(LdapFilter.Or(
                LdapFilter.Contains(Attr.MsDsLastKnownRdn, name),
                LdapFilter.StartsWith(Attr.Name, name),
                LdapFilter.StartsWith(Attr.DisplayName, name),
                LdapFilter.StartsWith(Attr.SamAccountName, name)));
        }

        if (filter?.DeletedAfter is { } after)
        {
            parts.Add(LdapFilter.GreaterOrEqual(Attr.WhenChanged, AdConvert.ToGeneralizedTime(after)));
        }

        if (filter?.LastKnownParent is { Length: > 0 } parent)
        {
            parts.Add(LdapFilter.Equal(Attr.LastKnownParent, parent));
        }

        return LdapFilter.And(parts);
    }

    /// <summary>
    /// The objectClass based filter of a kind. Deleted objects lose objectCategory and sAMAccountType, so the indexed
    /// filters of <see cref="ObjectKinds.ToLdapFilter"/> would match nothing.
    /// </summary>
    public static string KindFilter(ObjectKind kind) => kind switch
    {
        ObjectKind.User => LdapFilter.And(LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.User), LdapFilter.Not(LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.Computer))),
        ObjectKind.InetOrgPerson => LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.InetOrgPerson),
        ObjectKind.Computer => LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.Computer),
        ObjectKind.Group => LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.Group),
        ObjectKind.Contact => LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.Contact),
        ObjectKind.OrganizationalUnit => LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.OrganizationalUnit),
        ObjectKind.Container => LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.Container),
        ObjectKind.ManagedServiceAccount => LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.ManagedServiceAccount),
        ObjectKind.GroupManagedServiceAccount => LdapFilter.Or(
            LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.GroupManagedServiceAccount),
            LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.DelegatedManagedServiceAccount)),
        ObjectKind.ForeignSecurityPrincipal => LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.ForeignSecurityPrincipal),
        ObjectKind.PrintQueue => LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.PrintQueue),
        ObjectKind.SharedFolder => LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.Volume),
        ObjectKind.BitLockerRecovery => LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.BitLockerRecoveryInformation),
        _ => LdapFilter.Present(Attr.ObjectClass),
    };

    /// <summary>
    /// One-level search of CN=Deleted Objects of <paramref name="namingContextDn"/> with the Show Deleted control (recycled
    /// objects of a Recycle Bin forest stay hidden: they cannot be restored).
    /// </summary>
    public static DirectorySearch CreateSearch(string namingContextDn, DeletedObjectFilter? filter = null, int pageSize = 500) => new()
    {
        BaseDn = GetDeletedObjectsContainer(namingContextDn),
        Scope = SearchScope.OneLevel,
        Filter = BuildFilter(filter),
        Attributes = Attributes,
        PageSize = pageSize,
        IncludeDeleted = true,
    };

    /// <summary>
    /// Plans the restore of deleted objects. Every object is read again live (with the Show Deleted control, in a Recycle
    /// Bin forest also Show Recycled); objects whose
    /// lastKnownParent is selected too are restored below their restored parent, the others to
    /// <paramref name="targetContainerDn"/> or, when it is null, to their original container. The plan lists parents
    /// before their children.
    /// </summary>
    /// <remarks>
    /// Findings (the object is skipped): the deleted object no longer exists or is not deleted (<see cref="IssueCodes.NotFound"/>,
    /// <see cref="IssueCodes.NotApplicable"/>), it was recycled (<see cref="IssueCodes.Recycled"/>), its original container
    /// is deleted too and not selected (<see cref="IssueCodes.ParentDeleted"/>), its selected parent cannot be restored
    /// (<see cref="IssueCodes.ParentNotRestored"/>), the target container is missing (<see cref="IssueCodes.TargetNotFound"/>),
    /// an object with the same name exists in the target (<see cref="IssueCodes.AlreadyExists"/>, a live check) or another
    /// selected object gets the same name (<see cref="IssueCodes.DuplicateInBatch"/>). Without the AD Recycle Bin every object
    /// gets a <see cref="IssueCodes.TombstoneReanimation"/> warning.
    /// </remarks>
    /// <param name="session">Session of the DC the plan is executed on.</param>
    /// <param name="deletedObjectDns">DNs of the deleted objects (in CN=Deleted Objects).</param>
    /// <param name="targetContainerDn">"Restore to": the container for objects whose parent is not restored with them; null = original location.</param>
    /// <param name="options">Title, parallelism and clock.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<ChangePlan> PlanRestoreAsync(
        IDirectorySession session,
        IReadOnlyList<string> deletedObjectDns,
        string? targetContainerDn = null,
        RestorePlanningOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(deletedObjectDns);
        options ??= new RestorePlanningOptions();
        var createdAt = options.TimeProvider.GetUtcNow();
        var parallel = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(options.MaxDegreeOfParallelism, 1, 64),
            CancellationToken = cancellationToken,
        };

        var items = new List<RestoreItem>();
        var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dn in deletedObjectDns)
        {
            ArgumentNullException.ThrowIfNull(dn, nameof(deletedObjectDns));
            if (requested.Add(Key(dn)))
            {
                items.Add(new RestoreItem(dn, items.Count));
            }
        }

        // Fresh state of every object, read live (the list may be minutes old). In a Recycle Bin forest the read also sees
        // recycled objects, so an object recycled since it was listed is reported as such instead of "not found".
        var recycleBinEnabled = session.Info.RecycleBinEnabled;
        var showRecycled = recycleBinEnabled && session.Info.Supports(LdapControls.ShowRecycled);
        await Parallel.ForEachAsync(items, parallel, async (item, token) =>
            item.Current = await ReadDeletedAsync(session, item.RequestedDn, showRecycled, token).ConfigureAwait(false)).ConfigureAwait(false);

        var byDn = new Dictionary<string, RestoreItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            Classify(item, recycleBinEnabled);

            // Only deleted objects can be the restored parent of another one (a live container is an ordinary target).
            if (item.Info is { IsDeleted: true } info)
            {
                byDn.TryAdd(Key(info.DistinguishedName), item);
                byDn.TryAdd(Key(item.RequestedDn), item);
            }
        }

        foreach (var item in items)
        {
            if (item.Info?.LastKnownParent is { } parentDn && byDn.TryGetValue(Key(parentDn), out var parent) && !ReferenceEquals(parent, item))
            {
                item.Parent = parent;
            }
        }

        var ordered = items.OrderBy(Depth).ThenBy(i => i.Index).ToList();
        var target = await CheckTargetContainerAsync(session, targetContainerDn, cancellationToken).ConfigureAwait(false);
        await AssignTargetsAsync(session, ordered, targetContainerDn, target, cancellationToken).ConfigureAwait(false);

        // A live name check for every object restored into an existing container (a child of an object restored in this
        // plan goes into a container that does not exist yet).
        await Parallel.ForEachAsync(
            ordered.Where(i => i.TargetDn is not null && !i.HasErrors && i.Parent is null),
            parallel,
            async (item, token) =>
            {
                if (await session.GetAsync(item.TargetDn!, [Attr.DistinguishedName], token).ConfigureAwait(false) is not null)
                {
                    item.Issues.Add(Error(IssueCodes.AlreadyExists,
                        $"An object named '{item.Info!.Name}' already exists in '{ParentOf(item.TargetDn!)}'. Rename or move it first, or restore to another container."));
                }
            }).ConfigureAwait(false);

        // Parents come first, so one pass carries a failure down the whole chain.
        foreach (var item in ordered)
        {
            if (item.Info is not null && !item.HasErrors)
            {
                if (item.Parent is { HasErrors: true } parent)
                {
                    item.Issues.Add(Error(IssueCodes.ParentNotRestored,
                        $"The parent container '{parent.DisplayName}' cannot be restored, so this object cannot be restored either."));
                }
                else if (item.TargetDn is null)
                {
                    // Only with inconsistent lastKnownParent chains (a cycle): never restore to a guessed place.
                    item.Issues.Add(Error(IssueCodes.ParentNotRestored, "The container to restore the object into cannot be determined."));
                }
            }

            if (!recycleBinEnabled && item.Info is not null && !item.HasErrors)
            {
                item.Issues.Add(new ValidationIssue(IssueSeverity.Warning, IssueCodes.TombstoneReanimation,
                    "The AD Recycle Bin is not enabled: the object is reanimated from a tombstone and gets back only the few attributes a tombstone keeps (group memberships and most other attributes are lost)."));
            }
        }

        return new ChangePlan
        {
            Objects = [.. ordered.Select(i => i.Build())],
            Title = options.Title,
            CreatedAt = createdAt,
            Server = session.Info.Server,
        };
    }

    private static string Key(string dn) => Ldap.DistinguishedName.TryParse(dn, out var parsed) ? parsed.ToString() : dn.Trim();

    private static ValidationIssue Error(string code, string message) => new(IssueSeverity.Error, code, message);

    private static string ParentOf(string dn) =>
        Ldap.DistinguishedName.TryParse(dn, out var parsed) && parsed.Parent is { IsRoot: false } parent ? parent.ToString() : dn;

    /// <summary>Base read of a deleted object with the Show Deleted (and Show Recycled) control; null when it does not exist (any more).</summary>
    private static async Task<DirectoryObject?> ReadDeletedAsync(IDirectorySession session, string dn, bool showRecycled, CancellationToken cancellationToken)
    {
        if (!Ldap.DistinguishedName.TryParse(dn, out var parsed) || parsed.IsRoot)
        {
            return null;
        }

        var search = new DirectorySearch
        {
            BaseDn = dn,
            Scope = SearchScope.Base,
            Attributes = Attributes,
            SizeLimit = 1,
            IncludeDeleted = true,
            IncludeRecycled = showRecycled,
        };
        try
        {
            await foreach (var obj in session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
            {
                return obj;
            }
        }
        catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.NoSuchObject)
        {
            // Restored, recycled or purged meanwhile.
        }

        return null;
    }

    private static void Classify(RestoreItem item, bool recycleBinEnabled)
    {
        if (!Ldap.DistinguishedName.TryParse(item.RequestedDn, out var parsed) || parsed.IsRoot)
        {
            item.Issues.Add(Error(IssueCodes.InvalidValue, $"'{item.RequestedDn}' is not a valid distinguished name."));
            return;
        }

        if (item.Current is null)
        {
            item.Issues.Add(Error(IssueCodes.NotFound, "The deleted object does not exist any more (it was restored, recycled or purged)."));
            return;
        }

        var info = DeletedObjectInfo.From(item.Current);
        item.Info = info;
        if (!info.IsDeleted)
        {
            item.Issues.Add(Error(IssueCodes.NotApplicable, "The object is not deleted."));
        }
        else if (info.IsRecycled && recycleBinEnabled)
        {
            item.Issues.Add(Error(IssueCodes.Recycled,
                "The object was recycled (its deleted object lifetime expired) and cannot be restored any more."));
        }
    }

    /// <summary>Depth in the selection: 0 when the parent is not selected (cycle safe).</summary>
    private static int Depth(RestoreItem item)
    {
        var depth = 0;
        var visited = new HashSet<RestoreItem>(ReferenceEqualityComparer.Instance) { item };
        for (var parent = item.Parent; parent is not null && visited.Add(parent); parent = parent.Parent)
        {
            depth++;
        }

        return depth;
    }

    /// <summary>Validates the "Restore to" container: null when none was chosen or it exists, otherwise the finding for every top-level object.</summary>
    private static async Task<ValidationIssue?> CheckTargetContainerAsync(IDirectorySession session, string? targetContainerDn, CancellationToken cancellationToken)
    {
        if (targetContainerDn is null)
        {
            return null;
        }

        if (!Ldap.DistinguishedName.TryParse(targetContainerDn, out var parsed) || parsed.IsRoot || IsDeletedObjectDn(targetContainerDn))
        {
            return Error(IssueCodes.TargetNotFound, $"'{targetContainerDn}' is not a valid target container.");
        }

        var container = await session.GetAsync(targetContainerDn, [Attr.DistinguishedName], cancellationToken).ConfigureAwait(false);
        return container is null
            ? Error(IssueCodes.TargetNotFound, $"The target container '{targetContainerDn}' does not exist (any more).")
            : null;
    }

    /// <summary>Computes the target DN of every restorable object, in parent-first order.</summary>
    private static async Task AssignTargetsAsync(
        IDirectorySession session,
        IReadOnlyList<RestoreItem> ordered,
        string? targetContainerDn,
        ValidationIssue? targetIssue,
        CancellationToken cancellationToken)
    {
        var parentExists = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var targets = new Dictionary<string, RestoreItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ordered)
        {
            if (item.Info is not { } info || item.HasErrors)
            {
                continue;
            }

            Ldap.DistinguishedName? targetParent = null;
            if (item.Parent is { } parent)
            {
                // Below the restored parent - also for "Restore to": the subtree keeps its structure.
                if (parent.TargetDn is not null)
                {
                    targetParent = Ldap.DistinguishedName.Parse(parent.TargetDn);
                }
            }
            else if (targetContainerDn is not null)
            {
                if (targetIssue is not null)
                {
                    item.Issues.Add(targetIssue);
                    continue;
                }

                targetParent = Ldap.DistinguishedName.Parse(targetContainerDn);
            }
            else if (info.LastKnownParent is not { } lastKnownParent || !Ldap.DistinguishedName.TryParse(lastKnownParent, out var original) || original.IsRoot)
            {
                item.Issues.Add(Error(IssueCodes.ParentDeleted, "The original location of the object is unknown. Restore it to another container."));
                continue;
            }
            else if (info.IsParentDeleted)
            {
                item.Issues.Add(Error(IssueCodes.ParentDeleted,
                    $"The original container '{RemoveDeletedMarker(original.Name)}' is deleted as well. Select it for restore too, or restore the object to another container."));
                continue;
            }
            else
            {
                var key = Key(lastKnownParent);
                if (!parentExists.TryGetValue(key, out var exists))
                {
                    exists = await session.GetAsync(lastKnownParent, [Attr.DistinguishedName], cancellationToken).ConfigureAwait(false) is not null;
                    parentExists[key] = exists;
                }

                if (!exists)
                {
                    item.Issues.Add(Error(IssueCodes.ParentDeleted,
                        $"The original container '{lastKnownParent}' does not exist any more. Restore the object to another container."));
                    continue;
                }

                targetParent = original;
            }

            if (targetParent is null)
            {
                // The selected parent has no target (its own findings say why); reported in the final pass.
                continue;
            }

            var targetDn = targetParent.Child(info.RdnType, info.Name).ToString();
            item.TargetDn = targetDn;
            if (!targets.TryAdd(Key(targetDn), item))
            {
                item.Issues.Add(new ValidationIssue(IssueSeverity.Error, IssueCodes.DuplicateInBatch,
                    $"Another selected object is restored as '{info.Name}' into '{targetParent}'.", Attr.Name));
            }
        }
    }

    /// <summary>Planning state of one selected deleted object.</summary>
    private sealed class RestoreItem(string requestedDn, int index)
    {
        public string RequestedDn { get; } = requestedDn;

        public int Index { get; } = index;

        public DirectoryObject? Current { get; set; }

        public DeletedObjectInfo? Info { get; set; }

        /// <summary>The selected object that is the lastKnownParent of this one (restored first).</summary>
        public RestoreItem? Parent { get; set; }

        public string? TargetDn { get; set; }

        public List<ValidationIssue> Issues { get; } = [];

        public bool HasErrors => Issues.Any(i => i.Severity == IssueSeverity.Error);

        public string DisplayName => Info?.Name
            ?? (Ldap.DistinguishedName.TryParse(RequestedDn, out var dn) && !dn.IsRoot ? RemoveDeletedMarker(dn.Name) : RequestedDn);

        public ObjectPlan Build() => new()
        {
            DistinguishedName = Info?.DistinguishedName ?? RequestedDn,
            DisplayName = DisplayName,
            Kind = Info?.Kind ?? ObjectKind.Unknown,
            ObjectClasses = Info?.ObjectClasses ?? [],
            ObjectGuid = Info?.ObjectGuid,
            Changes = TargetDn is null ? [] : [new PlannedChange { Action = ChangeAction.Restore, TargetDn = TargetDn }],
            Issues = [.. Issues],
        };
    }
}
