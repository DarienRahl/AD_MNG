using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Tags of a detected event (section 10.6.2). <c>privileged</c> comes only from the current PrivilegeState (the
/// member for membership kinds); <c>adminCountOrphan</c> marks adminCount=1 outside it; container tags use the
/// resolved wellKnownObjects; <c>longDisabled</c> is decided by the caller (it needs a base read). The attribution
/// adds selfChange, passwordReset and syncAccount later.
/// </summary>
internal sealed class EventTagger
{
    private static readonly HashSet<WatchEventKind> CreatedKinds =
    [
        WatchEventKind.UserCreated, WatchEventKind.ComputerCreated, WatchEventKind.GroupCreated,
        WatchEventKind.OrganizationalUnitCreated, WatchEventKind.ContactCreated, WatchEventKind.ServiceAccountCreated,
    ];

    /// <summary>Tags for <paramref name="draft"/> about <paramref name="subject"/> (null for objects without a snapshot).</summary>
    public IReadOnlyList<string> Tag(EventDraft draft, SnapshotObject? subject, ObjectRef objectRef, TaggingContext context, bool longDisabled = false)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(objectRef);
        ArgumentNullException.ThrowIfNull(context);
        var tags = new List<string>(draft.ExtraTags);
        var privilegedKey = draft.Kind is WatchEventKind.GroupMemberAdded or WatchEventKind.GroupMemberRemoved
            ? MemberKey(draft.Member)
            : MemberKey(objectRef);
        var privileged = privilegedKey is not null && context.IsPrivileged(privilegedKey);
        if (privileged)
        {
            tags.Add(EventTags.Privileged);
        }

        if (subject is not null)
        {
            // adminCountOrphan is about the subject itself (for membership kinds the group, not the member).
            var subjectPrivileged = context.IsPrivileged(subject.ObjectGuid.ToString("N")) || context.IsPrivilegedRoot(subject);
            TagSubject(tags, draft, subject, objectRef, context, subjectPrivileged);
        }

        if (longDisabled && draft.Kind == WatchEventKind.AccountEnabled)
        {
            tags.Add(EventTags.LongDisabled);
        }

        if (IsUnder(objectRef.Dn, context.DisabledAccountsSubtrees)
            || (draft.Kind is WatchEventKind.ObjectMoved && IsUnder(draft.PreviousDn, context.DisabledAccountsSubtrees)))
        {
            tags.Add(EventTags.DisabledOu);
        }

        switch (AdConvert.GetRid(objectRef.Sid))
        {
            case WellKnownRids.Krbtgt:
                tags.Add(EventTags.Krbtgt);
                break;
            case WellKnownRids.Administrator:
                tags.Add(EventTags.BuiltInAdministrator);
                break;
        }

        if (context.Reconciliation)
        {
            tags.Add(EventTags.Reconciliation);
        }

        return tags.Distinct(StringComparer.Ordinal).ToList();
    }

    private static void TagSubject(List<string> tags, EventDraft draft, SnapshotObject subject, ObjectRef objectRef, TaggingContext context, bool privileged)
    {
        if (!privileged && First(subject, Attr.AdminCount) == "1")
        {
            tags.Add(EventTags.AdminCountOrphan);
        }

        var spnCount = subject.Attributes.TryGetValue(Attr.ServicePrincipalName, out var spns) ? spns.Values.Count : 0;
        if (subject.Kind == WatchObjectKind.ServiceAccount || (subject.Kind == WatchObjectKind.User && spnCount > 0)
            || IsUnder(objectRef.Dn, context.ServiceAccountSubtrees))
        {
            tags.Add(EventTags.ServiceAccount);
        }

        var uac = long.TryParse(First(subject, Attr.UserAccountControl), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        if (subject.Kind == WatchObjectKind.Computer)
        {
            if ((uac & (long)UserAccountControl.ServerTrustAccount) != 0)
            {
                tags.Add(EventTags.DomainController);
            }

            if (First(subject, Attr.OperatingSystem)?.Contains("Server", StringComparison.OrdinalIgnoreCase) == true)
            {
                tags.Add(EventTags.Server);
            }
        }

        var created = CreatedKinds.Contains(draft.Kind);
        if (created && subject.Kind == WatchObjectKind.Computer && subject.Attributes.ContainsKey(WatchdogAttributes.CreatorSid))
        {
            tags.Add(EventTags.CreatedByNonAdmin);
        }

        if ((created || draft.Kind == WatchEventKind.ObjectMoved) && context.Containers is { } containers
            && DistinguishedName.TryParse(objectRef.Dn, out var dn) && dn.Parent is { } parent)
        {
            if (containers.IsDefaultContainer(parent.ToString()))
            {
                tags.Add(EventTags.DefaultContainer);
            }

            if (created && containers.IsUnexpectedContainer(parent.ToString()))
            {
                tags.Add(EventTags.UnexpectedContainer);
            }
        }
    }

    /// <summary>The PrivilegeState key of a reference: GUID "N", else sid:…, else null.</summary>
    public static string? MemberKey(ObjectRef? reference) => reference switch
    {
        { ObjectGuid: { } guid } => guid.ToString("N"),
        { Sid: { } sid } => ValueCanonicalizer.SidPrefix + sid,
        _ => null,
    };

    private static string? First(SnapshotObject subject, string attribute) =>
        subject.Attributes.TryGetValue(attribute, out var value) && value.Values.Count > 0 ? value.Values[0] : null;

    private static bool IsUnder(string? dn, IReadOnlyList<DistinguishedName> subtrees) =>
        dn is not null && subtrees.Count > 0 && DistinguishedName.TryParse(dn, out var parsed)
        && subtrees.Any(s => parsed == s || parsed.IsDescendantOf(s));
}
