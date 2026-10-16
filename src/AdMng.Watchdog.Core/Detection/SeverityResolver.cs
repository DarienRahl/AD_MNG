using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Events;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Severity of a watch event (section 12.2), in this order: (1) base = catalog default per kind and object kind, the
/// userAccountControl bit tables (10.6.1), the PrivilegeGranted tier and the KeyCredentialChanged pattern (10.6.4);
/// (2) tag bumps – never lowering; (3) attribution-dependent re-resolution of PasswordChanged, AdminCountSet and user
/// KeyCredentialChanged (provisional while attribution is pending); (4) explicit per-kind overrides, which may lower
/// (krbtgt password change is exactly Medium). Stateless and thread-safe.
/// </summary>
public sealed class SeverityResolver
{
    /// <summary>Kinds whose severity depends on the final attribution (held by the planner, 13.2). The one source of this
    /// set: the attribution engine and the notification planner refer to it.</summary>
    public static IReadOnlySet<WatchEventKind> AttributionDependentKinds { get; } = new HashSet<WatchEventKind>
    {
        WatchEventKind.PasswordChanged, WatchEventKind.AdminCountSet, WatchEventKind.KeyCredentialChanged,
    };

    /// <summary>Kinds bumped to High for privileged and service accounts (12.2 row 1).</summary>
    private static readonly HashSet<WatchEventKind> PrivilegedBumpKinds =
    [
        WatchEventKind.AccountDisabled, WatchEventKind.AccountEnabled, WatchEventKind.AccountLockedOut,
        WatchEventKind.PasswordMustChangeSet, WatchEventKind.PasswordAgeReset, WatchEventKind.AccountFlagsChanged,
        WatchEventKind.AccountExpirationChanged, WatchEventKind.AccountNameChanged, WatchEventKind.ObjectDeleted,
        WatchEventKind.ObjectMoved, WatchEventKind.ObjectRenamed,
    ];

