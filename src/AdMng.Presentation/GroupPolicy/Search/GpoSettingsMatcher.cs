using System.Globalization;
using System.Text;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Presentation.GroupPolicy.Settings;

namespace AdMng.Presentation.GroupPolicy.Search;

/// <summary>Where "Wyszukiwarka ustawień GPO" looks for the typed text (field selector).</summary>
public enum GpoSearchField
{
    /// <summary>Name, path, value, technical key and accounts.</summary>
    All,

    /// <summary>The setting's name in GPMC's words.</summary>
    Name,

    /// <summary>The value and its parts (never of a masked unit).</summary>
    Value,

    /// <summary>The technical key, the unit's keys and its identity.</summary>
    Technical,

    /// <summary>An account: its SID (exact, resolved from the typed text) or a resolved name containing the text.</summary>
    Account,
}

/// <summary>The area facet of the search (where GPMC lists the setting).</summary>
public enum GpoSearchArea
{
    AdministrativeTemplates,
    Security,
    Audit,
    Preferences,
    Scripts,
    Other,
}

/// <summary>Whether the occurrences of a unit that apply (not disabled preference items) agree on their value.</summary>
public enum GpoValueAgreement
{
    /// <summary>Fewer than two occurrences apply: nothing to compare.</summary>
    Single,

    Same,
    Different,

    /// <summary>An occurrence cannot be compared (masked, conditional, scripts, unreadable, ambiguous, truncated).</summary>
    NotComparable,
}

/// <summary>
/// A query of the search: the typed text, the field and the facets. <see cref="ToString"/> prints the options only (the text
/// may name directory values).
/// </summary>
public sealed record GpoSearchQuery(string Text)
{
    public GpoSearchField Field { get; init; }

    /// <summary>Computer or User (null: both; comments of the GPO are shown only then).</summary>
    public GpoScope? Scope { get; init; }

    public GpoSearchArea? Area { get; init; }

    /// <summary>With <see cref="GpoSearchArea.Preferences"/>: one preference type (<c>Drives</c>, <c>Groups</c> …; null: every type).</summary>
    public string? PreferenceType { get; init; }

    /// <summary>"Tylko różne wartości": only units whose applying occurrences differ.</summary>
    public bool OnlyDifferent { get; init; }

    /// <summary><see cref="GpoSearchField.Account"/>: the SIDs the typed text resolved to (exact match on the units' principals).</summary>
    public IReadOnlyCollection<string>? AccountSids { get; init; }

    /// <summary>For diagnostics: the options only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Field} {Scope} {Area} different: {OnlyDifferent}, text length {Text.Length}, SIDs {AccountSids?.Count ?? 0}");
}

/// <summary>
/// One searchable occurrence: a unit of one GPO with the presenter's texts and the folded search keys (in memory only,
/// CLAUDE.md rule 1). <see cref="ToString"/> prints the kind and scope only.
/// </summary>
public sealed class GpoSearchRecord
{
    internal GpoSearchRecord(GpoIndexEntry entry, GpoSettingUnit unit, GpoSettingText text, string principalKey)
    {
        Entry = entry;
        Unit = unit;
        Text = text;
        Area = GpoSettingsMatcher.AreaOf(unit.Kind);
        IsMasked = text.IsMasked || unit.Comparability == GpoUnitComparability.Masked;
        NameKey = GpoSettingsMatcher.Fold(text.Name);
        PathKey = GpoSettingsMatcher.Fold(text.GroupPath);
        ValueKey = IsMasked ? string.Empty : GpoSettingsMatcher.Fold(string.Join('\n', [text.ValueText, .. text.Elements]));
        TechnicalKey = GpoSettingsMatcher.Fold(string.Join('\n', [text.Technical, unit.BaseId, .. unit.Settings.Select(s => s.Key)]));
        PrincipalKey = principalKey;
    }

    public GpoIndexEntry Entry { get; }

    public GpoSettingUnit Unit { get; }

    /// <summary>What the GPO's presenter makes of the unit (one row).</summary>
    public GpoSettingText Text { get; }

    public GpoSearchArea Area { get; }

    /// <summary>A setting of the unit is masked: its value is never matched.</summary>
    public bool IsMasked { get; }

    /// <summary>The group of every occurrence of the unit's base id (set once the corpus is built).</summary>
    public GpoSearchUnitGroup Group { get; internal set; } = null!;

    internal string NameKey { get; }

    internal string PathKey { get; }

    internal string ValueKey { get; }

    internal string TechnicalKey { get; }

    /// <summary>The principals as written, their resolved names and SIDs, folded.</summary>
    internal string PrincipalKey { get; }

