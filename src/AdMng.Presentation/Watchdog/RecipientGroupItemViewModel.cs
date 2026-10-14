using AdMng.Presentation.Infrastructure;
using AdMng.Watchdog.Contracts.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.Watchdog;

/// <summary>One recipient group: id, name, addresses (one per line) and the detail level of its e-mails.</summary>
public sealed partial class RecipientGroupItemViewModel : ObservableObject
{
    private readonly RecipientGroup _original;
    private readonly Action _changed;
    private readonly bool _loading;

    public RecipientGroupItemViewModel(RecipientGroup group, int index, IReadOnlyList<Option<DetailLevel>> detailLevels, Action changed)
    {
        _original = group ?? throw new ArgumentNullException(nameof(group));
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        Index = index;
        DetailLevels = detailLevels ?? throw new ArgumentNullException(nameof(detailLevels));
        _loading = true;
        Id = group.Id;
        Name = group.Name;
        Addresses = string.Join(Environment.NewLine, group.Addresses);
        DetailLevel = detailLevels.First(d => d.Value == group.DetailLevel);
        _loading = false;
    }

    /// <summary>Position in <c>configuration.recipientGroups</c>.</summary>
    public int Index { get; }

    public IReadOnlyList<Option<DetailLevel>> DetailLevels { get; }

    public WatchdogFieldErrors Errors { get; } = new();

    [ObservableProperty]
    public partial string Id { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; }

    /// <summary>E-mail addresses, one per line (removed in the Viewer's redacted copy).</summary>
    [ObservableProperty]
    public partial string Addresses { get; set; }

    [ObservableProperty]
    public partial Option<DetailLevel> DetailLevel { get; set; }

    /// <summary>"Administratorzy IT (2)" for lists.</summary>
    public string DisplayText => $"{Name} ({AddressList.Count})";

    public IReadOnlyList<string> AddressList =>
        [.. Addresses.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(a => a.Length > 0)];

    public RecipientGroup Build() => _original with
    {
        Id = Id.Trim(),
        Name = Name.Trim(),
        Addresses = AddressList,
        DetailLevel = DetailLevel.Value,
    };

    partial void OnIdChanged(string value) => Changed();

    partial void OnNameChanged(string value) => Changed();

    partial void OnAddressesChanged(string value) => Changed();

    partial void OnDetailLevelChanged(Option<DetailLevel> value) => Changed();

    private void Changed()
    {
        if (!_loading)
        {
            OnPropertyChanged(nameof(DisplayText));
            _changed();
        }
    }
}
