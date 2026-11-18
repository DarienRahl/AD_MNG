namespace AdMng.App.PowerShell;

/// <summary>
/// The text of the editor in two spellings: a <c>TextBox</c> breaks lines with <c>\r\n</c> (what Enter and a paste put into it), the
/// model of the line editor with <c>\n</c>, and a caret is an index into either. These convert the text and the caret between them, so
/// that the highlighting and the history of the model and the caret of the box always talk about the same place. Pure.
/// </summary>
internal static class PowerShellEditorText
{
    /// <summary>The text of a box as the model keeps it: every line break is <c>\n</c>.</summary>
    public static string ToModel(string boxText)
    {
        ArgumentNullException.ThrowIfNull(boxText);
        return boxText.Contains('\r', StringComparison.Ordinal) ? boxText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') : boxText;
    }

    /// <summary>The index in the model's text that the caret of the box stands at (a <c>\r</c> before a <c>\n</c> is not a character of the model).</summary>
    public static int ToModelIndex(string boxText, int boxIndex)
    {
        ArgumentNullException.ThrowIfNull(boxText);
        boxIndex = Math.Clamp(boxIndex, 0, boxText.Length);
        var removed = 0;
        for (var i = 0; i < boxIndex; i++)
        {
            if (boxText[i] == '\r' && i + 1 < boxText.Length && boxText[i + 1] == '\n')
            {
                removed++;
            }
        }

        return boxIndex - removed;
    }

    /// <summary>The index in the box's text that stands for <paramref name="modelIndex"/> of the model's text (the box text is the model text with <c>\r\n</c> or <c>\n</c> breaks).</summary>
    public static int ToBoxIndex(string boxText, int modelIndex)
    {
        ArgumentNullException.ThrowIfNull(boxText);
        var consumed = 0;
        for (var i = 0; i < boxText.Length; i++)
        {
            if (consumed >= modelIndex)
            {
                return i;
            }

            if (boxText[i] == '\r' && i + 1 < boxText.Length && boxText[i + 1] == '\n')
            {
                continue;
            }

            consumed++;
        }

        return boxText.Length;
    }
}
