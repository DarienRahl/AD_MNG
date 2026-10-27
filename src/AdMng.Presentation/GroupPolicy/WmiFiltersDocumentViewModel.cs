using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy;

/// <summary>A WMI filter of the domain with the GPOs that use it.</summary>
public sealed class WmiFilterRowViewModel
{
    public required WmiFilterInfo Filter { get; init; }

    public string Name => Filter.Name;

    public string Description => Filter.Description ?? string.Empty;

    /// <summary>The queries, one per line ("root\CIMv2: SELECT …").</summary>
    public required string QueriesText { get; init; }

    /// <summary>The GPOs linked to the filter ("–" when none).</summary>
    public required string GposText { get; init; }

    public int GpoCount { get; init; }

    public required string AuthorText { get; init; }

    public required string ChangedText { get; init; }
}

/// <summary>"Filtry WMI" (design note 5.2): the filters with their queries and the GPOs that use them; New, Edit, Delete.</summary>
public sealed partial class WmiFiltersDocumentViewModel(GroupPolicyServices services, GroupPolicyActions actions)
    : GroupPolicyDocumentBase(services, actions)
{
    public override string Title => Localizer[L.GroupPolicy.WmiFiltersTitle];

    public override ObjectIcon Icon => ObjectIcon.WmiFilter;

    [ObservableProperty]
    public partial IReadOnlyList<WmiFilterRowViewModel> Rows { get; private set; } = [];

    [ObservableProperty]
    public partial WmiFilterRowViewModel? SelectedRow { get; set; }

    partial void OnSelectedRowChanged(WmiFilterRowViewModel? value) => RefreshCommands();

    protected override async Task LoadCoreAsync(IDirectorySession session, ISysvolWriteConnection? sysvol, CancellationToken cancellationToken)
    {
        var reader = Gp.Reader;
        var filters = await Task.Run(() => reader.ReadWmiFiltersAsync(session, cancellationToken), cancellationToken).ConfigureAwait(true);
        var gpos = await Task.Run(() => reader.ReadPoliciesAsync(session, cancellationToken), cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        var zone = TimeZoneInfo.Local;
        var selected = SelectedRow?.Filter.Id;
        Rows = [.. filters.Select(f =>
        {
            var users = gpos.Where(g => string.Equals(g.WmiFilterId, f.Id, StringComparison.OrdinalIgnoreCase)).Select(g => g.DisplayName).ToList();
            return new WmiFilterRowViewModel
            {
                Filter = f,
                QueriesText = f.Queries.Count == 0
                    ? Localizer[L.GroupPolicy.WmiQueriesUnreadable]
                    : string.Join(Environment.NewLine, f.Queries.Select(q => q.Namespace + ": " + q.Query)),
                GposText = users.Count == 0 ? "–" : string.Join(", ", users),
                GpoCount = users.Count,
                AuthorText = f.Author ?? "–",
                ChangedText = GroupPolicyText.Date(Localizer, f.Changed ?? f.Created, zone),
            };
        })];
        SelectedRow = Rows.FirstOrDefault(r => string.Equals(r.Filter.Id, selected, StringComparison.OrdinalIgnoreCase));
        LoadedText = Localizer.Format(L.GroupPolicy.WmiFiltersCount, Rows.Count);
    }

    protected override void OnNotConnected() => Rows = [];

    protected override void RefreshCommands()
    {
        NewCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task NewAsync() => RunAsync(Actions.NewWmiFilterAsync);

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private Task EditAsync() => SelectedRow is { } row ? RunAsync(() => Actions.EditWmiFilterAsync(row.Filter)) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private Task DeleteAsync() => SelectedRow is { } row ? RunAsync(() => Actions.DeleteWmiFilterAsync(row.Filter)) : Task.CompletedTask;

    private bool CanEdit() => !IsLoading && HasSession;

    private bool CanEditSelected() => CanEdit() && SelectedRow is not null;
}
