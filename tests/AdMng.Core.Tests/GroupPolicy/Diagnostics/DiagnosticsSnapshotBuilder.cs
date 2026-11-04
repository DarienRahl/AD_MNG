using System.Security.Cryptography;
using System.Text;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Diagnostics;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Security;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy.Diagnostics;

/// <summary>
/// Builds pure diagnostics snapshots for the rule tests (no directory): a domain contoso.test with Polish OUs, GPOs with the
/// default DACL, links in gPLink order (the last link has link order 1), folders, counts and resolved SIDs.
/// </summary>
internal sealed class DiagnosticsSnapshotBuilder
{
    public const string DomainDn = "DC=contoso,DC=test";
    public const string DomainDnsName = "contoso.test";
    public const string DomainSid = "S-1-5-21-1004336348-1177238915-682003330";
    public const string DomainAdmins = DomainSid + "-512";
    public const string DomainUsers = DomainSid + "-513";
    public const string DomainComputers = DomainSid + "-515";
    public const string EnterpriseAdmins = DomainSid + "-519";
    public const string GroupSerwisanci = DomainSid + "-1105";
    public const string GroupKsiegowosc = DomainSid + "-1106";
    public const string Deleted = DomainSid + "-1999";

    private readonly List<GpoInfo> _gpos = [];
    private readonly Dictionary<string, (GroupPolicySomKind Kind, string Name, List<string> Entries, bool Blocks)> _containers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WmiFilterInfo> _wmiFilters = [];
    private readonly Dictionary<Guid, GpoFolderState> _folders = [];
    private Dictionary<string, GpoObjectCount>? _counts;
    private HashSet<string>? _existing;

    public DiagnosticsSnapshotBuilder()
    {
        _containers[DomainDn] = (GroupPolicySomKind.Domain, DomainDnsName, [], false);
    }

    /// <summary>An OU by its path from the domain head down: <c>Ou("Firma", "Komputery")</c> = <c>OU=Komputery,OU=Firma,DC=…</c>.</summary>
    public static string Ou(params string[] path) =>
        string.Join(",", path.Reverse().Select(p => "OU=" + p)) + "," + DomainDn;

    public static string DomainControllersOu => Ou("Domain Controllers");

    public static string SiteDn(string name) => $"CN={name},CN=Sites,CN=Configuration,{DomainDn}";

    public GpoDiagnosticsDepth Depth { get; set; } = GpoDiagnosticsDepth.Full;

    public Guid? Scope { get; set; }

    public bool SysvolVersionsRead { get; set; } = true;

    public bool FoldersRead { get; set; } = true;

    /// <summary>GPOs a gPLink names that exist but the session may not read (<see cref="GpoDiagnosticsSnapshot.InaccessibleGpoIds"/>).</summary>
    public HashSet<Guid> Inaccessible { get; } = [];

    /// <summary>The default DACL GPMC gives a GPO (Authenticated Users read + apply).</summary>
    public static byte[] DefaultDescriptor(params FakeAce[] extra) => FakeSecurityDescriptor.Build(DomainAdmins, DomainAdmins,
    [
        new FakeAce(FakeAce.AccessAllowed, 0x02, 0x000F00FF, DomainAdmins),
        new FakeAce(FakeAce.AccessAllowed, 0x02, 0x000F00FF, EnterpriseAdmins),
        new FakeAce(FakeAce.AccessAllowed, 0x0A, 0x000F00FF, "S-1-3-0"),
        new FakeAce(FakeAce.AccessAllowed, 0x02, 0x00020094, "S-1-5-11"),
        new FakeAce(FakeAce.AccessAllowedObject, 0x02, 0x00000100, "S-1-5-11", GroupPolicySecurityFilter.ApplyGroupPolicyRight),
        new FakeAce(FakeAce.AccessAllowed, 0x02, 0x000F00FF, "S-1-5-18"),
        new FakeAce(FakeAce.AccessAllowed, 0x02, 0x00020094, "S-1-5-9"),
        .. extra,
    ]);

    /// <summary>A descriptor with exactly these ACEs (owner Domain Admins).</summary>
    public static byte[] Descriptor(params FakeAce[] aces) => FakeSecurityDescriptor.Build(DomainAdmins, DomainAdmins, aces);

