using System.Security.Cryptography;
using System.Text;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Cleanup;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy.Cleanup;

/// <summary>
/// Builds a <see cref="GpoSettingsIndex"/> by hand for the cleanup analyzer tests (docs/dev/k-computers-and-gpo.md
/// 21.10, W3): GPO records, unit records and links written directly – no SYSVOL, no unit builder. The containers are the
/// domain head, OU=Firma with OU=Księgowość and OU=Kadry below it, OU=Serwery and the site Warszawa.
/// </summary>
internal sealed class CleanupIndex
{
    public const string Domain = "DC=contoso,DC=test";
    public const string Firma = "OU=Firma," + Domain;
    public const string Ksiegowosc = "OU=Księgowość," + Firma;
    public const string Kadry = "OU=Kadry," + Firma;
    public const string Serwery = "OU=Serwery," + Domain;
    public const string Warszawa = "CN=Warszawa,CN=Sites,CN=Configuration," + Domain;

    /// <summary>A group for security filtering (GG_Księgowość).</summary>
    public const string AccountingGroupSid = "S-1-5-21-1000-2000-3000-1201";

    private const string DomainAdminsSid = "S-1-5-21-1000-2000-3000-512";

    private readonly List<Draft> _gpos = [];
    private readonly Dictionary<string, List<(Guid Gpo, int Options, string? GpoDomain)>> _links = new(StringComparer.OrdinalIgnoreCase)
    {
        [Domain] = [],
        [Firma] = [],
        [Ksiegowosc] = [],
        [Kadry] = [],
        [Serwery] = [],
        [Warszawa] = [],
    };

    private readonly HashSet<string> _blocking = new(StringComparer.OrdinalIgnoreCase);

    public List<string> OtherDomains { get; } = [];

    /// <summary>A GPO (status Read, all settings enabled, no units unless given); its GUID is derived from the name.</summary>
    public Draft Gpo(string name, params GpoSettingUnit[] units)
    {
        var draft = new Draft(name, IdOf(name)) { Units = [.. units] };
        _gpos.Add(draft);
        return draft;
    }

