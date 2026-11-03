using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Comparison;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.GroupPolicy.Settings;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.GroupPolicy;

/// <summary>
/// The settings of a GPO comparison in the operator's language (K6, design note 20.1): each side's settings through the
/// presenter – a preference item and an administrative template policy are one row, as on the "Ustawienia" tab – named and
/// placed in GPMC's tree, with the technical key as tool tip and an extra CSV column.
/// </summary>
public sealed partial class GpoComparisonViewModel
{
    /// <summary>The settings rows of a comparison: one row per setting, preference item or policy of either side.</summary>
    private List<GpoComparisonRowViewModel> SettingRows(IReadOnlyList<GpoComparisonRow> rows, GpoSettingPresenter a, GpoSettingPresenter b)
    {
        var order = new List<string>();
        var buckets = new Dictionary<string, List<GpoComparisonRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Where(r => r.Section == GpoComparisonSection.Settings))
        {
            var info = GpoSettingKey.Parse(row.Key);
            var policy = (row.A is null ? null : a.PolicyOf(info)) ?? (row.B is null ? null : b.PolicyOf(info));
            var bucket = policy is { } owner ? "P|" + owner.Class + "|" + owner.Policy.Definition.Id
                : info is { Area: GpoSettingArea.Preference, ItemKey: { } item, Truncated: false } ? "I|" + item
                : "K|" + row.Key;
            if (!buckets.TryGetValue(bucket, out var members))
            {
                members = [];
                buckets[bucket] = members;
                order.Add(bucket);
            }

            members.Add(row);
        }

        var result = new List<GpoComparisonRowViewModel>(order.Count);
        foreach (var bucket in order)
        {
            var members = buckets[bucket];
            var sideA = members.Where(m => m.A is not null).Select(m => m.A!).ToList();
            var sideB = members.Where(m => m.B is not null).Select(m => m.B!).ToList();
            var textA = Side(a, sideA);
            var textB = Side(b, sideB);
            var text = (textA ?? textB)!;
            var state = members.All(m => m.State == GpoComparisonState.Same) ? GpoComparisonState.Same
                : sideA.Count == 0 ? GpoComparisonState.OnlyInB
                : sideB.Count == 0 ? GpoComparisonState.OnlyInA
                : GpoComparisonState.Different;
            result.Add(new GpoComparisonRowViewModel(members[0], GroupPolicyText.Section(Localizer, GpoComparisonSection.Settings), text.Name,
                textA?.ValueText ?? Localizer[L.GroupPolicy.Absent], textB?.ValueText ?? Localizer[L.GroupPolicy.Absent], GroupPolicyText.State(Localizer, state))
            {
                State = state,
                Area = text.GroupPath,
                Technical = text.Technical,
            });
        }

        return result;
    }

    /// <summary>One side of a row: its settings as one text (null: the side does not have them).</summary>
    private static GpoSettingText? Side(GpoSettingPresenter presenter, IReadOnlyList<GpoSetting> settings) => settings.Count switch
    {
        0 => null,
        1 => presenter.DescribeRows(settings)[0],
        _ => presenter.DescribeRows(settings) is { Count: 1 } rows ? rows[0] : Merge(presenter.DescribeRows(settings)),
    };

    /// <summary>Rows that did not collapse into one (a policy one side configures differently): their values joined.</summary>
    private static GpoSettingText Merge(IReadOnlyList<GpoSettingText> rows) =>
        rows[0] with { ValueText = string.Join("; ", rows.Select(r => r.ValueText)), Keys = [.. rows.SelectMany(r => r.Keys)] };

    /// <summary>The presenters of both sides: domain SIDs by name, preference item names, and – with templates – the policies.</summary>
    private static (GpoSettingPresenter A, GpoSettingPresenter B) Presenters(ILocalizer localizer, IReadOnlyDictionary<string, string> names,
        IReadOnlyList<GpoComparisonRow> rows, GpoFolderContent? contentA, GpoFolderContent? contentB, PolicyDefinitionSet? definitions)
    {
        var settingsA = rows.Where(r => r.A is not null).Select(r => r.A!).ToList();
        var settingsB = rows.Where(r => r.B is not null).Select(r => r.B!).ToList();
        var policiesA = definitions is null || contentA is null ? default : GpoSettingPresenter.Policies(contentA, definitions);
        var policiesB = definitions is null || contentB is null ? default : GpoSettingPresenter.Policies(contentB, definitions);
        return (
            new GpoSettingPresenter(localizer, names, definitions)
            {
                ItemNames = GpoSettingPresenter.ItemNamesOf(settingsA),
                ComputerPolicies = policiesA.Computer,
                UserPolicies = policiesA.User,
            },
            new GpoSettingPresenter(localizer, names, definitions)
            {
                ItemNames = GpoSettingPresenter.ItemNamesOf(settingsB),
                ComputerPolicies = policiesB.Computer,
                UserPolicies = policiesB.User,
            });
    }

    /// <summary>
    /// The administrative templates of the session's DC (cached per window), read after the rows are shown; null when there
    /// are none or they cannot be read (Registry.pol values then stay registry values).
    /// </summary>
    private async Task<PolicyDefinitionSet?> DefinitionsAsync(Core.Directory.IDirectorySession session, IReadOnlyList<GpoComparisonRow> rows, CancellationToken token)
    {
        if (_gp.Definitions is not { } service
            || !rows.Any(r => r.Section == GpoComparisonSection.Settings
                && (r.Key.Contains("/Registry/", StringComparison.OrdinalIgnoreCase) || r.Key.Contains("/Comment/", StringComparison.OrdinalIgnoreCase))))
        {
            return null;
        }

        try
        {
            var access = await _gp.OpenSysvolAsync(session, token).ConfigureAwait(true);
            try
            {
                var result = await service.GetAsync(session, access.Connection, Localizer.Culture, reload: false, token).ConfigureAwait(true);
                return result.Set.PolicyCount == 0 ? null : result.Set;
            }
            finally
            {
                if (access.Connection is { } connection)
                {
                    await connection.DisposeAsync().ConfigureAwait(true);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && (GroupPolicyText.IsExpected(ex) || ex is GpoParseException or System.Xml.XmlException))
        {
            return null;
        }
    }
}
