using AdMng.Core.PowerShell;

namespace AdMng.Presentation.Tests.PowerShell;

/// <summary>What the context menu of the output does: copies lines and distinguished names (track R, stage R1).</summary>
public sealed class PowerShellWindowCopyTests
{
    [Fact]
    public async Task SelectedLines_AreCopiedOnePerLine_AndTheToastCountsThem()
    {
        var harness = await PowerShellHarness.CreateAsync();
        var window = await harness.OpenAsync();
        window.Output.Add([new PowerShellTextItem(PowerShellStream.Output, "first\nsecond\nthird\n")]);

        window.CopyLines(window.Output.Lines.Skip(window.Output.Lines.Count - 2));

        Assert.Equal($"second{Environment.NewLine}third", harness.Directory.Clipboard.Text);
        Assert.Contains(harness.Directory.Notifications.Items, item => item.Message.Contains("2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoLines_CopyNothing_AndTheClipboardIsLeftAlone()
    {
        var harness = await PowerShellHarness.CreateAsync();
        var window = await harness.OpenAsync();

        window.CopyLines(null);
        window.CopyLines([]);

        Assert.Null(harness.Directory.Clipboard.Text);
    }

    [Fact]
    public async Task ADistinguishedName_IsCopiedAsItIs()
    {
        var harness = await PowerShellHarness.CreateAsync();
        var window = await harness.OpenAsync();

        window.CopyDistinguishedNameCommand.Execute("CN=Łukasz Żółć,OU=Firma,DC=contoso,DC=test");
        Assert.Equal("CN=Łukasz Żółć,OU=Firma,DC=contoso,DC=test", harness.Directory.Clipboard.Text);

        window.CopyDistinguishedNameCommand.Execute(" ");
        window.CopyDistinguishedNameCommand.Execute(null);
        Assert.Equal("CN=Łukasz Żółć,OU=Firma,DC=contoso,DC=test", harness.Directory.Clipboard.Text);
    }
}
