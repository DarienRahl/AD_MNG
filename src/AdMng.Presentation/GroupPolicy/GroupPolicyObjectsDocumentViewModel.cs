using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy;

/// <summary>One GPO of the "Obiekty zasad grupy" list.</summary>
public sealed class GpoRowViewModel
{
    public required GpoInfo Gpo { get; init; }

    public string Name => Gpo.DisplayName;

    public required string StatusText { get; init; }

    /// <summary>The WMI filter's name ("–" when none).</summary>
    public required string WmiFilterText { get; init; }

    /// <summary>"Komputery, contoso.test" (the containers that link the GPO).</summary>
    public required string LinksText { get; init; }

    public int LinkCount { get; init; }

    /// <summary>"komputer 4, użytkownik 0".</summary>
    public required string DirectoryVersionText { get; init; }

    public required string SysvolVersionText { get; init; }

    /// <summary>AD and GPT.INI disagree (the row is marked).</summary>
    public bool HasVersionMismatch => Gpo.VersionMismatch;

    public required string OwnerText { get; init; }

    public required string ChangedText { get; init; }

    public string GuidText => GroupPolicyText.Guid(Gpo.Id);
}

/// <summary>
/// "Obiekty zasad grupy" (design note 5.2): every GPO of the domain with status, WMI filter, links, versions of AD and
/// SYSVOL (a mismatch is marked), owner and modification time; multi-select; New, Copy, Rename, Delete, Backup, Restore,
/// Import settings and Compare (two selected).
/// </summary>
public sealed partial class GroupPolicyObjectsDocumentViewModel(GroupPolicyServices services, GroupPolicyActions actions)
    : GroupPolicyDocumentBase(services, actions)
{
    public override string Title => Localizer[L.GroupPolicy.ObjectsTitle];

    public override ObjectIcon Icon => ObjectIcon.GroupPolicy;

    public override string SelectionText => Selection.Count == 0 ? string.Empty : Localizer.Format(L.GroupPolicy.Selected, Selection.Count);

    [ObservableProperty]
    public partial IReadOnlyList<GpoRowViewModel> Rows { get; private set; } = [];

    /// <summary>The focused row.</summary>
    [ObservableProperty]
    public partial GpoRowViewModel? SelectedRow { get; set; }

    /// <summary>The selected rows (the view sets them; <see cref="SelectedRow"/> alone counts as a selection of one).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText))]
    public partial IReadOnlyList<GpoRowViewModel> Selection { get; set; } = [];

    partial void OnSelectedRowChanged(GpoRowViewModel? value)
    {
        if (value is not null && !Selection.Contains(value))
        {
            Selection = [value];
        }

        RefreshCommands();
    }

    partial void OnSelectionChanged(IReadOnlyList<GpoRowViewModel> value) => RefreshCommands();

    protected override async Task LoadCoreAsync(IDirectorySession session, ISysvolWriteConnection? sysvol, CancellationToken cancellationToken)
    {
        var reader = Gp.Reader;
        var gpos = await Task.Run(() => reader.ReadPoliciesAsync(session, cancellationToken), cancellationToken).ConfigureAwait(true);
        var containers = await Task.Run(() => reader.ReadLinkingContainersAsync(session, cancellationToken), cancellationToken).ConfigureAwait(true);
        var filters = await Task.Run(() => reader.ReadWmiFiltersAsync(session, cancellationToken), cancellationToken).ConfigureAwait(true);
        if (sysvol is not null)
        {
            gpos = await reader.WithSysvolVersionsAsync(sysvol, gpos, cancellationToken).ConfigureAwait(true);
        }

        var owners = await TrusteeNames.ResolveAsync(session, gpos.Select(g => g.OwnerSid).OfType<string>(), Localizer, cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        var links = GroupPolicyReader.LinksByGpo(containers);
        var filterNames = filters.ToDictionary(f => f.Id, f => f.Name, StringComparer.OrdinalIgnoreCase);
        var zone = TimeZoneInfo.Local;
        var selected = Selection.Select(r => r.Gpo.Id).ToHashSet();
        var focused = SelectedRow?.Gpo.Id;
        Rows = [.. gpos.Select(g =>
        {
            var gpoLinks = links.TryGetValue(g.Id, out var list) ? list : [];
            return new GpoRowViewModel
            {
                Gpo = g,
                StatusText = GroupPolicyText.Status(Localizer, g.Status),
                WmiFilterText = g.WmiFilterId is { } id ? filterNames.TryGetValue(id, out var name) ? name : id : "–",
                LinksText = gpoLinks.Count == 0 ? Localizer[L.GroupPolicy.NotLinked] : string.Join(", ", gpoLinks.Select(l => l.ContainerName)),
                LinkCount = gpoLinks.Count,
                DirectoryVersionText = GroupPolicyText.Version(Localizer, g.DirectoryVersion),
                SysvolVersionText = sysvol is null ? "–" : g.SysvolVersion is { } version ? GroupPolicyText.Version(Localizer, version) : Localizer[L.GroupPolicy.GptIniMissing],
                OwnerText = TrusteeNames.NameOf(owners, g.OwnerSid),
                ChangedText = GroupPolicyText.Date(Localizer, g.Changed, zone),
            };
        })];
        Selection = [.. Rows.Where(r => selected.Contains(r.Gpo.Id))];
        SelectedRow = Rows.FirstOrDefault(r => r.Gpo.Id == focused);
        LoadedText = Localizer.Format(L.GroupPolicy.ObjectsCount, Rows.Count, Rows.Count(r => r.HasVersionMismatch));
    }

    protected override void OnNotConnected()
    {
        Rows = [];
        Selection = [];
    }

    protected override void RefreshCommands()
    {
        NewCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        BackupCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
        ImportCommand.NotifyCanExecuteChanged();
        CompareCommand.NotifyCanExecuteChanged();
        OpenCommand.NotifyCanExecuteChanged();
        HistoryCommand.NotifyCanExecuteChanged();
    }

    private GpoInfo? Single => Selection.Count == 1 ? Selection[0].Gpo : null;

    [RelayCommand(CanExecute = nameof(IsSysvolWritable))]
    private Task NewAsync() => RunAsync(() => Actions.NewAsync());

    [RelayCommand(CanExecute = nameof(CanSysvolSingle))]
    private Task CopyAsync() => Single is { } gpo ? RunAsync(() => Actions.CopyAsync(gpo)) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanSingle))]
    private Task RenameAsync() => Single is { } gpo ? RunAsync(() => Actions.RenameAsync(gpo)) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private Task DeleteAsync() => Single is { } gpo ? RunAsync(() => Actions.DeleteAsync(gpo)) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanBackup))]
    private Task BackupAsync() => RunAsync(() => Actions.BackupAsync([.. Selection.Select(r => r.Gpo)]));

    [RelayCommand(CanExecute = nameof(IsSysvolWritable))]
    private Task RestoreAsync() => RunAsync(Actions.RestoreAsync);

    [RelayCommand(CanExecute = nameof(CanSysvolSingle))]
    private Task ImportAsync() => Single is { } gpo ? RunAsync(() => Actions.ImportAsync(gpo)) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanCompare))]
    private Task CompareAsync() => Selection.Count == 2 ? Actions.CompareAsync(Selection[0].Gpo, Selection[1].Gpo) : Task.CompletedTask;

    /// <summary>Opens the GPO's document (double click).</summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open(GpoRowViewModel? row)
    {
        if ((row ?? SelectedRow) is { } target)
        {
            Actions.OpenGpo(target.Gpo.Id, target.Name);
        }
    }

    [RelayCommand(CanExecute = nameof(CanHistory))]
    private Task HistoryAsync() => Single is { } gpo ? Actions.ShowHistoryAsync(gpo) : Task.CompletedTask;

    private bool CanSingle() => !IsLoading && HasSession && Single is not null;

    private bool CanSysvolSingle() => IsSysvolWritable && Single is not null;

    private bool CanDelete() => IsSysvolWritable && Single is { IsDefault: false };

    private bool CanBackup() => IsSysvolWritable && Selection.Count > 0;

    private bool CanCompare() => !IsLoading && HasSession && Selection.Count == 2;

    private bool CanOpen(GpoRowViewModel? row) => (row ?? SelectedRow) is not null;

    private bool CanHistory() => Single is not null && Actions.CanShowHistory;
}
