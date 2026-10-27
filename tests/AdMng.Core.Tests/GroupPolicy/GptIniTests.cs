using System.Text;
using AdMng.Core.GroupPolicy;

namespace AdMng.Core.Tests.GroupPolicy;

public class GptIniTests
{
    private static readonly Encoding Cp1250 = Windows1250();

    private static Encoding Windows1250()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1250);
    }

    [Fact]
    public void Initial_IsWhatSambaToolWrites() =>
        Assert.Equal("[General]\r\nVersion=0\r\n", Encoding.ASCII.GetString(GptIni.Initial));

    [Fact]
    public void WithVersion_ChangesOnlyTheVersionLine_KeepsOtherBytesAndCrLf()
    {
        // ANSI (cp1250) with a Polish display name: every byte but the version line must survive.
        var original = Cp1250.GetBytes("[General]\r\nVersion=65539\r\ndisplayName=Stacje robocze – Zasady haseł\r\n");

        var updated = GptIni.WithVersion(original, new GpoVersion(1, 4).ToInt32());

        Assert.Equal(Cp1250.GetBytes("[General]\r\nVersion=65540\r\ndisplayName=Stacje robocze – Zasady haseł\r\n"), updated);
        Assert.Equal(65540, GptIni.ReadVersion(updated));
    }

    [Fact]
    public void WithVersion_KeepsLfAndTheKeySpelling()
    {
        var updated = GptIni.WithVersion(Encoding.ASCII.GetBytes("[general]\nversion = 3\n"), 4);

        Assert.Equal("[general]\nversion =4\n", Encoding.ASCII.GetString(updated));
    }

    [Fact]
    public void WithVersion_KeepsUtf16WithBom()
    {
        var original = (byte[])[0xFF, 0xFE, .. Encoding.Unicode.GetBytes("[General]\r\nVersion=1\r\ndisplayName=Łódź\r\n")];

        var updated = GptIni.WithVersion(original, 2);

        Assert.Equal((byte[])[0xFF, 0xFE, .. Encoding.Unicode.GetBytes("[General]\r\nVersion=2\r\ndisplayName=Łódź\r\n")], updated);
    }

    [Fact]
    public void WithVersion_AddsAMissingLineAfterTheHeader_OrAMissingSection()
    {
        Assert.Equal("[General]\r\nVersion=7\r\ngPCUserExtensionNames=[]\r\n",
            Encoding.ASCII.GetString(GptIni.WithVersion(Encoding.ASCII.GetBytes("[General]\r\ngPCUserExtensionNames=[]\r\n"), 7)));
        Assert.Equal("[General]\r\nVersion=7\r\n[Other]\r\nx=1\r\n",
            Encoding.ASCII.GetString(GptIni.WithVersion(Encoding.ASCII.GetBytes("[Other]\r\nx=1\r\n"), 7)));
        Assert.Equal("[General]\r\nVersion=7\r\n", Encoding.ASCII.GetString(GptIni.WithVersion([], 7)));
    }

    [Fact]
    public void WithVersion_DoesNotTouchAVersionKeyOfAnotherSection()
    {
        var updated = GptIni.WithVersion(Encoding.ASCII.GetBytes("[Other]\r\nVersion=9\r\n[General]\r\nVersion=1\r\n"), 2);

        Assert.Equal("[Other]\r\nVersion=9\r\n[General]\r\nVersion=2\r\n", Encoding.ASCII.GetString(updated));
    }

    [Fact]
    public void WithVersion_WritesNegativeValuesAsAdKeepsThem()
    {
        var version = new GpoVersion(0xFFFF, 1).ToInt32();

        var updated = GptIni.WithVersion(GptIni.Initial, version);

        Assert.Equal(version, GptIni.ReadVersion(updated));
    }
}
