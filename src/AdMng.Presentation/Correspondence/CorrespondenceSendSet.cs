using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Correspondence.Import;

namespace AdMng.Presentation.Correspondence;

/// <summary>One message of a run: the unit it is rendered with and the recipient rows it covers.</summary>
/// <param name="Unit">One row, or every row of one address (grouping).</param>
/// <param name="Rows">The rows of the unit, in list order.</param>
internal sealed record SendUnit(CorrespondenceUnit Unit, IReadOnlyList<CorrespondenceRecipientViewModel> Rows)
{
    /// <summary>The mailbox keys of the rows (mailbox owner queries), distinct, in order.</summary>
    public IReadOnlyList<string> Keys =>
        [.. Rows.SelectMany(r => r.MailboxKeys).Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>"Name &lt;email&gt;", or the address alone (the preview's recipient choice).</summary>
    public string Text => string.IsNullOrWhiteSpace(Unit.Name) ? Unit.Email : $"{Unit.Name} <{Unit.Email}>";

    public override string ToString() => $"{nameof(SendUnit)} ({Rows.Count} rows)";
}

/// <summary>One BCC message of a run: at most the chunk size of distinct addresses and every row of them.</summary>
internal sealed record BccChunk(IReadOnlyList<string> Addresses, IReadOnlyList<CorrespondenceRecipientViewModel> Rows)
{
    public override string ToString() => $"{nameof(BccChunk)} ({Addresses.Count} addresses)";
}

/// <summary>
/// The send list of the recipients page at one moment, taken on the UI thread: the rows that are included, valid, not on the
/// "Nie wysyłać" list and – when "skip already sent" is on – not in the journal (the rule of
/// <see cref="RecipientsPageViewModel.SendList"/>, design note 6.4, applied to the rows' own state), the counts of the
/// included rows that are skipped or invalid (MailerWPiA's <c>SendList(out skipped, out invalid)</c>) and the columns of the
/// list. The rows are view models; their address, name and fields never change, so units and chunks can be built and
/// rendered off the UI thread. In memory only (rule 1).
/// </summary>
internal sealed class CorrespondenceSendSet
{
    private CorrespondenceSendSet(IReadOnlyList<CorrespondenceRecipientViewModel> rows, int skipped, int invalid, IReadOnlyCollection<string> knownColumns)
    {
        Rows = rows;
        Skipped = skipped;
        Invalid = invalid;
        KnownColumns = knownColumns;
    }

    /// <summary>The unit of a BCC message: no address, no name, no rows (the fields of a row are never used).</summary>
    public static CorrespondenceUnit BccUnit { get; } = new(string.Empty, string.Empty, []);

    /// <summary>The rows to send, in list order.</summary>
    public IReadOnlyList<CorrespondenceRecipientViewModel> Rows { get; }

    /// <summary>Included rows left out by the "Nie wysyłać" list or the journal.</summary>
    public int Skipped { get; }

    /// <summary>Included rows without a valid address.</summary>
    public int Invalid { get; }

    /// <summary>Every column of the recipient list (a column empty in a row renders "").</summary>
    public IReadOnlyCollection<string> KnownColumns { get; }

    /// <summary>The number of distinct addresses of <see cref="Rows"/>.</summary>
    public int AddressCount => Rows.Select(r => Key(r.Email)).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).Count();

    /// <summary>The send list of <paramref name="recipients"/> now (call on the UI thread).</summary>
    public static CorrespondenceSendSet Of(RecipientsPageViewModel recipients)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        var skipAlreadySent = recipients.SkipAlreadySent;
        var rows = new List<CorrespondenceRecipientViewModel>();
        int skipped = 0, invalid = 0;
        foreach (var row in recipients.Items)
        {
            if (!row.Include)
            {
                continue;
            }

            if (!row.IsValid || Key(row.Email).Length == 0)
            {
                invalid++;
            }
            else if (row.IsExcluded || (skipAlreadySent && row.IsAlreadySent))
            {
                skipped++;
            }
            else
            {
                rows.Add(row);
            }
        }

        return new CorrespondenceSendSet(rows, skipped, invalid, [.. recipients.KnownColumns]);
    }

    /// <summary>
    /// The messages of the individual mode: one per row, or – with <paramref name="groupByAddress"/> – one per address with
    /// all its rows (<see cref="RecipientGrouping"/>: first-appearance order, the first non-empty name).
    /// </summary>
    public IReadOnlyList<SendUnit> Units(bool groupByAddress) => UnitsOf(Rows, groupByAddress);

    /// <summary><see cref="Units"/> of any rows (a retry of the failed ones).</summary>
    public static IReadOnlyList<SendUnit> UnitsOf(IReadOnlyList<CorrespondenceRecipientViewModel> rows, bool groupByAddress)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var groups = new List<List<CorrespondenceRecipientViewModel>>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var key = groupByAddress ? Key(row.Email) : string.Empty;
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

        return [.. groups.Select(group => new SendUnit(
            RecipientGrouping.Group(group.Select(r => (r.Email, r.Name, r.Fields)), byAddress: true)[0],
            group))];
    }

    /// <summary>The messages of the BCC mode: the distinct addresses in list order, at most <paramref name="size"/> each.</summary>
    public IReadOnlyList<BccChunk> BccChunks(int size) => ChunksOf(Rows, size);

    /// <summary><see cref="BccChunks"/> of any rows (a retry of the failed ones).</summary>
    public static IReadOnlyList<BccChunk> ChunksOf(IReadOnlyList<CorrespondenceRecipientViewModel> rows, int size)
    {
        ArgumentNullException.ThrowIfNull(rows);
        size = Math.Clamp(size, 1, CorrespondenceLimits.MaxBccPerMessage);
        var byAddress = new Dictionary<string, List<CorrespondenceRecipientViewModel>>(StringComparer.Ordinal);
        var addresses = new List<(string Key, string Address)>();
        foreach (var row in rows)
        {
            var key = Key(row.Email);
            if (key.Length == 0)
            {
                continue;
            }

            if (!byAddress.TryGetValue(key, out var list))
            {
                byAddress[key] = list = [];
                addresses.Add((key, row.Email.Trim()));
            }

            list.Add(row);
        }

        return [.. addresses.Chunk(size).Select(chunk => new BccChunk(
            [.. chunk.Select(a => a.Address)],
            [.. chunk.SelectMany(a => byAddress[a.Key])]))];
    }

    /// <summary>The normalized address of a row ("" for none).</summary>
    public static string Key(string? email) => EmailAddressText.Normalize(email);

    public override string ToString() => $"{nameof(CorrespondenceSendSet)} ({Rows.Count} rows)";
}
