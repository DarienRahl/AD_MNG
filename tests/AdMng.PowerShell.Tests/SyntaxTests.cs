using AdMng.Core.PowerShell;
using AdMng.PowerShell.Input;

namespace AdMng.PowerShell.Tests;

/// <summary>D5: highlighting comes from the parser's tokens; Enter versus a new line comes from the completeness.</summary>
public sealed class SyntaxTests
{
    [Fact]
    public void The_tokens_of_a_pipeline_get_their_kinds_and_places()
    {
        const string Input = "Get-AdmVersion -Verbose | Where-Object { $_ -eq 'x' } # note";

        var spans = PowerShellSyntax.Tokenize(Input);

        Assert.Contains(new PowerShellSyntaxSpan(0, 14, PowerShellSyntaxKind.Command), spans);
        Assert.Contains(new PowerShellSyntaxSpan(15, 8, PowerShellSyntaxKind.Parameter), spans);
        Assert.Contains(new PowerShellSyntaxSpan(26, 12, PowerShellSyntaxKind.Command), spans);
        Assert.Contains(new PowerShellSyntaxSpan(41, 2, PowerShellSyntaxKind.Variable), spans);
        Assert.Contains(new PowerShellSyntaxSpan(44, 3, PowerShellSyntaxKind.Operator), spans);
        Assert.Contains(new PowerShellSyntaxSpan(48, 3, PowerShellSyntaxKind.String), spans);
        Assert.Contains(new PowerShellSyntaxSpan(54, 6, PowerShellSyntaxKind.Comment), spans);
    }

    [Fact]
    public void Keywords_numbers_types_and_members_are_told_apart()
    {
        var spans = PowerShellSyntax.Tokenize("if ($true) { [int]$x = 42; $x.Length } else { 0 }");

        Assert.Contains(spans, s => s.Kind == PowerShellSyntaxKind.Keyword && s.Start == 0 && s.Length == 2);
        Assert.Contains(spans, s => s.Kind == PowerShellSyntaxKind.Keyword && s.Length == 4);
        Assert.Contains(spans, s => s.Kind == PowerShellSyntaxKind.Number && s.Length == 2);
        Assert.Contains(spans, s => s.Kind == PowerShellSyntaxKind.Type);
        Assert.Contains(spans, s => s.Kind == PowerShellSyntaxKind.Member && s.Length == 6);
    }

    [Fact]
    public void Spans_are_in_order_inside_the_input_and_never_empty()
    {
        const string Input = "Get-ChildItem 'a b' -Recurse | Select-Object -First 3 # tail";

        var spans = PowerShellSyntax.Tokenize(Input);

        Assert.NotEmpty(spans);
        Assert.All(spans, s => Assert.True(s.Length > 0 && s.Start >= 0 && s.Start + s.Length <= Input.Length));
        Assert.Equal(spans.OrderBy(s => s.Start).ToList(), spans);
    }

    /// <summary>
    /// A text longer than the parser's limit is not parsed (InputParser): no highlighting, and complete, so that Enter runs it and the guard
    /// says why it is refused. A long text below the limit is parsed on the parser's own thread and highlighted like a short one.
    /// </summary>
    [Fact]
    public void A_text_past_the_length_limit_is_complete_and_not_highlighted_and_a_long_one_below_it_is()
    {
        var tooLong = "'" + new string('a', InputParser.MaxLength) + "'";
        var line = "Get-AdmUser -Identity jkowalski | Select-Object Name";
        var lines = string.Join("\n", Enumerable.Repeat(line, 200));

        Assert.Empty(PowerShellSyntax.Tokenize(tooLong));
        Assert.True(PowerShellSyntax.IsComplete(tooLong));
        Assert.True(lines.Length > InputParser.InlineLength);
        Assert.Equal(200 * PowerShellSyntax.Tokenize(line).Count, PowerShellSyntax.Tokenize(lines).Count);
        Assert.False(PowerShellSyntax.IsComplete(lines + " | ForEach-Object {"));
    }

    [Fact]
    public void Tokenizing_an_incomplete_or_broken_input_does_not_throw()
    {
        Assert.NotNull(PowerShellSyntax.Tokenize("Get-Date -"));
        Assert.NotNull(PowerShellSyntax.Tokenize("'unterminated"));
        Assert.Empty(PowerShellSyntax.Tokenize(string.Empty));
    }

    [Theory]
    [InlineData("\"open", false)]
    [InlineData("'open", false)]
    [InlineData("Get-Date |", false)]
    [InlineData("if ($true) {", false)]
    [InlineData("Get-Process | Where-Object {", false)]
    [InlineData("@(1, 2", false)]
    [InlineData("@'\nhere", false)]
    [InlineData("Get-Date", true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("if ($true) { 1 }", true)]
    [InlineData("@'\nhere\n'@", true)]
    [InlineData("Get-Date )", true)]
    public void Input_is_complete_unless_something_is_still_open(string input, bool expected)
    {
        Assert.Equal(expected, PowerShellSyntax.IsComplete(input));
    }

    [Fact]
    public async Task The_session_gives_the_same_answers()
    {
        await using var session = await TestSessions.CreateAsync();

        Assert.False(session.IsInputComplete("'open"));
        Assert.True(session.IsInputComplete("Get-Date"));
        Assert.Contains(new PowerShellSyntaxSpan(0, 8, PowerShellSyntaxKind.Command), session.Tokenize("Get-Date"));
    }
}
