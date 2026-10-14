using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Formatting.AdaptiveCards;

/// <summary>Building blocks shared by the Teams card formatters: severity container styles, label/value rows (label in
/// a TextBlock, value in a TextRun) and table rows of TextRuns.</summary>
public static class TeamsCardParts
{
    /// <summary>Container style of a severity: attention (Critical/High), warning (Medium), accent (Low), default (Info).</summary>
    public static string Style(Severity severity) => severity switch
    {
        Severity.Critical or Severity.High => "attention",
        Severity.Medium => "warning",
        Severity.Low => "accent",
        _ => "default",
    };

    /// <summary>A "Kto | CONTOSO\adm.nowak" row: the watchdog-generated label in a TextBlock, the value in a TextRun.</summary>
    public static ColumnSet LabelValue(string label, string value, string spacing = "small") => new(
    [
        new Column([new TextBlock(label) { Weight = "bolder" }], 1),
        new Column([RichTextBlock.Of(value)], 3),
    ]) { Spacing = spacing };

    /// <summary>A row of plain-text cells with relative widths.</summary>
    public static ColumnSet TextRow(IReadOnlyList<(string Text, int Width)> cells, bool subtleFirst = false) => new(
        [.. cells.Select((c, i) => new Column([RichTextBlock.Of(c.Text, isSubtle: subtleFirst && i == 0)], c.Width))])
    {
        Spacing = "small",
    };

    /// <summary>The bold, separated section title ("Zmiany (2)").</summary>
    public static TextBlock SectionTitle(string text) => new(text) { Weight = "bolder", Spacing = "large", Separator = true };

    /// <summary>A subtle watchdog-generated line ("… i 12 kolejnych", "Pokazano 40 z 52").</summary>
    public static TextBlock Subtle(string text) => new(text) { IsSubtle = true, Spacing = "small" };
}
