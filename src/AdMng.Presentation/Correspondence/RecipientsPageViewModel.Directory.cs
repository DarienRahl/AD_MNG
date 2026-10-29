using AdMng.Core.ActiveDirectory;
using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
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
/// contacts, the members of a group (nested, users and contacts, at most <see cref="MaxGroupMembers"/>). Every object is
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
    /// Adds the objects (users, contacts, inetOrgPersons; groups: their members) as AD rows, de-duplicated by address.
    /// Directory errors and a missing session propagate (the caller reports them); a read cancelled because the session
    /// ended adds nothing.
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

        var session = Session();
        var builder = new DirectoryRowBuilder(Correspondence.Fields, _directoryFields.Definitions, DirectoryRowBuilder.ContextFor(Services, session));
        var token = _directoryReads.Token;
        var directorySource = Localizer[L.CorrespondenceRecipients.SourceDirectory];
        var localizer = Localizer;
        Func<string, string> groupSource = group => localizer.Format(L.CorrespondenceRecipients.SourceGroup, group);
        var pageSize = Math.Clamp(Services.Settings.Current.PageSize, 1, 1000);
        DirectoryRead read;
        try
        {
            read = await Task.Run(() => ReadAsync(session, builder, dns, directorySource, groupSource, pageSize, token), token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;   // the window's session ended: the AD actions are disabled
        }

        var added = AddDirectoryRows(read.Rows, out var withoutAddress);
        var status = Localizer.Format(L.CorrespondenceRecipients.FromDirectory, CountsPart(added, 0, withoutAddress, read.Other));
        foreach (var group in read.TruncatedGroups)
        {
            status += " " + Localizer.Format(L.CorrespondenceRecipients.GroupTruncated, group, MaxGroupMembers);
        }

        ReportStatus(status);
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
        var objects = await DirectoryLookup.ReadManyAsync(session, dns, attributes, token).ConfigureAwait(false);
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
                    var (members, more) = await MembersAsync(session, obj.DistinguishedName, attributes, pageSize, token).ConfigureAwait(false);
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

    /// <summary>The users and contacts of a group, nested membership included (<c>LDAP_MATCHING_RULE_IN_CHAIN</c>), at most <see cref="MaxGroupMembers"/>.</summary>
    private static async Task<(List<DirectoryObject> Members, bool More)> MembersAsync(IDirectorySession session, string groupDn,
        IReadOnlyList<string> attributes, int pageSize, CancellationToken token)
    {
        var search = new DirectorySearch
        {
            BaseDn = session.Info.DefaultNamingContext,
            Scope = SearchScope.Subtree,
            Filter = LdapFilter.And(LdapFilter.Or(ObjectKind.User.ToLdapFilter(), ObjectKind.Contact.ToLdapFilter()), LdapFilter.InChain(Attr.MemberOf, groupDn)),
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

    /// <summary>Runs a directory action of a command: errors are shown, cancellation (session ended) is quiet.</summary>
    private async Task RunDirectoryAsync(Func<IDirectorySession, Task<IReadOnlyList<string>>> pick)
    {
        var title = Localizer[L.CorrespondenceRecipients.DirectoryTitle];
        try
        {
            var session = Session();
            var dns = await pick(session).ConfigureAwait(true);
            await AddFromDirectoryAsync(dns).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The window's session ended while reading.
        }
        catch (DirectoryException ex)
        {
            await Services.Dialogs.ShowErrorAsync(title, Services.Errors.Translate(ex)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is SessionEndedException or InvalidOperationException or ObjectDisposedException)
        {
            await Services.Dialogs.ShowErrorAsync(title, Localizer[L.WindowSession.Ended]).ConfigureAwait(true);
        }
    }

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
