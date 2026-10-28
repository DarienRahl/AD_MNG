using System.Text;
using AdMng.Core.Correspondence.Import;
using static AdMng.Core.Tests.Correspondence.Import.ImportTestFiles;

namespace AdMng.Core.Tests.Correspondence.Import;

/// <summary>Text files, size limits and the supported extensions (the packaged formats have their own test classes).</summary>
public class ContactFileReaderTests
{
    [Theory]
    [InlineData("kontakty.csv")]
    [InlineData("KONTAKTY.CSV")]
    [InlineData(@"C:\Users\jan\Pobrane\prawo-pl_kontakty_2026.xlsx")]
    [InlineData("/tmp/lista.Docx")]
    [InlineData("a.tsv")]
    [InlineData("a.txt")]
    [InlineData("a.xlsm")]
    [InlineData("a.xltx")]
    [InlineData("a.dotx")]
    [InlineData("a.docm")]
    [InlineData("a.odt")]
    [InlineData("a.ods")]
    [InlineData("a.rtf")]
    [InlineData("a.htm")]
    [InlineData("a.html")]
    public void IsSupported_TheListedExtensions(string fileName) => Assert.True(ContactFileReader.IsSupported(fileName));

    [Theory]
    [InlineData("a.pdf")]
    [InlineData("a.xls")]
    [InlineData("a.doc")]
    [InlineData("csv")]
    [InlineData("katalog.csv/plik")]
    [InlineData("")]
    [InlineData("  ")]
    public void IsSupported_OtherExtensions(string fileName) => Assert.False(ContactFileReader.IsSupported(fileName));

    [Fact]
    public void Extensions_AreLowerCaseWithADot() =>
        Assert.All(ContactFileReader.Extensions, e => Assert.Matches("^\\.[a-z]+$", e));

    [Fact]
    public void Read_RefusesFilesAbove50MiB()
    {
        var content = new byte[ContactFileReader.MaxFileBytes + 1];
        Assert.Throws<InvalidDataException>(() => ContactFileReader.Read("duzy.csv", content, 1250));
    }

    [Fact]
    public void Read_CsvInWindows1250()
    {
        var bytes = Cp1250.GetBytes("Nazwa;E-mail;Miejscowość\r\nUrząd Miasta Łodzi;um@lodz.pl;Łódź\r\nGmina Źródła;gmina@zrodla.pl;Źródła\r\n");
        var result = ContactFileReader.Read("kontakty.csv", bytes, 1250);
        Assert.Equal(ContactSourceFormat.Delimited, result.Format);
        Assert.Equal(["Nazwa", "E-mail", "Miejscowość"], result.Columns);
        Assert.Equal(["Urząd Miasta Łodzi", "Gmina Źródła"], result.Contacts.Select(c => c.Name));
        Assert.Equal("Łódź", result.Contacts[0].Fields["Miejscowość"]);
    }

    [Fact]
    public void Read_CsvInUtf8WithBom()
    {
        byte[] bytes = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("E-mail;Nazwa\nżaneta@firma.pl;x\nzaneta@firma.pl;Żaneta Wójcik\n")];
        var contact = Assert.Single(ContactFileReader.Read("k.csv", bytes, 1252).Contacts);
        Assert.Equal(("zaneta@firma.pl", "Żaneta Wójcik"), (contact.Email, contact.Name));
    }

    [Fact]
    public void Read_TxtFreeText()
    {
        var result = ContactFileReader.Read("lista.txt", Encoding.UTF8.GetBytes("Szanowni Państwo,\nproszę o kontakt: Jan Żółtowski <jan@urzad.pl>, a.nowak@urzad.pl"), 1250);
        Assert.Equal(ContactSourceFormat.Text, result.Format);
        Assert.Equal(["jan@urzad.pl", "a.nowak@urzad.pl"], result.Contacts.Select(c => c.Email));
        Assert.Equal("Jan Żółtowski", result.Contacts[0].Name);
    }

    [Fact]
    public void Read_TsvTable() =>
        Assert.Equal("Sołectwo Łęg", Assert.Single(ContactFileReader.Read("a.tsv", Encoding.UTF8.GetBytes("Nazwa\tE-mail\nSołectwo Łęg\tsoltys@leg.pl\n"), 1250).Contacts).Name);

    [Fact]
    public void Read_UnknownExtension_IsText() =>
        Assert.Single(ContactFileReader.Read("adresy.lst", Encoding.UTF8.GetBytes("jan@urzad.pl"), 1250).Contacts);

    [Fact]
    public void Read_EmptyFile() => Assert.Empty(ContactFileReader.Read("pusty.csv", [], 1250).Contacts);

    [Theory]
    [InlineData("a.xlsx")]
    [InlineData("a.docx")]
    [InlineData("a.odt")]
    [InlineData("a.ods")]
    public void Read_NotAZipPackage_IsInvalidData(string fileName) =>
        Assert.Throws<InvalidDataException>(() => ContactFileReader.Read(fileName, Encoding.ASCII.GetBytes("to nie jest plik zip"), 1250));
}