    /// <summary>For diagnostics: the unit's kind and scope only.</summary>
    public override string ToString() => Unit.ToString();
}

/// <summary>Every occurrence of one <see cref="GpoSettingUnit.BaseId"/> in the index, with the agreement of their values.</summary>
public sealed class GpoSearchUnitGroup
{
    internal GpoSearchUnitGroup(string baseId, IReadOnlyList<GpoSearchRecord> occurrences)
    {
        BaseId = baseId;
        Occurrences = occurrences;
        Agreement = GpoSettingsMatcher.Agreement(occurrences.Select(o => o.Unit));
        GpoCount = occurrences.Select(o => o.Entry.Gpo.Id).Distinct().Count();
    }

    public string BaseId { get; }

    /// <summary>The occurrences in index order (GPOs, then units).</summary>
    public IReadOnlyList<GpoSearchRecord> Occurrences { get; }

    public GpoValueAgreement Agreement { get; }

    /// <summary>The GPOs that set the unit.</summary>
    public int GpoCount { get; }

    /// <summary>The position in GPMC's order (configuration, path, name), set once the corpus is built.</summary>
    internal int Rank { get; set; }

    /// <summary>For diagnostics: the kind and counts only (the base id holds directory values).</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Occurrences[0]} in {GpoCount} GPOs, {Agreement}");
}

/// <summary>The settings of one area in the summary (an empty or one-character query).</summary>
/// <param name="Area">The area.</param>
/// <param name="Settings">The distinct units (base ids).</param>
/// <param name="Occurrences">The units in every GPO.</param>
/// <param name="Gpos">The GPOs with at least one of them.</param>
public sealed record GpoSearchAreaCount(GpoSearchArea Area, int Settings, int Occurrences, int Gpos);

/// <summary>
/// The search records of one index view, built once per view off the UI thread (<see cref="Build"/>) and matched per query
/// (<see cref="GpoSettingsMatcher.Match"/>). In memory only (rule 1).
/// </summary>
public sealed class GpoSearchCorpus
{
    private GpoSearchCorpus(GpoIndexView view, GpoIndexTexts texts, IReadOnlyList<GpoSearchRecord> records, IReadOnlyList<GpoSearchUnitGroup> groups)
    {
        View = view;
        Texts = texts;
        Records = records;
        Groups = groups;
        Areas = [.. Enum.GetValues<GpoSearchArea>().Select(area =>
        {
            var inArea = records.Where(r => r.Area == area).ToList();
            return new GpoSearchAreaCount(area, inArea.Select(r => r.Group).Distinct().Count(), inArea.Count, inArea.Select(r => r.Entry.Gpo.Id).Distinct().Count());
        })];
        PreferenceTypes = [.. records.Where(r => r.Unit.PreferenceType is { Length: > 0 }).Select(r => r.Unit.PreferenceType!)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
        MaskedCount = records.Count(r => r.IsMasked);
        GposWithSettings = records.Select(r => r.Entry.Gpo.Id).Distinct().Count();
    }

    public GpoIndexView View { get; }

    public GpoIndexTexts Texts { get; }

    public IReadOnlyList<GpoSearchRecord> Records { get; }

    /// <summary>The groups by base id, in GPMC's order.</summary>
    public IReadOnlyList<GpoSearchUnitGroup> Groups { get; }

    /// <summary>The summary counts per area.</summary>
    public IReadOnlyList<GpoSearchAreaCount> Areas { get; }

    /// <summary>The preference types present (the area facet offers one entry per type).</summary>
    public IReadOnlyList<string> PreferenceTypes { get; }

    /// <summary>Occurrences whose value is masked (never matched by value).</summary>
    public int MaskedCount { get; }

    public int GposWithSettings { get; }

    /// <summary>
    /// One record per (GPO, unit) of the entries with content: the presenter's single row of the unit and the folded keys;
    /// the groups by base id ordered as GPMC lists the settings (configuration, path, name in the view's culture).
    /// </summary>
    public static GpoSearchCorpus Build(GpoIndexView view, GpoIndexTexts texts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(texts);
        var records = new List<GpoSearchRecord>();
        var byBaseId = new Dictionary<string, List<GpoSearchRecord>>(StringComparer.OrdinalIgnoreCase);
        var principalNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in view.Index.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var unit in entry.Units)
            {
                var record = new GpoSearchRecord(entry, unit, texts.Describe(entry, unit), PrincipalKey(unit, texts, principalNames));
                records.Add(record);
                if (!byBaseId.TryGetValue(unit.BaseId, out var list))
                {
                    list = [];
                    byBaseId[unit.BaseId] = list;
                }

                list.Add(record);
            }
        }

