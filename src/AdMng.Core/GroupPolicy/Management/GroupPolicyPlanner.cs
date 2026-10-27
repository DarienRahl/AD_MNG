using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Batch.Planning;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Security;

namespace AdMng.Core.GroupPolicy.Management;

/// <summary>
/// Plans every GPO operation of K2 (design note docs/dev/k-computers-and-gpo.md 5.3) as a <see cref="ChangePlan"/> with
/// <see cref="ChangePlan.Sequential"/> and semantic PowerShell equivalents (<see cref="ObjectPlan.PowerShell"/>): the
/// operator previews it in the change plan preview window and the batch executor runs it (CLAUDE.md rule 5). Current values
/// are read from the session's DC while planning (rule 4) and kept in the plan for the compare-and-swap, the preview and
/// the rollback; SYSVOL files are read through the write connection the caller opened on the same DC and kept in memory
/// within the limits of <see cref="GpoTransaction"/> (rule 1). Filters and DNs are built with <see cref="LdapFilter"/> and
/// <see cref="DistinguishedName"/> (rule 3).
/// </summary>
public sealed partial class GroupPolicyPlanner(GroupPolicyReader reader, TimeProvider time)
{
    /// <summary>Longest GPO name (displayName).</summary>
    public const int MaxNameLength = 255;

    /// <summary>gPCFunctionalityVersion of a new GPO (what GPMC writes).</summary>
    public const int FunctionalityVersion = 2;

    private static readonly string[] ContainerClasses = ["top", "container"];
    private static readonly string[] PolicyClasses = ["top", "container", GroupPolicyAttributes.ContainerClass];

