using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// The actor of a resolved candidate (section 11.4 #5): <c>SubjectUserSid</c> → snapshot (<c>FindBySidAsync</c>) for
/// display name and DN, fallback <c>SubjectDomainName\SubjectUserName</c>. S-1-5-18 or the DC's own <c>DC$</c> account →
/// reason SYSTEM ("System (DC01)"). Subject = object (a computer updating itself, a user's own password change via
/// 4723) → tag <c>selfChange</c>, reason SELF_CHANGE for computers and service accounts; 4724 by another account →
/// tag <c>passwordReset</c>; a detected sync account → tag <c>syncAccount</c>.
/// </summary>
public sealed class ActorResolver(IDetectionStore detection, ISyncAccountCatalog syncAccounts)
{
    /// <summary>LocalSystem.</summary>
    public const string LocalSystemSid = "S-1-5-18";

    /// <summary>The actor of <paramref name="evidence"/> for <paramref name="watchEvent"/>.</summary>
    public async Task<ResolvedActor> ResolveAsync(SecurityEvent evidence, WatchEvent watchEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(watchEvent);
        var sid = evidence.SubjectSid;
        if (IsSystem(evidence))
        {
            var dc = evidence.Computer.Split('.')[0].ToUpperInvariant();
            return new ResolvedActor(
                new PrincipalRef { Sid = sid ?? LocalSystemSid, Account = evidence.SubjectAccount ?? "NT AUTHORITY\\SYSTEM", DisplayName = $"System ({dc})" },
                AttributionReasons.System,
                []);
        }

        var snapshot = sid is null ? null : await detection.FindBySidAsync(sid, cancellationToken).ConfigureAwait(false);
        var actor = new PrincipalRef
        {
            Sid = sid,
            Account = evidence.SubjectAccount ?? snapshot?.SamAccountName,
            DisplayName = snapshot?.DisplayName ?? snapshot?.Name,
            Dn = snapshot?.Dn,
        };

        var tags = new List<string>();
        string? reason = null;
        var self = sid is not null && string.Equals(sid, watchEvent.Object.Sid, StringComparison.OrdinalIgnoreCase);
        if (self || evidence.EventCode == SecurityEventCatalog.PasswordChangeAttempt)
        {
            tags.Add(EventTags.SelfChange);
            if (self && watchEvent.Object.Kind is WatchObjectKind.Computer or WatchObjectKind.ServiceAccount)
            {
                reason = AttributionReasons.SelfChange;
            }
        }

        if (evidence.EventCode == SecurityEventCatalog.PasswordReset && !self)
        {
            tags.Add(EventTags.PasswordReset);
        }

        if (syncAccounts.IsSyncAccount(sid))
        {
            tags.Add(EventTags.SyncAccount);
        }

        return new ResolvedActor(actor, reason, tags);
    }

    /// <summary>True when the subject is the DC itself: S-1-5-18, or the <c>NAME$</c> account of the DC that logged
    /// the record.</summary>
    public static bool IsSystem(SecurityEvent evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (string.Equals(evidence.SubjectSid, LocalSystemSid, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var user = evidence.Get("SubjectUserName");
        return user is { Length: > 1 } && user.EndsWith('$')
            && string.Equals(user[..^1], evidence.Computer.Split('.')[0], StringComparison.OrdinalIgnoreCase);
    }
}
