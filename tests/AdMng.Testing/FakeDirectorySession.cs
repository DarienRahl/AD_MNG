using System.Collections;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Schema;

namespace AdMng.Testing;

/// <summary>
/// In-memory <see cref="IDirectorySession"/> for unit tests of planners, executors and view models. It follows real
/// LDAP / Active Directory semantics closely enough not to hide bugs of the real implementation:
/// <list type="bullet">
/// <item>DNs are case-insensitive and normalized; every call validates its DN.</item>
/// <item>Modify requests are atomic and validated against the schema (unknown, system-only, constructed and back-link
/// attributes, naming attributes, single-valued, ranges, syntaxes, allowed and mandatory attributes, DN references).
/// Adding an existing value fails with AttributeOrValueExists and deleting a missing value with NoSuchAttribute unless
/// the permissive modify control is used. pwdLastSet only accepts 0/-1 and lockoutTime only 0.</item>
/// <item>Linked attributes: back links (memberOf, directReports) are computed from forward links (member, manager);
/// renames/moves update DN references; deletes remove them (and restore them on Recycle Bin restore).</item>
/// <item>ModifyDN re-keys all descendants; delete fails with NotAllowedOnNonLeaf for non-leaf objects unless the
/// subtree is deleted.</item>
/// <item>Deleted objects move to CN=Deleted Objects (which every naming context root gets when it is seeded) like in AD:
/// mangled RDN <c>&lt;name&gt;\0ADEL:&lt;objectGUID&gt;</c>, isDeleted=TRUE, lastKnownParent (a DN reference, so the
/// children of a deleted container point at the container's deleted DN), msDS-LastKnownRDN, whenChanged = deletion time;
/// objectCategory and sAMAccountType are removed. With <see cref="SessionInfo.RecycleBinEnabled"/> they keep their other
/// attributes and links (restored by <see cref="RestoreDeletedAsync"/>); without it they are tombstones (isRecycled=TRUE,
/// only the attributes a tombstone keeps, no msDS-LastKnownRDN and no links - as Samba returns them). Searches return them
/// only with <see cref="DirectorySearch.IncludeDeleted"/>; recycled objects of a Recycle Bin forest (<see cref="Recycle"/>)
/// only with <see cref="DirectorySearch.IncludeRecycled"/>. Restore (reanimation) removes isDeleted, sets the new DN and
/// makes DN references follow the object.</item>
/// <item>Search supports Base/OneLevel/Subtree, attribute scoped queries and the filter grammar of RFC 4515 with
/// &amp;, |, !, =, =*, substrings, &gt;=, &lt;=, ~=, the AD bit AND/OR and in-chain matching rules, (anr=...) and
/// objectCategory short names. Server side sort and VLV are emulated.</item>
/// </list>
/// Test hooks: <see cref="FailNext"/> (e.g. Busy twice for a DN), <see cref="BeforeOperation"/> (simulate a concurrent
/// change or cancel a token), <see cref="SetPermissions"/>, <see cref="Latency"/> and the <see cref="Operations"/> log.
/// The AdMng Watchdog emulation (RootDSE, domain controllers, USN spaces, replication metadata, Extended DN, lockout
/// expiry, read restrictions) lives in <c>FakeDirectorySession.Watchdog.cs</c>; all of it is opt-in.
/// </summary>
public sealed partial class FakeDirectorySession : IDirectorySession
{
    /// <summary>Domain SID used for security principals created through <see cref="AddAsync"/>.</summary>
    public const string DefaultDomainSid = "S-1-5-21-3623811015-3361044348-30300820";

    private const string DeletedObjectsName = "Deleted Objects";
    private const string AccountExpiresNever = "9223372036854775807";

    /// <summary>objectGUID of the seeded CN=Deleted Objects containers (fixed, so seeding does not shift generated GUIDs).</summary>
    private static readonly Guid DeletedObjectsGuid = new("18e2ea80-684f-11d2-b9aa-00c04f79f805");

    /// <summary>Attributes removed from every deleted object and put back (recomputed by AD) when it is restored.</summary>
    private static readonly string[] RecomputedOnRestore = [Attr.ObjectCategory, Attr.SamAccountType];

