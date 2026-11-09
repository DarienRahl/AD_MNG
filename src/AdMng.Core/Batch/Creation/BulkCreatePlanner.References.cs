using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;

namespace AdMng.Core.Batch.Creation;

// References and run reads (docs/dev/phase3-bulk-create.md 2.5): the option groups with their fine-grained password policy, the
// RID pool, the KB5020276 join owner, the manager of every row, the row's groups with the nesting rules of created groups and
// the possible existing accounts of the same person (2.4).
public sealed partial class BulkCreatePlanner
{
    /// <summary>The arguments of the <see cref="BulkCreateIssueCodes.GroupPasswordPolicy"/> run warning: the group, then – when the PSO is readable – its minimum length and complexity (<c>1</c>/<c>0</c>), which <see cref="BuildPlan"/> applies to a shared password.</summary>
    private const int PsoArguments = 3;

    private const string PsoMinimumLength = "msDS-MinimumPasswordLength";
    private const string PsoComplexity = "msDS-PasswordComplexityEnabled";
    private const string PsoApplied = "msDS-PSOApplied";

    private sealed partial class ComputeRun
    {
        private readonly List<GroupInfo> _optionGroups = [];

        // ------------------------------------------------------------------ run reads

        private async Task ReadOptionGroupsAsync()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dn in _input.Options.GroupDns)
            {
                if (string.IsNullOrWhiteSpace(dn) || !seen.Add(Key(dn)))
                {
                    continue;
                }

                var group = await ReadGroupAsync(dn).ConfigureAwait(false);
                if (group is null)
                {
                    _runIssues.Add(Warning(BulkCreateIssueCodes.GroupNotFound, "An option group does not exist; it is left out.", null, RdnValue(dn)));
                    continue;
                }

                _optionGroups.Add(group);
                if (!group.Writable)
                {
                    _runIssues.Add(Warning(IssueCodes.NotWritable, "The window's account cannot add members to an option group.", Attr.Member));
                }

                if (group.PasswordSettings.Count > 0)
                {
                    _runIssues.Add(await GroupPasswordPolicyAsync(group).ConfigureAwait(false));
                }
            }
        }

        private async Task<ValidationIssue> GroupPasswordPolicyAsync(GroupInfo group)
        {
            const string message = "The group has a fine-grained password policy that applies after the account joins it.";
            try
            {
                var pso = await _lookups.ReadOnceAsync("pso\u0001" + Key(group.PasswordSettings[0]),
                    () => _session.GetAsync(group.PasswordSettings[0], [PsoMinimumLength, PsoComplexity], _cancellationToken)).ConfigureAwait(false);
                if (pso?.GetInt32(PsoMinimumLength) is { } minimum)
                {
                    var complexity = pso.GetBoolean(PsoComplexity) == true ? "1" : "0";
                    return Warning(BulkCreateIssueCodes.GroupPasswordPolicy, message, null, group.Name, Text(minimum), complexity);
                }
            }
            catch (DirectoryException)
            {
                // PSOs are readable by Domain Admins only by default: the warning without the PSO's limits.
            }

            return Warning(BulkCreateIssueCodes.GroupPasswordPolicy, message, null, group.Name);
        }

        /// <summary>A group by DN (option groups, DN cells): class, scope, SID, the PSO applied and whether <c>member</c> is writable.</summary>
        private async Task<GroupInfo?> ReadGroupAsync(string dn)
        {
            DirectoryObject? entry;
            try
            {
                entry = await _lookups.ReadOnceAsync("group\u0001" + Key(dn),
                    () => _session.GetAsync(dn, [Attr.ObjectClass, Attr.GroupType, Attr.ObjectSid, Attr.Name, Attr.SamAccountName, PsoApplied], _cancellationToken))
                    .ConfigureAwait(false);
            }
            catch (DirectoryException ex) when (ex.Kind is DirectoryErrorKind.NoSuchObject or DirectoryErrorKind.InvalidSyntax)
            {
                entry = null;
            }

            if (entry is null || !entry.GetStrings(Attr.ObjectClass).Contains(ObjectClasses.Group, StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }

            var permissions = await ReadPermissionsAsync(entry.DistinguishedName).ConfigureAwait(false);
            return GroupInfo.From(entry, permissions.CanWrite(Attr.Member));
        }

        /// <summary>RID pool of the DC for more than 100 security principals (2.5, best effort).</summary>
        private async Task CheckRidPoolAsync()
        {
            var toCreate = _table.Rows.Count(r => !_input.CreatedRows.ContainsKey(r.Number) && !_input.ExcludedRows.Contains(r.Number));
            if (!HasSamAccountName(_kind) || toCreate <= RidPoolCheckRows)
            {
                return;
            }

            try
            {
                var server = await _lookups.ReadOnceAsync("dc", () => FirstAsync(new DirectorySearch
                {
                    BaseDn = _session.Info.DefaultNamingContext,
                    Filter = LdapFilter.And(LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.Computer), LdapFilter.Equal(Attr.DnsHostName, _session.Info.Server)),
                    Attributes = ["rIDSetReferences"],
                    SizeLimit = 1,
                })).ConfigureAwait(false);
                if (server?.GetString("rIDSetReferences") is not { Length: > 0 } ridSetDn)
                {
                    return;
                }

                var ridSet = await _lookups.ReadOnceAsync("ridset",
                    () => _session.GetAsync(ridSetDn, ["rIDAllocationPool", "rIDNextRID"], _cancellationToken)).ConfigureAwait(false);
                if (ridSet?.GetInt64("rIDAllocationPool") is not { } pool || ridSet.GetInt64("rIDNextRID") is not { } next)
                {
                    return;
                }

                var first = pool & 0xFFFFFFFF;
                var last = pool >> 32;
                var remaining = next < first ? last - first + 1 : Math.Max(0, last - next + 1);
                if (remaining >= toCreate)
                {
                    return;
                }

                var managerDn = Ldap.DistinguishedName.Parse(_session.Info.DefaultNamingContext).Child("CN", "System").Child("CN", "RID Manager$").ToString();
                var manager = await _lookups.ReadOnceAsync("ridmanager",
                    () => _session.GetAsync(managerDn, ["fSMORoleOwner"], _cancellationToken)).ConfigureAwait(false);
                if (IsThisServer(manager?.GetString("fSMORoleOwner")) == false)
                {
                    _runIssues.Add(Warning(BulkCreateIssueCodes.RidPoolLow, "The DC has fewer free RIDs than rows and another DC holds the RID master role."));
                }
            }
            catch (DirectoryException)
            {
                // Best effort: an unreadable RID set gives no warning (2.14, ad-14 (d)).
            }
        }

        /// <summary>KB5020276 (2.5): with a joiner, the window's account should be an owner a reusing join trusts.</summary>
        private async Task CheckJoinOwnerAsync()
        {
            if (_kind != ObjectKind.Computer || string.IsNullOrWhiteSpace(_input.Options.JoinerSid))
            {
                return;
            }

            bool? trusted = null;
            if (_session.Info.BoundUserDn is { Length: > 0 } ownerDn)
            {
                try
                {
                    trusted = await _lookups.ReadOnceAsync("joinowner", () => IsJoinOwnerAsync(ownerDn)).ConfigureAwait(false);
                }
                catch (DirectoryException)
                {
                    trusted = null;
                }
            }

            if (trusted != true)
            {
                _runIssues.Add(Warning(BulkCreateIssueCodes.JoinOwnerNotTrusted, "A join that reuses the computer account fails unless its owner is trusted.",
                    null, _session.Info.BoundIdentity));
            }
        }

        /// <summary>Domain Admins, Enterprise Admins or BUILTIN\Administrators (the owners KB5020276 trusts, as <c>PrivilegeCheck.IsJoinOwnerAsync</c>).</summary>
        private async Task<bool> IsJoinOwnerAsync(string userDn)
        {
            static bool Trusted(string? sid) =>
                string.Equals(sid, "S-1-5-32-544", StringComparison.OrdinalIgnoreCase)
                || (sid is not null && sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase)
                    && AdConvert.GetRid(sid) is WellKnownRids.DomainAdmins or WellKnownRids.EnterpriseAdmins);
            var user = await _session.GetAsync(userDn, [Attr.TokenGroups], _cancellationToken).ConfigureAwait(false);
            if (user is not null && user.TryGet(Attr.TokenGroups, out var tokenGroups) && tokenGroups.Count > 0)
            {
                return tokenGroups.OfType<byte[]>().Any(b => b.Length >= 8 && Trusted(AdConvert.SidToString(b)));
            }

            var search = new DirectorySearch
            {
                BaseDn = _session.Info.DefaultNamingContext,
                Filter = LdapFilter.And(LdapFilter.Equal(Attr.ObjectCategory, ObjectClasses.Group), LdapFilter.InChain(Attr.Member, userDn)),
                Attributes = [Attr.ObjectSid],
            };
            await foreach (var group in _session.SearchAsync(search, _cancellationToken).ConfigureAwait(false))
            {
                if (Trusted(group.ObjectSid))
                {
                    return true;
                }
            }

            return false;
        }

        private async Task<DirectoryObject?> FirstAsync(DirectorySearch search)
        {
            await foreach (var entry in _session.SearchAsync(search, _cancellationToken).ConfigureAwait(false))
            {
                return entry;
            }

            return null;
        }

        // ------------------------------------------------------------------ managers and groups (2.5 steps 3 and 4)

        private async Task ResolveReferencesAsync()
        {
            var rows = _rows.Where(r => r.Fixed is null).ToList();
            await ResolveManagersAsync(rows).ConfigureAwait(false);
            await ResolveGroupsAsync(rows).ConfigureAwait(false);
        }

        private string ManagerAttribute => _kind is ObjectKind.Group or ObjectKind.Computer ? Attr.ManagedBy : Attr.Manager;

        private string ManagerKinds => _kind is ObjectKind.Group or ObjectKind.Computer
            ? LdapFilter.Or(ObjectKind.User.ToLdapFilter(), ObjectKind.Contact.ToLdapFilter(), ObjectKind.Group.ToLdapFilter())
            : LdapFilter.Or(ObjectKind.User.ToLdapFilter(), ObjectKind.Contact.ToLdapFilter());

        private async Task ResolveManagersAsync(List<RowState> rows)
        {
            var texts = rows.Select(r => r.ManagerText).OfType<string>().Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (texts.Count == 0)
            {
                return;
            }

            var index = _lookups.Index("manager|" + ManagerAttribute);
            var unknown = texts.Where(t => !index.IsKnown(t)).ToList();
            foreach (var dn in unknown.Where(IsDn))
            {
                var entry = await _lookups.ReadOnceAsync("managerdn\u0001" + Key(dn),
                    () => _session.GetAsync(dn, [Attr.ObjectClass, Attr.SamAccountType, Attr.Name], _cancellationToken)).ConfigureAwait(false);
                var found = entry is not null && IsManagerKind(entry) ? new[] { (dn, new LookupOwner(entry.DistinguishedName, entry.Dn.Name, false, false)) } : [];
                index.Record([dn], found);
            }

            var names = unknown.Where(t => !IsDn(t)).ToList();
            var batches = names.Where(t => t.Contains('@', StringComparison.Ordinal)).Chunk(ManagerBatchSize).Select(c => (Addresses: true, Values: c))
                .Concat(names.Where(t => !t.Contains('@', StringComparison.Ordinal)).Chunk(ManagerBatchSize).Select(c => (Addresses: false, Values: c)))
                .ToList();
            await Parallel.ForEachAsync(
                batches,
                new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = _cancellationToken },
                async (batch, _) => await LookupManagersAsync(index, batch.Addresses, batch.Values).ConfigureAwait(false)).ConfigureAwait(false);

            foreach (var state in rows.Where(r => r.ManagerText is { Length: > 0 }))
            {
                var text = state.ManagerText!;
                var owners = index.Owners(text);
                switch (owners.Count)
                {
                    case 1:
                        state.ManagerDn = owners[0].DistinguishedName;
                        break;
                    case 0 when IsInBatch(state, text):
                        state.Add(Warning(BulkCreateIssueCodes.ManagerInBatch, "The manager is created in the same batch.", ManagerAttribute));
                        break;
                    case 0:
                        state.Add(Warning(BulkCreateIssueCodes.ManagerNotFound, "No account matches the manager.", ManagerAttribute));
                        break;
                    default:
                        state.ManagerCandidates.AddRange(owners.Select(o => o.DistinguishedName));
                        state.Add(Warning(BulkCreateIssueCodes.ManagerAmbiguous, "Several accounts match the manager.", ManagerAttribute, Text(owners.Count)));
                        break;
                }
            }
        }

        private async Task LookupManagersAsync(LookupIndex index, bool addresses, string[] values)
        {
            var clauses = addresses
                ? values.SelectMany(v => new[] { LdapFilter.Equal(Attr.UserPrincipalName, v), LdapFilter.Equal(Attr.Mail, v) })
                : values.SelectMany(v => new[] { LdapFilter.Equal(Attr.SamAccountName, v), LdapFilter.Equal(Attr.DisplayName, v), LdapFilter.Equal(Attr.Cn, v) });
            var search = new DirectorySearch
            {
                BaseDn = _session.Info.DefaultNamingContext,
                Filter = LdapFilter.And(ManagerKinds, LdapFilter.Or(clauses)),
                Attributes = [Attr.SamAccountName, Attr.UserPrincipalName, Attr.Mail, Attr.DisplayName, Attr.Cn],
                PageSize = 1000,
            };
            var found = new List<(string Value, LookupOwner Owner)>();
            _lookups.CountQuery();
            await foreach (var entry in _session.SearchAsync(search, _cancellationToken).ConfigureAwait(false))
            {
                var owner = new LookupOwner(entry.DistinguishedName, entry.Dn.Name, false, false);
                string[] attributes = addresses ? [Attr.UserPrincipalName, Attr.Mail] : [Attr.SamAccountName, Attr.DisplayName, Attr.Cn];
                foreach (var value in values.Where(v => attributes.Any(a => entry.GetStrings(a).Contains(v, StringComparer.OrdinalIgnoreCase))))
                {
                    found.Add((value, owner));
                }
            }

            index.Record(values, found);
        }

        private bool IsManagerKind(DirectoryObject entry) => entry.Kind switch
        {
            ObjectKind.User or ObjectKind.InetOrgPerson or ObjectKind.Contact => true,
            ObjectKind.Group => _kind is ObjectKind.Group or ObjectKind.Computer,
            _ => false,
        };

        /// <summary>The value names another row of the batch: its sAMAccountName, UPN, mail or displayName (2.5).</summary>
        private bool IsInBatch(RowState self, string text)
        {
            if (_batchIdentifiers is null)
            {
                _batchIdentifiers = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in _rows.Where(r => r.Fixed != BulkRowStatus.Excluded))
                {
                    foreach (var attribute in new[] { Attr.SamAccountName, Attr.UserPrincipalName, Attr.Mail, Attr.DisplayName })
                    {
                        if (row.Values.TryGetValue(attribute, out var draft) && draft.HasValue)
                        {
                            if (!_batchIdentifiers.TryGetValue(draft.First, out var numbers))
                            {
                                _batchIdentifiers[draft.First] = numbers = [];
                            }

                            numbers.Add(row.Number);
                        }
                    }
                }
            }

            return _batchIdentifiers.TryGetValue(text, out var rows) && rows.Any(n => n != self.Number);
        }

        private Dictionary<string, List<int>>? _batchIdentifiers;

        private static bool IsDn(string text) => text.Contains('=', StringComparison.Ordinal) && Ldap.DistinguishedName.TryParse(text, out var dn) && !dn.IsRoot;

        private async Task ResolveGroupsAsync(List<RowState> rows)
        {
            var parts = rows.Where(r => r.GroupsText is { Length: > 0 })
                .ToDictionary(r => r, r => CellText.SplitList(r.GroupsText!).Where(p => p.Length > 0).ToList());
            var index = _lookups.Index("group|names");
            await LookupGroupsAsync(index, parts.Values.SelectMany(p => p)).ConfigureAwait(false);
            var fallback = parts.Values.SelectMany(p => p)
                .Where(p => !IsDn(p) && index.Owners(p).Count == 0 && p.Contains(',', StringComparison.Ordinal))
                .SelectMany(CellText.SplitCommas)
                .ToList();
            await LookupGroupsAsync(index, fallback).ConfigureAwait(false);

            foreach (var state in rows)
            {
                var groups = new List<GroupInfo>(_optionGroups);
                if (parts.TryGetValue(state, out var list))
                {
                    foreach (var part in list)
                    {
                        groups.AddRange(await ResolveGroupPartAsync(state, index, part).ConfigureAwait(false));
                    }
                }

                AddGroups(state, groups);
            }
        }

        private async Task LookupGroupsAsync(LookupIndex index, IEnumerable<string> parts)
        {
            var unknown = parts.Where(p => p.Length > 0 && !index.IsKnown(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var dn in unknown.Where(IsDn))
            {
                var group = await ReadGroupAsync(dn).ConfigureAwait(false);
                index.Record([dn], group is null ? [] : [(dn, group.ToOwner())]);
                if (group is not null)
                {
                    _groups[Key(group.Dn)] = group;
                }
            }

            await Parallel.ForEachAsync(
                unknown.Where(p => !IsDn(p)).Chunk(LookupBatchSize / 2),
                new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = _cancellationToken },
                async (names, _) =>
                {
                    var search = new DirectorySearch
                    {
                        BaseDn = _session.Info.DefaultNamingContext,
                        Filter = LdapFilter.And(ObjectKind.Group.ToLdapFilter(),
                            LdapFilter.Or(names.SelectMany(n => new[] { LdapFilter.Equal(Attr.SamAccountName, n), LdapFilter.Equal(Attr.Cn, n) }))),
                        Attributes = [Attr.ObjectClass, Attr.GroupType, Attr.ObjectSid, Attr.Name, Attr.SamAccountName, Attr.Cn],
                        PageSize = 1000,
                    };
                    var found = new List<(string Value, LookupOwner Owner)>();
                    _lookups.CountQuery();
                    await foreach (var entry in _session.SearchAsync(search, _cancellationToken).ConfigureAwait(false))
                    {
                        var group = GroupInfo.From(entry, writable: true);
                        lock (_groups)
                        {
                            _groups.TryAdd(Key(group.Dn), group);
                        }

                        foreach (var name in names.Where(n => entry.GetStrings(Attr.SamAccountName).Concat(entry.GetStrings(Attr.Cn)).Contains(n, StringComparer.OrdinalIgnoreCase)))
                        {
                            found.Add((name, group.ToOwner()));
                        }
                    }

                    index.Record(names, found);
                }).ConfigureAwait(false);
        }

        private readonly Dictionary<string, GroupInfo> _groups = new(StringComparer.OrdinalIgnoreCase);

        private async Task<IReadOnlyList<GroupInfo>> ResolveGroupPartAsync(RowState state, LookupIndex index, string part)
        {
            var owners = index.Owners(part);
            if (owners.Count == 1)
            {
                return [await GroupWithPermissionsAsync(owners[0].DistinguishedName).ConfigureAwait(false)];
            }

            if (owners.Count > 1)
            {
                state.Add(Warning(BulkCreateIssueCodes.GroupAmbiguous, "Several groups match the name.", null, part));
                return [];
            }

            if (!IsDn(part) && part.Contains(',', StringComparison.Ordinal))
            {
                var pieces = CellText.SplitCommas(part).Where(p => p.Length > 0).ToList();
                if (pieces.Count > 1 && pieces.All(p => index.Owners(p).Count == 1))
                {
                    state.Add(Warning(BulkCreateIssueCodes.GroupsSplitOnCommas, "The groups were split on commas."));
                    var result = new List<GroupInfo>();
                    foreach (var piece in pieces)
                    {
                        result.Add(await GroupWithPermissionsAsync(index.Owners(piece)[0].DistinguishedName).ConfigureAwait(false));
                    }

                    return result;
                }
            }

            state.Add(Warning(BulkCreateIssueCodes.GroupNotFound, "The group does not exist.", null, part));
            return [];
        }

        private async Task<GroupInfo> GroupWithPermissionsAsync(string dn)
        {
            GroupInfo? group;
            lock (_groups)
            {
                _groups.TryGetValue(Key(dn), out group);
            }

            group ??= await ReadGroupAsync(dn).ConfigureAwait(false) ?? new GroupInfo(dn, RdnValue(dn), null, null, true, []);
            var permissions = await ReadPermissionsAsync(group.Dn).ConfigureAwait(false);
            return group.Writable == permissions.CanWrite(Attr.Member) ? group : group.WithWritable(permissions.CanWrite(Attr.Member));
        }

        /// <summary>The groups of a row: distinct, the primary group left out, the nesting rules of created groups applied (2.5 step 4).</summary>
        private void AddGroups(RowState state, IEnumerable<GroupInfo> groups)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
            {
                if (!seen.Add(Key(group.Dn)))
                {
                    continue;
                }

                var primaryRid = _kind switch
                {
                    ObjectKind.User or ObjectKind.InetOrgPerson => WellKnownRids.DomainUsers,
                    ObjectKind.Computer => WellKnownRids.DomainComputers,
                    _ => (uint?)null,
                };
                if (primaryRid is { } rid && AdConvert.GetRid(group.Sid) == rid)
                {
                    state.Add(Info(BulkCreateIssueCodes.PrimaryGroupSkipped, "The group is the account's primary group.", null, group.Name));
                    continue;
                }

                if (_kind == ObjectKind.Group && group.GroupType is { } groupType && !CanNest(_input.Options.GroupScope, GroupTypes.GetScope(groupType)))
                {
                    state.Add(Warning(BulkCreateIssueCodes.GroupScopeNotAllowed, "The created group cannot be a member of this group.", null, group.Name));
                    continue;
                }

                if (!group.Writable && _optionGroups.All(o => !Is(Key(o.Dn), Key(group.Dn))))
                {
                    state.Add(Warning(IssueCodes.NotWritable, "The window's account cannot add members to the group.", Attr.Member));
                }

                state.GroupDns.Add(group.Dn);
            }
        }

        /// <summary>Global → any scope; Universal → Universal or Domain Local; Domain Local → Domain Local (all groups are of the window's domain).</summary>
        private static bool CanNest(GroupScope created, GroupScope target) => created switch
        {
            GroupScope.Global => true,
            GroupScope.Universal => target is GroupScope.Universal or GroupScope.DomainLocal,
            GroupScope.DomainLocal => target == GroupScope.DomainLocal,
            _ => false,
        };

        private static string RdnValue(string dn) => Ldap.DistinguishedName.TryParse(dn, out var parsed) && !parsed.IsRoot ? parsed.Name : dn;

        // ------------------------------------------------------------------ possible existing accounts (2.4)

        private static readonly string[] DefaultMatchAttributes = [Attr.EmployeeId, Attr.EmployeeNumber, Attr.Mail, Attr.Mobile, Attr.GivenName];

        /// <summary>
        /// Users only: the first attribute of <see cref="BulkAccountOptions.MatchAttributes"/> (null = employeeID,
        /// employeeNumber, mail, mobile, then given name + surname) that the row has from a column or an edit is looked up among
        /// the domain's users and contacts – a match is <see cref="BulkCreateIssueCodes.PossibleExistingAccount"/>, two rows with
        /// the same value are <see cref="BulkCreateIssueCodes.DuplicateSourceRow"/> (warnings, never errors).
        /// </summary>
        private async Task FindExistingAccountsAsync()
        {
            var order = _input.Options.MatchAttributes ?? DefaultMatchAttributes;
            if (!IsUserKind(_kind) || order.Count == 0)
            {
                return;
            }

            var matches = new List<(RowState State, string Attribute, string Value)>();
            foreach (var state in _rows.Where(r => r.Fixed is null))
            {
                foreach (var attribute in order)
                {
                    if (Is(attribute, Attr.GivenName) || Is(attribute, Attr.Sn))
                    {
                        if (OperatorValue(state, Attr.GivenName) is { } given && OperatorValue(state, Attr.Sn) is { } surname)
                        {
                            matches.Add((state, Attr.Cn, given + "\u0001" + surname));
                            break;
                        }

                        continue;
                    }

                    if (OperatorValue(state, attribute) is { } value)
                    {
                        matches.Add((state, Canonical(attribute), value));
                        break;
                    }
                }
            }

            foreach (var group in matches.GroupBy(m => (m.Attribute.ToUpperInvariant(), m.Value.ToUpperInvariant())).Where(g => g.Count() > 1))
            {
                var list = group.ToList();
                foreach (var (state, attribute, _) in list)
                {
                    var other = list.First(m => !ReferenceEquals(m.State, state)).State.Number;
                    state.Add(Warning(BulkCreateIssueCodes.DuplicateSourceRow, "Another row describes the same person.", attribute, Text(other)));
                }
            }

            foreach (var byAttribute in matches.GroupBy(m => m.Attribute, StringComparer.OrdinalIgnoreCase))
            {
                var index = _lookups.Index("person|" + byAttribute.Key);
                var unknown = byAttribute.Select(m => m.Value).Where(v => !index.IsKnown(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (unknown.Count > FullListingThreshold)
                {
                    await LookupPersonsAsync(index, byAttribute.Key, null).ConfigureAwait(false);
                }
                else
                {
                    await Parallel.ForEachAsync(
                        unknown.Chunk(LookupBatchSize),
                        new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = _cancellationToken },
                        async (values, _) => await LookupPersonsAsync(index, byAttribute.Key, values).ConfigureAwait(false)).ConfigureAwait(false);
                }

                foreach (var (state, attribute, value) in byAttribute)
                {
                    var owners = index.Owners(value);
                    if (owners.Count > 0)
                    {
                        state.MatchDns.AddRange(owners.Select(o => o.DistinguishedName));
                        state.Add(Warning(BulkCreateIssueCodes.PossibleExistingAccount, "An existing account looks like the same person.", attribute, owners[0].Name));
                    }
                }
            }
        }

        private static string? OperatorValue(RowState state, string attribute) =>
            state.Values.TryGetValue(attribute, out var draft) && draft.HasValue && draft.IsOperatorValue ? draft.First : null;

        /// <summary>
        /// The users and contacts holding <paramref name="values"/> of <paramref name="attribute"/> (<c>cn</c>: given name and
        /// surname together); null <paramref name="values"/> reads every holder of the attribute in one search.
        /// </summary>
        private async Task LookupPersonsAsync(LookupIndex index, string attribute, string[]? values)
        {
            var byName = Is(attribute, Attr.Cn);
            string match;
            if (values is null)
            {
                match = byName ? LdapFilter.And(LdapFilter.Present(Attr.GivenName), LdapFilter.Present(Attr.Sn)) : LdapFilter.Present(attribute);
            }
            else
            {
                match = LdapFilter.Or(values.Select(v =>
                {
                    if (!byName)
                    {
                        return LdapFilter.Equal(attribute, v);
                    }

                    var at = v.IndexOf('\u0001', StringComparison.Ordinal);
                    return LdapFilter.And(LdapFilter.Equal(Attr.GivenName, v[..at]), LdapFilter.Equal(Attr.Sn, v[(at + 1)..]));
                }));
            }

            var search = new DirectorySearch
            {
                BaseDn = _session.Info.DefaultNamingContext,
                Filter = LdapFilter.And(LdapFilter.Or(ObjectKind.User.ToLdapFilter(), ObjectKind.Contact.ToLdapFilter()), match),
                Attributes = byName ? [Attr.GivenName, Attr.Sn, Attr.DisplayName] : [attribute, Attr.DisplayName],
                PageSize = 1000,
            };
            var wanted = values is null ? null : new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
            var found = new List<(string Value, LookupOwner Owner)>();
            _lookups.CountQuery();
            await foreach (var entry in _session.SearchAsync(search, _cancellationToken).ConfigureAwait(false))
            {
                var owner = new LookupOwner(entry.DistinguishedName, entry.GetString(Attr.DisplayName) ?? entry.Dn.Name, false, false);
                var held = byName
                    ? entry.GetStrings(Attr.GivenName).SelectMany(g => entry.GetStrings(Attr.Sn).Select(s => g + "\u0001" + s))
                    : entry.GetStrings(attribute);
                found.AddRange(held.Where(v => wanted is null || wanted.Contains(v)).Select(v => (v, owner)));
            }

            if (values is null)
            {
                index.Complete(found);
            }
            else
            {
                index.Record(values, found);
            }
        }
    }

    /// <summary>A group read from the directory (a class: it holds directory values in memory only and is never printed with them).</summary>
    private sealed class GroupInfo(string dn, string name, int? groupType, string? sid, bool writable, IReadOnlyList<string> passwordSettings)
    {
        public string Dn { get; } = dn;

        public string Name { get; } = name;

        public int? GroupType { get; } = groupType;

        public string? Sid { get; } = sid;

        public bool Writable { get; } = writable;

        /// <summary>The PSOs applied to the group (<c>msDS-PSOApplied</c>).</summary>
        public IReadOnlyList<string> PasswordSettings { get; } = passwordSettings;

        public static GroupInfo From(DirectoryObject entry, bool writable) =>
            new(entry.DistinguishedName, entry.GetString(Attr.SamAccountName) ?? entry.Dn.Name, entry.GetInt32(Attr.GroupType), entry.ObjectSid, writable,
                entry.GetStrings(PsoApplied));

        public GroupInfo WithWritable(bool writable) => new(Dn, Name, GroupType, Sid, writable, PasswordSettings);

        public LookupOwner ToOwner() => new(Dn, Name, false, false);

        public override string ToString() => $"Group (writable={Writable})";
    }
}
