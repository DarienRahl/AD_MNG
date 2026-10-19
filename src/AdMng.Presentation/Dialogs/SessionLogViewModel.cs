using System.Collections.Concurrent;
using AdMng.Core.Audit;
using AdMng.Core.Batch;
using AdMng.Core.Ldap;
using AdMng.Presentation.Connection;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Dialogs;

/// <summary>One change of the session log, formatted for the grid.</summary>
public sealed class AuditEntryViewModel
{
    internal AuditEntryViewModel(AuditEntry entry, ILocalizer localizer, TimeZoneInfo timeZone)
    {
        Entry = entry;
        Timestamp = TimeZoneInfo.ConvertTime(entry.Timestamp, timeZone).ToString("G", localizer.Culture);
        Name = DistinguishedName.TryParse(entry.DistinguishedName, out var dn) && !dn.IsRoot ? dn.Name : entry.DistinguishedName;
        Action = entry.Action == DomainControllerSwitch.SessionLogAction
            ? localizer[L.ChangeDc.SessionLogAction]
            : Enum.TryParse<ChangeAction>(entry.Action, out var action) ? localizer[DisplayNames.ChangeActionKey(action)] : entry.Action;
        Attribute = string.IsNullOrEmpty(entry.Attribute) ? string.Empty : localizer.AttributeName(entry.Attribute, null);
        ResultText = localizer[entry.Succeeded ? L.SessionLog.Succeeded : L.SessionLog.Failed];
    }

    public AuditEntry Entry { get; }

    /// <summary>Local time in the UI culture.</summary>
    public string Timestamp { get; }

    public string Operator => Entry.Operator;

    public string Server => Entry.Server;

    public string Dn => Entry.DistinguishedName;

    /// <summary>RDN value of the object.</summary>
    public string Name { get; }

    /// <summary>Localized action.</summary>
    public string Action { get; }

    /// <summary>Friendly attribute name.</summary>
    public string Attribute { get; }

    public string OldValue => Entry.OldValue ?? string.Empty;

    public string NewValue => Entry.NewValue ?? string.Empty;

    public bool Succeeded => Entry.Succeeded;

    public string ResultText { get; }

    public string Error => Entry.Error ?? string.Empty;
}

/// <summary>
/// Session log: every change this application made in the session (who, when, which DC, object, attribute, old →
/// new; never passwords), updated live, filterable and exportable to CSV on request. In memory only.
/// </summary>
public sealed partial class SessionLogViewModel : WindowViewModelBase
{
    private readonly IAuditLog _log;
    private readonly AuditLogExporter _exporter;
    private readonly List<AuditEntryViewModel> _all = [];
    private readonly HashSet<AuditEntry> _known = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentQueue<AuditEntry> _incoming = new();
    private int _flushScheduled;

