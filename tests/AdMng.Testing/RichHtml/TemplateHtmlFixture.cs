using System.Text;

namespace AdMng.Testing.RichHtml;

/// <summary>
/// Template bodies of Korespondencja for the sanitizer and editor round-trip tests (embedded resources
/// <c>RichHtml/Templates/{name}.html</c>, UTF-8, LF line ends). <see cref="MailboxStillNeeded"/> is the body of the
/// built-in "mailbox still needed" template: an e-mail layout of borderless tables, boxes with a coloured left border, a
/// background and a padding, Word's paragraph spacing (<c>mso-margin-top-alt</c>), margins on <c>div</c>, <c>p</c>,
/// <c>ol</c> and <c>li</c>, a styled link and the fields <c>{{Termin}}</c> and <c>{{podpis}}</c>.
/// </summary>
public static class TemplateHtmlFixture
{
    /// <summary>The body of the built-in template asking whether a mailbox is still needed (one message per mailbox).</summary>
    public static string MailboxStillNeeded => Resource("mailbox-still-needed");

    private static string Resource(string name)
    {
        var suffix = $".RichHtml.Templates.{name}.html";
        var assembly = typeof(TemplateHtmlFixture).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException(resource);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }
}