    /// <summary>Attributes whose change on a privileged or service account is High (12.2 row 2).</summary>
    private static readonly HashSet<string> SecurityRelevantAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        Attr.ScriptPath, Attr.ProfilePath, Attr.HomeDirectory, Attr.HomeDrive, Attr.UserWorkstations, Attr.LogonHours,
        Attr.ServicePrincipalName, Attr.SupportedEncryptionTypes, WatchdogAttributes.UserCertificate,
    };

    /// <summary>Logon-settings attributes: AttributesChanged is Medium (section 7).</summary>
    private static readonly HashSet<string> LogonSettingsAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        Attr.ScriptPath, Attr.ProfilePath, Attr.HomeDirectory, Attr.HomeDrive, Attr.UserWorkstations, Attr.LogonHours,
    };

    private static readonly HashSet<string> RiskyFlags = new(StringComparer.Ordinal)
    {
        "PASSWD_NOTREQD", "DONT_REQ_PREAUTH", "ENCRYPTED_TEXT_PWD_ALLOWED", "USE_DES_KEY_ONLY",
    };

    private static readonly HashSet<string> ProtectionFlags = new(StringComparer.Ordinal) { "SMARTCARD_REQUIRED", "NOT_DELEGATED" };

    private static readonly string[] DefaultPolicyGuids =
    [
        FormatGuid("31B2F340-016D-11D2-945F-00C04FB984F9"), FormatGuid("6AC1786C-016F-11D2-945F-00C04FB984F9"),
    ];

    /// <summary>
    /// The severity at detection time (attribution normally Pending): steps 1–4. <paramref name="baseSeverity"/>
    /// replaces step 1 where the event alone cannot tell it (PrivilegeGranted tier, ManagedAccountLinkChanged with a
    /// privileged link target).
    /// </summary>
    public Severity Resolve(WatchEvent watchEvent, SeverityContext? context = null, Severity? baseSeverity = null)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        return ResolveCore(watchEvent, watchEvent.Tags, watchEvent.Attribution, context ?? SeverityContext.Empty, baseSeverity);
    }

    /// <summary>
    /// Re-resolution with the final attribution and the tags it added (11.4 #8). Kinds that are not attribution
    /// dependent keep their stored severity.
    /// </summary>
    public Severity ResolveWithAttribution(
        WatchEvent watchEvent,
        EventAttribution attribution,
        IEnumerable<string>? addedTags = null,
        SeverityContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        ArgumentNullException.ThrowIfNull(attribution);
        if (!AttributionDependentKinds.Contains(watchEvent.Kind))
        {
            return watchEvent.Severity;
        }

        var tags = watchEvent.Tags.Concat(addedTags ?? []).Distinct(StringComparer.Ordinal).ToList();
        return ResolveCore(watchEvent, tags, attribution, context ?? SeverityContext.Empty, null);
    }

    /// <summary>
    /// Steps 3 and 4 alone, applied to a stored severity (the result of steps 1–2 at detection): the attribution engine's
    /// re-resolution when an attribution becomes final (11.4 #8). Kinds without a step-3 rule keep
    /// <paramref name="severity"/> apart from the explicit overrides of step 4.
    /// </summary>
    public static Severity ApplyAttribution(WatchEvent watchEvent, Severity severity, IReadOnlyList<string> tags, EventAttribution attribution)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(attribution);
        return Overrides(watchEvent, AttributionDependent(watchEvent, severity, tags, attribution), tags);
    }

    /// <summary>Step 1 computed from the event data (catalog default, bit tables, patterns).</summary>
    public Severity BaseSeverity(WatchEvent watchEvent, SeverityContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        context ??= SeverityContext.Empty;
        var tags = watchEvent.Tags;
        var catalog = EventKindCatalog.DefaultSeverity(watchEvent.Kind, watchEvent.Object.Kind);
        return watchEvent.Kind switch
        {
            WatchEventKind.AccountFlagsChanged => FlagSeverity(watchEvent, tags),
            WatchEventKind.DelegationChanged => DelegationSeverity(watchEvent),
            WatchEventKind.KeyCredentialChanged => KeyCredentialBase(watchEvent, tags),
            WatchEventKind.AccountExpirationChanged => ClearedToNever(watchEvent) ? Severity.Medium : catalog,
            WatchEventKind.AdminCountSet => Has(tags, EventTags.Privileged) ? Severity.Info : Severity.High,
            WatchEventKind.AttributesChanged => watchEvent.Changes.Any(c => LogonSettingsAttributes.Contains(c.Attribute)) ? Severity.Medium : catalog,
            WatchEventKind.ServiceAccountCreated => IsDelegatedMsa(watchEvent) ? Severity.High : catalog,
            WatchEventKind.GroupPolicyChanged => IsDefaultPolicy(watchEvent.Object.Dn) ? Severity.Medium : catalog,
            WatchEventKind.GroupPolicyLinkChanged => IsDomainControllersOu(watchEvent.Object.Dn, context) ? Severity.High : catalog,
            WatchEventKind.CertificateMappingChanged => Has(tags, EventTags.Privileged) ? Severity.Critical : catalog,
            WatchEventKind.DomainControllerAdded => Has(tags, EventTags.OtherDomain) ? Severity.High : catalog,
            WatchEventKind.DomainControllerRemoved => Has(tags, EventTags.OtherDomain) ? Severity.Medium : catalog,
            _ => catalog,
        };
    }

    private Severity ResolveCore(WatchEvent watchEvent, IReadOnlyList<string> tags, EventAttribution attribution, SeverityContext context, Severity? baseSeverity)
    {
        var tagged = watchEvent with { Tags = tags };
        var severity = baseSeverity ?? BaseSeverity(tagged, context);
        severity = Max(severity, Bumps(tagged, tags, context));
        severity = AttributionDependent(tagged, severity, tags, attribution);
        return Overrides(tagged, severity, tags);
    }

    private static Severity Bumps(WatchEvent e, IReadOnlyList<string> tags, SeverityContext context)
    {
        var privileged = Has(tags, EventTags.Privileged) || Has(tags, EventTags.ServiceAccount);
        var result = Severity.Info;
        if (privileged && PrivilegedBumpKinds.Contains(e.Kind))
        {
            result = Severity.High;
        }

        if (privileged && e.Kind == WatchEventKind.AttributesChanged && e.Changes.Any(c => SecurityRelevantAttributes.Contains(c.Attribute)))
        {
            result = Severity.High;
        }

        if (IsCreated(e.Kind) && (Has(tags, EventTags.UnexpectedContainer) || Has(tags, EventTags.CreatedByNonAdmin) || Has(tags, EventTags.Server)))
        {
            result = Max(result, Severity.Medium);
        }

        if (e.Kind == WatchEventKind.AccountEnabled && (Has(tags, EventTags.LongDisabled) || Has(tags, EventTags.DisabledOu)))
        {
            result = Severity.High;
        }

        if (e.Kind == WatchEventKind.ObjectMoved && Has(tags, EventTags.DisabledOu)
            && context.IsInDisabledSubtree(e.PreviousDn) && !context.IsInDisabledSubtree(e.Object.Dn))
        {
            result = Severity.High;
        }

        if (privileged && e.Kind is WatchEventKind.PasswordExpiring or WatchEventKind.AccountExpiring && e.Expiry is { DaysLeft: <= 7 })
        {
            result = Severity.High;
        }

        if (e.Kind == WatchEventKind.GroupMemberRemoved && e.Object.Sid is { } groupSid && context.ProtectRemovalSids.Contains(groupSid))
        {
            result = Severity.High;
        }

        return result;
    }

    private static Severity AttributionDependent(WatchEvent e, Severity severity, IReadOnlyList<string> tags, EventAttribution attribution)
    {
        var final = attribution.Status is AttributionStatus.Resolved or AttributionStatus.Ambiguous or AttributionStatus.Unknown;
        switch (e.Kind)
        {
            case WatchEventKind.PasswordChanged when Has(tags, EventTags.Privileged) || Has(tags, EventTags.ServiceAccount):
                if (!final)
                {
                    return Severity.High;
                }

                return Has(tags, EventTags.SelfChange) && !Has(tags, EventTags.PasswordReset) ? Severity.Info : Severity.High;
            case WatchEventKind.AdminCountSet when !Has(tags, EventTags.Privileged):
                return final && attribution.Status == AttributionStatus.Resolved
                    && string.Equals(attribution.Reason, AttributionReasons.System, StringComparison.Ordinal)
                    ? Severity.Info
                    : Severity.High;
            case WatchEventKind.KeyCredentialChanged when e.Object.Kind == WatchObjectKind.User && !Has(tags, EventTags.Privileged):
                var actorIsOther = attribution.Status == AttributionStatus.Resolved && attribution.Actor?.Sid is { } actorSid
                    && !string.Equals(actorSid, e.Object.Sid, StringComparison.OrdinalIgnoreCase)
                    && !Has(tags, EventTags.SyncAccount);
                return actorIsOther ? Severity.High : Severity.Medium;
            default:
                return severity;
        }
    }

    private static Severity Overrides(WatchEvent e, Severity severity, IReadOnlyList<string> tags) =>
        e.Kind == WatchEventKind.PasswordChanged && Has(tags, EventTags.Krbtgt) ? Severity.Medium : severity;

    private static Severity FlagSeverity(WatchEvent e, IReadOnlyList<string> tags)
    {
        var change = e.Changes.FirstOrDefault(c => c.Attribute.Equals(Attr.UserAccountControl, StringComparison.OrdinalIgnoreCase));
        if (change is null)
        {
            return Severity.Low;
        }

        var creation = change.OldValues.Count == 0;
        var disabledNow = change.NewValues.Count > 0 && long.TryParse(change.NewValues[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var uac)
            && (uac & (long)UserAccountControl.AccountDisable) != 0;
        var result = Severity.Low;
        foreach (var flag in change.FlagsAdded)
        {
            var severity = flag switch
            {
                "PASSWD_NOTREQD" when creation && disabledNow => Severity.Low,
                _ when RiskyFlags.Contains(flag) => Severity.High,
                "DONT_EXPIRE_PASSWORD" => Severity.Medium,
                _ => Severity.Low,
            };
            result = Max(result, severity);
        }

        foreach (var flag in change.FlagsRemoved)
        {
            var severity = flag switch
            {
                "SERVER_TRUST_ACCOUNT" => Severity.High,
                _ when ProtectionFlags.Contains(flag) && Has(tags, EventTags.Privileged) => Severity.High,
                _ => Severity.Low,
            };
            result = Max(result, severity);
        }

        return result;
    }

    private static Severity DelegationSeverity(WatchEvent e)
    {
        var result = Severity.Info;
        foreach (var change in e.Changes)
        {
            if (change.Attribute.Equals(Attr.UserAccountControl, StringComparison.OrdinalIgnoreCase))
            {
                if (change.FlagsAdded.Contains("TRUSTED_FOR_DELEGATION"))
                {
                    result = Max(result, Severity.Critical);
                }

                if (change.FlagsAdded.Contains("TRUSTED_TO_AUTH_FOR_DELEGATION"))
                {
                    result = Max(result, Severity.High);
                }

                if (change.FlagsRemoved.Contains("TRUSTED_FOR_DELEGATION") || change.FlagsRemoved.Contains("TRUSTED_TO_AUTH_FOR_DELEGATION"))
                {
                    result = Max(result, Severity.Medium);
                }
            }
            else
            {
                result = Max(result, Severity.High);
            }
        }

        return result == Severity.Info ? Severity.High : result;
    }

    private static Severity KeyCredentialBase(WatchEvent e, IReadOnlyList<string> tags)
    {
        var privileged = Has(tags, EventTags.Privileged);
        if (e.Object.Kind is WatchObjectKind.Computer)
        {
            if (privileged || Has(tags, EventTags.DomainController))
            {
                return Severity.High;
            }

            var change = e.Changes.FirstOrDefault(c => c.Attribute.Equals(WatchdogAttributes.KeyCredentialLink, StringComparison.OrdinalIgnoreCase));
            return change is { OldCount: 0, NewCount: 1 } ? Severity.Low : Severity.High;
        }

        return privileged ? Severity.High : Severity.Medium;
    }

    private static bool ClearedToNever(WatchEvent e) =>
        e.Changes.FirstOrDefault(c => c.Attribute.Equals(Attr.AccountExpires, StringComparison.OrdinalIgnoreCase)) is { } change
        && change.OldValues.Count > 0 && !IsNever(change.OldValues[0])
        && (change.NewValues.Count == 0 || IsNever(change.NewValues[0]));

    private static bool IsNever(string value) => value is "0" or "9223372036854775807";

    private static bool IsDelegatedMsa(WatchEvent e) =>
        e.Changes.Any(c => c.Attribute.Equals(Attr.ObjectClass, StringComparison.OrdinalIgnoreCase)
            && c.NewValues.Contains(ObjectClasses.DelegatedManagedServiceAccount, StringComparer.OrdinalIgnoreCase));

    private static bool IsDefaultPolicy(string dn) => DefaultPolicyGuids.Any(g => dn.Contains(g, StringComparison.OrdinalIgnoreCase));

    private static bool IsDomainControllersOu(string dn, SeverityContext context) =>
        context.DomainDn is { } domain && DistinguishedName.TryParse(domain, out var domainDn)
        && DistinguishedName.AreEqual(dn, domainDn.Child("OU", "Domain Controllers").ToString());

    private static bool IsCreated(WatchEventKind kind) => kind is WatchEventKind.UserCreated or WatchEventKind.ComputerCreated
        or WatchEventKind.GroupCreated or WatchEventKind.OrganizationalUnitCreated or WatchEventKind.ContactCreated
        or WatchEventKind.ServiceAccountCreated;

    private static bool Has(IReadOnlyList<string> tags, string tag) => tags.Contains(tag, StringComparer.Ordinal);

    private static Severity Max(Severity a, Severity b) => a >= b ? a : b;

    private static string FormatGuid(string guid) => "{" + guid + "}";
}