    /// <summary>A GPO; the GPT.INI version is the AD version unless given (null: not read).</summary>
    public Guid Gpo(
        string name,
        int user = 0,
        int computer = 1,
        GpoStatus status = GpoStatus.AllSettingsEnabled,
        byte[]? descriptor = null,
        string? wmiFilterId = null,
        string? machineExtensions = null,
        string? userExtensions = null,
        (int User, int Computer)? gptVersion = null,
        SysvolFileStatus? gptStatus = null,
        string? fileSysPath = null,
        Guid? id = null)
    {
        var guid = id ?? new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("diag:" + name)).AsSpan(0, 16));
        var directory = new GpoVersion(user, computer);
        _gpos.Add(new GpoInfo
        {
            Id = guid,
            Dn = GpLinkValue.GpoDn(guid, DomainDn),
            DisplayName = name,
            DirectoryVersion = directory,
            SysvolVersion = gptStatus is null ? gptVersion is { } g ? new GpoVersion(g.User, g.Computer) : directory : null,
            SysvolStatus = gptStatus,
            Flags = (int)status,
            SecurityDescriptor = descriptor ?? DefaultDescriptor(),
            WmiFilterLink = wmiFilterId is null ? null : WmiFilterLinks.Format(DomainDnsName, wmiFilterId),
            MachineExtensionNames = machineExtensions,
            UserExtensionNames = userExtensions,
            FileSysPath = fileSysPath ?? $@"\\{DomainDnsName}\SysVol\{DomainDnsName}\Policies\{SysvolPaths.FolderName(guid)}",
        });
        return guid;
    }

    /// <summary>An OU (or site) with no links (e.g. one that only blocks inheritance).</summary>
    public DiagnosticsSnapshotBuilder Container(string dn, bool blocksInheritance = false, GroupPolicySomKind kind = GroupPolicySomKind.OrganizationalUnit)
    {
        if (_containers.TryGetValue(dn, out var existing))
        {
            _containers[dn] = (existing.Kind, existing.Name, existing.Entries, blocksInheritance);
        }
        else
        {
            _containers[dn] = (kind, dn.Split(',')[0].Split('=')[1], [], blocksInheritance);
        }

        return this;
    }

    /// <summary>Appends a link to the container's gPLink (the last appended has link order 1).</summary>
    public DiagnosticsSnapshotBuilder Link(string containerDn, Guid gpo, bool enabled = true, bool enforced = false)
    {
        var options = (enabled ? 0 : GroupPolicyLink.DisabledOption) | (enforced ? GroupPolicyLink.EnforcedOption : 0);
        return Raw(containerDn, GpLinkValue.Format(gpo, DomainDn, options));
    }

    /// <summary>Appends raw gPLink text (foreign or malformed entries).</summary>
    public DiagnosticsSnapshotBuilder Raw(string containerDn, string entry)
    {
        if (!_containers.ContainsKey(containerDn))
        {
            var kind = containerDn.StartsWith("CN=", StringComparison.OrdinalIgnoreCase) && containerDn.Contains("CN=Sites", StringComparison.OrdinalIgnoreCase)
                ? GroupPolicySomKind.Site
                : GroupPolicySomKind.OrganizationalUnit;
            Container(containerDn, kind: kind);
        }

        _containers[containerDn].Entries.Add(entry);
        return this;
    }

    public DiagnosticsSnapshotBuilder WmiFilter(string id, string name)
    {
        _wmiFilters.Add(new WmiFilterInfo { Dn = $"CN={id},CN=SOM,CN=WMIPolicy,CN=System,{DomainDn}", Id = id, Name = name });
        return this;
    }

    /// <summary>A folder that was read (GPT.INI added), with its ACL mapped from the GPO's DACL unless given.</summary>
    public DiagnosticsSnapshotBuilder Folder(Guid gpo, IEnumerable<(string Path, byte[] Content)> files, byte[]? folderSecurity = null, IReadOnlyList<string>? leftovers = null)
    {
        var info = _gpos.Single(g => g.Id == gpo);
        var all = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [GptIni.RelativePath] = Encoding.ASCII.GetBytes($"[General]\r\nVersion={info.DirectoryVersion!.Value.ToInt32()}\r\n"),
        };
        foreach (var (path, content) in files)
        {
            all[path] = content;
        }

        var entries = all.Select(f => new SysvolEntry(f.Key, false, f.Value.Length, DateTimeOffset.UnixEpoch)).ToList();
        var folder = new GpoFolderContent(entries, all) { Leftovers = leftovers ?? [] };
        _folders[gpo] = GpoFolderState.Of(folder, folderSecurity ?? GpoFolderAcl.FromDirectoryDescriptor(info.SecurityDescriptor), securityRead: true);
        return this;
    }

    public DiagnosticsSnapshotBuilder Folder(Guid gpo, params (string Path, byte[] Content)[] files) => Folder(gpo, files, null, null);

    public DiagnosticsSnapshotBuilder FolderState(Guid gpo, GpoFolderReadStatus status)
    {
        _folders[gpo] = new GpoFolderState { Status = status };
        return this;
    }

    /// <summary>Users and computers directly below a container.</summary>
    public DiagnosticsSnapshotBuilder Objects(string parentDn, int users, int computers)
    {
        _counts ??= new Dictionary<string, GpoObjectCount>(StringComparer.OrdinalIgnoreCase);
        _counts[parentDn] = new GpoObjectCount(users, computers);
        return this;
    }

    /// <summary>The domain SIDs that resolve (others with a RID ≥ 1000 are unresolved).</summary>
    public DiagnosticsSnapshotBuilder Existing(params string[] sids)
    {
        _existing = new HashSet<string>(sids, StringComparer.OrdinalIgnoreCase);
        return this;
    }

    public GpoDiagnosticsSnapshot Build() => new()
    {
        Depth = Depth,
        GpoId = Scope,
        DomainDn = DomainDn,
        DomainDnsName = DomainDnsName,
        DomainSid = DomainSid,
        Gpos = _gpos,
        Containers = [.. _containers.Select(c =>
        {
            var raw = c.Value.Entries.Count == 0 ? null : string.Concat(c.Value.Entries);
            return new GroupPolicyContainerInfo(c.Key, c.Value.Kind, c.Value.Name, GpLinkValue.Parse(raw, DomainDn), raw, c.Value.Blocks ? "1" : null);
        })],
        WmiFilters = _wmiFilters,
        InaccessibleGpoIds = Inaccessible,
        SysvolVersionsRead = SysvolVersionsRead,
        FoldersRead = FoldersRead,
        Folders = _folders,
        ObjectCounts = Depth == GpoDiagnosticsDepth.Full ? _counts ?? new Dictionary<string, GpoObjectCount>() : null,
        ExistingSids = Depth == GpoDiagnosticsDepth.Full ? _existing ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null,
    };

    public GpoDiagnosticsResult Analyze() => GroupPolicyDiagnostics.Analyze(Build());

    // ---- File contents --------------------------------------------------------------------------------------------------

    /// <summary><c>GptTmpl.inf</c> with the usual header and the lines.</summary>
    public static (string, byte[]) Template(params string[] lines) =>
        (SecurityTemplate.RelativePath, FakeSysvolFileReader.Utf16(FakeDirectoryBuilder.Template(lines)));

    /// <summary><c>Groups.xml</c> with the items (<see cref="FakeDirectoryBuilder.LocalGroup"/>).</summary>
    public static (string, byte[]) Groups(params string[] items) =>
        (GppGroupsParser.RelativePath, Encoding.UTF8.GetBytes(FakeDirectoryBuilder.Groups(items)));

    /// <summary><c>audit.csv</c> with one subcategory (Credential Validation, success and failure).</summary>
    public static (string, byte[]) AuditCsv()
    {
        var document = AuditPolicyDocument.New();
        var subcategory = SecuritySettingsCatalog.AuditSubcategories[0];
        document.SetSubcategory(subcategory.Guid, subcategory.CsvName, 3);
        return (AuditPolicyDocument.RelativePath, document.ToBytes());
    }

    /// <summary>Registry.pol of a half with DWORD values (key, name, data).</summary>
    public static (string, byte[]) RegistryPol(bool user, params (string Key, string Name, uint Value)[] values) =>
        (user ? GroupPolicyPlanner.UserRegistryPolicyPath : GroupPolicyPlanner.MachineRegistryPolicyPath,
            RegistryPolFile.Write(values.Select(v => new RegistryPolEntry(v.Key, v.Name, RegistryFormat.RegDword, RegistryPolFile.DwordData(v.Value)))));

    /// <summary>A preference file of the user half with one drive item.</summary>
    public static (string, byte[]) Drives(string uid, string path) => ("User/Preferences/Drives/Drives.xml", Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><Drives clsid=\"{8FDDCC1A-0C3C-43cd-A6B4-71A6DF20DA8C}\">"
        + $"<Drive clsid=\"{{935D1B74-9CB8-4e3c-9914-7DD559B7A417}}\" name=\"K:\" uid=\"{uid}\"><Properties action=\"U\" path=\"{path}\" label=\"Księgowość\" letter=\"K\"/></Drive></Drives>"));
}

/// <summary>Assertions on findings.</summary>
internal static class Findings
{
    public static GpoFinding Single(IEnumerable<GpoFinding> findings, string code, Guid? gpo = null) =>
        Assert.Single(findings, f => f.Code == code && (gpo is null || f.GpoId == gpo));

    public static void None(IEnumerable<GpoFinding> findings, string code, Guid? gpo = null) =>
        Assert.DoesNotContain(findings, f => f.Code == code && (gpo is null || f.GpoId == gpo));

    public static IReadOnlyList<GpoFinding> All(IEnumerable<GpoFinding> findings, string code, Guid? gpo = null) =>
        [.. findings.Where(f => f.Code == code && (gpo is null || f.GpoId == gpo))];
}
