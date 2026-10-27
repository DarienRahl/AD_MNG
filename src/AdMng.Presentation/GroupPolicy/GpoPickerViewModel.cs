using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.Dialogs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy;

/// <summary>A GPO in <see cref="GpoPickerViewModel"/>.</summary>
public sealed partial class GpoPickerItem(GpoInfo gpo, string statusText) : ObservableObject
{
    public GpoInfo Gpo { get; } = gpo;

    public string Name => Gpo.DisplayName;

    public string StatusText { get; } = statusText;

    /// <summary>Checked (multi-select).</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

/// <summary>
/// Choose GPOs of the window's domain ("Połącz istniejący GPO…", "Porównaj z…"): the GPOs read on the session's DC, a name
/// filter, single or multiple selection; GPOs in <c>exclude</c> are not offered.
/// </summary>
public sealed partial class GpoPickerViewModel : WindowViewModelBase
{
    private readonly GroupPolicyReader _reader;
    private readonly HashSet<Guid> _exclude;
    private readonly string _title;
    private IReadOnlyList<GpoPickerItem> _all = [];

    public GpoPickerViewModel(DialogServices services, GroupPolicyReader reader, string title, bool multiSelect, IEnumerable<Guid> exclude)
        : base(services)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _title = title ?? string.Empty;
        MultiSelect = multiSelect;
        _exclude = [.. exclude ?? []];
    }

    public override string WindowTitle => _title;

    public bool MultiSelect { get; }

    [ObservableProperty]
    public partial IReadOnlyList<GpoPickerItem> Items { get; private set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    public partial GpoPickerItem? SelectedItem { get; set; }

    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>The chosen GPOs (set by OK).</summary>
    public IReadOnlyList<GpoInfo> Result { get; private set; } = [];

    public override async Task InitializeAsync()
    {
        if (Session is not { } session)
        {
            Error = NoSessionMessage;
            return;
        }

        IsLoading = true;
        try
        {
            var gpos = await Task.Run(() => _reader.ReadPoliciesAsync(session, CancellationToken.None)).ConfigureAwait(true);
            _all = [.. gpos.Where(g => !_exclude.Contains(g.Id)).Select(g => new GpoPickerItem(g, GroupPolicyText.Status(Localizer, g.Status)))];
            foreach (var item in _all)
            {
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(GpoPickerItem.IsChecked))
                    {
                        OkCommand.NotifyCanExecuteChanged();
                    }
                };
            }

            ApplyFilter();
        }
        catch (Exception ex) when (GroupPolicyText.IsExpected(ex))
        {
            Error = GroupPolicyText.Error(Localizer, Errors, ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var filter = FilterText?.Trim() ?? string.Empty;
        Items = filter.Length == 0 ? _all : [.. _all.Where(i => i.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase))];
        if (SelectedItem is not null && !Items.Contains(SelectedItem))
        {
            SelectedItem = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanOk))]
    private void Ok()
    {
        Result = MultiSelect ? [.. _all.Where(i => i.IsChecked).Select(i => i.Gpo)] : SelectedItem is { } item ? [item.Gpo] : [];
        if (Result.Count > 0)
        {
            RequestClose(true);
        }
    }

    private bool CanOk() => MultiSelect ? _all.Any(i => i.IsChecked) : SelectedItem is not null;

    [RelayCommand]
    private void Cancel() => RequestClose(false);
}