    /// <summary>The GUID of a GPO of this index (derived from the name).</summary>
    public static Guid IdOf(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes("cleanup:" + name)).AsSpan(0, 16));

    /// <summary>A default GPO (Default Domain Policy GUID).</summary>
    public Draft DefaultDomainPolicy(params GpoSettingUnit[] units)
    {
        var draft = new Draft("Default Domain Policy", GpoInfo.DefaultDomainPolicyId) { Units = [.. units] };
        _gpos.Add(draft);
        return draft;
    }

    /// <summary>Links a GPO; within a container the first link added has link order 1 (the highest precedence).</summary>
    public CleanupIndex Link(string container, Draft gpo, bool enforced = false, bool disabled = false)
    {
        _links[container].Add((gpo.Id, (enforced ? GroupPolicyLink.EnforcedOption : 0) | (disabled ? GroupPolicyLink.DisabledOption : 0), null));
        return this;
    }

    /// <summary>Links a GPO of another domain of the forest (it has no entry in this index).</summary>
    public CleanupIndex LinkForeign(string container, Guid gpoId, string gpoDomainDn)
    {
        _links[container].Add((gpoId, 0, gpoDomainDn));
        return this;
    }

    public CleanupIndex BlockInheritance(string container)
    {
        _blocking.Add(container);
        return this;
    }

    public GpoSettingsIndex Build()
    {
        var containers = _links.Select(c =>
        {
            var kind = c.Key == Domain ? GroupPolicySomKind.Domain : c.Key == Warszawa ? GroupPolicySomKind.Site : GroupPolicySomKind.OrganizationalUnit;

            // The gPLink value lists the lowest precedence first: link order 1 is the last entry.
            var raw = string.Concat(Enumerable.Reverse(c.Value).Select(l => GpLinkValue.Format(l.Gpo, l.GpoDomain ?? Domain, l.Options)));
            var value = raw.Length > 0 ? raw : null;
            return new GroupPolicyContainerInfo(c.Key, kind, c.Key.Split(',')[0][3..], GpLinkValue.Parse(value, Domain), value,
                _blocking.Contains(c.Key) ? "1" : null);
        }).ToList();
        var links = GroupPolicyReader.LinksByGpo(containers);
        return new GpoSettingsIndex
        {
            Server = "dc01.contoso.test",
            DomainDn = Domain,
            DomainDnsName = "contoso.test",
            DomainSid = "S-1-5-21-1000-2000-3000",
            SysvolRead = true,
            Containers = containers,
            OtherDomains = [.. OtherDomains],
            Entries = [.. _gpos.Select(g => g.ToEntry(links.TryGetValue(g.Id, out var l) ? l : []))],
        };
    }

    /// <summary>Builds the index and analyzes it.</summary>
    public GpoCleanupResult Analyze(GpoCleanupOptions? options = null) => GpoCleanupAnalyzer.Analyze(Build(), options);

    /// <summary>A security descriptor whose Apply Group Policy right is allowed to <paramref name="allowed"/> (and denied to <paramref name="denied"/>).</summary>
    public static byte[] Filter(string[] allowed, params string[] denied) => FakeSecurityDescriptor.Build(DomainAdminsSid, DomainAdminsSid,
    [
        new FakeAce(FakeAce.AccessAllowed, 0x02, 0x000F00FF, DomainAdminsSid),
        .. denied.Select(s => new FakeAce(FakeAce.AccessDeniedObject, 0x02, 0x100, s, GroupPolicySecurityFilter.ApplyGroupPolicyRight)),
        .. allowed.Select(s => new FakeAce(FakeAce.AccessAllowed, 0x02, 0x00020094, s)),
        .. allowed.Select(s => new FakeAce(FakeAce.AccessAllowedObject, 0x02, 0x100, s, GroupPolicySecurityFilter.ApplyGroupPolicyRight)),
    ]);

    /// <summary>The default filter: Authenticated Users.</summary>
    public static byte[] AuthenticatedUsers => Filter([GroupPolicySecurityFilter.AuthenticatedUsersSid]);

    /// <summary>A GPO of the hand-built index.</summary>
    internal sealed class Draft(string name, Guid id)
    {
        public string Name { get; } = name;

        public Guid Id { get; } = id;

        public GpoIndexStatus Status { get; set; } = GpoIndexStatus.Read;

        public GpoStatus GpoStatus { get; set; } = GpoStatus.AllSettingsEnabled;

        public List<GpoSettingUnit> Units { get; set; } = [];

        public List<string> OtherFiles { get; } = [];

        public int DirectorySettings { get; set; }

        /// <summary>The GPO's descriptor (default: Authenticated Users may apply it; null: not readable, filter unknown).</summary>
        public byte[]? SecurityDescriptor { get; set; } = AuthenticatedUsers;

        public string? WmiFilterId { get; set; }

        public GpoIndexEntry ToEntry(IReadOnlyList<GpoLinkInfo> links) => new()
        {
            Gpo = new GpoInfo
            {
                Id = Id,
                Dn = GpLinkValue.GpoDn(Id, Domain),
                DisplayName = Name,
                Flags = (int)GpoStatus,
                SecurityDescriptor = SecurityDescriptor,
                WmiFilterLink = WmiFilterId is null ? null : WmiFilterLinks.Format("contoso.test", WmiFilterId),
            },
            Status = Status,
            Links = links,
            Units = Units,
            OtherFiles = OtherFiles,
            DirectorySettings = DirectorySettings,
        };
    }
}

