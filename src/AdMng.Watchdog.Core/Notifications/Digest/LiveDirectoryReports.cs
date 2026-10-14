using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AdMng.Watchdog.Core.Notifications.Digest;

/// <summary>
/// <see cref="IDigestDirectoryReports"/> over the service's pinned LDAP session: the inactive-accounts filters of 10.17
/// (enabled, <c>lastLogonTimestamp</c> older than the threshold or never set, created before <c>MinAgeDays</c>) and the
/// hygiene counts (PASSWD_NOTREQD, DONT_EXPIRE_PASSWORD, unconstrained delegation outside DCs, adminCount orphans,
/// krbtgt / RID-500 password age). Privileged = a principal key of the stored PrivilegeState. Filters are built with
/// <see cref="LdapFilter"/>; nothing is written anywhere.
/// </summary>
public sealed partial class LiveDirectoryReports : IDigestDirectoryReports
{
    private const long AccountDisable = (long)UserAccountControl.AccountDisable;

    private static readonly string[] AccountAttributes =
        [Attr.ObjectGuid, Attr.ObjectSid, Attr.SamAccountName, Attr.Name, Attr.DisplayName, Attr.LastLogonTimestamp];

    private readonly IDirectorySessionProvider _sessions;
    private readonly IDetectionStore _detection;
    private readonly ILogger<LiveDirectoryReports> _logger;

