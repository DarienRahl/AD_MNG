using AdMng.Core.Correspondence.Mailboxes;
using AdMng.Core.Directory;
using AdMng.Core.Schema;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Session;
using AdMng.Presentation.Settings;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Correspondence;

/// <summary>
/// The query of the "Skrzynki" tab: the mailboxes of the scope, then the persons of the person base (both streamed from the
/// window's session on the thread pool, with their limits and cancellation), the index of the persons, the matcher per
/// mailbox, and the rows handed to the UI in batches.
/// </summary>
public sealed partial class MailboxOwnersPageViewModel
{
    /// <summary>Rows handed to the UI at once.</summary>
    internal const int RowBatchSize = 200;

    private CancellationTokenSource? _run;

    /// <summary>The attributes the mailboxes of <see cref="Rows"/> were read with (a later mailbox field may need more).</summary>
    private IReadOnlyCollection<string> _mailboxAttributesRead = [];

    /// <summary>The most mailboxes of one query (tests lower it).</summary>
    internal int MaxMailboxes { get; set; } = MailboxOwnerFinder.DefaultMaxMailboxes;

    /// <summary>The most persons of the person base (tests lower it).</summary>
    internal int MaxPersons { get; set; } = MailboxOwnerFinder.DefaultMaxPersons;

    /// <summary>The attributes of the mailbox read: entry, owner name and the attributes of the mailbox fields.</summary>
    internal static IReadOnlyList<string> MailboxAttributes(ISchemaCatalog? schema, DirectoryRowBuilder builder, OwnerNameSource source, string? attribute) =>
    [
        .. MailboxEntry.AttributesFor(schema)
            .Concat(OwnerNameParser.AttributesFor(source, attribute))
            .Concat(builder.AttributesFor(DirectoryFieldTarget.Mailbox))
            .Distinct(StringComparer.OrdinalIgnoreCase),
    ];

    /// <summary>Reads the mailboxes and the persons, builds the index, matches and streams the rows.</summary>
    [RelayCommand(CanExecute = nameof(CanFindOwners))]
    private async Task FindOwnersAsync()
    {
        Error = null;
        if (TrySession() is not { } session)
        {
            Fail(Localizer[L.WindowSession.Ended]);
            return;
        }

        RefreshSchema(session.Schema);
        var source = NameSource.Value;
        var attribute = NameAttribute?.Trim() ?? string.Empty;
        if (source == OwnerNameSource.Attribute && !OwnerNameParser.IsUsableAttribute(attribute))
        {
            Fail(Localizer.Format(L.CorrespondenceMailboxes.NameAttributeInvalid, attribute));
            return;
        }

        if (ExtraFilterError is not null)
        {
            return;
        }

        if (await ResolveScopeAsync(session).ConfigureAwait(true) is not { } scope)
        {
            return;
        }

        var run = new CancellationTokenSource();
        Interlocked.Exchange(ref _run, run)?.Cancel();
        var token = run.Token;
        ClearRows();
        IsRunning = true;
        ProgressText = Localizer.Format(L.CorrespondenceMailboxes.ProgressMailboxes, 0);
        try
        {
            await RunAsync(session, scope, source, attribute, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ProgressText = Localizer.Format(L.CorrespondenceMailboxes.Cancelled, ProgressText);
        }
        catch (DirectoryException ex)
        {
            Fail(Localizer.Format(L.CorrespondenceMailboxes.Failed, Services.Errors.Translate(ex)));
        }
        catch (Exception ex) when (ex is SessionEndedException or InvalidOperationException or ObjectDisposedException)
        {
            Fail(Localizer[L.WindowSession.Ended]);
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _run, null, run), run))
            {
                IsRunning = false;
            }

            UpdateSummary();
            AddToRecipientsCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanFindOwners() => !IsRunning && ExtraFilterError is null;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => Volatile.Read(ref _run)?.Cancel();

