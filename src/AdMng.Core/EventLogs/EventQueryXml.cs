using System.Buffers;
using System.Globalization;
using System.Text;
using System.Xml;

namespace AdMng.Core.EventLogs;

/// <summary>
/// <see cref="EventLogQuery"/> → Windows structured query XML (design note e-event-logs.md 2, CLAUDE.md rule 3), e.g.
/// <c>&lt;QueryList&gt;&lt;Query Id="0" Path="Security"&gt;&lt;Select Path="Security"&gt;*[System[(EventID=4740 or
/// EventID=4771) and band(Keywords,4503599627370496) and TimeCreated[@SystemTime&amp;gt;='2026-11-01T00:00:00.000Z' and
/// @SystemTime&amp;lt;='2026-11-02T00:00:00.000Z']] and EventData[Data[@Name='TargetUserName']='jan.kowalski']]&lt;/Select&gt;
/// &lt;/Query&gt;&lt;/QueryList&gt;</c>. The XPath is built, never concatenated from operator text.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>At most <see cref="MaxEventIdsPerSelect"/> <c>EventID</c> terms per <c>Select</c> (the event log service
/// rejects more complex expressions); more IDs give several Selects, which the service ORs; each repeats the other
/// conditions.</item>
/// <item><c>Level</c>, <c>Provider[@Name=…]</c> and the values of one data condition are OR-ed, everything else AND-ed.
/// Levels are taken as given – Event Viewer's "Information" is <see cref="EventLogLevels.LogAlways"/> and
/// <see cref="EventLogLevels.Information"/> (Security audits are level 0).</item>
/// <item>The outcome is <c>band(Keywords, …)</c> with <see cref="EventLogRecord.AuditSuccessKeyword"/> or
/// <see cref="EventLogRecord.AuditFailureKeyword"/>.</item>
/// <item>Time bounds are inclusive UTC with milliseconds: <see cref="EventLogQuery.From"/> is rounded down and
/// <see cref="EventLogQuery.To"/> up to the millisecond (<see cref="TimeBounds"/>), so no record inside the bounds is
/// lost to the rounding.</item>
/// <item>The channel is a name of <c>[A-Za-z0-9 ._\-/]</c> (no file paths); data names follow
/// <see cref="XPathLiteral.IsAllowedName"/>; data values and provider names are one <see cref="XPathLiteral"/>.</item>
/// <item><see cref="EventLogQuery.RawXPath"/> (expert mode) replaces the whole generated selection – IDs, levels,
/// providers, outcome, data and the time bounds – after <see cref="IsValidRawXPath"/>; the target's own validation
/// decides the rest. <see cref="EventLogQuery.NewestFirst"/> and <see cref="EventLogQuery.MaxRecords"/> are read
/// options, not part of the XML.</item>
/// </list>
/// Invalid input throws <see cref="ArgumentException"/> whose message carries no value.
/// </remarks>
public static class EventQueryXml
{
    /// <summary>EventID terms per Select.</summary>
    public const int MaxEventIdsPerSelect = 20;

    /// <summary>The longest raw XPath accepted.</summary>
    public const int MaxRawXPathLength = 4096;

    /// <summary>The longest channel name accepted.</summary>
    public const int MaxChannelLength = 256;

    /// <summary>Nesting of brackets and parentheses a raw XPath may use.</summary>
    public const int MaxRawXPathDepth = 32;

    /// <summary>The structured query of <paramref name="query"/>.</summary>
    /// <exception cref="ArgumentException">The channel, an event ID, a data name, a data value or a provider name is not
    /// allowed, a data condition has no value, <c>From</c> is after <c>To</c>, or the raw XPath fails
    /// <see cref="IsValidRawXPath"/>; the message names the rule, never the value.</exception>
    public static string Build(EventLogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!IsAllowedChannel(query.Channel))
        {
            throw new ArgumentException("The channel name is empty, too long or contains characters outside [A-Za-z0-9 ._-/].", nameof(query));
        }

