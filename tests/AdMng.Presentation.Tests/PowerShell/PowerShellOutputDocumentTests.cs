using System.Diagnostics;
using AdMng.Core.PowerShell;
using AdMng.Presentation.PowerShell;

namespace AdMng.Presentation.Tests.PowerShell;

/// <summary>The scrollback (track R, decisions D6 and D8): lines, partial lines, colours, links, the cap and its note.</summary>
public sealed class PowerShellOutputDocumentTests
{
    private static PowerShellTextItem Text(string text, PowerShellStream stream = PowerShellStream.Output, ConsoleColor? foreground = null, ConsoleColor? background = null) =>
        new(stream, text, foreground, background);

    private static string[] Texts(PowerShellOutputDocument document) => [.. document.Lines.Select(line => line.Text)];

    [Fact]
    public void TextIsSplitIntoLines_TheEndsAreNotPartOfThem_ABlankLineIsALine()
    {
        var document = new PowerShellOutputDocument();

        document.Add([Text("one\r\ntwo\n\nthree\n")]);

        Assert.Equal(["one", "two", string.Empty, "three"], Texts(document));
        Assert.All(document.Lines, line => Assert.False(line.IsOpen));
        Assert.Equal(4, document.OutputLinesWritten);
    }

    [Fact]
    public void ALineWithoutAnEnd_IsOpen_AndGrowsWithTheTextThatFollowsInTheSameStream()
    {
        var document = new PowerShellOutputDocument();
        document.Add([Text("a", foreground: ConsoleColor.Green)]);
        var line = document.Lines[0];
        var changes = new List<string?>();
        line.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        document.Add([Text("b", foreground: ConsoleColor.Red), Text("c\nd")]);

        Assert.Single(document.Lines.Take(1));
        Assert.Equal(["abc", "d"], Texts(document));
        Assert.Same(line, document.Lines[0]);
        Assert.False(line.IsOpen);
        Assert.True(document.Lines[1].IsOpen);
        Assert.Equal([ConsoleColor.Green, ConsoleColor.Red, null], line.Runs.Select(run => run.Foreground));
        Assert.Contains(nameof(PowerShellOutputLine.Runs), changes);
        Assert.Contains(nameof(PowerShellOutputLine.Text), changes);
        Assert.Equal(1, document.OutputLinesWritten);
    }

    [Fact]
    public void AnotherStream_EndsTheOpenLine_AndStartsItsOwn()
    {
        var document = new PowerShellOutputDocument();

        document.Add([Text("out"), Text("err\n", PowerShellStream.Error), Text("more\n")]);

        Assert.Equal(["out", "err", "more"], Texts(document));
        Assert.Equal([PowerShellLineKind.Output, PowerShellLineKind.Error, PowerShellLineKind.Output], document.Lines.Select(line => line.Kind));
    }

    [Fact]
    public void EndRun_EndsAnOpenLine_SoTheNextTextStartsANewOne()
    {
        var document = new PowerShellOutputDocument();
        document.Add([Text("no end")]);
        Assert.True(document.Lines[0].IsOpen);

        document.EndRun();
        document.Add([Text("next\n")]);

        Assert.Equal(["no end", "next"], Texts(document));
        Assert.Equal(2, document.OutputLinesWritten);
    }

    [Fact]
    public void EveryStreamHasItsKind()
    {
        var document = new PowerShellOutputDocument();

        document.Add(
            [
                Text("o\n"), Text("e\n", PowerShellStream.Error), Text("w\n", PowerShellStream.Warning), Text("v\n", PowerShellStream.Verbose), Text("d\n", PowerShellStream.Debug),
            ]);

        Assert.Equal(
            [PowerShellLineKind.Output, PowerShellLineKind.Error, PowerShellLineKind.Warning, PowerShellLineKind.Verbose, PowerShellLineKind.Debug],
            document.Lines.Select(line => line.Kind));
    }

