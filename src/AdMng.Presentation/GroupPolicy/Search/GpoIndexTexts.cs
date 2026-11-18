using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Presentation.GroupPolicy.Settings;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.GroupPolicy.Search;

/// <summary>
/// K7 (docs/dev/k-computers-and-gpo.md 21.3): the GPMC texts of an index view – one <see cref="GpoSettingPresenter"/> per
/// entry (its configured policies, the item names of its preference items, the view's SID names and definitions), built
/// once per view (<see cref="For"/>, warmed by the index service on <c>Task.Run</c>), never per keystroke. A unit is
/// described by the single row the presenter makes of its settings. In memory only (rule 1). Thread-safe.
/// </summary>
public sealed class GpoIndexTexts
{
    private static readonly ConditionalWeakTable<GpoIndexView, GpoIndexTexts> Cache = new();

    private readonly ConcurrentDictionary<Guid, GpoSettingPresenter> _presenters = new();
    private readonly ConcurrentDictionary<(Guid Gpo, string Unit), GpoSettingText> _rows = new();
    private readonly GpoSettingPresenter _names;

    public GpoIndexTexts(ILocalizer localizer, GpoIndexView view)
    {
        Localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        View = view ?? throw new ArgumentNullException(nameof(view));
        _names = new GpoSettingPresenter(Localizer, View.SidNames, View.Index.Definitions);
    }

    public ILocalizer Localizer { get; }

    public GpoIndexView View { get; }

    /// <summary>The texts of <paramref name="view"/>, made once per view (later calls return the same instance).</summary>
    public static GpoIndexTexts For(ILocalizer localizer, GpoIndexView view)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(view);
        return Cache.GetValue(view, v => new GpoIndexTexts(localizer, v));
    }

    /// <summary>The presenter of one entry (made on first use, then kept).</summary>
    public GpoSettingPresenter PresenterOf(GpoIndexEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _presenters.GetOrAdd(entry.Gpo.Id, _ => new GpoSettingPresenter(Localizer, View.SidNames, View.Index.Definitions)
        {
            ComputerPolicies = entry.ComputerPolicies,
            UserPolicies = entry.UserPolicies,
            ItemNames = GpoSettingPresenter.ItemNamesOf(entry.Settings),
        });
    }

    /// <summary>Makes the presenters of every entry with content (the index service runs this once per view, off the UI thread).</summary>
    public void Warm()
    {
        foreach (var entry in View.Index.Entries.Where(e => e.HasContent))
        {
            PresenterOf(entry);
        }
    }

    /// <summary>
    /// The single row the presenter makes of the unit's settings (cached per GPO and unit id). Units are built so that the
    /// presenter collapses them into one row; should it ever make more, the first row carries every key of the unit.
    /// </summary>
    public GpoSettingText Describe(GpoIndexEntry entry, GpoSettingUnit unit)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(unit);
        return _rows.GetOrAdd((entry.Gpo.Id, unit.Id), _ =>
        {
            var rows = PresenterOf(entry).DescribeRows(unit.Settings);
            return rows.Count switch
            {
                1 => rows[0],
                0 => new GpoSettingText
                {
                    Key = unit.Id,
                    Keys = [],
                    Scope = unit.Scope,
                    Kind = unit.Area,
                    Configuration = string.Empty,
                    Name = unit.Id,
                    Technical = unit.Id,
                    ValueText = string.Empty,
                },
                _ => rows[0] with { Keys = [.. unit.Settings.Select(s => s.Key)] },
            };
        });
    }

    /// <summary>DOMAIN\name of a SID (local groups and well-known principals by their localized names), or the name as written.</summary>
    public string PrincipalName(string sidOrName)
    {
        ArgumentNullException.ThrowIfNull(sidOrName);
        return _names.PrincipalName(sidOrName);
    }

    /// <summary>For diagnostics: the view only.</summary>
    public override string ToString() => View.ToString();
}
