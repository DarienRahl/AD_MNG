using System.Globalization;
using AdMng.Watchdog.Contracts.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// "Wiadomości do użytkowników" on the Channels and SMTP sub-tab (<see cref="UserMessagingSettings"/>, README U1b): the
/// switch that lets AD Manager send messages to users through the service's SMTP channel, the hourly and daily limits
/// and the allowed senders (one address or <c>@domain</c> per line). Every edit goes to the working configuration; the
/// service's issues of the "userMessages." section are shown next to the fields.
/// </summary>
public sealed partial class WatchdogUserMessagingViewModel : ObservableObject
{
    public const string Prefix = "userMessages.";

    private readonly WatchdogServices _services;
    private readonly WatchdogConfigSession _session;
    private readonly UserMessagingSettings _original;
    private readonly bool _loading;

    public WatchdogUserMessagingViewModel(WatchdogServices services, WatchdogConfigSession session)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _original = session.Working?.UserMessages ?? new UserMessagingSettings();
        _loading = true;
        Enabled = _original.Enabled;
        MaxMessagesPerHour = _original.MaxMessagesPerHour.ToString(CultureInfo.InvariantCulture);
        MaxMessagesPerDay = _original.MaxMessagesPerDay.ToString(CultureInfo.InvariantCulture);
        AllowedSenders = string.Join(Environment.NewLine, _original.AllowedSenders ?? []);
        _loading = false;
        RefreshErrors();
    }

    public bool CanEdit => _session.CanEdit;

    public WatchdogFieldErrors Errors { get; } = new();

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial string MaxMessagesPerHour { get; set; }

    [ObservableProperty]
    public partial string MaxMessagesPerDay { get; set; }

    /// <summary>One address or <c>@domain</c> per line (commas and semicolons separate too).</summary>
    [ObservableProperty]
    public partial string AllowedSenders { get; set; }

    /// <summary>A limit holds no number.</summary>
    [ObservableProperty]
    public partial string? NumbersError { get; private set; }

    /// <summary>The issues of the allowed senders (any line).</summary>
    [ObservableProperty]
    public partial string? AllowedSendersError { get; private set; }

    /// <summary>The settings built from the fields.</summary>
    public UserMessagingSettings Build() => _original with
    {
        Enabled = Enabled,
        MaxMessagesPerHour = Number(MaxMessagesPerHour, _original.MaxMessagesPerHour),
        MaxMessagesPerDay = Number(MaxMessagesPerDay, _original.MaxMessagesPerDay),
        AllowedSenders = [.. AllowedSenders.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(a => a.Length > 0)],
    };

    /// <summary>Shows the service's issues of the "userMessages." section.</summary>
    public void RefreshErrors()
    {
        Errors.Set(_session.IssuesFor(Prefix), Prefix);
        AllowedSendersError = Errors.All.Where(e => e.Key.StartsWith("allowedSenders", StringComparison.OrdinalIgnoreCase)).Select(e => e.Value).FirstOrDefault();
    }

    partial void OnEnabledChanged(bool value) => Commit();

    partial void OnMaxMessagesPerHourChanged(string value) => Commit();

    partial void OnMaxMessagesPerDayChanged(string value) => Commit();

    partial void OnAllowedSendersChanged(string value) => Commit();

    private static int Number(string text, int fallback) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static bool IsNumber(string text) => int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _);

    private void Commit()
    {
        if (_loading || !CanEdit)
        {
            return;
        }

        NumbersError = IsNumber(MaxMessagesPerHour) && IsNumber(MaxMessagesPerDay)
            ? null
            : _services.Localizer[WatchdogDisplay.ValidationKey("range")];
        var settings = Build();
        _session.Update(c => c with { UserMessages = settings });
    }
}