    [Fact]
    public void ADistinguishedName_IsALinkRun_ThePartsAroundItAreNot_AndTheColoursStay()
    {
        var document = new PowerShellOutputDocument();

        document.Add([Text("Jan  CN=Jan,OU=X,DC=a,DC=b  ok\n", foreground: ConsoleColor.Cyan, background: ConsoleColor.Black)]);

        var runs = document.Lines[0].Runs;
        Assert.Equal(["Jan  ", "CN=Jan,OU=X,DC=a,DC=b", "  ok"], runs.Select(run => run.Text));
        Assert.Equal([false, true, false], runs.Select(run => run.IsLink));
        Assert.Equal("CN=Jan,OU=X,DC=a,DC=b", runs[1].DistinguishedName);
        Assert.All(runs, run => Assert.Equal((ConsoleColor?)ConsoleColor.Cyan, run.Foreground));
        Assert.All(runs, run => Assert.Equal((ConsoleColor?)ConsoleColor.Black, run.Background));
        Assert.Equal("Jan  CN=Jan,OU=X,DC=a,DC=b  ok", document.Lines[0].Text);
    }

    [Fact]
    public void ANameInAnErrorOrAWarning_IsALink_ANameInTheEchoOfACommandIsNot()
    {
        var document = new PowerShellOutputDocument();

        document.AddCommand("Get-AdmObject 'CN=Jan,DC=a,DC=b'", "PS> ", ">> ");
        document.Add([Text("Cannot find CN=Jan,DC=a,DC=b\n", PowerShellStream.Error), Text("Skipped CN=Ola,DC=a,DC=b\n", PowerShellStream.Warning)]);

        Assert.DoesNotContain(document.Lines[0].Runs, run => run.IsLink);
        Assert.Contains(document.Lines[1].Runs, run => run.IsLink);
        Assert.Contains(document.Lines[2].Runs, run => run.IsLink);
    }

    [Fact]
    public void ACutName_IsNotALink()
    {
        var document = new PowerShellOutputDocument();

        document.Add([Text("Jan  CN=Jan Kowalski,OU=Handlowcy,OU=Użytkownicy,DC=contoso,DC=…\n")]);

        Assert.DoesNotContain(document.Lines[0].Runs, run => run.IsLink);
    }

    [Fact]
    public void TheEchoOfACommand_HasThePromptAndTheContinuationPrompt_AndIsNotCounted()
    {
        var document = new PowerShellOutputDocument();

        document.AddCommand("if ($x) {\n  1\n}", "PS> ", ">> ");

        Assert.Equal(["PS> if ($x) {", ">>   1", ">> }"], Texts(document));
        Assert.All(document.Lines, line => Assert.Equal(PowerShellLineKind.Command, line.Kind));
        Assert.Equal(0, document.OutputLinesWritten);
    }

    [Fact]
    public void TheWindowsOwnLines_AreNotCounted_AndEndAnOpenLine()
    {
        var document = new PowerShellOutputDocument();
        document.Add([Text("partial")]);

        document.AddLine(PowerShellLineKind.Information, "line one\nline two");

        Assert.Equal(["partial", "line one", "line two"], Texts(document));
        Assert.Equal(PowerShellLineKind.Information, document.Lines[1].Kind);
        Assert.Equal(1, document.OutputLinesWritten);
    }

    [Fact]
    public void ABatchIsOneChangeOfTheList_NoMatterHowManyLines()
    {
        var document = new PowerShellOutputDocument();
        var changes = 0;
        document.Lines.CollectionChanged += (_, _) => changes++;

        document.Add([.. Enumerable.Range(0, 5_000).Select(i => Text($"line {i}\n"))]);

        Assert.Equal(1, changes);
        Assert.Equal(5_000, document.Lines.Count);
    }

    [Fact]
    public void PastTheCapPlusItsSlack_TheOldestLinesAreDropped_AndCounted()
    {
        var document = new PowerShellOutputDocument();
        var total = PowerShellOutputDocument.MaxLines + PowerShellOutputDocument.Slack;

        document.Add([.. Enumerable.Range(1, total).Select(i => Text($"line {i}\n"))]);
        Assert.Equal(total, document.Lines.Count);
        Assert.Equal(0, document.DroppedLines);

        document.Add([Text("one more\n")]);

        Assert.Equal(PowerShellOutputDocument.MaxLines, document.Lines.Count);
        Assert.Equal(PowerShellOutputDocument.Slack + 1, document.DroppedLines);
        Assert.Equal("one more", document.Lines[^1].Text);
        Assert.Equal($"line {PowerShellOutputDocument.Slack + 2}", document.Lines[0].Text);
    }

