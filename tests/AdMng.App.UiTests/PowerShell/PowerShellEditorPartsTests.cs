using System.Windows;
using System.Windows.Media;
using AdMng.App.PowerShell;
using AdMng.App.UiTests.Infrastructure;
using AdMng.Core.PowerShell;

namespace AdMng.App.UiTests.PowerShell;

/// <summary>
/// The parts of the PowerShell window that need no window (track R, stage R1): the conversion of the text and the caret between the
/// box and the model, and the theme brushes the colours of the tokens and of <c>Write-Host</c> are drawn with - every one of them must
/// be a brush of the running theme, because a key that is not there compiles and shows no colour (found on Windows only).
/// </summary>
public sealed class PowerShellEditorPartsTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("abc", "abc")]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("a\r\n\r\nb\r\n", "a\n\nb\n")]
    [InlineData("a\rb", "a\nb")]
    [InlineData("a\nb", "a\nb")]
    public void TheTextOfTheBox_BecomesTheTextOfTheModel(string box, string model) => Assert.Equal(model, PowerShellEditorText.ToModel(box));

    [Fact]
    public void ACaret_IsTheSamePlaceInBothSpellings()
    {
        var box = "ab\r\ncd\r\nef";
        var model = PowerShellEditorText.ToModel(box);

        // Every place of the model's text is a place of the box's text with the same character after it.
        for (var index = 0; index <= model.Length; index++)
        {
            var inBox = PowerShellEditorText.ToBoxIndex(box, index);
            Assert.Equal(index, PowerShellEditorText.ToModelIndex(box, inBox));
            if (index < model.Length && model[index] != '\n')
            {
                Assert.Equal(model[index], box[inBox]);
            }
        }

        Assert.Equal(box.Length, PowerShellEditorText.ToBoxIndex(box, 99));
        Assert.Equal(0, PowerShellEditorText.ToModelIndex(box, -3));
        Assert.Equal(model.Length, PowerShellEditorText.ToModelIndex(box, 99));
    }

    [Fact]
    public void ABoxThatBreaksLinesWithLineFeeds_NeedsNoConversion()
    {
        Assert.Equal(3, PowerShellEditorText.ToBoxIndex("a\nb", 3));
        Assert.Equal(2, PowerShellEditorText.ToModelIndex("a\nb", 2));
    }

    [Fact]
    public Task EveryKindOfToken_AndEveryConsoleColour_IsDrawnWithABrushOfTheTheme() => WpfTestHost.RunAsync(() =>
    {
        foreach (var kind in Enum.GetValues<PowerShellSyntaxKind>())
        {
            var key = PowerShellEditor.BrushKeyOf(kind);
            Assert.IsAssignableFrom<Brush>(Application.Current.FindResource(key));
        }

        foreach (var color in Enum.GetValues<ConsoleColor>())
        {
            if (PowerShellRuns.BrushKeyOf(color) is { } key)
            {
                Assert.IsAssignableFrom<Brush>(Application.Current.FindResource(key));
            }
        }

        // White, grey and black are the colours of a console, not of this window: the text keeps its own.
        Assert.Null(PowerShellRuns.BrushKeyOf(ConsoleColor.White));
        Assert.Null(PowerShellRuns.BrushKeyOf(ConsoleColor.Black));
        Assert.Null(PowerShellRuns.BrushKeyOf(ConsoleColor.Gray));
        Assert.Equal("SystemFillColorCriticalBrush", PowerShellRuns.BrushKeyOf(ConsoleColor.Red));

        // The tokens a script is read by do not share one colour: a command, a variable, a string and a number differ.
        var keys = new[] { PowerShellSyntaxKind.Command, PowerShellSyntaxKind.Variable, PowerShellSyntaxKind.String, PowerShellSyntaxKind.Number }
            .Select(PowerShellEditor.BrushKeyOf)
            .ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheMonospaceTokenIsAFontFamilyOfTheApplication() => WpfTestHost.RunAsync(() =>
    {
        Assert.IsType<FontFamily>(Application.Current.FindResource("Ad.Font.Mono"));
        return Task.CompletedTask;
    });
}
