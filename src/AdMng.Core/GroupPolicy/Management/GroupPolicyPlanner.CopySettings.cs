using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Backup;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Modeling;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Core.GroupPolicy.Security;
using AdMng.Core.Ldap;

namespace AdMng.Core.GroupPolicy.Management;

/// <summary>
/// What the K7 planners take from the setting-unit layer (docs/dev/k-computers-and-gpo.md 21.1–21.3): the settings read of a
/// folder, the principal names to resolve, their resolution, the units and value equality. <see cref="Default"/> is the real
/// layer; tests may pass units built by hand.
/// </summary>
internal sealed record GpoUnitSource(
    Func<GpoFolderContent, GpoSettingsRead> ReadSettings,
    Func<IReadOnlyList<GpoSetting>, string?, IReadOnlyCollection<string>> PrincipalNames,
    Func<IDirectorySession, IEnumerable<string>, string?, CancellationToken, Task<IReadOnlyList<GpoPrincipal>>> ResolveAsync,
    Func<GpoFolderContent, GpoUnitContext, IReadOnlyList<GpoSettingUnit>> Units,
    Func<GpoSettingUnit, GpoSettingUnit, bool> SameValue,
    Func<string?, string?, string?> KnownSid)
{
    public static GpoUnitSource Default { get; } = new(
        GroupPolicyReader.ReadSettings,
        GpoSettingUnits.PrincipalNames,
        GpoPrincipalResolver.ResolveAsync,
        GpoSettingUnits.Of,
        GpoSettingUnits.SameValue,
        GpoPrincipalResolver.KnownSid);

    public override string ToString() => nameof(GpoUnitSource);
}

/// <summary>
/// K7 (docs/dev/k-computers-and-gpo.md 21.5): copy settings between GPOs, remove settings, merge one GPO into another,
/// unlink a GPO everywhere and delete it only while it is as the cleanup advisor saw it. Every content plan for one GPO
/// is one <see cref="ObjectPlan"/>: one extension-name Modify, one WriteGpoFile per touched file whose old content is the
/// file read now on the session's DC, one version change – the executor runs them as one GPO transaction and the rollback
/// planner undoes them. Index values are never used: sources and target are read again here.
/// </summary>
public sealed partial class GroupPolicyPlanner
{
    /// <summary>The setting-unit layer the K7 planners use (tests replace it).</summary>
    internal GpoUnitSource UnitSource { get; set; } = GpoUnitSource.Default;

    /// <summary>
    /// Copies units of up to <see cref="GpoSettingsCopyRequest.MaxSources"/> source GPOs into one target GPO: one plan with
    /// one <see cref="ObjectPlan"/> for the target (one extension-name Modify, one WriteGpoFile per touched file built from
    /// bytes read now on the session's DC, one version change). Refused units are issues; the rest goes on.
    /// </summary>
    /// <exception cref="GpoTooLargeException">A folder exceeds the in-memory limits.</exception>
    /// <exception cref="SysvolWriteException">A folder cannot be listed or a file cannot be read.</exception>
    public async Task<GpoSettingsPlan> PlanCopySettingsAsync(IDirectorySession session, ISysvolWriteConnection sysvol,
        GpoSettingsCopyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sysvol);
        ArgumentNullException.ThrowIfNull(request);
        var target = await RequirePolicyAsync(session, request.TargetId, cancellationToken).ConfigureAwait(false);
        var title = $"Copy settings to {target.DisplayName}";
        var sourceIds = request.Units.Select(u => u.GpoId).Where(id => id != request.TargetId).Distinct().ToList();
        if (sourceIds.Count > GpoSettingsCopyRequest.MaxSources)
        {
            var error = new ValidationIssue(IssueSeverity.Error, IssueCodes.GpoCopyTooManySources,
                $"{sourceIds.Count} source GPOs selected; one copy takes at most {GpoSettingsCopyRequest.MaxSources}.")
            {
                Arguments = [sourceIds.Count.ToString(CultureInfo.InvariantCulture), GpoSettingsCopyRequest.MaxSources.ToString(CultureInfo.InvariantCulture)],
            };
            return new GpoSettingsPlan(Plan(session, [PolicyObject(target, [], null) with { Issues = [error] }], title),
                [.. request.Units.Select(u => new GpoUnitOutcome(u, GpoUnitOutcomeKind.Refused, IssueCodes.GpoCopyTooManySources))]);
        }

        var work = await LoadAsync(session, sysvol, target, sourceIds, request.Definitions, cancellationToken).ConfigureAwait(false);
        if (work.TargetIncomplete)
        {
            work.Error(IssueCodes.GpoSettingIncomplete, "Not every setting of the target GPO could be read.", target.DisplayName);
        }

        // Settings kept as directory objects below CN=Machine / CN=User are never units: say that they are not copied.
        foreach (var source in work.Sources.Values.Where(s => s.Info is not null && request.Units.Any(u => u.GpoId == s.Info.Id)))
        {
            if (await _reader.CountDirectorySettingsAsync(session, source.Info!.Id, cancellationToken).ConfigureAwait(false) is > 0 and var count)
            {
                work.Warn(IssueCodes.GpoDirectorySettings, string.Create(CultureInfo.InvariantCulture,
                    $"{count} settings objects kept in the directory (software installation, wireless or wired network policies) are not copied."));
            }
        }

        // The same setting from two source GPOs: neither (no silent "last wins").
        var found = request.Units.DistinctBy(u => (u.GpoId, u.UnitId.ToUpperInvariant())).Select(u => (Selection: u, Unit: work.Find(u))).ToList();
        var ambiguous = found.Where(f => f.Unit is not null).GroupBy(f => f.Unit!.BaseId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(f => f.Selection.GpoId).Distinct().Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var outcomes = new List<GpoUnitOutcome>();
        foreach (var (selection, unit) in found)
        {
            outcomes.Add(CopyOne(work, selection, unit, ambiguous, request.ConflictByUnit.TryGetValue(unit?.BaseId ?? string.Empty, out var mode) ? mode : request.Conflict,
                merge: false));
        }

        var objectPlan = await TargetObjectAsync(session, sysvol, work, cancellationToken).ConfigureAwait(false);
        return new GpoSettingsPlan(Plan(session, [objectPlan], title), outcomes);
    }

