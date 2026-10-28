namespace AdMng.Core.Correspondence.Journal;

/// <summary>
/// One line of the Korespondencja journal "już wysłane" (decision D4): the keyed hash of an address, when it was sent and
/// the subject as typed (fields not filled in). Never the address.
/// </summary>
public sealed record JournalEntry(string AddressHash, DateTimeOffset SentAt, string Subject)
{
    public override string ToString() => nameof(JournalEntry);
}

/// <summary>The keyed hashes of the journal.</summary>
public static class JournalHashing
{
    /// <summary>Length of the per-user key.</summary>
    public const int KeyBytes = 32;

    /// <summary>Base64url of the first 16 bytes of HMAC-SHA-256(key, UTF-8(EmailAddressText.Normalize(address))).</summary>
    public static string Hash(ReadOnlySpan<byte> key, string address) => throw new NotImplementedException("U5 T1 core");
}

/// <summary>The journal file: a header line, then one line per sent address.</summary>
public static class JournalFile
{
    public const string Header = "AdMng.CorrespondenceJournal\t1";

    /// <summary>Longest subject kept in a line.</summary>
    public const int MaxSubjectLength = 200;

    /// <summary>The entries of a journal file; malformed lines are skipped.</summary>
    public static IReadOnlyList<JournalEntry> Read(TextReader reader) => throw new NotImplementedException("U5 T1 core");

    /// <summary><c>hash \t yyyy-MM-ddTHH:mm:ssZ \t subject</c> (tabs and newlines of the subject become spaces).</summary>
    public static string Line(JournalEntry entry) => throw new NotImplementedException("U5 T1 core");
}
