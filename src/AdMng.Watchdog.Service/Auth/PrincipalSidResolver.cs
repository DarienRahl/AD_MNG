using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Diagnostics;

namespace AdMng.Watchdog.Service.Auth;

/// <summary>The outcome of a principal lookup (stage B design 6.2 step 3).</summary>
public enum PrincipalLookupStatus
{
    /// <summary>Exactly one user or computer with the name: <see cref="PrincipalLookup.UserSid"/> and its tokenGroups.</summary>
    Found,

    /// <summary>No account, or more than one, with the name (no role; cached as a negative entry).</summary>
    NotFound,

    /// <summary>LDAP did not answer (no session, transport error, timeout): never cached, never a role.</summary>
    Unavailable,
}

/// <summary>A principal's objectSid and the SIDs of its tokenGroups (primary group and nested groups included).</summary>
public sealed record PrincipalLookup(PrincipalLookupStatus Status, string? UserSid = null, IReadOnlyList<string>? GroupSids = null)
{
    public static readonly PrincipalLookup NotFound = new(PrincipalLookupStatus.NotFound);

    public static readonly PrincipalLookup Unavailable = new(PrincipalLookupStatus.Unavailable);
}

/// <summary>Looks up the SIDs of an API caller in the directory (Linux role resolution, stage B design 6.2).</summary>
public interface IPrincipalSidResolver
{
    /// <summary>Resolves <paramref name="samAccountName"/> (already validated against the sAMAccountName syntax).</summary>
    Task<PrincipalLookup> ResolveAsync(string samAccountName, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves an API caller over the service's own LDAP session (<see cref="IDirectorySessionProvider"/>, GSSAPI sign and
/// seal as the service account – CLAUDE.md rule 4): a subtree search of the domain NC for
/// <c>(&amp;(sAMAccountName=…)(|(objectCategory=person)(objectCategory=computer)))</c> that must find exactly one object,
/// then a base-scope read of the constructed attribute <c>tokenGroups</c> on it. The SIDs come only from AD, never from
/// the ticket or the client – the MIT acceptor exposes no PAC, and <c>NegotiateOptions.EnableLdap</c> (group names by
/// CN) is spoofable and never used. An unreadable <c>tokenGroups</c> yields no group SIDs (fail closed; the permission
/// self-check reports <c>permission.tokenGroupsUnreadable</c>).
/// </summary>
public sealed partial class PrincipalSidResolver(IDirectorySessionProvider sessions, ILogger<PrincipalSidResolver> logger) : IPrincipalSidResolver
{
    public async Task<PrincipalLookup> ResolveAsync(string samAccountName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(samAccountName);
        IDirectorySession session;
        try
        {
            session = await sessions.GetSessionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryException)
        {
            return PrincipalLookup.Unavailable;
        }

        try
        {
            var search = new DirectorySearch
            {
                BaseDn = session.Info.DefaultNamingContext,
                Filter = LdapFilter.And(
                    LdapFilter.Equal(Attr.SamAccountName, samAccountName),
                    LdapFilter.Or(LdapFilter.Equal(Attr.ObjectCategory, "person"), LdapFilter.Equal(Attr.ObjectCategory, "computer"))),
                Attributes = [Attr.ObjectSid],
                SizeLimit = 2,
                PageSize = 2,
            };
            var found = new List<DirectoryObject>(2);
            await foreach (var entry in session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
            {
                found.Add(entry);
                if (found.Count == 2)
                {
                    break;
                }
            }

            if (found.Count != 1 || found[0].ObjectSid is not { Length: > 0 } userSid)
            {
                return PrincipalLookup.NotFound;
            }

            var groups = await session.GetAsync(found[0].DistinguishedName, [Attr.TokenGroups], cancellationToken).ConfigureAwait(false);
            if (groups is null || !groups.TryGet(Attr.TokenGroups, out var values))
            {
                Log.TokenGroupsUnreadable(logger);
                return new PrincipalLookup(PrincipalLookupStatus.Found, userSid, []);
            }

            return new PrincipalLookup(
                PrincipalLookupStatus.Found,
                userSid,
                [.. values.OfType<byte[]>().Where(b => b.Length >= 8).Select(b => AdConvert.SidToString(b))]);
        }
        catch (DirectoryException ex)
        {
            if (ex.IsTransient)
            {
                sessions.ReportFailure(session, ex);
            }

            SafeExceptionLog.LogWarning(logger, ex, "role lookup", new EventId(RoleLookupFailedEventId));
            return PrincipalLookup.Unavailable;
        }
    }

    /// <summary>Event id of a failed role lookup (exception type only, never the caller's name).</summary>
    public const int RoleLookupFailedEventId = 2503;

    private static partial class Log
    {
        [LoggerMessage(EventId = 2504, Level = LogLevel.Warning,
            Message = "tokenGroups of an API caller is not readable by the service account: the caller gets no group SIDs (grant read as in the instructions, appendix A.5).")]
        public static partial void TokenGroupsUnreadable(ILogger logger);
    }
}
