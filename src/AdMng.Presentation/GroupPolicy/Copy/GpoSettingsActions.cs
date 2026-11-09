using System.Text;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.Ldap;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.GroupPolicy.Copy;

/// <summary>What the cleanup advisor's guarded delete did.</summary>
internal enum GpoCheckedDeleteResult
{
    /// <summary>Nothing was deleted (cancelled, refused or failed).</summary>
    NotDeleted,

    /// <summary>The GPO was deleted.</summary>
    Deleted,

    /// <summary>The GPO changed (version or links) since the analysis: refused, the advisor reads the GPOs again.</summary>
    ChangedSinceAnalysis,
}

/// <summary>
/// K7 (docs/dev/k-computers-and-gpo.md 21.5, 21.6): copy, remove, merge, unlink-all and the advisor's guarded delete, each
/// a plan made on the session's DC through <c>GroupPolicyActions.RunAsync</c> and shown in the change plan preview, which
/// executes it and offers undo (rule 5). The items carry everything the windows show, so the search and the cleanup
/// document need no shared state. Plans that would change nothing (every unit refused, skipped or unchanged) are not
/// previewed: an error dialog lists the units and why – the MS14-025 refusal is never swallowed. Copy, remove and merge
/// plan with <c>definitions</c>, the index's definition set (<c>IGpoSettingsIndexService.Current.Index.Definitions</c>),
/// never a fresh read of the templates.
/// </summary>
public static class GpoSettingsActions
{
    /// <summary>
    /// "Kopiuj do innego GPO…": the target (picked when <paramref name="target"/> is null; the sources are not offered),
    /// the "Kopiuj ustawienia" window (settings, conflict mode, at most <see cref="GpoSettingsCopyRequest.MaxSources"/>
    /// source GPOs), then one plan for the target through the preview. True when something was written.
    /// </summary>
    /// <param name="actions">The window's operations.</param>
    /// <param name="items">The settings offered (refused ones are shown disabled).</param>
    /// <param name="target">The target GPO (null: the GPO picker).</param>
    /// <param name="definitions">The index's definition set (null: administrative template units are refused).</param>
    public static async Task<bool> CopySettingsAsync(this GroupPolicyActions actions, IReadOnlyList<GpoCopyItem> items, GpoInfo? target = null,
        PolicyDefinitionSet? definitions = null)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return false;
        }

        var services = actions.Services;
        var localizer = services.Localizer;
        if (target is null)
        {
            using var picker = new GpoPickerViewModel(services.Ui, services.Reader, localizer[L.GpoCopy.PickTargetTitle], multiSelect: false,
                items.Select(i => i.Unit.GpoId).Distinct());
            if (!await services.Dialogs.ShowGpoPickerAsync(picker).ConfigureAwait(true) || picker.Result is not [var picked, ..])
            {
                return false;
            }

            target = picked;
        }

        // A GPO is never its own source.
        var offered = items.Where(i => i.Unit.GpoId != target.Id).ToList();
        if (offered.Count == 0)
        {
            return false;
        }

        IReadOnlyList<GpoCopyItem> chosen;
        GpoCopyConflictMode conflict;
        using (var dialog = new GpoCopySettingsViewModel(services.Ui, offered, target.DisplayName))
        {
            if (!await services.Dialogs.ShowCopySettingsAsync(dialog).ConfigureAwait(true) || !dialog.Confirmed || dialog.SelectedItems.Count == 0)
            {
                return false;
            }

            chosen = dialog.SelectedItems;
            conflict = dialog.Conflict ?? GpoCopyConflictMode.Replace;
        }

        var request = new GpoSettingsCopyRequest
        {
            TargetId = target.Id,
            Units = [.. chosen.Select(i => i.Unit)],
            Conflict = conflict,
            Definitions = definitions,
        };
        var title = localizer.Format(L.GpoCopy.TitleCopy, target.DisplayName);
        GpoSettingsPlan? planned = null;
        return await actions.RunAsync(title, GroupPolicyActions.SysvolUse.Required, async (session, sysvol, token) =>
            {
                planned = await services.Planner.PlanCopySettingsAsync(session, sysvol!, request, token).ConfigureAwait(true);
                return planned.Plan;
            },
            describeChanges: (plan, session, token) => DescribeAsync(actions, planned, chosen, plan, session, definitions, title,
                localizer.Format(L.GpoCopy.NothingCopied, target.DisplayName), mergeLike: false, token)).ConfigureAwait(true);
    }

    /// <summary>"Usuń duplikat z tego GPO": the items' units removed from <paramref name="gpo"/> (one plan, one version bump).</summary>
    public static async Task<bool> RemoveSettingsAsync(this GroupPolicyActions actions, GpoInfo gpo, IReadOnlyList<GpoCopyItem> items,
        PolicyDefinitionSet? definitions = null)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(gpo);
        ArgumentNullException.ThrowIfNull(items);
        var own = items.Where(i => i.Unit.GpoId == gpo.Id).ToList();
        if (own.Count == 0)
        {
            return false;
        }

        var services = actions.Services;
        var localizer = services.Localizer;
        var title = localizer.Format(L.GpoCopy.TitleRemove, gpo.DisplayName);
        GpoSettingsPlan? planned = null;
        return await actions.RunAsync(title, GroupPolicyActions.SysvolUse.Required, async (session, sysvol, token) =>
            {
                planned = await services.Planner.PlanRemoveSettingsAsync(session, sysvol!, gpo.Id, [.. own.Select(i => i.Unit)], definitions, token)
                    .ConfigureAwait(true);
                return planned.Plan;
            },
            describeChanges: (plan, session, token) => DescribeAsync(actions, planned, own, plan, session, definitions, title,
                localizer.Format(L.GpoCopy.NothingRemoved, gpo.DisplayName), mergeLike: false, token)).ConfigureAwait(true);
    }

    /// <summary>
    /// "Scal z…": the merge window (A, B, the conflict mode of the link order or the operator's choice when it is mixed, the
    /// settings that block the merge, the differences of filtering, WMI filter and links), then one sequential plan – B
    /// receives A's settings, A's links are replaced by B, A is set to all settings disabled. Deleting A is a separate step.
    /// </summary>
    public static async Task<bool> MergeAsync(this GroupPolicyActions actions, GpoInfo source, GpoInfo target, IReadOnlyList<GpoCopyItem> sourceItems,
        PolicyDefinitionSet? definitions = null)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(sourceItems);
        var services = actions.Services;
        var localizer = services.Localizer;
        var title = localizer.Format(L.GpoCopy.TitleMerge, source.DisplayName, target.DisplayName);
        if (services.Sessions.Current is not { } current)
        {
            await services.Ui.Dialogs.ShowErrorAsync(title, localizer[L.Errors.NotConnected]).ConfigureAwait(true);
            return false;
        }

        GpoMergeInfo info;
        try
        {
            info = await MergeInfoAsync(services, current, source, target).ConfigureAwait(true);
        }
        catch (Exception ex) when (GroupPolicyText.IsExpected(ex))
        {
            await services.Ui.Dialogs.ShowErrorAsync(title, GroupPolicyText.Error(localizer, services.Errors, ex)).ConfigureAwait(true);
            return false;
        }

        GpoCopyConflictMode? conflict;
        using (var dialog = new GpoCopySettingsViewModel(services.Ui, [.. sourceItems.Where(i => i.Unit.GpoId == source.Id)], info))
        {
            if (!await services.Dialogs.ShowCopySettingsAsync(dialog).ConfigureAwait(true) || !dialog.Confirmed)
            {
                return false;
            }

            conflict = dialog.Conflict;
        }

        var request = new GpoMergeRequest { SourceId = source.Id, TargetId = target.Id, Conflict = conflict, Definitions = definitions };
        GpoSettingsPlan? planned = null;
        return await actions.RunAsync(title, GroupPolicyActions.SysvolUse.Required, async (session, sysvol, token) =>
            {
                planned = await services.Planner.PlanMergeAsync(session, sysvol!, request, token).ConfigureAwait(true);
                return planned.Plan;
            },
            describeChanges: (plan, session, token) => DescribeAsync(actions, planned, sourceItems, plan, session, definitions, title,
                localizer.Format(L.GpoCopy.NothingCopied, target.DisplayName), mergeLike: true, token)).ConfigureAwait(true);
    }

    /// <summary>"Odłącz wszędzie…": every link of the GPO in this domain removed (one gPLink compare-and-swap per container).</summary>
    public static Task<bool> UnlinkAllAsync(this GroupPolicyActions actions, GpoInfo gpo)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(gpo);
        return actions.RunAsync(actions.Services.Localizer.Format(L.GpoCopy.TitleUnlinkAll, gpo.DisplayName), GroupPolicyActions.SysvolUse.None,
            (session, _, token) => actions.Services.Planner.PlanUnlinkAllAsync(session, gpo.Id, token));
    }

    /// <summary>
    /// The advisor's "Usuń GPO…": refused when the GPO's version, status or links moved since the analysis (in another window or by
    /// another administrator), otherwise <see cref="GroupPolicyActions.DeleteAsync"/> with its backup offer and typed
    /// confirmation – its plan is guarded by <paramref name="expected"/> too, up to execution.
    /// </summary>
    public static async Task<bool> DeleteCheckedAsync(this GroupPolicyActions actions, GpoInfo gpo, GpoExpectedState expected) =>
        await DeleteCheckedCoreAsync(actions, gpo, expected).ConfigureAwait(true) == GpoCheckedDeleteResult.Deleted;

    /// <summary><see cref="DeleteCheckedAsync"/> telling a refusal for a changed GPO apart (the advisor then reads the GPOs again).</summary>
    internal static async Task<GpoCheckedDeleteResult> DeleteCheckedCoreAsync(GroupPolicyActions actions, GpoInfo gpo, GpoExpectedState expected)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(gpo);
        ArgumentNullException.ThrowIfNull(expected);
        var services = actions.Services;
        var localizer = services.Localizer;
        var title = localizer.Format(L.GroupPolicy.TitleDelete, gpo.DisplayName);
        if (services.Sessions.Current is not { } session)
        {
            await services.Ui.Dialogs.ShowErrorAsync(title, localizer[L.Errors.NotConnected]).ConfigureAwait(true);
            return GpoCheckedDeleteResult.NotDeleted;
        }

        bool changed;
        try
        {
            var fresh = await services.Reader.ReadPolicyAsync(session, gpo.Id, services.Lifetime).ConfigureAwait(true);
            IReadOnlyList<GpoLinkInfo> links = fresh is null ? [] : await services.Reader.ReadLinksOfAsync(session, gpo.Id, services.Lifetime).ConfigureAwait(true);
            changed = fresh is null || !expected.Matches(fresh, links);
        }
        catch (Exception ex) when (GroupPolicyText.IsExpected(ex))
        {
            await services.Ui.Dialogs.ShowErrorAsync(title, GroupPolicyText.Error(localizer, services.Errors, ex)).ConfigureAwait(true);
            return GpoCheckedDeleteResult.NotDeleted;
        }

        if (changed)
        {
            await services.Ui.Dialogs.ShowErrorAsync(title, localizer.Format(L.GpoCopy.ChangedSinceAnalysis, gpo.DisplayName)).ConfigureAwait(true);
            return GpoCheckedDeleteResult.ChangedSinceAnalysis;
        }

        return await actions.DeleteAsync(gpo, expected).ConfigureAwait(true) ? GpoCheckedDeleteResult.Deleted : GpoCheckedDeleteResult.NotDeleted;
    }

    // ---- Plumbing -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// After planning: a plan that changes no unit is not previewed – the dialog lists the units and why (null returned);
    /// otherwise the "Zmiany ustawień" rows. A merge relinks and disables A even when every unit is unchanged, so it is
    /// stopped only when its plan would do nothing at all.
    /// </summary>
    private static async Task<IReadOnlyList<GroupPolicySettingChange>?> DescribeAsync(GroupPolicyActions actions, GpoSettingsPlan? planned,
        IReadOnlyList<GpoCopyItem> items, ChangePlan plan, IDirectorySession session, PolicyDefinitionSet? definitions, string title, string heading,
        bool mergeLike, CancellationToken cancellationToken)
    {
        var services = actions.Services;
        if (planned is { ChangesAnything: false } && (!mergeLike || (plan.ExecutableCount == 0 && plan.ErrorCount == 0)))
        {
            await services.Ui.Dialogs.ShowErrorAsync(title, NothingText(services, planned, items, plan, heading)).ConfigureAwait(true);
            return null;
        }

        return await GpoSettingChangesBuilder.BuildAsync(plan, session, services.Localizer, definitions, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>The "nothing would change" text: one line per unit (name, GPO, short reason), then the plan's issues in full.</summary>
    internal static string NothingText(GroupPolicyServices services, GpoSettingsPlan planned, IReadOnlyList<GpoCopyItem> items, ChangePlan plan, string heading)
    {
        var localizer = services.Localizer;
        var text = new StringBuilder(heading);
        text.AppendLine();
        foreach (var outcome in planned.Outcomes)
        {
            var item = items.FirstOrDefault(i => i.Unit.GpoId == outcome.Unit.GpoId && string.Equals(i.Unit.UnitId, outcome.Unit.UnitId, StringComparison.OrdinalIgnoreCase));
            var reason = OutcomeReason(localizer, outcome, item);
            text.AppendLine().Append("• ").Append(localizer.Format(L.GpoCopy.OutcomeLine, item?.Name ?? localizer[L.GpoCopy.NoSettings],
                item?.SourceGpoName ?? string.Empty, reason));
        }

        var issues = plan.Objects.SelectMany(o => o.Issues.Concat(o.Changes.SelectMany(c => c.Issues)))
            .Select(i => services.Errors.TranslateIssue(i))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (issues.Count > 0)
        {
            text.AppendLine();
            foreach (var issue in issues)
            {
                text.AppendLine().Append(issue);
            }
        }

        return text.ToString();
    }

    /// <summary>Why one unit changes nothing: its refusal (the MS14-025 one included), else the outcome.</summary>
    private static string OutcomeReason(ILocalizer localizer, GpoUnitOutcome outcome, GpoCopyItem? item)
    {
        if (outcome.Kind == GpoUnitOutcomeKind.Refused)
        {
            if (string.Equals(outcome.IssueCode, IssueCodes.GpoSettingSecret, StringComparison.Ordinal))
            {
                return GpoUnitRefusalText.Text(localizer, Core.GroupPolicy.Search.GpoUnitRefusal.Secret);
            }

            if (item?.Refusal is { } refusal)
            {
                return GpoUnitRefusalText.Text(localizer, refusal);
            }
        }

        return localizer[L.GpoCopy.OutcomeKey(outcome.Kind)];
    }

    /// <summary>What the merge window shows besides the settings: the derived conflict mode and the differences of scope (fresh LDAP reads).</summary>
    private static async Task<GpoMergeInfo> MergeInfoAsync(GroupPolicyServices services, IDirectorySession session, GpoInfo source, GpoInfo target)
    {
        var localizer = services.Localizer;
        var linksA = await services.Reader.ReadLinksOfAsync(session, source.Id, services.Lifetime).ConfigureAwait(true);
        var linksB = await services.Reader.ReadLinksOfAsync(session, target.Id, services.Lifetime).ConfigureAwait(true);
        var shared = linksA.Select(a => (A: a, B: linksB.FirstOrDefault(b => DistinguishedName.AreEqual(a.ContainerDn, b.ContainerDn))))
            .Where(p => p.B is not null)
            .ToList();
        var sourceFirst = shared.Count(p => p.A.Order < p.B!.Order);
        GpoCopyConflictMode? derived = shared.Count == 0 || sourceFirst == shared.Count ? GpoCopyConflictMode.Replace
            : sourceFirst == 0 ? GpoCopyConflictMode.Skip
            : null;
        var warnings = new List<string>();
        var onlyA = linksA.Where(a => !linksB.Any(b => DistinguishedName.AreEqual(a.ContainerDn, b.ContainerDn))).Select(l => l.ContainerName).Distinct().ToList();
        var onlyB = linksB.Where(b => !linksA.Any(a => DistinguishedName.AreEqual(a.ContainerDn, b.ContainerDn))).Select(l => l.ContainerName).Distinct().ToList();
        if (onlyA.Count > 0)
        {
            warnings.Add(localizer.Format(L.GpoCopy.MergeOnlySourceLinked, source.DisplayName, string.Join(", ", onlyA), target.DisplayName));
        }

        if (onlyB.Count > 0)
        {
            warnings.Add(localizer.Format(L.GpoCopy.MergeOnlyTargetLinked, target.DisplayName, string.Join(", ", onlyB), source.DisplayName));
        }

        var filterA = source.SecurityFilter;
        var filterB = target.SecurityFilter;
        if (filterA is null || filterB is null ? filterA is not null || filterB is not null : !filterA.Allowed.SetEquals(filterB.Allowed) || !filterA.Denied.SetEquals(filterB.Denied))
        {
            warnings.Add(localizer[L.GpoCopy.MergeSecurityFilterDiffers]);
        }

        if (!string.Equals(source.WmiFilterId ?? string.Empty, target.WmiFilterId ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(localizer[L.GpoCopy.MergeWmiFilterDiffers]);
        }

        return new GpoMergeInfo(source.DisplayName, target.DisplayName, derived, shared.Count > 0, shared.Count > 0 && derived is null, warnings);
    }
}
