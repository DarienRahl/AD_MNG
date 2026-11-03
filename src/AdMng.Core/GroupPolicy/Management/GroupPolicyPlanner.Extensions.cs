using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Security;

namespace AdMng.Core.GroupPolicy.Management;

/// <summary>
/// A client-side extension and the editor extension ("tool") that a settings file of a GPO folder needs in
/// <c>gPCMachineExtensionNames</c> / <c>gPCUserExtensionNames</c> ([MS-GPOL] 2.2.4).
/// </summary>
/// <param name="RelativePath">The file below the GPO folder.</param>
/// <param name="User">The pair belongs to <c>gPCUserExtensionNames</c> (else the machine's).</param>
/// <param name="Extension">The client-side extension (<see cref="GpoExtensionNames.PreferencesCore"/> is GPMC's preference core entry, not a CSE).</param>
/// <param name="Tool">The editor extension that writes the file.</param>
public sealed record GpoExtensionRequirement(string RelativePath, bool User, Guid Extension, Guid Tool)
{
    /// <summary>The pair names a real client-side extension (not GPMC's preference core entry).</summary>
    public bool IsClientExtension => Extension != GpoExtensionNames.PreferencesCore;
}

/// <summary>Repairs the GPO diagnostics offer (design note docs/dev/k-computers-and-gpo.md 20.2): extension names, duplicate links.</summary>
public sealed partial class GroupPolicyPlanner
{
    /// <summary>
    /// The settings files the editors of AD Manager write (K3, K4) and the pairs each needs, as GPMC's editors list them:
    /// Registry.pol of each half (Registry + the administrative templates tool of the half), <c>GptTmpl.inf</c> (Security),
    /// <c>audit.csv</c> (Audit Policy Configuration), <c>Groups.xml</c> (the preference core entry and Local Users and
    /// Groups).
    /// </summary>
    public static IReadOnlyList<GpoExtensionRequirement> KnownExtensionFiles { get; } =
    [
        new(MachineRegistryPolicyPath, false, GpoExtensionNames.Registry, GpoExtensionNames.AdministrativeTemplatesMachineTool),
        new(UserRegistryPolicyPath, true, GpoExtensionNames.Registry, GpoExtensionNames.AdministrativeTemplatesUserTool),
        new(SecurityTemplate.RelativePath, false, GpoExtensionNames.Security, GpoExtensionNames.SecurityTool),
        new(AuditPolicyDocument.RelativePath, false, GpoExtensionNames.AuditPolicy, GpoExtensionNames.AuditPolicyTool),
        new(GppGroupsDocument.RelativePath, false, GpoExtensionNames.PreferencesCore, GpoExtensionNames.PreferencesLocalUsersAndGroupsTool),
        new(GppGroupsDocument.RelativePath, false, GpoExtensionNames.PreferencesLocalUsersAndGroups, GpoExtensionNames.PreferencesLocalUsersAndGroupsTool),
    ];

    /// <summary>
    /// The pairs of <see cref="KnownExtensionFiles"/> whose file is in <paramref name="files"/> with at least one setting (an
    /// empty file, a file with only a header and a file that cannot be parsed need none - the latter is reported as
    /// unreadable instead).
    /// </summary>
    /// <param name="files">File contents by path relative to the GPO folder (compared without case, as on SYSVOL).</param>
    public static IReadOnlyList<GpoExtensionRequirement> RequiredExtensions(IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var byPath = files.ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase);
        var withSettings = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var result = new List<GpoExtensionRequirement>();
        foreach (var requirement in KnownExtensionFiles)
        {
            if (!withSettings.TryGetValue(requirement.RelativePath, out var has))
            {
                has = byPath.TryGetValue(requirement.RelativePath, out var bytes) && HasSettings(requirement.RelativePath, bytes);
                withSettings[requirement.RelativePath] = has;
            }

            if (has)
            {
                result.Add(requirement);
            }
        }

