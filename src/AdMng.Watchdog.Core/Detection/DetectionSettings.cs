using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Core.Schema;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Configuration;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// The configuration of one detection cycle resolved against the session: tokens expanded, DN lists parsed, effective
/// watched sets per kind, recorded kinds, privileged roots and the severity context.
/// </summary>
internal sealed class DetectionSettings
{
    private readonly Dictionary<(WatchObjectKind Kind, bool Critical), IReadOnlySet<string>> _watched = [];
    private readonly HashSet<WatchEventKind> _disabled;
    private readonly HashSet<WatchEventKind> _enabledOptional;

    public DetectionSettings(WatchdogConfiguration configuration, DirectoryFacts facts, ISchemaCatalog? schema, DetectionHostOptions? hostOptions = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(facts);
        Configuration = configuration;
        Facts = facts;
        Schema = schema;
        ExcludedSubtrees = ParseDns(Directory.ExcludedSubtrees);
        ServiceAccountSubtrees = ParseDns(Directory.ServiceAccountSubtrees);
        DisabledAccountsSubtrees = ParseDns(Directory.DisabledAccountsSubtrees);
        CriticalObjects = ParseDns(Directory.CriticalObjects);
        Privileged = new PrivilegedGroupSet(configuration.PrivilegedGroups, facts, hostOptions?.RoleGroupSids);
        Severity = SeverityContext.Create(configuration, facts);
        RequestAttributes = WatchedAttributes.RequestSet(Directory, schema);
        _disabled = [.. Directory.DisabledKinds];
        _enabledOptional = [.. Directory.EnabledOptionalKinds];
        if (configuration.Scans.PasswordSpray.Enabled)
        {
            _enabledOptional.Add(WatchEventKind.PasswordSprayingSuspected);
        }

        TimeZone = TimeZoneResolver.TryResolve(configuration.General.TimeZone, out var zone) ? zone : TimeZoneInfo.Utc;
    }

    public WatchdogConfiguration Configuration { get; }

    public DirectorySettings Directory => Configuration.Directory;

    public DirectoryFacts Facts { get; }

    public ISchemaCatalog? Schema { get; }

    public IReadOnlyList<DistinguishedName> ExcludedSubtrees { get; }

    public IReadOnlyList<DistinguishedName> ServiceAccountSubtrees { get; }

    public IReadOnlyList<DistinguishedName> DisabledAccountsSubtrees { get; }

    public IReadOnlyList<DistinguishedName> CriticalObjects { get; }

    public PrivilegedGroupSet Privileged { get; }

    public SeverityContext Severity { get; }

    /// <summary>Attributes of the change searches (identity + every kind's watched set + member).</summary>
    public IReadOnlyList<string> RequestAttributes { get; }

    public TimeZoneInfo TimeZone { get; }

    /// <summary>True for a configured critical object (its nTSecurityDescriptor is tracked).</summary>
    public bool IsCritical(string dn) => DistinguishedName.TryParse(dn, out var parsed) && CriticalObjects.Any(c => c == parsed);

    /// <summary>The effective diffed set of a kind (critical objects add nTSecurityDescriptor).</summary>
    public IReadOnlySet<string> Watched(WatchObjectKind kind, bool critical = false)
    {
        if (!_watched.TryGetValue((kind, critical), out var set))
        {
            var result = new HashSet<string>(WatchedAttributes.For(kind, Directory, Schema), StringComparer.OrdinalIgnoreCase);
            if (critical)
            {
                result.Add(Attr.NtSecurityDescriptor);
            }
            else if (kind != WatchObjectKind.PublicKeyInfrastructure)
            {
                result.Remove(Attr.NtSecurityDescriptor);
            }

            _watched[(kind, critical)] = set = result;
        }

        return set;
    }

    /// <summary>
    /// False for kinds in Directory.DisabledKinds and optional kinds that are not enabled; health kinds are always
    /// recorded.
    /// </summary>
    public bool IsRecorded(WatchEventKind kind)
    {
        var info = EventKindCatalog.Get(kind);
        if (info.IsHealth)
        {
            return true;
        }

        if (_disabled.Contains(kind))
        {
            return false;
        }

        return info.RecordedByDefault || _enabledOptional.Contains(kind);
    }

    private List<DistinguishedName> ParseDns(IEnumerable<string> values) =>
        values.Select(v => DetectionTokens.Expand(v, Facts.DomainDn, Facts.DomainSid, Facts.RootDomainSid))
            .Select(v => DistinguishedName.TryParse(v, out var dn) && !dn.IsRoot ? dn : null)
            .OfType<DistinguishedName>()
            .ToList();
}
