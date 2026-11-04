using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;

namespace AdMng.Core.Tests.GroupPolicy.AdministrativeTemplates;

public class RegistryPolicyDocumentTests
{
    private const string Au = @"Software\Policies\Contoso\AU";

    [Fact]
    public void File_RoundTripsEveryEntryByteForByte()
    {
        var entries = new[]
        {
            Dword(Au, "NoAutoUpdate", 0),
            new RegistryPolEntry(Au, "**del.AUOptions", RegistryFormat.RegSz, RegistryPolEntry.DeletionData),
            new RegistryPolEntry(@"Software\Policies\Contoso\Ścieżki", "Ścieżka", RegistryFormat.RegExpandSz, RegistryPolFile.StringData(@"%SystemRoot%\Łódź")),
            new RegistryPolEntry(Au, "Lines", RegistryFormat.RegMultiSz, RegistryPolFile.MultiStringData(["a", "b"])),
            new RegistryPolEntry(Au, "Big", RegistryFormat.RegQword, RegistryPolFile.QwordData(10_000_000_000)),
        };

        var bytes = RegistryPolFile.Write(entries);
        var read = RegistryPolFile.Read(bytes);

        Assert.Equal(entries.Select(e => e.ToString()), read.Select(e => e.ToString()));
        Assert.True(entries.Zip(read).All(p => p.First.Key == p.Second.Key && p.First.ValueName == p.Second.ValueName && p.First.SameData(p.Second)));
        Assert.Equal(bytes, RegistryPolFile.Write(read));
        Assert.Equal([0x50, 0x52, 0x65, 0x67, 1, 0, 0, 0], RegistryPolFile.Empty);
        Assert.Equal(["a", "b"], RegistryPolFile.ReadMultiString(read[3].Data));
    }

    [Fact]
    public void File_WritesTheDocumentedLayout()
    {
        var bytes = RegistryPolFile.Write([Dword("K", "V", 1)]);

        // PReg 1 [ K\0 ; V\0 ; 4 ; 4 ; 01000000 ]
        Assert.Equal(Convert.FromHexString("50526567010000005B004B0000003B00560000003B00040000003B00040000003B00010000005D00"), bytes);
    }

    [Theory]
    [InlineData("50526567")]
    [InlineData("5052656702000000")]
    [InlineData("50526567010000005B004B00")]
    [InlineData("50526567010000005B004B0000003B00560000003B00040000003B00FF0000003B00010000005D00")]
    public void File_RefusesAMalformedFile(string hex) =>
        Assert.Throws<GpoParseException>(() => RegistryPolFile.Read(Convert.FromHexString(hex)));

    [Fact]
    public void Untouched_DocumentKeepsItsBytes_EvenUnsortedAndWithDuplicates()
    {
        var original = RegistryPolFile.Write([Dword("Z", "a", 1), Dword("A", "b", 2), Dword("Z", "a", 3)]);

        Assert.Equal(original, RegistryPolicyDocument.Parse(original).ToBytes());
        Assert.Equal(3u, Number(RegistryPolicyDocument.Parse(original).View.Get("Z", "a")));
    }

