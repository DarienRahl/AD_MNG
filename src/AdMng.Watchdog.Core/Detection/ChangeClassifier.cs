using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// <see cref="RawObjectDiff"/> → event drafts (section 10.6 table). Each attribute is consumed by the first matching
/// line; the leftovers go into one AttributesChanged event. Computer noise is dropped unless enabled (9.3), but
/// ComputerNameSpoofing, foreign-host SPNs and every Tier-0 attribute are always evaluated.
/// </summary>
internal sealed class ChangeClassifier
{
    private static readonly string[] AccountNameAttributes = [Attr.SamAccountName, Attr.UserPrincipalName];

    private static readonly string[] DelegationAttributes = [Attr.AllowedToDelegateTo, WatchdogAttributes.AllowedToActOnBehalfOfOtherIdentity];

    private static readonly string[] ManagedAccountLinkAttributes =
    [
        WatchdogAttributes.ManagedAccountPrecededByLink, WatchdogAttributes.DelegatedMsaState,
        WatchdogAttributes.SupersededManagedAccountLink, WatchdogAttributes.SupersededServiceAccountState,
    ];

    private static readonly string[] GroupPolicyLinkAttributes = [Attr.GpLink, WatchdogAttributes.GpOptions];

    private readonly AttributeChangeBuilder _changes;
    private readonly UacFlagClassifier _uac;

    public ChangeClassifier(AttributeChangeBuilder changes)
    {
        _changes = changes;
        _uac = new UacFlagClassifier(changes);
    }

    /// <summary>Classifies one object diff.</summary>
    public IReadOnlyList<EventDraft> Classify(ClassificationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var diff = input.Diff;
        var kind = diff.New.Kind;
        var drafts = new List<EventDraft>();
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Attr.Cn, Attr.Ou, Attr.Name, WatchSignature.Key };

        if (diff.Moved)
        {
            drafts.Add(new EventDraft { Kind = WatchEventKind.ObjectMoved, PreviousDn = diff.Old.Dn, MetadataAttributes = [Attr.Name] });
        }

        if (diff.Renamed)
        {
            drafts.Add(new EventDraft { Kind = WatchEventKind.ObjectRenamed, PreviousDn = diff.Old.Dn, MetadataAttributes = [Attr.Name] });
        }

        if (Take(diff, consumed, Attr.UserAccountControl) is { } uac)
        {
            drafts.AddRange(_uac.Classify(uac.Old, uac.New));
        }

        ClassifyLockout(input, consumed, drafts);
        ClassifyPassword(input, consumed, drafts);

        if (Take(diff, consumed, Attr.AccountExpires) is { } expires)
        {
            drafts.Add(Single(WatchEventKind.AccountExpirationChanged, expires));
        }

        ClassifyNames(input, consumed, drafts);
        ClassifyPrimaryGroup(input, consumed, drafts);

        if (Take(diff, consumed, Attr.SidHistory) is { } sidHistory)
        {
            drafts.Add(Single(WatchEventKind.SidHistoryChanged, sidHistory));
        }

        if (diff.Get(Attr.AdminCount) is { } adminCount && Number(adminCount.OldValue) != 1 && Number(adminCount.NewValue) == 1)
        {
            consumed.Add(Attr.AdminCount);
            drafts.Add(Single(WatchEventKind.AdminCountSet, adminCount));
        }

        ClassifyServicePrincipalNames(input, consumed, drafts);

        if (Take(diff, consumed, WatchdogAttributes.KeyCredentialLink) is { } keys)
        {
            drafts.Add(Single(WatchEventKind.KeyCredentialChanged, keys));
        }

        var delegation = TakeAll(diff, consumed, DelegationAttributes);
        if (delegation.Count > 0)
        {
            MergeDelegation(drafts, delegation);
        }

        ClassifyManagedAccountLinks(input, consumed, drafts);

        if (Take(diff, consumed, WatchdogAttributes.AltSecurityIdentities) is { } mapping)
        {
            drafts.Add(Single(WatchEventKind.CertificateMappingChanged, mapping));
        }

