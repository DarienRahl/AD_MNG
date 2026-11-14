using AdMng.Core.ActiveDirectory;
using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Session;
using AdMng.Presentation.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Correspondence;

/// <summary>What one read of the directory produced: rows (in request order) and the counts of the status line.</summary>
internal sealed record DirectoryRead(IReadOnlyList<(DirectoryRow Row, string Source)> Rows, int Other, IReadOnlyList<string> TruncatedGroups)
{
    public override string ToString() => $"{nameof(DirectoryRead)} ({Rows.Count})";
}

/// <summary>
/// Recipients from the directory (design note 6.4): the objects selected in the connection window, picked users and
/// contacts, the members of a group (nested, and the accounts whose primary group it is – Domain Users; users and
/// contacts, at most <see cref="MaxGroupMembers"/>). Every object is
/// read from the window's session (rule 4) with the attributes its field definitions need and turned into a row by
/// <see cref="DirectoryRowBuilder"/> on the thread pool; objects without an address become invalid rows.
/// </summary>
public sealed partial class RecipientsPageViewModel
{
    /// <summary>The objects selected in the connection window when this window was opened ("Zaznaczone obiekty").</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddFromSelectionCommand))]
    public partial IReadOnlyList<string> SelectedObjects { get; set; } = [];

    /// <summary>
    /// Adds the objects (users, contacts, inetOrgPersons; groups: their members) as AD rows, de-duplicated by address. The
    /// read is the operation of the page - the line says how many objects it reads, Stop ends it and nothing is added -
    /// and its outcome is said there too: what was added, what was left out, or why nothing was (with Retry). Directory
    /// errors and a missing session are also thrown (after the line said them): the caller that keeps its own record of
    /// them (the window, for the objects it was opened with) still gets them; a read cancelled because the session ended
    /// adds nothing and says nothing - the page says why in its state that lasts.
    /// </summary>
    /// <exception cref="DirectoryException">The directory refused a read.</exception>
    /// <exception cref="SessionEndedException">The window's session ended.</exception>
    public async Task AddFromDirectoryAsync(IReadOnlyList<string> distinguishedNames)
    {
        ArgumentNullException.ThrowIfNull(distinguishedNames);
        var dns = distinguishedNames.Where(dn => !string.IsNullOrWhiteSpace(dn)).ToList();
        if (dns.Count == 0)
        {
            return;
        }

        _lastDirectoryRead = dns;
        using var scope = Status.Begin(
            Localizer.Format(L.RecipientsStatus.DirectoryReading, PageStatusText.Objects(Localizer, dns.Count)),
            new PageStatusLog(L.RecipientsStatus.DirectoryReading, dns.Count));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_directoryReads.Token, scope.Token);
        var token = linked.Token;
        DirectoryRead read;
        try
        {
            var session = Session();
            var builder = new DirectoryRowBuilder(Correspondence.Fields, _directoryFields.Definitions, DirectoryRowBuilder.ContextFor(Services, session));
            var directorySource = Localizer[L.CorrespondenceRecipients.SourceDirectory];
            var localizer = Localizer;
            Func<string, string> groupSource = group => localizer.Format(L.CorrespondenceRecipients.SourceGroup, group);
            var pageSize = Math.Clamp(Services.Settings.Current.PageSize, 1, 1000);
            read = await Task.Run(() => ReadAsync(session, builder, dns, directorySource, groupSource, pageSize, token), token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Only the operator's Stop says so: nothing was added, and the read is repeated by the action of the line. The
            // end of the session and a newer operation say nothing here.
            if (scope.IsCancelling)
            {
                scope.CompleteAttention(
                    PageStatusText.Failure(Localizer[L.RecipientsStatus.DirectoryNothingAdded], Localizer[L.RecipientsStatus.DirectoryStopped]),
                    new PageStatusLog(L.RecipientsStatus.DirectoryStopped, dns.Count),
                    new PageStatusAction(Localizer[L.Common.Retry], RetryDirectoryReadCommand));
            }

            return;
        }
        catch (DirectoryException ex)
        {
            var code = ResultCode.Of(ex);
            scope.CompleteError(
                PageStatusText.Failure(Localizer[L.RecipientsStatus.DirectoryNothingAdded], Services.Errors.Translate(ex), Localizer[L.Feedback.RetryNext], code),
                new PageStatusLog(L.RecipientsStatus.DirectoryNothingAdded, dns.Count).WithCode(code),
                new PageStatusAction(Localizer[L.Common.Retry], RetryDirectoryReadCommand));
            throw;
        }
        catch (Exception ex) when (ex is SessionEndedException or InvalidOperationException or ObjectDisposedException)
        {
            scope.CompleteError(
                PageStatusText.Failure(Localizer[L.RecipientsStatus.DirectoryNothingAdded], Localizer[L.WindowSession.Ended]),
                new PageStatusLog(L.RecipientsStatus.DirectoryNothingAdded, dns.Count));
            throw;
        }

        var added = AddDirectoryRows(read.Rows, out var withoutAddress);
        var status = Localizer.Format(L.CorrespondenceRecipients.FromDirectory, CountsPart(added, 0, withoutAddress, read.Other));
        foreach (var group in read.TruncatedGroups)
        {
            status += " " + Localizer.Format(L.CorrespondenceRecipients.GroupTruncated, group, MaxGroupMembers);
        }

        StatusText = string.Empty;
        StatusText = status;

        // A read that found nothing, or left members out of a group, says what was skipped; the others say what was added.
        var skipped = read.TruncatedGroups.Count > 0 || (FoundNothing(added, 0) && withoutAddress == 0 && read.Other == 0);
        var log = new PageStatusLog(L.CorrespondenceRecipients.FromDirectory, added.Added, added.Duplicates, withoutAddress, read.Other, read.TruncatedGroups.Count);
        if (skipped)
        {
            scope.CompleteAttention(status, log);
        }
        else
        {
            scope.CompleteDone(status, log);
        }
    }

