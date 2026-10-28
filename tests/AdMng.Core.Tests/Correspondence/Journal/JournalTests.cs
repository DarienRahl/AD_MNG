using System.Security.Cryptography;
using System.Text;
using AdMng.Core.Correspondence.Journal;

namespace AdMng.Core.Tests.Correspondence.Journal;

public class JournalHashingTests
{
    private static readonly byte[] Key = [.. Enumerable.Range(1, JournalHashing.KeyBytes).Select(i => (byte)i)];

    [Fact]
    public void Hash_IsTheTruncatedHmacOfTheNormalizedAddress_Base64Url()
    {
        var mac = HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes("jan.kowalski@urzad.pl"));
        var expected = Convert.ToBase64String(mac, 0, 16).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, JournalHashing.Hash(Key, "jan.kowalski@urzad.pl"));
        Assert.Equal(22, expected.Length);
    }

    [Fact]
    public void Hash_IsDeterministic_AndIgnoresCaseAndDecorations()
    {
        var hash = JournalHashing.Hash(Key, "jan.kowalski@urzad.pl");
        Assert.Equal(hash, JournalHashing.Hash(Key, "jan.kowalski@urzad.pl"));
        Assert.Equal(hash, JournalHashing.Hash(Key, "  MAILTO:Jan.Kowalski@URZAD.pl?subject=x "));
        Assert.Equal(hash, JournalHashing.Hash(Key, "<jan.kowalski@urzad.pl>"));
        Assert.True(JournalHashing.IsHash(hash));
    }

    [Fact]
    public void Hash_DependsOnTheKeyAndTheAddress()
    {
        var other = (byte[])Key.Clone();
        other[0] ^= 0xFF;
        var hash = JournalHashing.Hash(Key, "jan.kowalski@urzad.pl");
        Assert.NotEqual(hash, JournalHashing.Hash(other, "jan.kowalski@urzad.pl"));
        Assert.NotEqual(hash, JournalHashing.Hash(Key, "jan.kowalsky@urzad.pl"));
    }

    [Fact]
    public void Hash_NeverContainsTheAddress()
    {
        var hash = JournalHashing.Hash(Key, "żaneta@firma.pl");
        Assert.DoesNotContain("@", hash, StringComparison.Ordinal);
        Assert.DoesNotContain("firma", hash, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    public void Hash_RefusesKeysOfAnotherLength(int length) =>
        Assert.Throws<ArgumentException>(() => JournalHashing.Hash(new byte[length], "a@b.pl"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("<>")]
    public void Hash_RefusesBlankAddresses(string? address) =>
        Assert.Throws<ArgumentException>(() => JournalHashing.Hash(Key, address!));

    [Theory]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAA", true)]
    [InlineData("abc-_0123456789ABCDEFGH", false)]
    [InlineData("abc-_0123456789ABCDEFG", true)]
    [InlineData("abc+/0123456789ABCDEFG", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsHash(string? value, bool expected) => Assert.Equal(expected, JournalHashing.IsHash(value));
}

public class JournalFileTests
{
    private static readonly byte[] Key = [.. Enumerable.Range(100, JournalHashing.KeyBytes).Select(i => (byte)i)];

    private static string HashOf(string address) => JournalHashing.Hash(Key, address);

    [Fact]
    public void Line_HashUtcTimeSubject()
    {
        var entry = new JournalEntry(HashOf("a@b.pl"), new DateTimeOffset(2026, 10, 28, 9, 15, 30, TimeSpan.FromHours(1)), "Zaproszenie na sesję");
        Assert.Equal(HashOf("a@b.pl") + "\t2026-10-28T08:15:30Z\tZaproszenie na sesję", JournalFile.Line(entry));
    }

    [Fact]
    public void Line_SubjectControlCharactersBecomeSpaces_LongSubjectsAreCut()
    {
        var entry = new JournalEntry(HashOf("a@b.pl"), DateTimeOffset.UnixEpoch, "Temat\tz\r\nłamaniem\u2028linii");
        Assert.EndsWith("\tTemat z  łamaniem linii", JournalFile.Line(entry), StringComparison.Ordinal);
        var longSubject = new string('ż', 150) + new string('x', 100);
        var line = JournalFile.Line(entry with { Subject = longSubject });
        Assert.Equal(JournalFile.MaxSubjectLength, line.Split('\t')[2].Length);
    }

    [Fact]
    public void Line_NeverCutsASurrogatePair()
    {
        var subject = new string('a', JournalFile.MaxSubjectLength - 1) + "😀";
        var line = JournalFile.Line(new JournalEntry(HashOf("a@b.pl"), DateTimeOffset.UnixEpoch, subject));
        Assert.Equal(new string('a', JournalFile.MaxSubjectLength - 1), line.Split('\t')[2]);
    }

    [Fact]
    public void Line_RefusesAnInvalidHash()
    {
        Assert.Throws<ArgumentException>(() => JournalFile.Line(new JournalEntry("a@b.pl", DateTimeOffset.UnixEpoch, "x")));
        Assert.Throws<ArgumentException>(() => JournalFile.Line(new JournalEntry("zly\thash", DateTimeOffset.UnixEpoch, "x")));
    }

    [Fact]
    public void RoundTrip()
    {
        var entries = new[]
        {
            new JournalEntry(HashOf("jan@urzad.pl"), new DateTimeOffset(2026, 10, 28, 8, 0, 0, TimeSpan.Zero), "Pismo w sprawie {{Miejscowość}}"),
            new JournalEntry(HashOf("anna@urzad.pl"), new DateTimeOffset(2026, 10, 28, 8, 0, 3, TimeSpan.Zero), string.Empty),
            new JournalEntry(HashOf("ewa@urzad.pl"), new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.FromHours(-5)), "Życzenia ✓"),
        };
        var text = JournalFile.Header + "\n" + string.Join("\n", entries.Select(JournalFile.Line)) + "\n";
        var read = JournalFile.Read(new StringReader(text));
        Assert.Equal(entries.Select(e => (e.AddressHash, e.SentAt.ToUniversalTime(), e.Subject)), read.Select(e => (e.AddressHash, e.SentAt, e.Subject)));
        Assert.All(read, e => Assert.Equal(TimeSpan.Zero, e.SentAt.Offset));
    }

    [Fact]
    public void Read_SkipsMalformedLines()
    {
        var good = HashOf("jan@urzad.pl");
        var text = string.Join("\r\n",
            JournalFile.Header,
            good + "\t2026-10-28T08:00:00Z\tOK 1",
            string.Empty,
            "śmieci",
            good + "\t2026-10-28T08:00:00Z",                       // no subject column
            good + "\t28.10.2026 08:00\tzła data",
            good + "\t2026-10-28T08:00:00\tbez Z",
            "jan@urzad.pl\t2026-10-28T08:00:00Z\tadres zamiast skrótu",
            good + "x\t2026-10-28T08:00:00Z\tza długi skrót",
            good + "\t2026-13-01T00:00:00Z\tzły miesiąc",
            good + "\t2026-10-28T09:00:00Z\tOK 2\tz tabulatorem",
            good + "\t2026-10-28T10:00:00Z\t" + new string('x', 5000),
            good + "\t2026-10-28T11:00:00Z\t");
        var read = JournalFile.Read(new StringReader(text));
        Assert.Equal(["OK 1", "OK 2 z tabulatorem", string.Empty], read.Select(e => e.Subject));
    }

    [Fact]
    public void Read_WithoutHeader_ReadsTheLines()
    {
        var line = JournalFile.Line(new JournalEntry(HashOf("a@b.pl"), DateTimeOffset.UnixEpoch, "x"));
        Assert.Single(JournalFile.Read(new StringReader(line + "\n")));
    }

    [Fact]
    public void Read_BomBeforeTheHeader()
    {
        var line = JournalFile.Line(new JournalEntry(HashOf("a@b.pl"), DateTimeOffset.UnixEpoch, "x"));
        Assert.Single(JournalFile.Read(new StringReader("\uFEFF" + JournalFile.Header + "\n" + line)));
    }

    [Fact]
    public void Read_AnotherVersion_IsNotRead()
    {
        var line = JournalFile.Line(new JournalEntry(HashOf("a@b.pl"), DateTimeOffset.UnixEpoch, "x"));
        Assert.Empty(JournalFile.Read(new StringReader("AdMng.CorrespondenceJournal\t2\n" + line)));
    }

    [Fact]
    public void Read_Empty() => Assert.Empty(JournalFile.Read(new StringReader(string.Empty)));

    [Fact]
    public void Entry_ToStringNeverShowsTheSubject() =>
        Assert.Equal(nameof(JournalEntry), new JournalEntry(HashOf("a@b.pl"), DateTimeOffset.UnixEpoch, "Poufny temat").ToString());
}
