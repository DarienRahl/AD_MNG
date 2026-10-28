namespace AdMng.Outlook.Tests;

/// <summary>The private temp folders of inline images (design note D5, 4.1).</summary>
public sealed class OutlookTempFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "admng-outlook-temp-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Create_MakesAPrivateFolder_NamedAfterTheProcess_DeletedWithItsFiles()
    {
        var files = new OutlookTempFiles(_root, _ => true);

        string path;
        string written;
        using (var folder = files.Create())
        {
            path = folder.Path;
            written = folder.Write("logo.png", [1, 2, 3]);
            Assert.Equal(_root, Path.GetDirectoryName(path));
            Assert.Matches($"^AdMng-Outlook-{Environment.ProcessId}-[0-9a-f]{{16}}$", Path.GetFileName(path));
            Assert.Equal([1, 2, 3], File.ReadAllBytes(written));
            Assert.Equal(path, Path.GetDirectoryName(written));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(path));
            }
        }

        Assert.False(Directory.Exists(path));
        Assert.False(File.Exists(written));
    }

    [Fact]
    public void Create_EveryItemGetsItsOwnFolder()
    {
        var files = new OutlookTempFiles(_root, _ => true);

        using var first = files.Create();
        using var second = files.Create();

        Assert.NotEqual(first.Path, second.Path);
    }

    [Fact]
    public void Write_MakesNamesSafeAndUnique()
    {
        using var folder = new OutlookTempFiles(_root, _ => true).Create();

        var names = new[] { "logo.png", "LOGO.png", "logo.png", "..\\..\\evil.png", "C:\\Users\\jan\\obraz ż.png", null, ".hidden", "" }
            .Select(name => Path.GetFileName(folder.Write(name, [0])))
            .ToList();

        Assert.Equal(["logo.png", "LOGO-2.png", "logo-3.png", "evil.png", "obraz__.png", "file", "hidden", "file-2"], names);
        Assert.All(Directory.EnumerateFiles(folder.Path), file => Assert.Equal(folder.Path, Path.GetDirectoryName(file)));
    }

    [Theory]
    [InlineData("image001.png", "image001.png")]
    [InlineData("a/b/c.gif", "c.gif")]
    [InlineData("...", "file")]
    [InlineData("___", "file")]
    [InlineData("zdjęcie.jpg", "zdj_cie.jpg")]
    public void SafeName_KeepsOnlyAPlainFileName(string name, string expected) => Assert.Equal(expected, OutlookTempFolder.SafeName(name));

    [Fact]
    public void SafeName_CutsLongNames_KeepingTheExtension()
    {
        var name = OutlookTempFolder.SafeName(new string('a', 200) + ".jpeg");

        Assert.Equal(64, name.Length);
        Assert.EndsWith(".jpeg", name, StringComparison.Ordinal);
    }

    [Fact]
    public void Dispose_Twice_AndReserveAfterDispose()
    {
        var folder = new OutlookTempFiles(_root, _ => true).Create();
        folder.Dispose();
        folder.Dispose();

        Assert.Throws<ObjectDisposedException>(() => folder.Reserve("x.png"));
    }

    [Fact]
    public void Dispose_DeletesReadOnlyFiles()
    {
        var folder = new OutlookTempFiles(_root, _ => true).Create();
        var file = folder.Write("logo.png", [1]);
        File.SetAttributes(file, FileAttributes.ReadOnly);

        folder.Dispose();

        Assert.False(Directory.Exists(folder.Path));
    }

    [Fact]
    public void CleanupStale_DeletesOnlyTheFoldersOfDeadProcesses()
    {
        Directory.CreateDirectory(_root);
        var dead = Folder("AdMng-Outlook-4242-0123456789abcdef", withFile: true);
        var alive = Folder("AdMng-Outlook-4343-0123456789abcdef");
        var mine = Folder($"AdMng-Outlook-{Environment.ProcessId}-0123456789abcdef");
        var foreign = Folder("AdMng-Outlook-notes");
        var unrelated = Folder("MailerWPiA");
        var files = new OutlookTempFiles(_root, pid => pid == 4343);

        var deleted = files.CleanupStale();

        Assert.Equal(1, deleted);
        Assert.False(Directory.Exists(dead));
        Assert.True(Directory.Exists(alive));
        Assert.True(Directory.Exists(mine));
        Assert.True(Directory.Exists(foreign));
        Assert.True(Directory.Exists(unrelated));
    }

    [Fact]
    public void CleanupStale_WithoutTheRoot_DoesNothing() =>
        Assert.Equal(0, new OutlookTempFiles(Path.Combine(_root, "brak"), _ => false).CleanupStale());

    [Fact]
    public void Default_IsTheUsersTempFolder_AndTheRealProcessCheck()
    {
        var files = new OutlookTempFiles();

        Assert.Equal(Path.GetTempPath(), files.Root);
        Assert.Equal(nameof(OutlookTempFiles), files.ToString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
    }

    private string Folder(string name, bool withFile = false)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        if (withFile)
        {
            File.WriteAllBytes(Path.Combine(path, "logo.png"), [1, 2, 3]);
        }

        return path;
    }
}