        var groups = byBaseId.Select(p => new GpoSearchUnitGroup(p.Key, p.Value)).ToList();
        foreach (var group in groups)
        {
            foreach (var record in group.Occurrences)
            {
                record.Group = group;
            }
        }

        var compare = StringComparer.Create(view.Culture, ignoreCase: true);
        groups = [.. groups
            .OrderBy(g => g.Occurrences[0].Text.Configuration, compare)
            .ThenBy(g => g.Occurrences[0].Text.Path, compare)
            .ThenBy(g => g.Occurrences[0].Text.Name, compare)
            .ThenBy(g => g.BaseId, StringComparer.OrdinalIgnoreCase)];
        for (var i = 0; i < groups.Count; i++)
        {
            groups[i].Rank = i;
        }

        return new GpoSearchCorpus(view, texts, records, groups);
    }

    private static string PrincipalKey(GpoSettingUnit unit, GpoIndexTexts texts, Dictionary<string, string> names)
    {
        if (unit.Principals.Count == 0 && unit.PrincipalSids.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>(unit.Principals);
        foreach (var sid in unit.PrincipalSids)
        {
            if (!names.TryGetValue(sid, out var name))
            {
                name = texts.PrincipalName(sid);
                names[sid] = name;
            }

            parts.Add(name);
            parts.Add(sid);
        }

        return GpoSettingsMatcher.Fold(string.Join('\n', parts));
    }

    /// <summary>For diagnostics: counts only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Records.Count} records, {Groups.Count} groups");
}

/// <summary>What one query found: the groups to show (at most <see cref="GpoSettingsMatcher.MaxGroups"/>) and how many there were.</summary>
public sealed record GpoSearchResult(IReadOnlyList<GpoSearchUnitGroup> Groups, int TotalGroups, int MatchedOccurrences)
{
    /// <summary>The query was too short: the document shows the summary instead.</summary>
    public bool IsSummary { get; init; }

    /// <summary>More groups matched than are shown.</summary>
    public bool IsCapped => TotalGroups > Groups.Count;

    public static GpoSearchResult Summary { get; } = new([], 0, 0) { IsSummary = true };

    /// <summary>For diagnostics: counts only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Groups.Count} of {TotalGroups} groups, summary: {IsSummary}");
}

/// <summary>
/// The matching of "Wyszukiwarka ustawień GPO" (K7, docs/dev/k-computers-and-gpo.md 21.4): pure, thread-safe, run on
/// <c>Task.Run</c> by the document. Text is compared case- and diacritics-insensitively on keys folded once per corpus
/// (<see cref="Fold"/>: what <c>CompareOptions.IgnoreCase | IgnoreNonSpace</c> ignores, plus the stroke letters such as
/// "ł", so "dlugosc" finds "długość"), with an ordinal search – a pass over 100 000 records stays far below 500 ms. Values of
/// masked units are never matched. Results are grouped by base id, every occurrence of a matched unit is shown.
/// </summary>
public static class GpoSettingsMatcher
{
    /// <summary>The shortest query that searches (shorter: the summary).</summary>
    public const int MinLength = 2;

    /// <summary>The most groups a result shows ("Zawęź wyszukiwanie").</summary>
    public const int MaxGroups = 2000;

