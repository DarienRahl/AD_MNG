using System.Collections.ObjectModel;
using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.EventLogs;
using AdMng.Core.Model;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.EventLogs;

/// <summary>The sources of the reader, the details of the selected record and the exports.</summary>
public sealed partial class EventLogReaderDocumentViewModel
{
    /// <summary>The sources: the domain's DCs (PDC emulator first), computers, files.</summary>
    public ObservableCollection<EventLogSourceViewModel> Sources { get; } = [];

    /// <summary>The computer to add (FQDN).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddComputerCommand))]
    public partial string ComputerText { get; set; } = string.Empty;

    /// <summary>Why a source could not be added or listed, null when it could.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSourceError))]
    public partial string? SourceError { get; private set; }

    public bool HasSourceError => SourceError is not null;

    private async Task LoadDomainControllersAsync()
    {
        if (Session is not { } session)
        {
            return;
        }

        try
        {
            var controllers = await Task.Run(() => Services.ReadDomainControllersAsync(session, CancellationToken.None)).ConfigureAwait(true);
            foreach (var dc in controllers.Where(dc => !Sources.Any(s => string.Equals(s.Host, dc.Host, StringComparison.OrdinalIgnoreCase))))
            {
                AddSource(new EventLogSourceViewModel(EventLogSourceKind.DomainController, dc.Host,
                    Localizer[dc.IsPdcEmulator ? L.EventLogs.KindPdc : L.EventLogs.KindDomainController], isPdcEmulator: dc.IsPdcEmulator));
            }
        }
        catch (Exception ex) when (ex is DirectoryException or InvalidOperationException or ObjectDisposedException)
        {
            SourceError = Localizer.Format(L.EventLogs.NoDomainControllers, Services.Ui.Errors.Translate(ex));
        }
    }