    private async Task RunAsync(IDirectorySession session, MailboxSearchScope scope, OwnerNameSource source, string attribute, CancellationToken token)
    {
        var builder = new DirectoryRowBuilder(Correspondence.Fields, _fields.Definitions, DirectoryRowBuilder.ContextFor(Services, session));
        var attributes = MailboxAttributes(session.Schema, builder, source, attribute);
        _mailboxAttributesRead = attributes;
        var finder = new MailboxOwnerFinder(session, Services.Settings.Current.PageSize) { MaxMailboxes = MaxMailboxes, MaxPersons = MaxPersons };
        var filter = MailboxOwnerFinder.MailboxFilter(EffectiveSelection, ExtraFilter);
        var personBase = PersonBaseDn(session);
        var enabledOnly = EnabledOwnersOnly;

        var (mailboxes, failure) = await Task.Run(
            () => finder.ReadMailboxesAsync(scope, filter, attributes, source, attribute, count => ShowProgressAsync(token, count, null), token),
            token).ConfigureAwait(true);
        if (failure != MailboxReadFailure.None)
        {
            Fail(Localizer.Format(L.CorrespondenceMailboxes.TooManyMailboxes, MaxMailboxes));
            return;
        }

        if (mailboxes.Count == 0)
        {
            ProgressText = Localizer[L.CorrespondenceMailboxes.NoMailboxes];
            return;
        }

        var (persons, personFailure) = await Task.Run(
            () => finder.ReadPersonsAsync(personBase, count => ShowProgressAsync(token, mailboxes.Count, count), token),
            token).ConfigureAwait(true);
        if (personFailure != MailboxReadFailure.None)
        {
            Fail(Localizer.Format(L.CorrespondenceMailboxes.TooManyPersons, MaxPersons));
            return;
        }

        await Task.Run(
            async () =>
            {
                var index = new PersonIndex(persons);
                var options = new OwnerMatchOptions();
                var batch = new List<(MailboxRead Mailbox, OwnerMatch Match)>(RowBatchSize);
                foreach (var mailbox in mailboxes)
                {
                    token.ThrowIfCancellationRequested();
                    batch.Add((mailbox, MailboxOwnerMatcher.Match(mailbox.Entry, mailbox.Name, index, options)));
                    if (batch.Count == RowBatchSize)
                    {
                        var full = batch;
                        batch = new List<(MailboxRead, OwnerMatch)>(RowBatchSize);
                        await Services.Dispatcher.InvokeAsync(() => AddRows(full, enabledOnly, token)).ConfigureAwait(false);
                    }
                }

                await Services.Dispatcher.InvokeAsync(() => AddRows(batch, enabledOnly, token)).ConfigureAwait(false);
            },
            token).ConfigureAwait(true);
        token.ThrowIfCancellationRequested();
        ProgressText = Localizer.Format(L.CorrespondenceMailboxes.Progress, mailboxes.Count, persons.Count);
    }

    private Task ShowProgressAsync(CancellationToken token, int mailboxes, int? persons) => Services.Dispatcher.InvokeAsync(() =>
    {
        if (!token.IsCancellationRequested)
        {
            ProgressText = persons is { } count
                ? Localizer.Format(L.CorrespondenceMailboxes.Progress, mailboxes, count)
                : Localizer.Format(L.CorrespondenceMailboxes.ProgressMailboxes, mailboxes);
        }
    });

    /// <summary>The scope of the query; an OU not chosen yet opens the container picker; null when there is nothing to search.</summary>
    private async Task<MailboxSearchScope?> ResolveScopeAsync(IDirectorySession session)
    {
        var domain = session.Info.DefaultNamingContext;
        switch (ScopeKind)
        {
            case MailboxScopeKind.Selection when _selection.Count == 0:
                Fail(Localizer[L.CorrespondenceMailboxes.ScopeSelectionNone]);
                return null;
            case MailboxScopeKind.Selection:
                return new MailboxSearchScope(domain, _selection);
            case MailboxScopeKind.OrganizationalUnit:
                if (_scopeOu is null)
                {
                    await BrowseScopeAsync().ConfigureAwait(true);
                }

                return _scopeOu is null ? null : new MailboxSearchScope(_scopeOu, null);
            default:
                return new MailboxSearchScope(domain, null);
        }
    }

    private void Fail(string message)
    {
        Error = message;
        ProgressText = message;
    }
}
