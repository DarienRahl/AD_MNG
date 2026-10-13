using System.Text;

namespace AdMng.Core.Csv;

/// <summary>
/// RFC 4180 CSV reader and writer (see <see cref="ICsvCodec"/>). Pure and stateless; records are parsed
/// incrementally from the <see cref="TextReader"/> (<see cref="ReadRecords(TextReader, char)"/> streams them one by one).
/// </summary>
/// <remarks>
/// Reading is lenient where Excel is lenient: records may end with CRLF, LF or CR; a quote inside an unquoted
/// field is literal; text after a closing quote is appended to the field; blank lines and rows whose cells are all
/// empty are skipped; header names are trimmed. An unterminated quoted field is an error
/// (<see cref="FormatException"/> with the line number) because silently swallowing the rest of the file would
/// hide data. When no delimiter is detected (single column file) ';' is assumed.
/// Writing uses CRLF line endings and writes no BOM - give the writer a <c>new UTF8Encoding(true)</c> when the file
/// is meant for Excel.
/// </remarks>
public sealed class CsvCodec : ICsvCodec
{
    /// <summary>Delimiters recognised by auto-detection, in order of preference on a tie.</summary>
    public static IReadOnlyList<char> CandidateDelimiters { get; } = [';', ',', '\t'];

    /// <summary>Delimiter assumed when the header line contains none of <see cref="CandidateDelimiters"/>.</summary>
    public const char DefaultDelimiter = ';';

    private const char Quote = '"';

    /// <inheritdoc />
    public CsvTable Read(TextReader reader, char? delimiter = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var source = new CharSource(reader);
        var effective = delimiter ?? DetectDelimiter(source);
        ValidateDelimiter(effective);

        IReadOnlyList<string> headers = [];
        var rows = new List<IReadOnlyList<string>>();
        var first = true;
        foreach (var record in ParseRecords(source, effective))
        {
            if (first)
            {
                headers = record.Select(h => h.Trim()).ToArray();
                first = false;
                continue;
            }

            rows.Add(record);
        }

        return new CsvTable(headers, rows, effective);
    }

