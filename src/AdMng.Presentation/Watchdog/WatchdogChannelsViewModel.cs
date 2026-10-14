using System.Collections.ObjectModel;
using System.Globalization;
using AdMng.Presentation.Localization;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// The "Kanały i SMTP" sub-tab: SMTP (<see cref="SmtpSettingsViewModel"/>) with a test e-mail, recipient groups
/// (<see cref="RecipientGroupsViewModel"/>) and the Teams / webhook channels (<see cref="ChannelEditorViewModel"/>), each
/// with a test send. Editors are rebuilt whenever the configuration is loaded, saved or reverted (typed secrets are gone).
/// </summary>
public sealed partial class WatchdogChannelsViewModel : ObservableObject, IDisposable
{
    private readonly WatchdogServices _services;

    public WatchdogChannelsViewModel(WatchdogServices services, WatchdogConfigSession session)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        Session = session ?? throw new ArgumentNullException(nameof(session));
        EmailTest = new DeliveryTestViewModel(services);
        EmailTest.SecretRebindRequired += OnEmailRebindRequired;
        Session.Reloaded += OnReloaded;
        Session.IssuesChanged += OnIssuesChanged;
        Session.PropertyChanged += OnSessionPropertyChanged;
        Rebuild();
    }

    public WatchdogConfigSession Session { get; }

    [ObservableProperty]
    public partial SmtpSettingsViewModel? Smtp { get; private set; }

    [ObservableProperty]
    public partial RecipientGroupsViewModel? RecipientGroups { get; private set; }

    public ObservableCollection<ChannelEditorViewModel> Channels { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveChannelCommand))]
    public partial ChannelEditorViewModel? SelectedChannel { get; set; }

    /// <summary>Recipients of the test e-mail (one or more addresses).</summary>
    [ObservableProperty]
    public partial string TestRecipients { get; set; } = string.Empty;

    public DeliveryTestViewModel EmailTest { get; }

    /// <summary>Texts after a language switch (the editors are rebuilt from the working copy).</summary>
    public void Relocalize() => Rebuild();

    public void Dispose()
    {
        Session.Reloaded -= OnReloaded;
        Session.IssuesChanged -= OnIssuesChanged;
        Session.PropertyChanged -= OnSessionPropertyChanged;
        EmailTest.SecretRebindRequired -= OnEmailRebindRequired;
    }

    /// <summary>Test e-mail with the unsaved SMTP settings.</summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    private Task SendTestEmailAsync()
    {
        if (Smtp is not { } smtp)
        {
            return Task.CompletedTask;
        }

        var settings = smtp.Build();
        var recipients = TestRecipients.Split([',', ';', ' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return EmailTest.RunAsync(token => _services.Client.SendTestEmailAsync(new TestEmailRequest { Recipients = recipients, Settings = settings }, token));
    }

    private bool CanTest() => Session.IsAdmin && Session.IsLoaded && !Session.IsRedacted;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddTeamsChannel() => AddChannel(ChannelType.Teams);

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddWebhookChannel() => AddChannel(ChannelType.Webhook);

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveChannelAsync()
    {
        if (SelectedChannel is not { } channel)
        {
            return;
        }

        var localizer = _services.Localizer;
        if (!await _services.Dialogs.ConfirmAsync(localizer[L.Watchdog.ChannelRemove], localizer.Format(L.Watchdog.ChannelRemoveConfirm, channel.Name), destructive: true).ConfigureAwait(true))
        {
            return;
        }

        var index = channel.Index;
        Session.Update(c => c with { Channels = [.. c.Channels.Where((_, i) => i != index)] });
        Rebuild(Math.Min(index, Channels.Count - 2));
    }

    private bool CanRemove() => CanEdit() && SelectedChannel is not null;

    private bool CanEdit() => Session.CanEdit;

    private void AddChannel(ChannelType type)
    {
        var localizer = _services.Localizer;
        var ids = Session.Working?.Channels.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var baseId = type == ChannelType.Teams ? "teams" : "webhook";
        var id = baseId;
        for (var i = 2; ids.Contains(id); i++)
        {
            id = string.Create(CultureInfo.InvariantCulture, $"{baseId}-{i}");
        }

        var channel = new ChannelSettings
        {
            Id = id,
            Type = type,
            Name = localizer[type == ChannelType.Teams ? L.Watchdog.ChannelNewTeams : L.Watchdog.ChannelNewWebhook],
            DetailLevel = type == ChannelType.Teams ? DetailLevel.Standard : DetailLevel.Minimal,
            Teams = type == ChannelType.Teams ? new TeamsChannelOptions() : null,
            Webhook = type == ChannelType.Webhook ? new WebhookChannelOptions() : null,
        };
        Session.Update(c => c with { Channels = [.. c.Channels, channel] });
        Rebuild(Session.Working?.Channels.Count - 1);
    }

    private void Rebuild(int? selectIndex = null)
    {
        var selectedId = SelectedChannel?.Id;
        Channels.Clear();
        if (Session.Working is not { } working)
        {
            Smtp = null;
            RecipientGroups = null;
            SelectedChannel = null;
            return;
        }

        Smtp = new SmtpSettingsViewModel(_services, Session);
        RecipientGroups = new RecipientGroupsViewModel(_services, Session);
        for (var i = 0; i < working.Channels.Count; i++)
        {
            Channels.Add(new ChannelEditorViewModel(_services, Session, i));
        }

        SelectedChannel = selectIndex is { } index && index >= 0 && index < Channels.Count
            ? Channels[index]
            : Channels.FirstOrDefault(c => string.Equals(c.Id, selectedId, StringComparison.Ordinal)) ?? Channels.FirstOrDefault();
        AddTeamsChannelCommand.NotifyCanExecuteChanged();
        AddWebhookChannelCommand.NotifyCanExecuteChanged();
        SendTestEmailCommand.NotifyCanExecuteChanged();
    }

    private void OnReloaded(object? sender, EventArgs e) => Rebuild();

    private void OnIssuesChanged(object? sender, EventArgs e)
    {
        Smtp?.RefreshErrors();
        RecipientGroups?.RefreshErrors();
        foreach (var channel in Channels)
        {
            channel.RefreshErrors();
        }
    }

    private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WatchdogConfigSession.IsAdmin))
        {
            Rebuild();
        }
    }

    private void OnEmailRebindRequired(object? sender, string path) => Smtp?.FocusSecret(path);
}
