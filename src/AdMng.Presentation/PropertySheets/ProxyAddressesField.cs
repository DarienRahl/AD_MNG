using System.Collections.ObjectModel;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Model;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.PropertySheets;

/// <summary>
/// One proxyAddresses value, "type:address". An upper-case type ("SMTP:") marks the primary address of that type,
/// a lower-case one ("smtp:") a secondary address (Exchange convention).
/// </summary>
public sealed record ProxyAddressViewModel(string Prefix, string Address)
{
    /// <summary>Type for display (upper case): SMTP, X500, SIP...</summary>
    public string Type => Prefix.ToUpperInvariant();

    /// <summary>Primary address of its type (upper-case prefix).</summary>
    public bool IsPrimary => Prefix.Length > 0 && Prefix.All(c => !char.IsLetter(c) || char.IsUpper(c));

    public bool IsSmtp => Prefix.Equals(ProxyAddressesFieldViewModel.SmtpType, StringComparison.OrdinalIgnoreCase);

    /// <summary>The stored value.</summary>
    public string Value => Prefix.Length == 0 ? Address : $"{Prefix}:{Address}";

    public static ProxyAddressViewModel Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        return colon <= 0 ? new ProxyAddressViewModel(string.Empty, value) : new ProxyAddressViewModel(value[..colon], value[(colon + 1)..]);
    }

    public override string ToString() => Value;
}

/// <summary>
/// E-mail addresses editor (proxyAddresses) with the primary SMTP rule: exactly one "SMTP:" address when there are
/// SMTP addresses; making an address primary demotes the previous one to "smtp:".
/// </summary>
public sealed partial class ProxyAddressesFieldViewModel : PropertyFieldViewModel
{
    public const string SmtpType = "smtp";

    private IReadOnlyList<string> _original = [];

    internal ProxyAddressesFieldViewModel(FieldContext context)
        : base(context, Attr.ProxyAddresses, context.Label(Attr.ProxyAddresses))
    {
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(PrimarySmtpAddress));
            OnEdited();
        };
    }

    /// <summary>Raised when the primary SMTP address changed through this editor (the page updates the E-mail field).</summary>
    public event EventHandler<string?>? PrimarySmtpChanged;

    public ObservableCollection<ProxyAddressViewModel> Items { get; } = [];

    /// <summary>Types offered when adding an address.</summary>
    public IReadOnlyList<string> Types { get; } = ["SMTP", "X500", "SIP", "X400", "EUM"];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(SetPrimaryCommand), nameof(CopyAddressCommand))]
    public partial ProxyAddressViewModel? SelectedItem { get; set; }

    [ObservableProperty]
    public partial string NewType { get; set; } = "SMTP";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string NewAddress { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? NewAddressError { get; private set; }

    /// <summary>The primary SMTP address (without prefix), or null.</summary>
    public string? PrimarySmtpAddress => Items.FirstOrDefault(i => i.IsSmtp && i.IsPrimary)?.Address;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        var type = (NewType ?? string.Empty).Trim().TrimEnd(':');
        var address = NewAddress.Trim();
        if (type.Length == 0 || type.Any(c => !char.IsAsciiLetterOrDigit(c)) || address.Length == 0
            || (type.Equals(SmtpType, StringComparison.OrdinalIgnoreCase) && !IsSmtpAddress(address)))
        {
            NewAddressError = Localizer[L.Fields.InvalidAddress];
            return;
        }

        if (Items.Any(i => i.Type.Equals(type, StringComparison.OrdinalIgnoreCase) && i.Address.Equals(address, StringComparison.OrdinalIgnoreCase)))
        {
            NewAddressError = Localizer[L.Fields.DuplicateValue];
            return;
        }

        var hasPrimary = Items.Any(i => i.Type.Equals(type, StringComparison.OrdinalIgnoreCase) && i.IsPrimary);
        var item = new ProxyAddressViewModel(hasPrimary ? type.ToLowerInvariant() : type.ToUpperInvariant(), address);
        NewAddressError = null;
        Items.Add(item);
        NewAddress = string.Empty;
        if (!hasPrimary && item.IsSmtp)
        {
            PrimarySmtpChanged?.Invoke(this, item.Address);
        }
    }

    private bool CanAdd() => IsEditable && !string.IsNullOrWhiteSpace(NewAddress);

    [RelayCommand(CanExecute = nameof(CanChangeSelected))]
    private void Remove()
    {
        if (SelectedItem is not { } selected)
        {
            return;
        }

        var index = Items.IndexOf(selected);
        Items.RemoveAt(index);
        SelectedItem = null;
        if (selected.IsPrimary && Items.FirstOrDefault(i => i.Type == selected.Type) is { } next)
        {
            // Keep exactly one primary address of the type (Exchange would otherwise pick one at random).
            Promote(next);
        }
    }

    [RelayCommand(CanExecute = nameof(CanChangeSelected))]
    private void SetPrimary()
    {
        if (SelectedItem is { IsPrimary: false } selected)
        {
            Promote(selected);
        }
    }

    private bool CanChangeSelected() => IsEditable && SelectedItem is not null;

    /// <summary>
    /// Copies the selected address without its type prefix ("smtp:", "SIP:"...) - what goes into an e-mail or a ticket.
    /// Available on read-only fields too.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyAddress() =>
        ClipboardCopy.Value(Context.Services.Clipboard, Context.Services.Notifications, Localizer, SelectedItem?.Address, Label);

    private bool HasSelection() => SelectedItem is not null;

    protected override void OnAccessChanged()
    {
        AddCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        SetPrimaryCommand.NotifyCanExecuteChanged();
    }

    protected override void LoadCore(DirectoryObject obj)
    {
        _original = obj.GetStrings(Attribute);
        RevertCore();
        NewAddress = string.Empty;
        NewAddressError = null;
    }

    protected override void RevertCore()
    {
        Items.Clear();
        foreach (var value in _original)
        {
            Items.Add(ProxyAddressViewModel.Parse(value));
        }
    }

    protected override bool ComputeIsDirty() =>
        Items.Count != _original.Count
        || !Items.Select(i => i.Value).Order(StringComparer.Ordinal).SequenceEqual(_original.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    protected override string? Validate()
    {
        var smtp = Items.Where(i => i.IsSmtp).ToList();
        return smtp.Count > 0 && smtp.Count(i => i.IsPrimary) != 1 ? Localizer[L.Fields.OnePrimarySmtp] : null;
    }

    protected override IEnumerable<BatchOperation> CreateOperations() => [new SetValuesOperation(Attribute, [.. Items.Select(i => i.Value)])];

    private static bool IsSmtpAddress(string address)
    {
        var at = address.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && at == address.LastIndexOf('@') && at < address.Length - 1 && !address.Any(char.IsWhiteSpace);
    }

    private void Promote(ProxyAddressViewModel item)
    {
        for (var i = 0; i < Items.Count; i++)
        {
            var current = Items[i];
            if (!current.Type.Equals(item.Type, StringComparison.Ordinal))
            {
                continue;
            }

            var promoted = ReferenceEquals(current, item);
            var prefix = promoted ? current.Type : current.Type.ToLowerInvariant();
            if (!string.Equals(prefix, current.Prefix, StringComparison.Ordinal))
            {
                Items[i] = current with { Prefix = prefix };
            }
        }

        if (item.IsSmtp)
        {
            PrimarySmtpChanged?.Invoke(this, item.Address);
        }
    }
}
