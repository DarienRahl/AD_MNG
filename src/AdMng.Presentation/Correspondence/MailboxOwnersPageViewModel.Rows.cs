using System.ComponentModel;
using AdMng.Core.Correspondence.Import;
using AdMng.Core.Correspondence.Mailboxes;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Session;
using AdMng.Presentation.Settings;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Correspondence;

/// <summary>
/// The rows of the "Skrzynki" tab: their texts (status, candidates, why), the filter and the summary, the operator's
/// decisions (pick a user, type an address, exclude, restore) and "Dodaj właścicieli do odbiorców".
/// </summary>
public sealed partial class MailboxOwnersPageViewModel
{
    /// <summary>Adds a batch of matched mailboxes (UI thread); the automatic owner follows <paramref name="enabledOnly"/>.</summary>
    private void AddRows(IReadOnlyList<(MailboxRead Mailbox, OwnerMatch Match)> batch, bool enabledOnly, CancellationToken token)
    {
        if (token.IsCancellationRequested || batch.Count == 0)
        {
            return;
        }

        var rows = new List<MailboxOwnerRowViewModel>(batch.Count);
        foreach (var (mailbox, match) in batch)
        {
            var candidates = match.Candidates.Select(c => Option(c.Person, c.IsDelegate, c.Level)).ToList();
            var row = new MailboxOwnerRowViewModel(mailbox.Entry, Location(mailbox.Entry.DistinguishedName),
                Localizer[CorrespondenceText.MailboxTypeKey(mailbox.Entry.Type)], mailbox.Name, match, candidates)
            {
                MailboxObject = mailbox.Account,
            };
            row.ApplyAutomatic(match.Status switch
            {
                OwnerMatchStatus.Matched or OwnerMatchStatus.MatchedByDelegate or OwnerMatchStatus.Probable or OwnerMatchStatus.OwnerWithoutAddress => true,
                OwnerMatchStatus.OwnerDisabled => !enabledOnly,
                _ => false,
            });
            UpdateRow(row);
            row.PropertyChanged += OnRowChanged;
            rows.Add(row);
        }

        Rows.AddRange(rows);
        var filter = Filter.Value;
        View.AddRange(rows.Where(r => Matches(r, filter)));
        UpdateSummary();
    }

