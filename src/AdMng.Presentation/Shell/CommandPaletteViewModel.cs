using System.Collections.ObjectModel;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Shell;

/// <summary>
/// A command offered in menus, toolbars, keyboard shortcuts and the command palette. Titles are resource keys
/// so the descriptor list survives language changes.
/// </summary>
/// <param name="Id">Stable identifier (e.g. "object.disable").</param>
/// <param name="TitleKey">Resource key of the title.</param>
/// <param name="CategoryKey">Resource key of the category.</param>
/// <param name="Shortcut">Keyboard gesture text as WPF KeyGestureConverter understands it ("Ctrl+Shift+B"), or empty.</param>
/// <param name="Command">The command (CanExecute decides whether the palette offers it).</param>
public sealed record CommandDescriptor(string Id, string TitleKey, string CategoryKey, string Shortcut, IRelayCommand Command)
{
    /// <summary>Optional parameter passed to <see cref="Command"/>.</summary>
    public object? Parameter { get; init; }
}

/// <summary>One palette entry (descriptor with localized texts).</summary>
public sealed record CommandPaletteItem(CommandDescriptor Descriptor, string Title, string Category)
{
    public string Shortcut => Descriptor.Shortcut;

    public bool CanExecute => Descriptor.Command.CanExecute(Descriptor.Parameter);
}

/// <summary>
/// Ctrl+K command palette: fuzzy (subsequence, diacritics-insensitive) search over all commands, Enter executes.
/// </summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private readonly ILocalizer _localizer;
    private readonly List<CommandDescriptor> _commands = [];

    public CommandPaletteViewModel(ILocalizer localizer)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
    }

    public IReadOnlyList<CommandDescriptor> Commands => _commands;

    public ObservableCollection<CommandPaletteItem> Results { get; } = [];

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    public partial CommandPaletteItem? SelectedResult { get; set; }

    public void Register(IEnumerable<CommandDescriptor> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _commands.AddRange(commands);
        Refresh();
    }

    /// <summary>Finds a descriptor by id (the view uses it to build menus and key bindings).</summary>
    public CommandDescriptor? Find(string id) => _commands.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));

    [RelayCommand]
    private void Open()
    {
        FilterText = string.Empty;
        Refresh();
        IsOpen = true;
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand(CanExecute = nameof(CanExecuteItem))]
    private void Execute(CommandPaletteItem? item)
    {
        item ??= SelectedResult;
        if (item is null || !item.CanExecute)
        {
            return;
        }

        IsOpen = false;
        item.Descriptor.Command.Execute(item.Descriptor.Parameter);
    }

    private bool CanExecuteItem(CommandPaletteItem? item) => (item ?? SelectedResult) is not null;

    [RelayCommand]
    private void MoveSelection(int delta)
    {
        if (Results.Count == 0)
        {
            return;
        }

        var index = SelectedResult is null ? -1 : Results.IndexOf(SelectedResult);
        SelectedResult = Results[Math.Clamp(index + delta, 0, Results.Count - 1)];
    }

    partial void OnFilterTextChanged(string value) => Refresh();

    /// <summary>Re-filters (also call after a language change).</summary>
    public void Refresh()
    {
        var query = FilterText;
        var ranked = _commands
            .Select(c => new CommandPaletteItem(c, _localizer[c.TitleKey], _localizer[c.CategoryKey]))
            .Where(i => i.CanExecute)
            .Select(i => (Item: i, Score: BestScore(i, query)))
            .Where(x => x.Score is not null)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Item.Title, StringComparer.Create(_localizer.Culture, ignoreCase: true))
            .Select(x => x.Item)
            .ToList();
        Results.Clear();
        foreach (var item in ranked)
        {
            Results.Add(item);
        }

        SelectedResult = Results.FirstOrDefault();
    }

    /// <summary>Title matches rank above matches that need the category ("View Refresh").</summary>
    private static int? BestScore(CommandPaletteItem item, string query)
    {
        var title = TextFolding.SubsequenceScore(item.Title, query);
        var withCategory = TextFolding.SubsequenceScore($"{item.Category} {item.Title}", query) - 1;
        return title is null ? withCategory : withCategory is null ? title : Math.Max(title.Value, withCategory.Value);
    }
}