    [Fact]
    public void AHundredThousandLines_AreFast_AndTheDocumentNeverHoldsMoreThanTheCapAndItsSlack()
    {
        var document = new PowerShellOutputDocument();
        var most = 0;
        document.Lines.CollectionChanged += (_, _) => most = Math.Max(most, document.Lines.Count);
        var watch = Stopwatch.StartNew();

        for (var batch = 0; batch < 200; batch++)
        {
            document.Add([.. Enumerable.Range(0, 500).Select(i => Text($"CN=User {batch * 500 + i},OU=Users,DC=contoso,DC=test\n"))]);
        }

        Assert.True(most <= PowerShellOutputDocument.MaxLines + PowerShellOutputDocument.Slack + 500);
        Assert.Equal(100_000, document.OutputLinesWritten);
        Assert.Equal(100_000, document.Lines.Count + document.DroppedLines);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
    }

    [Fact]
    public void LineByLine_TheDocumentDoesNotCopyItsListForEveryLine()
    {
        var document = new PowerShellOutputDocument();
        var drops = 0;
        var last = 0;
        document.Lines.CollectionChanged += (_, _) =>
        {
            if (document.Lines.Count < last)
            {
                drops++;
            }

            last = document.Lines.Count;
        };

        for (var index = 0; index < PowerShellOutputDocument.MaxLines * 2; index++)
        {
            document.Add([Text($"line {index}\n")]);
        }

        // One drop per slack, not one per line.
        Assert.InRange(drops, 1, (PowerShellOutputDocument.MaxLines * 2 / PowerShellOutputDocument.Slack) + 1);
        Assert.InRange(document.Lines.Count, PowerShellOutputDocument.MaxLines, PowerShellOutputDocument.MaxLines + PowerShellOutputDocument.Slack);
    }

    [Fact]
    public void ASingleLine_IsAnOrdinaryAdd_AMoreThanOneALoneReset()
    {
        var document = new PowerShellOutputDocument();
        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        document.Lines.CollectionChanged += (_, e) => actions.Add(e.Action);

        document.Add([Text("one\n")]);
        document.Add([Text("two\n"), Text("three\n")]);

        Assert.Equal([System.Collections.Specialized.NotifyCollectionChangedAction.Add, System.Collections.Specialized.NotifyCollectionChangedAction.Reset], actions);
    }

    [Fact]
    public void Clear_EmptiesTheDocument_AndStartsTheCountAgain()
    {
        var document = new PowerShellOutputDocument();
        document.Add([.. Enumerable.Range(1, PowerShellOutputDocument.MaxLines + PowerShellOutputDocument.Slack + 10).Select(i => Text($"{i}\n"))]);
        document.Add([Text("open")]);
        Assert.True(document.DroppedLines > 0);

        document.Clear();
        document.Add([Text("fresh\n")]);

        Assert.Equal(["fresh"], Texts(document));
        Assert.Equal(0, document.DroppedLines);
    }

    [Fact]
    public void AnEmptyItemAddsNothing_AndNothingToSplitIsNotAnError()
    {
        var document = new PowerShellOutputDocument();

        document.Add([Text(string.Empty), Text(string.Empty, PowerShellStream.Error)]);
        document.Add([]);

        Assert.Empty(document.Lines);
    }

    [Fact]
    public void TheLinesShowNoText_InToString()
    {
        var document = new PowerShellOutputDocument();
        document.Add([Text("CN=Jan,DC=a,DC=b\n")]);

        var text = document.Lines[0].ToString();

        Assert.DoesNotContain("Jan", text, StringComparison.Ordinal);
        Assert.Contains("Output", text, StringComparison.Ordinal);
    }
}
