using System.Xml.Linq;

namespace AdMng.Watchdog.Core.Gpo.Parsers;

/// <summary>
/// Policy comments: <c>Machine|User/comment.cmtx</c> (XML, namespace <c>http://www.microsoft.com/GroupPolicy/CommentDefinitions</c>:
/// <c>policyNamespaces/using</c> prefixes, <c>comments/admTemplate/comment</c> with <c>policyRef</c> and <c>commentText</c>, the
/// text usually a <c>$(resource.id)</c> reference into <c>resources/stringTable/string</c>) and the GPO comment <c>GPO.cmt</c>
/// (UTF-16LE text). Keys <c>Computer/Comment/&lt;namespace&gt;:&lt;policy&gt;</c>, <c>User/Comment/…</c>, <c>Gpo/Comment</c>;
/// each comment is cut to 200 characters (8.4).
/// </summary>
public static class PolicyCommentParser
{
    public static void ParseCmtx(ReadOnlySpan<byte> content, GpoScope scope, GpoSettingSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var root = GpoXml.Load(content).Root!;
        var namespaces = Descendants(root, "using")
            .Select(u => (Prefix: GpoXml.Attribute(u, "prefix"), Namespace: GpoXml.Attribute(u, "namespace")))
            .Where(u => !string.IsNullOrEmpty(u.Prefix) && u.Namespace is not null)
            .GroupBy(u => u.Prefix!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Namespace!, StringComparer.Ordinal);
        var strings = Descendants(root, "string")
            .Select(s => (Id: GpoXml.Attribute(s, "id"), Text: s.Value))
            .Where(s => !string.IsNullOrEmpty(s.Id))
            .GroupBy(s => s.Id!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Text, StringComparer.Ordinal);
        var prefix = (scope == GpoScope.User ? "User" : "Computer") + "/Comment/";
        foreach (var comment in Descendants(root, "comment"))
        {
            var policyRef = GpoXml.Attribute(comment, "policyRef");
            if (string.IsNullOrWhiteSpace(policyRef))
            {
                continue;
            }

            var colon = policyRef.IndexOf(':', StringComparison.Ordinal);
            var policy = colon > 0 && namespaces.TryGetValue(policyRef[..colon], out var ns) ? ns + policyRef[colon..] : policyRef;
            var text = GpoXml.Attribute(comment, "commentText") ?? string.Empty;
            if (text.StartsWith("$(resource.", StringComparison.Ordinal) && text.EndsWith(')'))
            {
                text = strings.TryGetValue(text[11..^1], out var resolved) ? resolved : string.Empty;
            }

            sink.Text(prefix + policy, GpoValues.Truncate(text), GpoSettingCategory.Comments);
        }
    }

    public static void ParseGpoComment(ReadOnlySpan<byte> content, GpoSettingSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var text = GpoText.DecodeUnicodeText(content);
        if (text.Trim().Length > 0)
        {
            sink.Text("Gpo/Comment", GpoValues.Truncate(text), GpoSettingCategory.Comments);
        }
    }

    private static IEnumerable<XElement> Descendants(XElement root, string localName) =>
        root.Descendants().Where(e => e.Name.LocalName.Equals(localName, StringComparison.Ordinal));
}
