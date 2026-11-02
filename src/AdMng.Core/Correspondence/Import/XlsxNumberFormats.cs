using System.Globalization;
using System.Text;
using System.Xml;

namespace AdMng.Core.Correspondence.Import;

/// <summary>
/// The number formats of an XLSX workbook (<c>xl/styles.xml</c>: <c>cellXfs</c> and <c>numFmts</c>), applied to numeric
/// cells so a field holds what the operator sees in Excel rather than the stored number: a date (an Excel serial such as
/// 46096) becomes "15.03.2026" (with the time "15.03.2026 10:00", a time alone "10:00"), a whole number in a format of
/// digit placeholders and literals becomes that text ("00-000" turns 95020 into "95-020", "00000000000" keeps the leading
/// zero of a PESEL). Any other format – decimals, thousands, currency, percent, scientific, text, elapsed time – leaves
/// the stored value as it is (MailerWPiA's "numbers as text").
/// </summary>
/// <remarks>Dates are written the Polish way (day.month.year, 24-hour clock) whatever the format's order or month names:
/// the letters are Polish and the stored number holds no culture.</remarks>
internal sealed class XlsxNumberFormats
{
    private const int MaxStyles = 65_536;
    private const int MaxCodeLength = 255;
    private const double MaxSerial = 2_958_466;   // 10000-01-01

    private readonly List<CellFormat> _styles;
    private readonly bool _date1904;

    private XlsxNumberFormats(List<CellFormat> styles, bool date1904)
    {
        _styles = styles;
        _date1904 = date1904;
    }

    private enum Kind
    {
        Raw,
        Date,
        Digits,
    }

