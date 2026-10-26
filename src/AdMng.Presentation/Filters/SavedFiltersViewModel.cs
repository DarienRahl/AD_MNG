using System.Text;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Filters;

/// <summary>A saved filter in the list of <see cref="SavedFiltersViewModel"/>.</summary>
public sealed record SavedFilterItemViewModel(SavedFilter Filter, string Summary)
{
    public string Id => Filter.Id;

    public string Name => Filter.Name;

    public string Description => Filter.Description ?? string.Empty;
}

/// <summary>
/// "Zapisane filtry" (Tools menu, the account review): the operator's saved filters - New, Edit, Delete, export and import
/// as JSON for the team (text only). One window per application; it never acts in the directory session (the editor's
/// attribute list and test use the current session).
/// </summary>
public sealed partial class SavedFiltersViewModel : WindowViewModelBase
{
    private readonly FilterLibrary _library;

    public SavedFiltersViewModel(DialogServices services, FilterLibrary library)
        : base(services)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _library.Changed += OnLibraryChanged;
        Refresh();
    }

    public override string WindowTitle => Localizer[L.Filters.SavedTitle];

    protected override bool UsesSession => false;

    public BulkObservableCollection<SavedFilterItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(DeleteCommand))]
    public partial SavedFilterItemViewModel? SelectedItem { get; set; }

    public bool IsEmpty => Items.Count == 0;

    /// <summary>Creates a filter with the builder; returns the saved filter (null when cancelled).</summary>
    public static async Task<SavedFilter?> CreateOrEditAsync(DialogServices services, FilterLibrary library, SavedFilter? existing)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(library);
        var editor = new FilterEditorViewModel(services.Sessions, services.Dialogs, services.Localizer, services.Errors, services.Time, existing);
        if (!await services.Dialogs.ShowFilterEditorAsync(editor).ConfigureAwait(true) || editor.Result is not { } result)
        {
            return null;
        }

        await library.SaveAsync(result).ConfigureAwait(true);
        services.Notifications.Show(NotificationSeverity.Success, services.Localizer.Format(L.Filters.Saved, result.Name));
        return result;
    }

    [RelayCommand]
    private async Task NewAsync()
    {
        if (await CreateOrEditAsync(Services, _library, null).ConfigureAwait(true) is { } created)
        {
            Refresh(created.Id);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditAsync()
    {
        if (SelectedItem is { } item && await CreateOrEditAsync(Services, _library, item.Filter).ConfigureAwait(true) is { } saved)
        {
            Refresh(saved.Id);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        if (SelectedItem is not { } item
            || !await Services.Dialogs.ConfirmAsync(Localizer[L.Filters.DeleteTitle], Localizer.Format(L.Filters.DeleteConfirm, item.Name), destructive: true).ConfigureAwait(true))
        {
            return;
        }

        await _library.DeleteAsync(item.Id).ConfigureAwait(true);
        Refresh();
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var title = Localizer[L.Filters.ExportTitle];
        var path = await Services.Files.SaveFileAsync(title, "filtry.json", [new FileFilter(Localizer[L.Files.JsonFilter], "*.json")]).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, _library.ExportJson(), new UTF8Encoding(false)).ConfigureAwait(true);
            Services.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.Filters.Exported, _library.Filters.Count, Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Services.Dialogs.ShowErrorAsync(title, Errors.Translate(ex)).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var title = Localizer[L.Filters.ImportTitle];
        var path = await Services.Files.OpenFileAsync(title, [new FileFilter(Localizer[L.Files.JsonFilter], "*.json")]).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        try
        {
            var parsed = FilterLibrary.ParseImport(await File.ReadAllTextAsync(path).ConfigureAwait(true));
            var added = await _library.ImportAsync(parsed.Filters).ConfigureAwait(true);
            Refresh();
            Services.Notifications.Show(
                parsed.Rejected > 0 ? NotificationSeverity.Warning : NotificationSeverity.Success,
                Localizer.Format(L.Filters.Imported, added, parsed.Rejected));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Services.Dialogs.ShowErrorAsync(title, Errors.Translate(ex)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or NotSupportedException)
        {
            await Services.Dialogs.ShowErrorAsync(title, Localizer[L.Filters.InvalidFile]).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void Close() => RequestClose(true);

    private bool HasSelection() => SelectedItem is not null;

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        Refresh(SelectedItem?.Id);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _library.Changed -= OnLibraryChanged;
        }
    }

    private void OnLibraryChanged(object? sender, EventArgs e) => _ = Services.Dispatcher.RunAsync(() => Refresh(SelectedItem?.Id));

    private void Refresh(string? select = null)
    {
        Items.ReplaceAll(_library.Filters
            .OrderBy(f => f.Name, StringComparer.Create(Localizer.Culture, ignoreCase: true))
            .Select(f => new SavedFilterItemViewModel(f, FilterTexts.Summary(Localizer, f.Definition))));
        SelectedItem = Items.FirstOrDefault(i => i.Id == select);
        OnPropertyChanged(nameof(IsEmpty));
    }
}
