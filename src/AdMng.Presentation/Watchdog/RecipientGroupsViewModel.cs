using System.Collections.ObjectModel;
using System.Globalization;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Watchdog.Contracts.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Watchdog;

/// <summary>The named recipient lists referenced by rules and the digest (add, remove, edit addresses and detail level).</summary>
public sealed partial class RecipientGroupsViewModel : ObservableObject
{
    private readonly WatchdogServices _services;
    private readonly WatchdogConfigSession _session;
    private readonly IReadOnlyList<Option<DetailLevel>> _detailLevels;

    public RecipientGroupsViewModel(WatchdogServices services, WatchdogConfigSession session)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _detailLevels = [.. Enum.GetValues<DetailLevel>().Select(d => new Option<DetailLevel>(d, services.Localizer[WatchdogDisplay.DetailLevelKey(d)]))];
        var groups = session.Working?.RecipientGroups ?? [];
        for (var i = 0; i < groups.Count; i++)
        {
            Groups.Add(new RecipientGroupItemViewModel(groups[i], i, _detailLevels, Commit));
        }

        SelectedGroup = Groups.FirstOrDefault();
        RefreshErrors();
    }

    public bool CanEdit => _session.CanEdit;

    public ObservableCollection<RecipientGroupItemViewModel> Groups { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveGroupCommand))]
    public partial RecipientGroupItemViewModel? SelectedGroup { get; set; }

    public void RefreshErrors()
    {
        foreach (var group in Groups)
        {
            var prefix = string.Create(CultureInfo.InvariantCulture, $"recipientGroups[{group.Index}].");
            group.Errors.Set(_session.IssuesFor(prefix), prefix);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddGroup()
    {
        var ids = Groups.Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var id = "nowa-grupa";
        for (var i = 2; ids.Contains(id); i++)
        {
            id = string.Create(CultureInfo.InvariantCulture, $"nowa-grupa-{i}");
        }

        var group = new RecipientGroupItemViewModel(new RecipientGroup { Id = id, Name = _services.Localizer[L.Watchdog.GroupsNewName] }, Groups.Count, _detailLevels, Commit);
        Groups.Add(group);
        SelectedGroup = group;
        Commit();
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveGroupAsync()
    {
        if (SelectedGroup is not { } group)
        {
            return;
        }

        var localizer = _services.Localizer;
        if (!await _services.Dialogs.ConfirmAsync(localizer[L.Watchdog.GroupsRemove], localizer.Format(L.Watchdog.GroupsRemoveConfirm, group.Name), destructive: true).ConfigureAwait(true))
        {
            return;
        }

        Groups.Remove(group);
        SelectedGroup = Groups.FirstOrDefault();
        Commit();
    }

    private bool CanRemove() => CanEdit && SelectedGroup is not null;

    private void Commit()
    {
        if (!CanEdit)
        {
            return;
        }

        var groups = Groups.Select(g => g.Build()).ToList();
        _session.Update(c => c with { RecipientGroups = groups });
    }
}
