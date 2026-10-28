using System.Collections.ObjectModel;
using System.Text;
using AdMng.Core.Correspondence.Fields;
using AdMng.Core.Messaging.Html;
using AdMng.Core.Messaging.Images;

namespace AdMng.Core.Correspondence.Composition;

/// <summary>Merges the draft with the fields of one unit (design note 3.3).</summary>
public static class CorrespondenceRenderer
{
    /// <summary>The list element that wraps the <c>{{lista}}</c> items (MailerWPiA's list margin).</summary>
    public const string ListStart = "<ul style=\"margin:0 0 8pt 0\">";

    /// <summary>
    /// Merges subject (Header) and body (Html) for one unit; groups render <c>{{lista}}</c> as <c>&lt;ul&gt;&lt;li&gt;</c>
    /// of the list item template (Html) per row and <c>{{liczba}}</c> as the row count; the first row gives the other
    /// fields. The body is repaired (<c>FieldTemplate.RepairHtml</c>), merged, wrapped by
    /// <see cref="CorrespondenceDocument.Build"/> and re-sanitized as a document.
    /// </summary>
    /// <remarks>
    /// <para>Every unit is a group (one row when not grouped): <c>{{lista}}</c> lists its rows – empty (and reported as an
    /// empty field) when the list item template is blank or the unit has no rows (a BCC message) – and <c>{{liczba}}</c> is
    /// the number of rows. List items see the row's fields only (<c>{{lista}}</c>/<c>{{liczba}}</c> there name columns, if
    /// any). In the subject
    /// <c>{{lista}}</c> is the text of the items joined by ", ". A paragraph holding <c>{{lista}}</c> is split around it
    /// (a list cannot stand in a paragraph), empty halves dropped.</para>
    /// <para>The signature is appended as parsed (no fields). The sanitizer removes anything outside the message policy –
    /// a script or a <c>javascript:</c> link merged from a value included – and empties <c>&lt;head&gt;</c> (Outlook takes
    /// the body as a Unicode string, no charset needed). <see cref="RenderedCorrespondence.Images"/> holds the draft and
    /// signature images the document references, in order of first reference; a <c>cid:</c> that is not the content id of
    /// one of them is removed.</para>
    /// <para>Unknown and empty fields are reported once each (by <see cref="FieldKey"/>), subject first, then body, then
    /// list items.</para>
    /// </remarks>
    public static RenderedCorrespondence Render(CorrespondenceDraft draft, CorrespondenceUnit unit, IReadOnlyCollection<string> knownColumns)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(knownColumns);
        var rows = unit.Rows ?? [];
        var first = rows.Count > 0 ? rows[0] ?? ReadOnlyDictionary<string, string>.Empty : ReadOnlyDictionary<string, string>.Empty;
        var customFields = draft.CustomFields ?? [];

        // The list items.
        var listUnknown = new List<string>();
        var listEmpty = new List<string>();
        var list = string.Empty;
        var listText = string.Empty;
        var itemTemplate = FieldTemplate.RepairHtml(draft.ListItemTemplate);
        if (!string.IsNullOrWhiteSpace(itemTemplate) && rows.Count > 0)
        {
            var builder = new StringBuilder(ListStart);
            var texts = new List<string>(rows.Count);
            foreach (var row in rows)
            {
                var itemFields = new RecipientFieldSet(unit.Email, unit.Name, row ?? ReadOnlyDictionary<string, string>.Empty, draft.Salutation,
                    customFields, knownColumns);
                var item = FieldTemplate.Merge(itemTemplate, itemFields.Lookup, FieldEncoding.Html, listUnknown, listEmpty);
                builder.Append("<li>").Append(item).Append("</li>");
                texts.Add(HtmlToText.Convert(item).Trim());
            }

            list = builder.Append("</ul>").ToString();
            listText = string.Join(", ", texts.Where(t => t.Length > 0));
        }

        var fields = new RecipientFieldSet(unit.Email, unit.Name, first, draft.Salutation, customFields, knownColumns, list, rows.Count);

        // Subject.
        var unknown = new List<string>();
        var empty = new List<string>();
        var subject = FieldTemplate.Merge(draft.Subject, field => IsList(field) ? listText : fields.Lookup(field), FieldEncoding.Header, unknown, empty);

        // Body.
        var body = FieldTemplate.MergeCore(
            ListPlacement.Prepare(FieldTemplate.RepairHtml(draft.BodyHtml)), fields.Lookup, FieldEncoding.Html, unknown, empty, raw: IsList);
        var images = new Dictionary<string, MessageImage>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in (draft.Images ?? ReadOnlyDictionary<string, MessageImage>.Empty).Values)
        {
            if (image is not null)
            {
                images.TryAdd(image.ContentId, image);   // the id Outlook gets (a key that differs would break the image)
            }
        }

        foreach (var image in draft.Signature?.Images ?? [])
        {
            images.TryAdd(image.ContentId, image);
        }

        var result = MessageHtmlSanitizer.Sanitize(CorrespondenceDocument.Build(body, draft.Signature), new HtmlSanitizeOptions
        {
            Mode = HtmlSanitizeMode.Document,
            InlineImageIds = images.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
        });
        var referenced = result.ImageIds
            .Select(id => images.TryGetValue(id, out var image) ? image : null)
            .OfType<MessageImage>()
            .DistinctBy(i => i.ContentId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new RenderedCorrespondence(
            subject.Trim(),
            result.Html,
            referenced,
            Distinct(unknown.Concat(listUnknown)),
            Distinct(empty.Concat(listEmpty)));
    }

    /// <summary>The text of a document (<c>HtmlToText.Convert</c>) for log-free previews and tests.</summary>
    public static string PlainText(string htmlDocument) => HtmlToText.Convert(htmlDocument);

    private static bool IsList(string field) => FieldKey.Of(field) == BuiltInFields.List;

    private static List<string> Distinct(IEnumerable<string> names)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        return [.. names.Where(n => keys.Add(FieldKey.Of(n)))];
    }
}