    /// <summary>Removes units from one GPO (same reads, writers and refusals as the copy; one version bump).</summary>
    /// <exception cref="GpoTooLargeException">The folder exceeds the in-memory limits.</exception>
    /// <exception cref="SysvolWriteException">The folder cannot be listed or a file cannot be read.</exception>
    public async Task<GpoSettingsPlan> PlanRemoveSettingsAsync(IDirectorySession session, ISysvolWriteConnection sysvol, Guid gpoId,
        IReadOnlyList<GpoUnitSelection> units, PolicyDefinitionSet? definitions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sysvol);
        ArgumentNullException.ThrowIfNull(units);
        var gpo = await RequirePolicyAsync(session, gpoId, cancellationToken).ConfigureAwait(false);
        var work = await LoadAsync(session, sysvol, gpo, [], definitions, cancellationToken).ConfigureAwait(false);
        var outcomes = new List<GpoUnitOutcome>();
        if (work.TargetIncomplete)
        {
            work.Error(IssueCodes.GpoSettingIncomplete, "Not every setting of the GPO could be read.", gpo.DisplayName);
        }

        foreach (var selection in units.DistinctBy(u => (u.GpoId, u.UnitId.ToUpperInvariant())))
        {
            var unit = selection.GpoId == gpoId ? FindUnit(work.TargetUnits, selection) : null;
            if (unit is null && definitions is null && selection.GpoId == gpoId && IsPolicyId(selection.UnitId))
            {
                work.Warn(IssueCodes.GpoSettingNoDefinitions, "The administrative template needs the ADMX templates.", gpo.DisplayName, selection.UnitId);
                outcomes.Add(new GpoUnitOutcome(selection, GpoUnitOutcomeKind.Refused, IssueCodes.GpoSettingNoDefinitions));
                continue;
            }

            if (unit is null)
            {
                work.Warn(IssueCodes.GpoSettingNotFound, "The setting is no longer in the GPO as it was found.", gpo.DisplayName, selection.UnitId);
                outcomes.Add(new GpoUnitOutcome(selection, GpoUnitOutcomeKind.NotFound, IssueCodes.GpoSettingNotFound));
                continue;
            }

            var refusal = work.TargetIncomplete ? IssueCodes.GpoSettingIncomplete
                : RefusalCode(unit) ?? (unit.Kind == GpoSettingUnitKind.AdministrativeTemplate && definitions is null ? IssueCodes.GpoSettingNoDefinitions : null)
                ?? work.Writer.CheckRemove(unit);
            if (refusal is not null)
            {
                if (refusal != IssueCodes.GpoSettingIncomplete)
                {
                    work.Warn(refusal, "The setting cannot be removed by AD Manager.", gpo.DisplayName, TechnicalKey(unit));
                }

                outcomes.Add(new GpoUnitOutcome(selection, GpoUnitOutcomeKind.Refused, refusal));
                continue;
            }

            if (unit.RemovePolicy)
            {
                work.Warn(IssueCodes.GpoSettingRemovePolicy, "Clients undo the preference item when it is removed.", gpo.DisplayName, TechnicalKey(unit));
            }

            work.Writer.Remove(unit);
            outcomes.Add(new GpoUnitOutcome(selection, GpoUnitOutcomeKind.Removed));
        }

