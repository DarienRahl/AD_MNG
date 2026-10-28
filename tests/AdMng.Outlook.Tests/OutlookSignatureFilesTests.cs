namespace AdMng.Outlook.Tests;

/// <summary>
/// Outlook signature files (design note 4.1): listing, the path safety of the image references and the size limit. Parsing
/// itself is Core's OutlookSignatureParser (tested in AdMng.Core.Tests); here ReadAsync is exercised up to the parser.
/// </summary>
public sealed class OutlookSignatureFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "admng-signature-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _folder;

    public OutlookSignatureFilesTests()
    {
        _folder = Path.Combine(_root, "Signatures");
        Directory.CreateDirectory(_folder);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void List_NamesTheHtmFiles_OncePerSignature_InOrdinalOrder()
    {
        Write("Jan Kowalski – podpis.htm");
        Write("Jan Kowalski – podpis.txt");
        Write("Jan Kowalski – podpis.rtf");
        Write("Krótki.HTM");
        Write("Krótki.html");
        Write("Angielski.html");
        Write("Łódź.htm");
        Directory.CreateDirectory(Path.Combine(_folder, "Jan Kowalski – podpis_files"));
        Write(Path.Combine("Jan Kowalski – podpis_files", "image001.htm"));
        Directory.CreateDirectory(Path.Combine(_folder, "Folder.htm"));

        var names = new OutlookSignatureFiles(_folder).List();

        Assert.Equal(["Angielski", "Jan Kowalski – podpis", "Krótki", "Łódź"], names);
    }

    [Fact]
    public void List_WithoutTheFolder_IsEmpty()
    {
        Assert.Empty(new OutlookSignatureFiles(Path.Combine(_root, "brak")).List());
    }

    [Fact]
    public void Folder_DefaultsToAppDataOnWindows_AndToNothingElsewhere()
    {
        var files = new OutlookSignatureFiles();

        if (OperatingSystem.IsWindows())
        {
            Assert.EndsWith(Path.Combine("Microsoft", "Signatures"), files.Folder, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Null(files.Folder);
            Assert.Empty(files.List());
        }

        Assert.Equal(_folder, new OutlookSignatureFiles(_folder).Folder);
        Assert.Equal(nameof(OutlookSignatureFiles), files.ToString());
    }

    [Theory]
    [InlineData("Nieistniejący")]
    [InlineData("..")]
    [InlineData("../Signatures/Jan")]
    [InlineData("..\\Jan")]
    [InlineData("C:Jan")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReadAsync_UnknownOrUnsafeName_IsNull(string name)
    {
        Write("Jan.htm");
        Write(Path.Combine("..", "Jan.htm"));

        Assert.Null(await new OutlookSignatureFiles(_folder).ReadAsync(name, 1250, Ct));
    }

    [Fact]
    public async Task ReadAsync_WithoutAFolder_IsNull()
    {
        Assert.Null(await new OutlookSignatureFiles(Path.Combine(_root, "brak")).ReadAsync("Jan", 1250, Ct));
    }

    [Fact]
    public async Task ReadAsync_AFileOverTheLimit_IsRefused()
    {
        File.WriteAllBytes(Path.Combine(_folder, "Duży.htm"), new byte[OutlookSignatureFiles.MaxFileBytes + 1]);

        await Assert.ThrowsAsync<InvalidDataException>(() => new OutlookSignatureFiles(_folder).ReadAsync("Duży", 1250, Ct));
    }

    [Fact]
    public async Task ReadAsync_Cancelled_Throws()
    {
        Write("Jan.htm");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OutlookSignatureFiles(_folder).ReadAsync("Jan", 1250, cancellation.Token));
    }

    [Theory]
    [InlineData("Jan_files/image001.png")]
    [InlineData("Jan_files\\image001.png")]
    [InlineData("Jan%5Ffiles/image001.png")]
    [InlineData(" Jan_files/image001.png ")]
    public void ReadRelative_AnImageInsideTheFolder_IsRead(string reference)
    {
        Directory.CreateDirectory(Path.Combine(_folder, "Jan_files"));
        File.WriteAllBytes(Path.Combine(_folder, "Jan_files", "image001.png"), [1, 2, 3]);

        Assert.Equal([1, 2, 3], OutlookSignatureFiles.ReadRelative(_folder, reference));
    }

    [Fact]
    public void ReadRelative_UrlEncodedPolishNames_AreDecoded()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "Mój podpis_pliki"));
        File.WriteAllBytes(Path.Combine(_folder, "Mój podpis_pliki", "obraz ż.png"), [7]);

        Assert.Equal([7], OutlookSignatureFiles.ReadRelative(_folder, "M%C3%B3j%20podpis_pliki/obraz%20%C5%BC.png"));
    }

    [Theory]
    [InlineData("../secret.png")]
    [InlineData("..\\secret.png")]
    [InlineData("Jan_files/../../secret.png")]
    [InlineData("%2E%2E/secret.png")]
    [InlineData("Jan_files/%2e%2e/%2e%2e/secret.png")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\image.png")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("C:secret.png")]
    [InlineData("file:///C:/secret.png")]
    [InlineData("http://example.com/image.png")]
    [InlineData("./Jan_files/image001.png")]
    [InlineData("Jan_files//image001.png")]
    [InlineData("image001.png:stream")]
    [InlineData("")]
    [InlineData(null)]
    public void ReadRelative_RefusesEverythingOutsideTheFolder(string? reference)
    {
        File.WriteAllBytes(Path.Combine(_root, "secret.png"), [6, 6, 6]);
        Directory.CreateDirectory(Path.Combine(_folder, "Jan_files"));
        File.WriteAllBytes(Path.Combine(_folder, "Jan_files", "image001.png"), [1]);

        Assert.Null(OutlookSignatureFiles.ReadRelative(_folder, reference));
    }

    [Fact]
    public void ReadRelative_ASymbolicLinkLeadingOut_IsRefused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        File.WriteAllBytes(Path.Combine(_root, "secret.png"), [6, 6, 6]);
        Directory.CreateDirectory(Path.Combine(_root, "outside"));
        File.WriteAllBytes(Path.Combine(_root, "outside", "image001.png"), [6]);
        Directory.CreateDirectory(Path.Combine(_folder, "Jan_files"));
        File.CreateSymbolicLink(Path.Combine(_folder, "Jan_files", "link.png"), Path.Combine(_root, "secret.png"));
        Directory.CreateSymbolicLink(Path.Combine(_folder, "Inny_files"), Path.Combine(_root, "outside"));
        File.WriteAllBytes(Path.Combine(_folder, "Jan_files", "real.png"), [1]);
        File.CreateSymbolicLink(Path.Combine(_folder, "Jan_files", "inside.png"), Path.Combine(_folder, "Jan_files", "real.png"));

        Assert.Null(OutlookSignatureFiles.ReadRelative(_folder, "Jan_files/link.png"));
        Assert.Null(OutlookSignatureFiles.ReadRelative(_folder, "Inny_files/image001.png"));
        Assert.Equal([1], OutlookSignatureFiles.ReadRelative(_folder, "Jan_files/inside.png"));
    }

    [Fact]
    public void ReadRelative_MissingOrTooLarge_IsNull()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "Jan_files"));
        File.WriteAllBytes(Path.Combine(_folder, "Jan_files", "duzy.png"), new byte[OutlookSignatureFiles.MaxFileBytes + 1]);
        File.WriteAllBytes(Path.Combine(_folder, "Jan_files", "graniczny.png"), new byte[OutlookSignatureFiles.MaxFileBytes]);

        Assert.Null(OutlookSignatureFiles.ReadRelative(_folder, "Jan_files/brak.png"));
        Assert.Null(OutlookSignatureFiles.ReadRelative(_folder, "Jan_files/duzy.png"));
        Assert.Equal(OutlookSignatureFiles.MaxFileBytes, OutlookSignatureFiles.ReadRelative(_folder, "Jan_files/graniczny.png")!.Length);
        Assert.Null(OutlookSignatureFiles.ReadRelative(_folder, "Jan_files"));
    }

    [Theory]
    [InlineData("Jan Kowalski – podpis", true)]
    [InlineData("Łódź", true)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("C:a", false)]
    [InlineData(null, false)]
    public void IsSafeName_IsAPlainFileName(string? name, bool expected) => Assert.Equal(expected, OutlookSignatureFiles.IsSafeName(name));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Write(string relative)
    {
        var path = Path.Combine(_folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<html><body>Pozdrawiam</body></html>");
    }
}
