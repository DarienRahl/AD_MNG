using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Correspondence.Fields;
using AdMng.Core.Correspondence.Import;
using AdMng.Core.Directory;
using AdMng.Core.Schema;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using AdMng.Presentation.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Correspondence;

/// <summary>One message of the send list with the rows it was made of (the send page sets their statuses).</summary>
/// <param name="Unit">The message: address, name and the fields of every row.</param>
/// <param name="Rows">The rows of the message (one, or every row of the address when grouping).</param>
public sealed record RecipientUnit(CorrespondenceUnit Unit, IReadOnlyList<CorrespondenceRecipientViewModel> Rows)
{
    /// <summary>The campaign keys of the message: the mailbox keys of its rows, distinct, in row order.</summary>
    public IReadOnlyList<string> Keys => [.. Rows.SelectMany(r => r.MailboxKeys).Distinct(StringComparer.OrdinalIgnoreCase)];

    public override string ToString() => $"{nameof(RecipientUnit)} ({Rows.Count} rows)";
}

/// <summary>
/// "1 · Odbiorcy" (design note 6.4 and 7.1): recipients from files, the clipboard, typed text, the newest contacts file in
/// Downloads, drag and drop and AD (selected objects, picked users, group members); the "Nie wysyłać" list, the journal
/// marks, the counts, the filter box and the AD field definitions. Rows exist in memory only (rule 1); AD reads use the
/// window's session (rule 4).
/// </summary>
public sealed partial class RecipientsPageViewModel : ObservableObject
{
    /// <summary>The most members read from one group (nested membership, users and contacts).</summary>
    public const int MaxGroupMembers = 10_000;

    /// <summary>The longest subject shown in <see cref="CorrespondenceRecipientViewModel.PreviouslySentText"/>.</summary>
    internal const int MaxSubjectShown = 60;

    private readonly HashSet<string> _excluded = new(StringComparer.Ordinal);
    private readonly DirectoryFieldEditor _directoryFields;
    private IList? _selectedItems;
    private string _downloadsPattern = CorrespondenceSettings.DefaultDownloadsPattern;
    private (bool Loaded, int Count, DateTimeOffset? Newest)? _journalState;
    private CancellationTokenSource _directoryReads = new();
    private bool _bulk;