    /// <summary>
    /// Adds directory rows: valid ones de-duplicated by address, objects without an address once per DN as invalid rows
    /// (status "brak adresu e-mail").
    /// </summary>
    private RecipientsAdded AddDirectoryRows(IReadOnlyList<(DirectoryRow Row, string Source)> rows, out int withoutAddress)
    {
        var known = Addresses();
        var invalidDns = new HashSet<string>(Items.Where(r => !r.IsValid && r.DistinguishedName is not null).Select(r => DnKey.For(r.DistinguishedName!)), StringComparer.Ordinal);
        var room = Math.Max(0, CorrespondenceLimits.MaxRecipients - Items.Count);
        var added = new List<CorrespondenceRecipientViewModel>();
        int duplicates = 0, overLimit = 0;
        withoutAddress = 0;
        foreach (var (row, source) in rows)
        {
            if (row.IsValid ? !known.Add(row.Email) : !invalidDns.Add(DnKey.For(row.DistinguishedName)))
            {
                duplicates++;
                continue;
            }

            if (added.Count >= room)
            {
                overLimit++;
                continue;
            }

            var item = new CorrespondenceRecipientViewModel(row.Email, row.Name, source, row.Fields, row.IsValid, row.DistinguishedName)
            {
                Origin = RecipientOrigin.Directory,
            };
            if (!row.IsValid)
            {
                withoutAddress++;
                item.SetStatus(RecipientStatusKind.Skipped, Localizer[L.CorrespondenceRecipients.NoAddress]);
            }

            added.Add(item);
        }

        Insert(added);
        return new RecipientsAdded(added.Count - withoutAddress, duplicates, overLimit);
    }

    /// <summary>Reads the objects by DN, expands groups and builds the rows (thread pool).</summary>
    private static async Task<DirectoryRead> ReadAsync(IDirectorySession session, DirectoryRowBuilder builder, IReadOnlyList<string> dns,
        string directorySource, Func<string, string> groupSource, int pageSize, CancellationToken token)
    {
        var attributes = builder.AttributesFor(DirectoryFieldTarget.Person);

        // objectSid: the RID of a group finds the accounts whose primary group it is.
        IReadOnlyList<string> read = [.. attributes.Append(Attr.ObjectSid).Distinct(StringComparer.OrdinalIgnoreCase)];
        var objects = await DirectoryLookup.ReadManyAsync(session, dns, read, token).ConfigureAwait(false);
        var rows = new List<(DirectoryRow, string)>();
        var truncated = new List<string>();
        var other = 0;
        foreach (var dn in dns.DistinctBy(DnKey.For, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (!objects.TryGetValue(DnKey.For(dn), out var obj))
            {
                other++;
                continue;
            }

            switch (obj.Kind)
            {
                case ObjectKind.User or ObjectKind.InetOrgPerson or ObjectKind.Contact:
                    rows.Add((builder.Recipient(obj), directorySource));
                    break;
                case ObjectKind.Group:
                    var name = DirectoryRowBuilder.NameOf(obj);
                    var source = groupSource(name);
                    var (members, more) = await MembersAsync(session, obj.DistinguishedName, AdConvert.GetRid(obj.ObjectSid), attributes, pageSize, token)
                        .ConfigureAwait(false);
                    rows.AddRange(members.Select(m => (builder.Recipient(m), source)));
                    if (more)
                    {
                        truncated.Add(name);
                    }

                    break;
                default:
                    other++;
                    break;
            }
        }

        return new DirectoryRead(rows, other, truncated);
    }

    /// <summary>
    /// The users and contacts of a group, at most <see cref="MaxGroupMembers"/>: nested membership
    /// (<c>LDAP_MATCHING_RULE_IN_CHAIN</c>) and the accounts whose primary group it is (<c>primaryGroupID</c> = the group's
    /// RID – the primary group is not in <c>memberOf</c>; every ordinary user of Domain Users is a member that way only).
    /// </summary>
    private static async Task<(List<DirectoryObject> Members, bool More)> MembersAsync(IDirectorySession session, string groupDn, uint? groupRid,
        IReadOnlyList<string> attributes, int pageSize, CancellationToken token)
    {
        var search = new DirectorySearch
        {
            BaseDn = session.Info.DefaultNamingContext,
            Scope = SearchScope.Subtree,
            Filter = LdapFilter.And(LdapFilter.Or(ObjectKind.User.ToLdapFilter(), ObjectKind.Contact.ToLdapFilter()),
                AccountReviewQuery.GroupMembersFilter(groupDn, groupRid)),
            Attributes = attributes,
            PageSize = pageSize,
            SizeLimit = MaxGroupMembers + 1,
        };
        var members = new List<DirectoryObject>();
        try
        {
            await foreach (var member in session.SearchAsync(search, token).ConfigureAwait(false))
            {
                if (members.Count == MaxGroupMembers)
                {
                    return (members, true);
                }

                members.Add(member);
            }
        }
        catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.SizeLimitExceeded)
        {
            return (members, true);
        }

        return (members, false);
    }

