using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;

namespace AdMng.Core.Batch.Creation;

// Uniqueness (docs/dev/phase3-bulk-create.md 2.4): the namespaces of sAMAccountName (domain), userPrincipalName and the mail
// addresses (forest, through the GC session when there is one) and the object name (the container), batched lookups cached in
// BulkCreateLookups, the reservations of the batch, the implicit UPN, deleted objects and the collisions with their
// disambiguation (roots and AppendNumber) or errors.
public sealed partial class BulkCreatePlanner
{
    private enum NamespaceKind
    {
        Sam,
        Upn,
        Address,
        Name,
    }

    private sealed partial class ComputeRun
    {
        private readonly Dictionary<string, List<int>> _operatorReserved = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _assigned = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Namespace?> _namespaces = new(StringComparer.OrdinalIgnoreCase);

        // ------------------------------------------------------------------ namespaces

        /// <summary>sAMAccountName in the domain <paramref name="domainDns"/>: the window's session, or the GC for another domain of the forest.</summary>
        private Namespace? SamNamespace(string domainDns)
        {
            var key = "sam\u0001" + domainDns;
            if (_namespaces.TryGetValue(key, out var cached))
            {
                return cached;
            }

            Namespace? created = null;
            if (Is(domainDns, _windowDomain))
            {
                created = new Namespace(NamespaceKind.Sam, "sam|" + Key(_session.Info.DefaultNamingContext), _session, _session.Info.DefaultNamingContext)
                {
                    IncludeDeleted = _session.Info.RecycleBinEnabled,
                };
            }
            else if (_lookups.ForestReader is { } gc && _forest.NamingContextOf(domainDns) is { } nc)
            {
                created = new Namespace(NamespaceKind.Sam, "sam|" + Key(nc), gc, nc) { DomainNamingContext = nc, IncludeDeleted = _session.Info.RecycleBinEnabled };
            }

            _namespaces[key] = created;
            return created;
        }

        private Namespace ForestNamespace(NamespaceKind kind)
        {
            var key = kind.ToString();
            if (_namespaces.TryGetValue(key, out var cached) && cached is not null)
            {
                return cached;
            }

            var prefix = kind == NamespaceKind.Upn ? "upn|" : "address|";
            var created = _scope == UniquenessScope.Forest && _lookups.ForestReader is { } gc
                ? new Namespace(kind, prefix + "forest", gc, string.Empty) { IsForestBase = true, IncludeDeleted = _session.Info.RecycleBinEnabled }
                : new Namespace(kind, prefix + Key(_session.Info.DefaultNamingContext), _session, _session.Info.DefaultNamingContext)
                {
                    IncludeDeleted = _session.Info.RecycleBinEnabled,
                };
            _namespaces[key] = created;
            return created;
        }

        private Namespace NameNamespace()
        {
            const string key = "name";
            if (_namespaces.TryGetValue(key, out var cached) && cached is not null)
            {
                return cached;
            }

            var created = new Namespace(NamespaceKind.Name, "name|" + Key(_containerDn), _session, _containerDn) { Scope = SearchScope.OneLevel };
            _namespaces[key] = created;
            return created;
        }

        /// <summary>The namespace a value of <paramref name="attribute"/> is unique in (null: none, e.g. a contact's sAMAccountName).</summary>
        private Namespace? NamespaceOf(string attribute) => attribute switch
        {
            _ when Is(attribute, Attr.SamAccountName) => HasSamAccountName(_kind) ? SamNamespace(_windowDomain) : null,
            _ when Is(attribute, Attr.UserPrincipalName) => ForestNamespace(NamespaceKind.Upn),
            _ when Is(attribute, Attr.Mail) => ForestNamespace(NamespaceKind.Address),
            _ when Is(attribute, Attr.Cn) => NameNamespace(),
            _ => null,
        };

