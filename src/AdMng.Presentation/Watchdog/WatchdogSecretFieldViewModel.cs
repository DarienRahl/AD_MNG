using AdMng.Presentation.Localization;
using AdMng.Watchdog.Contracts.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// A write-only secret (SMTP password, OAuth client secret, channel URL, webhook signing secret): shows only "ustawione" /
/// "nieustawione" and the service's hint; typing sets <see cref="SecretField.NewValue"/>, "Wyczyść" sets
/// <see cref="SecretField.Clear"/>. The value is never exposed for binding and never re-displayed; it lives in this object
/// and the working configuration until the next save or revert (both rebuild the editors).
/// </summary>
public sealed partial class WatchdogSecretFieldViewModel : ObservableObject
{
    private readonly ILocalizer _localizer;
    private readonly Action _changed;
    private string? _newValue;

    public WatchdogSecretFieldViewModel(SecretField field, ILocalizer localizer, Action changed)
    {
        ArgumentNullException.ThrowIfNull(field);
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        IsSet = field.IsSet;
        Hint = field.Hint;
        _newValue = field.NewValue;
        IsCleared = field.Clear;
    }

    /// <summary>Asks the view to focus the input (e.g. the destination changed: the secret must be entered again).</summary>
    public event EventHandler? FocusRequested;

    /// <summary>A secret is stored on the service.</summary>
    public bool IsSet { get; }

    /// <summary>"https://siem.contoso.com/hooks/…", "whsec_…Xw==" – never the value.</summary>
    public string? Hint { get; }

    public bool HasHint => !string.IsNullOrEmpty(Hint);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(HasNewValue))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    public partial bool IsCleared { get; private set; }

    /// <summary>A new value was typed (it is sent with the next save or test).</summary>
    public bool HasNewValue => _newValue is not null;

    /// <summary>"ustawione" / "nieustawione" / "nowa wartość – zapisz" / "zostanie usunięte".</summary>
    public string StateText => IsCleared
        ? _localizer[L.Watchdog.SecretCleared]
        : HasNewValue ? _localizer[L.Watchdog.SecretNew] : _localizer[IsSet ? L.Watchdog.SecretSet : L.Watchdog.SecretNotSet];

    /// <summary>The service reported an issue for this secret (e.g. "secretRebindRequired").</summary>
    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>Takes the typed value (from a password box); empty = no new value.</summary>
    public void SetNewValue(string? value)
    {
        var newValue = string.IsNullOrEmpty(value) ? null : value;
        if (string.Equals(newValue, _newValue, StringComparison.Ordinal) && !IsCleared)
        {
            return;
        }

        _newValue = newValue;
        IsCleared = false;
        OnPropertyChanged(nameof(HasNewValue));
        OnPropertyChanged(nameof(StateText));
        UndoCommand.NotifyCanExecuteChanged();
        _changed();
    }

    /// <summary>The field as sent to the service (NewValue and Clear never both).</summary>
    public SecretField ToField() => new() { IsSet = IsSet, Hint = Hint, NewValue = IsCleared ? null : _newValue, Clear = IsCleared && IsSet };

    /// <summary>Focuses the input (the view listens).</summary>
    public void RequestFocus() => FocusRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>"Wyczyść": removes the stored secret with the next save.</summary>
    [RelayCommand]
    private void Clear()
    {
        _newValue = null;
        IsCleared = IsSet;
        OnPropertyChanged(nameof(HasNewValue));
        OnPropertyChanged(nameof(StateText));
        UndoCommand.NotifyCanExecuteChanged();
        _changed();
    }

    /// <summary>Forgets the typed value / the clear request.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        _newValue = null;
        IsCleared = false;
        OnPropertyChanged(nameof(HasNewValue));
        OnPropertyChanged(nameof(StateText));
        _changed();
    }

    private bool CanUndo() => IsCleared || HasNewValue;

    public override string ToString() => $"WatchdogSecretField({StateText})";
}