        if (Take(diff, consumed, WatchdogAttributes.GroupMsaMembership) is { } readers)
        {
            drafts.Add(Single(WatchEventKind.ManagedPasswordReadersChanged, readers));
        }

        if (Take(diff, consumed, Attr.NtSecurityDescriptor) is { } descriptor)
        {
            drafts.Add(Single(WatchEventKind.SecurityDescriptorChanged, descriptor));
        }

        if (Take(diff, consumed, Attr.GroupType) is { } groupType)
        {
            drafts.Add(Single(WatchEventKind.GroupTypeChanged, groupType));
        }

        ClassifyGroupPolicyLinks(input, consumed, drafts);
        ClassifyFsmo(input, consumed, drafts);
        ClassifyByKind(input, consumed, drafts);
        consumed.Add(WatchdogAttributes.CreatorSid);
        ClassifyLeftovers(input, consumed, drafts);
        return drafts;
    }

    /// <summary>
    /// Drafts of a settled new object (10.6.3): the Created event with the final watched values, the risky bits of the
    /// final userAccountControl against an empty baseline. Member events are added by the caller.
    /// </summary>
    public IReadOnlyList<EventDraft> ClassifyCreated(SnapshotObject created)
    {
        ArgumentNullException.ThrowIfNull(created);
        var changes = created.Attributes
            .Where(a => a.Key != WatchSignature.Key && a.Value.Values.Count > 0)
            .OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase)
            .Select(a => _changes.Build(a.Key, null, a.Value))
            .ToList();
        var drafts = new List<EventDraft>();
        WatchEventKind? kind = created.Kind switch
        {
            WatchObjectKind.User => WatchEventKind.UserCreated,
            WatchObjectKind.Computer => WatchEventKind.ComputerCreated,
            WatchObjectKind.Group => WatchEventKind.GroupCreated,
            WatchObjectKind.OrganizationalUnit => WatchEventKind.OrganizationalUnitCreated,
            WatchObjectKind.Contact => WatchEventKind.ContactCreated,
            WatchObjectKind.ServiceAccount => WatchEventKind.ServiceAccountCreated,
            WatchObjectKind.GroupPolicy => WatchEventKind.GroupPolicyChanged,
            WatchObjectKind.Trust => WatchEventKind.TrustChanged,
            WatchObjectKind.PasswordSettings => WatchEventKind.PasswordSettingsChanged,
            _ => null,
        };
        if (kind is not { } createdKind)
        {
            return drafts;
        }

        if (created.Kind == WatchObjectKind.ServiceAccount)
        {
            changes.Insert(0, _changes.Text(Attr.ObjectClass, [created.ObjectClasses.LastOrDefault() ?? string.Empty]));
        }

        if (createdKind is WatchEventKind.GroupPolicyChanged or WatchEventKind.TrustChanged or WatchEventKind.PasswordSettingsChanged)
        {
            changes.Insert(0, _changes.Text("operation", ["created"]));
        }

        drafts.Add(new EventDraft { Kind = createdKind, Changes = changes, MetadataAttributes = [Attr.ObjectClass] });
        if (WatchObjectKinds.IsAccount(created.Kind) && created.Attributes.TryGetValue(Attr.UserAccountControl, out var uac))
        {
            drafts.AddRange(_uac.ClassifyCreated(uac));
        }

        return drafts;
    }

    private void ClassifyLockout(ClassificationInput input, HashSet<string> consumed, List<EventDraft> drafts)
    {
        if (Take(input.Diff, consumed, Attr.LockoutTime) is not { } lockout)
        {
            return;
        }

        var before = Number(lockout.OldValue) ?? 0;
        var after = Number(lockout.NewValue) ?? 0;
        var change = _changes.FromDelta(lockout);
        if (after > 0 && (before <= 0 || after > before))
        {
            var lockedAt = AdConvert.FromFileTime(after) ?? input.Now;
            drafts.Add(new EventDraft
            {
                Kind = WatchEventKind.AccountLockedOut,
                Changes = [change],
                MetadataAttributes = [Attr.LockoutTime],
                Lockout = new LockoutInfo
                {
                    LockedAt = lockedAt,
                    LockedUntil = input.Lockout.Duration is { } duration ? lockedAt + duration : null,
                    LockoutsToday = input.LockoutsToday + 1,
                },
            });
            return;
        }

        if (before > 0 && after <= 0)
        {
            // An unlock only when the write precedes the end of the lockout; a reset at the first logon after the
            // lockout expired is the DC's bookkeeping, not an unlock (10.6, review A2).
            var lockedAt = AdConvert.FromFileTime(before) ?? input.Now;
            var resetAt = input.Metadata.Get(Attr.LockoutTime)?.OriginatingTime ?? input.Now;
            if (input.Lockout.Duration is not { } duration || resetAt < lockedAt + duration)
            {
                drafts.Add(new EventDraft { Kind = WatchEventKind.AccountUnlocked, Changes = [change], MetadataAttributes = [Attr.LockoutTime] });
            }
        }
    }

    private void ClassifyPassword(ClassificationInput input, HashSet<string> consumed, List<EventDraft> drafts)
    {
        var diff = input.Diff;
        if (diff.Get(Attr.PwdLastSet) is not { } pwd)
        {
            return;
        }

        var before = Number(pwd.OldValue) ?? 0;
        var after = Number(pwd.NewValue) ?? 0;
        if (after == 0 && before != 0)
        {
            consumed.Add(Attr.PwdLastSet);
            drafts.Add(Single(WatchEventKind.PasswordMustChangeSet, pwd));
            return;
        }

        if (after <= before)
        {
            return;
        }

        consumed.Add(Attr.PwdLastSet);
        var kind = diff.New.Kind;
        if ((kind == WatchObjectKind.Computer && !input.Directory.TrackComputerPasswordChanges)
            || (kind == WatchObjectKind.ServiceAccount && !input.Directory.TrackManagedPasswordRollover))
        {
            return;
        }

        var written = !input.Metadata.IsEmpty && input.Watermark is { } watermark
            ? input.Metadata.Get(Attr.UnicodePwd) is { } unicodePwd && unicodePwd.UsnLocal > watermark
            : true;
        drafts.Add(new EventDraft
        {
            Kind = written ? WatchEventKind.PasswordChanged : WatchEventKind.PasswordAgeReset,
            Changes = [_changes.FromDelta(pwd)],
            MetadataAttributes = written ? [Attr.UnicodePwd, Attr.PwdLastSet] : [Attr.PwdLastSet],
        });
    }

    private void ClassifyNames(ClassificationInput input, HashSet<string> consumed, List<EventDraft> drafts)
    {
        var deltas = TakeAll(input.Diff, consumed, AccountNameAttributes);
        if (deltas.Count == 0)
        {
            return;
        }

        var spoofed = new List<AttributeDelta>();
        if (input.Diff.New.Kind == WatchObjectKind.Computer
            && deltas.FirstOrDefault(d => d.Attribute.Equals(Attr.SamAccountName, StringComparison.OrdinalIgnoreCase)) is { NewValue: { } sam } samDelta
            && (!sam.EndsWith('$') || input.DomainControllerNames.Contains(sam.TrimEnd('$')) && !IsOwnDcName(input, sam)))
        {
            spoofed.Add(samDelta);
        }

        if (spoofed.Count > 0)
        {
            drafts.Add(new EventDraft
            {
                Kind = WatchEventKind.ComputerNameSpoofing,
                Changes = spoofed.Select(_changes.FromDelta).ToList(),
                MetadataAttributes = [Attr.SamAccountName],
            });
        }

        var rest = deltas.Except(spoofed).ToList();
        if (rest.Count > 0)
        {
            drafts.Add(new EventDraft
            {
                Kind = WatchEventKind.AccountNameChanged,
                Changes = rest.Select(_changes.FromDelta).ToList(),
                MetadataAttributes = AccountNameAttributes,
            });
        }
    }

    private void ClassifyPrimaryGroup(ClassificationInput input, HashSet<string> consumed, List<EventDraft> drafts)
    {
        if (input.Diff.Get(Attr.PrimaryGroupId) is not { } primary || Number(primary.NewValue) is not { } rid)
        {
            return;
        }

        var kind = input.Diff.New.Kind;
        var uac = Number(input.Diff.New.Attributes.TryGetValue(Attr.UserAccountControl, out var u) && u.Values.Count > 0 ? u.Values[0] : null) ?? 0;
        var isDc = (uac & (long)UserAccountControl.ServerTrustAccount) != 0 || (uac & (long)UserAccountControl.PartialSecretsAccount) != 0;
        var defaults = kind switch
        {
            WatchObjectKind.User => [WellKnownRids.DomainUsers, WellKnownRids.DomainGuests],
            WatchObjectKind.Computer or WatchObjectKind.ServiceAccount when isDc => [WellKnownRids.DomainControllers, WellKnownRids.ReadOnlyDomainControllers],
            WatchObjectKind.Computer or WatchObjectKind.ServiceAccount => new[] { WellKnownRids.DomainComputers },
            _ => [],
        };
        if (!defaults.Contains((uint)rid))
        {
            consumed.Add(Attr.PrimaryGroupId);
            drafts.Add(Single(WatchEventKind.PrimaryGroupChanged, primary));
        }
    }

    private void ClassifyServicePrincipalNames(ClassificationInput input, HashSet<string> consumed, List<EventDraft> drafts)
    {
        if (input.Diff.Get(Attr.ServicePrincipalName) is not { } spn)
        {
            return;
        }

        var kind = input.Diff.New.Kind;
        if (kind is WatchObjectKind.User or WatchObjectKind.ServiceAccount && spn.AddedValues.Count > 0)
        {
            consumed.Add(Attr.ServicePrincipalName);
            drafts.Add(Single(WatchEventKind.ServicePrincipalNameAdded, spn));
            return;
        }

        if (kind == WatchObjectKind.Computer && !input.Directory.TrackComputerOsChanges)
        {
            var dns = First(input.Diff.New, Attr.DnsHostName) ?? First(input.Diff.Old, Attr.DnsHostName);
            var sam = input.Diff.New.SamAccountName ?? input.Diff.Old.SamAccountName;
            var foreign = spn.AddedValues.Concat(spn.RemovedValues).Any(v => !NoiseAttributes.IsOwnHostSpn(v, dns, sam));
            if (!foreign)
            {
                consumed.Add(Attr.ServicePrincipalName);
            }
        }
    }

    private void ClassifyManagedAccountLinks(ClassificationInput input, HashSet<string> consumed, List<EventDraft> drafts)
    {
        var deltas = TakeAll(input.Diff, consumed, ManagedAccountLinkAttributes);
        if (deltas.Count == 0)
        {
            return;
        }

        var linkedPrivileged = deltas
            .SelectMany(d => d.AddedValues.Concat(d.RemovedValues).Concat(d.New?.Values ?? []))
            .Select(ValueCanonicalizer.GuidOf)
            .Any(g => g is { } guid && input.IsPrivileged(guid.ToString("N")));
        drafts.Add(new EventDraft
        {
            Kind = WatchEventKind.ManagedAccountLinkChanged,
            Changes = deltas.Select(_changes.FromDelta).ToList(),
            BaseSeverity = linkedPrivileged || input.IsPrivileged(input.Diff.New.ObjectGuid.ToString("N")) ? Severity.Critical : Severity.High,
            MetadataAttributes = ManagedAccountLinkAttributes,
        });
    }

    private void ClassifyGroupPolicyLinks(ClassificationInput input, HashSet<string> consumed, List<EventDraft> drafts)
    {
        if (input.Diff.New.Kind is not (WatchObjectKind.OrganizationalUnit or WatchObjectKind.Domain))
        {
            return;
        }

        var deltas = TakeAll(input.Diff, consumed, GroupPolicyLinkAttributes);
        if (deltas.Count == 0)
        {
            return;
        }

        var changes = new List<AttributeChange>();
        foreach (var delta in deltas)
        {
            if (!delta.Attribute.Equals(Attr.GpLink, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(_changes.FromDelta(delta));
                continue;
            }

            var before = GpLinks.Parse(delta.OldValue).Select(l => Describe(l, input)).ToList();
            var after = GpLinks.Parse(delta.NewValue).Select(l => Describe(l, input)).ToList();
            changes.Add(_changes.Text(Attr.GpLink, after, before) with
            {
                Added = after.Except(before, StringComparer.Ordinal).ToList(),
                Removed = before.Except(after, StringComparer.Ordinal).ToList(),
            });
        }

        drafts.Add(new EventDraft { Kind = WatchEventKind.GroupPolicyLinkChanged, Changes = changes, MetadataAttributes = GroupPolicyLinkAttributes });
    }

    private void ClassifyFsmo(ClassificationInput input, HashSet<string> consumed, List<EventDraft> drafts)
    {
        if (input.Diff.New.Kind is not (WatchObjectKind.Domain or WatchObjectKind.Configuration)
            || Take(input.Diff, consumed, WatchdogAttributes.FsmoRoleOwner) is not { } owner)
        {
            return;
        }

        drafts.Add(FsmoDraft(owner, input.Diff.New, input.DsaHostName, _changes));
    }

    /// <summary>FsmoRoleChanged with the role name and the old → new DC host (NTDS Settings DN when unknown).</summary>
    public static EventDraft FsmoDraft(AttributeDelta owner, SnapshotObject holder, Func<string, string?> dsaHostName, AttributeChangeBuilder changes)
    {
        var role = FsmoRole(holder);
        var before = AttributeChangeBuilder.Display(owner.Old).Select(d => dsaHostName(d) ?? d).ToList();
        var after = AttributeChangeBuilder.Display(owner.New).Select(d => dsaHostName(d) ?? d).ToList();
        return new EventDraft
        {
            Kind = WatchEventKind.FsmoRoleChanged,
            Changes = [changes.Text("role", [role]), changes.Text(WatchdogAttributes.FsmoRoleOwner, after, before) with { ValueKind = AttributeValueKind.Dn }],
            MetadataAttributes = [WatchdogAttributes.FsmoRoleOwner],
        };
    }

    private static string FsmoRole(SnapshotObject holder)
    {
        var classes = new HashSet<string>(holder.ObjectClasses, StringComparer.OrdinalIgnoreCase);
        return holder.Kind == WatchObjectKind.Domain ? "PDC"
            : classes.Contains(WatchdogObjectClasses.RidManager) ? "RID"
            : classes.Contains(ObjectClasses.InfrastructureUpdate) ? "Infrastructure"
            : classes.Contains("crossRefContainer") ? "DomainNaming"
            : classes.Contains("dMD") ? "Schema"
            : "FSMO";
    }

    private void ClassifyByKind(ClassificationInput input, HashSet<string> consumed, List<EventDraft> drafts)
    {
        var diff = input.Diff;
        var (kind, attributes) = diff.New.Kind switch
        {
            WatchObjectKind.Domain => (WatchEventKind.DomainPolicyChanged, (IReadOnlyList<string>)WatchedAttributes.DomainPolicy),
            WatchObjectKind.Configuration => (WatchEventKind.DomainPolicyChanged, [WatchdogAttributes.EnabledFeature, WatchdogAttributes.DsHeuristics, Attr.TombstoneLifetime]),
            WatchObjectKind.PasswordSettings => (WatchEventKind.PasswordSettingsChanged, WatchedAttributes.PasswordSettings),
            WatchObjectKind.GroupPolicy => (WatchEventKind.GroupPolicyChanged, WatchedAttributes.GroupPolicy),
            WatchObjectKind.Trust => (WatchEventKind.TrustChanged, WatchedAttributes.Trust),
            _ => (default(WatchEventKind), (IReadOnlyList<string>)[]),
        };
        var deltas = TakeAll(diff, consumed, attributes);
        if (deltas.Count > 0)
        {
            drafts.Add(new EventDraft { Kind = kind, Changes = deltas.Select(_changes.FromDelta).ToList(), MetadataAttributes = attributes });
        }
    }

    private void ClassifyLeftovers(ClassificationInput input, HashSet<string> consumed, List<EventDraft> drafts)
    {
        var diff = input.Diff;
        var leftovers = diff.Attributes.Where(a => !consumed.Contains(a.Attribute)).ToList();
        if (diff.New.Kind == WatchObjectKind.Computer)
        {
            leftovers = leftovers.Where(a => !NoiseAttributes.IsDroppedComputerChange(a.Attribute, input.Directory)
                || a.Attribute.Equals(Attr.ServicePrincipalName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (input.DnsHostNameConflict && diff.Get(Attr.DnsHostName) is { } dns)
            {
                drafts.Add(Single(WatchEventKind.ComputerNameSpoofing, dns));
                leftovers.RemoveAll(a => a.Attribute.Equals(Attr.DnsHostName, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (leftovers.Count > 0)
        {
            drafts.Add(new EventDraft
            {
                Kind = WatchEventKind.AttributesChanged,
                Changes = leftovers.Select(_changes.FromDelta).ToList(),
                MetadataAttributes = leftovers.Select(a => a.Attribute).ToList(),
            });
        }
    }

    /// <summary>Delegation attribute changes join the DelegationChanged event of the UAC bits (one event per diff).</summary>
    private void MergeDelegation(List<EventDraft> drafts, IReadOnlyList<AttributeDelta> deltas)
    {
        var changes = deltas.Select(_changes.FromDelta).ToList();
        var index = drafts.FindIndex(d => d.Kind == WatchEventKind.DelegationChanged);
        if (index >= 0)
        {
            drafts[index] = drafts[index] with
            {
                Changes = [.. drafts[index].Changes, .. changes],
                MetadataAttributes = [.. drafts[index].MetadataAttributes, .. deltas.Select(d => d.Attribute)],
            };
            return;
        }

        drafts.Add(new EventDraft { Kind = WatchEventKind.DelegationChanged, Changes = changes, MetadataAttributes = DelegationAttributes });
    }

    private EventDraft Single(WatchEventKind kind, AttributeDelta delta) =>
        new() { Kind = kind, Changes = [_changes.FromDelta(delta)], MetadataAttributes = [delta.Attribute] };

    private static string Describe(GpLinks.Link link, ClassificationInput input)
    {
        var name = input.GroupPolicyNames.TryGetValue(ValueCanonicalizer.NormalizeDn(link.Dn), out var displayName) ? displayName : null;
        var flags = link.Enforced ? " [enforced]" : string.Empty;
        flags += link.Disabled ? " [disabled]" : string.Empty;
        return name is null ? link.Dn + flags : $"{name} ({link.Dn}){flags}";
    }

    private static bool IsOwnDcName(ClassificationInput input, string sam) =>
        string.Equals(input.Diff.Old.SamAccountName, sam, StringComparison.OrdinalIgnoreCase);

    private static AttributeDelta? Take(RawObjectDiff diff, HashSet<string> consumed, string attribute)
    {
        if (consumed.Contains(attribute) || diff.Get(attribute) is not { } delta)
        {
            return null;
        }

        consumed.Add(attribute);
        return delta;
    }

    private static List<AttributeDelta> TakeAll(RawObjectDiff diff, HashSet<string> consumed, IEnumerable<string> attributes)
    {
        var result = new List<AttributeDelta>();
        foreach (var attribute in attributes)
        {
            if (Take(diff, consumed, attribute) is { } delta)
            {
                result.Add(delta);
            }
        }

        return result;
    }

    private static string? First(SnapshotObject snapshot, string attribute) =>
        snapshot.Attributes.TryGetValue(attribute, out var value) && value.Values.Count > 0 ? value.Values[0] : null;

    private static long? Number(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
}
