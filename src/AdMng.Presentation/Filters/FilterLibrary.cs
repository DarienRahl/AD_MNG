using System.Text.Json;
using AdMng.Core.Filters;
using AdMng.Presentation.Settings;

namespace AdMng.Presentation.Filters;

/// <summary>Result of parsing a filters file.</summary>
/// <param name="Filters">Valid filters (ids not yet de-duplicated).</param>
/// <param name="Rejected">Entries skipped (no name or an invalid filter).</param>
public sealed record FilterImportResult(IReadOnlyList<SavedFilter> Filters, int Rejected);

/// <summary>
/// The operator's saved filters (stored in the settings): add, edit, delete and JSON export/import for the team - the
/// same pattern as <see cref="Views.ViewLibrary"/>. Deleting a filter also removes it from the saved views, the account
/// review's scope and the privileged access rules that use it.
/// </summary>
public sealed class FilterLibrary
{
    /// <summary>Format marker of exported files.</summary>
    public const string ExportFormat = "AdMng.Filters";

    /// <summary>Most saved filters kept.</summary>
    public const int MaxFilters = 500;

    private readonly ISettingsStore _settings;
    private readonly Lock _gate = new();
    private SavedFilter[] _last;

    public FilterLibrary(ISettingsStore settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _last = [.. _settings.Current.SavedFilters];
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>Raised when the saved filters changed (also after the settings were reloaded).</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<SavedFilter> Filters => _settings.Current.SavedFilters;

    public SavedFilter? Find(string? id) => id is null ? null : Filters.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.Ordinal));

    /// <summary>Adds a new filter or replaces the filter with the same id.</summary>
    public Task SaveAsync(SavedFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return _settings.UpdateAsync(s =>
        {
            var index = s.SavedFilters.FindIndex(f => string.Equals(f.Id, filter.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                s.SavedFilters[index] = filter;
            }
            else if (s.SavedFilters.Count < MaxFilters)
            {
                s.SavedFilters.Add(filter);
            }
        });
    }

    /// <summary>Deletes a filter and every use of it (saved views, the review's scope, privileged access rules).</summary>
    public Task DeleteAsync(string id) => _settings.UpdateAsync(s =>
    {
        s.SavedFilters.RemoveAll(f => string.Equals(f.Id, id, StringComparison.Ordinal));
        for (var i = 0; i < s.SavedViews.Count; i++)
        {
            if (s.SavedViews[i].SavedFilters.Contains(id, StringComparer.Ordinal))
            {
                s.SavedViews[i] = s.SavedViews[i] with { SavedFilters = [.. s.SavedViews[i].SavedFilters.Where(f => !string.Equals(f, id, StringComparison.Ordinal))] };
            }
        }

        if (string.Equals(s.AccountReview.Scope.FilterId, id, StringComparison.Ordinal))
        {
            s.AccountReview.Scope = s.AccountReview.Scope with { FilterId = null };
        }

        s.AccountReview.Privileges.Rules.RemoveAll(r => string.Equals(r.FilterId, id, StringComparison.Ordinal));
    });

    /// <summary>Serializes filters for sharing (all when <paramref name="filters"/> is null).</summary>
    public string ExportJson(IEnumerable<SavedFilter>? filters = null)
    {
        var file = new FiltersFile { Format = ExportFormat, Version = 1, Filters = [.. filters ?? Filters] };
        return JsonSerializer.Serialize(file, JsonSettingsStore.SerializerOptions);
    }

    /// <summary>Parses and validates a filters file. Throws <see cref="JsonException"/> / <see cref="FormatException"/> for unreadable files.</summary>
    public static FilterImportResult ParseImport(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var file = JsonSerializer.Deserialize<FiltersFile>(json, JsonSettingsStore.SerializerOptions);
        if (file is null || !string.Equals(file.Format, ExportFormat, StringComparison.Ordinal) || file.Filters is null)
        {
            throw new FormatException("Not an AD Manager filters file.");
        }

        var context = new FilterContext { Now = DateTimeOffset.UnixEpoch };
        var valid = new List<SavedFilter>();
        var rejected = 0;
        foreach (var filter in file.Filters)
        {
            if (filter is null || string.IsNullOrWhiteSpace(filter.Name) || Normalize(filter) is not { } normalized
                || FilterCompiler.Validate(normalized.Definition, context).Count > 0)
            {
                rejected++;
                continue;
            }

            valid.Add(normalized);
        }

        return new FilterImportResult(valid, rejected);
    }

    /// <summary>Adds imported filters; ids that already exist get a new id (imports never overwrite). Returns the number added.</summary>
    public async Task<int> ImportAsync(IReadOnlyList<SavedFilter> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        var added = 0;
        await _settings.UpdateAsync(s =>
        {
            var ids = new HashSet<string>(s.SavedFilters.Select(f => f.Id), StringComparer.Ordinal);
            foreach (var filter in filters)
            {
                if (s.SavedFilters.Count >= MaxFilters)
                {
                    break;
                }

                var copy = ids.Contains(filter.Id) || string.IsNullOrWhiteSpace(filter.Id) ? filter with { Id = SavedFilter.NewId() } : filter;
                ids.Add(copy.Id);
                s.SavedFilters.Add(copy);
                added++;
            }
        }).ConfigureAwait(true);
        return added;
    }

    /// <summary>A filter of a hand-edited file with its lists repaired; null when it cannot be repaired.</summary>
    internal static SavedFilter? Normalize(SavedFilter? filter)
    {
        if (filter is null || string.IsNullOrWhiteSpace(filter.Id) || string.IsNullOrWhiteSpace(filter.Name))
        {
            return null;
        }

        var definition = filter.Definition ?? new FilterDefinition();
        return filter with
        {
            Name = filter.Name.Trim(),
            Definition = definition with
            {
                Groups = [.. (definition.Groups ?? [])
                    .Where(g => g is not null)
                    .Select(g => g with { Conditions = [.. (g.Conditions ?? []).Where(c => c is not null).Select(c => c with { Attribute = c.Attribute ?? string.Empty, Value = c.Value ?? string.Empty })] })],
            },
        };
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            SavedFilter[] current = [.. _settings.Current.SavedFilters];
            if (current.SequenceEqual(_last))
            {
                return;
            }

            _last = current;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FiltersFile
    {
        public string? Format { get; set; }

        public int Version { get; set; }

        public List<SavedFilter>? Filters { get; set; }
    }
}
