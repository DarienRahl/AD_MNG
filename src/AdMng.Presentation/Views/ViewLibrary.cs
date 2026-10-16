using System.Text.Json;
using AdMng.Core.Ldap;
using AdMng.Presentation.Settings;

namespace AdMng.Presentation.Views;

/// <summary>Result of parsing a views file.</summary>
/// <param name="Views">Valid views (ids not yet de-duplicated).</param>
/// <param name="Rejected">Number of entries skipped (no name, invalid filter or base DN).</param>
public sealed record ViewImportResult(IReadOnlyList<SavedView> Views, int Rejected);

/// <summary>
/// Saved views and pinned containers of the operator (stored in settings): add, edit, delete, reorder, pin/unpin,
/// and JSON import/export for sharing views within the team.
/// </summary>
public sealed class ViewLibrary
{
    /// <summary>Format marker of exported files.</summary>
    public const string ExportFormat = "AdMng.Views";

    private readonly ISettingsStore _settings;
    private readonly Lock _gate = new();
    private SavedView[] _lastViews;
    private PinnedContainer[] _lastPins;

    public ViewLibrary(ISettingsStore settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        (_lastViews, _lastPins) = Snapshot();
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>
    /// Raised when views or pinned containers changed (also after settings were reloaded) - not for unrelated settings
    /// such as column widths or recent containers, which change on every navigation and would rebuild the tree.
    /// </summary>
    public event EventHandler? Changed;

    public IReadOnlyList<SavedView> SavedViews => _settings.Current.SavedViews;

    public IReadOnlyList<PinnedContainer> PinnedContainers => _settings.Current.PinnedContainers;

    public SavedView? FindView(string id) => SavedViews.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.Ordinal));

    /// <summary>Adds a new view or replaces the view with the same id.</summary>
    public Task SaveViewAsync(SavedView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return _settings.UpdateAsync(s =>
        {
            var index = s.SavedViews.FindIndex(v => string.Equals(v.Id, view.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                s.SavedViews[index] = view;
            }
            else
            {
                s.SavedViews.Add(view);
            }
        });
    }

    public Task DeleteViewAsync(string id) =>
        _settings.UpdateAsync(s =>
        {
            s.SavedViews.RemoveAll(v => string.Equals(v.Id, id, StringComparison.Ordinal));
            s.ColumnLayouts.Remove(SavedView.LayoutKey(id));
        });

    /// <summary>Moves a view up (negative delta) or down in the navigation tree.</summary>
    public Task MoveViewAsync(string id, int delta) =>
        _settings.UpdateAsync(s => MoveItem(s.SavedViews, s.SavedViews.FindIndex(v => string.Equals(v.Id, id, StringComparison.Ordinal)), delta));

    public bool IsPinned(string distinguishedName) =>
        PinnedContainers.Any(p => DistinguishedName.AreEqual(p.DistinguishedName, distinguishedName));

    public Task PinAsync(string distinguishedName, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distinguishedName);
        if (IsPinned(distinguishedName))
        {
            return Task.CompletedTask;
        }

        return _settings.UpdateAsync(s => s.PinnedContainers.Add(new PinnedContainer(distinguishedName, name)));
    }

    public Task UnpinAsync(string distinguishedName) =>
        _settings.UpdateAsync(s => s.PinnedContainers.RemoveAll(p => DistinguishedName.AreEqual(p.DistinguishedName, distinguishedName)));

    public Task MovePinnedAsync(string distinguishedName, int delta) =>
        _settings.UpdateAsync(s => MoveItem(
            s.PinnedContainers,
            s.PinnedContainers.FindIndex(p => DistinguishedName.AreEqual(p.DistinguishedName, distinguishedName)),
            delta));

    /// <summary>Serializes views for sharing (all saved views when <paramref name="views"/> is null).</summary>
    public string ExportJson(IEnumerable<SavedView>? views = null)
    {
        var file = new ViewsFile { Format = ExportFormat, Version = 1, Views = [.. views ?? SavedViews] };
        return JsonSerializer.Serialize(file, JsonSettingsStore.SerializerOptions);
    }

    /// <summary>Parses and validates a views file. Throws <see cref="JsonException"/> / <see cref="FormatException"/> for unreadable files.</summary>
    public static ViewImportResult ParseImport(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var file = JsonSerializer.Deserialize<ViewsFile>(json, JsonSettingsStore.SerializerOptions);
        if (file is null || !string.Equals(file.Format, ExportFormat, StringComparison.Ordinal) || file.Views is null)
        {
            throw new FormatException("Not an AD Manager views file.");
        }

        var valid = new List<SavedView>();
        var rejected = 0;
        foreach (var view in file.Views)
        {
            if (view is null || string.IsNullOrWhiteSpace(view.Name) || LdapFilter.Validate(view.Filter) is not null || !IsValidBase(view.BaseDn))
            {
                rejected++;
                continue;
            }

            valid.Add(view with { Columns = view.Columns ?? [] });
        }

        return new ViewImportResult(valid, rejected);
    }

    /// <summary>Adds imported views; ids that already exist get a new id (imports never overwrite). Returns the number added.</summary>
    public async Task<int> ImportAsync(IReadOnlyList<SavedView> views)
    {
        ArgumentNullException.ThrowIfNull(views);
        var added = 0;
        await _settings.UpdateAsync(s =>
        {
            var ids = new HashSet<string>(s.SavedViews.Select(v => v.Id), StringComparer.Ordinal);
            foreach (var view in views)
            {
                var copy = ids.Contains(view.Id) || string.IsNullOrWhiteSpace(view.Id) ? view with { Id = SavedView.NewId() } : view;
                ids.Add(copy.Id);
                s.SavedViews.Add(copy);
                added++;
            }
        }).ConfigureAwait(true);
        return added;
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            var (views, pins) = Snapshot();

            // Views are immutable records replaced on save, so element equality detects edits, adds, removals and moves.
            if (views.SequenceEqual(_lastViews) && pins.SequenceEqual(_lastPins))
            {
                return;
            }

            (_lastViews, _lastPins) = (views, pins);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private (SavedView[] Views, PinnedContainer[] Pins) Snapshot() =>
        ([.. _settings.Current.SavedViews], [.. _settings.Current.PinnedContainers]);

    private static bool IsValidBase(string? baseDn)
    {
        if (string.IsNullOrWhiteSpace(baseDn))
        {
            return false;
        }

        var probe = baseDn.Replace(SavedView.DomainPlaceholder, "DC=example,DC=com", StringComparison.OrdinalIgnoreCase);
        return DistinguishedName.TryParse(probe, out var dn) && !dn.IsRoot;
    }

    private static void MoveItem<T>(List<T> items, int index, int delta)
    {
        var target = index + delta;
        if (index < 0 || target < 0 || target >= items.Count || delta == 0)
        {
            return;
        }

        var item = items[index];
        items.RemoveAt(index);
        items.Insert(target, item);
    }

    private sealed class ViewsFile
    {
        public string? Format { get; set; }

        public int Version { get; set; }

        public List<SavedView>? Views { get; set; }
    }
}