        /// <summary>
        /// The uniqueness keys of a row's values (those <paramref name="include"/> selects), each value with its implicit-UPN
        /// counterpart (2.4): a sAMAccountName as <c>sam@window domain</c> among the UPNs, a UPN whose suffix is a domain of the
        /// forest as its prefix among that domain's sAMAccountNames. A real key comes before its implicit one.
        /// </summary>
        private IEnumerable<UniqueKey> Keys(Dictionary<string, Draft> values, Func<Draft, bool> include)
        {
            foreach (var attribute in UniqueAttributes)
            {
                if (!values.TryGetValue(attribute, out var draft) || !draft.HasValue || !include(draft) || NamespaceOf(attribute) is not { } ns)
                {
                    continue;
                }

                var value = draft.First;
                yield return new UniqueKey(ns, value, attribute, isImplicit: false);
                if (!IsUserKind(_kind))
                {
                    // The implicit UPN is a logon name: groups and contacts have none (and computers log on as NAME$).
                    continue;
                }

                if (Is(attribute, Attr.SamAccountName))
                {
                    yield return new UniqueKey(ForestNamespace(NamespaceKind.Upn), value + "@" + _windowDomain, attribute, isImplicit: true);
                }
                else if (Is(attribute, Attr.UserPrincipalName) && value.LastIndexOf('@') is var at and > 0
                    && _forest.IsDomain(value[(at + 1)..]) && SamNamespace(value[(at + 1)..]) is { } sam)
                {
                    yield return new UniqueKey(sam, value[..at], attribute, isImplicit: true);
                }
            }
        }

        // ------------------------------------------------------------------ lookups

        /// <summary>Asks the directory about every key not known yet: batched equality lookups per namespace, the container listing for names.</summary>
        private async Task EnsureKnownAsync(IEnumerable<UniqueKey> keys)
        {
            var unknown = keys.Where(k => !_lookups.Index(k.Namespace.Key).IsKnown(k.Value)).GroupBy(k => k.Namespace.Key).ToList();
            if (unknown.Count == 0)
            {
                return;
            }

            var batches = new List<(Namespace Namespace, string[] Values)>();
            foreach (var group in unknown)
            {
                var ns = group.First().Namespace;
                if (ns.Kind == NamespaceKind.Name)
                {
                    await ListNamesAsync(ns).ConfigureAwait(false);
                    continue;
                }

                var values = group.Select(k => k.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (values.Count > FullListingThreshold)
                {
                    await ListNamespaceAsync(ns).ConfigureAwait(false);
                    continue;
                }

                batches.AddRange(values.Chunk(LookupBatchSize).Select(c => (ns, c)));
            }

            await Parallel.ForEachAsync(
                batches,
                new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = _cancellationToken },
                async (batch, _) => await LookupBatchAsync(batch.Namespace, batch.Values).ConfigureAwait(false)).ConfigureAwait(false);
        }

        /// <summary>
        /// Every value of a namespace in one paged search (<c>(attribute=*)</c>): used instead of the batched equality lookups
        /// when more than <see cref="FullListingThreshold"/> values are unknown – one query instead of hundreds (rule 6).
        /// </summary>
        private async Task ListNamespaceAsync(Namespace ns)
        {
            var index = _lookups.Index(ns.Key);
            if (index.IsComplete)
            {
                return;
            }

            var filter = ns.Kind switch
            {
                NamespaceKind.Sam => LdapFilter.Present(Attr.SamAccountName),
                NamespaceKind.Upn => LdapFilter.Present(Attr.UserPrincipalName),
                _ => LdapFilter.Or(LdapFilter.Present(Attr.Mail), LdapFilter.Present(Attr.ProxyAddresses)),
            };
            var found = new List<(string Value, LookupOwner Owner)>();
            foreach (var entry in await SearchAsync(ns, filter).ConfigureAwait(false))
            {
                var owner = OwnerOf(entry);
                found.AddRange(ValuesOf(ns.Kind, entry).Select(v => (v, owner)));
            }

            index.Complete(found);
        }

