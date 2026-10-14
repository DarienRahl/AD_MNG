using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Recognizes Entra Connect / sync accounts (section 10.6.4): sAMAccountName <c>MSOL_*</c> or <c>Sync_*</c>, or a
/// description containing "Azure Active Directory Connect", "Microsoft Entra Connect" or "Azure AD Connect". Run at
/// baseline, reconciliation and the first cycle after start; their SIDs are kept in memory (tag syncAccount on
/// attribution) and offered as <c>tier0-security</c> exclusion suggestions – never applied automatically.
/// </summary>
public sealed class SyncAccountDetector
{
    /// <summary>The built-in rule the suggestions target.</summary>
    public const string SuggestedRuleId = "tier0-security";

    private static readonly string[] DescriptionMarkers = ["Azure Active Directory Connect", "Microsoft Entra Connect", "Azure AD Connect"];

    private readonly ExclusionSuggestionRegistry _suggestions;
    private readonly Lock _gate = new();
    private IReadOnlyList<SyncAccount> _accounts = [];

    public SyncAccountDetector(ExclusionSuggestionRegistry suggestions)
    {
        _suggestions = suggestions;
    }

    /// <summary>The accounts found by the last refresh.</summary>
    public IReadOnlyList<SyncAccount> Accounts
    {
        get
        {
            lock (_gate)
            {
                return _accounts;
            }
        }
    }

    /// <summary>True when the refresh has run at least once in this process.</summary>
    public bool HasRun { get; private set; }

    /// <summary>True when <paramref name="sid"/> belongs to a detected sync account.</summary>
    public bool IsSyncAccount(string? sid) => sid is not null && Accounts.Any(a => string.Equals(a.Sid, sid, StringComparison.OrdinalIgnoreCase));

    /// <summary>The naming rule of sync accounts.</summary>
    public static bool IsSyncAccount(string? samAccountName, IEnumerable<string>? descriptions)
    {
        if (samAccountName is not null && (samAccountName.StartsWith("MSOL_", StringComparison.OrdinalIgnoreCase)
            || samAccountName.StartsWith("Sync_", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return descriptions?.Any(d => DescriptionMarkers.Any(m => d.Contains(m, StringComparison.OrdinalIgnoreCase))) == true;
    }

    /// <summary>Searches the domain for sync accounts and updates the suggestions.</summary>
    public async Task<IReadOnlyList<SyncAccount>> RefreshAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var filter = LdapFilter.And(
            LdapFilter.Equal(Attr.ObjectCategory, WatchdogObjectClasses.Person),
            LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.User),
            LdapFilter.Or(
            [
                LdapFilter.StartsWith(Attr.SamAccountName, "MSOL_"),
                LdapFilter.StartsWith(Attr.SamAccountName, "Sync_"),
                .. DescriptionMarkers.Select(m => LdapFilter.Contains(Attr.Description, m)),
            ]));
        var found = new List<SyncAccount>();
        await foreach (var entry in session.SearchAsync(new DirectorySearch
        {
            BaseDn = session.Info.DefaultNamingContext,
            Filter = filter,
            Attributes = [Attr.ObjectSid, Attr.SamAccountName, Attr.Description],
            PageSize = 1000,
        }, cancellationToken).ConfigureAwait(false))
        {
            if (entry.ObjectSid is { } sid && IsSyncAccount(entry.GetString(Attr.SamAccountName), entry.GetStrings(Attr.Description)))
            {
                found.Add(new SyncAccount(sid, entry.GetString(Attr.SamAccountName), entry.DistinguishedName));
            }
        }

        lock (_gate)
        {
            _accounts = found;
            HasRun = true;
        }

        var domain = session.Info.DomainNetBiosName;
        _suggestions.Replace(ExclusionSuggestionRegistry.EntraConnectSync, found.Select(a => new ExclusionSuggestionDto
        {
            RuleId = SuggestedRuleId,
            ActorSid = a.Sid,
            ActorAccount = a.SamAccountName is null ? null : domain is null ? a.SamAccountName : $"{domain}\\{a.SamAccountName}",
            ReasonCode = ExclusionSuggestionRegistry.EntraConnectSync,
        }));
        return found;
    }
}
