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

    /// <summary>
    /// A line nested deeper than the window accepts is refused, whether the parser gave up on it ("ScriptTooComplicated", no tree - it
    /// depends on the stack it parses on) or made a tree: 200 levels (parsed on the caller's thread), 5 000 and 20 000 (on the parser's
    /// own thread).
    /// </summary>
    [Theory]
    [InlineData("(", ")", 200)]
    [InlineData("$(", ")", 200)]
    [InlineData("{", "}", 200)]
    [InlineData("@(", ")", 200)]
    [InlineData("(", ")", 5000)]
    [InlineData("$(", ")", 5000)]
    [InlineData("{", "}", 5000)]
    [InlineData("(", ")", 20000)]
    [InlineData("$(", ")", 20000)]
    [InlineData("{", "}", 20000)]
    public void A_line_nested_too_deeply_is_refused(string open, string close, int levels)
    {
        var script = "$x = " + string.Concat(Enumerable.Repeat(open, levels)) + "1" + string.Concat(Enumerable.Repeat(close, levels));

        Assert.Equal(InputGuard.TooComplexMessage, InputGuard.Refusal(script));
    }

    /// <summary>
    /// The limits of <see cref="Input.InputParser"/>: a tree deeper than its maximum is refused (a deeper one ends the engine's compiler or
    /// the walks over it with a stack overflow), a text longer than its maximum is refused unparsed; just below both a command passes, also
    /// one long enough to be parsed on the parser's own thread.
    /// </summary>
    [Fact]
    public void A_text_past_the_parsers_limits_is_refused_and_one_below_them_passes()
    {
        static string Sum(int terms) => "$x = 1" + string.Concat(Enumerable.Repeat("+1", terms));
        var lines = string.Join("\n", Enumerable.Repeat("Get-AdmUser -Identity jkowalski -Properties mail, department | Select-Object Name", 300));

        Assert.Null(InputGuard.Refusal(Sum(Input.InputParser.MaxDepth - 10)));
        Assert.Equal(InputGuard.TooComplexMessage, InputGuard.Refusal(Sum(Input.InputParser.MaxDepth)));
        Assert.Equal(InputGuard.TooComplexMessage, InputGuard.Refusal("$x = " + new string('(', 200) + "1" + new string(')', 200)));
        Assert.True(lines.Length > Input.InputParser.InlineLength);
        Assert.Null(InputGuard.Refusal(lines));
        Assert.Null(InputGuard.Refusal("'" + new string('a', Input.InputParser.MaxLength - 2) + "'"));
        Assert.Equal(InputGuard.TooComplexMessage, InputGuard.Refusal("'" + new string('a', Input.InputParser.MaxLength - 1) + "'"));
        Assert.Equal(InputGuard.TooComplexMessage, InputGuard.Refusal(lines + "\n'x' > out.txt" + new string(' ', Input.InputParser.MaxLength)));
        Assert.Contains($"more than {Input.InputParser.MaxLength} characters", InputGuard.TooComplexMessage, StringComparison.Ordinal);
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
