using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// userAccountControl bit changes → kinds (section 10.6.1): ACCOUNTDISABLE → AccountDisabled/Enabled; the delegation
/// bits → DelegationChanged (TRUSTED_FOR_DELEGATION on a DC is no event); SERVER_TRUST_ACCOUNT set →
/// DomainControllerAdded; every other bit → one AccountFlagsChanged carrying all of them. Each event's
/// userAccountControl change lists only its own flags, so the SeverityResolver's bit table applies per event.
/// </summary>
internal sealed class UacFlagClassifier
{
    private const long AccountDisable = (long)UserAccountControl.AccountDisable;
    private const long TrustedForDelegation = (long)UserAccountControl.TrustedForDelegation;
    private const long TrustedToAuthForDelegation = (long)UserAccountControl.TrustedToAuthForDelegation;
    private const long ServerTrustAccount = (long)UserAccountControl.ServerTrustAccount;
    private const long Lockout = (long)UserAccountControl.Lockout;

    private readonly AttributeChangeBuilder _changes;

    public UacFlagClassifier(AttributeChangeBuilder changes)
    {
        _changes = changes;
    }

    /// <summary>Drafts for a change from <paramref name="before"/> to <paramref name="after"/> (absent = 0).</summary>
    public IReadOnlyList<EventDraft> Classify(SnapshotAttribute? before, SnapshotAttribute? after)
    {
        var oldValue = Value(before);
        var newValue = Value(after);
        var set = newValue & ~oldValue;
        var cleared = oldValue & ~newValue;
        var drafts = new List<EventDraft>();
        var full = _changes.Build(Attr.UserAccountControl, before, after);
        // The tag domainController comes from the new value (10.6.1), so a one-step promotion (SERVER_TRUST_ACCOUNT and
        // TRUSTED_FOR_DELEGATION set together) is DomainControllerAdded only – as for a new DC (ClassifyCreated).
        var isDomainController = (newValue & ServerTrustAccount) != 0;

        if ((set & AccountDisable) != 0)
        {
            drafts.Add(Draft(WatchEventKind.AccountDisabled, full, AccountDisable));
        }
        else if ((cleared & AccountDisable) != 0)
        {
            drafts.Add(Draft(WatchEventKind.AccountEnabled, full, AccountDisable));
        }

        var delegation = (set | cleared) & (TrustedForDelegation | TrustedToAuthForDelegation);
        if (isDomainController && (set & TrustedForDelegation) != 0)
        {
            delegation &= ~TrustedForDelegation;
        }

        if (delegation != 0)
        {
            drafts.Add(Draft(WatchEventKind.DelegationChanged, full, delegation));
        }

        if ((set & ServerTrustAccount) != 0)
        {
            drafts.Add(Draft(WatchEventKind.DomainControllerAdded, full, ServerTrustAccount));
        }

        var other = (set | cleared) & ~(AccountDisable | TrustedForDelegation | TrustedToAuthForDelegation | Lockout);
        other &= ~(set & ServerTrustAccount);
        if (other != 0)
        {
            drafts.Add(Draft(WatchEventKind.AccountFlagsChanged, full, other));
        }

        return drafts;
    }

    /// <summary>
    /// Risky bits of a new account's final userAccountControl evaluated against an empty baseline (10.6.3):
    /// AccountFlagsChanged for PASSWD_NOTREQD, DONT_REQ_PREAUTH, ENCRYPTED_TEXT_PWD_ALLOWED, USE_DES_KEY_ONLY and
    /// DONT_EXPIRE_PASSWORD, DelegationChanged for the delegation bits.
    /// </summary>
    public IReadOnlyList<EventDraft> ClassifyCreated(SnapshotAttribute? final)
    {
        var value = Value(final);
        var full = _changes.Build(Attr.UserAccountControl, null, final);
        var drafts = new List<EventDraft>();
        const long risky = (long)(UserAccountControl.PasswordNotRequired | UserAccountControl.DontRequirePreauth
            | UserAccountControl.EncryptedTextPasswordAllowed | UserAccountControl.UseDesKeyOnly | UserAccountControl.DontExpirePassword);
        if ((value & risky) != 0)
        {
            drafts.Add(Draft(WatchEventKind.AccountFlagsChanged, full, value & risky));
        }

        var delegation = value & (TrustedForDelegation | TrustedToAuthForDelegation);
        if ((value & ServerTrustAccount) != 0)
        {
            delegation &= ~TrustedForDelegation;
        }

        if (delegation != 0)
        {
            drafts.Add(Draft(WatchEventKind.DelegationChanged, full, delegation));
        }

        return drafts;
    }

    private static EventDraft Draft(WatchEventKind kind, AttributeChange full, long bits)
    {
        var names = FlagNames.Decode(Attr.UserAccountControl, bits);
        return new EventDraft
        {
            Kind = kind,
            Changes =
            [
                full with
                {
                    FlagsAdded = full.FlagsAdded.Where(names.Contains).ToList(),
                    FlagsRemoved = full.FlagsRemoved.Where(names.Contains).ToList(),
                },
            ],
            MetadataAttributes = [Attr.UserAccountControl],
        };
    }

    private static long Value(SnapshotAttribute? attribute) =>
        attribute is { Values.Count: > 0 } && long.TryParse(attribute.Values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value & 0xFFFFFFFFL
            : 0;
}