/// <summary>Unit records for the hand-built index (the grammar of 21.2, values as fingerprints).</summary>
internal static class Units
{
    public const string MinimumPasswordLength = "K|Computer/Security/System Access/MinimumPasswordLength";
    public const string MaxTicketAge = "K|Computer/Security/Kerberos Policy/MaxTicketAge";
    public const string RemoteDesktopRight = "K|Computer/Security/Privilege Rights/SeRemoteInteractiveLogonRight";
    public const string DriveK = @"I|User|Drives|Drive|K";
    public const string Wallpaper = @"K|User/Registry/Software\Microsoft\Windows\CurrentVersion\Policies\System!Wallpaper";
    public const string NoPreview = "P|Machine|" + SampleAdministrativeTemplates.HidePreview;
    public const string LogonAudit = "A|{0CCE9215-69AE-11D9-BED3-505054503030}";

    /// <summary>A plain comparable unit (security value unless another kind is given).</summary>
    public static GpoSettingUnit Value(string baseId, string value, GpoScope scope = GpoScope.Computer,
        GpoSettingUnitKind kind = GpoSettingUnitKind.SecurityValue) => new()
        {
            Id = baseId,
            BaseId = baseId,
            Kind = kind,
            Scope = scope,
            ValueFingerprint = value,
        };

    /// <summary>The drive K: as a preference item (user part) with the given path.</summary>
    public static GpoSettingUnit Drive(string path, string uid) => Value(DriveK, "letter=K\npath=" + path, GpoScope.User, GpoSettingUnitKind.Preference) with
    {
        PreferenceType = "Drives",
        ItemId = uid,
    };

    /// <summary>A Registry.pol value of the user part.</summary>
    public static GpoSettingUnit Registry(string value) => Value(Wallpaper, "=REG_SZ:" + value, GpoScope.User, GpoSettingUnitKind.RegistryValue);

    /// <summary>The policy "Wyłącz podgląd" (machine) with a fresh value dictionary each time.</summary>
    public static GpoSettingUnit Policy(PolicyState state) => new()
    {
        Id = NoPreview,
        BaseId = NoPreview,
        Kind = GpoSettingUnitKind.AdministrativeTemplate,
        Scope = GpoScope.Computer,
        PolicyId = SampleAdministrativeTemplates.HidePreview,
        Policy = state == PolicyState.Enabled ? PolicySetting.Enabled(new Dictionary<string, PolicyElementValue>()) : new PolicySetting(state, new Dictionary<string, PolicyElementValue>()),
    };

    /// <summary>The GPO comment (not a setting).</summary>
    public static GpoSettingUnit Comment() => Value("K|GPO/Comment", "Porządki – opis", GpoScope.Gpo, GpoSettingUnitKind.Comment) with { Refusal = GpoUnitRefusal.Comment };

    /// <summary>A script payload (not a setting).</summary>
    public static GpoSettingUnit ScriptFile() => Value("K|Machine/Scripts/Startup/logowanie.cmd", "sha256:ab", GpoScope.Computer, GpoSettingUnitKind.ScriptFile) with
    {
        Refusal = GpoUnitRefusal.Script,
    };

    /// <summary>A startup script (cumulative, refused).</summary>
    public static GpoSettingUnit Script(string commandLine = "logowanie.cmd") => Value(
        "S|Computer|cmd|Startup|" + commandLine + "|", string.Empty, GpoScope.Computer, GpoSettingUnitKind.Script) with
    {
        Comparability = GpoUnitComparability.Cumulative,
        Refusal = GpoUnitRefusal.Script,
    };

    /// <summary>The unreadable-file marker.</summary>
    public static GpoSettingUnit Unreadable() => Value("K|File/Machine/Registry.pol", string.Empty, GpoScope.Computer, GpoSettingUnitKind.UnreadableFile) with
    {
        Comparability = GpoUnitComparability.Unreadable,
        Refusal = GpoUnitRefusal.Unreadable,
    };

    /// <summary>Copies a unit as its n-th occurrence in a GPO.</summary>
    public static GpoSettingUnit Second(GpoSettingUnit unit) => unit with { Id = unit.BaseId + "#2", Occurrence = 2 };
}