    /// <summary>The groups whose units match the query and the facets, in GPMC's order, at most <see cref="MaxGroups"/>.</summary>
    public static GpoSearchResult Match(GpoSearchCorpus corpus, GpoSearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(query);
        var text = Fold(query.Text.Trim());
        var sids = query.Field == GpoSearchField.Account && query.AccountSids is { Count: > 0 } resolved
            ? new HashSet<string>(resolved, StringComparer.OrdinalIgnoreCase)
            : null;
        if (text.Length < MinLength && sids is null)
        {
            return GpoSearchResult.Summary;
        }

        var matched = new HashSet<GpoSearchUnitGroup>();
        var occurrences = 0;
        var records = corpus.Records;
        for (var i = 0; i < records.Count; i++)
        {
            if ((i & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var record = records[i];
            if (!InFacets(record, query) || !Matches(record, text, query.Field, sids))
            {
                continue;
            }

            occurrences++;
            matched.Add(record.Group);
        }

        var groups = matched.Where(g => !query.OnlyDifferent || g.Agreement == GpoValueAgreement.Different).OrderBy(g => g.Rank).ToList();
        return new GpoSearchResult(groups.Count > MaxGroups ? groups.GetRange(0, MaxGroups) : groups, groups.Count, occurrences);
    }

    /// <summary>
    /// The SIDs an account text names without LDAP: SID text (with or without <c>*</c>), well-known and domain names
    /// (<see cref="GpoPrincipalResolver.KnownSid"/>), the names the index resolved and the view's SID names (exact, case ignored).
    /// </summary>
    public static IReadOnlyCollection<string> AccountSids(GpoIndexView view, string text)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(text);
        var written = text.Trim();
        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (written.Length == 0)
        {
            return sids;
        }

        if (GpoPrincipalResolver.KnownSid(written, view.Index.DomainSid) is { } known)
        {
            sids.Add(known);
        }

        if (view.Index.SidsByName.TryGetValue(written, out var resolved))
        {
            sids.Add(resolved);
        }

        foreach (var (sid, name) in view.SidNames)
        {
            if (string.Equals(name, written, StringComparison.OrdinalIgnoreCase))
            {
                sids.Add(sid);
            }
        }

        return sids;
    }

    /// <summary>The area facet of a unit kind.</summary>
    public static GpoSearchArea AreaOf(GpoSettingUnitKind kind) => kind switch
    {
        GpoSettingUnitKind.AdministrativeTemplate or GpoSettingUnitKind.RegistryValue or GpoSettingUnitKind.RegistryKey => GpoSearchArea.AdministrativeTemplates,
        GpoSettingUnitKind.SecurityValue or GpoSettingUnitKind.UserRight or GpoSettingUnitKind.RestrictedGroup or GpoSettingUnitKind.SecurityRegistryValue
            or GpoSettingUnitKind.Service or GpoSettingUnitKind.ObjectSecurity => GpoSearchArea.Security,
        GpoSettingUnitKind.AuditSubcategory or GpoSettingUnitKind.AuditOption => GpoSearchArea.Audit,
        GpoSettingUnitKind.Preference => GpoSearchArea.Preferences,
        GpoSettingUnitKind.Script or GpoSettingUnitKind.ScriptConfiguration or GpoSettingUnitKind.ScriptFile => GpoSearchArea.Scripts,
        _ => GpoSearchArea.Other,
    };

    /// <summary>
    /// The agreement of the occurrences that apply (disabled preference items left out): fewer than two – nothing to compare;
    /// any not comparable – not comparable; otherwise <see cref="GpoSettingUnits.SameValue"/> against the first.
    /// </summary>
    public static GpoValueAgreement Agreement(IEnumerable<GpoSettingUnit> units)
    {
        ArgumentNullException.ThrowIfNull(units);
        var active = units.Where(u => !u.Disabled).ToList();
        if (active.Count < 2)
        {
            return GpoValueAgreement.Single;
        }

        if (active.Any(u => u.Comparability != GpoUnitComparability.Comparable))
        {
            return GpoValueAgreement.NotComparable;
        }

        return active.Skip(1).All(u => GpoSettingUnits.SameValue(active[0], u)) ? GpoValueAgreement.Same : GpoValueAgreement.Different;
    }

    /// <summary>
    /// Lower case without diacritics: canonical decomposition with the combining marks dropped (what <c>IgnoreNonSpace</c>
    /// ignores) and the stroke letters ł, đ, ø folded to l, d, o.
    /// </summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            folded.Append(c switch
            {
                'ł' or 'Ł' => 'l',
                'đ' or 'Đ' => 'd',
                'ø' or 'Ø' => 'o',
                _ => char.ToLowerInvariant(c),
            });
        }

        return folded.ToString();
    }

    private static bool InFacets(GpoSearchRecord record, GpoSearchQuery query) =>
        (query.Scope is not { } scope || record.Unit.Scope == scope)
        && (query.Area is not { } area || (record.Area == area
            && (query.PreferenceType is not { } type || string.Equals(record.Unit.PreferenceType, type, StringComparison.OrdinalIgnoreCase))));

    private static bool Matches(GpoSearchRecord record, string text, GpoSearchField field, HashSet<string>? sids)
    {
        var searchable = text.Length >= MinLength;
        return field switch
        {
            GpoSearchField.Name => searchable && record.NameKey.Contains(text, StringComparison.Ordinal),
            GpoSearchField.Value => searchable && record.ValueKey.Contains(text, StringComparison.Ordinal),
            GpoSearchField.Technical => searchable && record.TechnicalKey.Contains(text, StringComparison.Ordinal),
            GpoSearchField.Account => (sids is not null && record.Unit.PrincipalSids.Any(sids.Contains))
                || (searchable && record.PrincipalKey.Contains(text, StringComparison.Ordinal)),
            _ => searchable && (record.NameKey.Contains(text, StringComparison.Ordinal)
                || record.PathKey.Contains(text, StringComparison.Ordinal)
                || record.ValueKey.Contains(text, StringComparison.Ordinal)
                || record.TechnicalKey.Contains(text, StringComparison.Ordinal)
                || record.PrincipalKey.Contains(text, StringComparison.Ordinal)),
        };
    }
}
