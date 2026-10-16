using System.Text;

namespace AdMng.Watchdog.Core.Formatting;

/// <summary>
/// The text/plain alternative of notification e-mails (section 14.3): aligned "Label: value" lines and paragraphs,
/// wrapped at <see cref="LineWidth"/> characters (continuation lines are indented under the value).
/// </summary>
public sealed class PlainTextWriter
{
    /// <summary>Maximum characters per line.</summary>
    public const int LineWidth = 100;

    private readonly StringBuilder _text = new();

    /// <summary>A paragraph (wrapped) followed by an empty line.</summary>
    public PlainTextWriter Paragraph(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                Wrap(line, string.Empty, string.Empty);
            }
        }

        _text.Append('\n');
        return this;
    }

    /// <summary>A single (wrapped) line.</summary>
    public PlainTextWriter Line(string? text = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            _text.Append('\n');
            return this;
        }

        Wrap(text, string.Empty, string.Empty);
        return this;
    }

    /// <summary>Aligned "Label: value" lines.</summary>
    public PlainTextWriter Facts(IEnumerable<(string Label, string Value)> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var list = facts.ToList();
        if (list.Count == 0)
        {
            return this;
        }

        var width = Math.Min(28, list.Max(f => f.Label.Length) + 1);
        foreach (var (label, value) in list)
        {
            var prefix = (label + ":").PadRight(width + 1);
            if (!prefix.EndsWith(' '))
            {
                prefix += " ";
            }

            var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                Wrap(lines[i], i == 0 ? prefix : new string(' ', prefix.Length), new string(' ', prefix.Length));
            }
        }

        return this;
    }

    /// <summary>The text with LF line ends.</summary>
    public override string ToString() => _text.ToString().TrimEnd('\n') + "\n";

    private void Wrap(string text, string firstPrefix, string nextPrefix)
    {
        var prefix = firstPrefix;
        var remaining = text.TrimEnd();
        while (prefix.Length + remaining.Length > LineWidth)
        {
            var room = Math.Max(10, LineWidth - prefix.Length);
            var cut = remaining.LastIndexOf(' ', Math.Min(room, remaining.Length - 1));
            if (cut <= 0)
            {
                cut = Math.Min(room, remaining.Length);
            }

            _text.Append(prefix).Append(remaining[..cut].TrimEnd()).Append('\n');
            remaining = remaining[cut..].TrimStart();
            prefix = nextPrefix;
        }

        _text.Append(prefix).Append(remaining).Append('\n');
    }
}