    /// <summary>
    /// Applies a request: a computer (DN, read through the window's DC for its DNS name) or a host becomes the one selected
    /// source (added when missing).
    /// </summary>
    private async Task ApplyRequestAsync(EventLogDocumentRequest request)
    {
        var host = request.Host;
        if (request.ComputerDn is { } dn && Session is { } session)
        {
            try
            {
                var computer = await Task.Run(() => session.GetAsync(dn, [Attr.DnsHostName, Attr.Name], CancellationToken.None)).ConfigureAwait(true);
                host = computer?.GetString(Attr.DnsHostName);
                if (string.IsNullOrWhiteSpace(host))
                {
                    SourceError = Localizer.Format(L.EventLogs.ComputerNoDns, computer?.Name ?? dn);
                    return;
                }
            }
            catch (Exception ex) when (ex is DirectoryException or InvalidOperationException or ObjectDisposedException)
            {
                SourceError = Services.Ui.Errors.Translate(ex);
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            return;
        }

        foreach (var source in Sources)
        {
            source.IsSelected = false;
        }

        var existing = Sources.FirstOrDefault(s => !s.IsFiles && string.Equals(s.Host, host.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new EventLogSourceViewModel(EventLogSourceKind.Computer, host.Trim().TrimEnd('.'), Localizer[L.EventLogs.KindComputer]);
            AddSource(existing);
        }

        existing.IsSelected = true;
        SourceError = null;
    }

    /// <summary>"Dodaj": a computer by its FQDN (Kerberos needs the full DNS name).</summary>
    [RelayCommand(CanExecute = nameof(CanAddComputer))]
    private void AddComputer()
    {
        var host = ComputerText.Trim().TrimEnd('.');
        if (!EventHostNames.IsHostName(host) || !host.Contains('.', StringComparison.Ordinal))
        {
            SourceError = Localizer.Format(L.EventLogs.InvalidHost, host);
            return;
        }

        if (Sources.FirstOrDefault(s => string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            existing.IsSelected = true;
        }
        else
        {
            AddSource(new EventLogSourceViewModel(EventLogSourceKind.Computer, host, Localizer[L.EventLogs.KindComputer]));
        }

        ComputerText = string.Empty;
        SourceError = null;
    }

    private bool CanAddComputer() => !string.IsNullOrWhiteSpace(ComputerText);

    /// <summary>"Wybierz z AD…": computers picked from the directory (their DNS names).</summary>
    [RelayCommand(CanExecute = nameof(CanPick))]
    private async Task PickComputersAsync()
    {
        if (Session is not { } session)
        {
            return;
        }

        var picked = await Services.Ui.Dialogs.PickObjectsAsync(session, [ObjectKind.Computer], multiSelect: true).ConfigureAwait(true);
        var missing = new List<string>();
        foreach (var dn in picked)
        {
            try
            {
                var computer = await Task.Run(() => session.GetAsync(dn, [Attr.DnsHostName, Attr.Name], CancellationToken.None)).ConfigureAwait(true);
                if (computer?.GetString(Attr.DnsHostName) is { Length: > 0 } host)
                {
                    if (Sources.FirstOrDefault(s => string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase)) is { } existing)
                    {
                        existing.IsSelected = true;
                    }
                    else
                    {
                        AddSource(new EventLogSourceViewModel(EventLogSourceKind.Computer, host, Localizer[L.EventLogs.KindComputer]));
                    }
                }
                else
                {
                    missing.Add(computer?.Name ?? dn);
                }
            }
            catch (Exception ex) when (ex is DirectoryException or InvalidOperationException or ObjectDisposedException)
            {
                SourceError = Services.Ui.Errors.Translate(ex);
                return;
            }
        }

        SourceError = missing.Count > 0 ? Localizer.Format(L.EventLogs.ComputerNoDns, string.Join(", ", missing)) : null;
    }

    private bool CanPick() => HasSession;

    /// <summary>"Otwórz plik .evtx…": a file the operator chose becomes a source (read only, never copied).</summary>
    [RelayCommand]
    private async Task OpenFilesAsync()
    {
        var path = await Services.Ui.Files.OpenFileAsync(Localizer[L.EventLogs.OpenFiles], [new FileFilter(Localizer[L.EventLogs.EvtxFilter], "*.evtx")]).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (Sources.FirstOrDefault(s => s.IsFiles && s.Files.Contains(path, StringComparer.OrdinalIgnoreCase)) is { } existing)
        {
            existing.IsSelected = true;
            return;
        }

        AddSource(new EventLogSourceViewModel(EventLogSourceKind.Files, FileSourceHost(path), Localizer[L.EventLogs.KindFiles], [path]));
    }

    /// <summary>
    /// The host of a file source – the target's host, the host stamped on its records (<see cref="EventLogHostRunner"/>) and
    /// the key of its row in "Zasięg i dostęp" – unique among the sources: the file name, else its folder and name
    /// (<c>DC02\Security.evtx</c>), else the name with a number (<c>Security.evtx (2)</c>), so two files of the same name
    /// never share a row, a record host or "Pokaż komunikat".
    /// </summary>
    private string FileSourceHost(string path)
    {
        var name = Path.GetFileName(path);
        if (!IsTaken(name))
        {
            return name;
        }

        var folder = Path.GetFileName(Path.GetDirectoryName(path));
        var withFolder = string.IsNullOrEmpty(folder) ? name : Path.Combine(folder, name);
        if (!IsTaken(withFolder))
        {
            return withFolder;
        }

        for (var number = 2; ; number++)
        {
            var numbered = string.Create(CultureInfo.InvariantCulture, $"{name} ({number})");
            if (!IsTaken(numbered))
            {
                return numbered;
            }
        }

        bool IsTaken(string host) => Sources.Any(s => string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"Usuń" of a computer or file source (the domain's DCs stay; they are only unselected).</summary>
    [RelayCommand]
    private void RemoveSource(EventLogSourceViewModel? source)
    {
        if (source is null)
        {
            return;
        }

        if (source.Kind == EventLogSourceKind.DomainController)
        {
            source.IsSelected = false;
            return;
        }

        source.PropertyChanged -= OnSourceChanged;
        Sources.Remove(source);
    }

    /// <summary>"Wczytaj kanały": the channels of the first selected computer, after the common ones.</summary>
    [RelayCommand(CanExecute = nameof(CanLoadChannels))]
    private Task LoadChannelsAsync()
    {
        if (Sources.FirstOrDefault(s => s.IsSelected && !s.IsFiles) is not { } source || Services.Connector is not { } connector)
        {
            return Task.CompletedTask;
        }

        return RunExclusiveAsync(async (session, token) =>
        {
            ProgressText = Localizer.Format(L.EventLogs.LoadingChannels, source.Host);
            await using var reader = await connector.OpenComputerAsync(source.Host, session, token).ConfigureAwait(true);
            var channels = await reader.GetChannelsAsync(token).ConfigureAwait(true);
            ChannelOptions = [.. EventLogText.CommonChannels, .. channels.Where(c => !EventLogText.CommonChannels.Contains(c, StringComparer.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase)];
            LoadedText = Localizer.Format(L.EventLogs.ChannelsLoaded, source.Host, channels.Count);
        });
    }

    private bool CanLoadChannels() => IsSupported && HasSession && !IsLoading && Sources.Any(s => s.IsSelected && !s.IsFiles);

    private void AddSource(EventLogSourceViewModel source)
    {
        source.PropertyChanged += OnSourceChanged;
        Sources.Add(source);
    }

    private void OnSourceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RefreshCommands();

    // ---- Details --------------------------------------------------------------------------------------------------------

    partial void OnSelectedRowChanged(EventLogRecordRow? value)
    {
        MessageText = null;
        if (value is null)
        {
            ClearDetails();
            return;
        }

        Fields = FieldsOf(value.Record);
        ResolveObjectsAsync(value.Record, Fields).Forget();
        RefreshCommands();
    }

    private void ClearDetails()
    {
        _details.Cancel();
        Fields = [];
        MessageText = null;
    }

    /// <summary>The system fields, then every value of the record with the meaning of codes.</summary>
    private List<EventLogFieldRow> FieldsOf(EventLogRecord record)
    {
        var fields = new List<EventLogFieldRow>
        {
            new(Localizer[L.EventLogs.FieldTime], EventLogText.Moment(Localizer, record.TimeCreated, Zone), isSystem: true),
            new(Localizer[L.EventLogs.FieldHost], record.Host, isSystem: true),
            new(Localizer[L.EventLogs.FieldComputer], record.Computer, isSystem: true),
            new(Localizer[L.EventLogs.FieldChannel], EventLogText.ChannelOption(Localizer, record.Channel), isSystem: true),
            new(Localizer[L.EventLogs.FieldProvider], record.Provider ?? string.Empty, isSystem: true),
            new(Localizer[L.EventLogs.FieldEventId], record.EventId.ToString(CultureInfo.InvariantCulture), isSystem: true),
            new(Localizer[L.EventLogs.FieldLevel], record.IsAuditFailure
                ? EventLogText.Outcome(Localizer, EventAuditOutcome.Failure)
                : record.IsAuditSuccess ? EventLogText.Outcome(Localizer, EventAuditOutcome.Success) : EventLogText.Level(Localizer, record.Level), isSystem: true),
            new(Localizer[L.EventLogs.FieldRecordId], record.RecordId.ToString(CultureInfo.InvariantCulture), isSystem: true),
        };
        if (record.UserSid is { Length: > 0 } userSid)
        {
            fields.Add(new EventLogFieldRow(Localizer[L.EventLogs.FieldUser], userSid, isSystem: true));
        }

        foreach (var (name, value) in record.Data)
        {
            fields.Add(new EventLogFieldRow(name, value, EventLogText.Explain(Localizer, record, name, value)));
        }

        return fields;
    }

    /// <summary>
    /// SIDs and account names of the fields resolved to AD objects through the window's DC (best effort, a few per record). A
    /// name is resolved only when the record's matching <c>…DomainName</c> field names the window's domain (or is empty):
    /// a local or foreign account is never linked to a same-named account of this domain.
    /// </summary>
    private async Task ResolveObjectsAsync(EventLogRecord record, IReadOnlyList<EventLogFieldRow> fields)
    {
        if (Session is not { } session)
        {
            return;
        }

        var token = _details.Next();
        var candidates = fields
            .Where(f => IsSid(f.Value) || IsAccountField(f.Name))
            .Take(EventLogServices.NameLookupLimit)
            .ToList();
        foreach (var field in candidates)
        {
            try
            {
                var isSid = IsSid(field.Value);
                var domain = isSid ? null : DomainOf(record, field.Name);
                var found = await Task.Run(() => Services.ResolveObjectAsync(session, field.Value, isSid, domain, token), token).ConfigureAwait(true);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                if (found is { } match)
                {
                    field.ObjectDn = match.Dn;
                    field.ObjectName = match.Name;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is DirectoryException or InvalidOperationException or ObjectDisposedException)
            {
                // Best effort: the value stays as it was logged.
            }
        }
    }

    private static bool IsSid(string value) => value.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase);

    private static bool IsAccountField(string name) => name is "TargetUserName" or "SubjectUserName" or "TargetOutboundUserName" or "MemberName";

    /// <summary>
    /// The domain logged with an account field (<c>TargetUserName</c> → <c>TargetDomainName</c> …), null when the event has
    /// none. 4740 writes the caller computer into <c>TargetDomainName</c>, not the account's domain.
    /// </summary>
    private static string? DomainOf(EventLogRecord record, string field) => field switch
    {
        "TargetUserName" when record.EventId != 4740 => record.Get("TargetDomainName"),
        "SubjectUserName" => record.Get("SubjectDomainName"),
        "TargetOutboundUserName" => record.Get("TargetOutboundDomainName"),
        _ => null,
    };

    /// <summary>"Właściwości" of the AD object a field names.</summary>
    [RelayCommand]
    private Task OpenObjectAsync(EventLogFieldRow? field) =>
        field?.ObjectDn is { } dn ? Services.Ui.Dialogs.ShowPropertiesAsync(dn) : Task.CompletedTask;

    /// <summary>"Pokaż komunikat": the message the source's publisher renders for the selected record (on demand only).</summary>
    [RelayCommand(CanExecute = nameof(CanShowMessage))]
    private async Task ShowMessageAsync()
    {
        if (SelectedRow is not { } row || Session is not { } session || Services.Connector is not { } connector)
        {
            return;
        }

        var record = row.Record;
        IsMessageLoading = true;
        try
        {
            var source = Sources.FirstOrDefault(s => s.IsFiles && string.Equals(s.Host, record.Host, StringComparison.OrdinalIgnoreCase));
            var token = _details.Next();
            var text = await Task.Run(async () =>
            {
                await using var reader = source is not null ? connector.OpenFiles(source.Files) : await connector.OpenComputerAsync(record.Host, session, token).ConfigureAwait(false);
                return await reader.FormatMessageAsync(record, token).ConfigureAwait(false);
            }, token).ConfigureAwait(true);
            if (ReferenceEquals(SelectedRow, row))
            {
                MessageText = string.IsNullOrWhiteSpace(text) ? Localizer[L.EventLogs.MessageUnavailable] : text;
            }
        }
        catch (OperationCanceledException)
        {
            // Another record was selected.
        }
        catch (EventLogAccessException ex)
        {
            if (ReferenceEquals(SelectedRow, row))
            {
                MessageText = EventLogText.AccessError(Localizer, ex.Error, ex.NativeErrorCode);
            }
        }
        finally
        {
            IsMessageLoading = false;
        }
    }

    private bool CanShowMessage() => SelectedRow is not null && !IsMessageLoading && IsSupported && HasSession;

    /// <summary>"Kopiuj": the fields of the selected record as text.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyDetails()
    {
        if (SelectedRow is not null && Services.Ui.Clipboard.SetText(string.Join(Environment.NewLine, Fields.Select(f => f.Line))))
        {
            Services.Ui.Notifications.Show(NotificationSeverity.Info, Localizer[L.EventLogs.Copied]);
        }
    }

    // ---- Export ---------------------------------------------------------------------------------------------------------

    /// <summary>"Eksportuj do CSV…": the shown records (after the search box) to a file the operator picks.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportCsvAsync() =>
        EventLogExport.ExportCsvAsync(Services.Ui, Services.Csv, Localizer[L.EventLogs.ExportCsv], ReportTitle(), ExportTable());

    /// <summary>"Eksportuj do HTML…".</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportHtmlAsync()
    {
        var subtitle = new List<string> { LoadedText };
        if (DroppedNote is { } dropped)
        {
            subtitle.Add(dropped);
        }

        var notes = Hosts.Where(h => h.HasError || h.HasWarning).Select(h => h.Host + ": " + (h.ErrorText ?? h.Warning)).ToList();
        return EventLogExport.ExportHtmlAsync(Services.Ui, Localizer[L.EventLogs.ExportHtml], ReportTitle(), subtitle, notes, [ExportTable()]);
    }

    private bool CanExport() => Rows.Count > 0 && !IsLoading;

    private string ReportTitle() => Localizer.Format(L.EventLogs.ReportTitle, Session?.Info.DomainDnsName ?? string.Empty);

    /// <summary>The shown records: time, host, channel, ID, level, provider, summary and every field.</summary>
    internal EventLogExportTable ExportTable() => new(
        null,
        [.. new[]
        {
            L.EventLogs.ColumnTime, L.EventLogs.ColumnHost, L.EventLogs.ColumnChannel, L.EventLogs.ColumnId, L.EventLogs.ColumnLevel,
            L.EventLogs.ColumnProvider, L.EventLogs.ColumnSummary, L.EventLogs.ColumnData,
        }.Select(k => Localizer[k])],
        [.. Rows.Select(r => (IReadOnlyList<string>)
        [
            r.TimeText, r.Record.Host, r.ChannelText, r.EventIdText, r.LevelText, r.Provider, r.Summary,
            string.Join(" | ", r.Record.Data.Select(d => d.Key + "=" + d.Value)),
        ])]);
}
