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
    /// of the list item template (plain text, HTML-encoded; values Html) per row and <c>{{liczba}}</c> as the row count;
    /// the first row gives the other fields. The body is repaired (<c>FieldTemplate.RepairHtml</c>), merged, wrapped by
    /// <see cref="CorrespondenceDocument.Build"/> and re-sanitized as a document.
    /// </summary>
    /// <remarks>
    /// <para>Every unit is a group (one row when not grouped): <c>{{lista}}</c> lists its rows – empty (and reported as an
    /// empty field) when the list item template is blank or the unit has no rows (a BCC message) – and <c>{{liczba}}</c> is
    /// the number of rows. Without grouping by address (<see cref="CorrespondenceDraft.GroupByAddress"/>) a column or a
    /// custom field named "Lista" or "Liczba" wins over the built-in of that name (<see cref="BuiltInApplies"/>; "Wstaw
    /// pole" offers the column then, the built-ins only with grouping). List items see the row's fields only
    /// (<c>{{lista}}</c>/<c>{{liczba}}</c> there name columns, if any); they are rendered (and their fields reported) only
    /// when the subject or the body uses <c>{{lista}}</c>, or a custom field they use does. In the subject and inside a
    /// custom field's value <c>{{lista}}</c> is the text of the items joined by ", ". A paragraph holding <c>{{lista}}</c>
    /// is split around it (a list cannot stand in a paragraph), empty halves dropped.</para>
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
        var listApplies = BuiltInApplies(BuiltInFields.List, draft, unit, knownColumns);
        var countApplies = BuiltInApplies(BuiltInFields.Count, draft, unit, knownColumns);
        bool IsBuiltInList(string field) => listApplies && IsList(field);

        // The list items (only when the message lists them – directly, or through a custom field it uses).
        var repaired = FieldTemplate.RepairHtml(draft.BodyHtml);
        var bodyTemplate = listApplies ? ListPlacement.Prepare(repaired) : repaired;
        var customUsesList = listApplies && CustomFieldsUseList(customFields, draft.Subject, bodyTemplate);
        var subjectUsesList = listApplies && UsesList(draft.Subject);
        var listUnknown = new List<string>();
        var listEmpty = new List<string>();
        var list = string.Empty;
        var listText = string.Empty;
        var itemTemplate = FieldTemplate.TextToHtml(draft.ListItemTemplate);   // plain text, as typed in its text box
        if (listApplies && !string.IsNullOrWhiteSpace(itemTemplate) && rows.Count > 0 && (subjectUsesList || customUsesList || UsesList(bodyTemplate)))
        {
            var builder = new StringBuilder(ListStart);
            var texts = new List<string>(rows.Count);
            foreach (var row in rows)
            {
                var itemFields = new RecipientFieldSet(unit.Email, unit.Name, row ?? ReadOnlyDictionary<string, string>.Empty, draft.Salutation,
                    customFields, knownColumns);
                var item = FieldTemplate.Merge(itemTemplate, itemFields.Lookup, FieldEncoding.Html, listUnknown, listEmpty);
                builder.Append("<li>").Append(item).Append("</li>");
                if (subjectUsesList || customUsesList)
                {
                    texts.Add(HtmlToText.Convert(item).Trim());
                }
            }

            list = builder.Append("</ul>").ToString();
            listText = string.Join(", ", texts.Where(t => t.Length > 0));
        }

        var fields = new RecipientFieldSet(unit.Email, unit.Name, first, draft.Salutation, customFields, knownColumns,
            listApplies ? list : null, countApplies ? rows.Count : null, listText);

        // Subject.
        var unknown = new List<string>();
        var empty = new List<string>();
        var subject = FieldTemplate.Merge(draft.Subject, field => IsBuiltInList(field) ? listText : fields.Lookup(field), FieldEncoding.Header, unknown, empty);

        // Body (the built-in list is HTML already; a column named "Lista" is encoded like any value).
        var body = FieldTemplate.MergeCore(bodyTemplate, fields.Lookup, FieldEncoding.Html, unknown, empty, raw: IsBuiltInList);
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

        var result = MessageHtmlSanitizer.Sanitize(CorrespondenceDocument.Build(body, draft.Signature, draft.BaseStyle), new HtmlSanitizeOptions
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

    /// <summary>
    /// Whether the built-in <paramref name="field"/> (<c>lista</c> or <c>liczba</c>) names the unit's rows: always in messages
    /// grouped by address; otherwise only when no column of the list (or of the unit's first row) and no custom field has
    /// that name – a file's "Liczba" column keeps its value in one-row messages, as "Wstaw pole" offers it there.
    /// </summary>
    public static bool BuiltInApplies(string field, CorrespondenceDraft draft, CorrespondenceUnit unit, IReadOnlyCollection<string> knownColumns)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(knownColumns);
        if (draft.GroupByAddress)
        {
            return true;
        }

        var key = FieldKey.Of(field);
        return !knownColumns.Any(c => FieldKey.Of(c) == key)
            && !(unit.Rows is { Count: > 0 } rows && rows[0] is { } first && first.Keys.Any(c => FieldKey.Of(c) == key))
            && !(draft.CustomFields ?? []).Any(f => f is not null && FieldKey.Of(f.Name) == key);
    }

    private static bool IsList(string field) => FieldKey.Of(field) == BuiltInFields.List;

    private static bool UsesList(string? template) => FieldTemplate.Parse(template).Any(t => IsList(t.Field));

    /// <summary>A text custom field the subject or the body uses has <c>{{lista}}</c> in its value (it gets the list as
    /// text; a date field's value is a date).</summary>
    private static bool CustomFieldsUseList(IReadOnlyList<CustomFieldDefinition> customFields, string? subject, string body)
    {
        var listing = customFields.Where(f => f is not null && f.Kind != CustomFieldKind.Date && UsesList(f.Value)).Select(f => FieldKey.Of(f.Name)).Where(k => k.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        return listing.Count > 0 && FieldTemplate.Parse(subject).Concat(FieldTemplate.Parse(body)).Any(t => listing.Contains(FieldKey.Of(t.Field)));
    }

    private static List<string> Distinct(IEnumerable<string> names)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        return [.. names.Where(n => keys.Add(FieldKey.Of(n)))];
    }
}