        private async Task LookupBatchAsync(Namespace ns, string[] values)
        {
            var filter = LdapFilter.Or(values.Select(v => EqualFilter(ns.Kind, v)));
            var wanted = new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
            var found = new List<(string Value, LookupOwner Owner)>();
            foreach (var entry in await SearchAsync(ns, filter).ConfigureAwait(false))
            {
                var owner = OwnerOf(entry);
                found.AddRange(ValuesOf(ns.Kind, entry).Where(wanted.Contains).Select(v => (v, owner)));
            }

            _lookups.Index(ns.Key).Record(values, found);
        }

        /// <summary>One search per (namespace, prefix, suffix) (2.4 step 1): every value under it is known afterwards.</summary>
        private async Task PrefixQueryAsync(Namespace ns, string prefix, string suffix)
        {
            var index = _lookups.Index(ns.Key);
            if (index.IsCovered(prefix, suffix))
            {
                return;
            }

            var filter = PrefixFilter(ns.Kind, prefix, suffix);
            var found = new List<(string Value, LookupOwner Owner)>();
            foreach (var entry in await SearchAsync(ns, filter).ConfigureAwait(false))
            {
                var owner = OwnerOf(entry);
                found.AddRange(ValuesOf(ns.Kind, entry)
                    .Where(v => v.Length >= prefix.Length + suffix.Length
                        && v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && v.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    .Select(v => (v, owner)));
            }

            index.Cover(prefix, suffix, found);
        }

        /// <summary>
        /// The object names of the container: one paged one-level search of its children (any class, any RDN type), and one
        /// one-level search of the domain's Deleted Objects for objects whose <c>lastKnownParent</c> is the container (2.4).
        /// </summary>
        private async Task ListNamesAsync(Namespace ns)
        {
            var live = _lookups.Index(ns.Key);
            if (!live.IsComplete)
            {
                var found = new List<(string Value, LookupOwner Owner)>();
                if (_containerExists)
                {
                    foreach (var entry in await SearchAsync(ns, "(objectClass=*)").ConfigureAwait(false))
                    {
                        found.Add((entry.Dn.Name, OwnerOf(entry)));
                    }
                }

                live.Complete(found);
            }

            var deleted = _lookups.Index("deleted|" + Key(_containerDn));
            if (deleted.IsComplete)
            {
                return;
            }

            var tombstones = new List<(string Value, LookupOwner Owner)>();
            if (_containerExists && _session.Info.RecycleBinEnabled)
            {
                var search = new DirectorySearch
                {
                    BaseDn = RecycleBin.GetDeletedObjectsContainer(_session.Info.DefaultNamingContext),
                    Scope = SearchScope.OneLevel,
                    Filter = LdapFilter.Equal(Attr.LastKnownParent, _containerDn),
                    Attributes = [Attr.MsDsLastKnownRdn, Attr.IsDeleted, Attr.IsRecycled],
                    PageSize = 1000,
                    IncludeDeleted = true,
                };
                try
                {
                    _lookups.CountQuery();
                    await foreach (var entry in _session.SearchAsync(search, _cancellationToken).ConfigureAwait(false))
                    {
                        if (entry.GetString(Attr.MsDsLastKnownRdn) is { Length: > 0 } name)
                        {
                            tombstones.Add((name, new LookupOwner(entry.DistinguishedName, name, true, entry.GetBoolean(Attr.IsRecycled) == true)));
                        }
                    }
                }
                catch (DirectoryException ex) when (ex.Kind is DirectoryErrorKind.AccessDenied or DirectoryErrorKind.NoSuchObject or DirectoryErrorKind.UnwillingToPerform)
                {
                    // Deleted Objects is readable by administrators only: without it there is no NameInRecycleBin warning.
                }
            }

            deleted.Complete(tombstones);
        }

        private async Task<List<DirectoryObject>> SearchAsync(Namespace ns, string filter)
        {
            var attributes = ns.Kind switch
            {
                NamespaceKind.Sam => new[] { Attr.SamAccountName, Attr.IsDeleted, Attr.IsRecycled, Attr.MsDsLastKnownRdn },
                NamespaceKind.Upn => [Attr.UserPrincipalName, Attr.IsDeleted, Attr.IsRecycled, Attr.MsDsLastKnownRdn],
                NamespaceKind.Address => [Attr.Mail, Attr.ProxyAddresses, Attr.IsDeleted, Attr.IsRecycled, Attr.MsDsLastKnownRdn],
                _ => [Attr.Name],
            };
            while (true)
            {
                var search = new DirectorySearch
                {
                    BaseDn = ns.BaseDn,
                    Scope = ns.Scope,
                    Filter = filter,
                    Attributes = attributes,
                    PageSize = 1000,
                    IncludeDeleted = ns.IncludeDeleted,
                };
                try
                {
                    _lookups.CountQuery();
                    var result = new List<DirectoryObject>();
                    await foreach (var entry in ns.Reader.SearchAsync(search, _cancellationToken).ConfigureAwait(false))
                    {
                        if (ns.DomainNamingContext is not { } nc || Ldap.DistinguishedName.TryParse(entry.DistinguishedName, out var dn)
                            && Ldap.DistinguishedName.TryParse(nc, out var root) && (dn == root || dn.IsDescendantOf(root)))
                        {
                            result.Add(entry);
                        }
                    }

                    return result;
                }
                catch (DirectoryException ex) when (ns.IsForestBase && ns.BaseDn.Length == 0
                    && ex.Kind is DirectoryErrorKind.NoSuchObject or DirectoryErrorKind.UnwillingToPerform or DirectoryErrorKind.InvalidSyntax or DirectoryErrorKind.Referral)
                {
                    // A server that refuses the empty base of the whole GC gets the forest root (subtree) – 2.2.
                    ns.BaseDn = _session.Info.RootDomainNamingContext ?? _session.Info.DefaultNamingContext;
                }
            }
        }

        private static string EqualFilter(NamespaceKind kind, string value) => kind switch
        {
            NamespaceKind.Sam => LdapFilter.Equal(Attr.SamAccountName, value),
            NamespaceKind.Upn => LdapFilter.Equal(Attr.UserPrincipalName, value),
            NamespaceKind.Address => LdapFilter.Or(LdapFilter.Equal(Attr.Mail, value), LdapFilter.Equal(Attr.ProxyAddresses, "smtp:" + value)),
            _ => LdapFilter.Equal(Attr.Name, value),
        };

        private static string PrefixFilter(NamespaceKind kind, string prefix, string suffix)
        {
            string Part(string attribute, string start) =>
                LdapFilter.And(LdapFilter.StartsWith(attribute, start), suffix.Length > 0 ? LdapFilter.EndsWith(attribute, suffix) : null);
            return kind switch
            {
                NamespaceKind.Sam => Part(Attr.SamAccountName, prefix),
                NamespaceKind.Upn => Part(Attr.UserPrincipalName, prefix),
                NamespaceKind.Address => LdapFilter.Or(Part(Attr.Mail, prefix), Part(Attr.ProxyAddresses, "smtp:" + prefix)),
                _ => Part(Attr.Name, prefix),
            };
        }

        /// <summary>The values an entry holds in a namespace (<c>proxyAddresses</c>: the SMTP addresses without their prefix).</summary>
        private static IEnumerable<string> ValuesOf(NamespaceKind kind, DirectoryObject entry) => kind switch
        {
            NamespaceKind.Sam => entry.GetStrings(Attr.SamAccountName),
            NamespaceKind.Upn => entry.GetStrings(Attr.UserPrincipalName),
            NamespaceKind.Address => entry.GetStrings(Attr.Mail)
                .Concat(entry.GetStrings(Attr.ProxyAddresses)
                    .Where(p => p.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase))
                    .Select(p => p[5..])),
            _ => [entry.Dn.Name],
        };