    public LiveDirectoryReports(IDirectorySessionProvider sessions, IDetectionStore detection, ILogger<LiveDirectoryReports> logger)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(detection);
        ArgumentNullException.ThrowIfNull(logger);
        _sessions = sessions;
        _detection = detection;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<InactiveAccountsResult?> GetInactiveAccountsAsync(InactiveAccountsScan settings, DateTimeOffset now, int maxRows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return await RunAsync("inactive accounts report", async session =>
        {
            var privileged = await PrivilegedKeysAsync(cancellationToken).ConfigureAwait(false);
            var created = LdapFilter.LessOrEqual(Attr.WhenCreated, AdConvert.ToGeneralizedTime(now.AddDays(-Math.Max(0, settings.MinAgeDays))));
            var users = await InactiveAsync(session, UserFilter(), WatchObjectKind.User, settings.Days, created, now, privileged, cancellationToken).ConfigureAwait(false);
            var computers = await InactiveAsync(session, LdapFilter.Equal(Attr.ObjectClass, "computer"), WatchObjectKind.Computer, settings.Days, created, now,
                privileged, cancellationToken).ConfigureAwait(false);
            var privilegedInactive = await InactiveAsync(session, UserFilter(), WatchObjectKind.User, settings.PrivilegedDays, created, now, privileged,
                cancellationToken).ConfigureAwait(false);
            var stalePrivileged = privilegedInactive.Where(a => a.Privileged).ToList();
            var rows = stalePrivileged
                .Concat(users.Where(u => !u.Privileged))
                .Concat(computers)
                .OrderByDescending(a => a.Privileged)
                .ThenBy(a => a.LastLogon ?? DateTimeOffset.MinValue)
                .ThenBy(a => a.Account.Dn, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(0, maxRows))
                .ToList();
            // Privileged users count with their own (longer) threshold.
            var staleUsers = users.Count(u => !u.Privileged) + stalePrivileged.Count;
            return new InactiveAccountsResult { Users = staleUsers, Computers = computers.Count, Privileged = stalePrivileged.Count, Rows = rows };
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<HygieneCounters?> GetHygieneAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        await RunAsync("hygiene counters", async session =>
        {
            var enabled = LdapFilter.Not(LdapFilter.BitAnd(Attr.UserAccountControl, AccountDisable));
            var passwordNotRequired = await CountAsync(session,
                LdapFilter.And(UserFilter(), enabled, LdapFilter.BitAnd(Attr.UserAccountControl, (long)UserAccountControl.PasswordNotRequired)), cancellationToken)
                .ConfigureAwait(false);
            var neverExpires = await CountAsync(session,
                LdapFilter.And(UserFilter(), enabled, LdapFilter.BitAnd(Attr.UserAccountControl, (long)UserAccountControl.DontExpirePassword)), cancellationToken)
                .ConfigureAwait(false);
            var unconstrained = await CountAsync(session, LdapFilter.And(
                enabled,
                LdapFilter.BitAnd(Attr.UserAccountControl, (long)UserAccountControl.TrustedForDelegation),
                LdapFilter.Not(LdapFilter.BitAnd(Attr.UserAccountControl, (long)UserAccountControl.ServerTrustAccount))), cancellationToken).ConfigureAwait(false);

            var privileged = await PrivilegedKeysAsync(cancellationToken).ConfigureAwait(false);
            var orphans = 0;
            int? administratorAge = null;
            var search = new DirectorySearch
            {
                BaseDn = session.Info.DefaultNamingContext,
                Filter = LdapFilter.And(LdapFilter.Equal(Attr.AdminCount, 1), LdapFilter.Equal(Attr.ObjectClass, "user")),
                Attributes = [Attr.ObjectGuid, Attr.ObjectSid, Attr.PwdLastSet],
            };
            await foreach (var entry in session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
            {
                var rid = AdConvert.GetRid(entry.ObjectSid);
                if (rid == 500)
                {
                    administratorAge = PasswordAge(entry, now);
                }

                if (rid != 502 && !IsPrivileged(entry, privileged))
                {
                    orphans++;
                }
            }

            var krbtgt = await FirstAsync(session, LdapFilter.And(LdapFilter.Equal(Attr.ObjectClass, "user"), LdapFilter.Equal(Attr.SamAccountName, "krbtgt")),
                [Attr.PwdLastSet], cancellationToken).ConfigureAwait(false);
            return new HygieneCounters
            {
                PasswordNotRequired = passwordNotRequired,
                PasswordNeverExpires = neverExpires,
                UnconstrainedDelegation = unconstrained,
                AdminCountOrphans = orphans,
                KrbtgtPasswordAgeDays = krbtgt is null ? null : PasswordAge(krbtgt, now),
                AdministratorPasswordAgeDays = administratorAge,
            };
        }, cancellationToken).ConfigureAwait(false);

    private static string UserFilter() => LdapFilter.And(LdapFilter.Equal(Attr.ObjectCategory, "person"), LdapFilter.Equal(Attr.ObjectClass, "user"));

    private static async Task<List<InactiveAccount>> InactiveAsync(IDirectorySession session, string kindFilter, WatchObjectKind kind, int days, string created,
        DateTimeOffset now, IReadOnlySet<string> privileged, CancellationToken cancellationToken)
    {
        var cutoff = AdConvert.ToFileTime(now.AddDays(-Math.Max(1, days)));
        var search = new DirectorySearch
        {
            BaseDn = session.Info.DefaultNamingContext,
            Filter = LdapFilter.And(
                kindFilter,
                LdapFilter.Not(LdapFilter.BitAnd(Attr.UserAccountControl, AccountDisable)),
                LdapFilter.Or(LdapFilter.LessOrEqual(Attr.LastLogonTimestamp, cutoff), LdapFilter.NotPresent(Attr.LastLogonTimestamp)),
                created),
            Attributes = AccountAttributes,
        };
        var accounts = new List<InactiveAccount>();
        await foreach (var entry in session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
        {
            var lastLogon = entry.GetFileTime(Attr.LastLogonTimestamp);
            accounts.Add(new InactiveAccount(
                new ObjectRef
                {
                    ObjectGuid = entry.ObjectGuid,
                    Dn = entry.DistinguishedName,
                    Name = entry.GetString(Attr.Name),
                    DisplayName = entry.GetString(Attr.DisplayName),
                    SamAccountName = entry.GetString(Attr.SamAccountName),
                    Sid = entry.ObjectSid,
                    Kind = kind,
                },
                lastLogon is { } value && value.Year > 1601 ? value : null,
                IsPrivileged(entry, privileged)));
        }

        return accounts;
    }

    private static async Task<int> CountAsync(IDirectorySession session, string filter, CancellationToken cancellationToken)
    {
        var count = 0;
        var search = new DirectorySearch { BaseDn = session.Info.DefaultNamingContext, Filter = filter };
        await foreach (var _ in session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
        {
            count++;
        }

        return count;
    }

    private static async Task<DirectoryObject?> FirstAsync(IDirectorySession session, string filter, IReadOnlyList<string> attributes,
        CancellationToken cancellationToken)
    {
        var search = new DirectorySearch { BaseDn = session.Info.DefaultNamingContext, Filter = filter, Attributes = attributes, SizeLimit = 1 };
        await foreach (var entry in session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
        {
            return entry;
        }

        return null;
    }

    private static int? PasswordAge(DirectoryObject entry, DateTimeOffset now) =>
        entry.GetFileTime(Attr.PwdLastSet) is { } set && set.Year > 1601 ? (int)Math.Floor((now - set).TotalDays) : null;

    private static bool IsPrivileged(DirectoryObject entry, IReadOnlySet<string> privileged) =>
        (entry.ObjectGuid is { } guid && privileged.Contains(guid.ToString("N")))
        || (entry.ObjectSid is { } sid && privileged.Contains("sid:" + sid));

    private async Task<IReadOnlySet<string>> PrivilegedKeysAsync(CancellationToken cancellationToken)
    {
        var state = await _detection.GetPrivilegeStateAsync(cancellationToken).ConfigureAwait(false);
        return new HashSet<string>((state?.Principals ?? []).Select(p => p.PrincipalKey), StringComparer.OrdinalIgnoreCase);
    }

    private async Task<T?> RunAsync<T>(string operation, Func<IDirectorySession, Task<T>> query, CancellationToken cancellationToken)
        where T : class
    {
        IDirectorySession? session = null;
        try
        {
            session = await _sessions.GetSessionAsync(cancellationToken).ConfigureAwait(false);
            return await query(session).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            SafeExceptionLog.LogWarning(_logger, ex, operation);
            if (session is not null && ex is DirectoryException { Kind: DirectoryErrorKind.ServerUnavailable or DirectoryErrorKind.Timeout })
            {
                _sessions.ReportFailure(session, ex);
            }

            return null;
        }
    }
}
