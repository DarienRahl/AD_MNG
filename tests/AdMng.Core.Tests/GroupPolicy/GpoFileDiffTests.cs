using System.Text;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Testing;

namespace AdMng.Core.Tests.GroupPolicy;

public class GpoFileDiffTests
{
    [Fact]
    public void SettingsFile_IsComparedBySettings()
    {
        var before = FakeSysvolFileReader.Utf16("[Unicode]\r\nUnicode=yes\r\n[System Access]\r\nMinimumPasswordLength = 7\r\n");
        var after = FakeSysvolFileReader.Utf16("[Unicode]\r\nUnicode=yes\r\n[System Access]\r\nMinimumPasswordLength = 12\r\nPasswordComplexity = 1\r\n");

        var diff = GpoFileDiff.Compare(SecurityTemplate.RelativePath, before, after);

        Assert.Equal(GpoFileDiffKind.Settings, diff.Kind);
        Assert.Contains(diff.Lines, l => l.Kind == GpoDiffLineKind.Removed && l.Text.EndsWith("= 7", StringComparison.Ordinal));
        Assert.Contains(diff.Lines, l => l.Kind == GpoDiffLineKind.Added && l.Text.EndsWith("= 12", StringComparison.Ordinal));
        Assert.Equal(2, diff.Added);
    }

    [Fact]
    public void TextFile_IsComparedByLines()
    {
        var diff = GpoFileDiff.Compare("Machine/Scripts/Startup/start.cmd",
            Encoding.UTF8.GetBytes("@echo off\r\necho Zażółć\r\nexit\r\n"), Encoding.UTF8.GetBytes("@echo off\r\necho gęślą jaźń\r\nexit\r\n"));

        Assert.Equal(GpoFileDiffKind.Text, diff.Kind);
        Assert.Equal(
            [(GpoDiffLineKind.Unchanged, "@echo off"), (GpoDiffLineKind.Removed, "echo Zażółć"), (GpoDiffLineKind.Added, "echo gęślą jaźń"), (GpoDiffLineKind.Unchanged, "exit")],
            diff.Lines.Select(l => (l.Kind, l.Text)));
    }

    [Fact]
    public void NewFile_IsAllAdded_AndBinaryIsBinary()
    {
        Assert.All(GpoFileDiff.Compare("GPO.cmt", null, Encoding.UTF8.GetBytes("komentarz")).Lines, l => Assert.Equal(GpoDiffLineKind.Added, l.Kind));
        Assert.Equal(GpoFileDiffKind.Binary, GpoFileDiff.Compare("Machine/Applications/app.aas", [0, 1, 2], [0, 1, 3]).Kind);
    }

    [Fact]
    public void Secrets_AreNeverShown()
    {
        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><Groups clsid=\"{3125E937-EB16-4b4c-9934-544FC6D24D26}\"><User clsid=\"{DF5F1855-51E5-4d24-8B1A-D9BDE98BA1D1}\" name=\"admin\" uid=\"{1}\"><Properties action=\"U\" userName=\"admin\" cpassword=\"tajne-haslo-base64\"/></User></Groups>";

        var diff = GpoFileDiff.Compare("Machine/Preferences/Groups/Groups.xml", null, Encoding.UTF8.GetBytes(xml));

        Assert.Equal(GpoFileDiffKind.Settings, diff.Kind);
        Assert.DoesNotContain(diff.Lines, l => l.Text.Contains("tajne", StringComparison.Ordinal));
        Assert.Contains(diff.Lines, l => l.Text.EndsWith("= ***", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Machine/Preferences/Groups/Groups.xml")]
    [InlineData("Machine/Scripts/Startup/kopia-groups.xml")]
    public void UnparseableFileWithAGppPassword_IsNeverShownAsText(string path)
    {
        // Malformed XML (the parser throws): the raw text would show the cpassword value.
        var broken = Encoding.UTF8.GetBytes("<Groups><User name=\"admin\"><Properties cpassword=\"tajne-haslo-łódź\" </User>");

        var diff = GpoFileDiff.Compare(path, null, broken);

        Assert.Equal(GpoFileDiffKind.Binary, diff.Kind);
        Assert.DoesNotContain(diff.Lines, l => l.Text.Contains("tajne", StringComparison.Ordinal));
    }
}
