namespace AdMng.Core.Correspondence.Composition;

/// <summary>The HTML document of a Korespondencja message (MailerWPiA's BuildMessage without a &lt;style&gt; block).</summary>
public static class CorrespondenceDocument
{
    public const string BodyFont = "Calibri,'Segoe UI',Arial,sans-serif";

    public const double BodyPoints = 11;

    /// <summary>
    /// <c>&lt;html&gt;&lt;head&gt;&lt;meta charset=utf-8&gt;&lt;/head&gt;&lt;body&gt;&lt;div
    /// style="font-family:…;font-size:11pt;color:#000000;line-height:1.4"&gt;</c> body (+ "&lt;br&gt;" + signature)
    /// <c>&lt;/div&gt;&lt;/body&gt;&lt;/html&gt;</c>.
    /// </summary>
    public static string Build(string bodyHtml, CorrespondenceSignature? signature) => throw new NotImplementedException("U5 T1 core");

    /// <summary>The inner content of &lt;body&gt; (for previews).</summary>
    public static string BodyOf(string document) => throw new NotImplementedException("U5 T1 core");
}