    private void ClearRows()
    {
        foreach (var row in Rows)
        {
            row.PropertyChanged -= OnRowChanged;
        }

        Rows.Clear();
        View.Clear();
        UpdateSummary();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is MailboxOwnerRowViewModel row
            && e.PropertyName is nameof(MailboxOwnerRowViewModel.SelectedCandidate) or nameof(MailboxOwnerRowViewModel.RowState)
                or nameof(MailboxOwnerRowViewModel.OwnerAddress) or nameof(MailboxOwnerRowViewModel.Include))
        {
            UpdateRow(row);
            UpdateSummary();
        }
    }

    private void RefreshView()
    {
        var filter = Filter.Value;
        View.ReplaceAll(Rows.Where(r => Matches(r, filter)));
    }

    private static bool Matches(MailboxOwnerRowViewModel row, MailboxRowFilter filter) => filter switch
    {
        MailboxRowFilter.Ready => row.IsReady,
        MailboxRowFilter.NeedsReview => row.NeedsReview,
        MailboxRowFilter.NotFound => IsNotFound(row),
        MailboxRowFilter.Excluded => row.RowState == MailboxOwnerRowState.Excluded,
        _ => true,
    };

    private static bool IsNotFound(MailboxOwnerRowViewModel row) =>
        row.RowState == MailboxOwnerRowState.Automatic && row.Status is OwnerMatchStatus.NotFound or OwnerMatchStatus.NoName;

    /// <summary>"Dopasowane · do sprawdzenia · nieznalezione · wykluczone" – each row counted once.</summary>
    private void UpdateSummary()
    {
        int matched = 0, review = 0, notFound = 0, excluded = 0;
        foreach (var row in Rows)
        {
            if (row.RowState == MailboxOwnerRowState.Excluded)
            {
                excluded++;
            }
            else if (IsNotFound(row))
            {
                notFound++;
            }
            else if (row.NeedsReview)
            {
                review++;
            }
            else
            {
                matched++;
            }
        }

        SummaryText = Localizer.Format(L.CorrespondenceMailboxes.Summary, matched, review, notFound, excluded);
    }

    /// <summary>Status, colour and the reason of one row.</summary>
    private void UpdateRow(MailboxOwnerRowViewModel row)
    {
        switch (row.RowState)
        {
            case MailboxOwnerRowState.Excluded:
                row.StatusText = Localizer[CorrespondenceText.MailboxStatusKey(MailboxOwnerRowState.Excluded)];
                row.StatusKind = MailboxRowStatusKind.Warning;
                row.DetailText = Localizer[L.CorrespondenceMailboxes.DetailExcluded];
                return;
            case MailboxOwnerRowState.Manual:
                row.StatusText = Localizer[CorrespondenceText.MailboxStatusKey(MailboxOwnerRowState.Manual)];
                row.StatusKind = row.IsReady ? MailboxRowStatusKind.Ok : MailboxRowStatusKind.Warning;
                row.DetailText = Join(
                    Localizer[row.HasTypedAddress ? L.CorrespondenceMailboxes.DetailTyped : L.CorrespondenceMailboxes.DetailManual],
                    row.SelectedCandidate is { } chosen ? CandidateDetail(chosen, includeAddress: !row.HasTypedAddress) : null);
                return;
        }

        row.StatusText = Localizer[CorrespondenceText.MailboxStatusKey(row.Status)];
        row.StatusKind = row.Status switch
        {
            OwnerMatchStatus.Matched or OwnerMatchStatus.MatchedByDelegate => MailboxRowStatusKind.Ok,
            OwnerMatchStatus.NotFound or OwnerMatchStatus.NoName => MailboxRowStatusKind.Error,
            _ => MailboxRowStatusKind.Warning,
        };
        row.DetailText = row.Status switch
        {
            OwnerMatchStatus.NoName => Localizer[L.CorrespondenceMailboxes.DetailNoName],
            OwnerMatchStatus.NotFound => Localizer.Format(L.CorrespondenceMailboxes.DetailNotFound, row.OwnerNameText),
            OwnerMatchStatus.Ambiguous => Localizer.Format(L.CorrespondenceMailboxes.DetailAmbiguous, row.Candidates.Count),
            _ when row.SelectedCandidate is { } chosen => CandidateDetail(chosen, includeAddress: true),
            _ when row.Match.Best is { } best => Join(CandidateDetail(Option(best.Person, best.IsDelegate, best.Level), includeAddress: true),
                Localizer[L.CorrespondenceMailboxes.DetailNotChosen]),
            _ => string.Empty,
        };
    }

    /// <summary>"zgodne imię i nazwisko · ma dostęp do skrzynki · konto wyłączone".</summary>
    private string CandidateDetail(CandidateOption option, bool includeAddress) => Join(
        Localizer[CorrespondenceText.LevelKey(option.Level)],
        option.IsDelegate ? Localizer[L.CorrespondenceMailboxes.DetailDelegate] : null,
        option.Person.IsDisabled ? Localizer[L.CorrespondenceMailboxes.DetailDisabled] : null,
        includeAddress && option.Person.Address is null ? Localizer[L.CorrespondenceMailboxes.DetailNoAddress] : null);

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <summary>A candidate of the combo box: "Jan Kowalski – IT, jan.kowalski@contoso.test".</summary>
    private CandidateOption Option(PersonCandidate person, bool isDelegate, OwnerMatchLevel level)
    {
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(person.Department))
        {
            details.Add(person.Department);
        }

        details.Add(person.Address ?? Localizer[L.CorrespondenceMailboxes.CandidateNoAddress]);
        if (person.IsDisabled)
        {
            details.Add(Localizer[L.CorrespondenceMailboxes.CandidateDisabled]);
        }

        if (isDelegate)
        {
            details.Add(Localizer[L.CorrespondenceMailboxes.CandidateDelegate]);
        }

        return new CandidateOption(person, Localizer.Format(L.CorrespondenceMailboxes.Candidate, person.DisplayName, string.Join(", ", details)), isDelegate, level);
    }

    private static string Location(string distinguishedName) =>
        DistinguishedName.TryParse(distinguishedName, out var dn) && dn.Parent is { IsRoot: false } parent ? parent.ToCanonicalName() : string.Empty;

    /// <summary>The object picker (users): the picked person becomes the owner (Manual).</summary>
    [RelayCommand]
    private async Task PickOwnerAsync(MailboxOwnerRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            var session = Session();
            var picked = await Services.Dialogs.PickObjectsAsync(session, [ObjectKind.User], multiSelect: false).ConfigureAwait(true);
            if (picked.Count == 0)
            {
                return;
            }

            var account = await Task.Run(() => session.GetAsync(picked[0], PersonCandidate.AttributesFor(session.Schema), CancellationToken.None)).ConfigureAwait(true);
            if (account is null)
            {
                return;
            }

            var person = PersonCandidate.From(account);
            var level = row.OwnerName is { } name ? MailboxOwnerMatcher.LevelOf(name, person) : null;
            var isDelegate = row.Mailbox.DelegateDns.Any(d => DistinguishedName.AreEqual(d, person.DistinguishedName));
            row.ChooseManually(Option(person, isDelegate, level ?? OwnerMatchLevel.Partial));
        }
        catch (DirectoryException ex)
        {
            await Services.Dialogs.ShowErrorAsync(Localizer[L.CorrespondenceMailboxes.AddTitle], Services.Errors.Translate(ex)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is SessionEndedException or InvalidOperationException or ObjectDisposedException)
        {
            await Services.Dialogs.ShowErrorAsync(Localizer[L.CorrespondenceMailboxes.AddTitle], Localizer[L.WindowSession.Ended]).ConfigureAwait(true);
        }
    }

    /// <summary>A typed address becomes the owner's address (Manual).</summary>
    [RelayCommand(CanExecute = nameof(CanTypeOwnerAddress))]
    private async Task TypeOwnerAddressAsync(MailboxOwnerRowViewModel? row)
    {
        if (row is null || Correspondence.Dialogs is not { } dialogs)
        {
            return;
        }

        var title = Localizer[L.CorrespondenceMailboxes.TypeAddressTitle];
        var text = await dialogs.PromptTextAsync(title, Localizer.Format(L.CorrespondenceMailboxes.TypeAddressPrompt, row.MailboxName),
            row.OwnerAddress, multiline: false).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var address = EmailAddressText.Normalize(text);
        if (!EmailAddressText.IsValid(address))
        {
            await Services.Dialogs.ShowErrorAsync(title, Localizer.Format(L.CorrespondenceMailboxes.InvalidAddress, text.Trim())).ConfigureAwait(true);
            return;
        }

        row.TypeAddress(address);
    }

    private bool CanTypeOwnerAddress(MailboxOwnerRowViewModel? row) => Correspondence.Dialogs is not null;

    [RelayCommand]
    private void Exclude(MailboxOwnerRowViewModel? row) => row?.Exclude();

    [RelayCommand]
    private void Restore(MailboxOwnerRowViewModel? row) => row?.Restore();

    /// <summary>
    /// Rows with an owner address and Include become recipient rows (replacing earlier "Skrzynki" rows); grouping follows
    /// <see cref="GroupByOwner"/>; the built-in template is loaded when the message is empty. The owners are read again
    /// (with the attributes of the owner fields; the mailboxes too when a mailbox field added after the query needs more
    /// attributes) from the window's session on the thread pool; the read runs like a query (<see cref="IsRunning"/>,
    /// <see cref="CancelCommand"/>, cancelled when the session ends).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddToRecipients))]
    private async Task AddToRecipientsAsync()
    {
        var title = Localizer[L.CorrespondenceMailboxes.AddTitle];
        var ready = Rows.Where(r => r.IsReady).ToList();
        if (ready.Count == 0)
        {
            await Services.Dialogs.ShowInfoAsync(title, Localizer[L.CorrespondenceMailboxes.AddNothing]).ConfigureAwait(true);
            return;
        }

        var run = new CancellationTokenSource();
        Interlocked.Exchange(ref _run, run)?.Cancel();
        var token = run.Token;
        var progress = ProgressText;
        IsRunning = true;
        IReadOnlyList<CorrespondenceRecipientViewModel>? recipients = null;
        try
        {
            var session = Session();
            var builder = new DirectoryRowBuilder(Correspondence.Fields, _fields.Definitions, DirectoryRowBuilder.ContextFor(Services, session));
            var source = Localizer[L.CorrespondenceRecipients.SourceMailboxes];
            var work = ready.Select(r => (r.MailboxObject, r.Mailbox, Owner: r.Owner, Address: r.OwnerAddress, r.OwnerDisplayName)).ToList();
            var mailboxAttributes = builder.AttributesFor(DirectoryFieldTarget.Mailbox);
            var rereadMailboxes = work.Any(w => w.MailboxObject is null) || mailboxAttributes.Except(_mailboxAttributesRead, StringComparer.OrdinalIgnoreCase).Any();
            ProgressText = Localizer.Format(L.CorrespondenceMailboxes.ReadingOwners, work.Count(w => w.Owner is not null));
            recipients = await Task.Run(
                async () =>
                {
                    var owners = await DirectoryLookup.ReadManyAsync(session, work.Select(w => w.Owner?.DistinguishedName).OfType<string>(),
                        builder.AttributesFor(DirectoryFieldTarget.Person), token).ConfigureAwait(false);
                    var mailboxes = rereadMailboxes
                        ? await DirectoryLookup.ReadManyAsync(session, work.Select(w => w.Mailbox.DistinguishedName), mailboxAttributes, token).ConfigureAwait(false)
                        : null;
                    return work.Select(w =>
                    {
                        token.ThrowIfCancellationRequested();
                        var mailbox = mailboxes?.GetValueOrDefault(DnKey.For(w.Mailbox.DistinguishedName)) ?? w.MailboxObject;
                        return RecipientRow(builder, owners, source, mailbox, w.Mailbox, w.Owner, w.Address, w.OwnerDisplayName);
                    }).ToList();
                },
                token).ConfigureAwait(true);
            ProgressText = progress;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ProgressText = progress;
            ReportStatus(Localizer.Format(L.CorrespondenceMailboxes.Cancelled, Localizer[L.CorrespondenceMailboxes.AddCancelled]));
        }
        catch (DirectoryException ex)
        {
            ProgressText = progress;
            await Services.Dialogs.ShowErrorAsync(title, Services.Errors.Translate(ex)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is SessionEndedException or InvalidOperationException or ObjectDisposedException)
        {
            ProgressText = progress;
            await Services.Dialogs.ShowErrorAsync(title, Localizer[L.WindowSession.Ended]).ConfigureAwait(true);
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _run, null, run), run))
            {
                IsRunning = false;
            }
        }

        if (recipients is null)
        {
            return;
        }

        var added = Recipients.ReplaceMailboxRows(recipients);
        if (Message.IsBodyEmpty)
        {
            SelectMailboxTemplate();
        }

        Message.GroupByAddress = GroupByOwner;
        var owners = recipients.Select(r => r.Email).Distinct(StringComparer.Ordinal).Count();
        var status = Localizer.Format(L.CorrespondenceMailboxes.Added, added, owners);
        ReportStatus(status);
        Recipients.ReportStatus(status);
        OnRecipientsAdded();
    }

    /// <summary>Raises a status line (also when it repeats the previous one, so the status bar shows it again).</summary>
    private void ReportStatus(string text)
    {
        StatusText = string.Empty;
        StatusText = text;
    }

    private bool CanAddToRecipients() => !IsRunning && Rows.Count > 0;

    /// <summary>The recipient row of one mailbox: the owner's address and name, the mailbox fields, then the owner fields.</summary>
    private static CorrespondenceRecipientViewModel RecipientRow(DirectoryRowBuilder builder, IReadOnlyDictionary<string, DirectoryObject> owners,
        string source, DirectoryObject? mailbox, MailboxEntry entry, PersonCandidate? owner, string address, string ownerName)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (mailbox is not null)
        {
            foreach (var (name, value) in builder.Evaluate(mailbox, DirectoryFieldTarget.Mailbox))
            {
                fields.TryAdd(name, value);
            }
        }

        if (owner is not null && owners.TryGetValue(DnKey.For(owner.DistinguishedName), out var account))
        {
            foreach (var (name, value) in builder.Evaluate(account, DirectoryFieldTarget.Person))
            {
                fields.TryAdd(name, value);
            }
        }

        foreach (var name in builder.FieldNames(DirectoryFieldTarget.Person))
        {
            fields.TryAdd(name, string.Empty);
        }

        var key = string.IsNullOrWhiteSpace(entry.Address) ? entry.DistinguishedName : entry.Address;
        return new CorrespondenceRecipientViewModel(EmailAddressText.Normalize(address), ownerName, source, fields, isValid: true,
            owner?.DistinguishedName, [key])
        {
            Origin = RecipientOrigin.Mailboxes,
        };
    }

    /// <summary>
    /// "Czy skrzynka pocztowa jest nadal potrzebna?" through the message page's template choice (the message page loads a
    /// template when its selection changes).
    /// </summary>
    private void SelectMailboxTemplate()
    {
        var id = CorrespondenceTemplateLibrary.MailboxStillNeededId;
        Message.SelectedTemplate = Message.Templates.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal))
            ?? new TemplateOption(id, CorrespondenceTemplateLibrary.BuiltIns[0].Name, IsBuiltIn: true);
    }
}