    private readonly GroupPolicyReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));

    /// <summary>The reader the planner uses (the documents read through it too).</summary>
    public GroupPolicyReader Reader => _reader;

    // ---- Create, copy, rename, delete -------------------------------------------------------------------------------

    /// <summary>
    /// A new, empty GPO: the container (<c>displayName</c>, <c>gPCFileSysPath</c>, <c>gPCFunctionalityVersion</c> 2,
    /// <c>versionNumber</c> 0, <c>flags</c> 0; the DACL from the schema default), its SYSVOL folder (Machine, User, GPT.INI 0)
    /// with the folder DACL mapped from the container's, <c>CN=Machine</c> and <c>CN=User</c>, and optionally a link.
    /// </summary>
    public async Task<ChangePlan> PlanCreateAsync(IDirectorySession session, string name, string? linkToDn, CancellationToken cancellationToken, Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var gpoId = id ?? Guid.NewGuid();
        var trimmed = (name ?? string.Empty).Trim();
        var issues = await ValidateNameAsync(session, trimmed, exceptId: null, cancellationToken).ConfigureAwait(false);
        var objects = new List<ObjectPlan>(NewPolicyObjects(session, gpoId, trimmed, issues, PowerShellEquivalent.Of(PowerShellCommand.Of("New-GPO",
            PowerShellParameter.Literal("Name", trimmed), Domain(session), PowerShellParameter.Server))));
        if (linkToDn is not null && await LinkObjectAsync(session, linkToDn, gpoId, trimmed, cancellationToken, newGpoName: trimmed).ConfigureAwait(false) is { } link)
        {
            objects.Add(link);
        }

        return Plan(session, objects, $"New GPO {trimmed}");
    }

    /// <summary>
    /// A copy within the domain (<c>Copy-GPO</c>): a new GUID with the source's files (GPT.INI excluded), extension names,
    /// status and WMI filter link (settings kept as directory objects below CN=Machine/CN=User are not copied: a warning); versions 1 on each half the source uses (a new GUID has no client history); the
    /// permissions are the default, or with <paramref name="copyPermissions"/> exactly the source's explicit ACEs.
    /// </summary>
    /// <exception cref="GpoTooLargeException">The source folder exceeds the in-memory limits.</exception>
    public async Task<ChangePlan> PlanCopyAsync(
        IDirectorySession session,
        ISysvolWriteConnection sysvol,
        Guid sourceId,
        string newName,
        bool copyPermissions,
        CancellationToken cancellationToken,
        Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sysvol);
        var source = await _reader.ReadPolicyAsync(session, sourceId, cancellationToken).ConfigureAwait(false)
            ?? throw new DirectoryException(DirectoryErrorKind.NoSuchObject, "The source GPO does not exist.");
        var content = await GroupPolicyReader.ReadFolderAsync(sysvol, sourceId, cancellationToken).ConfigureAwait(false);
        var gpoId = id ?? Guid.NewGuid();
        var trimmed = (newName ?? string.Empty).Trim();
        var issues = await ValidateNameAsync(session, trimmed, exceptId: null, cancellationToken).ConfigureAwait(false);
        AddDirectorySettingsWarning(issues, await _reader.CountDirectorySettingsAsync(session, sourceId, cancellationToken).ConfigureAwait(false), "copied");
        var sourceVersion = await GptVersionAsync(sysvol, sourceId, cancellationToken).ConfigureAwait(false);
        var used = GpoVersion.Max(source.DirectoryVersion ?? GpoVersion.Zero, sourceVersion ?? GpoVersion.Zero);
        var files = content.Files.Where(f => !string.Equals(f.Key, GptIni.RelativePath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).ToList();
        var newVersion = new GpoVersion(
            used.User > 0 || files.Any(f => IsBelow(f.Key, "User")) ? 1 : 0,
            used.Computer > 0 || files.Any(f => IsBelow(f.Key, "Machine")) ? 1 : 0);

        var parameters = new List<PowerShellParameter>
        {
            PowerShellParameter.Literal("SourceGuid", sourceId.ToString("D")),
            PowerShellParameter.Literal("TargetName", trimmed),
            PowerShellParameter.Literal("SourceDomain", session.Info.DomainDnsName),
            PowerShellParameter.Literal("TargetDomain", session.Info.DomainDnsName),
        };
        if (copyPermissions)
        {
            parameters.Add(PowerShellParameter.Switch("CopyAcl"));
        }

        parameters.Add(PowerShellParameter.Server);
        var objects = NewPolicyObjects(session, gpoId, trimmed, issues, new PowerShellEquivalent([new PowerShellCommand("Copy-GPO", parameters)]),
            extra: create => create with
            {
                Attributes =
                [
                    .. create.Attributes.Where(a => !string.Equals(a.Attribute, GroupPolicyAttributes.Flags, StringComparison.OrdinalIgnoreCase)),
                    New(GroupPolicyAttributes.Flags, source.Flags.ToString(CultureInfo.InvariantCulture)),
                    .. NewIfSet(GroupPolicyAttributes.MachineExtensionNames, source.MachineExtensionNames),
                    .. NewIfSet(GroupPolicyAttributes.UserExtensionNames, source.UserExtensionNames),
                    .. NewIfSet(GroupPolicyAttributes.WqlFilter, source.WmiFilterLink),
                ],
            },
            content: [.. files.Select(f => new PlannedChange
            {
                Action = ChangeAction.WriteGpoFile,
                Sysvol = new SysvolChange { GpoId = gpoId, RelativePath = f.Key, NewContent = f.Value },
            }),
            .. newVersion == GpoVersion.Zero
                ? Array.Empty<PlannedChange>()
                : [new PlannedChange { Action = ChangeAction.SetGpoVersion, GpoVersion = new GpoVersionChange(GpoVersion.Zero, GpoVersion.Zero, newVersion) }]],
            security: copyPermissions ? CopiedAces(source.SecurityDescriptor) : null);
        return Plan(session, objects, $"Copy GPO {source.DisplayName}");
    }

    /// <summary>A rename (<c>Rename-GPO</c>): <c>displayName</c> compare-and-swap; GPT.INI is not touched (GPMC does not either).</summary>
    public async Task<ChangePlan> PlanRenameAsync(IDirectorySession session, Guid gpoId, string newName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var gpo = await RequirePolicyAsync(session, gpoId, cancellationToken).ConfigureAwait(false);
        var trimmed = (newName ?? string.Empty).Trim();
        var issues = await ValidateNameAsync(session, trimmed, exceptId: gpoId, cancellationToken).ConfigureAwait(false);
        var change = Modify(Replace(Attr.DisplayName, gpo.DisplayName == SysvolPaths.FolderName(gpoId) ? null : gpo.DisplayName, trimmed)) with { Issues = issues };
        return Plan(session, [PolicyObject(gpo, [change], PowerShellEquivalent.Of(PowerShellCommand.Of("Rename-GPO",
            GuidParameter(gpoId), PowerShellParameter.Literal("TargetName", trimmed), Domain(session), PowerShellParameter.Server)))], $"Rename GPO {gpo.DisplayName}");
    }

    /// <summary>
    /// A delete (<c>Remove-GPO</c>): every container of the domain and every site that links the GPO loses the link, then
    /// the container (subtree: CN=Machine, CN=User) and the SYSVOL folder. The default GPOs are refused.
    /// </summary>
    public async Task<ChangePlan> PlanDeleteAsync(IDirectorySession session, Guid gpoId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var gpo = await RequirePolicyAsync(session, gpoId, cancellationToken).ConfigureAwait(false);
        var links = await _reader.ReadLinksOfAsync(session, gpoId, cancellationToken).ConfigureAwait(false);
        var objects = new List<ObjectPlan>();
        foreach (var containerDn in links.Select(l => l.ContainerDn).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var container = await _reader.ReadContainerAsync(session, containerDn, cancellationToken).ConfigureAwait(false);
            if (container is null || !container.Links.Contains(gpoId))
            {
                continue;
            }

            var after = container.Links.Remove(gpoId);
            var powerShell = container.Kind == GroupPolicySomKind.Site
                ? PowerShellEquivalent.Of(PowerShellCommand.Of("Remove-GPLink", GuidParameter(gpoId), PowerShellParameter.Literal("Target", container.Dn),
                    Domain(session), PowerShellParameter.Server))
                : PowerShellEquivalent.Covered;
            objects.Add(ContainerObject(container, [Modify(GpLinkChange(container, after))], powerShell));
        }

        IReadOnlyList<ValidationIssue> refused = gpo.IsDefault
            ? [new ValidationIssue(IssueSeverity.Error, IssueCodes.DefaultGpo, "The default domain policies cannot be deleted.")]
            : [];

        // The links go first (a link to a missing GPO would be left behind otherwise), then the GPO; nothing of a refused
        // delete runs.
        objects = [.. objects.Select(o => refused.Count == 0 ? o : o with { Issues = refused })];
        objects.Add(PolicyObject(gpo,
        [
            new PlannedChange { Action = ChangeAction.Delete, DeleteSubtree = true, Issues = refused },
            new PlannedChange { Action = ChangeAction.DeleteGpoFolder, Sysvol = new SysvolChange { GpoId = gpoId } },
        ], PowerShellEquivalent.Of(PowerShellCommand.Of("Remove-GPO", GuidParameter(gpoId), Domain(session), PowerShellParameter.Server))));
        return Plan(session, objects, $"Delete GPO {gpo.DisplayName}");
    }

    /// <summary>The SYSVOL folder left behind by a delete whose folder step failed.</summary>
    public ChangePlan PlanDeleteOrphanedFolder(IDirectorySession session, Guid gpoId, string name)
    {
        ArgumentNullException.ThrowIfNull(session);
        var dn = GpLinkValue.GpoDn(gpoId, session.Info.DefaultNamingContext);
        return Plan(session, [new ObjectPlan
        {
            DistinguishedName = dn,
            DisplayName = name,
            Kind = ObjectKind.GroupPolicyContainer,
            Changes = [new PlannedChange { Action = ChangeAction.DeleteGpoFolder, Sysvol = new SysvolChange { GpoId = gpoId } }],
        }], $"Delete GPO folder {name}");
    }

    // ---- Links and inheritance ---------------------------------------------------------------------------------------

    /// <summary>A link of an existing GPO (<c>New-GPLink</c>); without an order the link gets the lowest precedence.</summary>
    public async Task<ChangePlan> PlanLinkAsync(IDirectorySession session, string containerDn, Guid gpoId, CancellationToken cancellationToken,
        bool enforced = false, bool enabled = true, int? order = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var gpo = await RequirePolicyAsync(session, gpoId, cancellationToken).ConfigureAwait(false);
        var link = await LinkObjectAsync(session, containerDn, gpoId, gpo.DisplayName, cancellationToken, enforced: enforced, enabled: enabled, order: order)
            .ConfigureAwait(false);
        return Plan(session, link is null ? [] : [link], $"Link GPO {gpo.DisplayName}");
    }

    /// <summary>Removes a link (<c>Remove-GPLink</c>); no link left clears <c>gPLink</c>.</summary>
    public Task<ChangePlan> PlanUnlinkAsync(IDirectorySession session, string containerDn, Guid gpoId, CancellationToken cancellationToken) =>
        PlanLinkEditAsync(session, containerDn, gpoId, links => links.Remove(gpoId), "Remove-GPLink", [], cancellationToken, "Unlink GPO");

    /// <summary>Moves a link to another link order (<c>Set-GPLink -Order</c>).</summary>
    public Task<ChangePlan> PlanMoveLinkAsync(IDirectorySession session, string containerDn, Guid gpoId, int order, CancellationToken cancellationToken) =>
        PlanLinkEditAsync(session, containerDn, gpoId, links => links.MoveTo(gpoId, order), "Set-GPLink",
            [PowerShellParameter.Number("Order", order)], cancellationToken, "Reorder GPO link");

    /// <summary>Enforced (<c>Set-GPLink -Enforced</c>) and/or link enabled (<c>-LinkEnabled</c>).</summary>
    public Task<ChangePlan> PlanLinkOptionsAsync(IDirectorySession session, string containerDn, Guid gpoId, bool? enforced, bool? enabled, CancellationToken cancellationToken)
    {
        var parameters = new List<PowerShellParameter>();
        if (enforced is { } e)
        {
            parameters.Add(PowerShellParameter.Literal("Enforced", e ? "Yes" : "No"));
        }

        if (enabled is { } on)
        {
            parameters.Add(PowerShellParameter.Literal("LinkEnabled", on ? "Yes" : "No"));
        }

        return PlanLinkEditAsync(session, containerDn, gpoId, links => links.SetOptions(gpoId, enforced, enabled), "Set-GPLink", parameters,
            cancellationToken, "Change GPO link");
    }

    /// <summary>Block inheritance of a domain head or OU (<c>Set-GPInheritance</c>): <c>gPOptions</c> 1 or cleared.</summary>
    public async Task<ChangePlan> PlanBlockInheritanceAsync(IDirectorySession session, string containerDn, bool block, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var container = await _reader.ReadContainerAsync(session, containerDn, cancellationToken).ConfigureAwait(false)
            ?? throw new DirectoryException(DirectoryErrorKind.NoSuchObject, "The container does not exist.");
        var issues = new List<ValidationIssue>();
        if (container.Kind == GroupPolicySomKind.Site)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, IssueCodes.NotApplicable, "Sites cannot block inheritance."));
        }

        var options = long.TryParse(container.GpOptions, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        var next = block ? options | GroupPolicyLinks.BlockInheritanceOption : options & ~GroupPolicyLinks.BlockInheritanceOption;
        var change = next == options
            ? Modify()
            : Modify(Replace(GroupPolicyAttributes.GpOptions, container.GpOptions, next == 0 ? null : next.ToString(CultureInfo.InvariantCulture)));
        return Plan(session, [ContainerObject(container, [change with { Issues = issues }], PowerShellEquivalent.Of(PowerShellCommand.Of("Set-GPInheritance",
            PowerShellParameter.Literal("Target", container.Dn), PowerShellParameter.Literal("IsBlocked", block ? "Yes" : "No"), Domain(session),
            PowerShellParameter.Server)))], block ? "Block inheritance" : "Allow inheritance");
    }

    // ---- GPO status, WMI filter link --------------------------------------------------------------------------------

    /// <summary>The GPO status (<c>flags</c>; <c>(Get-GPO …).GpoStatus = …</c>).</summary>
    public async Task<ChangePlan> PlanStatusAsync(IDirectorySession session, Guid gpoId, GpoStatus status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var gpo = await RequirePolicyAsync(session, gpoId, cancellationToken).ConfigureAwait(false);
        var flags = (gpo.Flags & ~3) | (int)status;
        var change = flags == gpo.Flags
            ? Modify()
            : Modify(Replace(GroupPolicyAttributes.Flags, gpo.Flags.ToString(CultureInfo.InvariantCulture), flags.ToString(CultureInfo.InvariantCulture)));
        return Plan(session, [PolicyObject(gpo, [change], PowerShellEquivalent.Of(new PowerShellCommand("Get-GPO", [GuidParameter(gpoId), Domain(session), PowerShellParameter.Server])
        {
            Property = "GpoStatus",
            PropertyValue = status.ToString(),
        }))], $"GPO status {gpo.DisplayName}");
    }

    /// <summary>The WMI filter link of a GPO (<c>gPCWQLFilter</c> = <c>[domain;{ID};0]</c>, or cleared).</summary>
    public async Task<ChangePlan> PlanWmiFilterLinkAsync(IDirectorySession session, Guid gpoId, WmiFilterInfo? filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var gpo = await RequirePolicyAsync(session, gpoId, cancellationToken).ConfigureAwait(false);
        var value = filter is null ? null : WmiFilterLinks.Format(session.Info.DomainDnsName, filter.Id);
        var change = string.Equals(gpo.WmiFilterLink, value, StringComparison.OrdinalIgnoreCase)
            ? Modify()
            : Modify(Replace(GroupPolicyAttributes.WqlFilter, gpo.WmiFilterLink, value));

        // No GroupPolicy cmdlet sets the filter: the exporter writes the Set-ADObject line of the step.
        return Plan(session, [PolicyObject(gpo, [change], null)], $"WMI filter of {gpo.DisplayName}");
    }

    // ---- Versions and folder permissions (Stan tab) ------------------------------------------------------------------

    /// <summary>"Wyrównaj wersje": both halves to the higher of AD and GPT.INI + 1 (clients reprocess the GPO).</summary>
    public async Task<ChangePlan> PlanAlignVersionsAsync(IDirectorySession session, ISysvolWriteConnection sysvol, Guid gpoId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sysvol);
        var gpo = await RequirePolicyAsync(session, gpoId, cancellationToken).ConfigureAwait(false);
        var gpt = await GptVersionAsync(sysvol, gpoId, cancellationToken).ConfigureAwait(false);
        var change = VersionChange(gpo.DirectoryVersion, gpt, user: true, computer: true);
        return Plan(session, [PolicyObject(gpo, [change], null)], $"Align versions of {gpo.DisplayName}");
    }

    /// <summary>"Napraw uprawnienia folderu": the folder DACL mapped from the container's again (the old one kept for Undo).</summary>
    public async Task<ChangePlan> PlanRepairFolderSecurityAsync(IDirectorySession session, ISysvolWriteConnection sysvol, Guid gpoId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sysvol);
        var gpo = await RequirePolicyAsync(session, gpoId, cancellationToken).ConfigureAwait(false);
        var old = await sysvol.GetGpoFolderSecurityAsync(gpoId, cancellationToken).ConfigureAwait(false);
        return Plan(session, [PolicyObject(gpo,
        [
            new PlannedChange
            {
                Action = ChangeAction.SyncGpoFolderSecurity,
                Sysvol = new SysvolChange { GpoId = gpoId, OldFolderSecurity = old, NewFolderSecurity = GpoFolderAcl.FromDirectoryDescriptor(gpo.SecurityDescriptor) },
            },
        ], null)], $"Repair folder permissions of {gpo.DisplayName}");
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// A version step for content changes of the named halves: old = what AD and GPT.INI hold now, new = their higher half +
    /// 1 on each changed half (design note 3.4). A half at its maximum gives an error issue instead.
    /// </summary>
    public static PlannedChange VersionChange(GpoVersion? directory, GpoVersion? gptIni, bool user, bool computer)
    {
        var current = GpoVersion.Max(directory ?? GpoVersion.Zero, gptIni ?? GpoVersion.Zero);
        try
        {
            return new PlannedChange
            {
                Action = ChangeAction.SetGpoVersion,
                GpoVersion = new GpoVersionChange(directory, gptIni, current.Bump(user, computer)),
            };
        }
        catch (GpoVersionOverflowException)
        {
            return new PlannedChange
            {
                Action = ChangeAction.SetGpoVersion,
                GpoVersion = new GpoVersionChange(directory, gptIni, current),
                Issues = [new ValidationIssue(IssueSeverity.Error, IssueCodes.GpoVersionOverflow, "The GPO version reached its maximum.")],
            };
        }
    }

    /// <summary>The <c>Version</c> of GPT.INI (null: missing or unreadable).</summary>
    public static async Task<GpoVersion?> GptVersionAsync(ISysvolConnection sysvol, Guid gpoId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sysvol);
        var file = await sysvol.ReadGpoFileAsync(gpoId, GptIni.RelativePath, 64 * 1024, cancellationToken).ConfigureAwait(false);
        if (file.Content is null)
        {
            return null;
        }

        try
        {
            return GptIni.ReadVersion(file.Content) is { } version ? GpoVersion.FromInt32(version) : null;
        }
        catch (GpoParseException)
        {
            return null;
        }
    }

    /// <summary>A plan of GPO objects: sequential, pinned to the session's DC.</summary>
    private ChangePlan Plan(IDirectorySession session, IReadOnlyList<ObjectPlan> objects, string title) => new()
    {
        Objects = objects,
        Title = title,
        Server = session.Info.Server,
        CreatedAt = _time.GetUtcNow(),
        Sequential = true,
    };

    private async Task<GpoInfo> RequirePolicyAsync(IDirectorySession session, Guid gpoId, CancellationToken cancellationToken) =>
        await _reader.ReadPolicyAsync(session, gpoId, cancellationToken).ConfigureAwait(false)
        ?? throw new DirectoryException(DirectoryErrorKind.NoSuchObject, "The GPO does not exist.");

    private async Task<List<ValidationIssue>> ValidateNameAsync(IDirectorySession session, string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        var issues = new List<ValidationIssue>();
        if (name.Length == 0)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, IssueCodes.TooShort, "The GPO needs a name.", Attr.DisplayName));
            return issues;
        }

        if (name.Length > MaxNameLength)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, IssueCodes.TooLong, "The GPO name is too long.", Attr.DisplayName));
        }

        if (name.Any(char.IsControl))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, IssueCodes.InvalidValue, "The GPO name contains control characters.", Attr.DisplayName));
        }

        var existing = await _reader.ReadPoliciesAsync(session, cancellationToken).ConfigureAwait(false);
        if (existing.Any(p => p.Id != exceptId && string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, IssueCodes.Duplicate, "A GPO with this name already exists.", Attr.DisplayName));
        }

        return issues;
    }

    /// <summary>The objects of a new GPO: the container with its folder (and content), CN=Machine, CN=User.</summary>
    private static List<ObjectPlan> NewPolicyObjects(
        IDirectorySession session,
        Guid gpoId,
        string name,
        IReadOnlyList<ValidationIssue> issues,
        PowerShellEquivalent powerShell,
        Func<PlannedChange, PlannedChange>? extra = null,
        IReadOnlyList<PlannedChange>? content = null,
        AclChange? security = null)
    {
        var domainDn = session.Info.DefaultNamingContext;
        var dns = session.Info.DomainDnsName.Trim().TrimEnd('.');
        var gpcDn = GpLinkValue.GpoDn(gpoId, domainDn);
        var create = new PlannedChange
        {
            Action = ChangeAction.Create,
            Issues = issues,
            Attributes =
            [
                new AttributeChange(Attr.ObjectClass, AttributeChangeKind.Replace, [], [GroupPolicyAttributes.ContainerClass]),
                New(Attr.DisplayName, name),
                New(GroupPolicyAttributes.FileSysPath, @"\\" + dns + @"\SysVol\" + dns + @"\Policies\" + SysvolPaths.FolderName(gpoId)),
                New(GroupPolicyAttributes.FunctionalityVersion, FunctionalityVersion.ToString(CultureInfo.InvariantCulture)),
                New(GroupPolicyAttributes.VersionNumber, "0"),
                New(GroupPolicyAttributes.Flags, "0"),
            ],
        };
        if (extra is not null)
        {
            create = extra(create);
        }

        var steps = new List<PlannedChange>
        {
            create,
            new() { Action = ChangeAction.CreateGpoFolder, Sysvol = new SysvolChange { GpoId = gpoId } },
        };
        steps.AddRange(content ?? []);
        if (security is { IsEmpty: false })
        {
            steps.Add(AclPlanning.ForNewObject(security) with
            {
                Issues = security.ReplacesExplicit
                    ? [new ValidationIssue(IssueSeverity.Info, IssueCodes.GpoPermissionsReplaced,
                        "The new GPO gets exactly these permissions: the default entries it lacks (such as Authenticated Users' Apply Group Policy) are removed.")]
                    : [],
            });
        }

        steps.Add(new PlannedChange { Action = ChangeAction.SyncGpoFolderSecurity, Sysvol = new SysvolChange { GpoId = gpoId } });
        PlannedChange Child() => new()
        {
            Action = ChangeAction.Create,
            Attributes = [new AttributeChange(Attr.ObjectClass, AttributeChangeKind.Replace, [], ["container"])],
        };
        return
        [
            new ObjectPlan
            {
                DistinguishedName = gpcDn,
                DisplayName = name,
                Kind = ObjectKind.GroupPolicyContainer,
                ObjectClasses = PolicyClasses,
                PowerShell = powerShell,
                Changes = steps,
            },
            new ObjectPlan
            {
                DistinguishedName = DistinguishedName.Parse(gpcDn).Child("CN", "Machine").ToString(),
                DisplayName = name + " – Machine",
                Kind = ObjectKind.Container,
                ObjectClasses = ContainerClasses,
                PowerShell = PowerShellEquivalent.Covered,
                Changes = [Child()],
            },
            new ObjectPlan
            {
                DistinguishedName = DistinguishedName.Parse(gpcDn).Child("CN", "User").ToString(),
                DisplayName = name + " – User",
                Kind = ObjectKind.Container,
                ObjectClasses = ContainerClasses,
                PowerShell = PowerShellEquivalent.Covered,
                Changes = [Child()],
            },
        ];
    }

    /// <summary>
    /// The source's explicit ACEs as the exact explicit ACEs of the copy (<c>Copy-GPO -CopyAcl</c>, a GPMC restore): the
    /// schema default entries the source lacks are removed, so its security filtering survives.
    /// </summary>
    private static AclChange? CopiedAces(byte[]? descriptor)
    {
        if (descriptor is null || !SecurityDescriptorSummary.TryParse(descriptor, out var summary) || summary is null)
        {
            return null;
        }

        var aces = summary.Aces.Select(AceSpec.FromAce).OfType<AceSpec>().Where(a => AceSpec.IsValidSid(a.TrusteeSid) && a.Mask != 0).ToList();
        return aces.Count == 0 ? null : new AclChange(aces, []) { ReplacesExplicit = true };
    }

    private async Task<ObjectPlan?> LinkObjectAsync(
        IDirectorySession session,
        string containerDn,
        Guid gpoId,
        string gpoName,
        CancellationToken cancellationToken,
        bool enforced = false,
        bool enabled = true,
        int? order = null,
        string? newGpoName = null)
    {
        var container = await _reader.ReadContainerAsync(session, containerDn, cancellationToken).ConfigureAwait(false)
            ?? throw new DirectoryException(DirectoryErrorKind.NoSuchObject, "The container does not exist.");
        var options = (enforced ? GroupPolicyLink.EnforcedOption : 0) | (enabled ? 0 : GroupPolicyLink.DisabledOption);
        var issues = new List<ValidationIssue>();
        GpLinkValue after;
        if (container.Links.Contains(gpoId))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Info, IssueCodes.NoChange, "The GPO is already linked to this container."));
            after = container.Links;
        }
        else
        {
            after = container.Links.Insert(gpoId, options, order);
        }

        var parameters = new List<PowerShellParameter>
        {
            newGpoName is not null ? PowerShellParameter.Literal("Name", newGpoName) : GuidParameter(gpoId),
            PowerShellParameter.Literal("Target", container.Dn),
            PowerShellParameter.Literal("LinkEnabled", enabled ? "Yes" : "No"),
        };
        if (enforced)
        {
            parameters.Add(PowerShellParameter.Literal("Enforced", "Yes"));
        }

        if (order is { } o)
        {
            parameters.Add(PowerShellParameter.Number("Order", o));
        }

        parameters.Add(Domain(session));
        parameters.Add(PowerShellParameter.Server);
        return ContainerObject(container, [Modify(GpLinkChange(container, after)) with { Issues = issues }],
            new PowerShellEquivalent([new PowerShellCommand("New-GPLink", parameters)]));
    }

    private async Task<ChangePlan> PlanLinkEditAsync(
        IDirectorySession session,
        string containerDn,
        Guid gpoId,
        Func<GpLinkValue, GpLinkValue> edit,
        string cmdlet,
        IReadOnlyList<PowerShellParameter> parameters,
        CancellationToken cancellationToken,
        string title)
    {
        ArgumentNullException.ThrowIfNull(session);
        var container = await _reader.ReadContainerAsync(session, containerDn, cancellationToken).ConfigureAwait(false)
            ?? throw new DirectoryException(DirectoryErrorKind.NoSuchObject, "The container does not exist.");
        PlannedChange change;
        if (!container.Links.Contains(gpoId))
        {
            change = Modify() with { Issues = [new ValidationIssue(IssueSeverity.Error, IssueCodes.NotFound, "The GPO is not linked to this container.")] };
        }
        else
        {
            change = Modify(GpLinkChange(container, edit(container.Links)));
        }

        return Plan(session, [ContainerObject(container, [change], new PowerShellEquivalent([new PowerShellCommand(cmdlet,
            [GuidParameter(gpoId), PowerShellParameter.Literal("Target", container.Dn), .. parameters, Domain(session), PowerShellParameter.Server])]))], title);
    }

    /// <summary>
    /// The <c>gPLink</c> change from the value read to <paramref name="after"/> (compare-and-swap on the whole value; a value
    /// of a single space, which some tools leave behind, counts as empty but is replaced exactly).
    /// </summary>
    private static AttributeChange? GpLinkChange(GroupPolicyContainerInfo container, GpLinkValue after)
    {
        var newValue = after.ToAttributeValue();
        var raw = container.RawGpLink;
        if ((string.IsNullOrWhiteSpace(raw) && newValue is null) || string.Equals(raw, newValue, StringComparison.Ordinal))
        {
            return null;
        }

        return Replace(Attr.GpLink, string.IsNullOrEmpty(raw) ? null : raw, newValue);
    }

    private static ObjectPlan PolicyObject(GpoInfo gpo, IReadOnlyList<PlannedChange> changes, PowerShellEquivalent? powerShell) => new()
    {
        DistinguishedName = gpo.Dn,
        DisplayName = gpo.DisplayName,
        Kind = ObjectKind.GroupPolicyContainer,
        ObjectClasses = PolicyClasses,
        PowerShell = powerShell,
        Changes = changes,
    };

    private static ObjectPlan ContainerObject(GroupPolicyContainerInfo container, IReadOnlyList<PlannedChange> changes, PowerShellEquivalent? powerShell) => new()
    {
        DistinguishedName = container.Dn,
        DisplayName = container.Name,
        Kind = container.Kind switch
        {
            GroupPolicySomKind.Site => ObjectKind.Site,
            GroupPolicySomKind.Domain => ObjectKind.Domain,
            _ => ObjectKind.OrganizationalUnit,
        },
        PowerShell = powerShell,
        Changes = changes,
    };

    private static PlannedChange Modify(params AttributeChange?[] changes) => new()
    {
        Action = ChangeAction.Modify,
        Attributes = [.. changes.OfType<AttributeChange>()],
    };

    private static AttributeChange New(string attribute, string value) => new(attribute, AttributeChangeKind.Replace, [], [value]);

    private static IEnumerable<AttributeChange> NewIfSet(string attribute, string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : [New(attribute, value)];

    /// <summary>A replace from <paramref name="oldValue"/> to <paramref name="newValue"/> (null: clear).</summary>
    internal static AttributeChange Replace(string attribute, string? oldValue, string? newValue) => newValue is null
        ? new AttributeChange(attribute, AttributeChangeKind.Clear, oldValue is null ? [] : [oldValue], [])
        : new AttributeChange(attribute, AttributeChangeKind.Replace, oldValue is null ? [] : [oldValue], [newValue]);

    private static PowerShellParameter GuidParameter(System.Guid id) => PowerShellParameter.Literal("Guid", id.ToString("D"));

    private static PowerShellParameter Domain(IDirectorySession session) => PowerShellParameter.Literal("Domain", session.Info.DomainDnsName);

    private static bool IsBelow(string path, string folder) => path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
}
