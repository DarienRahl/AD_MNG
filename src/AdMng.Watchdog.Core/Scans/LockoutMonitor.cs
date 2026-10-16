using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Detection;

namespace AdMng.Watchdog.Core.Scans;

/// <summary>
/// Lockout expiry (section 10.13, only when <see cref="WatchEventKind.LockoutExpired"/> is recorded): every
/// <c>LockoutRecheckInterval</c> the users with <c>lockoutTime ≥ 1</c> are read with the constructed
/// <c>msDS-User-Account-Control-Computed</c> (the filter cannot use the constructed bit – evaluated client-side). An
/// account that was locked (0x10) at the previous check, is no longer locked and still has the same lockoutTime →
/// LockoutExpired. The previous set is kept in memory; the first check produces no events.
/// </summary>
internal sealed class LockoutMonitor
{
    private const long LockedBit = (long)UserAccountControl.Lockout;

    private readonly ScanEventWriter _writer;
    private readonly LockoutPolicyResolver _policies;
    private Dictionary<Guid, long>? _locked;

    public LockoutMonitor(ScanEventWriter writer, LockoutPolicyResolver policies)
    {
        _writer = writer;
        _policies = policies;
    }

    public async Task RunAsync(ScanContext context, CancellationToken cancellationToken)
    {
        if (!context.Settings.IsRecorded(WatchEventKind.LockoutExpired))
        {
            _locked = null;
            return;
        }

        var filter = LdapFilter.And(DetectionFilters.Users, LdapFilter.GreaterOrEqual(Attr.LockoutTime, 1));
        var locked = new Dictionary<Guid, long>();
        var previous = _locked;
        await foreach (var entry in ScanQueries.SearchAsync(context, filter, [Attr.LockoutTime, Attr.UserAccountControlComputed], cancellationToken).ConfigureAwait(false))
        {
            if (entry.ObjectGuid is not { } guid || entry.GetInt64(Attr.LockoutTime) is not { } lockoutTime)
            {
                continue;
            }

            var isLocked = ((entry.GetInt64(Attr.UserAccountControlComputed) ?? 0) & LockedBit) != 0;
            if (isLocked)
            {
                locked[guid] = lockoutTime;
            }
            else if (previous is not null && previous.TryGetValue(guid, out var before) && before == lockoutTime)
            {
                await EmitAsync(context, entry, lockoutTime, cancellationToken).ConfigureAwait(false);
            }
        }

        _locked = locked;
    }

    private async Task EmitAsync(ScanContext context, AdMng.Core.Model.DirectoryObject entry, long lockoutTime, CancellationToken cancellationToken)
    {
        var lockedAt = AdConvert.FromFileTime(lockoutTime) ?? context.Now;
        var policy = await _policies.ResolveAsync(context.Session, entry.DistinguishedName, cancellationToken).ConfigureAwait(false);
        var draft = new EventDraft
        {
            Kind = WatchEventKind.LockoutExpired,
            Lockout = new LockoutInfo { LockedAt = lockedAt, LockedUntil = policy.Duration is { } duration ? lockedAt + duration : null },
        };
        await _writer.AppendAsync(context, draft, ScanEventWriter.RefOf(entry), DetectionSource.LockoutMonitor, cancellationToken).ConfigureAwait(false);
    }
}