    /// <summary>Reads <c>cellXfs</c> and <c>numFmts</c> of the styles part (null: no styles).</summary>
    public static XlsxNumberFormats Read(XmlReader? styles, bool date1904)
    {
        if (styles is null)
        {
            return new XlsxNumberFormats([], date1904);
        }

        var codes = new Dictionary<int, string>();
        var xfIds = new List<int>();
        var cellXfsDepth = -1;
        while (styles.Read())
        {
            if (styles.NodeType == XmlNodeType.EndElement && styles.LocalName == "cellXfs" && styles.Depth == cellXfsDepth)
            {
                cellXfsDepth = -1;
                continue;
            }

            if (styles.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (styles.LocalName)
            {
                case "numFmt" when codes.Count < MaxStyles && Number(styles.GetAttribute("numFmtId")) is { } id
                    && styles.GetAttribute("formatCode") is { Length: <= MaxCodeLength } code:
                    codes[id] = code;   // a crafted part cannot make the table grow without bound (Excel: ≤ 255 characters)
                    break;
                case "cellXfs" when !styles.IsEmptyElement:
                    cellXfsDepth = styles.Depth;
                    break;
                case "xf" when cellXfsDepth >= 0 && styles.Depth == cellXfsDepth + 1 && xfIds.Count < MaxStyles:
                    xfIds.Add(Number(styles.GetAttribute("numFmtId")) ?? 0);
                    break;
            }
        }

        var formats = new Dictionary<int, CellFormat>();
        var result = new List<CellFormat>(xfIds.Count);
        foreach (var id in xfIds)
        {
            if (!formats.TryGetValue(id, out var format))
            {
                format = codes.TryGetValue(id, out var code) ? Classify(code) : BuiltIn(id);
                formats[id] = format;
            }

            result.Add(format);
        }

        return new XlsxNumberFormats(result, date1904);
    }

    /// <summary>The text of a numeric cell of style <paramref name="style"/> (the index of its <c>s</c> attribute).</summary>
    public string Apply(string value, string? style)
    {
        if (_styles.Count == 0 || Number(style) is not { } index || index >= _styles.Count
            || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
        {
            return value;
        }

        var format = _styles[index];
        switch (format.Kind)
        {
            case Kind.Date when number >= 0 && number < MaxSerial:
                return Date(number, format) ?? value;
            case Kind.Digits when number >= 0 && number < 1e15 && number == Math.Floor(number):
                return ((long)number).ToString(format.Pattern, CultureInfo.InvariantCulture);
            default:
                return value;
        }
    }

    /// <summary>The serial date as text, or null when it is out of range.</summary>
    private string? Date(double serial, CellFormat format)
    {
        try
        {
            DateTime moment;
            if (_date1904)
            {
                moment = new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Unspecified).AddDays(serial);
            }
            else
            {
                // Excel counts 29 February 1900 (serial 60), which never was: serials below 61 are a day later than OLE dates.
                moment = serial < 61 ? new DateTime(1899, 12, 31, 0, 0, 0, DateTimeKind.Unspecified).AddDays(serial) : DateTime.FromOADate(serial);
            }

            var seconds = (long)Math.Round(moment.Ticks / (double)TimeSpan.TicksPerSecond);
            return new DateTime(seconds * TimeSpan.TicksPerSecond, DateTimeKind.Unspecified).ToString(format.Pattern, CultureInfo.InvariantCulture);
        }
        catch (ArgumentException)
        {
            return null;   // beyond 9999-12-31
        }
    }

    /// <summary>The built-in formats Excel writes without a code: short and medium dates (14–17), times (18–21), date and
    /// time (22); every other built-in (General, numbers, currency, percent, elapsed time) shows the stored value.</summary>
    private static CellFormat BuiltIn(int id) => id switch
    {
        14 or 15 => new CellFormat(Kind.Date, "dd.MM.yyyy"),
        16 => new CellFormat(Kind.Date, "dd.MM"),
        17 => new CellFormat(Kind.Date, "MM.yyyy"),
        18 or 20 => new CellFormat(Kind.Date, "HH:mm"),
        19 or 21 => new CellFormat(Kind.Date, "HH:mm:ss"),
        22 => new CellFormat(Kind.Date, "dd.MM.yyyy HH:mm"),
        _ => new CellFormat(Kind.Raw, string.Empty),
    };

    /// <summary>What a format code shows: a date or time, whole digits in a pattern, or the stored value.</summary>
    private static CellFormat Classify(string code)
    {
        var section = FirstSection(code);
        if (DatePattern(section) is { } date)
        {
            return new CellFormat(Kind.Date, date);
        }

        return DigitsPattern(section) is { } digits ? new CellFormat(Kind.Digits, digits) : new CellFormat(Kind.Raw, string.Empty);
    }

    /// <summary>The first section of a format code (up to a ';' outside quotes).</summary>
    private static string FirstSection(string code)
    {
        var quoted = false;
        for (var i = 0; i < code.Length; i++)
        {
            switch (code[i])
            {
                case '"':
                    quoted = !quoted;
                    break;
                case '\\' when !quoted:
                    i++;
                    break;
                case ';' when !quoted:
                    return code[..i];
            }
        }

        return code;
    }

    /// <summary>The .NET pattern of a date or time code: the parts it shows (day, month, year; hours, minutes, seconds) the
    /// Polish way; null when the code shows no date part, or elapsed time (<c>[h]</c>).</summary>
    private static string? DatePattern(string section)
    {
        bool day = false, month = false, year = false, hour = false, second = false;
        for (var i = 0; i < section.Length; i++)
        {
            var c = section[i];
            switch (c)
            {
                case '"':
                    var close = section.IndexOf('"', i + 1);
                    i = close < 0 ? section.Length : close;
                    continue;
                case '\\' or '_' or '*':
                    i++;
                    continue;
                case '[':
                    var end = section.IndexOf(']', i + 1);
                    var inner = end < 0 ? string.Empty : section[(i + 1)..end];
                    if (inner.Length > 0 && inner.All(ch => ch is 'h' or 'H' or 'm' or 'M' or 's' or 'S'))
                    {
                        return null;   // elapsed time ([h]:mm) is a duration, not a moment
                    }

                    i = end < 0 ? section.Length : end;
                    continue;
            }

            switch (char.ToLowerInvariant(c))
            {
                case 'd':
                    day = true;
                    break;
                case 'y':
                    year = true;
                    break;
                case 'm':
                    month = true;
                    break;
                case 'h':
                    hour = true;
                    break;
                case 's':
                    second = true;
                    break;
                case '0' or '#' or '?' or '@' or '%':
                    return null;   // a number or text placeholder: not a date code
            }
        }

        var time = hour || second;
        var date = day || year || (month && !time);
        if (!date && !time)
        {
            return null;
        }

        var datePart = (day, year) switch
        {
            (true, true) => "dd.MM.yyyy",
            (false, true) => "MM.yyyy",
            (true, false) => "dd.MM",
            _ => month ? "MM" : string.Empty,
        };
        var timePart = !time ? string.Empty : second ? "HH:mm:ss" : "HH:mm";
        return date && time ? datePart + " " + timePart : date ? datePart : timePart;
    }

    /// <summary>The .NET pattern of a code made only of digit placeholders (<c>0</c>, <c>#</c>) and literal text (a quoted
    /// string, an escaped character, a space, <c>- / ( ) + :</c>); null for anything else.</summary>
    private static string? DigitsPattern(string section)
    {
        var pattern = new StringBuilder();
        var placeholders = 0;
        for (var i = 0; i < section.Length; i++)
        {
            var c = section[i];
            switch (c)
            {
                case '0' or '#':
                    pattern.Append(c);
                    placeholders++;
                    break;
                case '"':
                    var close = section.IndexOf('"', i + 1);
                    if (close < 0)
                    {
                        return null;
                    }

                    foreach (var literal in section[(i + 1)..close])
                    {
                        pattern.Append('\\').Append(literal);
                    }

                    i = close;
                    break;
                case '\\' when i + 1 < section.Length:
                    pattern.Append('\\').Append(section[++i]);
                    break;
                case ' ' or '-' or '/' or '(' or ')' or '+' or ':':
                    pattern.Append('\\').Append(c);
                    break;
                default:
                    return null;   // decimals, thousands, percent, colours, conditions, text …
            }
        }

        return placeholders > 0 ? pattern.ToString() : null;
    }

    private static int? Number(string? text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;

    private readonly record struct CellFormat(Kind Kind, string Pattern);
}