        var objectPlan = await TargetObjectAsync(session, sysvol, work, cancellationToken).ConfigureAwait(false);
        return new GpoSettingsPlan(Plan(session, [objectPlan], $"Remove settings from {gpo.DisplayName}"), outcomes);
    }

    /// <summary>
    /// Merges A into B as one sequential plan: copy A's units into B, replace A's links by B (one gPLink compare-and-swap
    /// per container), set A to all settings disabled. Refused with an error when A cannot be carried completely (21.5).
    /// </summary>
    /// <exception cref="GpoTooLargeException">A folder exceeds the in-memory limits.</exception>
    /// <exception cref="SysvolWriteException">A folder cannot be listed or a file cannot be read.</exception>
    public async Task<GpoSettingsPlan> PlanMergeAsync(IDirectorySession session, ISysvolWriteConnection sysvol,
        GpoMergeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sysvol);
        ArgumentNullException.ThrowIfNull(request);
        var a = await RequirePolicyAsync(session, request.SourceId, cancellationToken).ConfigureAwait(false);
        var b = await RequirePolicyAsync(session, request.TargetId, cancellationToken).ConfigureAwait(false);
        var title = $"Merge GPO {a.DisplayName} into {b.DisplayName}";
        var errors = new List<ValidationIssue>();
        var warnings = new List<ValidationIssue>();
        void Add(List<ValidationIssue> list, IssueSeverity severity, string code, string message, params string[] arguments) =>
            list.Add(new ValidationIssue(severity, code, message) { Arguments = arguments });

        if (a.Id == b.Id)
        {
            Add(errors, IssueSeverity.Error, IssueCodes.InvalidValue, "A GPO cannot be merged into itself.");
            return new GpoSettingsPlan(Plan(session, [PolicyObject(a, [], null) with { Issues = errors }], title), []);
        }

        var work = await LoadAsync(session, sysvol, b, [a.Id], request.Definitions, cancellationToken).ConfigureAwait(false);
        var source = work.Sources[a.Id];
        if (a.IsDefault)
        {
            Add(errors, IssueSeverity.Error, IssueCodes.DefaultGpo, "A default domain policy cannot be merged into another GPO.");
        }

        if (source.Incomplete)
        {
            Add(errors, IssueSeverity.Error, IssueCodes.GpoSettingIncomplete, "Not every setting of the merged GPO could be read.", a.DisplayName);
        }

        if (work.TargetIncomplete)
        {
            Add(errors, IssueSeverity.Error, IssueCodes.GpoSettingIncomplete, "Not every setting of the target GPO could be read.", b.DisplayName);
        }

        if (await _reader.CountDirectorySettingsAsync(session, a.Id, cancellationToken).ConfigureAwait(false) is > 0 and var directory)
        {
            Add(errors, IssueSeverity.Error, IssueCodes.GpoDirectorySettings, string.Create(CultureInfo.InvariantCulture,
                $"{directory} settings objects kept in the directory (software installation, wireless or wired network policies) cannot be merged."));
        }

        // Units of a half A has disabled do not apply now and are left out (21.13): they neither block nor warn.
        var active = source.Units.Where(u => !PartDisabled(a, u.Scope)).ToList();
        var blocking = active.Count(u => u.BlocksMerge);
        var unparsed = source.Content!.Files.Keys.Count(IsUnparsedFile);
        if (unparsed > 0)
        {
            Add(errors, IssueSeverity.Error, IssueCodes.GpoMergeUnparsedFiles, "The GPO holds files AD Manager does not read.", a.DisplayName,
                unparsed.ToString(CultureInfo.InvariantCulture));
        }

        var notCarried = source.Units.Count(u => !u.CountsAsSetting && u.Kind != GpoSettingUnitKind.UnreadableFile);
        if (notCarried > 0)
        {
            Add(warnings, IssueSeverity.Info, IssueCodes.GpoMergeNotCarried, "Comments and script files are not carried over.", a.DisplayName,
                notCarried.ToString(CultureInfo.InvariantCulture));
        }

        // Links: every container that links A, read now (the compare-and-swap of the relink).
        var linksA = await _reader.ReadLinksOfAsync(session, a.Id, cancellationToken).ConfigureAwait(false);
        var linksB = await _reader.ReadLinksOfAsync(session, b.Id, cancellationToken).ConfigureAwait(false);
        var containers = new List<GroupPolicyContainerInfo>();
        foreach (var dn in linksA.Select(l => l.ContainerDn).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (await _reader.ReadContainerAsync(session, dn, cancellationToken).ConfigureAwait(false) is { } container && container.Links.Contains(a.Id))
            {
                containers.Add(container);
            }
        }

        // Precedence in the containers that link both (order 1 = highest precedence).
        var shared = containers.Where(c => c.Links.Contains(b.Id)).ToList();
        var aFirst = shared.Count(c => c.Links.Find(a.Id)!.Order < c.Links.Find(b.Id)!.Order);
        GpoCopyConflictMode? derived = shared.Count == 0 || aFirst == shared.Count ? GpoCopyConflictMode.Replace
            : aFirst == 0 ? GpoCopyConflictMode.Skip
            : null;
        var mode = request.Conflict ?? derived;
        if (mode is null)
        {
            Add(errors, IssueSeverity.Error, IssueCodes.GpoMergeOrderMixed, "The link order of the two GPOs is mixed: choose the conflict mode.",
                a.DisplayName, b.DisplayName);
        }

        // B's object: every unit of A that counts as a setting (units of A's disabled halves are left out).
        var outcomes = new List<GpoUnitOutcome>();
        var refused = 0;
        foreach (var unit in source.Units.Where(u => u.CountsAsSetting))
        {
            var selection = new GpoUnitSelection(a.Id, unit.Id, unit.ItemId, unit.ValueFingerprint);
            if (PartDisabled(a, unit.Scope))
            {
                work.Info(IssueCodes.GpoSettingSourcePartDisabled, "The setting comes from a disabled part of the merged GPO and is left out.", a.DisplayName, TechnicalKey(unit));
                outcomes.Add(new GpoUnitOutcome(selection, GpoUnitOutcomeKind.Skipped, IssueCodes.GpoSettingSourcePartDisabled));
                continue;
            }

            if (unit.BlocksMerge)
            {
                outcomes.Add(new GpoUnitOutcome(selection, GpoUnitOutcomeKind.Refused, RefusalCode(unit) ?? IssueCodes.GpoSettingUnsupported));
                continue;
            }

            var outcome = CopyOne(work, selection, unit, ambiguous: new HashSet<string>(), mode ?? GpoCopyConflictMode.Replace, merge: true);
            refused += outcome.Kind == GpoUnitOutcomeKind.Refused ? 1 : 0;
            outcomes.Add(outcome);
        }

        if (blocking + refused > 0)
        {
            Add(errors, IssueSeverity.Error, IssueCodes.GpoMergeNotCopyable, "The GPO has settings that cannot be copied.", a.DisplayName,
                (blocking + refused).ToString(CultureInfo.InvariantCulture));
        }

        foreach (var unit in active.Where(u => u.RemovePolicy && u.CountsAsSetting))
        {
            work.Warn(IssueCodes.GpoSettingRemovePolicy, "Clients undo the preference item of the disabled GPO before the copy applies.", a.DisplayName, TechnicalKey(unit));
        }

        if ((b.Status & GpoStatus.ComputerSettingsDisabled) != 0 && active.Any(u => u.CountsAsSetting && u.Scope == GpoScope.Computer))
        {
            work.Warn(IssueCodes.GpoComputerPartDisabled, "The computer configuration of the target GPO is disabled.");
        }

        if ((b.Status & GpoStatus.UserSettingsDisabled) != 0 && active.Any(u => u.CountsAsSetting && u.Scope == GpoScope.User))
        {
            work.Warn(IssueCodes.GpoUserPartDisabled, "The user configuration of the target GPO is disabled.");
        }

        var objects = new List<ObjectPlan> { await TargetObjectAsync(session, sysvol, work, cancellationToken).ConfigureAwait(false) };

        // The links: B takes A's place with A's options (B's link kept where it is linked already), A's link goes.
        foreach (var container in containers)
        {
            var linkA = container.Links.Find(a.Id)!;
            var linkB = container.Links.Find(b.Id);
            var after = linkB is null ? container.Links.Remove(a.Id).Insert(b.Id, linkA.Options, linkA.Order) : container.Links.Remove(a.Id);
            if (linkB is not null && (linkA.Order < linkB.Order || (linkA.Enforced && !linkB.Enforced) || (linkA.Enabled && !linkB.Enabled)))
            {
                Add(warnings, IssueSeverity.Warning, IssueCodes.GpoMergeLinkOptionsDiffer, "In this container the merged GPO ranks above the target or has a stronger link.",
                    a.DisplayName, b.DisplayName, container.Name);
            }

            var commands = new List<PowerShellCommand>();
            if (linkB is null)
            {
                commands.Add(new PowerShellCommand("New-GPLink",
                [
                    GuidParameter(b.Id), PowerShellParameter.Literal("Target", container.Dn),
                    PowerShellParameter.Literal("LinkEnabled", linkA.Enabled ? "Yes" : "No"),
                    PowerShellParameter.Literal("Enforced", linkA.Enforced ? "Yes" : "No"),
                    PowerShellParameter.Number("Order", linkA.Order), Domain(session), PowerShellParameter.Server,
                ]));
            }

            commands.Add(new PowerShellCommand("Remove-GPLink", [GuidParameter(a.Id), PowerShellParameter.Literal("Target", container.Dn), Domain(session), PowerShellParameter.Server]));
            objects.Add(ContainerObject(container, [Modify(GpLinkChange(container, after))], new PowerShellEquivalent(commands)));
        }

        // Scope differences (names only, never values).
        var onlyA = linksA.Where(l => !linksB.Any(x => DistinguishedName.AreEqual(x.ContainerDn, l.ContainerDn))).Select(l => l.ContainerName).Distinct().ToList();
        var onlyB = linksB.Where(l => !linksA.Any(x => DistinguishedName.AreEqual(x.ContainerDn, l.ContainerDn))).Select(l => l.ContainerName).Distinct().ToList();
        if (onlyA.Count > 0 || onlyB.Count > 0)
        {
            Add(warnings, IssueSeverity.Warning, IssueCodes.GpoMergeLinksDiffer, "The two GPOs are linked in different places.", a.DisplayName, b.DisplayName,
                string.Join(", ", onlyA), string.Join(", ", onlyB));
        }

        if (!SameFilter(a.SecurityFilter, b.SecurityFilter))
        {
            Add(warnings, IssueSeverity.Warning, IssueCodes.GpoMergeSecurityFilterDiffers, "The two GPOs have different security filtering.", a.DisplayName, b.DisplayName);
        }

        if (!string.Equals(a.WmiFilterId ?? string.Empty, b.WmiFilterId ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            Add(warnings, IssueSeverity.Warning, IssueCodes.GpoMergeWmiFilterDiffers, "The two GPOs have different WMI filters.", a.DisplayName, b.DisplayName);
        }

        var otherDomains = await _reader.ReadOtherDomainsAsync(session, cancellationToken).ConfigureAwait(false);
        if (otherDomains.Count > 0)
        {
            Add(warnings, IssueSeverity.Warning, IssueCodes.GpoLinkedElsewhere,
                $"The forest has other domains ({string.Join(", ", otherDomains)}): their links to the merged GPO are not replaced.", string.Join(", ", otherDomains));
        }

        // A: all settings disabled.
        var flags = (a.Flags & ~3) | (int)GpoStatus.AllSettingsDisabled;
        var disable = flags == a.Flags
            ? Modify()
            : Modify(Replace(GroupPolicyAttributes.Flags, a.Flags.ToString(CultureInfo.InvariantCulture), flags.ToString(CultureInfo.InvariantCulture)));
        objects.Add(PolicyObject(a, [disable], PowerShellEquivalent.Of(new PowerShellCommand("Get-GPO", [GuidParameter(a.Id), Domain(session), PowerShellParameter.Server])
        {
            Property = "GpoStatus",
            PropertyValue = nameof(GpoStatus.AllSettingsDisabled),
        })) with
        {
            Issues = [.. DistinctIssues(errors), .. DistinctIssues(warnings)],
        });
        return new GpoSettingsPlan(Plan(session, objects, title), outcomes);
    }

    /// <summary>Removes every link of the GPO in this domain (domain head, OUs, sites): one gPLink compare-and-swap per container.</summary>
    public async Task<ChangePlan> PlanUnlinkAllAsync(IDirectorySession session, Guid gpoId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var gpo = await RequirePolicyAsync(session, gpoId, cancellationToken).ConfigureAwait(false);
        var links = await _reader.ReadLinksOfAsync(session, gpoId, cancellationToken).ConfigureAwait(false);
        var otherDomains = await _reader.ReadOtherDomainsAsync(session, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ValidationIssue> elsewhere = otherDomains.Count == 0
            ? []
            : [new ValidationIssue(IssueSeverity.Warning, IssueCodes.GpoLinkedElsewhere,
                $"The forest has other domains ({string.Join(", ", otherDomains)}): links to this GPO from their OUs are not found or removed.")
            {
                Arguments = [string.Join(", ", otherDomains)],
            }];
        var objects = new List<ObjectPlan>();
        foreach (var containerDn in links.Select(l => l.ContainerDn).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var container = await _reader.ReadContainerAsync(session, containerDn, cancellationToken).ConfigureAwait(false);
            if (container is null || !container.Links.Contains(gpoId))
            {
                continue;
            }

            objects.Add(ContainerObject(container, [Modify(GpLinkChange(container, container.Links.Remove(gpoId))) with { Issues = objects.Count == 0 ? elsewhere : [] }],
                PowerShellEquivalent.Of(PowerShellCommand.Of("Remove-GPLink", GuidParameter(gpoId), PowerShellParameter.Literal("Target", container.Dn),
                    Domain(session), PowerShellParameter.Server))));
        }

        return Plan(session, objects, $"Unlink GPO {gpo.DisplayName} everywhere");
    }

    /// <summary>
    /// <see cref="PlanDeleteAsync(IDirectorySession, Guid, CancellationToken)"/> guarded by what the cleanup advisor saw: an
    /// error <see cref="IssueCodes.GpoChangedSinceAnalysis"/> when the GPO's version or links moved since.
    /// </summary>
    public async Task<ChangePlan> PlanDeleteAsync(IDirectorySession session, Guid gpoId, GpoExpectedState expected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(expected);
        var gpo = await RequirePolicyAsync(session, gpoId, cancellationToken).ConfigureAwait(false);
        var links = await _reader.ReadLinksOfAsync(session, gpoId, cancellationToken).ConfigureAwait(false);
        var plan = await PlanDeleteAsync(session, gpoId, cancellationToken).ConfigureAwait(false);
        var linked = links.Select(l => l.ContainerDn).ToList();
        var sameLinks = linked.All(dn => expected.LinkedContainerDns.Any(e => DistinguishedName.AreEqual(e, dn)))
            && expected.LinkedContainerDns.All(e => linked.Any(dn => DistinguishedName.AreEqual(e, dn)));
        if (gpo.DirectoryVersion == expected.DirectoryVersion && sameLinks)
        {
            return plan;
        }

        var changed = new ValidationIssue(IssueSeverity.Error, IssueCodes.GpoChangedSinceAnalysis,
            "The GPO changed (version or links) since the cleanup analysis.") { Arguments = [gpo.DisplayName] };
        return plan with { Objects = [.. plan.Objects.Select(o => o with { Issues = [.. o.Issues, changed] })] };
    }

    // ---- The shared work ------------------------------------------------------------------------------------------------

    /// <summary>Reads the target and the sources now (folders, settings, principal names in one batch, units).</summary>
    private async Task<SettingsWork> LoadAsync(IDirectorySession session, ISysvolWriteConnection sysvol, GpoInfo target, IReadOnlyList<Guid> sourceIds,
        PolicyDefinitionSet? definitions, CancellationToken cancellationToken)
    {
        var units = UnitSource;
        var head = await session.GetAsync(session.Info.DefaultNamingContext, [Attr.ObjectSid], cancellationToken).ConfigureAwait(false);
        var domainSid = head?.ObjectSid;
        var targetContent = await GroupPolicyReader.ReadFolderAsync(sysvol, target.Id, cancellationToken).ConfigureAwait(false);
        var targetRead = units.ReadSettings(targetContent);
        var sources = new Dictionary<Guid, (GpoInfo? Info, GpoFolderContent? Content, GpoSettingsRead? Read)>();
        foreach (var id in sourceIds)
        {
            var info = await _reader.ReadPolicyAsync(session, id, cancellationToken).ConfigureAwait(false);
            if (info is null)
            {
                sources[id] = (null, null, null);
                continue;
            }

            var content = await GroupPolicyReader.ReadFolderAsync(sysvol, id, cancellationToken).ConfigureAwait(false);
            sources[id] = (info, content, units.ReadSettings(content));
        }

        // Names of every side in one batch, so that a group written by name in one GPO and by SID in another is one unit.
        var names = new HashSet<string>(units.PrincipalNames(targetRead.Settings, domainSid), StringComparer.OrdinalIgnoreCase);
        foreach (var (_, _, read) in sources.Values)
        {
            if (read is not null)
            {
                names.UnionWith(units.PrincipalNames(read.Settings, domainSid));
            }
        }

        var sids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (names.Count > 0)
        {
            foreach (var principal in await units.ResolveAsync(session, names, domainSid, cancellationToken).ConfigureAwait(false))
            {
                if (principal.Sid is { } sid)
                {
                    sids[principal.Written] = sid;
                }
            }
        }

        var context = new GpoUnitContext(definitions, domainSid) { SidsByName = sids };
        string? SidOf(string principal) => units.KnownSid(principal, domainSid) ?? (sids.TryGetValue(principal, out var sid) ? sid : null);
        var work = new SettingsWork
        {
            Target = target,
            TargetContent = targetContent,
            TargetUnits = units.Units(targetContent, context),
            TargetIncomplete = !targetRead.Complete || targetContent.Leftovers.Count > 0,
            Definitions = definitions,
            Writer = new GpoSettingsWriter(targetContent, definitions, SidOf, _time.GetLocalNow().DateTime),
            SameValue = units.SameValue,
            TargetLinkedToDomain = (await _reader.ReadLinksOfAsync(session, target.Id, cancellationToken).ConfigureAwait(false))
                .Any(l => l.Kind == GroupPolicySomKind.Domain),
        };
        foreach (var (id, (info, content, read)) in sources)
        {
            work.Sources[id] = new SourceRead(info, content, info is null ? [] : units.Units(content!, context),
                read is not null && (!read.Complete || content!.Leftovers.Count > 0));
        }

        return work;
    }

    /// <summary>Copies one selected unit into the target of <paramref name="work"/> and says what happened.</summary>
    private static GpoUnitOutcome CopyOne(SettingsWork work, GpoUnitSelection selection, GpoSettingUnit? unit, IReadOnlySet<string> ambiguous,
        GpoCopyConflictMode mode, bool merge)
    {
        var target = work.Target;
        GpoUnitOutcome Outcome(GpoUnitOutcomeKind kind, string? code = null) => new(selection, kind, code);
        if (selection.GpoId == target.Id)
        {
            work.Warn(IssueCodes.GpoSettingUnsupported, "A setting cannot be copied into its own GPO.", target.DisplayName, selection.UnitId);
            return Outcome(GpoUnitOutcomeKind.Refused, IssueCodes.GpoSettingUnsupported);
        }

        if (!work.Sources.TryGetValue(selection.GpoId, out var source) || source.Info is null)
        {
            work.Warn(IssueCodes.GpoSettingNotFound, "The source GPO no longer exists.", SysvolPaths.FolderName(selection.GpoId), selection.UnitId);
            return Outcome(GpoUnitOutcomeKind.NotFound, IssueCodes.GpoSettingNotFound);
        }

        var sourceName = source.Info.DisplayName;
        if (source.Incomplete)
        {
            work.Warn(IssueCodes.GpoSettingIncomplete, "Not every setting of the source GPO could be read.", sourceName);
            return Outcome(GpoUnitOutcomeKind.Refused, IssueCodes.GpoSettingIncomplete);
        }

        if (unit is null && work.Definitions is null && IsPolicyId(selection.UnitId))
        {
            work.Warn(IssueCodes.GpoSettingNoDefinitions, "The administrative template needs the ADMX templates.", sourceName, selection.UnitId);
            return Outcome(GpoUnitOutcomeKind.Refused, IssueCodes.GpoSettingNoDefinitions);
        }

        if (unit is null)
        {
            work.Warn(IssueCodes.GpoSettingNotFound, "The setting is no longer in the source GPO as it was found.", sourceName, selection.UnitId);
            return Outcome(GpoUnitOutcomeKind.NotFound, IssueCodes.GpoSettingNotFound);
        }

        var key = TechnicalKey(unit);
        if (ambiguous.Contains(unit.BaseId))
        {
            work.Warn(IssueCodes.GpoSettingAmbiguous, "The same setting was selected from two source GPOs.", key);
            return Outcome(GpoUnitOutcomeKind.Refused, IssueCodes.GpoSettingAmbiguous);
        }

        var refusal = RefusalCode(unit)
            ?? (unit.Kind == GpoSettingUnitKind.AdministrativeTemplate && work.Definitions is null ? IssueCodes.GpoSettingNoDefinitions : null)
            ?? work.Writer.CheckCopy(unit, source.Content!);
        List<(bool User, Guid Extension, Guid Tool)>? pairs = null;
        if (refusal is null)
        {
            pairs = ExtensionPairs(unit, source.Info, target);
            refusal = pairs is null ? IssueCodes.GpoSettingExtensionUnknown : null;
        }

        if (refusal is not null)
        {
            // "{1} belongs to an administrative template {0} configures": the policy is the target's.
            work.Warn(refusal, "The setting is not copied.", refusal == IssueCodes.GpoSettingPolicyOwned ? target.DisplayName : sourceName, key);
            return Outcome(GpoUnitOutcomeKind.Refused, refusal);
        }

        if (PartDisabled(source.Info, unit.Scope))
        {
            if (merge)
            {
                work.Info(IssueCodes.GpoSettingSourcePartDisabled, "The setting comes from a disabled part of the merged GPO and is left out.", sourceName, key);
                return Outcome(GpoUnitOutcomeKind.Skipped, IssueCodes.GpoSettingSourcePartDisabled);
            }

            work.Warn(IssueCodes.GpoSettingSourcePartDisabled, "The setting comes from a disabled part of the source GPO.", sourceName, key);
        }

        if (selection.ValueFingerprint is { } seen && !string.Equals(seen, unit.ValueFingerprint, StringComparison.Ordinal))
        {
            work.Warn(IssueCodes.GpoSettingChanged, "The value changed since the search; the value read now is copied.", sourceName, key);
        }

        if (unit.Kind is not (GpoSettingUnitKind.UserRight or GpoSettingUnitKind.RestrictedGroup) && mode == GpoCopyConflictMode.Union)
        {
            mode = GpoCopyConflictMode.Replace;
        }

        var first = work.Handled.Add(unit.BaseId);
        var occurrences = work.TargetUnits.Where(u => u.BaseId.Equals(unit.BaseId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (occurrences.Count > 0 && mode == GpoCopyConflictMode.Skip)
        {
            work.Info(IssueCodes.GpoSettingSkipped, "The target already sets the setting; it is kept.", target.DisplayName, key);
            return Outcome(GpoUnitOutcomeKind.Skipped, IssueCodes.GpoSettingSkipped);
        }

        if (first && occurrences.Count == 1 && work.SameValue(unit, occurrences[0]))
        {
            return Outcome(GpoUnitOutcomeKind.Unchanged);
        }

        var removed = 0;
        var kept = 0;
        if (mode == GpoCopyConflictMode.Replace && first)
        {
            var replaced = unit.Kind == GpoSettingUnitKind.Preference
                ? occurrences.Where(o => string.Equals(o.FiltersFingerprint, unit.FiltersFingerprint, StringComparison.Ordinal)).ToList()
                : occurrences;
            if (replaced.Any(o => o.Refusal is not null || work.Writer.CheckRemove(o) is not null))
            {
                work.Warn(IssueCodes.GpoSettingUnsupported, "The target's setting cannot be replaced by AD Manager.", target.DisplayName, key);
                return Outcome(GpoUnitOutcomeKind.Refused, IssueCodes.GpoSettingUnsupported);
            }

            foreach (var occurrence in replaced)
            {
                if (occurrence.RemovePolicy)
                {
                    work.Warn(IssueCodes.GpoSettingRemovePolicy, "Clients undo the replaced preference item before the copy applies.", target.DisplayName, key);
                }

                work.Writer.Remove(occurrence);
                removed++;
            }

            kept = occurrences.Count - replaced.Count;
            if (kept > 0)
            {
                work.Info(IssueCodes.GpoSettingConditionalKept, "Variants with other item-level targeting are kept.", target.DisplayName, key);
            }
        }

        var union = mode == GpoCopyConflictMode.Union && occurrences.Count > 0;
        work.Writer.Copy(unit, source.Content!, union);
        work.Pairs.AddRange(pairs!);
        if (unit.Kind == GpoSettingUnitKind.SecurityValue && !work.TargetLinkedToDomain)
        {
            var info = GpoSettingKey.Parse(unit.Settings.Count > 0 ? unit.Settings[0].Key : unit.BaseId);
            if (info.Area == GpoSettingArea.KerberosPolicy)
            {
                work.Warn(IssueCodes.GpoKerberosPolicyBelowDomain, "Kerberos policy has no effect outside the domain head.", target.DisplayName);
            }
            else if (info.Area == GpoSettingArea.SystemAccess && info.Name is { } name && GroupPolicyModeler.AccountPolicyNames.Contains(name))
            {
                work.Warn(IssueCodes.GpoAccountPolicyBelowDomain, "Account policy below the domain head applies to local accounts only.", target.DisplayName);
            }
        }

        return Outcome(union ? GpoUnitOutcomeKind.Merged : removed > 0 && kept == 0 ? GpoUnitOutcomeKind.Replaced : GpoUnitOutcomeKind.Added);
    }

    /// <summary>
    /// The target's object: the extension names of every file a copy wrote into, one WriteGpoFile per changed file (old
    /// content = the bytes read now) and one version change; the issues of the planning on the object.
    /// </summary>
    private async Task<ObjectPlan> TargetObjectAsync(IDirectorySession session, ISysvolWriteConnection sysvol, SettingsWork work, CancellationToken cancellationToken)
    {
        var target = work.Target;
        var files = work.Writer.ChangedFiles();
        var machineNames = target.MachineExtensionNames;
        var userNames = target.UserExtensionNames;
        void Pair(bool user, Guid extension, Guid tool)
        {
            if (user)
            {
                userNames = GpoExtensionNames.With(userNames, extension, tool);
            }
            else
            {
                machineNames = GpoExtensionNames.With(machineNames, extension, tool);
            }
        }

        foreach (var path in work.Writer.CopiedPaths.Where(p => files.Any(f => f.Path.Equals(p, StringComparison.OrdinalIgnoreCase))))
        {
            foreach (var requirement in KnownExtensionFiles.Where(r => r.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                Pair(requirement.User, requirement.Extension, requirement.Tool);
            }

            var user = path.Equals(UserRegistryPolicyPath, StringComparison.OrdinalIgnoreCase);
            if ((user || path.Equals(MachineRegistryPolicyPath, StringComparison.OrdinalIgnoreCase)) && work.Definitions is { } set && work.Writer.Registry(path) is { } document)
            {
                var tool = user ? GpoExtensionNames.AdministrativeTemplatesUserTool : GpoExtensionNames.AdministrativeTemplatesMachineTool;
                foreach (var extension in PolicyClientExtensions.Of(document, set, user ? PolicyClass.User : PolicyClass.Machine, PolicyOwnershipIndex.Build(set))
                    .Where(e => e != Guid.Empty && e != GpoExtensionNames.Registry).OrderBy(e => e))
                {
                    Pair(user, extension, tool);
                }
            }
        }

        foreach (var (user, extension, tool) in work.Pairs.Distinct())
        {
            Pair(user, extension, tool);
        }

        var attributes = new List<AttributeChange>();
        AddIfChanged(attributes, GroupPolicyAttributes.MachineExtensionNames, target.MachineExtensionNames, machineNames);
        AddIfChanged(attributes, GroupPolicyAttributes.UserExtensionNames, target.UserExtensionNames, userNames);
        var steps = new List<PlannedChange>();
        if (files.Count == 0 && attributes.Count == 0)
        {
            return PolicyObject(target, [], null) with { Issues = work.Issues() };
        }

        // The extensions first: a client that sees them before the new files reads the old ones, never the other way round.
        if (attributes.Count > 0)
        {
            steps.Add(Modify([.. attributes]));
        }

        foreach (var (path, old, next) in files)
        {
            steps.Add(new PlannedChange
            {
                Action = ChangeAction.WriteGpoFile,
                Sysvol = new SysvolChange { GpoId = target.Id, RelativePath = path, OldContent = old, NewContent = next },
            });
        }

        var computer = files.Any(f => IsBelow(f.Path, "Machine")) || attributes.Any(a => a.Attribute == GroupPolicyAttributes.MachineExtensionNames);
        var userPart = files.Any(f => IsBelow(f.Path, "User")) || attributes.Any(a => a.Attribute == GroupPolicyAttributes.UserExtensionNames);
        var versionIssues = new List<ValidationIssue>();
        if (computer && (target.Status & GpoStatus.ComputerSettingsDisabled) != 0)
        {
            versionIssues.Add(new ValidationIssue(IssueSeverity.Warning, IssueCodes.GpoComputerPartDisabled,
                "The computer configuration of this GPO is disabled: clients apply the change only after it is enabled."));
        }

        if (userPart && (target.Status & GpoStatus.UserSettingsDisabled) != 0)
        {
            versionIssues.Add(new ValidationIssue(IssueSeverity.Warning, IssueCodes.GpoUserPartDisabled,
                "The user configuration of this GPO is disabled: clients apply the change only after it is enabled."));
        }

        var gpt = await GptVersionAsync(sysvol, target.Id, cancellationToken).ConfigureAwait(false);
        var version = VersionChange(target.DirectoryVersion, gpt, user: userPart, computer: computer);
        steps.Add(version with { Issues = [.. version.Issues, .. versionIssues.Where(v => !work.HasIssue(v.Code))] });

        var commands = new List<PowerShellCommand>
        {
            new("Get-GPO", [GuidParameter(target.Id), Domain(session), PowerShellParameter.Server])
            {
                Comment = "Settings of GptTmpl.inf, audit.csv and preference files have no GroupPolicy cmdlet: this plan writes the files on SYSVOL.",
            },
        };
        foreach (var (path, old, next) in files)
        {
            if (path.Equals(MachineRegistryPolicyPath, StringComparison.OrdinalIgnoreCase) || path.Equals(UserRegistryPolicyPath, StringComparison.OrdinalIgnoreCase))
            {
                commands.AddRange(RegistryCommands(session, target.Id, path.Equals(UserRegistryPolicyPath, StringComparison.OrdinalIgnoreCase) ? "HKCU" : "HKLM",
                    new RegistryPolicySave { Original = old, Content = next }));
            }
        }

        return PolicyObject(target, steps, new PowerShellEquivalent(commands)) with { Issues = work.Issues() };
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------------

    /// <summary>The unit a selection names in fresh units: by id, and for preference items and repeated units by item id too.</summary>
    private static GpoSettingUnit? FindUnit(IReadOnlyList<GpoSettingUnit> units, GpoUnitSelection selection)
    {
        var unit = units.FirstOrDefault(u => u.Id.Equals(selection.UnitId, StringComparison.OrdinalIgnoreCase));
        if (unit is null)
        {
            return null;
        }

        return (unit.Kind == GpoSettingUnitKind.Preference || unit.Occurrence > 1)
            && !string.Equals(unit.ItemId ?? string.Empty, selection.ItemId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            ? null
            : unit;
    }

    /// <summary>A unit id of an administrative template (<c>P|Machine|…</c>, <c>P|User|…</c>): without a definition set such units do not exist.</summary>
    private static bool IsPolicyId(string unitId) => unitId.StartsWith("P|", StringComparison.OrdinalIgnoreCase);

    private static string? RefusalCode(GpoSettingUnit unit) => unit.Refusal switch
    {
        null => null,
        GpoUnitRefusal.Secret => IssueCodes.GpoSettingSecret,
        _ => IssueCodes.GpoSettingUnsupported,
    };

    /// <summary>The unit's technical key for issue texts: the policy id, else its first setting key (never a value).</summary>
    private static string TechnicalKey(GpoSettingUnit unit) =>
        unit.Kind == GpoSettingUnitKind.AdministrativeTemplate && unit.PolicyId is { } id ? id
        : unit.Settings.Count > 0 ? (GpoSettingKey.Parse(unit.Settings[0].Key) is { ItemKey: { } item } ? item : unit.Settings[0].Key)
        : unit.BaseId;

    private static bool PartDisabled(GpoInfo gpo, GpoScope scope) => scope switch
    {
        GpoScope.Computer => (gpo.Status & GpoStatus.ComputerSettingsDisabled) != 0,
        GpoScope.User => (gpo.Status & GpoStatus.UserSettingsDisabled) != 0,
        _ => false,
    };

    /// <summary>
    /// The extension pairs a copied unit's file needs beyond <see cref="KnownExtensionFiles"/> and the Registry pairs (which
    /// the target object adds per written file): for a preference file other than the machine's Groups.xml the CSE of its
    /// type with the tools the source lists for it (and for the preference core entry). Null: the pairs cannot be known (an
    /// unknown type, or a source and a target that do not list the CSE) – the unit is refused.
    /// </summary>
    private static List<(bool User, Guid Extension, Guid Tool)>? ExtensionPairs(GpoSettingUnit unit, GpoInfo source, GpoInfo target)
    {
        if (unit.Kind != GpoSettingUnitKind.Preference)
        {
            return [];
        }

        var path = GpoSettingsWriter.PreferencePath(unit);
        if (path.Equals(GppGroupsDocument.RelativePath, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var type = unit.PreferenceType ?? GpoSettingKey.Parse(unit.Settings.Count > 0 ? unit.Settings[0].Key : string.Empty).PreferenceType;
        if (GpmcBackupFormat.TryPreferenceExtension(type) is not { } extension)
        {
            return null;
        }

        var user = unit.Scope == GpoScope.User;
        var sourcePairs = GpoExtensionNames.Parse(user ? source.UserExtensionNames : source.MachineExtensionNames) ?? [];
        var tools = sourcePairs.Where(p => p.Extension == extension).SelectMany(p => p.Tools).Distinct().ToList();
        if (tools.Count == 0)
        {
            var targetPairs = GpoExtensionNames.Parse(user ? target.UserExtensionNames : target.MachineExtensionNames) ?? [];
            return targetPairs.Any(p => p.Extension == extension) ? [] : null;
        }

        var core = sourcePairs.Where(p => p.Extension == GpoExtensionNames.PreferencesCore).SelectMany(p => p.Tools).ToHashSet();
        var result = tools.Select(t => (user, extension, t)).ToList();
        result.AddRange(tools.Where(core.Contains).Select(t => (user, GpoExtensionNames.PreferencesCore, t)));
        return result;
    }

    /// <summary>A file below Machine/ or User/ that no parser reads (folder redirection, software installation …).</summary>
    private static bool IsUnparsedFile(string path) =>
        (IsBelow(path, "Machine") || IsBelow(path, "User")) && GpoFileClassifier.Classify(path).Role == GpoFileRole.Inventory;

    private static bool SameFilter(GroupPolicySecurityFilter? a, GroupPolicySecurityFilter? b) =>
        a is null || b is null ? a is null && b is null : a.Allowed.SetEquals(b.Allowed) && a.Denied.SetEquals(b.Denied);

    private static IEnumerable<ValidationIssue> DistinctIssues(IEnumerable<ValidationIssue> issues) =>
        issues.DistinctBy(i => (i.Severity, i.Code, string.Join("\n", i.Arguments)));

    /// <summary>A source GPO read now: null info when it no longer exists.</summary>
    private sealed record SourceRead(GpoInfo? Info, GpoFolderContent? Content, IReadOnlyList<GpoSettingUnit> Units, bool Incomplete)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Info} {Units.Count} units");
    }

    /// <summary>One plan's reads and edits of its target GPO (in memory, for the planning only).</summary>
    private sealed class SettingsWork
    {
        private readonly List<ValidationIssue> _issues = [];

        public required GpoInfo Target { get; init; }

        public required GpoFolderContent TargetContent { get; init; }

        public required IReadOnlyList<GpoSettingUnit> TargetUnits { get; init; }

        public required bool TargetIncomplete { get; init; }

        public required PolicyDefinitionSet? Definitions { get; init; }

        public required GpoSettingsWriter Writer { get; init; }

        public required Func<GpoSettingUnit, GpoSettingUnit, bool> SameValue { get; init; }

        public required bool TargetLinkedToDomain { get; init; }

        public Dictionary<Guid, SourceRead> Sources { get; } = [];

        /// <summary>Base ids whose target occurrences were dealt with (a second occurrence from the same source only adds).</summary>
        public HashSet<string> Handled { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<(bool User, Guid Extension, Guid Tool)> Pairs { get; } = [];

        public GpoSettingUnit? Find(GpoUnitSelection selection) =>
            Sources.TryGetValue(selection.GpoId, out var source) ? FindUnit(source.Units, selection) : null;

        public void Error(string code, string message, params string[] arguments) => Add(IssueSeverity.Error, code, message, arguments);

        public void Warn(string code, string message, params string[] arguments) => Add(IssueSeverity.Warning, code, message, arguments);

        public void Info(string code, string message, params string[] arguments) => Add(IssueSeverity.Info, code, message, arguments);

        public bool HasIssue(string code) => _issues.Any(i => i.Code == code);

        public IReadOnlyList<ValidationIssue> Issues() => [.. _issues];

        private void Add(IssueSeverity severity, string code, string message, string[] arguments)
        {
            if (!_issues.Any(i => i.Severity == severity && i.Code == code && i.Arguments.SequenceEqual(arguments, StringComparer.Ordinal)))
            {
                _issues.Add(new ValidationIssue(severity, code, message) { Arguments = arguments });
            }
        }

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Target} {TargetUnits.Count} units, {Sources.Count} sources");
    }
}
