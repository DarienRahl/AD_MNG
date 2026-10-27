using AdMng.Core.Computers.Remote;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Computers;

/// <summary>
/// "Uruchom ponownie…" (K1 design note 4.5): delay 0–1 440 minutes (default from the settings, 10), a message of up to
/// 500 characters shown to the logged-on users (default text from the resources), "close applications without asking",
/// and for anything but a known workstation (server, domain controller, unknown type) the computer name typed as
/// confirmation. Modal; the result is
/// <see cref="Request"/>.
/// </summary>
public sealed partial class RestartComputerViewModel : WindowViewModelBase
{
    public RestartComputerViewModel(DialogServices services, RemoteTarget target)
        : base(services)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        var settings = services.Settings.Current.Computers;
        DelayMinutes = settings.RestartDelayMinutes;
        Message = settings.RestartMessage ?? DefaultMessage(DelayMinutes);
        _messageIsDefault = settings.RestartMessage is null;
    }

    private bool _messageIsDefault;

    public RemoteTarget Target { get; }

    public override string WindowTitle => Localizer.Format(L.RemoteActions.RestartTitle, Target.Name);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial int DelayMinutes { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(MessageLength))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial string Message { get; set; }

    /// <summary>Close applications without asking (force).</summary>
    [ObservableProperty]
    public partial bool Force { get; set; }

    /// <summary>The typed computer name (everything but a known workstation).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial string TypedName { get; set; } = string.Empty;

    /// <summary>Not a known workstation (server, DC, unknown type): the name must be typed.</summary>
    public bool NeedsTypedName => Target.RestartNeedsTypedName;

    public string TypeNamePrompt => Localizer.Format(L.RemoteActions.RestartTypeName, Target.Name);

    public string MessageLength => Localizer.Format(L.RemoteActions.RestartMessageLength, (Message ?? string.Empty).Length, ComputerSettings.MaxRestartMessageLength);

    /// <summary>"Komputer WAW-PC001 uruchomi się ponownie za 10 min."</summary>
    public string Summary => DelayMinutes == 0
        ? Localizer.Format(L.RemoteActions.RestartNow, Target.Name)
        : Localizer.Format(L.RemoteActions.RestartIn, Target.Name, DelayMinutes);

    public string? Error =>
        DelayMinutes is < 0 or > ComputerSettings.MaxRestartDelayMinutes ? Localizer.Format(L.RemoteActions.RestartDelayRange, 0, ComputerSettings.MaxRestartDelayMinutes)
        : (Message ?? string.Empty).Length > ComputerSettings.MaxRestartMessageLength ? Localizer.Format(L.RemoteActions.RestartMessageTooLong, ComputerSettings.MaxRestartMessageLength)
        : NeedsTypedName && !string.Equals(TypedName.Trim(), Target.Name, StringComparison.OrdinalIgnoreCase) ? Localizer.Format(L.RemoteActions.RestartTypeName, Target.Name)
        : null;

    /// <summary>The confirmed request (null until confirmed).</summary>
    public RestartRequest? Request { get; private set; }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        Request = new RestartRequest(TimeSpan.FromMinutes(DelayMinutes), Message ?? string.Empty, Force);
        RequestClose(true);
    }

    private bool CanConfirm() => Error is null;

    [RelayCommand]
    private void Cancel() => RequestClose(false);

    protected override bool UsesSession => false;

    partial void OnDelayMinutesChanged(int value)
    {
        OnPropertyChanged(nameof(Summary));
        if (_messageIsDefault)
        {
            Message = DefaultMessage(value);
            _messageIsDefault = true;
        }
    }

    partial void OnMessageChanged(string value) => _messageIsDefault = false;

    private string DefaultMessage(int minutes) => Localizer.Format(L.RemoteActions.RestartDefaultMessage, minutes);
}
