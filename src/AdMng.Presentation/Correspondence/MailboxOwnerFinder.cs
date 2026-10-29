using AdMng.Core.ActiveDirectory;
using AdMng.Core.Correspondence.Mailboxes;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.Lists;

namespace AdMng.Presentation.Correspondence;

/// <summary>Where the mailbox owner query reads mailboxes: a subtree, or the objects selected in the connection window.</summary>
/// <param name="BaseDn">The base of the search (the naming context for a selection).</param>
/// <param name="DistinguishedNames">The selected objects, or null for the whole subtree.</param>
internal sealed record MailboxSearchScope(string BaseDn, IReadOnlyList<string>? DistinguishedNames)
{
    public override string ToString() => $"{nameof(MailboxSearchScope)} ({DistinguishedNames?.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "subtree"})";
}

/// <summary>One mailbox read: the account (with the attributes of the mailbox fields), its entry and the owner's name.</summary>
internal sealed record MailboxRead(DirectoryObject Account, MailboxEntry Entry, OwnerName? Name)
{
    public override string ToString() => nameof(MailboxRead);
}

/// <summary>Why a read stopped before the end.</summary>
internal enum MailboxReadFailure
{
    None,

    /// <summary>More mailboxes than <see cref="MailboxOwnerFinder.MaxMailboxes"/>.</summary>
    TooManyMailboxes,

    /// <summary>More persons than <see cref="MailboxOwnerFinder.MaxPersons"/>.</summary>
    TooManyPersons,
}

/// <summary>
/// The directory part of the mailbox owner query (design note 6.4, decision D9): streams the mailbox accounts of a scope
/// (user accounts of the chosen <see cref="MailboxSelection"/>, the operator's filter ANDed, a selection read by DN in
/// chunks) and the persons of the person base (accounts with a given name, surname or display name), each with its limit,
/// on the caller's thread with cancellation and progress callbacks. Filters are built with <see cref="LdapFilter"/>
/// (rule 3); every read goes to the session it is given (rule 4). Matching itself is <see cref="MailboxOwnerMatcher"/>.
/// </summary>
internal sealed class MailboxOwnerFinder
{
    /// <summary>The most mailboxes of one query (20 000).</summary>
    public const int DefaultMaxMailboxes = 20_000;

    /// <summary>The most persons of one person base (300 000).</summary>
    public const int DefaultMaxPersons = 300_000;

    /// <summary>Objects between two progress callbacks.</summary>
    internal const int ProgressInterval = 250;

    /// <param name="session">The window's session.</param>
    /// <param name="pageSize">The page size of the searches.</param>
    public MailboxOwnerFinder(IDirectoryReader session, int pageSize)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        PageSize = Math.Clamp(pageSize, 1, 1000);
    }

    public IDirectoryReader Session { get; }

    public int PageSize { get; }

    /// <summary>The most mailboxes read (tests lower it).</summary>
    public int MaxMailboxes { get; init; } = DefaultMaxMailboxes;

    /// <summary>The most persons read (tests lower it).</summary>
    public int MaxPersons { get; init; } = DefaultMaxPersons;

    /// <summary>The persons filter: user accounts with a given name, surname or display name.</summary>
    public static string PersonFilter { get; } = LdapFilter.And(
        ObjectKind.User.ToLdapFilter(),
        LdapFilter.Or(LdapFilter.Present(Attr.GivenName), LdapFilter.Present(Attr.Sn), LdapFilter.Present(Attr.DisplayName)));

    /// <summary>The mailbox filter: user accounts, the type values of <paramref name="selection"/> and the operator's filter.</summary>
    public static string MailboxFilter(MailboxSelection selection, string? extraFilter) =>
        LdapFilter.And(ObjectKind.User.ToLdapFilter(), MailboxTypes.Filter(selection), string.IsNullOrWhiteSpace(extraFilter) ? null : extraFilter.Trim());

    /// <summary>Reads the mailboxes: their entries and owner names, in directory order.</summary>
    /// <param name="scope">Where.</param>
    /// <param name="filter">The <see cref="MailboxFilter"/>.</param>
    /// <param name="attributes">The attributes to read (entry, owner name, mailbox fields).</param>
    /// <param name="source">Where the owner's name is.</param>
    /// <param name="nameAttribute">The attribute of <see cref="OwnerNameSource.Attribute"/>.</param>
    /// <param name="progress">Called with the number read so far.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    public async Task<(IReadOnlyList<MailboxRead> Mailboxes, MailboxReadFailure Failure)> ReadMailboxesAsync(
        MailboxSearchScope scope, string filter, IReadOnlyList<string> attributes, OwnerNameSource source, string? nameAttribute,
        Func<int, Task> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(progress);
        var result = new List<MailboxRead>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var searches = scope.DistinguishedNames is { } dns
            ? dns.Where(dn => !string.IsNullOrWhiteSpace(dn)).DistinctBy(DnKey.For, StringComparer.Ordinal).Chunk(Infrastructure.DirectoryLookup.BatchSize)
                .Select(chunk => Search(scope.BaseDn, LdapFilter.And(filter, LdapFilter.Or(chunk.Select(dn => LdapFilter.Equal(Attr.DistinguishedName, dn)))), attributes, MaxMailboxes + 1))
            : [Search(scope.BaseDn, filter, attributes, MaxMailboxes + 1)];
        try
        {
            foreach (var search in searches)
            {
                await foreach (var account in Session.SearchAsync(search, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    if (!seen.Add(DnKey.For(account.DistinguishedName)))
                    {
                        continue;
                    }

                    if (result.Count == MaxMailboxes)
                    {
                        return (result, MailboxReadFailure.TooManyMailboxes);
                    }

                    result.Add(new MailboxRead(account, MailboxEntry.From(account), OwnerNameParser.Parse(account, source, nameAttribute)));
                    if (result.Count % ProgressInterval == 0)
                    {
                        await progress(result.Count).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.SizeLimitExceeded)
        {
            return (result, MailboxReadFailure.TooManyMailboxes);
        }

        await progress(result.Count).ConfigureAwait(false);
        return (result, MailboxReadFailure.None);
    }

    /// <summary>Reads the persons of <paramref name="baseDn"/> (subtree) as candidates.</summary>
    public async Task<(IReadOnlyList<PersonCandidate> Persons, MailboxReadFailure Failure)> ReadPersonsAsync(
        string baseDn, Func<int, Task> progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDn);
        ArgumentNullException.ThrowIfNull(progress);
        var persons = new List<PersonCandidate>();
        var search = Search(baseDn, PersonFilter, PersonCandidate.AttributesFor(Session.Schema), MaxPersons + 1);
        try
        {
            await foreach (var person in Session.SearchAsync(search, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (persons.Count == MaxPersons)
                {
                    return (persons, MailboxReadFailure.TooManyPersons);
                }

                persons.Add(PersonCandidate.From(person));
                if (persons.Count % ProgressInterval == 0)
                {
                    await progress(persons.Count).ConfigureAwait(false);
                }
            }
        }
        catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.SizeLimitExceeded)
        {
            return (persons, MailboxReadFailure.TooManyPersons);
        }

        await progress(persons.Count).ConfigureAwait(false);
        return (persons, MailboxReadFailure.None);
    }

    public override string ToString() => nameof(MailboxOwnerFinder);

    private DirectorySearch Search(string baseDn, string filter, IReadOnlyList<string> attributes, int sizeLimit) => new()
    {
        BaseDn = baseDn,
        Scope = SearchScope.Subtree,
        Filter = filter,
        Attributes = attributes,
        PageSize = PageSize,
        SizeLimit = sizeLimit,
    };
}
