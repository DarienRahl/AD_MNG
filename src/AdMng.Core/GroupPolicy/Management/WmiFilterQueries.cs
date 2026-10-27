using System.Globalization;
using System.Text;

namespace AdMng.Core.GroupPolicy.Management;

/// <summary>
/// <c>msWMI-Parm2</c> of a WMI filter (design note docs/dev/k-computers-and-gpo.md 5.3): the number of queries, then per
/// query its type (3), the length of the namespace, the length of the query, the language, the namespace and the query,
/// each followed by <c>;</c> -
/// <c>1;3;10;58;WQL;root\CIMv2;SELECT * FROM Win32_OperatingSystem WHERE ProductType = 1;</c>. The lengths are counted in
/// UTF-16 characters, so a query may itself contain <c>;</c>. Also the WMI filter dates.
/// </summary>
public static class WmiFilterQueries
{
    /// <summary>The query type GPMC writes.</summary>
    public const int QueryType = 3;

    /// <summary>The default namespace.</summary>
    public const string DefaultNamespace = @"root\CIMv2";

    /// <summary>The queries; null when the value is not in this format.</summary>
    public static IReadOnlyList<WmiFilterQuery>? Parse(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return [];
        }

        var position = 0;
        if (!TryReadNumber(value, ref position, out var count) || count < 0 || count > 100)
        {
            return null;
        }

        var queries = new List<WmiFilterQuery>(count);
        for (var i = 0; i < count; i++)
        {
            if (!TryReadNumber(value, ref position, out _)
                || !TryReadNumber(value, ref position, out var namespaceLength)
                || !TryReadNumber(value, ref position, out var queryLength)
                || !TryReadField(value, ref position, out var language)
                || !TryReadFixed(value, ref position, namespaceLength, out var @namespace)
                || !TryReadFixed(value, ref position, queryLength, out var query))
            {
                return null;
            }

            queries.Add(new WmiFilterQuery(@namespace, query, language));
        }

        return queries;
    }

    /// <summary>The value for the queries.</summary>
    public static string Format(IReadOnlyList<WmiFilterQuery> queries)
    {
        ArgumentNullException.ThrowIfNull(queries);
        var sb = new StringBuilder();
        sb.Append(queries.Count.ToString(CultureInfo.InvariantCulture)).Append(';');
        foreach (var query in queries)
        {
            var language = string.IsNullOrWhiteSpace(query.Language) ? "WQL" : query.Language;
            sb.Append(QueryType.ToString(CultureInfo.InvariantCulture)).Append(';')
                .Append(query.Namespace.Length.ToString(CultureInfo.InvariantCulture)).Append(';')
                .Append(query.Query.Length.ToString(CultureInfo.InvariantCulture)).Append(';')
                .Append(language).Append(';')
                .Append(query.Namespace).Append(';')
                .Append(query.Query).Append(';');
        }

        return sb.ToString();
    }

    /// <summary>A WMI filter date: <c>yyyyMMddHHmmss.ffffff-000</c> (UTC).</summary>
    public static string FormatDate(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("yyyyMMddHHmmss.ffffff", CultureInfo.InvariantCulture) + "-000";

    /// <summary>A WMI filter date, null when it cannot be parsed.</summary>
    public static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 14)
        {
            return null;
        }

        return DateTime.TryParseExact(value[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)
            ? new DateTimeOffset(time, TimeSpan.Zero)
            : null;
    }

    private static bool TryReadNumber(string value, ref int position, out int number)
    {
        number = 0;
        return TryReadField(value, ref position, out var text)
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private static bool TryReadField(string value, ref int position, out string field)
    {
        field = string.Empty;
        var end = value.IndexOf(';', position);
        if (end < 0)
        {
            return false;
        }

        field = value[position..end];
        position = end + 1;
        return true;
    }

    private static bool TryReadFixed(string value, ref int position, int length, out string field)
    {
        field = string.Empty;
        if (length < 0 || position + length >= value.Length || value[position + length] != ';')
        {
            return false;
        }

        field = value.Substring(position, length);
        position += length + 1;
        return true;
    }
}
