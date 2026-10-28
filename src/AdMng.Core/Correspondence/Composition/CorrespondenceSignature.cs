using AdMng.Core.Messaging.Images;

namespace AdMng.Core.Correspondence.Composition;

/// <summary>An Outlook signature ready to append: sanitized HTML fragment with <c>cid:</c> images.</summary>
public sealed record CorrespondenceSignature(string Name, string BodyHtml, IReadOnlyList<MessageImage> Images)
{
    public override string ToString() => $"{nameof(CorrespondenceSignature)} {Name}";
}

/// <summary>A parsed signature and what was dropped while parsing it.</summary>
public sealed record SignatureParseResult(CorrespondenceSignature Signature, IReadOnlyList<string> Problems);

/// <summary>Outlook signatures (<c>.htm</c> files of <c>%APPDATA%\Microsoft\Signatures</c>) as message fragments.</summary>
public static class OutlookSignatureParser
{
    /// <summary>
    /// Parses an Outlook signature (.htm): charset from &lt;meta&gt; else ANSI, CSS of &lt;style&gt; inlined, conditional
    /// comments, VML, o:p and &lt;link&gt; removed, sanitized as a fragment (U1c sanitizer); relative images
    /// ("Name_files/image001.png") read through <paramref name="readRelative"/> and embedded by content id. Unreadable /
    /// invalid images are dropped with a problem.
    /// </summary>
    public static SignatureParseResult Parse(string name, ReadOnlySpan<byte> html, Func<string, byte[]?> readRelative, int ansiCodePage) =>
        throw new NotImplementedException("U5 T1 core");
}