    public SessionLogViewModel(DialogServices services, IAuditLog log, AuditLogExporter exporter)
        : base(services)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        _log.EntryRecorded += OnEntryRecorded;
        Reload();
    }

    public override string WindowTitle => Localizer[L.SessionLog.WindowTitle];

    /// <summary>Visible entries, newest first.</summary>
    public BulkObservableCollection<AuditEntryViewModel> Entries { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyEntryCommand))]
    public partial AuditEntryViewModel? SelectedEntry { get; set; }

    /// <summary>Filter on object name/DN, attribute, values and operator (case- and diacritics-insensitive).</summary>
    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowFailuresOnly { get; set; }

    /// <summary>"12 of 40 entries".</summary>
    [ObservableProperty]
    public partial string CountText { get; private set; } = string.Empty;

    /// <summary>Exports the visible entries (CSV, formula injection protected).</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        var title = Localizer[L.SessionLog.ExportTitle];
        var path = await Services.Files.SaveFileAsync(title, $"session-log-{Services.Time.GetLocalNow():yyyyMMdd-HHmm}.csv", [new FileFilter(Localizer[L.Files.CsvFilter], "*.csv")])
            .ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        var entries = Entries.Select(e => e.Entry).Reverse().ToList();
        try
        {
            var delimiter = CsvFiles.ListSeparator(Services.Regional.Culture);
            await CsvFiles.WriteAsync(path, writer => _exporter.Write(writer, entries, delimiter)).ConfigureAwait(true);
            Services.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.Files.Exported, entries.Count, Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Services.Dialogs.ShowErrorAsync(title, Errors.Translate(ex)).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void CopyDn(AuditEntryViewModel? entry)
    {
        if ((entry ?? SelectedEntry) is { } target)
        {
            ClipboardCopy.Value(Services.Clipboard, Services.Notifications, Localizer, target.Dn, Localizer[L.Copy.DistinguishedName]);
        }
    }

    /// <summary>
    /// Ctrl+C: the entry as labelled lines (time, operator, DC, object, action, attribute, before → after, result, error)
    /// for a ticket or a chat. The log never holds passwords or secret values, so neither does the copy.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasEntry))]
    private void CopyEntry(AuditEntryViewModel? entry)
    {
        if ((entry ?? SelectedEntry) is not { } target)
        {
            return;
        }

        var lines = new List<string>
        {
            $"{Localizer[L.ToolsUi.Time]}: {target.Timestamp}",
            $"{Localizer[L.ToolsUi.Operator]}: {target.Operator}",
            $"{Localizer[L.ToolsUi.Server]}: {target.Server}",
            $"{Localizer[L.BatchUi.Object]}: {target.Name} ({target.Dn})",
            $"{Localizer[L.Batch.Action]}: {target.Action}",
        };
        if (target.Attribute.Length > 0)
        {
            lines.Add($"{Localizer[L.Labels.Attribute]}: {target.Attribute}");
            lines.Add($"{Localizer[L.Batch.Before]}: {target.OldValue}");
            lines.Add($"{Localizer[L.Batch.After]}: {target.NewValue}");
        }

        lines.Add($"{Localizer[L.Batch.Status]}: {target.ResultText}");
        if (target.Error.Length > 0)
        {
            lines.Add($"{Localizer[L.Common.Error]}: {target.Error}");
        }

        ClipboardCopy.Value(Services.Clipboard, Services.Notifications, Localizer, string.Join(Environment.NewLine, lines), Localizer[L.Copy.Entry]);
    }

    private bool HasEntry(AuditEntryViewModel? entry) => (entry ?? SelectedEntry) is not null;

    /// <summary>Opens the properties of the entry's object - not any more once the window's session ended.</summary>
    [RelayCommand(CanExecute = nameof(IsSessionActive))]
    private Task OpenAsync(AuditEntryViewModel? entry) =>
        !IsSessionEnded && (entry ?? SelectedEntry) is { } target ? Services.Dialogs.ShowPropertiesAsync(target.Dn) : Task.CompletedTask;

    private bool IsSessionActive(AuditEntryViewModel? entry) => !IsSessionEnded;

    protected override void OnSessionEnded() => OpenCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void Close() => RequestClose(true);

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    partial void OnShowFailuresOnlyChanged(bool value) => ApplyFilter();

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        Reload();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _log.EntryRecorded -= OnEntryRecorded;
        }
    }

    private void Reload()
    {
        _all.Clear();
        _known.Clear();
        foreach (var entry in _log.Entries)
        {
            if (_known.Add(entry))
            {
                _all.Add(Create(entry));
            }
        }

        ApplyFilter();
    }

    private AuditEntryViewModel Create(AuditEntry entry) => new(entry, Localizer, Services.Time.LocalTimeZone);

    /// <summary>
    /// The log records entries on background threads (batch execution, one entry per attribute and object). They are
    /// queued and added in one dispatcher call per burst, so a large batch does not rebuild the list once per entry
    /// on the UI thread.
    /// </summary>
    private void OnEntryRecorded(object? sender, AuditEntry entry)
    {
        _incoming.Enqueue(entry);
        if (Interlocked.Exchange(ref _flushScheduled, 1) == 0)
        {
            _ = Services.Dispatcher.InvokeAsync(FlushIncoming);
        }
    }

    private void FlushIncoming()
    {
        // Reset first: an entry queued while draining schedules another flush instead of being missed.
        Volatile.Write(ref _flushScheduled, 0);
        var added = false;
        while (_incoming.TryDequeue(out var entry))
        {
            // The snapshot taken when the window opened may already contain the entry.
            if (!IsDisposed && _known.Add(entry))
            {
                _all.Add(Create(entry));
                added = true;
            }
        }

        if (added)
        {
            ApplyFilter();
        }
    }

    private void ApplyFilter()
    {
        var text = FilterText.Trim();
        var visible = _all
            .Where(e => !ShowFailuresOnly || !e.Succeeded)
            .Where(e => text.Length == 0
                || TextFolding.Contains(e.Name, text) || TextFolding.Contains(e.Dn, text) || TextFolding.Contains(e.Attribute, text)
                || TextFolding.Contains(e.OldValue, text) || TextFolding.Contains(e.NewValue, text) || TextFolding.Contains(e.Operator, text)
                || TextFolding.Contains(e.Action, text))
            .Reverse()
            .ToList();
        Entries.ReplaceAll(visible);
        CountText = Localizer.Format(L.SessionLog.Count, visible.Count, _all.Count);
    }
}