    [Fact]
    public void Set_KeepsTheValuesPlace_RemovesLaterDuplicates_KeepsTheStoredSpelling()
    {
        var document = new RegistryPolicyDocument([Dword(Au, "First", 1), Dword(Au, "Second", 2), Dword(@"Software\Policies\Contoso\AU\", "FIRST", 9)]);

        document.Set(Au.ToUpperInvariant(), "first", RegistryFormat.RegDword, RegistryPolFile.DwordData(5));

        Assert.Equal(["First=5", "Second=2"], Describe(document));
        Assert.Equal(Au, document.Entries[0].Key);
    }

    [Fact]
    public void Set_AddsANewValueAfterItsKey_AndANewKeyWhereItSorts()
    {
        var document = new RegistryPolicyDocument([Dword(@"Software\A", "x", 1), Dword(@"Software\A\B", "y", 1), Dword(@"Software\C", "z", 1)]);

        document.Set(@"Software\A", "x2", RegistryFormat.RegDword, RegistryPolFile.DwordData(2));
        document.Set(@"Software\A.B", "w", RegistryFormat.RegDword, RegistryPolFile.DwordData(3));
        document.Set(@"Software\B", "v", RegistryFormat.RegDword, RegistryPolFile.DwordData(4));
        document.Set(@"Software\D", "u", RegistryFormat.RegDword, RegistryPolFile.DwordData(5));

        // Registry order: a key's subkeys come right after it ("A", "A\B", then "A.B").
        Assert.Equal([@"Software\A", @"Software\A", @"Software\A\B", @"Software\A.B", @"Software\B", @"Software\C", @"Software\D"],
            document.Entries.Select(e => e.Key));
        Assert.Equal("x2", document.Entries[1].ValueName);
    }

    [Fact]
    public void MarkDeleted_TakesTheValuesPlace_AndSetTakesTheDeletionsPlace()
    {
        var document = new RegistryPolicyDocument([Dword(Au, "A", 1), Dword(Au, "B", 2)]);

        document.MarkDeleted(Au, "A");
        Assert.Equal(["**del.A", "B=2"], Describe(document));
        Assert.True(document.View.Get(Au, "A").IsDeleted);
        Assert.Equal(RegistryPolEntry.DeletionData, document.Entries[0].Data);
        Assert.Equal(RegistryFormat.RegSz, document.Entries[0].Type);

        document.Set(Au, "A", RegistryFormat.RegDword, RegistryPolFile.DwordData(7));
        Assert.Equal(["A=7", "B=2"], Describe(document));

        document.MarkDeleted(Au, "C");
        Assert.Equal(["A=7", "B=2", "**del.C"], Describe(document));
    }

    [Fact]
    public void Forget_RemovesEveryEntryAboutAValue()
    {
        var document = new RegistryPolicyDocument([Dword(Au, "A", 1), Dword(Au, "**soft.A", 2), new RegistryPolEntry(Au, "**del.A", 1, RegistryPolEntry.DeletionData), Dword(Au, "B", 3)]);

        document.Forget(Au, "a");

        Assert.Equal(["B=3"], Describe(document));
        Assert.Equal(RegistryValueStateKind.Missing, document.View.Get(Au, "A").Kind);
    }

    [Fact]
    public void ClearValues_LeadsTheKey_ForgetKeyKeepsNamedValues()
    {
        var document = new RegistryPolicyDocument([Dword(Au, "Own", 1), Dword(Au, "1", 2)]);

        document.ClearValues(Au);
        Assert.Equal(["**delvals.", "Own=1", "1=2"], Describe(document));

        document.ForgetKey(Au, ["Own"]);
        Assert.Equal(["Own=1"], Describe(document));
    }

    [Fact]
    public void View_AppliesTheEntriesInOrder()
    {
        var document = new RegistryPolicyDocument(
        [
            Dword(Au, "A", 1),
            Dword(Au, "B", 2),
            new RegistryPolEntry(Au, "**delvals.", RegistryFormat.RegSz, RegistryPolEntry.DeletionData),
            Dword(Au, "C", 3),
            Dword(Au, "**soft.D", 4),
            new RegistryPolEntry(Au, "**DeleteValues", RegistryFormat.RegSz, RegistryPolFile.StringData("C; X")),
            Dword(Au + @"\Sub", "E", 5),
            new RegistryPolEntry(Au, "**DeleteKeys", RegistryFormat.RegSz, RegistryPolFile.StringData("Sub")),
            Dword(Au, "C", 6),
        ]);

        var view = document.View;

        Assert.True(view.Get(Au, "A").IsDeleted);
        Assert.True(view.Get(Au, "X").IsDeleted);
        Assert.Equal(6u, Number(view.Get(Au, "C")));
        Assert.Equal(4u, Number(view.Get(Au, "D")));
        Assert.True(view.Get(Au + @"\Sub", "E").IsDeleted);
        Assert.True(view.ClearsValues(Au));
        Assert.Equal(["**soft.D", "C"], view.ValuesOf(Au).Select(e => e.ValueName));
    }

    [Fact]
    public void CompareKeys_IsPerSegmentWithoutCase()
    {
        Assert.True(RegistryPolicyDocument.CompareKeys(@"Software\a\b", @"Software\A.b") < 0);
        Assert.Equal(0, RegistryPolicyDocument.CompareKeys(@"\Software\X\", @"software\x"));
        Assert.True(RegistryPolicyDocument.CompareKeys(@"Software\X", @"Software\X\Y") < 0);
    }

    internal static RegistryPolEntry Dword(string key, string name, uint value) => new(key, name, RegistryFormat.RegDword, RegistryPolFile.DwordData(value));

    internal static uint? Number(RegistryValueState state) =>
        state.Entry is { Type: RegistryFormat.RegDword } entry ? BitConverter.ToUInt32(entry.Data) : null;

    /// <summary>"Name=number" for DWORDs, "Name" otherwise.</summary>
    internal static List<string> Describe(RegistryPolicyDocument document) =>
        [.. document.Entries.Select(e => e.Type == RegistryFormat.RegDword ? e.ValueName + "=" + BitConverter.ToUInt32(e.Data) : e.ValueName)];
}