        List<string> selects;
        if (query.RawXPath is { } raw)
        {
            if (!IsValidRawXPath(raw, out _))
            {
                throw new ArgumentException("The XPath expression did not pass the structural check.", nameof(query));
            }

            selects = [raw.Trim()];
        }
        else
        {
            selects = SelectExpressions(query);
        }

        var builder = new StringBuilder();
        using (var writer = XmlWriter.Create(builder, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = false }))
        {
            writer.WriteStartElement("QueryList");
            writer.WriteStartElement("Query");
            writer.WriteAttributeString("Id", "0");
            writer.WriteAttributeString("Path", query.Channel);
            foreach (var select in selects)
            {
                writer.WriteStartElement("Select");
                writer.WriteAttributeString("Path", query.Channel);
                writer.WriteString(select);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        return builder.ToString();
    }

    /// <summary>True when <paramref name="channel"/> may be queried: 1–<see cref="MaxChannelLength"/> characters of
    /// <c>[A-Za-z0-9 ._\-/]</c>, not starting or ending with a space (<c>Security</c>,
    /// <c>Microsoft-Windows-GroupPolicy/Operational</c>, <c>Directory Service</c>). File paths are not channels.</summary>
    public static bool IsAllowedChannel(string? channel) =>
        !string.IsNullOrEmpty(channel) && channel.Length <= MaxChannelLength && channel[0] != ' ' && channel[^1] != ' '
        && channel.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '_' or '-' or '/');

    /// <summary>
    /// The inclusive bounds the query XML uses (and <see cref="EventLogMatcher"/> applies): <paramref name="from"/> rounded
    /// down and <paramref name="to"/> rounded up to the millisecond, in UTC.
    /// </summary>
    public static (DateTimeOffset? From, DateTimeOffset? To) TimeBounds(DateTimeOffset? from, DateTimeOffset? to)
    {
        DateTimeOffset? lower = from is { } f ? Floor(f.ToUniversalTime()) : null;
        DateTimeOffset? upper = null;
        if (to is { } t)
        {
            var utc = t.ToUniversalTime();
            var floor = Floor(utc);
            upper = floor == utc || floor.UtcTicks > DateTimeOffset.MaxValue.UtcTicks - TimeSpan.TicksPerMillisecond ? floor : floor.AddMilliseconds(1);
        }

        return (lower, upper);
    }

    /// <summary>
    /// The structural check of an operator-typed XPath (expert mode): one expression selecting events (<c>*[…]</c> or
    /// <c>Event[…]</c>) built only from element names (<c>[A-Za-z0-9_]</c>, e.g. <c>System</c>, <c>EventData</c>,
    /// <c>UserData</c>, <c>Data</c>), attributes (<c>@Name</c>, <c>@SystemTime</c>, …), child steps (<c>/</c>), string
    /// literals, numbers, comparison operators, <c>and</c>, <c>or</c>, <c>not()</c>, <c>band()</c>, <c>timediff()</c>,
    /// parentheses and brackets. No other function, no <c>|</c>, <c>//</c>, axes, variables or control characters.
    /// </summary>
    /// <param name="xpath">The expression.</param>
    /// <param name="reasonKey">Null when valid, else one of <see cref="RawXPathReasons"/> (a localization key).</param>
    public static bool IsValidRawXPath(string? xpath, out string? reasonKey)
    {
        reasonKey = RawXPathChecker.Check(xpath);
        return reasonKey is null;
    }

    /// <summary>The reasons <see cref="IsValidRawXPath"/> gives (stable localization keys).</summary>
    public static class RawXPathReasons
    {
        public const string Empty = "EventLogs.RawXPath.Empty";
        public const string TooLong = "EventLogs.RawXPath.TooLong";
        public const string InvalidCharacter = "EventLogs.RawXPath.InvalidCharacter";
        public const string UnterminatedLiteral = "EventLogs.RawXPath.UnterminatedLiteral";
        public const string Unbalanced = "EventLogs.RawXPath.Unbalanced";
        public const string TooDeep = "EventLogs.RawXPath.TooDeep";
        public const string UnsupportedFunction = "EventLogs.RawXPath.UnsupportedFunction";
        public const string UnsupportedPath = "EventLogs.RawXPath.UnsupportedPath";
        public const string NotAnEventSelection = "EventLogs.RawXPath.NotAnEventSelection";
        public const string Syntax = "EventLogs.RawXPath.Syntax";

        /// <summary>Every reason.</summary>
        public static IReadOnlyList<string> All { get; } =
            [Empty, TooLong, InvalidCharacter, UnterminatedLiteral, Unbalanced, TooDeep, UnsupportedFunction, UnsupportedPath, NotAnEventSelection, Syntax];
    }

    internal static string FormatSystemTime(DateTimeOffset utc) =>
        utc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset Floor(DateTimeOffset utc) =>
        new(utc.UtcTicks - (utc.UtcTicks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);

    private static List<string> SelectExpressions(EventLogQuery query)
    {
        var ids = query.EventIds.Distinct().ToList();
        if (ids.Any(id => id is < 0 or > ushort.MaxValue))
        {
            throw new ArgumentException("An event ID is outside 0–65535.", nameof(query));
        }

        var common = new List<string>();
        var levels = query.Levels.Distinct().ToList();
        if (levels.Count > 0)
        {
            common.Add(Group(levels.Select(l => string.Create(CultureInfo.InvariantCulture, $"Level={l}"))));
        }

        var providers = query.Providers.Distinct(StringComparer.Ordinal).ToList();
        if (providers.Count > 0)
        {
            if (!providers.All(XPathLiteral.IsAllowedValue))
            {
                throw new ArgumentException("A provider name is empty, too long, contains a control character or both quote characters.", nameof(query));
            }

            common.Add("Provider[" + string.Join(" or ", providers.Select(p => "@Name=" + XPathLiteral.Quote(p))) + "]");
        }

        if (query.Outcome is { } outcome)
        {
            var mask = outcome == EventAuditOutcome.Success ? EventLogRecord.AuditSuccessKeyword : EventLogRecord.AuditFailureKeyword;
            common.Add(string.Create(CultureInfo.InvariantCulture, $"band(Keywords,{mask})"));
        }

        if (query.From is { } from && query.To is { } to && from > to)
        {
            throw new ArgumentException("The start of the time window is after its end.", nameof(query));
        }

        var (lower, upper) = TimeBounds(query.From, query.To);
        var time = new List<string>();
        if (lower is { } l1)
        {
            time.Add($"@SystemTime>='{FormatSystemTime(l1)}'");
        }

        if (upper is { } u1)
        {
            time.Add($"@SystemTime<='{FormatSystemTime(u1)}'");
        }

        if (time.Count > 0)
        {
            common.Add("TimeCreated[" + string.Join(" and ", time) + "]");
        }

        var data = DataConditions(query);
        var chunks = ids.Count == 0 ? [[]] : ids.Chunk(MaxEventIdsPerSelect).ToList();
        return [.. chunks.Select(chunk => SelectExpression(chunk, common, data))];
    }

    private static List<string> DataConditions(EventLogQuery query)
    {
        var conditions = new List<string>();
        foreach (var condition in query.Data)
        {
            ArgumentNullException.ThrowIfNull(condition, nameof(query));
            if (!XPathLiteral.IsAllowedName(condition.Name))
            {
                throw new ArgumentException("A data name contains characters outside [0-9A-Za-z.-_:{}$ ].", nameof(query));
            }

            var values = (condition.Values ?? []).Distinct(StringComparer.Ordinal).ToList();
            if (values.Count == 0)
            {
                throw new ArgumentException("A data condition has no value.", nameof(query));
            }

            if (!values.All(XPathLiteral.IsAllowedValue))
            {
                throw new ArgumentException("A data value is empty, too long, contains a control character or both quote characters.", nameof(query));
            }

            conditions.Add("EventData[" + string.Join(" or ", values.Select(v => $"Data[@Name='{condition.Name}']={XPathLiteral.Quote(v)}")) + "]");
        }

        return conditions;
    }

    private static string SelectExpression(int[] ids, List<string> common, List<string> data)
    {
        var system = new List<string>();
        if (ids.Length > 0)
        {
            system.Add(Group(ids.Select(id => string.Create(CultureInfo.InvariantCulture, $"EventID={id}"))));
        }

        system.AddRange(common);
        var parts = new List<string>();
        if (system.Count > 0)
        {
            parts.Add("System[" + string.Join(" and ", system) + "]");
        }

        parts.AddRange(data);
        return parts.Count == 0 ? "*" : "*[" + string.Join(" and ", parts) + "]";
    }

    private static string Group(IEnumerable<string> terms) => "(" + string.Join(" or ", terms) + ")";

    /// <summary>A recursive-descent check of the raw XPath subset (no evaluation).</summary>
    private sealed class RawXPathChecker
    {
        private static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase) { "band", "timediff" };

        private static readonly SearchValues<char> LiteralControlCharacters =
            SearchValues.Create([.. Enumerable.Range(0, 0x20).Select(c => (char)c), .. Enumerable.Range(0x7F, 0x21).Select(c => (char)c)]);

        private readonly List<Token> _tokens;
        private int _position;
        private int _depth;

        private RawXPathChecker(List<Token> tokens) => _tokens = tokens;

        private enum Kind
        {
            Name,
            Attribute,
            Star,
            Slash,
            Literal,
            Number,
            Operator,
            Open,
            Close,
            OpenBracket,
            CloseBracket,
            Comma,
            End,
        }

        public static string? Check(string? xpath)
        {
            if (string.IsNullOrWhiteSpace(xpath))
            {
                return RawXPathReasons.Empty;
            }

            if (xpath.Length > MaxRawXPathLength)
            {
                return RawXPathReasons.TooLong;
            }

            if (Tokenize(xpath, out var tokens) is { } lexical)
            {
                return lexical;
            }

            if (Balance(tokens) is { } balance)
            {
                return balance;
            }

            var checker = new RawXPathChecker(tokens);
            try
            {
                return checker.Selection();
            }
            catch (SyntaxError error)
            {
                return error.Reason;
            }
        }

        private static string? Tokenize(string text, out List<Token> tokens)
        {
            tokens = [];
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (c is ' ' or '\t' or '\r' or '\n')
                {
                    i++;
                    continue;
                }

                if (char.IsControl(c))
                {
                    return RawXPathReasons.InvalidCharacter;
                }

                switch (c)
                {
                    case '\'' or '"':
                        var end = text.IndexOf(c, i + 1);
                        if (end < 0)
                        {
                            return RawXPathReasons.UnterminatedLiteral;
                        }

                        if (text.AsSpan(i + 1, end - i - 1).ContainsAny(LiteralControlCharacters))
                        {
                            return RawXPathReasons.InvalidCharacter;
                        }

                        tokens.Add(new Token(Kind.Literal, text[i..(end + 1)]));
                        i = end + 1;
                        continue;
                    case '*':
                        tokens.Add(new Token(Kind.Star, "*"));
                        i++;
                        continue;
                    case '/':
                        if (i + 1 < text.Length && text[i + 1] == '/')
                        {
                            return RawXPathReasons.UnsupportedPath;
                        }

                        tokens.Add(new Token(Kind.Slash, "/"));
                        i++;
                        continue;
                    case '(':
                        tokens.Add(new Token(Kind.Open, "("));
                        i++;
                        continue;
                    case ')':
                        tokens.Add(new Token(Kind.Close, ")"));
                        i++;
                        continue;
                    case '[':
                        tokens.Add(new Token(Kind.OpenBracket, "["));
                        i++;
                        continue;
                    case ']':
                        tokens.Add(new Token(Kind.CloseBracket, "]"));
                        i++;
                        continue;
                    case ',':
                        tokens.Add(new Token(Kind.Comma, ","));
                        i++;
                        continue;
                    case '=':
                        tokens.Add(new Token(Kind.Operator, "="));
                        i++;
                        continue;
                    case '!' or '<' or '>':
                        if (i + 1 < text.Length && text[i + 1] == '=')
                        {
                            tokens.Add(new Token(Kind.Operator, text.Substring(i, 2)));
                            i += 2;
                            continue;
                        }

                        if (c == '!')
                        {
                            return RawXPathReasons.InvalidCharacter;
                        }

                        tokens.Add(new Token(Kind.Operator, c.ToString()));
                        i++;
                        continue;
                    case '.':
                        // '.', '..' (self/parent) and leading-dot numbers are not part of the subset.
                        return RawXPathReasons.UnsupportedPath;
                    case '@':
                        var start = ++i;
                        while (i < text.Length && IsNameChar(text[i]))
                        {
                            i++;
                        }

                        if (i == start)
                        {
                            return i < text.Length && text[i] == '*' ? RawXPathReasons.UnsupportedPath : RawXPathReasons.Syntax;
                        }

                        tokens.Add(new Token(Kind.Attribute, text[start..i]));
                        continue;
                }

                if (char.IsAsciiDigit(c))
                {
                    var start = i;
                    if (c == '0' && i + 1 < text.Length && text[i + 1] is 'x' or 'X')
                    {
                        i += 2;
                        var digits = i;
                        while (i < text.Length && char.IsAsciiHexDigit(text[i]))
                        {
                            i++;
                        }

                        if (i == digits)
                        {
                            return RawXPathReasons.Syntax;   // 0x
                        }
                    }
                    else
                    {
                        while (i < text.Length && char.IsAsciiDigit(text[i]))
                        {
                            i++;
                        }

                        if (i < text.Length && text[i] == '.' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]))
                        {
                            i++;
                            while (i < text.Length && char.IsAsciiDigit(text[i]))
                            {
                                i++;
                            }
                        }
                    }

                    if (i < text.Length && IsNameChar(text[i]))
                    {
                        return RawXPathReasons.Syntax;   // 12ab
                    }

                    tokens.Add(new Token(Kind.Number, text[start..i]));
                    continue;
                }

                if (IsNameChar(c))
                {
                    var start = i;
                    while (i < text.Length && IsNameChar(text[i]))
                    {
                        i++;
                    }

                    if (i + 1 < text.Length && text[i] == ':' && text[i + 1] == ':')
                    {
                        return RawXPathReasons.UnsupportedPath;   // axis::
                    }

                    var name = text[start..i];
                    tokens.Add(name is "and" or "or" ? new Token(Kind.Operator, name) : new Token(Kind.Name, name));
                    continue;
                }

                return c == '|' ? RawXPathReasons.UnsupportedPath : RawXPathReasons.InvalidCharacter;   // union, variables, other characters
            }

            tokens.Add(new Token(Kind.End, string.Empty));
            return null;
        }

        private static string? Balance(List<Token> tokens)
        {
            var stack = new Stack<Kind>();
            foreach (var token in tokens)
            {
                switch (token.Kind)
                {
                    case Kind.Open or Kind.OpenBracket:
                        stack.Push(token.Kind);
                        if (stack.Count > MaxRawXPathDepth)
                        {
                            return RawXPathReasons.TooDeep;
                        }

                        break;
                    case Kind.Close:
                        if (!stack.TryPop(out var open) || open != Kind.Open)
                        {
                            return RawXPathReasons.Unbalanced;
                        }

                        break;
                    case Kind.CloseBracket:
                        if (!stack.TryPop(out var bracket) || bracket != Kind.OpenBracket)
                        {
                            return RawXPathReasons.Unbalanced;
                        }

                        break;
                }
            }

            return stack.Count == 0 ? null : RawXPathReasons.Unbalanced;
        }

        private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

        /// <summary>Selection := Path whose first step is <c>*</c> or <c>Event</c>, then the end.</summary>
        private string? Selection()
        {
            var first = Peek();
            if (first.Kind is not (Kind.Star or Kind.Name) || (first.Kind == Kind.Name && first.Text != "Event"))
            {
                return RawXPathReasons.NotAnEventSelection;
            }

            Path();
            return Peek().Kind == Kind.End ? null : RawXPathReasons.NotAnEventSelection;
        }

        private void Expression()
        {
            And();
            while (IsOperator("or"))
            {
                _position++;
                And();
            }
        }

        private void And()
        {
            Comparison();
            while (IsOperator("and"))
            {
                _position++;
                Comparison();
            }
        }

        private void Comparison()
        {
            Primary();
            if (Peek() is { Kind: Kind.Operator, Text: "=" or "!=" or "<" or "<=" or ">" or ">=" })
            {
                _position++;
                Primary();
                if (Peek() is { Kind: Kind.Operator, Text: "=" or "!=" or "<" or "<=" or ">" or ">=" })
                {
                    throw new SyntaxError(RawXPathReasons.Syntax);   // a = b = c
                }
            }
        }

        private void Primary()
        {
            var token = Peek();
            switch (token.Kind)
            {
                case Kind.Open:
                    Enter();
                    _position++;
                    Expression();
                    Expect(Kind.Close);
                    _depth--;
                    return;
                case Kind.Literal or Kind.Number:
                    _position++;
                    return;
                case Kind.Name when Next().Kind == Kind.Open:
                    Function(token.Text);
                    return;
                case Kind.Name or Kind.Star or Kind.Attribute:
                    Path();
                    return;
                default:
                    throw new SyntaxError(RawXPathReasons.Syntax);
            }
        }

        private void Function(string name)
        {
            if (!Functions.Contains(name) && name != "not")
            {
                throw new SyntaxError(RawXPathReasons.UnsupportedFunction);
            }

            Enter();
            _position += 2;   // name (
            Expression();
            if (!string.Equals(name, "not", StringComparison.Ordinal) && Peek().Kind == Kind.Comma)
            {
                _position++;
                Expression();
            }

            Expect(Kind.Close);
            _depth--;
        }

        /// <summary>Path := Step ('/' Step)*, Step := ('*' | Name) Predicate* | '@' Name.</summary>
        private void Path()
        {
            while (true)
            {
                var token = Peek();
                switch (token.Kind)
                {
                    case Kind.Attribute:
                        _position++;
                        if (Peek().Kind is Kind.Slash or Kind.OpenBracket)
                        {
                            throw new SyntaxError(RawXPathReasons.UnsupportedPath);   // nothing below an attribute
                        }

                        return;
                    case Kind.Star or Kind.Name:
                        _position++;
                        while (Peek().Kind == Kind.OpenBracket)
                        {
                            Enter();
                            _position++;
                            Expression();
                            Expect(Kind.CloseBracket);
                            _depth--;
                        }

                        break;
                    default:
                        throw new SyntaxError(RawXPathReasons.Syntax);
                }

                if (Peek().Kind != Kind.Slash)
                {
                    return;
                }

                _position++;
            }
        }

        private void Enter()
        {
            if (++_depth > MaxRawXPathDepth)
            {
                throw new SyntaxError(RawXPathReasons.TooDeep);
            }
        }

        private void Expect(Kind kind)
        {
            if (Peek().Kind != kind)
            {
                throw new SyntaxError(RawXPathReasons.Syntax);
            }

            _position++;
        }

        private bool IsOperator(string text) => Peek() is { Kind: Kind.Operator } token && token.Text == text;

        private Token Peek() => _tokens[Math.Min(_position, _tokens.Count - 1)];

        private Token Next() => _tokens[Math.Min(_position + 1, _tokens.Count - 1)];

        private readonly record struct Token(Kind Kind, string Text);

        private sealed class SyntaxError(string reason) : Exception(reason)
        {
            public string Reason { get; } = reason;
        }
    }
}