    /// <summary>
    /// Runs a directory action of a command: what the picker cannot do is said as an attention of the line (nothing was added
    /// and why), what the read says is said by the read, cancellation (session ended) is quiet.
    /// </summary>
    private async Task RunDirectoryAsync(Func<IDirectorySession, Task<IReadOnlyList<string>>> pick)
    {
        IReadOnlyList<string> dns;
        try
        {
            var session = Session();
            dns = await pick(session).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;   // The window's session ended while picking.
        }
        catch (DirectoryException ex)
        {
            NothingAdded(Services.Errors.Translate(ex), ResultCode.Of(ex));
            return;
        }
        catch (Exception ex) when (ex is SessionEndedException or InvalidOperationException or ObjectDisposedException)
        {
            NothingAdded(Localizer[L.WindowSession.Ended], ResultCode.None);
            return;
        }

        await ReadFromDirectoryAsync(dns).ConfigureAwait(true);
    }

    /// <summary>The read, whose failures the line said already (the exception is for the caller that records them).</summary>
    private async Task ReadFromDirectoryAsync(IReadOnlyList<string> dns)
    {
        try
        {
            await AddFromDirectoryAsync(dns).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is DirectoryException or SessionEndedException or InvalidOperationException or ObjectDisposedException)
        {
            // Said by the operation.
        }
    }

    private void NothingAdded(string reason, ResultCode code)
    {
        if (!Status.IsWorking)
        {
            Status.Show(PageStatus.Error(
                PageStatusText.Failure(Localizer[L.RecipientsStatus.DirectoryNothingAdded], reason, null, code),
                new PageStatusLog(L.RecipientsStatus.DirectoryNothingAdded).WithCode(code)));
        }
    }

    /// <summary>Reads the objects of the last read again (the action of its stopped or failed result).</summary>
    [RelayCommand]
    private Task RetryDirectoryRead() => ReadFromDirectoryAsync(_lastDirectoryRead);

    /// <summary>The objects selected in the connection window when this window was opened.</summary>
    [RelayCommand(CanExecute = nameof(CanAddFromSelection))]
    private Task AddFromSelectionAsync() => RunDirectoryAsync(_ => Task.FromResult(SelectedObjects));

    private bool CanAddFromSelection() => CanUseDirectory && SelectedObjects.Count > 0;

    /// <summary>The object picker (users and contacts).</summary>
    [RelayCommand(CanExecute = nameof(CanUseDirectory))]
    private Task PickUsersAsync() =>
        RunDirectoryAsync(session => Services.Dialogs.PickObjectsAsync(session, [ObjectKind.User, ObjectKind.Contact], multiSelect: true));

    /// <summary>A picked group's members (nested, users and contacts only).</summary>
    [RelayCommand(CanExecute = nameof(CanUseDirectory))]
    private Task AddGroupMembersAsync() =>
        RunDirectoryAsync(session => Services.Dialogs.PickObjectsAsync(session, [ObjectKind.Group], multiSelect: false));
}
