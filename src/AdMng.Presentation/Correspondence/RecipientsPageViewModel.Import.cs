using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Correspondence.Import;
using AdMng.Core.Messaging.Html;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Correspondence;

/// <summary>What one import added (design note 7.1).</summary>
/// <param name="Added">New rows.</param>
/// <param name="Duplicates">Addresses already on the list (or repeated in the source).</param>
/// <param name="OverLimit">Rows left out because the list holds <see cref="CorrespondenceLimits.MaxRecipients"/>.</param>
internal readonly record struct RecipientsAdded(int Added, int Duplicates, int OverLimit);

/// <summary>
/// Recipients from files, the clipboard, typed text, the newest contacts file of Downloads and drag and drop, and the
/// "Nie wysyłać" list (in memory only). Files are read and parsed on the thread pool with the ANSI code page of the system.
/// </summary>
public sealed partial class RecipientsPageViewModel
{
    /// <summary>Adds rows, de-duplicated by address against the existing rows; returns (added, duplicates).</summary>
    public (int Added, int Duplicates) AddRows(IEnumerable<ImportedContact> contacts, string source)
    {
        var result = AddRows(contacts, source, RecipientOrigin.File);
        return (result.Added, result.Duplicates);
    }

    /// <summary>Drag and drop: reads the files (≤ 50 MiB each) on the thread pool and adds their rows.</summary>
    public async Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var files = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var supported = files.Where(p => ContactFileReader.IsSupported(p) && File.Exists(p)).ToList();
        var unsupported = files.Except(supported, StringComparer.OrdinalIgnoreCase).Select(Path.GetFileName).ToList();
        StatusText = string.Empty;
        await ImportFilesCoreAsync(supported).ConfigureAwait(true);
        if (unsupported.Count > 0)
        {
            // What was loaded and what was left out are one sentence: the files of other formats are what was skipped.
            var note = Localizer.Format(L.CorrespondenceRecipients.Unsupported, string.Join(", ", unsupported));
            ReportStatus(StatusText.Length == 0 ? note : $"{StatusText} {note}", L.CorrespondenceRecipients.Unsupported, true, unsupported.Count);
        }
    }

    internal RecipientsAdded AddRows(IEnumerable<ImportedContact> contacts, string source, RecipientOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(contacts);
        var known = Addresses();
        var room = Math.Max(0, CorrespondenceLimits.MaxRecipients - Items.Count);
        var added = new List<CorrespondenceRecipientViewModel>();
        int duplicates = 0, overLimit = 0;
        foreach (var contact in contacts)
        {
            var email = EmailAddressText.Normalize(contact?.Email);
            if (contact is null || !EmailAddressText.IsValid(email))
            {
                continue;
            }

            if (!known.Add(email))
            {
                duplicates++;
                continue;
            }

            if (added.Count >= room)
            {
                overLimit++;
                continue;
            }

            added.Add(new CorrespondenceRecipientViewModel(email, contact.Name?.Trim() ?? string.Empty, source ?? string.Empty,
                contact.Fields ?? new Dictionary<string, string>()) { Origin = origin });
        }

        Insert(added);
        return new RecipientsAdded(added.Count, duplicates, overLimit);
    }

    /// <summary>"120 nowych, 3 powtórzone, 2 błędne adresy" (Polish plural forms); "" parts left out except the new ones.
    /// <paramref name="hiddenSheets"/>/<paramref name="hiddenRows"/>: what an Excel workbook hides and the import left out.</summary>
    internal string CountsPart(RecipientsAdded added, int invalid, int withoutAddress = 0, int other = 0, int hiddenSheets = 0, int hiddenRows = 0)
    {
        if (added.Added == 0 && added.Duplicates == 0 && added.OverLimit == 0 && invalid == 0 && withoutAddress == 0 && other == 0
            && hiddenSheets == 0 && hiddenRows == 0)
        {
            return Localizer[L.CorrespondenceRecipients.NothingFound];
        }

        var parts = new List<string>
        {
            CorrespondenceText.Plural(Localizer, added.Added, L.CorrespondenceRecipients.NewOne, L.CorrespondenceRecipients.NewFew, L.CorrespondenceRecipients.NewMany),
        };
        if (added.Duplicates > 0)
        {
            parts.Add(CorrespondenceText.Plural(Localizer, added.Duplicates, L.CorrespondenceRecipients.DuplicatesOne, L.CorrespondenceRecipients.DuplicatesFew, L.CorrespondenceRecipients.DuplicatesMany));
        }

        if (invalid > 0)
        {
            parts.Add(CorrespondenceText.Plural(Localizer, invalid, L.CorrespondenceRecipients.InvalidOne, L.CorrespondenceRecipients.InvalidFew, L.CorrespondenceRecipients.InvalidMany));
        }

        if (withoutAddress > 0)
        {
            parts.Add(Localizer.Format(L.CorrespondenceRecipients.WithoutAddress, withoutAddress));
        }

        if (other > 0)
        {
            parts.Add(Localizer.Format(L.CorrespondenceRecipients.OtherObjects, other));
        }

        if (hiddenSheets > 0)
        {
            parts.Add(Localizer.Format(L.CorrespondenceRecipients.HiddenSheets, hiddenSheets));
        }

        if (hiddenRows > 0)
        {
            parts.Add(Localizer.Format(L.CorrespondenceRecipients.HiddenRows, hiddenRows));
        }

        if (added.OverLimit > 0)
        {
            parts.Add(Localizer.Format(L.CorrespondenceRecipients.OverLimit, CorrespondenceLimits.MaxRecipients, added.OverLimit));
        }

        return string.Join(", ", parts);
    }

    /// <summary>The ANSI code page of legacy text files (Windows-1250 on Polish Windows).</summary>
    private int AnsiCodePage => Correspondence.Platform?.AnsiCodePage is > 0 and var page ? page : Services.Regional.AnsiCodePage;

    private IReadOnlyList<FileFilter> ContactFilters =>
    [
        new(Localizer[L.CorrespondenceRecipients.FilesFilter], string.Join(';', ContactFileReader.Extensions.Select(e => "*" + e))),
        new(Localizer[L.CorrespondenceRecipients.AllFiles], "*.*"),
    ];

    /// <summary>Reads and parses one file on the thread pool.</summary>
    /// <exception cref="InvalidDataException">Too large, corrupt or not readable as its format.</exception>
    private Task<ContactImportResult> ReadFileAsync(string path) =>
        ReadFileAsync(path, (name, bytes, codePage) => ContactFileReader.Read(name, bytes, codePage));

    /// <summary>A test seam: a read on the thread pool waits for this task first, so a test can stop an import that nothing else can hold.</summary>
    internal Task? ReadGate { get; set; }

    /// <summary>
    /// A test seam: how many reads on the thread pool have ended (parsed, failed or cancelled; their result kept or dropped),
    /// so a test of a stopped import can wait for the read it left behind instead of sleeping.
    /// </summary>
    internal int ReadsEnded => Volatile.Read(ref _readsEnded);

    /// <summary>Reads one file on the thread pool and parses it with <paramref name="parse"/> (file name, bytes, ANSI code page).</summary>
    /// <exception cref="InvalidDataException">Too large, corrupt or not readable as its format.</exception>
    private Task<T> ReadFileAsync<T>(string path, Func<string, byte[], int, T> parse)
    {
        var codePage = AnsiCodePage;
        var gate = ReadGate;
        return Task.Run(async () =>
        {
            try
            {
                if (gate is not null)
                {
                    await gate.ConfigureAwait(false);
                }

                if (new FileInfo(path).Length > ContactFileReader.MaxFileBytes)
                {
                    throw new InvalidDataException("The file is larger than 50 MiB.");
                }

                var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                return parse(Path.GetFileName(path), bytes, codePage);
            }
            finally
            {
                Interlocked.Increment(ref _readsEnded);
            }
        });
    }

    /// <summary>
    /// Reads the files one after another, adds their rows (source = file name), reports counts and failures. It is the
    /// operation of the page: the line says which file (or how many of them) is being read and Stop gives control back at
    /// once - the thread that parses runs to its end and its result is dropped; the rows of the files read before stay.
    /// </summary>
    private async Task ImportFilesCoreAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        // One after another with the other operations that add rows (see TakeAdditionTurnAsync).
        using var turn = await TakeAdditionTurnAsync().ConfigureAwait(true);
        var errors = new List<string>();
        int added = 0, duplicates = 0, overLimit = 0, invalid = 0, hiddenSheets = 0, hiddenRows = 0, read = 0;
        ContactSourceFormat? format = null;
        var single = paths.Count == 1;
        using var scope = Status.Begin(
            single
                ? Localizer.Format(L.RecipientsStatus.FileReading, Path.GetFileName(paths[0]))
                : Localizer.Format(L.RecipientsStatus.FilesReading, PageStatusText.Progress(Localizer, 0, paths.Count)),
            single ? new PageStatusLog(L.RecipientsStatus.FileReading) : new PageStatusLog(L.RecipientsStatus.FilesReading, 0, paths.Count),
            single ? default : PageProgress.Of(0, paths.Count));
        var token = scope.Token;
        var stopped = false;
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            try
            {
                var result = await ReadFileAsync(path).WaitOrAbandonAsync(token).ConfigureAwait(true);
                var outcome = AddRows(result.Contacts, name, RecipientOrigin.File);
                added += outcome.Added;
                duplicates += outcome.Duplicates + result.Duplicates;   // on the list already, or repeated in the file
                overLimit += outcome.OverLimit;
                invalid += result.InvalidAddresses;
                hiddenSheets += result.HiddenSheets;
                hiddenRows += result.HiddenRows;
                format = result.Format;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                stopped = true;
                break;
            }
            catch (InvalidDataException)
            {
                errors.Add(Localizer.Format(L.CorrespondenceRecipients.ReadFailed, name, Localizer[L.CorrespondenceRecipients.InvalidFile]));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                errors.Add(Localizer.Format(L.CorrespondenceRecipients.ReadFailed, name, Services.Errors.Translate(ex)));
            }

            read++;
            if (!single)
            {
                scope.Report(
                    Localizer.Format(L.RecipientsStatus.FilesReading, PageStatusText.Progress(Localizer, read, paths.Count)),
                    PageProgress.Of(read, paths.Count),
                    new PageStatusLog(L.RecipientsStatus.FilesReading, read, paths.Count));
            }
        }

        if (stopped)
        {
            // What the files read before the stop added stays on the list; the page says so and how far it got - when the
            // operator asked for it.
            if (scope.IsCancelling)
            {
                StatusText = string.Empty;
                StatusText = Localizer.Format(
                    L.RecipientsStatus.FilesStopped,
                    Localizer.Format(CorrespondenceText.PluralKey(paths.Count, L.RecipientsStatus.FilesOfOne, L.RecipientsStatus.FilesOfFew, L.RecipientsStatus.FilesOfMany), read, paths.Count));
                scope.CompleteAttention(StatusText, new PageStatusLog(L.RecipientsStatus.FilesStopped, read, paths.Count));
            }

            return;
        }

        string? done = null;
        if (paths.Count - errors.Count > 0)
        {
            var counts = CountsPart(new RecipientsAdded(added, duplicates, overLimit), invalid, hiddenSheets: hiddenSheets, hiddenRows: hiddenRows);
            done = single
                ? Localizer.Format(L.CorrespondenceRecipients.ImportedFile, Path.GetFileName(paths[0]),
                    Localizer[CorrespondenceText.FormatKey(format ?? ContactSourceFormat.Text)], counts)
                : Localizer.Format(L.CorrespondenceRecipients.ImportedFiles, paths.Count - errors.Count, counts);
            StatusText = string.Empty;
            StatusText = done;
        }

        var key = single ? L.CorrespondenceRecipients.ImportedFile : L.CorrespondenceRecipients.ImportedFiles;
        if (errors.Count == 0)
        {
            scope.CompleteDone(done!, new PageStatusLog(key, added, duplicates, invalid));
        }
        else if (done is not null)
        {
            // Some files were read, some were not: what was read and why the others were not is one sentence.
            scope.CompleteAttention(Localizer.Format(L.RecipientsStatus.ImportPartly, done, FileErrors(errors)),
                new PageStatusLog(L.RecipientsStatus.ImportPartly, added, errors.Count));
        }
        else
        {
            scope.CompleteError(Localizer.Format(L.RecipientsStatus.ImportFailed, FileErrors(errors)),
                new PageStatusLog(L.RecipientsStatus.ImportFailed, errors.Count));
        }
    }

    /// <summary>The reasons the files were not loaded, each a sentence, in one text (names on the screen only).</summary>
    private static string FileErrors(IReadOnlyList<string> errors) =>
        string.Join(" ", errors.Select(error => error.TrimEnd().EndsWith('.') ? error.TrimEnd() : error.TrimEnd() + "."));

    /// <summary>The files chosen in the open dialog (several with the window's dialogs, else one).</summary>
    private async Task<IReadOnlyList<string>> PickFilesAsync(string title)
    {
        if (Correspondence.Dialogs is { } dialogs)
        {
            return await dialogs.OpenFilesAsync(title, ContactFilters).ConfigureAwait(true);
        }

        return await Services.Files.OpenFileAsync(title, ContactFilters).ConfigureAwait(true) is { } path ? [path] : [];
    }

    /// <summary>The clipboard's text, else the text of its CF_HTML (<see cref="HtmlToText"/>); empty when it holds neither.</summary>
    private async Task<string> ClipboardTextAsync()
    {
        var content = Services.Clipboard.ReadFormatted();
        if (content is null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(content.Text))
        {
            return content.Text;
        }

        if (content.CfHtml is not { Length: > 0 } && string.IsNullOrWhiteSpace(content.CfHtmlText))
        {
            return string.Empty;
        }

        return await Task.Run(() =>
        {
            var document = content.CfHtml is { Length: > 0 } bytes ? CfHtmlParser.Parse(bytes) : CfHtmlParser.FromText(content.CfHtmlText!);
            return HtmlToText.Convert(document.Html);
        }).ConfigureAwait(true);
    }

    /// <summary>The newest <see cref="CorrespondenceSettings.DownloadsPattern"/> file of the Downloads folder.</summary>
    [RelayCommand(CanExecute = nameof(CanImportFromDownloads))]
    private async Task ImportFromDownloadsAsync()
    {
        if (Correspondence.Platform?.DownloadsFolder is not { Length: > 0 } folder)
        {
            NotLoaded(Localizer[L.CorrespondenceRecipients.NoDownloadsFolder], L.CorrespondenceRecipients.NoDownloadsFolder);
            return;
        }

        var pattern = SafePattern(_downloadsPattern);
        string? newest;
        try
        {
            newest = await Task.Run(() => new DirectoryInfo(folder).EnumerateFiles(pattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            newest = null;
        }

        if (newest is null)
        {
            NotLoaded(Localizer.Format(L.CorrespondenceRecipients.NoDownloadsFile, folder, pattern), L.CorrespondenceRecipients.NoDownloadsFile);
            return;
        }

        await ImportFilesCoreAsync([newest]).ConfigureAwait(true);
    }

    private bool CanImportFromDownloads() => Correspondence.Platform is not null;

    /// <summary>Files of <see cref="ContactFileReader.Extensions"/> (multi-select).</summary>
    [RelayCommand]
    private async Task ImportFilesAsync()
    {
        var paths = await PickFilesAsync(Localizer[L.CorrespondenceRecipients.FilesTitle]).ConfigureAwait(true);
        await ImportFilesCoreAsync(paths).ConfigureAwait(true);
    }

    /// <summary>The clipboard's text, else the text of its CF_HTML.</summary>
    [RelayCommand]
    private async Task PasteAsync()
    {
        var text = await ClipboardTextAsync().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(text))
        {
            ReportStatus(Localizer[L.CorrespondenceRecipients.ClipboardEmpty], L.CorrespondenceRecipients.ClipboardEmpty, attention: true);
            return;
        }

        var result = await Task.Run(() => ContactExtractor.FromText(text)).ConfigureAwait(true);
        var added = AddRows(result.Contacts, Localizer[L.CorrespondenceRecipients.SourceClipboard], RecipientOrigin.Clipboard);
        ReportStatus(Localizer.Format(L.CorrespondenceRecipients.Pasted, CountsPart(WithRepeats(added, result), result.InvalidAddresses)),
            L.CorrespondenceRecipients.Pasted, FoundNothing(WithRepeats(added, result), result.InvalidAddresses), added.Added, WithRepeats(added, result).Duplicates, result.InvalidAddresses);
    }

    /// <summary>Addresses typed into a multiline prompt.</summary>
    [RelayCommand(CanExecute = nameof(HasDialogs))]
    private async Task AddManualAsync()
    {
        if (Correspondence.Dialogs is not { } dialogs)
        {
            return;
        }

        var text = await dialogs.PromptTextAsync(Localizer[L.CorrespondenceRecipients.ManualTitle], Localizer[L.CorrespondenceRecipients.ManualPrompt],
            string.Empty, multiline: true).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var result = await Task.Run(() => ContactExtractor.FromText(text)).ConfigureAwait(true);
        var added = AddRows(result.Contacts, Localizer[L.CorrespondenceRecipients.SourceManual], RecipientOrigin.Manual);
        ReportStatus(Localizer.Format(L.CorrespondenceRecipients.Typed, CountsPart(WithRepeats(added, result), result.InvalidAddresses)),
            L.CorrespondenceRecipients.Typed, FoundNothing(WithRepeats(added, result), result.InvalidAddresses), added.Added, WithRepeats(added, result).Duplicates, result.InvalidAddresses);
    }

    /// <summary>The duplicates of an import: addresses already on the list and addresses repeated in the source itself
    /// (merged by the reader, design note 7.1).</summary>
    private static RecipientsAdded WithRepeats(RecipientsAdded added, ContactImportResult result) =>
        added with { Duplicates = added.Duplicates + result.Duplicates };

    private bool HasDialogs() => Correspondence.Dialogs is not null;

    /// <summary>An import that found no address at all: what it says is an attention, not a result.</summary>
    private static bool FoundNothing(RecipientsAdded added, int invalid) =>
        added.Added == 0 && added.Duplicates == 0 && added.OverLimit == 0 && invalid == 0;

    /// <summary>
    /// Nothing was loaded and why (the Downloads folder is not known, no file fits the pattern): what the button was pressed
    /// for did not happen, so the line says it as an attention - "Nothing was loaded: …" - and the sentence is its advice.
    /// </summary>
    private void NotLoaded(string reason, string key)
    {
        StatusText = string.Empty;
        StatusText = PageStatusText.Failure(Localizer[L.RecipientsStatus.NothingLoaded], reason);
        Say(PageStatus.Attention(StatusText, new PageStatusLog(key)));
    }

    /// <summary>Every address of the files becomes a "Nie wysyłać" entry – in any column, cell or sheet, not only the e-mail
    /// column of a table (<see cref="ContactFileReader.ReadAddresses"/>).</summary>
    [RelayCommand]
    private async Task ExclusionsFromFileAsync()
    {
        var paths = await PickFilesAsync(Localizer[L.CorrespondenceRecipients.ExclusionsFilesTitle]).ConfigureAwait(true);
        if (paths.Count == 0)
        {
            return;
        }

        var errors = new List<string>();
        var addresses = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                var found = await ReadFileAsync(path, (name, bytes, codePage) => ContactFileReader.ReadAddresses(name, bytes, codePage)).ConfigureAwait(true);
                addresses.AddRange(found);
            }
            catch (InvalidDataException)
            {
                errors.Add(Localizer.Format(L.CorrespondenceRecipients.ReadFailed, Path.GetFileName(path), Localizer[L.CorrespondenceRecipients.InvalidFile]));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                errors.Add(Localizer.Format(L.CorrespondenceRecipients.ReadFailed, Path.GetFileName(path), Services.Errors.Translate(ex)));
            }
        }

        var (added, text) = AddExclusionsQuietly(addresses);
        if (errors.Count == 0)
        {
            ReportStatus(text, L.CorrespondenceRecipients.ExclusionsAdded, false, added, _excluded.Count);
            return;
        }

        // The files that could not be read are part of the one answer: what was added and what was not read - not a done
        // first and the files after it (two toasts while a read has the line). Never dropped: an exclusion file that was
        // silently not read leaves addresses on the list that must not be written to.
        StatusText = string.Empty;
        StatusText = Localizer.Format(L.RecipientsStatus.ImportPartly, text, FileErrors(errors));
        Say(PageStatus.Attention(StatusText, new PageStatusLog(L.RecipientsStatus.ImportPartly, addresses.Count, errors.Count)));
    }

    /// <summary>The clipboard's addresses, else a prompt.</summary>
    [RelayCommand]
    private async Task ExclusionsFromClipboardAsync()
    {
        var addresses = EmailAddressText.Extract(await ClipboardTextAsync().ConfigureAwait(true));
        if (addresses.Count == 0 && Correspondence.Dialogs is { } dialogs)
        {
            var typed = await dialogs.PromptTextAsync(Localizer[L.CorrespondenceRecipients.ExclusionsTitle], Localizer[L.CorrespondenceRecipients.ExclusionsPrompt],
                string.Empty, multiline: true).ConfigureAwait(true);
            addresses = EmailAddressText.Extract(typed);
        }

        if (addresses.Count == 0)
        {
            ReportStatus(Localizer[L.CorrespondenceRecipients.NoAddressesFound], L.CorrespondenceRecipients.NoAddressesFound, attention: true);
            return;
        }

        AddExclusions(addresses);
    }

    [RelayCommand]
    private void ClearExclusions()
    {
        _excluded.Clear();
        ApplyExclusions();
        ReportStatus(Localizer[L.CorrespondenceRecipients.ExclusionsCleared], L.CorrespondenceRecipients.ExclusionsCleared);
    }

    private void AddExclusions(IEnumerable<string> addresses)
    {
        var (added, text) = AddExclusionsQuietly(addresses);
        ReportStatus(text, L.CorrespondenceRecipients.ExclusionsAdded, false, added, _excluded.Count);
    }

    /// <summary>Adds the "Nie wysyłać" entries and marks the rows; the caller says the answer (how many were added, and the sentence).</summary>
    private (int Added, string Text) AddExclusionsQuietly(IEnumerable<string> addresses)
    {
        var added = 0;
        foreach (var address in addresses)
        {
            var normalized = EmailAddressText.Normalize(address);
            if (EmailAddressText.IsValid(normalized) && _excluded.Add(normalized))
            {
                added++;
            }
        }

        ApplyExclusions();
        return (added, Localizer.Format(L.CorrespondenceRecipients.ExclusionsAdded, added, _excluded.Count));
    }

    /// <summary>A file name pattern without directories (a pattern with a path falls back to the default).</summary>
    private static string SafePattern(string pattern) =>
        string.IsNullOrWhiteSpace(pattern) || pattern.Contains("..", StringComparison.Ordinal) || pattern.IndexOfAny(['/', '\\', ':']) >= 0
            || pattern.IndexOfAny(Path.GetInvalidFileNameChars().Where(c => c is not '*' and not '?').ToArray()) >= 0
            ? CorrespondenceSettings.DefaultDownloadsPattern
            : pattern.Trim();
}
