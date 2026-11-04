using System.Globalization;
using System.Net;
using AdMng.Core.EventLogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.EventLogs;

/// <summary>The query of the reader: the builder's fields, the raw XPath with its live check, the saved queries.</summary>
public sealed partial class EventLogReaderDocumentViewModel
{
    /// <summary>Event IDs one query may name (ranges expanded).</summary>
    public const int MaxEventIds = 200;

    /// <summary>Values of one text field (comma-separated providers).</summary>
    public const int MaxProviders = 20;

    /// <summary>The fields an account name can be matched in (Security channel).</summary>
    public static IReadOnlyList<string> AccountFields { get; } = ["TargetUserName", "SubjectUserName"];

    /// <summary>The fields a SID can be matched in.</summary>
    public static IReadOnlyList<string> SidFields { get; } = ["TargetSid", "TargetUserSid", "SubjectUserSid"];

    /// <summary>The fields a workstation name can be matched in (4624/4625 and 4776).</summary>
    public static IReadOnlyList<string> WorkstationFields { get; } = ["WorkstationName", "Workstation"];

    /// <summary>The channel (a common one or one of the first computer's).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSecurityChannel), nameof(FieldsNote))]
    public partial string Channel { get; set; } = EventLogText.SecurityChannel;

    /// <summary>The channels offered: the common ones, then the first computer's after "Wczytaj kanały".</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> ChannelOptions { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<Option<EventLogTimePreset>> TimePresetOptions { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomTime))]
    public partial Option<EventLogTimePreset> SelectedTimePreset { get; set; } = new(EventLogTimePreset.Last24Hours, string.Empty);

    public bool IsCustomTime => SelectedTimePreset.Value == EventLogTimePreset.Custom;

    /// <summary>"Własny": the start in local time.</summary>
    [ObservableProperty]
    public partial DateTime? CustomFrom { get; set; }

    /// <summary>"Własny": the last day in local time, included (null: now).</summary>
    [ObservableProperty]
    public partial DateTime? CustomTo { get; set; }

    /// <summary>"4625, 4740, 4768-4771".</summary>
    [ObservableProperty]
    public partial string EventIdsText { get; set; } = string.Empty;

    public IReadOnlyList<EventLogLevelOption> LevelOptions { get; }

    /// <summary>Comma-separated provider names.</summary>
    [ObservableProperty]
    public partial string ProvidersText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<Option<EventAuditOutcome?>> OutcomeOptions { get; private set; } = [];

    [ObservableProperty]
    public partial Option<EventAuditOutcome?> SelectedOutcome { get; set; } = new(null, string.Empty);

    [ObservableProperty]
    public partial string AccountText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AccountField { get; set; } = "TargetUserName";

    [ObservableProperty]
    public partial string SidText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SidField { get; set; } = "TargetSid";

    [ObservableProperty]
    public partial string IpText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WorkstationText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WorkstationField { get; set; } = "WorkstationName";

    /// <summary>The account, SID, address and workstation fields are data conditions of the Security channel only.</summary>
    public bool IsSecurityChannel => string.Equals(Channel?.Trim(), EventLogText.SecurityChannel, StringComparison.OrdinalIgnoreCase);

    /// <summary>The note beside the fields when another channel is chosen.</summary>
    public string FieldsNote => IsSecurityChannel ? string.Empty : Localizer[L.EventLogs.FieldsOnlySecurity];

