using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.Settings;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Changes;

/// <summary>How a line of "Pokaż różnice" changed.</summary>
public enum PreviewDiffKind
{
    Unchanged,
    Removed,
    Added,

    /// <summary>A setting with another value after the change ("Name: old → new").</summary>
    Changed,
}

/// <summary>
/// One line of the SYSVOL differences of the change plan preview (K6, design note 20.1): a setting in the operator's language
/// ("Minimalna długość hasła: 12 znaków", a changed one as "… : 7 znaków → 12 znaków") with its technical text as tool tip, or
/// a text line of the file as it is.
/// </summary>
/// <param name="Kind">Unchanged, removed, added or changed.</param>
/// <param name="Text">The line as shown.</param>
/// <param name="ToolTip">Where GPMC lists the setting and the technical line(s) (null for text lines).</param>
public sealed record PreviewDiffLineViewModel(PreviewDiffKind Kind, string Text, string? ToolTip = null)
{
    /// <summary>The kind only (setting names and values stay out of logs).</summary>
    public override string ToString() => Kind.ToString();
}

/// <summary>"Pokaż różnice" in the operator's language.</summary>
public sealed partial class ChangePlanPreviewViewModel
{
    /// <summary>
    /// The lines of a file's differences: settings described by <see cref="GpoSettingPresenter"/> (SIDs named through the
    /// window's session when it is connected – one directory search), a removed and an added line of one setting joined into
    /// one "changed" line; text lines as they are.
    /// </summary>
    internal static async Task<IReadOnlyList<PreviewDiffLineViewModel>> DiffLinesAsync(IReadOnlyList<GpoDiffLine> lines, ILocalizer localizer, IDirectorySession? session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(localizer);
        var settings = lines.Where(l => l.Setting is not null).Select(l => l.Setting!).ToList();
        if (settings.Count == 0)
        {
            return [.. lines.Select(l => new PreviewDiffLineViewModel(Kind(l.Kind), l.Text))];
        }

        var sids = GpoSettingPresenter.Sids(settings);
        IReadOnlyDictionary<string, string> names = session is null || sids.Count == 0
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : await TrusteeNames.ResolveAsync(session, sids, localizer, cancellationToken).ConfigureAwait(true);
        var presenter = new GpoSettingPresenter(localizer, names) { ItemNames = GpoSettingPresenter.ItemNamesOf(settings) };

        // A setting removed and added again is one "changed" line, where it was removed.
        var removed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var partners = new Dictionary<int, int>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i] is { Kind: GpoDiffLineKind.Removed, Setting: { } old })
            {
                removed.TryAdd(old.Key, i);
            }
            else if (lines[i] is { Kind: GpoDiffLineKind.Added, Setting: { } added } && removed.Remove(added.Key, out var at))
            {
                partners[at] = i;
            }
        }

        var consumed = new HashSet<int>(partners.Values);
        var result = new List<PreviewDiffLineViewModel>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (consumed.Contains(i))
            {
                continue;
            }

            if (line.Setting is not { } setting)
            {
                result.Add(new PreviewDiffLineViewModel(Kind(line.Kind), line.Text));
                continue;
            }

            var before = presenter.Describe(setting);
            if (partners.TryGetValue(i, out var addedAt) && lines[addedAt].Setting is { } newSetting)
            {
                var after = presenter.Describe(newSetting);
                result.Add(new PreviewDiffLineViewModel(PreviewDiffKind.Changed,
                    localizer.Format(L.GpoSettings.LabelValue, after.Name, before.ValueText + " → " + after.ValueText),
                    after.GroupPath + Environment.NewLine + line.Text + Environment.NewLine + lines[addedAt].Text));
                continue;
            }

            result.Add(new PreviewDiffLineViewModel(Kind(line.Kind), localizer.Format(L.GpoSettings.LabelValue, before.Name, before.ValueText),
                before.GroupPath + Environment.NewLine + line.Text));
        }

        return result;
    }

    private static PreviewDiffKind Kind(GpoDiffLineKind kind) => kind switch
    {
        GpoDiffLineKind.Added => PreviewDiffKind.Added,
        GpoDiffLineKind.Removed => PreviewDiffKind.Removed,
        _ => PreviewDiffKind.Unchanged,
    };
}