        private static LookupOwner OwnerOf(DirectoryObject entry)
        {
            var isDeleted = entry.GetBoolean(Attr.IsDeleted) == true;
            var name = isDeleted && entry.GetString(Attr.MsDsLastKnownRdn) is { Length: > 0 } lastKnown ? lastKnown : entry.Dn.Name;
            return new LookupOwner(entry.DistinguishedName, name, isDeleted, entry.GetBoolean(Attr.IsRecycled) == true);
        }

        /// <summary>The holders of a key: live and deleted objects (recycled ones never; deleted copies of released values neither).</summary>
        private List<LookupOwner> OwnersOf(UniqueKey key)
        {
            var owners = new List<LookupOwner>(_lookups.Index(key.Namespace.Key).Owners(key.Value));
            if (key.Namespace.Kind == NamespaceKind.Name)
            {
                owners.AddRange(_lookups.Index("deleted|" + Key(_containerDn)).Owners(key.Value));
            }

            owners.RemoveAll(o => o.IsRecycled || (o.IsDeleted && _released.Contains(key.Value)));
            return owners;
        }

        // ------------------------------------------------------------------ reservations and checks

        private void ReserveOperator(UniqueKey key, int row)
        {
            if (!_operatorReserved.TryGetValue(key.Composite, out var rows))
            {
                _operatorReserved[key.Composite] = rows = [];
            }

            if (!rows.Contains(row))
            {
                rows.Add(row);
            }
        }