    /// <summary>"Tryb XPath (ekspert)": the raw expression replaces the builder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRawXPathValid))]
    public partial bool IsRawMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRawXPathValid))]
    public partial string RawXPath { get; set; } = string.Empty;

    /// <summary>The result of the live structural check of <see cref="RawXPath"/>, null when it passes (or raw mode is off).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRawXPathValid))]
    public partial string? RawXPathError { get; private set; }

    public bool IsRawXPathValid => IsRawMode && RawXPathError is null && !string.IsNullOrWhiteSpace(RawXPath);

    /// <summary>Why the query cannot run (an ID, a value, the range …), null when it can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQueryError))]
    public partial string? QueryError { get; private set; }

    public bool HasQueryError => QueryError is not null;

    // ---- Saved queries ----------------------------------------------------------------------------------------------------

    /// <summary>The names of the saved queries.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> SavedQueries { get; private set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadQueryCommand), nameof(DeleteQueryCommand))]
    public partial string? SelectedSavedQuery { get; set; }

    /// <summary>The name "Zapisz zapytanie" saves under.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveQueryCommand))]
    public partial string SavedQueryName { get; set; } = string.Empty;

    /// <summary>"Zapisz zapytanie": name, channel, IDs, levels, providers, conditions and XPath as typed, the selected computers.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveQuery))]
    private async Task SaveQueryAsync()
    {
        var name = SavedQueryName.Trim();
        if (name.Length == 0)
        {
            QueryError = Localizer[L.EventLogs.QueryNameRequired];
            return;
        }

        // A new name beyond the limit would be dropped by Normalize (it keeps the first MaxSavedQueries): say so instead.
        var existing = Services.Ui.Settings.Current.EventLogs.SavedQueries;
        if (existing.Count >= EventLogSettings.MaxSavedQueries
            && !existing.Any(q => string.Equals(q.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            Services.Ui.Notifications.Show(NotificationSeverity.Warning, Localizer.Format(L.EventLogs.SavedQueriesFull, EventLogSettings.MaxSavedQueries));
            return;
        }

        var saved = CurrentQuery(name);
        await Services.Ui.Settings.UpdateAsync(s =>
        {
            s.EventLogs.SavedQueries.RemoveAll(q => string.Equals(q.Name, name, StringComparison.CurrentCultureIgnoreCase));
            s.EventLogs.SavedQueries.Add(saved);
            s.EventLogs.Normalize();
        }).ConfigureAwait(true);
        LoadSavedQueryNames();
        SelectedSavedQuery = SavedQueries.FirstOrDefault(n => string.Equals(n, saved.Name, StringComparison.CurrentCultureIgnoreCase));
        if (SelectedSavedQuery is null)
        {
            // Not kept after all (the settings changed meanwhile): never report it saved.
            Services.Ui.Notifications.Show(NotificationSeverity.Warning, Localizer.Format(L.EventLogs.SavedQueriesFull, EventLogSettings.MaxSavedQueries));
            return;
        }

        Services.Ui.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.EventLogs.QuerySaved, SelectedSavedQuery));
    }

    private bool CanSaveQuery() => !string.IsNullOrWhiteSpace(SavedQueryName);

    /// <summary>"Wczytaj": the builder takes the saved query; its computers are selected (added when missing).</summary>
    [RelayCommand(CanExecute = nameof(HasSavedSelection))]
    private void LoadQuery()
    {
        if (Services.Ui.Settings.Current.EventLogs.SavedQueries.FirstOrDefault(q => string.Equals(q.Name, SelectedSavedQuery, StringComparison.Ordinal)) is not { } saved)
        {
            return;
        }

        Channel = saved.Channel;
        SelectedTimePreset = TimePresetOptions.FirstOrDefault(o => o.Value == saved.TimePreset) ?? SelectedTimePreset;
        EventIdsText = saved.EventIds;
        foreach (var level in LevelOptions)
        {
            level.IsSelected = saved.Levels.Contains(level.Level);
        }

        ProvidersText = saved.Providers;
        SelectedOutcome = OutcomeOptions.FirstOrDefault(o => o.Value == saved.Outcome) ?? OutcomeOptions[0];
        AccountText = saved.Account;
        AccountField = AccountFields.Contains(saved.AccountField) ? saved.AccountField : AccountFields[0];
        SidText = saved.Sid;
        SidField = SidFields.Contains(saved.SidField) ? saved.SidField : SidFields[0];
        IpText = saved.IpAddress;
        WorkstationText = saved.Workstation;
        WorkstationField = WorkstationFields.Contains(saved.WorkstationField) ? saved.WorkstationField : WorkstationFields[0];
        IsRawMode = saved.IsRawMode;
        RawXPath = saved.RawXPath;
        SavedQueryName = saved.Name;
        if (saved.Hosts.Count > 0)
        {
            foreach (var source in Sources.Where(s => !s.IsFiles))
            {
                source.IsSelected = saved.Hosts.Contains(source.Host, StringComparer.OrdinalIgnoreCase);
            }

            foreach (var host in saved.Hosts.Where(h => !Sources.Any(s => string.Equals(s.Host, h, StringComparison.OrdinalIgnoreCase))))
            {
                AddSource(new EventLogSourceViewModel(EventLogSourceKind.Computer, host, Localizer[L.EventLogs.KindComputer]));
            }
        }

        ValidateQuery();
    }

    /// <summary>"Usuń": removes the saved query after a confirmation.</summary>
    [RelayCommand(CanExecute = nameof(HasSavedSelection))]
    private async Task DeleteQueryAsync()
    {
        if (SelectedSavedQuery is not { } name
            || !await Services.Ui.Dialogs.ConfirmAsync(Localizer[L.EventLogs.DeleteQuery], Localizer.Format(L.EventLogs.DeleteQueryConfirm, name), destructive: true).ConfigureAwait(true))
        {
            return;
        }

        await Services.Ui.Settings.UpdateAsync(s => s.EventLogs.SavedQueries.RemoveAll(q => string.Equals(q.Name, name, StringComparison.Ordinal))).ConfigureAwait(true);
        LoadSavedQueryNames();
        SelectedSavedQuery = null;
    }

    private bool HasSavedSelection() => SelectedSavedQuery is not null;

    private void LoadSavedQueryNames() =>
        SavedQueries = [.. Services.Ui.Settings.Current.EventLogs.SavedQueries.Select(q => q.Name).Order(StringComparer.Create(Localizer.Culture, ignoreCase: true))];

    /// <summary>What "Zapisz zapytanie" stores: the builder as typed, never a value read from a log.</summary>
    private EventLogSavedQuery CurrentQuery(string name) => new()
    {
        Name = name,
        Channel = Channel.Trim(),
        TimePreset = SelectedTimePreset.Value,
        EventIds = EventIdsText.Trim(),
        Levels = [.. LevelOptions.Where(l => l.IsSelected).Select(l => l.Level)],
        Providers = ProvidersText.Trim(),
        Outcome = SelectedOutcome.Value,
        Account = AccountText.Trim(),
        AccountField = AccountField,
        Sid = SidText.Trim(),
        SidField = SidField,
        IpAddress = IpText.Trim(),
        Workstation = WorkstationText.Trim(),
        WorkstationField = WorkstationField,
        IsRawMode = IsRawMode,
        RawXPath = RawXPath.Trim(),
        Hosts = [.. Sources.Where(s => s.IsSelected && !s.IsFiles).Select(s => s.Host)],
    };

    // ---- Building -------------------------------------------------------------------------------------------------------

    partial void OnChannelChanged(string value) => ValidateQuery();

    partial void OnEventIdsTextChanged(string value) => ValidateQuery();

    partial void OnProvidersTextChanged(string value) => ValidateQuery();

    partial void OnAccountTextChanged(string value) => ValidateQuery();

    partial void OnSidTextChanged(string value) => ValidateQuery();

    partial void OnIpTextChanged(string value) => ValidateQuery();

    partial void OnWorkstationTextChanged(string value) => ValidateQuery();

    partial void OnSelectedTimePresetChanged(Option<EventLogTimePreset> value) => ValidateQuery();

    partial void OnCustomFromChanged(DateTime? value) => ValidateQuery();

    partial void OnCustomToChanged(DateTime? value) => ValidateQuery();

    partial void OnIsRawModeChanged(bool value) => ValidateQuery();

    partial void OnRawXPathChanged(string value) => ValidateQuery();

    /// <summary>Checks the builder (and the raw XPath live) and shows the first problem.</summary>
    private void ValidateQuery()
    {
        if (LevelOptions is null || OutcomeOptions.Count == 0 || TimePresetOptions.Count == 0)
        {
            return;
        }

        RawXPathError = IsRawMode && !EventQueryXml.IsValidRawXPath(RawXPath, out var reason) ? EventLogText.RawXPathReason(Localizer, reason!) : null;
        QueryError = TryBuildQuery(out _, out var error) ? null : error;
    }

    /// <summary>
    /// The query of the builder (or the raw XPath), checked like the reader will check it (<see cref="EventQueryXml.Build"/>):
    /// false with a localized reason when a field cannot be used.
    /// </summary>
    public bool TryBuildQuery(out EventLogQuery? query, out string? error)
    {
        query = null;
        error = null;
        var channel = (Channel ?? string.Empty).Trim();
        if (!EventQueryXml.IsAllowedChannel(channel))
        {
            error = Localizer[L.EventLogs.InvalidChannel];
            return false;
        }

        EventLogQuery candidate;
        if (IsRawMode)
        {
            if (!EventQueryXml.IsValidRawXPath(RawXPath, out var reason))
            {
                error = EventLogText.RawXPathReason(Localizer, reason!);
                return false;
            }

            candidate = new EventLogQuery { Channel = channel, RawXPath = RawXPath.Trim() };
        }
        else
        {
            if (!TryWindow(out var from, out var to, out error)
                || !TryParseEventIds(EventIdsText, out var ids, out error)
                || !TryDataConditions(channel, out var data, out error))
            {
                return false;
            }

            var providers = Split(ProvidersText);
            if (providers.Count > MaxProviders || providers.Any(p => !XPathLiteral.IsAllowedValue(p)))
            {
                error = Localizer.Format(L.EventLogs.InvalidValue, Localizer[L.EventLogs.ProvidersLabel]);
                return false;
            }

            var levels = LevelOptions.Where(l => l.IsSelected).Select(l => l.Level).ToList();
            if (levels.Contains(EventLogLevels.Information))
            {
                // Event Viewer's "Information" is level 4 and level 0 (Security audits).
                levels.Add(EventLogLevels.LogAlways);
            }

            candidate = new EventLogQuery
            {
                Channel = channel,
                From = from,
                To = to,
                EventIds = ids,
                Levels = levels,
                Providers = providers,
                Outcome = SelectedOutcome.Value,
                Data = data,
            };
        }

        try
        {
            _ = EventQueryXml.Build(candidate);
        }
        catch (ArgumentException)
        {
            error = Localizer[L.EventLogs.QueryRejected];
            return false;
        }

        query = candidate;
        return true;
    }

    private bool TryWindow(out DateTimeOffset? from, out DateTimeOffset? to, out string? error)
    {
        error = null;
        var now = Services.Ui.Time.GetUtcNow();
        if (EventLogTimeWindows.Of(SelectedTimePreset.Value, now) is { } window)
        {
            (from, to) = (window.From, window.To);
            return true;
        }

        // "Do" is a date: its whole day is read (up to now); "Od" = "Do" is that one day.
        from = CustomFrom is { } f ? EventLogTimeWindows.FromLocal(f, Zone) : null;
        to = CustomTo is { } t ? EventLogTimeWindows.CustomEnd(t, Zone, now) : null;
        if (from is { } a && to is { } b && a >= b)
        {
            error = Localizer[L.EventLogs.InvalidRange];
            return false;
        }

        return true;
    }

    /// <summary>"4625, 4740 4768-4771": numbers and ranges separated by commas, semicolons or spaces.</summary>
    public bool TryParseEventIds(string? text, out IReadOnlyList<int> ids, out string? error)
    {
        error = null;
        var result = new SortedSet<int>();
        ids = [];
        foreach (var token in (text ?? string.Empty).Split([',', ';', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var dash = token.IndexOf('-', 1);
            if (dash > 0)
            {
                if (!TryId(token[..dash], out var first) || !TryId(token[(dash + 1)..], out var last) || last < first || last - first >= MaxEventIds)
                {
                    error = Localizer.Format(L.EventLogs.InvalidEventIds, MaxEventIds);
                    return false;
                }

                for (var id = first; id <= last; id++)
                {
                    result.Add(id);
                }
            }
            else if (TryId(token, out var id))
            {
                result.Add(id);
            }
            else
            {
                error = Localizer.Format(L.EventLogs.InvalidEventIds, MaxEventIds);
                return false;
            }

            if (result.Count > MaxEventIds)
            {
                error = Localizer.Format(L.EventLogs.InvalidEventIds, MaxEventIds);
                return false;
            }
        }

        ids = [.. result];
        return true;

        static bool TryId(string value, out int id) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id is >= 0 and <= 65535;
    }

    /// <summary>The account, SID, address and workstation as data conditions of the Security channel (values OR-ed per field).</summary>
    private bool TryDataConditions(string channel, out IReadOnlyList<EventDataCondition> data, out string? error)
    {
        error = null;
        var list = new List<EventDataCondition>();
        data = list;
        var fields = new (string Label, string Field, string Text, Func<string, IEnumerable<string>> Values)[]
        {
            (Localizer[L.EventLogs.AccountLabel], AccountField, AccountText, CaseVariants),
            (Localizer[L.EventLogs.SidLabel], SidField, SidText, v => [v.ToUpperInvariant()]),
            (Localizer[L.EventLogs.IpLabel], "IpAddress", IpText, AddressVariants),
            (Localizer[L.EventLogs.WorkstationLabel], WorkstationField, WorkstationText, CaseVariants),
        };
        var typed = fields.Where(f => !string.IsNullOrWhiteSpace(f.Text)).ToList();
        if (typed.Count == 0)
        {
            return true;
        }

        if (!string.Equals(channel, EventLogText.SecurityChannel, StringComparison.OrdinalIgnoreCase))
        {
            error = Localizer[L.EventLogs.FieldsOnlySecurity];
            return false;
        }

        foreach (var (label, field, text, variants) in typed)
        {
            var values = variants(text.Trim()).Distinct(StringComparer.Ordinal).ToList();
            if (values.Any(v => !XPathLiteral.IsAllowedValue(v)))
            {
                error = Localizer.Format(L.EventLogs.InvalidValue, label);
                return false;
            }

            list.Add(new EventDataCondition(field, values));
        }

        return true;
    }

    /// <summary>The event log compares case-sensitively: the name as typed, in lower and in upper case.</summary>
    private static IEnumerable<string> CaseVariants(string value) => [value, value.ToLowerInvariant(), value.ToUpperInvariant()];

    /// <summary>An IPv4 address also as Windows writes it on a dual-stack DC (<c>::ffff:10.0.0.5</c>).</summary>
    private static IEnumerable<string> AddressVariants(string value) =>
        IPAddress.TryParse(value, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? [value, "::ffff:" + value] : [value];

    private static List<string> Split(string? text) =>
        [.. (text ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)];
}