    /// <param name="services">The window's services.</param>
    /// <param name="correspondence">The Korespondencja services.</param>
    /// <param name="session">The window's session (<c>RequireSession</c> of the window: throws when it ended).</param>
    public RecipientsPageViewModel(DialogServices services, CorrespondenceServices correspondence, Func<IDirectorySession> session)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Correspondence = correspondence ?? throw new ArgumentNullException(nameof(correspondence));
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _directoryFields = new DirectoryFieldEditor(DirectoryFields, correspondence.Fields, services.Localizer, TrySchema,
            DirectoryFieldDefinition.Defaults, DirectoryFieldTarget.Person, L.CorrespondenceRecipients.NewFieldName);
        _directoryFields.Load(null);
        UpdateCounts();
        UpdateExclusionsText();
    }

    /// <summary>Every row, in import order.</summary>
    public BulkObservableCollection<CorrespondenceRecipientViewModel> Items { get; } = [];

    /// <summary>The rows shown: <see cref="Items"/> filtered by <see cref="SearchText"/> (address, name, source, fields).</summary>
    public BulkObservableCollection<CorrespondenceRecipientViewModel> View { get; } = [];

    /// <summary>The filter box: rows whose address, name, source or a field contains the text (case and diacritics ignored).</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>The grid's multi-selection (set by the view).</summary>
    public IList? SelectedItems
    {
        get => _selectedItems;
        set => SetProperty(ref _selectedItems, value);
    }

    /// <summary>
    /// The field columns in import order (the view builds grid columns from them). Columns that duplicate a built-in field
    /// (<see cref="BuiltInFields.IsBuiltInColumn"/>: the file's e-mail, name and salutation columns) are not listed – the
    /// grid shows them as E-mail and Nazwa – but stay in the rows' fields.
    /// </summary>
    public ObservableCollection<string> Columns { get; } = [];

    /// <summary>Every column known to the list (a column empty in a row renders "").</summary>
    public IReadOnlyCollection<string> KnownColumns => Columns;

    /// <summary>"Odbiorcy: 120 • do wysłania: 95 • pominięci: 20 • błędne: 5".</summary>
    [ObservableProperty]
    public partial string CountsText { get; private set; } = string.Empty;

    /// <summary>"Pominięcia: 12 własnych + 340 z dziennika".</summary>
    [ObservableProperty]
    public partial string ExclusionsText { get; private set; } = string.Empty;

    /// <summary>Rows whose address is in the journal are skipped.</summary>
    [ObservableProperty]
    public partial bool SkipAlreadySent { get; set; } = true;

    /// <summary>The AD actions can be used (false after the window's session ended; running reads are cancelled).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddFromSelectionCommand), nameof(PickUsersCommand), nameof(AddGroupMembersCommand))]
    public partial bool CanUseDirectory { get; set; } = true;

    /// <summary>The fields of AD rows (target Person).</summary>
    public ObservableCollection<DirectoryFieldViewModel> DirectoryFields { get; } = [];

    /// <summary>The last outcome of an action ("Wczytano plik: 120 nowych, 3 powtórzone, 2 błędne adresy"), for the status bar.</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    /// <summary>The addresses on the "Nie wysyłać" list (normalized; in memory only).</summary>
    public int ExclusionCount => _excluded.Count;

    private DialogServices Services { get; }

    private CorrespondenceServices Correspondence { get; }

    private Func<IDirectorySession> Session { get; }

    private ILocalizer Localizer => Services.Localizer;

    /// <summary>The rows to send: included, valid, not excluded and not already sent (when skipping; the journal is read again).</summary>
    public IReadOnlyList<CorrespondenceRecipientViewModel> SendList()
    {
        RefreshJournalMarks();
        return [.. Items.Where(IsSendable)];
    }

    /// <summary>The messages of <see cref="SendList"/>: one per row, or one per address with all its rows.</summary>
    public IReadOnlyList<CorrespondenceUnit> Units(bool groupByAddress) =>
        RecipientGrouping.Group(SendList().Select(r => (r.Email, r.Name, r.Fields)), groupByAddress);

    /// <summary>
    /// The messages of <see cref="SendList"/> with their rows (the same units as <see cref="Units"/>): the send page marks
    /// the rows of each message and takes the mailbox keys of mailbox rows as campaign keys.
    /// </summary>
    public IReadOnlyList<RecipientUnit> RecipientUnits(bool groupByAddress)
    {
        var rows = SendList();
        if (!groupByAddress)
        {
            return [.. rows.Select(r => new RecipientUnit(RecipientGrouping.Group([(r.Email, r.Name, r.Fields)], false)[0], [r]))];
        }

        var groups = new List<List<CorrespondenceRecipientViewModel>>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var key = EmailAddressText.Normalize(row.Email);
            if (key.Length > 0 && index.TryGetValue(key, out var existing))
            {
                groups[existing].Add(row);
                continue;
            }

            if (key.Length > 0)
            {
                index[key] = groups.Count;
            }

            groups.Add([row]);
        }

        return [.. groups.Select(g => new RecipientUnit(RecipientGrouping.Group(g.Select(r => (r.Email, r.Name, r.Fields)), true)[0], g))];
    }

    /// <summary>Takes "skip already sent", the Downloads pattern and the AD field definitions of the settings.</summary>
    public void LoadFrom(CorrespondenceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        SkipAlreadySent = settings.SkipAlreadySent;
        _downloadsPattern = string.IsNullOrWhiteSpace(settings.DownloadsPattern) ? CorrespondenceSettings.DefaultDownloadsPattern : settings.DownloadsPattern.Trim();
        _directoryFields.Load(settings.DirectoryFields?.Where(d => d.Target == DirectoryFieldTarget.Person).ToList());
        RefreshJournalMarks();
    }

    /// <summary>Writes "skip already sent" and the AD field definitions into the settings (none while they are the defaults).</summary>
    public void SaveTo(CorrespondenceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.SkipAlreadySent = SkipAlreadySent;
        settings.DirectoryFields = _directoryFields.ToSettings();
    }

    /// <summary>
    /// Reads the journal marks of every row again when the journal changed (loaded, appended, pruned, cleared) and updates the
    /// counts – the window calls it after loading the journal, the send page after a run.
    /// </summary>
    public void RefreshJournalMarks()
    {
        var journal = Correspondence.Journal;
        var state = (journal.IsLoaded, journal.Count, journal.Newest);
        if (_journalState != state)
        {
            _journalState = state;
            foreach (var row in Items)
            {
                MarkJournal(row);
            }
        }
        else
        {
            foreach (var row in Items)
            {
                row.IsAlreadySent = SkipAlreadySent && row.IsInJournal;
            }
        }

        UpdateCounts();
        UpdateExclusionsText();
    }

    /// <summary>The window's language changed: the computed texts follow (row statuses keep the language they were set in).</summary>
    public void RefreshTexts()
    {
        _journalState = null;
        RefreshJournalMarks();
        _directoryFields.Validate();
    }

    /// <summary>
    /// Mailbox owner rows ("Skrzynki"): the earlier ones are replaced. Their addresses are not de-duplicated (one owner,
    /// several mailboxes – a message per owner when grouping); rows of the same mailbox key are added once.
    /// </summary>
    /// <returns>The rows added.</returns>
    internal int ReplaceMailboxRows(IReadOnlyList<CorrespondenceRecipientViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Remove([.. Items.Where(r => r.Origin == RecipientOrigin.Mailboxes)]);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var added = rows.Where(r => r.MailboxKeys.Count == 0 || r.MailboxKeys.Any(keys.Add)).Take(Math.Max(0, CorrespondenceLimits.MaxRecipients - Items.Count)).ToList();
        Insert(added);
        return added.Count;
    }

    /// <summary>Raises a status line (also when it repeats the previous one, so the status bar shows it again).</summary>
    internal void ReportStatus(string text)
    {
        StatusText = string.Empty;
        StatusText = text ?? string.Empty;
    }

    private static bool IsSendable(CorrespondenceRecipientViewModel row) =>
        row.Include && row.IsValid && !row.IsExcluded && !row.IsAlreadySent;

    private ISchemaCatalog? TrySchema()
    {
        try
        {
            return Session().Schema;
        }
        catch (Exception ex) when (ex is SessionEndedException or InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>Adds rows after the de-duplication: subscriptions, columns, exclusion and journal marks, view and counts.</summary>
    private void Insert(IReadOnlyList<CorrespondenceRecipientViewModel> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        foreach (var row in rows)
        {
            row.IsExcluded = row.IsValid && _excluded.Contains(row.Email);
            MarkJournal(row);
            row.PropertyChanged += OnRowChanged;
        }

        Items.AddRange(rows);
        AddColumns(rows.SelectMany(r => r.Fields.Keys));
        RefreshView();
        UpdateCounts();
    }

    private void Remove(IReadOnlyList<CorrespondenceRecipientViewModel> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var set = new HashSet<CorrespondenceRecipientViewModel>(rows);
        foreach (var row in rows)
        {
            row.PropertyChanged -= OnRowChanged;
        }

        Items.RemoveWhere(set.Contains);
        View.RemoveWhere(set.Contains);
        UpdateCounts();
    }

    /// <summary>The addresses of the list (valid rows), for de-duplication.</summary>
    private HashSet<string> Addresses() => new(Items.Where(r => r.IsValid).Select(r => r.Email), StringComparer.Ordinal);

    /// <summary>Adds the field names not known yet (by <see cref="FieldKey"/>), in the order of their first appearance.</summary>
    private void AddColumns(IEnumerable<string> names)
    {
        var known = Columns.Select(FieldKey.Of).ToHashSet(StringComparer.Ordinal);
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(name) || BuiltInFields.IsBuiltInColumn(name) || !known.Add(FieldKey.Of(name)))
            {
                continue;
            }

            Columns.Add(name);
        }
    }

    private void MarkJournal(CorrespondenceRecipientViewModel row)
    {
        var entry = row.IsValid ? Correspondence.Journal.Find(row.Email) : null;
        row.IsInJournal = entry is not null;
        row.IsAlreadySent = SkipAlreadySent && row.IsInJournal;
        if (entry is null)
        {
            row.PreviouslySentText = null;
            return;
        }

        var sent = TimeZoneInfo.ConvertTime(entry.SentAt, Services.Time.LocalTimeZone).DateTime;
        var subject = entry.Subject.Trim();
        if (subject.Length > MaxSubjectShown)
        {
            subject = subject[..(MaxSubjectShown - 1)].TrimEnd() + "…";
        }

        row.PreviouslySentText = subject.Length == 0
            ? Localizer.Format(L.CorrespondenceRecipients.PreviouslySentDate, sent)
            : Localizer.Format(L.CorrespondenceRecipients.PreviouslySent, sent, subject);
    }

    private void ApplyExclusions()
    {
        foreach (var row in Items)
        {
            row.IsExcluded = row.IsValid && _excluded.Contains(row.Email);
        }

        UpdateCounts();
        UpdateExclusionsText();
        OnPropertyChanged(nameof(ExclusionCount));
    }

    private void RefreshView()
    {
        var needle = TextFolding.Fold(SearchText?.Trim());
        View.ReplaceAll(needle.Length == 0 ? Items : Items.Where(r => r.SearchKey.Contains(needle, StringComparison.Ordinal)));
    }

    private void UpdateCounts()
    {
        int send = 0, skipped = 0, invalid = 0;
        foreach (var row in Items)
        {
            if (!row.Include)
            {
                continue;
            }

            if (!row.IsValid)
            {
                invalid++;
            }
            else if (row.IsExcluded || row.IsAlreadySent)
            {
                skipped++;
            }
            else
            {
                send++;
            }
        }

        var text = Localizer.Format(L.CorrespondenceRecipients.Counts, Items.Count, send);
        if (skipped > 0)
        {
            text += Localizer.Format(L.CorrespondenceRecipients.CountsSkipped, skipped);
        }

        if (invalid > 0)
        {
            text += Localizer.Format(L.CorrespondenceRecipients.CountsInvalid, invalid);
        }

        CountsText = text;
    }

    private void UpdateExclusionsText() =>
        ExclusionsText = Localizer.Format(L.CorrespondenceRecipients.Exclusions, _excluded.Count,
            Correspondence.Journal.IsLoaded ? Correspondence.Journal.Count : 0);

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_bulk && e.PropertyName == nameof(CorrespondenceRecipientViewModel.Include))
        {
            UpdateCounts();
        }
    }

    partial void OnSearchTextChanged(string value) => RefreshView();

    partial void OnSkipAlreadySentChanged(bool value)
    {
        foreach (var row in Items)
        {
            row.IsAlreadySent = value && row.IsInJournal;
        }

        UpdateCounts();
    }

    partial void OnCanUseDirectoryChanged(bool value)
    {
        if (!value)
        {
            var reads = Interlocked.Exchange(ref _directoryReads, new CancellationTokenSource());
            reads.Cancel();
            reads.Dispose();
        }
    }

    [RelayCommand]
    private void IncludeAll() => SetInclude(true);

    [RelayCommand]
    private void IncludeNone() => SetInclude(false);

    /// <summary>"Zaznacz/odznacz wszystkie": the rows shown (the filter box narrows them, as in MailerWPiA).</summary>
    private void SetInclude(bool include)
    {
        _bulk = true;
        try
        {
            foreach (var row in View)
            {
                row.Include = include;
            }
        }
        finally
        {
            _bulk = false;
        }

        UpdateCounts();
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        var selected = SelectedItems?.OfType<CorrespondenceRecipientViewModel>().Distinct().ToList() ?? [];
        if (selected.Count == 0)
        {
            ReportStatus(Localizer[L.CorrespondenceRecipients.RemoveNone]);
            return;
        }

        Remove(selected);
        ReportStatus(Localizer.Format(L.CorrespondenceRecipients.Removed, selected.Count));
    }

    /// <summary>CSV: Nazwa;E-mail;Źródło;Status and the columns (formulas protected) – an operator export (rule 1).</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Items.Count == 0)
        {
            ReportStatus(Localizer[L.CorrespondenceRecipients.ExportNothing]);
            return;
        }

        var path = await Services.Files.SaveFileAsync(
            Localizer[L.CorrespondenceRecipients.ExportTitle],
            Localizer[L.CorrespondenceRecipients.ExportFileName],
            [new FileFilter("CSV", "*.csv")]).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var columns = Columns.ToList();
        IReadOnlyList<string> headers =
        [
            Localizer[L.CorrespondenceApp.RecipientsColumnName],
            Localizer[L.CorrespondenceApp.RecipientsColumnEmail],
            Localizer[L.CorrespondenceApp.RecipientsColumnSource],
            Localizer[L.CorrespondenceApp.RecipientsColumnStatus],
            .. columns,
        ];
        var rows = Items.Select(r => (IReadOnlyList<string>)[r.Name, r.Email, r.Source, r.Status, .. columns.Select(c => r[c])]).ToList();
        var separator = CsvFiles.ListSeparator(Services.Regional.Culture);
        try
        {
            await CsvFiles.WriteAsync(path, writer => Correspondence.Csv.Write(writer, headers, rows, separator, protectFormulas: true)).ConfigureAwait(true);
            ReportStatus(Localizer.Format(L.CorrespondenceRecipients.Exported, path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Services.Dialogs.ShowErrorAsync(Localizer[L.CorrespondenceRecipients.ExportTitle], Services.Errors.Translate(ex)).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (Items.Count == 0)
        {
            return;
        }

        if (!await Services.Dialogs.ConfirmAsync(Localizer[L.CorrespondenceRecipients.ClearTitle],
                Localizer.Format(L.CorrespondenceRecipients.ClearConfirm, Items.Count), destructive: true).ConfigureAwait(true))
        {
            return;
        }

        foreach (var row in Items)
        {
            row.PropertyChanged -= OnRowChanged;
        }

        Items.Clear();
        View.Clear();
        Columns.Clear();
        UpdateCounts();
        ReportStatus(Localizer[L.CorrespondenceRecipients.Cleared]);
    }

    /// <summary>The journal report: count, oldest, newest, retention; prune or clear after a confirmation.</summary>
    [RelayCommand]
    private async Task ShowJournalAsync()
    {
        var journal = Correspondence.Journal;
        var title = Localizer[L.CorrespondenceRecipients.JournalTitle];
        if (!journal.IsLoaded)
        {
            await Services.Dialogs.ShowInfoAsync(title, Localizer[L.CorrespondenceRecipients.JournalUnavailable]).ConfigureAwait(true);
            return;
        }

        var retention = Math.Clamp(Services.Settings.Current.Correspondence.JournalRetentionDays, 1, CorrespondenceSettings.MaxJournalRetentionDays);
        string Date(DateTimeOffset? value) => value is { } time
            ? TimeZoneInfo.ConvertTime(time, Services.Time.LocalTimeZone).DateTime.ToString("g", Localizer.Culture)
            : Localizer[L.CorrespondenceRecipients.JournalNoDate];
        var report = Localizer.Format(L.CorrespondenceRecipients.JournalReport, journal.Count, Date(journal.Oldest), Date(journal.Newest), retention, journal.Folder);
        if (Correspondence.Dialogs is { } dialogs)
        {
            await dialogs.ShowReportAsync(title, report).ConfigureAwait(true);
        }
        else
        {
            await Services.Dialogs.ShowInfoAsync(title, report).ConfigureAwait(true);
        }

        if (journal.Count == 0)
        {
            return;
        }

        try
        {
            if (await Services.Dialogs.ConfirmAsync(title, Localizer.Format(L.CorrespondenceRecipients.JournalPruneConfirm, retention), destructive: false).ConfigureAwait(true))
            {
                var removed = await journal.PruneAsync(TimeSpan.FromDays(retention)).ConfigureAwait(true);
                ReportStatus(Localizer.Format(L.CorrespondenceRecipients.JournalPruned, removed));
            }
            else if (await Services.Dialogs.ConfirmAsync(title, Localizer[L.CorrespondenceRecipients.JournalClearConfirm], destructive: true).ConfigureAwait(true))
            {
                await journal.ClearAsync().ConfigureAwait(true);
                ReportStatus(Localizer[L.CorrespondenceRecipients.JournalCleared]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await Services.Dialogs.ShowErrorAsync(title, Services.Errors.Translate(ex)).ConfigureAwait(true);
        }

        RefreshJournalMarks();
    }

    [RelayCommand]
    private void AddDirectoryField() => _directoryFields.Add();

    [RelayCommand]
    private void RemoveDirectoryField(DirectoryFieldViewModel? field) => _directoryFields.Remove(field);

    [RelayCommand]
    private void ResetDirectoryFields() => _directoryFields.Reset();
}
