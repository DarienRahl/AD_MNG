using System.Security.Claims;
using System.Text.RegularExpressions;
using AdMng.Watchdog.Service.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Auth;

/// <summary>
/// Linux role resolution (stage B design 6.2): the MIT acceptor behind Kestrel's Negotiate handler yields only the client
/// principal <c>name@REALM</c> – no SIDs (no PAC access) – so every Kerberos identity gets its SID claims from the
/// directory: the realm must equal <c>Watchdog:Kerberos:Realm</c> and the name must have sAMAccountName syntax (otherwise
/// no SIDs, log 2502 with the realm only); the lookup goes through <see cref="RoleCache"/> and
/// <see cref="IPrincipalSidResolver"/> (objectSid + tokenGroups over the service's LDAP session). Added claims:
/// <see cref="ClaimTypes.PrimarySid"/>, one <see cref="ClaimTypes.GroupSid"/> per tokenGroups SID and
/// <see cref="SidSourceClaim"/>; SID claims of any other origin are removed. Without LDAP and without a usable cached
/// entry the identity carries <c>admng:sidSource = unavailable</c> and no SIDs: <see cref="SidRoleHandler"/> then fails
/// with <see cref="RoleLookupUnavailableReason"/> and the API answers 503 (<see cref="WatchdogAuthorizationResultHandler"/>).
/// Idempotent: an identity that already carries <see cref="SidSourceClaim"/> is returned unchanged (ASP.NET Core may run
/// the transformation more than once per request). Registered only on Linux with Negotiate on Kestrel; on Windows the
/// identity is a WindowsIdentity with the SIDs of its token.
/// </summary>
public sealed partial class KerberosSidClaimsTransformation(
    IOptionsMonitor<ServiceOptions> options,
    IPrincipalSidResolver resolver,
    RoleCache cache,
    ILogger<KerberosSidClaimsTransformation> logger) : IClaimsTransformation
{
    /// <summary>Where the SID claims came from: <see cref="LdapSource"/>, <see cref="NoneSource"/> or <see cref="UnavailableSource"/>.</summary>
    public const string SidSourceClaim = "admng:sidSource";

    public const string LdapSource = "ldap";

    /// <summary>No SIDs: foreign realm, a name outside the sAMAccountName syntax, or no (unique) account in AD.</summary>
    public const string NoneSource = "none";

    /// <summary>No SIDs because LDAP is unavailable and nothing usable is cached (→ 503).</summary>
    public const string UnavailableSource = "unavailable";

    /// <summary>Upper bound of one directory lookup (search + tokenGroups read) inside a request.</summary>
    public static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(15);

    private static readonly HashSet<string> SidClaimTypes = new(StringComparer.Ordinal)
    {
        ClaimTypes.PrimarySid, ClaimTypes.Sid, ClaimTypes.GroupSid, ClaimTypes.PrimaryGroupSid, ClaimTypes.DenyOnlySid,
        ClaimTypes.DenyOnlyPrimarySid, ClaimTypes.DenyOnlyPrimaryGroupSid, ClaimTypes.WindowsDeviceGroup, ClaimTypes.DenyOnlyWindowsDeviceGroup,
        SidSourceClaim,
    };

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity
            || !string.Equals(identity.AuthenticationType, KerberosOnlyEvents.Kerberos, StringComparison.OrdinalIgnoreCase)
            || principal.HasClaim(c => c.Type == SidSourceClaim))
        {
            return principal;
        }

        var (source, lookup) = await LookupAsync(identity.Name).ConfigureAwait(false);
        var transformed = principal.Clone();
        foreach (var claimsIdentity in transformed.Identities)
        {
            foreach (var claim in claimsIdentity.Claims.Where(c => SidClaimTypes.Contains(c.Type)).ToList())
            {
                claimsIdentity.TryRemoveClaim(claim);
            }
        }

        var target = (ClaimsIdentity)transformed.Identity!;
        target.AddClaim(new Claim(SidSourceClaim, source));
        if (lookup is { Status: PrincipalLookupStatus.Found, UserSid: { } userSid })
        {
            target.AddClaim(new Claim(ClaimTypes.PrimarySid, userSid));
            foreach (var group in lookup.GroupSids ?? [])
            {
                target.AddClaim(new Claim(ClaimTypes.GroupSid, group));
            }
        }

        return transformed;
    }

    /// <summary>Splits <c>name@REALM</c> at the last '@' and checks realm and name (6.2 step 1). <paramref name="key"/> is
    /// the cache key (name lower-case, realm upper-case); <paramref name="callerRealm"/> is set whenever a realm was given.</summary>
    public static bool TryParsePrincipal(string? principal, string? realm, out string samAccountName, out string key, out string? callerRealm)
    {
        samAccountName = string.Empty;
        key = string.Empty;
        callerRealm = null;
        var at = principal?.LastIndexOf('@') ?? -1;
        if (principal is null || at <= 0 || at == principal.Length - 1)
        {
            return false;
        }

        callerRealm = principal[(at + 1)..];
        var name = principal[..at];
        if (string.IsNullOrWhiteSpace(realm) || !string.Equals(callerRealm, realm.Trim(), StringComparison.OrdinalIgnoreCase) || !SamAccountNamePattern().IsMatch(name))
        {
            return false;
        }

        samAccountName = name;
        key = name.ToLowerInvariant() + "@" + realm.Trim().ToUpperInvariant();
        return true;
    }

    private async Task<(string Source, PrincipalLookup? Lookup)> LookupAsync(string? principal)
    {
        if (!TryParsePrincipal(principal, options.CurrentValue.Kerberos.Realm, out var samAccountName, out var key, out var callerRealm))
        {
            Log.PrincipalRejected(logger, callerRealm ?? "(none)");
            return (NoneSource, null);
        }

        if (cache.TryGet(key, allowStale: false, out var cached))
        {
            return (SourceOf(cached), cached);
        }

        PrincipalLookup lookup;
        using (var timeout = new CancellationTokenSource(LookupTimeout))
        {
            try
            {
                lookup = await resolver.ResolveAsync(samAccountName, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lookup = PrincipalLookup.Unavailable;
            }
        }

        if (lookup.Status != PrincipalLookupStatus.Unavailable)
        {
            cache.Store(key, lookup);
            return (SourceOf(lookup), lookup);
        }

        if (cache.TryGet(key, allowStale: true, out var stale))
        {
            Log.StaleEntryUsed(logger);
            return (SourceOf(stale), stale);
        }

        Log.LookupUnavailable(logger);
        return (UnavailableSource, null);
    }

    private static string SourceOf(PrincipalLookup lookup) => lookup.Status == PrincipalLookupStatus.Found ? LdapSource : NoneSource;

    /// <summary>sAMAccountName syntax of design 6.2 step 1 (control characters excluded as well).</summary>
    [GeneratedRegex("""^[^@\\/"\[\]:;|=+*?<>,\x00-\x1f]{1,20}\$?$""", RegexOptions.CultureInvariant)]
    private static partial Regex SamAccountNamePattern();

    private static partial class Log
    {
        [LoggerMessage(EventId = 2502, Level = LogLevel.Warning,
            Message = "A Kerberos caller of realm {Realm} gets no role: only principals name@<Watchdog:Kerberos:Realm> with a sAMAccountName are resolved.")]
        public static partial void PrincipalRejected(ILogger logger, string realm);

        [LoggerMessage(EventId = 2505, Level = LogLevel.Warning,
            Message = "Role lookup: LDAP is unavailable, a cached entry within Authorization.RoleCacheMaxStaleSeconds is used.")]
        public static partial void StaleEntryUsed(ILogger logger);

        [LoggerMessage(EventId = 2506, Level = LogLevel.Warning,
            Message = "Role lookup: LDAP is unavailable and nothing usable is cached; the request is answered with 503.")]
        public static partial void LookupUnavailable(ILogger logger);
    }
}