        private void Reserve(RowState state, Dictionary<string, Draft> values, Func<Draft, bool> include)
        {
            foreach (var key in Keys(values, include))
            {
                _assigned.TryAdd(key.Composite, state.Number);
            }
        }

        /// <summary>
        /// Whether a key is free for a row: not reserved by another row (an operator's value of any row, a value assigned to an
        /// earlier row), not held by a live object; a value held only by a deleted object is <see cref="Collision.IsDeletedOnly"/>
        /// unless <paramref name="deletedIsTaken"/> (counters without <see cref="BulkCreateRules.ReuseRecycleBinNumbers"/>).
        /// </summary>
        private Collision? Check(RowState state, UniqueKey key, bool deletedIsTaken)
        {
            if (_operatorReserved.TryGetValue(key.Composite, out var rows) && rows.Find(r => r != state.Number) is var other and > 0)
            {
                return new Collision(key.Attribute, BulkCreateIssueCodes.DuplicateRow, Text(other));
            }

            if (_assigned.TryGetValue(key.Composite, out var assigned) && assigned != state.Number)
            {
                return new Collision(key.Attribute, BulkCreateIssueCodes.DuplicateRow, Text(assigned));
            }

            var owners = OwnersOf(key);
            if (owners.Find(o => !o.IsDeleted) is { } live)
            {
                return new Collision(key.Attribute, key.IsImplicit ? BulkCreateIssueCodes.ImplicitUpnClash : BulkCreateIssueCodes.DuplicateInDirectory, live.Name);
            }

            if (!key.IsImplicit && owners.Find(o => o.IsDeleted) is { } deleted)
            {
                return deletedIsTaken
                    ? new Collision(key.Attribute, BulkCreateIssueCodes.DuplicateInDirectory, deleted.Name)
                    : new Collision(key.Attribute, BulkCreateIssueCodes.NameInRecycleBin, deleted.Name) { IsDeletedOnly = true };
            }

            return null;
        }

        /// <summary>The first collision per unique attribute of a row (a real key before its implicit one); deleted-only holders go to <paramref name="recycled"/>.</summary>
        private List<Collision> FindCollisions(RowState state, Dictionary<string, Draft> values, HashSet<string>? recycled)
        {
            var result = new List<Collision>();
            foreach (var group in Keys(values, _ => true).GroupBy(k => k.Attribute, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var key in group)
                {
                    var collision = Check(state, key, deletedIsTaken: false);
                    if (collision is null)
                    {
                        continue;
                    }

                    if (collision.IsDeletedOnly)
                    {
                        if (!values[key.Attribute].IsCounter)
                        {
                            recycled?.Add(key.Attribute);
                        }

                        continue;
                    }

                    result.Add(collision);
                    break;
                }
            }

            return result;
        }