        return result;
    }

    /// <summary>
    /// The pairs of <see cref="RequiredExtensions"/> whose client-side extension the GPO does not list in the extension
    /// names of its half - clients skip such a file.
    /// </summary>
    public static IReadOnlyList<GpoExtensionRequirement> MissingExtensions(string? machineExtensionNames, string? userExtensionNames, IReadOnlyDictionary<string, byte[]> files)
    {
        var machine = ListedExtensions(machineExtensionNames);
        var user = ListedExtensions(userExtensionNames);
        return [.. RequiredExtensions(files).Where(r => r.IsClientExtension && !(r.User ? user : machine).Contains(r.Extension))];
    }

    /// <summary>
    /// "Napraw nazwy rozszerzeń" (finding <c>ExtensionNamesMissing</c>): the extension names of each half gain the pairs of
    /// the files K3/K4 know that the folder holds with settings (<see cref="RequiredExtensions"/>), as GPMC's editors write
    /// them; the version of each changed half goes up by one so that clients process the GPO again. The files are read from
    /// SYSVOL of the session's DC (in memory). A plan without objects when the names are already complete. The GroupPolicy
    /// module has no cmdlet for the extension names: the script names the GPO and says so.
    /// </summary>
    public async Task<ChangePlan> PlanRepairExtensionNamesAsync(IDirectorySession session, ISysvolConnection sysvol, Guid gpoId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sysvol);
        var gpo = await RequirePolicyAsync(session, gpoId, cancellationToken).ConfigureAwait(false);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in KnownExtensionFiles.Select(r => r.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var file = await sysvol.ReadGpoFileAsync(gpoId, path, GpoTransaction.MaxFileBytes, cancellationToken).ConfigureAwait(false);
            if (file is { Status: SysvolFileStatus.Read, Content: { } content })
            {
                files[path] = content;
            }
        }

        var required = RequiredExtensions(files);
        var machine = gpo.MachineExtensionNames;
        var user = gpo.UserExtensionNames;
        foreach (var requirement in required)
        {
            if (requirement.User)
            {
                user = GpoExtensionNames.With(user, requirement.Extension, requirement.Tool);
            }
            else
            {
                machine = GpoExtensionNames.With(machine, requirement.Extension, requirement.Tool);
            }
        }

        var attributes = new List<AttributeChange>();
        AddIfChanged(attributes, GroupPolicyAttributes.MachineExtensionNames, gpo.MachineExtensionNames, machine);
        AddIfChanged(attributes, GroupPolicyAttributes.UserExtensionNames, gpo.UserExtensionNames, user);
        var title = $"Repair extension names of {gpo.DisplayName}";
        if (attributes.Count == 0)
        {
            return Plan(session, [], title);
        }

        var machineChanged = attributes.Any(a => a.Attribute == GroupPolicyAttributes.MachineExtensionNames);
        var userChanged = attributes.Any(a => a.Attribute == GroupPolicyAttributes.UserExtensionNames);
        var gpt = await GptVersionAsync(sysvol, gpoId, cancellationToken).ConfigureAwait(false);
        var command = new PowerShellCommand("Get-GPO", [GuidParameter(gpoId), Domain(session), PowerShellParameter.Server])
        {
            Comment = "The extension names (gPCMachineExtensionNames, gPCUserExtensionNames) have no GroupPolicy cmdlet: this plan writes them.",
        };
        return Plan(session, [PolicyObject(gpo,
        [
            Modify([.. attributes]),
            VersionChange(gpo.DirectoryVersion, gpt, user: userChanged, computer: machineChanged),
        ], PowerShellEquivalent.Of(command))], title);
    }

    /// <summary>
    /// "Usuń zduplikowane łącza" (finding <c>DuplicateLink</c>): a GPO linked more than once in one <c>gPLink</c> keeps only
    /// the link that counts - the one with the highest precedence (link order), with its options; the other entries of the
    /// value stay as they are (compare-and-swap on the whole value). PowerShell: <c>Remove-GPLink</c>, then <c>New-GPLink</c>
    /// with the kept link's order and options. A plan without objects when the GPO is linked at most once.
    /// </summary>
    public async Task<ChangePlan> PlanRemoveDuplicateLinksAsync(IDirectorySession session, string containerDn, Guid gpoId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var container = await _reader.ReadContainerAsync(session, containerDn, cancellationToken).ConfigureAwait(false)
            ?? throw new DirectoryException(DirectoryErrorKind.NoSuchObject, "The container does not exist.");
        var links = container.Links.Links.Where(l => l.GpoId == gpoId).ToList();
        const string Title = "Remove duplicate GPO links";
        if (links.Count < 2)
        {
            return Plan(session, [], Title);
        }

        // The entry nearest the end of the value has the lowest link order: it is applied last and counts (GroupPolicyInheritance).
        var kept = links.MinBy(l => l.Order)!;
        var after = container.Links.Remove(gpoId).Insert(gpoId, kept.Options, kept.Order);
        PowerShellParameter[] target = [GuidParameter(gpoId), PowerShellParameter.Literal("Target", container.Dn)];
        return Plan(session, [ContainerObject(container, [Modify(GpLinkChange(container, after))], new PowerShellEquivalent(
        [
            new PowerShellCommand("Remove-GPLink", [.. target, Domain(session), PowerShellParameter.Server]),
            new PowerShellCommand("New-GPLink",
            [
                .. target,
                PowerShellParameter.Literal("LinkEnabled", kept.Enabled ? "Yes" : "No"),
                PowerShellParameter.Literal("Enforced", kept.Enforced ? "Yes" : "No"),
                PowerShellParameter.Number("Order", kept.Order),
                Domain(session),
                PowerShellParameter.Server,
            ]),
        ]))], Title);
    }

    private static HashSet<Guid> ListedExtensions(string? value) =>
        [.. GpoExtensionNames.Parse(value)?.Select(p => p.Extension) ?? []];

    /// <summary>The file yields at least one normalized setting (one that cannot be parsed counts as none).</summary>
    private static bool HasSettings(string relativePath, byte[] content)
    {
        if (content.Length == 0)
        {
            return false;
        }

        var sink = new GpoSettingSink(relativePath);
        try
        {
            GpoContentParser.Parse(GpoFileClassifier.Classify(relativePath), content, sink);
        }
        catch (GpoParseException)
        {
            return false;
        }

        return sink.Settings.Count > 0;
    }
}
