using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Formatting;

/// <summary>
/// The shared HTML layout of notification e-mails and the event page (section 14.3): inline CSS only, no images, no
/// scripts, a 720 px table layout for Outlook, a severity-coloured banner with white text.
/// </summary>
public static class EmailLayout
{
    /// <summary>Maximum content width in pixels.</summary>
    public const int MaxWidth = 720;

    private const string Font = "font-family:'Segoe UI',Arial,Helvetica,sans-serif;";
    private const string CellPadding = "padding:4px 8px;vertical-align:top;";

    /// <summary>Banner colour of a severity: Critical #b00020, High #d83b01, Medium #ca5010, Low #0f6cbd, Info #616161.</summary>
    public static string Color(Severity severity) => severity switch
    {
        Severity.Critical => "#b00020",
        Severity.High => "#d83b01",
        Severity.Medium => "#ca5010",
        Severity.Low => "#0f6cbd",
        _ => "#616161",
    };

    /// <summary>Opens the document and the centred content table.</summary>
    public static void Begin(HtmlWriter html, string language, string title)
    {
        ArgumentNullException.ThrowIfNull(html);
        html.Doctype()
            .Open("html", ("lang", language))
            .Open("head")
            .Open("meta", ("charset", "utf-8"))
            .Open("meta", ("name", "viewport"), ("content", "width=device-width, initial-scale=1"))
            .Element("title", title)
            .Close()
            .Open("body", ("style", "margin:0;padding:0;background:#f3f3f3;"))
            .Open("table", ("role", "presentation"), ("width", "100%"), ("cellpadding", "0"), ("cellspacing", "0"), ("border", "0"),
                ("style", "background:#f3f3f3;"))
            .Open("tr").Open("td", ("align", "center"), ("style", "padding:16px 8px;"))
            .Open("table", ("role", "presentation"), ("width", MaxWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("cellpadding", "0"), ("cellspacing", "0"), ("border", "0"),
                ("style", $"width:100%;max-width:{MaxWidth}px;background:#ffffff;{Font}font-size:14px;line-height:1.4;color:#201f1e;"));
    }

    /// <summary>Closes what <see cref="Begin"/> opened.</summary>
    public static void End(HtmlWriter html)
    {
        ArgumentNullException.ThrowIfNull(html);
        html.Close().Close().Close().Close().Close().Close();
    }

    /// <summary>The coloured banner row ("KRYTYCZNE · Dostęp uprzywilejowany").</summary>
    public static void Banner(HtmlWriter html, Severity severity, string text)
    {
        ArgumentNullException.ThrowIfNull(html);
        html.Open("tr")
            .Element("td", text, ("bgcolor", Color(severity)),
                ("style", $"background:{Color(severity)};color:#ffffff;padding:12px 16px;font-size:16px;font-weight:bold;"))
            .Close();
    }

    /// <summary>A paragraph row.</summary>
    public static void Paragraph(HtmlWriter html, string? text, string style = "padding:12px 16px 4px 16px;font-size:15px;")
    {
        ArgumentNullException.ThrowIfNull(html);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        html.Open("tr").Open("td", ("style", style)).MultiLineText(text).Close().Close();
    }

    /// <summary>A heading row.</summary>
    public static void Heading(HtmlWriter html, string text)
    {
        ArgumentNullException.ThrowIfNull(html);
        html.Open("tr").Element("td", text, ("style", "padding:16px 16px 4px 16px;font-size:15px;font-weight:bold;border-top:1px solid #e1dfdd;")).Close();
    }

    /// <summary>A label/value table row block.</summary>
    public static void Facts(HtmlWriter html, IEnumerable<AlertFact> facts)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(facts);
        html.Open("tr").Open("td", ("style", "padding:8px 8px;"))
            .Open("table", ("role", "presentation"), ("width", "100%"), ("cellpadding", "0"), ("cellspacing", "0"), ("border", "0"));
        foreach (var fact in facts)
        {
            html.Open("tr")
                .Element("td", fact.Label, ("width", "120"), ("style", CellPadding + "color:#605e5c;font-weight:bold;white-space:nowrap;"))
                .Open("td", ("style", CellPadding)).MultiLineText(fact.Value).Close()
                .Close();
        }

        html.Close().Close().Close();
    }

    /// <summary>A data table with a header row (changes, aggregated items, digest sections).</summary>
    public static void Table(HtmlWriter html, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);
        html.Open("tr").Open("td", ("style", "padding:8px 16px;"))
            .Open("table", ("width", "100%"), ("cellpadding", "0"), ("cellspacing", "0"), ("border", "0"),
                ("style", "border-collapse:collapse;font-size:13px;"))
            .Open("tr");
        foreach (var header in headers)
        {
            html.Element("th", header, ("align", "left"), ("style", CellPadding + "border-bottom:2px solid #c8c6c4;color:#323130;"));
        }

        html.Close();
        foreach (var row in rows)
        {
            html.Open("tr");
            if (row.Count >= 2 && row[1] is null)
            {
                // A single-cell value spanning the remaining columns ("zmieniono (wartość ukryta)").
                html.Open("td", ("style", CellPadding + "border-bottom:1px solid #edebe9;")).MultiLineText(row[0]).Close()
                    .Open("td", ("colspan", (headers.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        ("style", CellPadding + "border-bottom:1px solid #edebe9;color:#605e5c;"))
                    .MultiLineText(row.Count > 2 ? row[2] : null).Close();
            }
            else
            {
                foreach (var cell in row)
                {
                    html.Open("td", ("style", CellPadding + "border-bottom:1px solid #edebe9;")).MultiLineText(cell).Close();
                }
            }

            html.Close();
        }

        html.Close().Close().Close();
    }

    /// <summary>The link row ("Otwórz w przeglądarce: …") and the AD_MNG hint.</summary>
    public static void Links(HtmlWriter html, string? linkLabel, string? link, string hint)
    {
        ArgumentNullException.ThrowIfNull(html);
        html.Open("tr").Open("td", ("style", "padding:12px 16px;"));
        if (link is not null && linkLabel is not null)
        {
            html.Text(linkLabel + ": ").Link(link, link, "color:#0f6cbd;").Open("br");
        }

        html.Text(hint).Close().Close();
    }

    /// <summary>The footer row.</summary>
    public static void Footer(HtmlWriter html, string text)
    {
        ArgumentNullException.ThrowIfNull(html);
        html.Open("tr")
            .Element("td", "── " + text, ("style", "padding:12px 16px;border-top:1px solid #e1dfdd;color:#605e5c;font-size:12px;"))
            .Close();
    }
}