    /// <summary>The attributes a tombstone (or recycled object) keeps; everything else is stripped.</summary>
    private static readonly HashSet<string> TombstoneAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        Attr.ObjectClass, Attr.ObjectGuid, Attr.ObjectSid, Attr.SidHistory, Attr.SamAccountName, Attr.UserAccountControl,
        Attr.GroupType, Attr.Name, Attr.IsDeleted, Attr.IsRecycled, Attr.LastKnownParent, Attr.MsDsLastKnownRdn, Attr.WhenCreated,
        Attr.WhenChanged, Attr.UsnCreated, Attr.UsnChanged, "instanceType", Attr.IsCriticalSystemObject, Attr.SystemFlags,
        Attr.NtSecurityDescriptor,
    };

    private static readonly string[] AnrAttributes = [Attr.DisplayName, Attr.GivenName, Attr.Sn, Attr.SamAccountName, Attr.Name, Attr.Mail];

    private static readonly HashSet<AttributeSyntax> LengthCheckedSyntaxes =
    [
        AttributeSyntax.UnicodeString, AttributeSyntax.CaseIgnoreString, AttributeSyntax.CaseExactString,
        AttributeSyntax.PrintableString, AttributeSyntax.IA5String, AttributeSyntax.NumericString,
    ];

    private readonly Lock _gate = new();
    private readonly Dictionary<DistinguishedName, Entry> _entries = [];
    private readonly List<FakeOperation> _operations = [];
    private readonly List<FakePasswordReset> _passwordResets = [];
    private readonly Dictionary<DistinguishedName, EffectivePermissions> _permissions = [];
    private readonly List<InjectedFailure> _failures = [];
    private readonly List<Watcher> _watchers = [];
    private readonly TimeProvider _timeProvider;
    private long _sequence;
    private long _usn = 12000;
    private int _guidCounter;
    private int _nextRid = 5000;
    private int _inFlight;
    private int _maxObservedConcurrency;
    private volatile bool _disposed;
    private int? _maxValueRange;

    public FakeDirectorySession(SessionInfo? info = null, ISchemaCatalog? schema = null, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        Info = info ?? CreateInfo();
        Schema = schema ?? FakeSchema.Create(Info.SchemaNamingContext);
    }

    public SessionInfo Info { get; set; }

    public ISchemaCatalog Schema { get; }

    /// <summary>Domain SID prefix for principals created with <see cref="AddAsync"/>.</summary>
    public string DomainSid { get; set; } = DefaultDomainSid;

    /// <summary>Artificial latency of every call (real time) - useful to observe parallelism.</summary>
    public TimeSpan Latency { get; set; }

    /// <summary>Highest number of calls that were in progress at the same time.</summary>
    public int MaxObservedConcurrency => Volatile.Read(ref _maxObservedConcurrency);

    /// <summary>Permissions returned for objects without <see cref="SetPermissions"/> (default: unknown).</summary>
    public EffectivePermissions DefaultPermissions { get; set; } = EffectivePermissions.Unknown;

    /// <summary>Optional password policy: return false to reject a password (PasswordPolicy error, like 0x52D).</summary>
    public Func<string, bool>? PasswordPolicy { get; set; }

    /// <summary>Invoked before every call is applied (outside the lock) - simulate concurrent changes or cancel tokens.</summary>
    public Action<FakeOperation>? BeforeOperation { get; set; }

    /// <summary>
    /// Simulates AD's MaxValRange: reads (GetAsync, SearchAsync) return at most this many values of an attribute, under
    /// a ranged description such as <c>member;range=0-1</c>, like AD does beyond 1500 values. Only
    /// <see cref="GetAllValuesAsync(string, string, CancellationToken)"/> returns everything. Null (default) returns all values.
    /// </summary>
    public int? MaxValueRange
    {
        get => _maxValueRange;
        set
        {
            if (value is < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "MaxValueRange must be at least 1.");
            }

            _maxValueRange = value;
        }
    }

    public bool IsDisposed => _disposed;

    /// <summary>All calls in completion order (password values are never recorded here).</summary>
    public IReadOnlyList<FakeOperation> Operations
    {
        get
        {
            lock (_gate)
            {
                return [.. _operations];
            }
        }
    }

    /// <summary>Write calls only.</summary>
    public IReadOnlyList<FakeOperation> Writes => Operations.Where(o => o.IsWrite).ToList();

    public IReadOnlyList<FakePasswordReset> PasswordResets
    {
        get
        {
            lock (_gate)
            {
                return [.. _passwordResets];
            }
        }
    }

    /// <summary>All existing (not deleted) DNs in creation order.</summary>
    public IReadOnlyList<string> DistinguishedNames
    {
        get
        {
            lock (_gate)
            {
                return _entries.Values.Where(e => !e.IsDeleted).OrderBy(e => e.Sequence).Select(e => e.Dn.ToString()).ToList();
            }
        }
    }

    /// <summary>Default session facts for a lab domain (dc01.contoso.test, Recycle Bin enabled).</summary>
    public static SessionInfo CreateInfo(string domainDnsName = "contoso.test", string? server = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domainDnsName);
        var labels = domainDnsName.Split('.');
        var domainDn = string.Join(",", labels.Select(l => "DC=" + DistinguishedName.EscapeValue(l)));
        var netBios = labels[0].ToUpperInvariant();
        return new SessionInfo
        {
            Server = server ?? $"dc01.{domainDnsName}",
            Port = 389,
            DomainDnsName = domainDnsName,
            DomainNetBiosName = netBios,
            DefaultNamingContext = domainDn,
            ConfigurationNamingContext = $"CN=Configuration,{domainDn}",
            SchemaNamingContext = $"CN=Schema,CN=Configuration,{domainDn}",
            RootDomainNamingContext = domainDn,
            BoundIdentity = $@"{netBios}\Administrator",
            BoundUserDn = $"CN=Administrator,CN=Users,{domainDn}",
            Authentication = AuthenticationMode.CurrentUser,
            Security = TransportSecurity.SignAndSeal,
            DomainFunctionality = 7,
            ForestFunctionality = 7,
            SupportedControls = typeof(LdapControls).GetFields()
                .Where(f => f.IsLiteral)
                .Select(f => (string)f.GetRawConstantValue()!)
                .ToHashSet(StringComparer.Ordinal),
            RecycleBinEnabled = true,
            ConnectedAt = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero),
        };
    }

    // ---------------------------------------------------------------- test setup and inspection

    /// <summary>
    /// Inserts an object without validation (test setup). Values may be strings, byte[], Guid, bool, numbers,
    /// DateTimeOffset or sequences of those; null values are ignored. SID syntax strings ("S-1-5-...") are stored as
    /// bytes. name, the RDN attribute, objectGUID, whenCreated/whenChanged, uSN* and objectCategory are filled in.
    /// </summary>
    /// <returns>The normalized DN.</returns>
    public string Seed(string distinguishedName, IReadOnlyDictionary<string, object?> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        var dn = FakeDn.Parse(distinguishedName);
        lock (_gate)
        {
            return SeedCore(dn, attributes);
        }
    }

    /// <summary><see cref="Seed"/> with the lock already held.</summary>
    private string SeedCore(DistinguishedName dn, IReadOnlyDictionary<string, object?> attributes)
    {
        if (_entries.ContainsKey(dn))
        {
            throw new InvalidOperationException($"'{dn}' already exists.");
        }

        if (!IsNamingContextRoot(dn) && (dn.Parent is null || !_entries.ContainsKey(dn.Parent)))
        {
            throw new InvalidOperationException($"The parent of '{dn}' does not exist.");
        }

        var entry = new Entry(dn, ++_sequence);
        foreach (var (name, value) in attributes)
        {
            if (value is null)
            {
                continue;
            }

            var canonical = Canonical(name);
            var values = ToValues(canonical, value);
            if (values.Count > 0)
            {
                entry.Attributes[canonical] = values;
            }
        }

        ApplyCreationDefaults(entry);
        _entries[dn] = entry;
        RecordCreationMetadata(entry, seeded: true);
        if (IsNamingContextRoot(dn))
        {
            // Every naming context of AD has its (deleted, hidden) Deleted Objects container.
            EnsureDeletedObjectsContainer(dn);
        }

        return dn.ToString();
    }

    /// <summary>
    /// Recycles a deleted object (its deleted object lifetime expired): isRecycled=TRUE and only the attributes of a
    /// tombstone are kept. In a Recycle Bin forest it can no longer be restored and searches need
    /// <see cref="DirectorySearch.IncludeRecycled"/> to see it.
    /// </summary>
    public void Recycle(string deletedObjectDn)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(FakeDn.Parse(deletedObjectDn), out var entry) || !entry.IsDeleted)
            {
                throw new InvalidOperationException($"'{deletedObjectDn}' is not a deleted object.");
            }

            var stripped = StripToTombstone(entry);
            Touch(entry, [Attr.IsRecycled, .. stripped]);
        }
    }

    /// <summary>DNs of the deleted objects in CN=Deleted Objects (including recycled ones), in deletion order.</summary>
    public IReadOnlyList<string> DeletedDistinguishedNames
    {
        get
        {
            lock (_gate)
            {
                return _entries.Values
                    .Where(e => e.IsDeleted && e.Dn.Parent is { } parent && !parent.IsRoot
                        && string.Equals(parent.Leaf.Value, DeletedObjectsName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.DeletedSequence)
                    .Select(e => e.Dn.ToString())
                    .ToList();
            }
        }
    }

    /// <summary>Sets raw values without validation, like a change made by somebody else (empty = remove).</summary>
    public void SetValues(string distinguishedName, string attribute, params object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        lock (_gate)
        {
            var entry = FindExisting(distinguishedName);
            var canonical = Canonical(attribute);
            var list = values.SelectMany(v => ToValues(canonical, v)).ToList();
            if (list.Count == 0)
            {
                entry.Attributes.Remove(canonical);
            }
            else
            {
                entry.Attributes[canonical] = list;
            }

            Touch(entry, [canonical]);
        }
    }

    public bool Exists(string distinguishedName)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(FakeDn.Parse(distinguishedName), out var entry) && !entry.IsDeleted;
        }
    }

    /// <summary>
    /// Values of an attribute (including computed ones such as memberOf) as text: SIDs as S-1-..., GUIDs in "D"
    /// format, other binary values as hex. Empty when the object or attribute does not exist.
    /// </summary>
    public IReadOnlyList<string> GetValues(string distinguishedName, string attribute)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(FakeDn.Parse(distinguishedName), out var entry))
            {
                return [];
            }

            var name = Canonical(attribute);
            return ValuesOf(entry, name).Select(v => DisplayText(name, v)).ToList();
        }
    }

    /// <summary>All attributes of an object (stored and back links), or null.</summary>
    public DirectoryObject? Snapshot(string distinguishedName)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(FakeDn.Parse(distinguishedName), out var entry) ? ToDirectoryObject(entry, ["*"], ranged: false) : null;
        }
    }

    /// <summary>Configures what <see cref="GetEffectivePermissionsAsync"/> returns for an object.</summary>
    public void SetPermissions(string distinguishedName, EffectivePermissions permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        lock (_gate)
        {
            _permissions[FakeDn.Parse(distinguishedName)] = permissions;
        }
    }

    /// <summary>
    /// Makes the next <paramref name="times"/> calls of <paramref name="kind"/> (optionally only for one DN) fail with
    /// <paramref name="error"/> before they are applied.
    /// </summary>
    public void FailNext(FakeOperationKind kind, string? distinguishedName, DirectoryErrorKind error, int times = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(times);
        lock (_gate)
        {
            _failures.Add(new InjectedFailure(kind, distinguishedName is null ? null : FakeDn.Parse(distinguishedName), error, times));
        }
    }

    /// <summary>
    /// Makes the next call of <paramref name="kind"/> (optionally only for one DN) succeed on the "server" but fail
    /// with <paramref name="error"/> on the client - a lost response, e.g. a timeout after the DC applied the write.
    /// </summary>
    public void LoseNextResponse(FakeOperationKind kind, string? distinguishedName, DirectoryErrorKind error = DirectoryErrorKind.Timeout)
    {
        lock (_gate)
        {
            _failures.Add(new InjectedFailure(kind, distinguishedName is null ? null : FakeDn.Parse(distinguishedName), error, 1) { AfterApply = true });
        }
    }

    public void ClearOperations()
    {
        lock (_gate)
        {
            _operations.Clear();
        }
    }

    /// <summary>Restarts <see cref="MaxObservedConcurrency"/> (e.g. between the planning and the execution phase).</summary>
    public void ResetConcurrencyStatistics() => Volatile.Write(ref _maxObservedConcurrency, Volatile.Read(ref _inFlight));

    // ---------------------------------------------------------------- IDirectorySession

    public async IAsyncEnumerable<DirectoryObject> SearchAsync(DirectorySearch search, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        var operation = new FakeOperation { Kind = FakeOperationKind.Search, DistinguishedName = search.BaseDn, Search = search, Attributes = search.Attributes };
        var results = await RunAsync(operation, cancellationToken, () => ExecuteSearch(search)).ConfigureAwait(false);
        foreach (var result in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return result;
        }
    }

    public Task<VirtualListPage> GetVirtualListPageAsync(DirectorySearch search, VirtualListRequest window, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(window);
        if (search.Sort is null)
        {
            throw new ArgumentException("A virtual list view requires a sort key.", nameof(search));
        }

        var operation = new FakeOperation { Kind = FakeOperationKind.VirtualListPage, DistinguishedName = search.BaseDn, Search = search, Attributes = search.Attributes };
        return RunAsync(operation, cancellationToken, () =>
        {
            var all = ExecuteSearch(search with { SizeLimit = 0 });
            int offset;
            if (window.SeekValue is { } seek)
            {
                // Like AD: the first entry at or after the value in the list order (descending: at or below it).
                var compare = CultureInfo.InvariantCulture.CompareInfo;
                offset = all.FindIndex(o =>
                {
                    var compared = compare.Compare(o.GetString(search.Sort.Attribute) ?? string.Empty, seek, CompareOptions.IgnoreCase);
                    return search.Sort.Descending ? compared <= 0 : compared >= 0;
                });
                if (offset < 0)
                {
                    offset = all.Count;
                }
            }
            else
            {
                offset = Math.Clamp(window.Offset, 0, all.Count);
            }

            var items = all.Skip(offset).Take(Math.Max(0, window.Count)).ToList();
            return new VirtualListPage(items, offset, all.Count, [0x56, 0x4C, 0x56, 0x01]);
        });
    }

    public Task<DirectoryObject?> GetAsync(string distinguishedName, IReadOnlyList<string> attributes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        ArgumentNullException.ThrowIfNull(attributes);
        var operation = new FakeOperation { Kind = FakeOperationKind.Get, DistinguishedName = distinguishedName, Attributes = attributes };
        return RunAsync(operation, cancellationToken, () =>
        {
            var dn = FakeDn.Parse(distinguishedName);
            if (dn.IsRoot)
            {
                return ReadRootDse(attributes);
            }

            return _entries.TryGetValue(dn, out var entry) && !entry.IsDeleted
                ? ToDirectoryObject(entry, attributes)
                : null;
        });
    }

    public async IAsyncEnumerable<string> GetAllValuesAsync(string distinguishedName, string attribute, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute);
        var operation = new FakeOperation { Kind = FakeOperationKind.GetAllValues, DistinguishedName = distinguishedName, Attributes = [attribute] };
        var values = await RunAsync(operation, cancellationToken, () =>
        {
            var entry = FindExisting(distinguishedName);
            return ValuesOf(entry, Canonical(attribute)).Select(v => v as string ?? Encoding.UTF8.GetString((byte[])v)).ToList();
        }).ConfigureAwait(false);
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
        }
    }

    /// <summary>Ranged retrieval with Extended DN values and/or of a deleted object (Show Deleted).</summary>
    public async IAsyncEnumerable<string> GetAllValuesAsync(string distinguishedName, string attribute, AttributeReadOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute);
        ArgumentNullException.ThrowIfNull(options);
        var operation = new FakeOperation { Kind = FakeOperationKind.GetAllValues, DistinguishedName = distinguishedName, Attributes = [attribute] };
        var values = await RunAsync(operation, cancellationToken, () =>
        {
            var entry = options.IncludeDeleted ? FindIncludingDeleted(distinguishedName) : FindExisting(distinguishedName);
            var name = Canonical(attribute);
            return ValuesOf(entry, name)
                .Select(v => options.ExtendedDn ? ToExtendedValue(name, v) : v)
                .Select(v => v as string ?? Encoding.UTF8.GetString((byte[])v))
                .ToList();
        }).ConfigureAwait(false);
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
        }
    }

    public Task<EffectivePermissions> GetEffectivePermissionsAsync(string distinguishedName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        var operation = new FakeOperation { Kind = FakeOperationKind.GetEffectivePermissions, DistinguishedName = distinguishedName };
        return RunAsync(operation, cancellationToken, () =>
        {
            var entry = FindExisting(distinguishedName);
            return _permissions.TryGetValue(entry.Dn, out var permissions) ? permissions : DefaultPermissions;
        });
    }

    public async Task ModifyAsync(string distinguishedName, IReadOnlyList<AttributeModification> modifications, ModifyOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        ArgumentNullException.ThrowIfNull(modifications);
        var operation = new FakeOperation
        {
            Kind = FakeOperationKind.Modify,
            DistinguishedName = distinguishedName,
            Modifications = [.. modifications],
            ModifyOptions = options,
        };
        var dn = await RunAsync(operation, cancellationToken, () => ApplyModify(distinguishedName, modifications, options?.Permissive == true)).ConfigureAwait(false);
        Notify(dn);
    }

    public async Task AddAsync(string distinguishedName, IReadOnlyDictionary<string, IReadOnlyList<object>> attributes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        ArgumentNullException.ThrowIfNull(attributes);
        var operation = new FakeOperation { Kind = FakeOperationKind.Add, DistinguishedName = distinguishedName, AddedAttributes = attributes };
        var dn = await RunAsync(operation, cancellationToken, () => ApplyAdd(distinguishedName, attributes)).ConfigureAwait(false);
        Notify(dn);
    }

    public async Task DeleteAsync(string distinguishedName, bool subtree, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        var operation = new FakeOperation { Kind = FakeOperationKind.Delete, DistinguishedName = distinguishedName, Subtree = subtree };
        var dn = await RunAsync(operation, cancellationToken, () => ApplyDelete(distinguishedName, subtree)).ConfigureAwait(false);
        Notify(dn);
    }

    public async Task<string> MoveAsync(string distinguishedName, string? newParentDn, string? newRdn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        if (newParentDn is null && newRdn is null)
        {
            throw new ArgumentException("Either a new parent or a new RDN is required.");
        }

        var operation = new FakeOperation { Kind = FakeOperationKind.Move, DistinguishedName = distinguishedName, NewParentDn = newParentDn, NewRdn = newRdn };
        var (oldDn, newDn) = await RunAsync(operation, cancellationToken, () => ApplyMove(distinguishedName, newParentDn, newRdn)).ConfigureAwait(false);
        Notify(oldDn, newDn);
        return newDn.ToString();
    }

    public async Task ResetPasswordAsync(string distinguishedName, string newPassword, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(distinguishedName);
        ArgumentNullException.ThrowIfNull(newPassword);
        var operation = new FakeOperation { Kind = FakeOperationKind.ResetPassword, DistinguishedName = distinguishedName };
        var dn = await RunAsync(operation, cancellationToken, () => ApplyResetPassword(distinguishedName, newPassword)).ConfigureAwait(false);
        Notify(dn);
    }

    public async Task RestoreDeletedAsync(string deletedObjectDn, string targetDn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deletedObjectDn);
        ArgumentNullException.ThrowIfNull(targetDn);
        var operation = new FakeOperation { Kind = FakeOperationKind.RestoreDeleted, DistinguishedName = deletedObjectDn, TargetDn = targetDn };
        var dn = await RunAsync(operation, cancellationToken, () => ApplyRestore(deletedObjectDn, targetDn)).ConfigureAwait(false);
        Notify(dn);
    }

    public Task<IAsyncDisposable?> WatchAsync(string containerDn, Action<string> onChanged, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(containerDn);
        ArgumentNullException.ThrowIfNull(onChanged);
        var operation = new FakeOperation { Kind = FakeOperationKind.Watch, DistinguishedName = containerDn };
        return RunAsync<IAsyncDisposable?>(operation, cancellationToken, () =>
        {
            var watcher = new Watcher(this, FindExisting(containerDn).Dn, onChanged);
            _watchers.Add(watcher);
            return watcher;
        });
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        lock (_gate)
        {
            _watchers.Clear();
        }

        return ValueTask.CompletedTask;
    }

    // ---------------------------------------------------------------- call plumbing

    private async Task<T> RunAsync<T>(FakeOperation operation, CancellationToken cancellationToken, Func<T> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        BeforeOperation?.Invoke(operation);
        var inFlight = Interlocked.Increment(ref _inFlight);
        int observed;
        while (inFlight > (observed = Volatile.Read(ref _maxObservedConcurrency))
               && Interlocked.CompareExchange(ref _maxObservedConcurrency, inFlight, observed) != observed)
        {
        }

        try
        {
            if (Latency > TimeSpan.Zero)
            {
                await Task.Delay(Latency, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Yield();
            }

            T result;
            lock (_gate)
            {
                var lostResponse = TakeInjected(operation);
                result = action();
                if (lostResponse is not null)
                {
                    throw Error(CodeFor(lostResponse.Error), $"Injected {lostResponse.Error} failure after the operation was applied.", operation.DistinguishedName, lostResponse.Error);
                }

                _operations.Add(operation);
            }

            return result;
        }
        catch (DirectoryException ex)
        {
            lock (_gate)
            {
                _operations.Add(operation with { Error = ex.Kind });
            }

            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <summary>Throws an injected failure, or returns the injected lost response to throw after applying the call.</summary>
    private InjectedFailure? TakeInjected(FakeOperation operation)
    {
        var target = DistinguishedName.TryParse(operation.DistinguishedName, out var parsed) ? parsed : null;
        var failure = _failures.FirstOrDefault(f => f.Remaining > 0 && f.Kind == operation.Kind && (f.Dn is null || f.Dn == target));
        if (failure is null)
        {
            return null;
        }

        failure.Remaining--;
        if (failure.AfterApply)
        {
            return failure;
        }

        throw Error(CodeFor(failure.Error), $"Injected {failure.Error} failure.", operation.DistinguishedName, failure.Error);
    }

    private void Notify(params DistinguishedName?[] changed)
    {
        List<(Action<string> Callback, string Dn)> calls;
        lock (_gate)
        {
            calls = [.. from watcher in _watchers
                       from dn in changed
                       where dn is not null && dn.Parent == watcher.Container
                       select (watcher.Callback, dn!.ToString())];
        }

        foreach (var (callback, dn) in calls)
        {
            callback(dn);
        }
    }

    // ---------------------------------------------------------------- search

    private List<DirectoryObject> ExecuteSearch(DirectorySearch search)
    {
        var filter = FakeFilterParser.Parse(search.Filter);
        var baseDn = FakeDn.Parse(search.BaseDn);
        if (!_entries.TryGetValue(baseDn, out var baseEntry) || !IsVisible(baseEntry, search))
        {
            throw Error(32, $"The search base '{search.BaseDn}' does not exist.", search.BaseDn);
        }

        IEnumerable<Entry> candidates;
        if (search.AttributeScopedQuery is { } scopedAttribute)
        {
            if (search.Scope != SearchScope.Base)
            {
                throw Error(53, "An attribute scoped query requires base scope.", search.BaseDn);
            }

            candidates = ValuesOf(baseEntry, Canonical(scopedAttribute))
                .OfType<string>()
                .Select(v => DistinguishedName.TryParse(v, out var dn) && _entries.TryGetValue(dn, out var e) ? e : null)
                .OfType<Entry>();
        }
        else
        {
            candidates = search.Scope switch
            {
                SearchScope.Base => [baseEntry],
                SearchScope.OneLevel => _entries.Values.Where(e => e.Dn.Parent == baseDn),
                _ => _entries.Values.Where(e => FakeDn.IsSameOrBelow(e.Dn, baseDn)),
            };
        }

        var matches = candidates
            .Where(e => IsVisible(e, search))
            .Where(e => search.Scope == SearchScope.Base || search.AttributeScopedQuery is not null || IsInSameNamingContext(e.Dn, baseDn))
            .Where(e => !IsDeniedDeletedObject(e, search))
            .Where(e => Evaluate(filter, e))
            .OrderBy(e => e.Sequence)
            .ToList();
        if (search.Sort is { } sort)
        {
            matches.Sort((a, b) => CompareForSort(a, b, sort));
        }

        if (search.SizeLimit > 0 && matches.Count > search.SizeLimit)
        {
            matches = matches.Take(search.SizeLimit).ToList();
        }

        return matches.Select(e => IsDeniedDeletedObjectsContainer(e, search)
                ? new DirectoryObject(e.Dn.ToString(), [])
                : ToDirectoryObject(e, search.Attributes, extendedDn: search.ExtendedDn))
            .ToList();
    }

    /// <summary>
    /// Show Deleted makes deleted objects visible - in a Recycle Bin forest not the recycled ones (Show Recycled); without
    /// the Recycle Bin every deleted object is a tombstone marked as recycled, and Show Deleted returns it.
    /// </summary>
    private bool IsVisible(Entry entry, DirectorySearch search) =>
        !entry.IsDeleted
        || search.IncludeRecycled
        || (search.IncludeDeleted && (!entry.IsRecycled || !Info.RecycleBinEnabled));

    /// <summary>Server side sort per RFC 2891: entries without the attribute sort after all others.</summary>
    private int CompareForSort(Entry a, Entry b, AdMng.Core.Directory.SortKey sort)
    {
        var name = Canonical(sort.Attribute);
        var va = ValuesOf(a, name).FirstOrDefault();
        var vb = ValuesOf(b, name).FirstOrDefault();
        if (va is null || vb is null)
        {
            var missing = (va is null ? 1 : 0) - (vb is null ? 1 : 0);
            return missing != 0 ? missing : a.Sequence.CompareTo(b.Sequence);
        }

        var definition = Schema.FindAttribute(name);
        var result = definition?.Syntax is AttributeSyntax.Integer or AttributeSyntax.LargeInteger or AttributeSyntax.Enumeration
            or AttributeSyntax.GeneralizedTime or AttributeSyntax.OctetString
            ? CompareValues(definition, va, Encoding.UTF8.GetBytes(Text(vb)), Text(vb))
            : CultureInfo.InvariantCulture.CompareInfo.Compare(Text(va), Text(vb), CompareOptions.IgnoreCase);
        if (result == 0)
        {
            return a.Sequence.CompareTo(b.Sequence);
        }

        return sort.Descending ? -result : result;
    }

    private bool Evaluate(FilterNode node, Entry entry) => node switch
    {
        AndNode and => and.Children.All(c => Evaluate(c, entry)),
        OrNode or => or.Children.Any(c => Evaluate(c, entry)),
        NotNode not => !Evaluate(not.Child, entry),
        PresentNode present => string.Equals(present.Attribute, Attr.ObjectClass, StringComparison.OrdinalIgnoreCase)
            || ValuesOf(entry, Canonical(present.Attribute)).Count > 0,
        ComparisonNode comparison => EvaluateComparison(comparison, entry),
        SubstringNode substring => EvaluateSubstring(substring, entry),
        ExtensibleNode extensible => EvaluateExtensible(extensible, entry),
        _ => false,
    };

    private bool EvaluateComparison(ComparisonNode node, Entry entry)
    {
        if (string.Equals(node.Attribute, "anr", StringComparison.OrdinalIgnoreCase))
        {
            return MatchesAnr(entry, node.Value.Text);
        }

        var name = Canonical(node.Attribute);
        var definition = Schema.FindAttribute(name);
        var assertion = node.Value;
        if (string.Equals(name, Attr.ObjectCategory, StringComparison.OrdinalIgnoreCase) && !assertion.Text.Contains('=', StringComparison.Ordinal))
        {
            // AD accepts the class name and compares with the class's defaultObjectCategory.
            var category = Schema.FindClass(assertion.Text)?.DefaultObjectCategory ?? assertion.Text;
            assertion = new AssertionValue(Encoding.UTF8.GetBytes(category), category);
        }

        foreach (var value in ValuesOf(entry, name))
        {
            var result = CompareValues(definition, value, assertion.Bytes, assertion.Text);
            var matches = node.Operator switch
            {
                ComparisonOperator.GreaterOrEqual => result >= 0,
                ComparisonOperator.LessOrEqual => result <= 0,
                _ => result == 0,
            };
            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    private bool EvaluateSubstring(SubstringNode node, Entry entry)
    {
        var name = Canonical(node.Attribute);
        var comparison = Schema.FindAttribute(name)?.Syntax == AttributeSyntax.CaseExactString
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        foreach (var value in ValuesOf(entry, name).Select(Text))
        {
            var position = 0;
            if (node.Initial is { } initial)
            {
                if (!value.StartsWith(initial, comparison))
                {
                    continue;
                }

                position = initial.Length;
            }

            var ok = true;
            foreach (var any in node.Any)
            {
                var index = value.IndexOf(any, position, comparison);
                if (index < 0)
                {
                    ok = false;
                    break;
                }

                position = index + any.Length;
            }

            if (ok && node.Final is { } final)
            {
                ok = value.Length - position >= final.Length && value.EndsWith(final, comparison);
            }

            if (ok)
            {
                return true;
            }
        }

        return false;
    }

    private bool EvaluateExtensible(ExtensibleNode node, Entry entry)
    {
        var name = Canonical(node.Attribute);
        switch (node.MatchingRule)
        {
            case LdapFilter.MatchingRuleBitAnd or LdapFilter.MatchingRuleBitOr:
                if (!long.TryParse(node.Value.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var mask))
                {
                    return false;
                }

                foreach (var value in ValuesOf(entry, name))
                {
                    if (long.TryParse(Text(value), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
                        && (node.MatchingRule == LdapFilter.MatchingRuleBitAnd ? (number & mask) == mask : (number & mask) != 0))
                    {
                        return true;
                    }
                }

                return false;
            case LdapFilter.MatchingRuleInChain:
                return DistinguishedName.TryParse(node.Value.Text, out var target) && ReachesInChain(entry, name, target);
            default:
                throw Error(18, $"Unsupported matching rule '{node.MatchingRule}'.", entry.Dn.ToString());
        }
    }

    /// <summary>LDAP_MATCHING_RULE_IN_CHAIN: follows the DN-valued attribute transitively.</summary>
    private bool ReachesInChain(Entry start, string attribute, DistinguishedName target)
    {
        var visited = new HashSet<DistinguishedName> { start.Dn };
        var queue = new Queue<Entry>([start]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var value in ValuesOf(current, attribute).OfType<string>())
            {
                if (!DistinguishedName.TryParse(value, out var dn))
                {
                    continue;
                }

                if (dn == target)
                {
                    return true;
                }

                if (visited.Add(dn) && _entries.TryGetValue(dn, out var next) && !next.IsDeleted)
                {
                    queue.Enqueue(next);
                }
            }
        }

        return false;
    }

    /// <summary>Ambiguous name resolution: prefix match on the ANR attributes, "first last" and "=exact".</summary>
    private bool MatchesAnr(Entry entry, string text)
    {
        var value = text.Trim();
        var exact = value.StartsWith('=');
        if (exact)
        {
            value = value[1..].Trim();
        }

        if (value.Length == 0)
        {
            return false;
        }

        bool Match(string attribute, string candidate) => ValuesOf(entry, attribute).Select(Text).Any(v => exact
            ? v.Equals(candidate, StringComparison.OrdinalIgnoreCase)
            : v.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));

        if (AnrAttributes.Any(a => Match(a, value)))
        {
            return true;
        }

        var space = value.IndexOf(' ', StringComparison.Ordinal);
        if (space <= 0)
        {
            return false;
        }

        var first = value[..space];
        var rest = value[(space + 1)..].Trim();
        return rest.Length > 0
            && ((Match(Attr.GivenName, first) && Match(Attr.Sn, rest)) || (Match(Attr.Sn, first) && Match(Attr.GivenName, rest)));
    }

    /// <summary>Compares a stored value with an assertion using the attribute's matching rule (0 = equal).</summary>
    private static int CompareValues(AttributeDefinition? definition, object stored, byte[] assertionBytes, string assertionText)
    {
        if (stored is byte[] bytes)
        {
            return bytes.AsSpan().SequenceCompareTo(assertionBytes);
        }

        var text = (string)stored;
        switch (definition?.Syntax)
        {
            case AttributeSyntax.DistinguishedName:
                return DistinguishedName.AreEqual(text, assertionText) ? 0 : string.Compare(text, assertionText, StringComparison.OrdinalIgnoreCase);
            case AttributeSyntax.Integer or AttributeSyntax.LargeInteger or AttributeSyntax.Enumeration
                when long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var a)
                     && long.TryParse(assertionText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var b):
                return a.CompareTo(b);
            case AttributeSyntax.GeneralizedTime
                when AdConvert.FromGeneralizedTime(text) is { } ta && AdConvert.FromGeneralizedTime(assertionText) is { } tb:
                return ta.CompareTo(tb);
            case AttributeSyntax.CaseExactString:
                return string.Compare(text, assertionText, StringComparison.Ordinal);
            default:
                return string.Compare(text, assertionText, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------------------------------------------------------------- writes

    private DistinguishedName ApplyModify(string distinguishedName, IReadOnlyList<AttributeModification> modifications, bool permissive)
    {
        var entry = FindExisting(distinguishedName);
        var work = entry.CloneAttributes();
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addedMembers = new List<string>();
        foreach (var modification in modifications)
        {
            var name = Canonical(modification.Attribute);
            var definition = Schema.FindAttribute(name) ?? throw Error(17, $"The attribute '{name}' is not defined in the schema.", distinguishedName);
            CheckWritable(entry, name, definition);
            var values = modification.Values.Select(v => v ?? throw new ArgumentException("Attribute values cannot be null.")).ToList();
            foreach (var value in values)
            {
                CheckSyntax(name, definition, value, distinguishedName);
            }

            if (string.Equals(name, Attr.PwdLastSet, StringComparison.OrdinalIgnoreCase))
            {
                work[name] = [PwdLastSetValue(modification, values, distinguishedName)];
                touched.Add(name);
                continue;
            }

            if (string.Equals(name, Attr.LockoutTime, StringComparison.OrdinalIgnoreCase))
            {
                if (modification.Operation != ModificationOperation.Replace || values is not [string and "0"])
                {
                    throw Error(53, "lockoutTime can only be set to 0.", distinguishedName);
                }

                work[name] = ["0"];
                touched.Add(name);
                continue;
            }

            var current = work.TryGetValue(name, out var existing) ? existing : [];
            switch (modification.Operation)
            {
                case ModificationOperation.Add:
                    if (values.Count == 0)
                    {
                        throw Error(2, $"An add of '{name}' must contain values.", distinguishedName);
                    }

                    foreach (var value in values)
                    {
                        if (current.Any(c => ValueEquals(definition, c, value)))
                        {
                            if (!permissive)
                            {
                                throw Error(20, $"The value '{Text(value)}' already exists in '{name}'.", distinguishedName);
                            }

                            continue;
                        }

                        current.Add(CloneValue(value));
                        if (string.Equals(name, Attr.Member, StringComparison.OrdinalIgnoreCase))
                        {
                            addedMembers.Add(Text(value));
                        }
                    }

                    work[name] = current;
                    break;
                case ModificationOperation.Delete:
                    if (values.Count == 0)
                    {
                        if (current.Count == 0 && !permissive)
                        {
                            throw Error(16, $"The attribute '{name}' has no values.", distinguishedName);
                        }

                        work.Remove(name);
                        break;
                    }

                    foreach (var value in values)
                    {
                        var index = current.FindIndex(c => ValueEquals(definition, c, value));
                        if (index < 0)
                        {
                            if (!permissive)
                            {
                                throw Error(16, $"The value '{Text(value)}' does not exist in '{name}'.", distinguishedName);
                            }

                            continue;
                        }

                        current.RemoveAt(index);
                    }

                    if (current.Count == 0)
                    {
                        work.Remove(name);
                    }
                    else
                    {
                        work[name] = current;
                    }

                    break;
                default:
                    if (values.Count == 0)
                    {
                        work.Remove(name);
                        break;
                    }

                    for (var i = 0; i < values.Count; i++)
                    {
                        if (values.Skip(i + 1).Any(v => ValueEquals(definition, v, values[i])))
                        {
                            throw Error(20, $"The value '{Text(values[i])}' is listed twice for '{name}'.", distinguishedName);
                        }
                    }

                    if (string.Equals(name, Attr.Member, StringComparison.OrdinalIgnoreCase))
                    {
                        addedMembers.AddRange(values.Where(v => !current.Any(c => ValueEquals(definition, c, v))).Select(Text));
                    }

                    work[name] = values.Select(CloneValue).ToList();
                    break;
            }

            touched.Add(name);
        }

        ValidateValues(entry.Dn, work, touched);
        CheckPrimaryGroupMembers(entry, addedMembers);
        CheckObjectClass(entry.Dn, work, touched);
        entry.ReplaceAttributes(work);
        Touch(entry, touched);
        return entry.Dn;
    }

    private string PwdLastSetValue(AttributeModification modification, List<object> values, string distinguishedName)
    {
        if (modification.Operation != ModificationOperation.Replace || values is not [string value] || value is not ("0" or "-1"))
        {
            throw Error(53, "pwdLastSet can only be set to 0 or -1.", distinguishedName);
        }

        return value == "0" ? "0" : FileTimeNow();
    }

    private void CheckWritable(Entry entry, string name, AttributeDefinition definition)
    {
        var dn = entry.Dn.ToString();
        if (string.Equals(name, Attr.UnicodePwd, StringComparison.OrdinalIgnoreCase) || string.Equals(name, "userPassword", StringComparison.OrdinalIgnoreCase))
        {
            throw Error(53, "Use ResetPasswordAsync to set passwords.", dn);
        }

        if (string.Equals(name, Attr.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(name, entry.Dn.Leaf.Type, StringComparison.OrdinalIgnoreCase))
        {
            throw Error(67, $"'{name}' is the naming attribute; use ModifyDN.", dn);
        }

        if (definition.IsBackLink || definition.IsConstructed || definition.IsSystemOnly)
        {
            throw Error(53, $"'{name}' cannot be modified (system-only, constructed or back link).", dn);
        }
    }

    private void CheckSyntax(string name, AttributeDefinition definition, object value, string distinguishedName)
    {
        if (value is byte[])
        {
            if (!definition.IsBinary && !Schema.IsBinary(name))
            {
                throw Error(21, $"'{name}' expects a text value.", distinguishedName);
            }

            return;
        }

        if (value is not string text)
        {
            throw new ArgumentException($"Unsupported value type {value.GetType()} for '{name}'.");
        }

        var valid = text.Length > 0 && definition.Syntax switch
        {
            AttributeSyntax.Integer or AttributeSyntax.Enumeration => int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _),
            AttributeSyntax.LargeInteger => long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _),
            AttributeSyntax.Boolean => text is "TRUE" or "FALSE",
            AttributeSyntax.DistinguishedName => DistinguishedName.TryParse(text, out var dn) && !dn.IsRoot,
            AttributeSyntax.GeneralizedTime => AdConvert.FromGeneralizedTime(text) is not null,
            _ => true,
        };
        if (!valid)
        {
            throw Error(21, $"'{text}' is not a valid value for '{name}'.", distinguishedName);
        }
    }

    private void ValidateValues(DistinguishedName dn, Dictionary<string, List<object>> work, IEnumerable<string> attributes)
    {
        foreach (var name in attributes)
        {
            if (!work.TryGetValue(name, out var values) || Schema.FindAttribute(name) is not { } definition)
            {
                continue;
            }

            if (definition.IsSingleValued && values.Count > 1)
            {
                throw Error(19, $"'{name}' is single-valued.", dn.ToString());
            }

            foreach (var value in values)
            {
                if (!InRange(definition, value))
                {
                    throw Error(19, $"A value of '{name}' is outside the allowed range.", dn.ToString());
                }

                if (definition.Syntax == AttributeSyntax.DistinguishedName && value is string reference && !ReferenceExists(reference))
                {
                    throw Error(32, $"'{name}' refers to '{reference}', which does not exist.", dn.ToString());
                }
            }
        }
    }

    /// <summary>
    /// DN values must reference existing objects (AD referential integrity). Only the domain partition is modelled,
    /// so references into other partitions (e.g. objectCategory in the schema) are accepted.
    /// </summary>
    private bool ReferenceExists(string reference)
    {
        if (!DistinguishedName.TryParse(reference, out var target))
        {
            return false;
        }

        if (!FakeDn.IsSameOrBelow(target, FakeDn.Parse(Info.DefaultNamingContext))
            || FakeDn.IsSameOrBelow(target, FakeDn.Parse(Info.ConfigurationNamingContext)))
        {
            return true;
        }

        return _entries.TryGetValue(target, out var referenced) && !referenced.IsDeleted;
    }

    private static bool InRange(AttributeDefinition definition, object value)
    {
        long size;
        if (value is byte[] bytes)
        {
            size = bytes.Length;
        }
        else if (definition.Syntax is AttributeSyntax.Integer or AttributeSyntax.LargeInteger or AttributeSyntax.Enumeration)
        {
            if (!long.TryParse((string)value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out size))
            {
                return false;
            }
        }
        else if (LengthCheckedSyntaxes.Contains(definition.Syntax))
        {
            size = ((string)value).Length;
        }
        else
        {
            return true;
        }

        return (definition.RangeLower is not { } lower || size >= lower) && (definition.RangeUpper is not { } upper || size <= upper);
    }

    /// <summary>Like AD: a member whose primary group is this group cannot be added explicitly.</summary>
    private void CheckPrimaryGroupMembers(Entry group, List<string> addedMembers)
    {
        if (addedMembers.Count == 0 || RidOf(group) is not { } rid)
        {
            return;
        }

        foreach (var member in addedMembers)
        {
            if (DistinguishedName.TryParse(member, out var dn) && _entries.TryGetValue(dn, out var entry)
                && entry.First(Attr.PrimaryGroupId) == rid.ToString(CultureInfo.InvariantCulture))
            {
                throw Error(68, $"'{member}' is already a member of the group through its primary group.", group.Dn.ToString());
            }
        }
    }

    private void CheckObjectClass(DistinguishedName dn, Dictionary<string, List<object>> work, IEnumerable<string> touched)
    {
        var classes = work.TryGetValue(Attr.ObjectClass, out var values) ? values.OfType<string>().ToList() : [];
        if (classes.Count == 0)
        {
            throw Error(65, "The object has no objectClass.", dn.ToString());
        }

        var allowed = new HashSet<string>(Schema.GetAllowedAttributes(classes), StringComparer.OrdinalIgnoreCase);
        foreach (var name in touched.Where(work.ContainsKey))
        {
            if (allowed.Count > 0 && !allowed.Contains(name))
            {
                throw Error(65, $"'{name}' is not allowed for the classes of the object.", dn.ToString());
            }
        }

        foreach (var mandatory in Schema.GetMandatoryAttributes(classes))
        {
            if (!work.ContainsKey(mandatory))
            {
                throw Error(65, $"The mandatory attribute '{mandatory}' is missing.", dn.ToString());
            }
        }
    }

    private DistinguishedName ApplyAdd(string distinguishedName, IReadOnlyDictionary<string, IReadOnlyList<object>> attributes)
    {
        var dn = FakeDn.Parse(distinguishedName);
        if (dn.IsRoot)
        {
            throw Error(53, "Cannot create the root DSE.", distinguishedName);
        }

        if (_entries.ContainsKey(dn))
        {
            throw Error(68, $"'{dn}' already exists.", distinguishedName);
        }

        if (dn.Parent is not { } parent || !_entries.TryGetValue(parent, out var parentEntry) || parentEntry.IsDeleted)
        {
            throw Error(32, $"The parent of '{dn}' does not exist.", distinguishedName);
        }

        var work = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
        string? password = null;
        foreach (var (attribute, values) in attributes)
        {
            var name = Canonical(attribute);
            var definition = Schema.FindAttribute(name) ?? throw Error(17, $"The attribute '{name}' is not defined in the schema.", distinguishedName);
            if (string.Equals(name, Attr.UnicodePwd, StringComparison.OrdinalIgnoreCase))
            {
                password = DecodeUnicodePwd(values);
                continue;
            }

            if (definition.IsBackLink || definition.IsConstructed || definition.IsSystemOnly)
            {
                throw Error(53, $"'{name}' cannot be set (system-only, constructed or back link).", distinguishedName);
            }

            foreach (var value in values)
            {
                CheckSyntax(name, definition, value, distinguishedName);
            }

            if (values.Count > 0)
            {
                work[name] = values.Select(CloneValue).ToList();
            }
        }

        if (!work.TryGetValue(Attr.ObjectClass, out var requestedClasses) || requestedClasses.Count == 0)
        {
            throw Error(65, "objectClass is required.", distinguishedName);
        }

        var classes = ExpandClasses(requestedClasses.OfType<string>());
        work[Attr.ObjectClass] = [.. classes];
        var rdnAttribute = Canonical(dn.Leaf.Type);
        if (work.TryGetValue(rdnAttribute, out var rdnValues) && !(rdnValues is [string rdnValue] && rdnValue.Equals(dn.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw Error(64, $"'{rdnAttribute}' does not match the RDN.", distinguishedName);
        }

        var entry = new Entry(dn, ++_sequence);
        entry.ReplaceAttributes(work);
        ApplyCreationDefaults(entry);
        ApplyPrincipalDefaults(entry, classes);
        ValidateValues(dn, entry.Attributes, entry.Attributes.Keys.ToList());
        CheckObjectClass(dn, entry.Attributes, work.Keys.ToList());
        _entries[dn] = entry;
        RecordCreationMetadata(entry, seeded: false);
        if (password is not null)
        {
            ApplyPassword(entry, password);
        }

        return dn;
    }

    private static string DecodeUnicodePwd(IReadOnlyList<object> values)
    {
        if (values is not [byte[] bytes])
        {
            throw new ArgumentException("unicodePwd must be one byte[] value.");
        }

        var text = Encoding.Unicode.GetString(bytes);
        return text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;
    }

    private DistinguishedName ApplyDelete(string distinguishedName, bool subtree)
    {
        var entry = FindExisting(distinguishedName);
        var victims = _entries.Values
            .Where(e => !e.IsDeleted && FakeDn.IsSameOrBelow(e.Dn, entry.Dn))
            .OrderByDescending(e => e.Dn.Rdns.Count)
            .ToList();
        if (victims.Count > 1 && !subtree)
        {
            throw Error(66, $"'{entry.Dn}' has child objects.", distinguishedName);
        }

        var victimDns = victims.Select(v => v.Dn).ToHashSet();
        var forwardLinks = Schema.Attributes.Where(a => a is { IsLinked: true, IsBackLink: false }).Select(a => a.LdapName).ToList();
        foreach (var survivor in _entries.Values.Where(e => !victimDns.Contains(e.Dn)))
        {
            foreach (var link in forwardLinks)
            {
                if (!survivor.Attributes.TryGetValue(link, out var values))
                {
                    continue;
                }

                foreach (var value in values.OfType<string>().ToList())
                {
                    if (DistinguishedName.TryParse(value, out var target) && victimDns.Contains(target))
                    {
                        values.Remove(value);
                        _entries[target].DeactivatedLinks.Add((survivor.Dn, link));
                    }
                }

                if (values.Count == 0)
                {
                    survivor.Attributes.Remove(link);
                }
            }
        }

        var deletedDn = entry.Dn;

        // Deepest first: a child is moved while its parent still has its live DN; when the parent follows, the child's
        // lastKnownParent follows it into CN=Deleted Objects (AD keeps DN references by object, not by name).
        foreach (var victim in victims)
        {
            Tombstone(victim);
        }

        return deletedDn;
    }

    /// <summary>Moves a deleted object to CN=Deleted Objects (a deleted-object, or a tombstone without the Recycle Bin).</summary>
    private void Tombstone(Entry victim)
    {
        var container = EnsureDeletedObjectsContainer(NamingContextRootOf(victim.Dn));
        var guid = victim.Attributes.TryGetValue(Attr.ObjectGuid, out var guidValues) && guidValues is [byte[] { Length: 16 } bytes]
            ? new Guid(bytes)
            : Guid.Empty;
        var deletedName = $"{victim.Dn.Name}\nDEL:{guid:D}";
        var originalDn = victim.Dn;
        _entries.Remove(originalDn);
        victim.Dn = container.Child(originalDn.Leaf.Type, deletedName);
        victim.DeletedSequence = ++_sequence;
        victim.Attributes[Attr.IsDeleted] = ["TRUE"];
        victim.Attributes[Attr.LastKnownParent] = [originalDn.Parent!.ToString()];
        victim.Attributes[Attr.Name] = [deletedName];
        victim.Attributes[Canonical(originalDn.Leaf.Type)] = [deletedName];
        foreach (var recomputed in RecomputedOnRestore)
        {
            if (victim.Attributes.Remove(recomputed, out var values))
            {
                victim.Recomputed[recomputed] = values;
            }
        }

        IReadOnlyList<string> stripped = [];
        if (Info.RecycleBinEnabled)
        {
            victim.Attributes[Attr.MsDsLastKnownRdn] = [originalDn.Name];
        }
        else
        {
            stripped = StripToTombstone(victim);
        }

        Touch(victim, [Attr.IsDeleted, Attr.LastKnownParent, Attr.Name, Canonical(originalDn.Leaf.Type), Attr.MsDsLastKnownRdn, .. stripped]);
        _entries[victim.Dn] = victim;
        UpdateReferences(originalDn, victim.Dn);
    }

    /// <summary>A tombstone / recycled object: isRecycled=TRUE, only the preserved attributes, no links to restore.</summary>
    /// <returns>The names of the attributes that were removed.</returns>
    private List<string> StripToTombstone(Entry entry)
    {
        var rdnAttribute = Canonical(entry.Dn.Leaf.Type);
        var removed = new List<string>();
        foreach (var name in entry.Attributes.Keys.ToList())
        {
            if (!TombstoneAttributes.Contains(name) && !string.Equals(name, rdnAttribute, StringComparison.OrdinalIgnoreCase))
            {
                entry.Attributes.Remove(name);
                removed.Add(name);
            }
        }

        entry.Attributes[Attr.IsRecycled] = ["TRUE"];
        entry.DeactivatedLinks.Clear();
        return removed;
    }

    private DistinguishedName EnsureDeletedObjectsContainer(DistinguishedName domain)
    {
        var dn = domain.Child("CN", DeletedObjectsName);
        if (!_entries.ContainsKey(dn))
        {
            var entry = new Entry(dn, ++_sequence);
            entry.Attributes[Attr.ObjectClass] = ["top", "container"];
            entry.Attributes[Attr.ObjectGuid] = [DeletedObjectsGuid.ToByteArray()];
            entry.Attributes[Attr.IsDeleted] = ["TRUE"];
            entry.Attributes[Attr.IsCriticalSystemObject] = ["TRUE"];
            entry.Attributes[Attr.ShowInAdvancedViewOnly] = ["TRUE"];
            ApplyCreationDefaults(entry);
            _entries[dn] = entry;
        }

        return dn;
    }

    private DistinguishedName ApplyRestore(string deletedObjectDn, string targetDn)
    {
        var deleted = FakeDn.Parse(deletedObjectDn);

        // The request carries the Show Deleted control: a recycled object of a Recycle Bin forest is not visible to it.
        if (!_entries.TryGetValue(deleted, out var entry) || !entry.IsDeleted || (entry.IsRecycled && Info.RecycleBinEnabled))
        {
            throw Error(32, $"The deleted object '{deletedObjectDn}' does not exist.", deletedObjectDn);
        }

        var target = FakeDn.Parse(targetDn);
        if (target.IsRoot || target.Parent is not { } parent || !_entries.TryGetValue(parent, out var parentEntry) || parentEntry.IsDeleted)
        {
            throw Error(32, $"The parent of '{target}' does not exist.", targetDn);
        }

        if (_entries.ContainsKey(target))
        {
            throw Error(68, $"'{target}' already exists.", targetDn);
        }

        if (!string.Equals(target.Leaf.Type, entry.Dn.Leaf.Type, StringComparison.OrdinalIgnoreCase))
        {
            throw Error(64, $"The naming attribute cannot change from '{entry.Dn.Leaf.Type}' to '{target.Leaf.Type}'.", targetDn);
        }

        var oldDn = entry.Dn;
        _entries.Remove(oldDn);
        entry.Dn = target;
        entry.Attributes.Remove(Attr.IsDeleted);
        entry.Attributes.Remove(Attr.IsRecycled);
        entry.Attributes[Attr.Name] = [target.Name];
        entry.Attributes[Canonical(target.Leaf.Type)] = [target.Name];
        foreach (var (name, values) in entry.Recomputed)
        {
            entry.Attributes[name] = values;
        }

        entry.Recomputed.Clear();
        _entries[target] = entry;
        UpdateReferences(oldDn, target);
        foreach (var (source, attribute) in entry.DeactivatedLinks)
        {
            if (_entries.TryGetValue(source, out var sourceEntry) && !sourceEntry.IsDeleted)
            {
                if (!sourceEntry.Attributes.TryGetValue(attribute, out var values))
                {
                    sourceEntry.Attributes[attribute] = values = [];
                }

                if (!values.OfType<string>().Any(v => DistinguishedName.AreEqual(v, target.ToString())))
                {
                    values.Add(target.ToString());
                }
            }
        }

        entry.DeactivatedLinks.Clear();
        Touch(entry, [Attr.IsDeleted, Attr.Name, Canonical(target.Leaf.Type), Attr.LastKnownParent]);
        return target;
    }

    private (DistinguishedName OldDn, DistinguishedName NewDn) ApplyMove(string distinguishedName, string? newParentDn, string? newRdn)
    {
        var entry = FindExisting(distinguishedName);
        var oldDn = entry.Dn;
        var parent = oldDn.Parent ?? throw Error(53, "The root cannot be moved.", distinguishedName);
        if (newParentDn is not null)
        {
            parent = FakeDn.Parse(newParentDn);
            if (!_entries.TryGetValue(parent, out var parentEntry) || parentEntry.IsDeleted)
            {
                throw Error(32, $"The new parent '{newParentDn}' does not exist.", distinguishedName);
            }
        }

        var leaf = oldDn.Leaf;
        if (newRdn is not null)
        {
            var rdn = FakeDn.Parse(newRdn);
            if (rdn.Rdns.Count != 1)
            {
                throw Error(34, $"'{newRdn}' is not a single RDN.", distinguishedName);
            }

            leaf = rdn.Leaf;
            if (!string.Equals(leaf.Type, oldDn.Leaf.Type, StringComparison.OrdinalIgnoreCase))
            {
                throw Error(64, $"The naming attribute cannot change from '{oldDn.Leaf.Type}' to '{leaf.Type}'.", distinguishedName);
            }
        }

        if (FakeDn.IsSameOrBelow(parent, oldDn))
        {
            throw Error(53, "An object cannot be moved below itself.", distinguishedName);
        }

        var newDn = parent.Child(oldDn.Leaf.Type, leaf.Value);
        if (newDn != oldDn && _entries.ContainsKey(newDn))
        {
            throw Error(68, $"'{newDn}' already exists.", distinguishedName);
        }

        var moved = _entries.Values.Where(e => FakeDn.IsSameOrBelow(e.Dn, oldDn)).ToList();
        foreach (var item in moved)
        {
            _entries.Remove(item.Dn);
        }

        foreach (var item in moved)
        {
            item.Dn = FakeDn.Rebase(item.Dn, oldDn, newDn);
            _entries[item.Dn] = item;
        }

        entry.Attributes[Attr.Name] = [leaf.Value];
        entry.Attributes[Canonical(leaf.Type)] = [leaf.Value];
        UpdateReferences(oldDn, newDn);
        Touch(entry, string.Equals(leaf.Value, oldDn.Leaf.Value, StringComparison.Ordinal) ? [Attr.Name] : [Attr.Name, Canonical(leaf.Type)]);
        return (oldDn, newDn);
    }

    /// <summary>DN-syntax values follow renamed/moved objects (AD tracks references by GUID).</summary>
    private void UpdateReferences(DistinguishedName oldDn, DistinguishedName newDn)
    {
        var dnAttributes = Schema.Attributes.Where(a => a.Syntax == AttributeSyntax.DistinguishedName).Select(a => a.LdapName).ToList();
        foreach (var entry in _entries.Values)
        {
            foreach (var attribute in dnAttributes)
            {
                if (!entry.Attributes.TryGetValue(attribute, out var values))
                {
                    continue;
                }

                for (var i = 0; i < values.Count; i++)
                {
                    if (values[i] is string text && DistinguishedName.TryParse(text, out var dn) && FakeDn.IsSameOrBelow(dn, oldDn))
                    {
                        values[i] = FakeDn.Rebase(dn, oldDn, newDn).ToString();
                    }
                }
            }

            for (var i = 0; i < entry.DeactivatedLinks.Count; i++)
            {
                var (source, attribute) = entry.DeactivatedLinks[i];
                if (FakeDn.IsSameOrBelow(source, oldDn))
                {
                    entry.DeactivatedLinks[i] = (FakeDn.Rebase(source, oldDn, newDn), attribute);
                }
            }
        }
    }

    private DistinguishedName ApplyResetPassword(string distinguishedName, string password)
    {
        var entry = FindExisting(distinguishedName);
        if (!entry.HasClass(ObjectClasses.User))
        {
            throw Error(53, "Only user objects have passwords.", distinguishedName);
        }

        if (Info.Security == TransportSecurity.None)
        {
            throw new DirectoryException(DirectoryErrorKind.UnwillingToPerform, "Password changes require an encrypted connection.")
            {
                LdapResultCode = 53,
                ServerMessage = "0000001F: SvcErr: DSID-031A12D2, problem 5003 (WILL_NOT_PERFORM), data 0",
                DistinguishedName = distinguishedName,
            };
        }

        ApplyPassword(entry, password);
        return entry.Dn;
    }

    private void ApplyPassword(Entry entry, string password)
    {
        if (PasswordPolicy is { } policy && !policy(password))
        {
            throw new DirectoryException(DirectoryErrorKind.PasswordPolicy, "The password does not meet the password policy requirements.")
            {
                LdapResultCode = 19,
                Win32Error = 0x52D,
                ServerMessage = "0000052D: Constraint violation - check_password_restrictions: the password does not meet the complexity criteria!",
                DistinguishedName = entry.Dn.ToString(),
            };
        }

        _passwordResets.Add(new FakePasswordReset(entry.Dn.ToString(), password));
        entry.Attributes[Attr.PwdLastSet] = [FileTimeNow()];
        Touch(entry, [Attr.UnicodePwd, Attr.PwdLastSet]);
    }

    // ---------------------------------------------------------------- entries and values

    private Entry FindExisting(string distinguishedName)
    {
        var dn = FakeDn.Parse(distinguishedName);
        if (!_entries.TryGetValue(dn, out var entry) || entry.IsDeleted)
        {
            throw Error(32, $"The object '{distinguishedName}' does not exist.", distinguishedName);
        }

        return entry;
    }

    private static bool IsNamingContextRoot(DistinguishedName dn) =>
        !dn.IsRoot && dn.Rdns.All(r => string.Equals(r.Type, "DC", StringComparison.OrdinalIgnoreCase));

    private string Canonical(string attribute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute);
        var semicolon = attribute.IndexOf(';', StringComparison.Ordinal);
        var name = semicolon < 0 ? attribute : attribute[..semicolon];
        return Schema.FindAttribute(name)?.LdapName ?? name;
    }

    /// <summary>Stored values plus computed attributes (back links, distinguishedName, canonicalName...).</summary>
    private IReadOnlyList<object> ValuesOf(Entry entry, string name)
    {
        if (IsHidden(entry, name))
        {
            return [];
        }

        if (TryGetWatchdogValues(entry, name, out var watchdogValues))
        {
            return watchdogValues;
        }

        switch (name)
        {
            case Attr.DistinguishedName:
                return [entry.Dn.ToString()];
            case Attr.CanonicalName:
                return [entry.Dn.ToCanonicalName()];
            case "primaryGroupToken":
                return entry.HasClass(ObjectClasses.Group) && RidOf(entry) is { } rid ? [rid.ToString(CultureInfo.InvariantCulture)] : [];
            case Attr.UserAccountControlComputed:
                if (!entry.HasClass(ObjectClasses.User))
                {
                    return [];
                }

                return [IsLockedOut(entry)
                    ? ((int)UserAccountControl.Lockout).ToString(CultureInfo.InvariantCulture)
                    : "0"];
            case Attr.AllowedAttributesEffective:
                var permissions = _permissions.TryGetValue(entry.Dn, out var configured) ? configured : DefaultPermissions;
                return permissions.IsKnown ? [.. permissions.WritableAttributes] : [];
        }

        if (Schema.FindAttribute(name) is { IsBackLink: true, LinkId: { } linkId }
            && Schema.Attributes.FirstOrDefault(a => a.LinkId == linkId - 1) is { } forward)
        {
            return _entries.Values
                .Where(e => !e.IsDeleted && e.Attributes.TryGetValue(forward.LdapName, out var values)
                    && values.OfType<string>().Any(v => DistinguishedName.TryParse(v, out var target) && target == entry.Dn))
                .OrderBy(e => e.Sequence)
                .Select(e => (object)e.Dn.ToString())
                .ToList();
        }

        return entry.Attributes.TryGetValue(name, out var stored) ? stored : [];
    }

    private DirectoryObject ToDirectoryObject(Entry entry, IReadOnlyList<string> requested, bool ranged = true, bool extendedDn = false)
    {
        var names = new List<string>();
        foreach (var attribute in requested)
        {
            if (attribute == "*")
            {
                names.AddRange(entry.Attributes.Keys);
                names.AddRange(Schema.Attributes.Where(a => a.IsBackLink).Select(a => a.LdapName));
            }
            else if (attribute is not ("1.1" or "+"))
            {
                names.Add(Canonical(attribute));
            }
        }

        var attributes = new List<AttributeValues>();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var values = ValuesOf(entry, name);
            if (values.Count == 0)
            {
                continue;
            }

            if (extendedDn)
            {
                values = values.Select(v => ToExtendedValue(name, v)).ToList();
            }

            if (ranged && _maxValueRange is { } max && values.Count > max)
            {
                var description = string.Create(CultureInfo.InvariantCulture, $"{name};range=0-{max - 1}");
                attributes.Add(new AttributeValues(description, values.Take(max).Select(CloneValue)));
            }
            else
            {
                attributes.Add(new AttributeValues(name, values.Select(CloneValue)));
            }
        }

        return new DirectoryObject(entry.Dn.ToString(), attributes);
    }

    private void ApplyCreationDefaults(Entry entry)
    {
        var attributes = entry.Attributes;
        var now = AdConvert.ToGeneralizedTime(_timeProvider.GetUtcNow());
        attributes[Attr.Name] = [entry.Dn.Name];
        attributes[Canonical(entry.Dn.Leaf.Type)] = [entry.Dn.Name];
        attributes.TryAdd(Attr.ObjectGuid, [NextGuid().ToByteArray()]);
        attributes.TryAdd(Attr.WhenCreated, [now]);
        attributes.TryAdd(Attr.WhenChanged, [now]);
        attributes.TryAdd("instanceType", ["4"]);
        var usn = (++_usn).ToString(CultureInfo.InvariantCulture);
        attributes.TryAdd(Attr.UsnCreated, [usn]);
        attributes.TryAdd(Attr.UsnChanged, [usn]);
        if (!attributes.ContainsKey(Attr.ObjectCategory) && attributes.TryGetValue(Attr.ObjectClass, out var classes)
            && classes.OfType<string>().Select(c => Schema.FindClass(c)?.DefaultObjectCategory).LastOrDefault(c => c is not null) is { } category)
        {
            attributes[Attr.ObjectCategory] = [category];
        }
    }

    /// <summary>What a DC fills in for new security principals.</summary>
    private void ApplyPrincipalDefaults(Entry entry, IReadOnlyList<string> classes)
    {
        var attributes = entry.Attributes;
        var isComputer = classes.Contains(ObjectClasses.Computer, StringComparer.OrdinalIgnoreCase);
        var isUser = classes.Contains(ObjectClasses.User, StringComparer.OrdinalIgnoreCase);
        var isGroup = classes.Contains(ObjectClasses.Group, StringComparer.OrdinalIgnoreCase);
        if (!isUser && !isGroup)
        {
            return;
        }

        attributes[Attr.ObjectSid] = [AdConvert.SidToBytes($"{DomainSid}-{++_nextRid}")];
        attributes.TryAdd(Attr.SamAccountName, [$"${Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}-{_nextRid}"]);
        if (isGroup)
        {
            attributes.TryAdd(Attr.GroupType, [GroupTypes.Compose(GroupScope.Global, security: true).ToString(CultureInfo.InvariantCulture)]);
            var groupType = int.Parse((string)attributes[Attr.GroupType][0], CultureInfo.InvariantCulture);
            var scope = GroupTypes.GetScope(groupType);
            var security = GroupTypes.IsSecurity(groupType);
            var type = scope is GroupScope.DomainLocal or GroupScope.BuiltinLocal
                ? (security ? SamAccountType.AliasObject : SamAccountType.NonSecurityAliasObject)
                : (security ? SamAccountType.GroupObject : SamAccountType.NonSecurityGroupObject);
            attributes[Attr.SamAccountType] = [type.ToString(CultureInfo.InvariantCulture)];
            return;
        }

        attributes[Attr.SamAccountType] = [(isComputer ? SamAccountType.MachineAccount : SamAccountType.UserObject).ToString(CultureInfo.InvariantCulture)];
        attributes.TryAdd(Attr.UserAccountControl, [(isComputer ? 0x1020 : 0x222).ToString(CultureInfo.InvariantCulture)]);
        attributes.TryAdd(Attr.PrimaryGroupId, [(isComputer ? WellKnownRids.DomainComputers : WellKnownRids.DomainUsers).ToString(CultureInfo.InvariantCulture)]);
        attributes.TryAdd(Attr.PwdLastSet, ["0"]);
        attributes.TryAdd(Attr.AccountExpires, [AccountExpiresNever]);
    }

    /// <summary>objectClass as AD stores it: the full inheritance chain from top to the most specific class.</summary>
    private List<string> ExpandClasses(IEnumerable<string> requested)
    {
        var chains = new List<List<string>>();
        foreach (var name in requested)
        {
            var chain = new List<string>();
            var current = Schema.FindClass(name);
            var canonical = current?.LdapName ?? name;
            chain.Insert(0, canonical);
            while (current?.SubClassOf is { Length: > 0 } parent && !chain.Contains(parent, StringComparer.OrdinalIgnoreCase))
            {
                chain.Insert(0, parent);
                current = Schema.FindClass(parent);
            }

            chains.Add(chain);
        }

        var result = chains.OrderByDescending(c => c.Count).First().ToList();
        foreach (var name in chains.SelectMany(c => c))
        {
            if (!result.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(name);
            }
        }

        return result;
    }

    private List<object> ToValues(string attribute, object value)
    {
        var definition = Schema.FindAttribute(attribute);
        switch (value)
        {
            case string text when definition?.Syntax == AttributeSyntax.Sid && text.StartsWith("S-", StringComparison.OrdinalIgnoreCase):
                return [AdConvert.SidToBytes(text)];
            case string text:
                return [text];
            case byte[] bytes:
                return [bytes.Clone()];
            case Guid guid:
                return [guid.ToByteArray()];
            case bool flag:
                return [flag ? "TRUE" : "FALSE"];
            case DateTimeOffset when Attr.FileTimeAttributes.Contains(attribute):
                return [AdConvert.ToFileTime((DateTimeOffset)value).ToString(CultureInfo.InvariantCulture)];
            case DateTimeOffset timestamp:
                return [AdConvert.ToGeneralizedTime(timestamp)];
            case IFormattable formattable:
                return [formattable.ToString(null, CultureInfo.InvariantCulture)];
            case IEnumerable sequence:
                return sequence.Cast<object?>().Where(v => v is not null).SelectMany(v => ToValues(attribute, v!)).ToList();
            default:
                return [value.ToString() ?? string.Empty];
        }
    }

    /// <summary>
    /// Records a write of <paramref name="entry"/>: new uSNChanged/whenChanged (only the written object - AD does not
    /// bump referencing or descendant objects) and replication metadata for <paramref name="changedAttributes"/>.
    /// </summary>
    private void Touch(Entry entry, IEnumerable<string> changedAttributes)
    {
        entry.Attributes[Attr.WhenChanged] = [AdConvert.ToGeneralizedTime(_timeProvider.GetUtcNow())];
        var usn = ++_usn;
        entry.Attributes[Attr.UsnChanged] = [usn.ToString(CultureInfo.InvariantCulture)];
        RecordMetadata(entry, changedAttributes, usn);
    }

    private string FileTimeNow() => AdConvert.ToFileTime(_timeProvider.GetUtcNow()).ToString(CultureInfo.InvariantCulture);

    private Guid NextGuid()
    {
        var counter = ++_guidCounter;
        return new Guid($"a0d10000-0000-4000-8000-{counter:x12}");
    }

    private static uint? RidOf(Entry entry) =>
        entry.Attributes.TryGetValue(Attr.ObjectSid, out var values) && values is [byte[] sid]
            ? AdConvert.GetRid(AdConvert.SidToString(sid))
            : null;

    private bool ValueEquals(AttributeDefinition definition, object a, object b)
    {
        if (a is byte[] || b is byte[])
        {
            return Bytes(a).AsSpan().SequenceEqual(Bytes(b));
        }

        return CompareValues(definition, a, Encoding.UTF8.GetBytes((string)b), (string)b) == 0;
    }

    private static byte[] Bytes(object value) => value as byte[] ?? Encoding.UTF8.GetBytes((string)value);

    private static string Text(object value) => value as string ?? Encoding.UTF8.GetString((byte[])value);

    private static object CloneValue(object value) => value is byte[] bytes ? bytes.Clone() : value;

    private string DisplayText(string attribute, object value)
    {
        if (value is string text)
        {
            return text;
        }

        var bytes = (byte[])value;
        if (Schema.FindAttribute(attribute)?.Syntax == AttributeSyntax.Sid)
        {
            return AdConvert.SidToString(bytes);
        }

        return bytes.Length == 16 && attribute.EndsWith("guid", StringComparison.OrdinalIgnoreCase)
            ? new Guid(bytes).ToString("D")
            : Convert.ToHexString(bytes);
    }

    private static int CodeFor(DirectoryErrorKind kind) => kind switch
    {
        DirectoryErrorKind.Busy => 51,
        DirectoryErrorKind.ServerUnavailable => 52,
        DirectoryErrorKind.Timeout => 85,
        DirectoryErrorKind.AccessDenied => 50,
        DirectoryErrorKind.NoSuchObject => 32,
        DirectoryErrorKind.AlreadyExists => 68,
        DirectoryErrorKind.ConstraintViolation => 19,
        DirectoryErrorKind.PasswordPolicy => 19,
        DirectoryErrorKind.ObjectClassViolation => 65,
        DirectoryErrorKind.NotAllowedOnNonLeaf => 66,
        DirectoryErrorKind.UnwillingToPerform => 53,
        DirectoryErrorKind.NoSuchAttribute => 16,
        DirectoryErrorKind.AttributeOrValueExists => 20,
        DirectoryErrorKind.InvalidSyntax => 21,
        DirectoryErrorKind.InvalidCredentials => 49,
        DirectoryErrorKind.AccountRestriction => 49,
        DirectoryErrorKind.SizeLimitExceeded => 4,
        DirectoryErrorKind.StrongAuthRequired => 8,
        DirectoryErrorKind.UnsupportedControl => 12,
        DirectoryErrorKind.Referral => 10,
        DirectoryErrorKind.Cancelled => 88,
        _ => 80,
    };

    private static DirectoryException Error(int ldapCode, string message, string? distinguishedName, DirectoryErrorKind? kind = null) =>
        new(kind ?? DirectoryException.Classify(ldapCode, null), message)
        {
            LdapResultCode = ldapCode,
            DistinguishedName = distinguishedName,
            ServerMessage = $"{ldapCode:X8}: FakeDirectory: {message}",
        };

    private sealed class Entry
    {
        public Entry(DistinguishedName dn, long sequence)
        {
            Dn = dn;
            Sequence = sequence;
        }

        public DistinguishedName Dn { get; set; }

        public long Sequence { get; }

        public Dictionary<string, List<object>> Attributes { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Forward links that pointed to this object when it was deleted (restored by the Recycle Bin).</summary>
        public List<(DistinguishedName Source, string Attribute)> DeactivatedLinks { get; } = [];

        /// <summary>Attributes AD removes on deletion and recomputes on restore (objectCategory, sAMAccountType).</summary>
        public Dictionary<string, List<object>> Recomputed { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Order of deletion (0 for objects that were never deleted).</summary>
        public long DeletedSequence { get; set; }

        public bool IsDeleted => First(Attr.IsDeleted) is { } value && value.Equals("TRUE", StringComparison.OrdinalIgnoreCase);

        public bool IsRecycled => First(Attr.IsRecycled) is { } value && value.Equals("TRUE", StringComparison.OrdinalIgnoreCase);

        public string? First(string attribute) =>
            Attributes.TryGetValue(attribute, out var values) && values.Count > 0 ? values[0] as string : null;

        public bool HasClass(string objectClass) =>
            Attributes.TryGetValue(Attr.ObjectClass, out var values)
            && values.OfType<string>().Contains(objectClass, StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, List<object>> CloneAttributes() =>
            Attributes.ToDictionary(kv => kv.Key, kv => kv.Value.Select(CloneValue).ToList(), StringComparer.OrdinalIgnoreCase);

        public void ReplaceAttributes(Dictionary<string, List<object>> attributes) => Attributes = attributes;
    }

    private sealed class InjectedFailure(FakeOperationKind kind, DistinguishedName? dn, DirectoryErrorKind error, int remaining)
    {
        public FakeOperationKind Kind { get; } = kind;

        public DistinguishedName? Dn { get; } = dn;

        public DirectoryErrorKind Error { get; } = error;

        public int Remaining { get; set; } = remaining;

        /// <summary>The call is applied first and the error is reported afterwards (lost response).</summary>
        public bool AfterApply { get; init; }
    }

    private sealed class Watcher(FakeDirectorySession owner, DistinguishedName container, Action<string> callback) : IAsyncDisposable
    {
        public DistinguishedName Container { get; } = container;

        public Action<string> Callback { get; } = callback;

        public ValueTask DisposeAsync()
        {
            lock (owner._gate)
            {
                owner._watchers.Remove(this);
            }

            return ValueTask.CompletedTask;
        }
    }
}