        // ------------------------------------------------------------------ name-based values and collisions (2.4)

        private async Task ResolveUniquenessAsync()
        {
            var rows = _rows.Where(r => r.Fixed is null).ToList();
            await EnsureKnownAsync(rows.SelectMany(r => Keys(r.Values, _ => true))).ConfigureAwait(false);
            _cancellationToken.ThrowIfCancellationRequested();

            // The disambiguated candidates (2–6) of every collided value in one more batched pass.
            var prefetch = new List<UniqueKey>();
            foreach (var state in rows.Where(CanDisambiguate))
            {
                var roots = FindCollisions(state, state.Values, null).SelectMany(c => Roots(state.Values, c.Attribute)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                for (var level = 2; level <= 6 && roots.Count > 0; level++)
                {
                    var bumps = roots.ToDictionary(r => r, _ => level, StringComparer.OrdinalIgnoreCase);
                    prefetch.AddRange(Keys(Evaluate(state, NumberOf(state), bumps, null), d => !d.IsCounter));
                }
            }

            await EnsureKnownAsync(prefetch).ConfigureAwait(false);
            foreach (var state in rows)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                await ResolveRowAsync(state).ConfigureAwait(false);
            }
        }

        private bool CanDisambiguate(RowState state) =>
            !state.IsFrozen && _rules.Collisions == CollisionStrategy.AppendNumber && _kind != ObjectKind.Computer;

        private static string? NumberOf(RowState state) => state.CounterNumber is { } number ? Text(number) : null;

        private async Task ResolveRowAsync(RowState state)
        {
            var original = state.Values;
            var values = original;
            var bumps = new Dictionary<string, int>(state.Bumps, StringComparer.OrdinalIgnoreCase);
            var recycled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var failed = false;
            while (true)
            {
                recycled.Clear();
                var collisions = FindCollisions(state, values, recycled);
                if (collisions.Count == 0)
                {
                    break;
                }

                var roots = collisions.Select(c => (Collision: c, Roots: CanBump(values, c.Attribute) ? Roots(values, c.Attribute) : [])).ToList();
                if (!CanDisambiguate(state) || roots.Any(r => r.Roots.Count == 0)
                    || roots.SelectMany(r => r.Roots).Distinct(StringComparer.OrdinalIgnoreCase).Any(r => bumps.GetValueOrDefault(r, 1) >= MaxDisambiguator))
                {
                    foreach (var collision in collisions)
                    {
                        state.Add(Error(collision.Code, CollisionMessage(collision.Code), collision.Attribute, collision.Argument));
                    }

                    failed = true;
                    break;
                }

                foreach (var root in roots.SelectMany(r => r.Roots).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    bumps[root] = bumps.GetValueOrDefault(root, 1) + 1;
                }

                values = Evaluate(state, NumberOf(state), bumps, null);
                await EnsureKnownAsync(Keys(values, _ => true)).ConfigureAwait(false);
            }

            foreach (var attribute in recycled)
            {
                state.Add(Warning(BulkCreateIssueCodes.NameInRecycleBin, "An object in the AD Recycle Bin holds this name.", attribute));
            }

            if (!ReferenceEquals(values, original))
            {
                foreach (var attribute in UniqueAttributes.Concat(values.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (values.TryGetValue(attribute, out var now) && now.HasValue
                        && (!original.TryGetValue(attribute, out var before) || !string.Equals(before.First, now.First, StringComparison.Ordinal)))
                    {
                        now.Adjusted = true;
                        if (IsUnique(attribute) || bumps.ContainsKey(attribute))
                        {
                            state.Add(Warning(BulkCreateIssueCodes.Disambiguated, "The value was taken; a number was appended.", attribute));
                        }
                    }
                }

                state.Values = values;
                state.Bumps = bumps;
            }

            if (!failed)
            {
                Reserve(state, values, _ => true);
            }
        }

        private static string CollisionMessage(string code) => code switch
        {
            BulkCreateIssueCodes.DuplicateRow => "Another row of the batch has the same value.",
            BulkCreateIssueCodes.ImplicitUpnClash => "The value clashes with an implicit UPN (name@domain).",
            _ => "The value is already used in the directory.",
        };

        /// <summary>
        /// A value may be disambiguated (<see cref="CollisionStrategy.AppendNumber"/>): it comes from a rule and does not depend
        /// on the counter – columns, edits, frozen and counter values never change (2.4).
        /// </summary>
        private static bool CanBump(Dictionary<string, Draft> values, string attribute) =>
            values.TryGetValue(attribute, out var draft) && draft.IsRuleValue && !draft.IsCounter;

        /// <summary>
        /// The roots a collision of <paramref name="attribute"/> bumps (2.4): the unique attributes whose value comes from a rule
        /// that references no other unique attribute – the attribute itself when it is one, else the roots it depends on.
        /// </summary>
        private List<string> Roots(Dictionary<string, Draft> values, string attribute)
        {
            bool IsRoot(string candidate) =>
                IsUnique(candidate) && values.TryGetValue(candidate, out var draft) && draft.IsRuleValue && !draft.IsCounter
                && !_patterns.DependsOn(candidate).Any(IsUnique);
            if (IsRoot(attribute))
            {
                return [attribute];
            }

            return [.. _patterns.DependsOn(attribute).Where(IsRoot)];
        }
    }

