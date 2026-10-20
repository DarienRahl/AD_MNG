using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AdMng.Watchdog.Core.Gpo.Parsers;

/// <summary>
/// Hardened XML loading for GPO files (stage B design 8.4): <c>DtdProcessing.Prohibit</c> (no entity expansion – "billion
/// laughs" fails), <c>XmlResolver = null</c> (no external entities), character checking off (invalid characters reach the
/// values and become U+FFFD there), a character limit and a nesting limit checked in a first streaming pass before the tree
/// is built. The encoding comes from the BOM / declaration (UTF-8 with BOM for GPP files and comment.cmtx).
/// </summary>
public static class GpoXml
{
    /// <summary>Deepest element nesting accepted (GPP task definitions are about 8 levels deep).</summary>
    public const int MaxDepth = 64;

    /// <summary>Most characters of one document (the per-file byte limit applies before).</summary>
    public const long MaxCharacters = 16L * 1024 * 1024;

    public static XDocument Load(ReadOnlySpan<byte> content)
    {
        var bytes = content.ToArray();
        try
        {
            using (var reader = XmlReader.Create(new MemoryStream(bytes, writable: false), Settings()))
            {
                while (reader.Read())
                {
                    if (reader.Depth > MaxDepth)
                    {
                        throw new GpoParseException("The XML document is nested too deeply.");
                    }
                }
            }

            using var treeReader = XmlReader.Create(new MemoryStream(bytes, writable: false), Settings());
            var document = XDocument.Load(treeReader, LoadOptions.None);
            return document.Root is null ? throw new GpoParseException("The XML document has no root element.") : document;
        }
        catch (XmlException e)
        {
            throw new GpoParseException("The XML document is not well-formed or uses a prohibited feature.", e);
        }
        catch (DecoderFallbackException e)
        {
            throw new GpoParseException("The XML document is not valid in its declared encoding.", e);
        }
    }

    /// <summary>The attribute value or null (local name, case-insensitive – GPP writers differ in case).</summary>
    public static string? Attribute(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(a => !a.IsNamespaceDeclaration && a.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>The first child element with the local name (case-insensitive).</summary>
    public static XElement? Child(XElement element, string localName) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));

    private static XmlReaderSettings Settings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        CheckCharacters = false,
        MaxCharactersInDocument = MaxCharacters,
        MaxCharactersFromEntities = 1024,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        CloseInput = true,
    };
}
