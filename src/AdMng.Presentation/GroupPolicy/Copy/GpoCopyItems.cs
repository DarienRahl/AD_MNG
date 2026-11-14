using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Presentation.GroupPolicy.Search;

namespace AdMng.Presentation.GroupPolicy.Copy;

/// <summary>
/// K7 (docs/dev/k-computers-and-gpo.md 21.5): the display items of index units – the selection the planners find the unit
/// by again (id, item id, the value the operator saw) and the GPMC texts of the window. In memory only (rule 1).
/// </summary>
public static class GpoCopyItems
{
    /// <summary>One unit of one GPO of the index.</summary>
    public static GpoCopyItem Of(GpoIndexTexts texts, GpoIndexEntry entry, GpoSettingUnit unit)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(unit);
        var text = texts.Describe(entry, unit);
        return new GpoCopyItem(new GpoUnitSelection(entry.Gpo.Id, unit.Id, unit.ItemId, unit.ValueFingerprint), entry.Gpo.DisplayName,
            text.Name, text.GroupPath, text.ValueText, unit.Refusal);
    }

    /// <summary>Every unit of a GPO that counts as a setting (comments, unreadable markers and script files are left out).</summary>
    public static IReadOnlyList<GpoCopyItem> OfGpo(GpoIndexTexts texts, GpoIndexEntry entry)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(entry);
        return [.. entry.Units.Where(u => u.CountsAsSetting).Select(u => Of(texts, entry, u))];
    }

    /// <summary>The units of <paramref name="entry"/> with <paramref name="baseId"/> (a duplicated setting: one occurrence).</summary>
    public static IReadOnlyList<GpoCopyItem> OfBaseId(GpoIndexTexts texts, GpoIndexEntry entry, string baseId)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(baseId);
        return [.. entry.Units.Where(u => string.Equals(u.BaseId, baseId, StringComparison.OrdinalIgnoreCase)).Select(u => Of(texts, entry, u))];
    }
}
