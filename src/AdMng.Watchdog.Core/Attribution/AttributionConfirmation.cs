using System.Globalization;
using AdMng.Core.Ldap;
using AdMng.Core.Schema;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// "Attribute confirmed" of the score table (section 11.4 #3): 5136 attribute name and value (Value Added = new value,
/// Value Deleted = old value, compared after normalization – DNs by <see cref="DistinguishedName.AreEqual"/>); 4738/4742
/// SAM field present; UAC bit changed in the ACB diff; 4722/4725 for enable/disable; 4781 old/new names; 4728… member SID;
/// the kind's own event (4720, 5137, 5141, 5138, 4767, 4724/4723, 4764, …). A record that describes a different change
/// of the same object (another attribute, member, value or DN) contradicts the change and is not a candidate.
/// </summary>
public static class AttributionConfirmation
{
    /// <summary>How <paramref name="candidate"/> fits <paramref name="watchEvent"/>.</summary>
    public static CandidateFit Assess(SecurityEvent candidate, WatchEvent watchEvent)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(watchEvent);
        var code = candidate.EventCode;
        return code switch
        {
            SecurityEventCatalog.ObjectModified => DirectoryModification(candidate, watchEvent),
            SecurityEventCatalog.ObjectMoved => Move(candidate, watchEvent),
            SecurityEventCatalog.UserChanged or SecurityEventCatalog.ComputerChanged => SamChange(candidate, watchEvent),
            SecurityEventCatalog.AccountNameChanged => Rename(candidate, watchEvent),
            _ when SecurityEventCatalog.IsMembershipCode(code) => Membership(candidate, watchEvent),
            _ when SecurityEventCatalog.GroupChangedCodes.Contains(code) => CandidateFit.Unconfirmed,
            _ => CandidateFit.Confirmed,   // the kind's own event id (creation, deletion, enable, unlock, reset, …)
        };
    }

    /// <summary>The attributes the change concerns: the event's changes plus the kind's implied attributes.</summary>
    public static IReadOnlySet<string> ChangedAttributes(WatchEvent watchEvent)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in watchEvent.Changes ?? [])
        {
            set.Add(change.Attribute);
        }

        set.UnionWith(SecurityEventCatalog.ImpliedAttributes(watchEvent.Kind));
        return set;
    }

    private static CandidateFit DirectoryModification(SecurityEvent candidate, WatchEvent watchEvent)
    {
        var attribute = candidate.Get("AttributeLDAPDisplayName");
        if (string.IsNullOrEmpty(attribute) || !ChangedAttributes(watchEvent).Contains(attribute))
        {
            return CandidateFit.Contradicts;
        }

        var value = candidate.Get("AttributeValue");
        var operation = candidate.Get("OperationType");
        var added = operation is InsertionStringDecoder.ValueAdded or InsertionStringDecoder.ValueAddedWithExpirationTime;
        var deleted = operation is InsertionStringDecoder.ValueDeleted or InsertionStringDecoder.ValueDeletedWithExpirationTime
            or InsertionStringDecoder.ValueAutoDeletedWithExpirationTime;

        if (string.Equals(attribute, "member", StringComparison.OrdinalIgnoreCase)
            && watchEvent.Kind is WatchEventKind.GroupMemberAdded or WatchEventKind.GroupMemberRemoved)
        {
            if ((watchEvent.Kind == WatchEventKind.GroupMemberAdded && deleted) || (watchEvent.Kind == WatchEventKind.GroupMemberRemoved && added))
            {
                return CandidateFit.Contradicts;
            }

            return value is null || value == SecretAttributes.Mask || watchEvent.Member?.Dn is not { } memberDn
                ? CandidateFit.Confirmed
                : DistinguishedName.AreEqual(value, memberDn) ? CandidateFit.Confirmed : CandidateFit.Contradicts;
        }

        if (value is null || value == SecretAttributes.Mask || !(added || deleted))
        {
            return CandidateFit.Confirmed;   // the name confirms; no comparable value was kept
        }

        var change = (watchEvent.Changes ?? []).FirstOrDefault(c => string.Equals(c.Attribute, attribute, StringComparison.OrdinalIgnoreCase));
        if (change is null || change.Redacted)
        {
            return CandidateFit.Confirmed;
        }

        var expected = added ? change.NewValues.Concat(change.Added) : change.OldValues.Concat(change.Removed);
        var values = expected.ToList();
        if (values.Count == 0 || change.Truncated)
        {
            return CandidateFit.Confirmed;
        }

        return values.Any(v => ValuesEqual(v, value)) ? CandidateFit.Confirmed : CandidateFit.Unconfirmed;
    }

    private static CandidateFit Move(SecurityEvent candidate, WatchEvent watchEvent)
    {
        var oldDn = candidate.Get("OldObjectDN");
        var newDn = candidate.Get("NewObjectDN");
        if (watchEvent.PreviousDn is { } previous && oldDn is not null && !DistinguishedName.AreEqual(previous, oldDn))
        {
            return CandidateFit.Contradicts;
        }

        if (newDn is not null && !DistinguishedName.AreEqual(watchEvent.Object.Dn, newDn))
        {
            return CandidateFit.Contradicts;
        }

        return CandidateFit.Confirmed;
    }

    private static CandidateFit SamChange(SecurityEvent candidate, WatchEvent watchEvent)
    {
        var attributes = ChangedAttributes(watchEvent);
        var reported = new List<string>();
        foreach (var (field, attribute) in SecurityEventCatalog.SamFieldAttributes)
        {
            if (field is not ("OldUacValue" or "NewUacValue") && candidate.Get(field) is not null)
            {
                reported.Add(attribute);
            }
        }

        var uacChanged = TryUacChange(candidate, out var set, out var cleared) && (set | cleared) != 0;
        if (uacChanged)
        {
            reported.Add("userAccountControl");
        }

        if (reported.Count == 0)
        {
            return CandidateFit.Unconfirmed;   // 4738 is logged for non-SAM attributes too (all fields "-")
        }

        if (!reported.Any(attributes.Contains))
        {
            return CandidateFit.Contradicts;
        }

        if (uacChanged && IsUacKind(watchEvent.Kind))
        {
            var eventBits = EventUacBits(watchEvent);
            return eventBits is null || ((set | cleared) & eventBits.Value) != 0 ? CandidateFit.Confirmed : CandidateFit.Unconfirmed;
        }

        return CandidateFit.Confirmed;
    }

    private static CandidateFit Rename(SecurityEvent candidate, WatchEvent watchEvent)
    {
        var change = (watchEvent.Changes ?? []).FirstOrDefault(c => string.Equals(c.Attribute, "sAMAccountName", StringComparison.OrdinalIgnoreCase));
        var newName = candidate.Get("NewTargetUserName");
        if (change is null || change.NewValues.Count == 0 || newName is null)
        {
            return CandidateFit.Confirmed;
        }

        return change.NewValues.Any(v => string.Equals(v, newName, StringComparison.OrdinalIgnoreCase)) ? CandidateFit.Confirmed : CandidateFit.Contradicts;
    }

    private static CandidateFit Membership(SecurityEvent candidate, WatchEvent watchEvent)
    {
        var memberSid = candidate.Get("MemberSid");
        if (watchEvent.Member?.Sid is { } expected && memberSid is not null)
        {
            return string.Equals(expected, memberSid, StringComparison.OrdinalIgnoreCase) ? CandidateFit.Confirmed : CandidateFit.Contradicts;
        }

        return watchEvent.Member?.Dn is { } dn && candidate.Get("MemberName") is { } memberName
            ? DistinguishedName.AreEqual(dn, memberName) ? CandidateFit.Confirmed : CandidateFit.Contradicts
            : CandidateFit.Unconfirmed;
    }

    private static bool IsUacKind(WatchEventKind kind) =>
        kind is WatchEventKind.AccountDisabled or WatchEventKind.AccountEnabled or WatchEventKind.AccountFlagsChanged
            or WatchEventKind.DelegationChanged or WatchEventKind.DomainControllerAdded;

    private static bool TryUacChange(SecurityEvent candidate, out uint set, out uint cleared)
    {
        set = 0;
        cleared = 0;
        if (!SamAccountControlDecoder.TryParse(candidate.Get("OldUacValue"), out var oldAcb)
            || !SamAccountControlDecoder.TryParse(candidate.Get("NewUacValue"), out var newAcb))
        {
            return false;
        }

        (set, cleared) = SamAccountControlDecoder.LdapChanges(oldAcb, newAcb);
        return true;
    }

    /// <summary>The LDAP userAccountControl bits the event says changed; null when not derivable.</summary>
    private static uint? EventUacBits(WatchEvent watchEvent)
    {
        var change = (watchEvent.Changes ?? []).FirstOrDefault(c => string.Equals(c.Attribute, "userAccountControl", StringComparison.OrdinalIgnoreCase));
        if (change is not null
            && change.OldValues.Count == 1 && change.NewValues.Count == 1
            && uint.TryParse(change.OldValues[0], NumberStyles.None, CultureInfo.InvariantCulture, out var oldValue)
            && uint.TryParse(change.NewValues[0], NumberStyles.None, CultureInfo.InvariantCulture, out var newValue))
        {
            return oldValue ^ newValue;
        }

        return watchEvent.Kind switch
        {
            WatchEventKind.AccountDisabled or WatchEventKind.AccountEnabled => 0x2u,
            WatchEventKind.DomainControllerAdded => 0x2000u,
            _ => null,
        };
    }

    private static bool ValuesEqual(string expected, string actual) =>
        string.Equals(expected.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase)
        || (expected.Contains('=', StringComparison.Ordinal) && DistinguishedName.AreEqual(expected, actual));
}
