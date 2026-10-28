namespace AdMng.Core.Correspondence.Composition;

/// <summary>Merges the draft with the fields of one unit (design note 3.3).</summary>
public static class CorrespondenceRenderer
{
    /// <summary>
    /// Merges subject (Header) and body (Html) for one unit; groups render <c>{{lista}}</c> as <c>&lt;ul&gt;&lt;li&gt;</c>
    /// of the list item template (Html) per row and <c>{{liczba}}</c> as the row count; the first row gives the other
    /// fields. The body is repaired (<c>FieldTemplate.RepairHtml</c>), merged, wrapped by
    /// <see cref="CorrespondenceDocument.Build"/> and re-sanitized as a document.
    /// </summary>
    public static RenderedCorrespondence Render(CorrespondenceDraft draft, CorrespondenceUnit unit, IReadOnlyCollection<string> knownColumns) =>
        throw new NotImplementedException("U5 T1 core");

    /// <summary>The text of a document (<c>HtmlToText.Convert</c>) for log-free previews and tests.</summary>
    public static string PlainText(string htmlDocument) => throw new NotImplementedException("U5 T1 core");
}