    /// <summary>
    /// Streams the records of a CSV document (the header line is the first record). Blank lines and records whose
    /// cells are all empty or whitespace are skipped. A leading UTF-8 BOM is ignored.
    /// </summary>
    /// <exception cref="FormatException">A quoted field is not terminated.</exception>
    public IEnumerable<IReadOnlyList<string>> ReadRecords(TextReader reader, char delimiter)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ValidateDelimiter(delimiter);
        return ParseRecords(new CharSource(reader), delimiter);
    }

    /// <inheritdoc />
    public void Write(TextWriter writer, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows, char delimiter = ';', bool protectFormulas = true)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);
        ValidateDelimiter(delimiter);

        WriteRecord(writer, headers, delimiter, protectFormulas);
        foreach (var row in rows)
        {
            ArgumentNullException.ThrowIfNull(row, nameof(rows));
            WriteRecord(writer, row, delimiter, protectFormulas);
        }
    }

    /// <summary>Formats one field: formula protection (optional) and RFC 4180 quoting.</summary>
    public static string FormatField(string? value, char delimiter = DefaultDelimiter, bool protectFormulas = true)
    {
        value ??= string.Empty;
        if (protectFormulas && value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        var needsQuotes = false;
        foreach (var c in value)
        {
            if (c == delimiter || c is Quote or '\r' or '\n')
            {
                needsQuotes = true;
                break;
            }
        }

        if (!needsQuotes && value.Length > 0 && (value[0] == ' ' || value[^1] == ' '))
        {
            needsQuotes = true;
        }

        return needsQuotes ? Quote + value.Replace("\"", "\"\"", StringComparison.Ordinal) + Quote : value;
    }

    private static void WriteRecord(TextWriter writer, IReadOnlyList<string> fields, char delimiter, bool protectFormulas)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                writer.Write(delimiter);
            }

            writer.Write(FormatField(fields[i], delimiter, protectFormulas));
        }

        writer.Write("\r\n");
    }

    private static void ValidateDelimiter(char delimiter)
    {
        if (delimiter is Quote or '\r' or '\n' or '\0')
        {
            throw new ArgumentException($"'{delimiter}' cannot be used as a CSV delimiter.", nameof(delimiter));
        }
    }

    /// <summary>Reads the first record (respecting quotes) into the look-ahead buffer and picks the most frequent candidate.</summary>
    private static char DetectDelimiter(CharSource source)
    {
        var line = source.PeekFirstRecord();
        var counts = new int[CandidateDelimiters.Count];
        var inQuotes = false;
        foreach (var c in line)
        {
            if (c == Quote)
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (inQuotes)
            {
                continue;
            }

            for (var i = 0; i < CandidateDelimiters.Count; i++)
            {
                if (c == CandidateDelimiters[i])
                {
                    counts[i]++;
                }
            }
        }

        var best = -1;
        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] > 0 && (best < 0 || counts[i] > counts[best]))
            {
                best = i;
            }
        }

        return best < 0 ? DefaultDelimiter : CandidateDelimiters[best];
    }

    private static IEnumerable<IReadOnlyList<string>> ParseRecords(CharSource source, char delimiter)
    {
        var record = new List<string>();
        var field = new StringBuilder();
        var atFieldStart = true;
        var recordHasContent = false;
        var line = 1;
        while (true)
        {
            var next = source.Read();
            if (next < 0)
            {
                if (recordHasContent)
                {
                    record.Add(field.ToString());
                    if (!IsBlank(record))
                    {
                        yield return record;
                    }
                }

                yield break;
            }

            var c = (char)next;
            if (atFieldStart && c == Quote)
            {
                atFieldStart = false;
                recordHasContent = true;
                line = ReadQuoted(source, field, line);
                continue;
            }

            if (c == delimiter)
            {
                record.Add(field.ToString());
                field.Clear();
                atFieldStart = true;
                recordHasContent = true;
                continue;
            }

            if (c is '\r' or '\n')
            {
                if (c == '\r' && source.Peek() == '\n')
                {
                    source.Read();
                }

                line++;
                if (recordHasContent)
                {
                    record.Add(field.ToString());
                    if (!IsBlank(record))
                    {
                        yield return record;
                    }

                    record = [];
                }

                field.Clear();
                atFieldStart = true;
                recordHasContent = false;
                continue;
            }

            field.Append(c);
            atFieldStart = false;
            recordHasContent = true;
        }
    }

    /// <summary>Reads a quoted field body after the opening quote. Returns the updated line number.</summary>
    private static int ReadQuoted(CharSource source, StringBuilder field, int line)
    {
        var startLine = line;
        while (true)
        {
            var next = source.Read();
            if (next < 0)
            {
                throw new FormatException($"Unterminated quoted field starting on line {startLine}.");
            }

            var c = (char)next;
            if (c == Quote)
            {
                if (source.Peek() == Quote)
                {
                    source.Read();
                    field.Append(Quote);
                    continue;
                }

                return line;
            }

            if (c == '\n' || (c == '\r' && source.Peek() != '\n'))
            {
                line++;
            }

            field.Append(c);
        }
    }

    private static bool IsBlank(List<string> record) => record.TrueForAll(string.IsNullOrWhiteSpace);

    /// <summary>Buffered character source with a look-ahead buffer used by delimiter detection. Skips a leading BOM.</summary>
    private sealed class CharSource
    {
        private readonly TextReader _reader;
        private readonly char[] _buffer = new char[4096];
        private int _position;
        private int _length;
        private string _lookAhead = string.Empty;
        private int _lookAheadPosition;
        private bool _started;

        public CharSource(TextReader reader)
        {
            _reader = reader;
        }

        public int Read()
        {
            var c = Peek();
            if (c < 0)
            {
                return c;
            }

            if (_lookAheadPosition < _lookAhead.Length)
            {
                _lookAheadPosition++;
            }
            else
            {
                _position++;
            }

            return c;
        }

        public int Peek()
        {
            if (_lookAheadPosition < _lookAhead.Length)
            {
                return _lookAhead[_lookAheadPosition];
            }

            if (!EnsureBuffer())
            {
                return -1;
            }

            return _buffer[_position];
        }

        /// <summary>Reads the raw text of the first record (quotes respected) into the look-ahead buffer.</summary>
        public string PeekFirstRecord()
        {
            var sb = new StringBuilder();
            var inQuotes = false;
            var seenContent = false;
            while (true)
            {
                var next = ReadRaw();
                if (next < 0)
                {
                    break;
                }

                var c = (char)next;
                sb.Append(c);
                if (!inQuotes && c is '\r' or '\n')
                {
                    if (seenContent)
                    {
                        break;
                    }

                    // Leading blank line - keep looking for the header.
                    continue;
                }

                // Lines of spaces are blank lines (tabs may be delimiters, so they count as content).
                seenContent |= c == '\t' || !char.IsWhiteSpace(c);
                if (c == Quote)
                {
                    inQuotes = !inQuotes;
                }
            }

            _lookAhead = _lookAhead[_lookAheadPosition..] + sb;
            _lookAheadPosition = 0;
            return sb.ToString();
        }

        private int ReadRaw()
        {
            if (!EnsureBuffer())
            {
                return -1;
            }

            return _buffer[_position++];
        }

        private bool EnsureBuffer()
        {
            if (_position < _length)
            {
                return true;
            }

            _length = _reader.Read(_buffer, 0, _buffer.Length);
            _position = 0;
            if (!_started && _length > 0)
            {
                _started = true;
                if (_buffer[0] == '﻿')
                {
                    _position = 1;
                    if (_length == 1)
                    {
                        return EnsureBuffer();
                    }
                }
            }

            return _length > 0;
        }
    }
}
