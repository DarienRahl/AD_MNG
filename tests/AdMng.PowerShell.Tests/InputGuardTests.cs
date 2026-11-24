using AdMng.PowerShell.Guards;

namespace AdMng.PowerShell.Tests;

/// <summary>What the window refuses from the text of a command, and what it must not refuse (the call operator, a string, discarding output).</summary>
public sealed class InputGuardTests
{
    [Theory]
    [InlineData("using module ActiveDirectory", InputGuard.UsingMessage)]
    [InlineData("using module @{ ModuleName = 'X'; ModuleVersion = '1.0' }", InputGuard.UsingMessage)]
    [InlineData("using assembly System.Windows.Forms", InputGuard.UsingMessage)]
    [InlineData("& { using module X }", InputGuard.UsingMessage)]
    [InlineData("Get-Date &", InputGuard.BackgroundMessage)]
    [InlineData("Get-Date | Out-String &", InputGuard.BackgroundMessage)]
    [InlineData("Get-Date && Get-Date &", InputGuard.BackgroundMessage)]
    [InlineData("if ($true) { Get-Date & }", InputGuard.BackgroundMessage)]
    [InlineData("'x' > out.txt", InputGuard.RedirectionMessage)]
    [InlineData("'x' >> out.txt", InputGuard.RedirectionMessage)]
    [InlineData("Get-Item x 2> errors.txt", InputGuard.RedirectionMessage)]
    [InlineData("'x' *> all.txt", InputGuard.RedirectionMessage)]
    [InlineData("'x' > $file", InputGuard.RedirectionMessage)]
    [InlineData("1 | ForEach-Object -Parallel { 1 }", InputGuard.ParallelMessage)]
    [InlineData("1 | % -Parallel { 1 }", InputGuard.ParallelMessage)]
    [InlineData("1 | foreach -Pa { 1 }", InputGuard.ParallelMessage)]
    [InlineData("1 | FOREACH-OBJECT { 1 } -AsJob", InputGuard.ParallelMessage)]
    [InlineData("1 | ForEach-Object { 1 } -ThrottleLimit 2", InputGuard.ParallelMessage)]
    public void A_construct_that_bypasses_the_command_list_is_refused(string script, string expected)
    {
        Assert.Equal(expected, InputGuard.Refusal(script));
    }

    /// <summary>A line the parser gives up on (no syntax tree, "ScriptTooComplicated") is refused, not passed - the guard could not look into it.</summary>
    [Theory]
    [InlineData("(", ")")]
    [InlineData("$(", ")")]
    [InlineData("{", "}")]
    public void A_line_the_parser_gives_up_on_is_refused(string open, string close)
    {
        var script = "$x = " + string.Concat(Enumerable.Repeat(open, 20000)) + "1" + string.Concat(Enumerable.Repeat(close, 20000));

        Assert.Equal(InputGuard.TooComplexMessage, InputGuard.Refusal(script));
    }

    [Theory]
    [InlineData("& { Get-Date }")]
    [InlineData("& 'Get-Date'")]
    [InlineData(". { $x = 1 }")]
    [InlineData("'a & b'; \"x > y\"; '# using module X'")]
    [InlineData("Get-Date && Get-Date")]
    [InlineData("Get-Date || Get-Date")]
    [InlineData("'x' > $null; 'y' > $NULL; Get-Item x 2> $null")]
    [InlineData("Get-Item x 2>&1 | Out-String; 'x' *>&1")]
    [InlineData("using namespace System.Text")]
    [InlineData("1 | ForEach-Object -Process { 1 } -ArgumentList 2; 1 | % -Begin { } -Process { } -End { }")]
    [InlineData("Get-AdmUser -Identity 'Jan & Anna' -Properties mail")]
    [InlineData("")]
    public void Ordinary_commands_pass(string script)
    {
        Assert.Null(InputGuard.Refusal(script));
    }
}