    /// <summary>Where the values of one unique attribute are looked up (a class: it holds a reader, never a value).</summary>
    private sealed class Namespace(NamespaceKind kind, string key, IDirectoryReader reader, string baseDn)
    {
        public NamespaceKind Kind { get; } = kind;

        /// <summary>The key of its <see cref="LookupIndex"/> in the lookups.</summary>
        public string Key { get; } = key;

        public IDirectoryReader Reader { get; } = reader;

        /// <summary>The search base (the GC's empty base can fall back to the forest root, 2.2).</summary>
        public string BaseDn { get; set; } = baseDn;

        public SearchScope Scope { get; init; } = SearchScope.Subtree;

        /// <summary>The whole GC (base <c>""</c>).</summary>
        public bool IsForestBase { get; init; }

        public bool IncludeDeleted { get; init; }

        /// <summary>Only entries below this naming context count (the GC searched for another domain's sAMAccountName).</summary>
        public string? DomainNamingContext { get; init; }

        public override string ToString() => $"Namespace {Kind}";
    }

    /// <summary>A value of a row in a namespace (a class: it is never printed with its value).</summary>
    private sealed class UniqueKey(Namespace ns, string value, string attribute, bool isImplicit)
    {
        public Namespace Namespace { get; } = ns;

        public string Value { get; } = value;

        public string Attribute { get; } = attribute;

        /// <summary>The implicit-UPN counterpart of the value (2.4).</summary>
        public bool IsImplicit { get; } = isImplicit;

        /// <summary>The reservation key: namespace and value, case-insensitive.</summary>
        public string Composite => Namespace.Key + "\u0001" + Value.ToUpperInvariant();

        public override string ToString() => $"Key {Namespace.Kind} of {Attribute}";
    }

    /// <summary>A collision of a value: its code and the argument of the issue (owner's name or row number).</summary>
    private sealed class Collision(string attribute, string code, string argument)
    {
        public string Attribute { get; } = attribute;

        public string Code { get; } = code;

        public string Argument { get; } = argument;

        public bool IsDeletedOnly { get; init; }

        public override string ToString() => $"Collision {Code} of {Attribute}";
    }
}
