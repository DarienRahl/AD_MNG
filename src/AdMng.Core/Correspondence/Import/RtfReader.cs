using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AdMng.Core.Correspondence.Import;

/// <summary>
/// The plain text of an RTF document (design note 3.1), a small reader without recursion: groups, <c>\par</c>/<c>\line</c>
/// → new line, <c>\tab</c>/<c>\cell</c> → tab, <c>\row</c> → new line, <c>\'hh</c> in the code page of the current font
/// (<c>\fcharsetN</c> of the font table), else <c>\ansicpgN</c>, else the ANSI code page; <c>\uN?</c> Unicode with
/// <c>\ucN</c> fallback characters skipped; <c>{\*…}</c>, <c>\fonttbl</c>, <c>\colortbl</c>, <c>\stylesheet</c>,
/// <c>\info</c>, <c>\pict</c> and other destinations without text skipped, <c>\binN</c> data jumped over. A
/// <c>HYPERLINK "mailto:…"</c> field whose result does not show the address gets it appended.
/// </summary>
internal static partial class RtfReader
{
    private const int MaxGroupDepth = 10_000;

    private static readonly HashSet<string> SkippedDestinations = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "listtable", "listoverridetable", "revtbl", "rsidtbl",
        "generator", "xmlnstbl", "themedata", "colorschememapping", "datastore", "latentstyles", "filetbl", "header", "footer",
        "headerl", "headerr", "headerf", "footerl", "footerr", "footerf", "footnote", "annotation", "fldinst", "nonshppict",
        "shppict", "objdata", "bkmkstart", "bkmkend", "pgdsctbl", "listtext", "pntext", "pntxta", "pntxtb", "mmathPr", "wgrffmtfilter",
    };

    private static readonly Dictionary<string, string> Symbols = new(StringComparer.Ordinal)
    {
        ["par"] = "\n", ["line"] = "\n", ["row"] = "\n", ["sect"] = "\n", ["page"] = "\n", ["tab"] = "\t", ["cell"] = "\t",
        ["emdash"] = "—", ["endash"] = "–", ["bullet"] = "•", ["lquote"] = "‘", ["rquote"] = "’", ["ldblquote"] = "“",
        ["rdblquote"] = "”", ["emspace"] = " ", ["enspace"] = " ", ["qmspace"] = " ",
    };

    /// <summary>\fcharset → Windows code page.</summary>
    private static readonly Dictionary<int, int> CharsetCodePages = new()
    {
        [0] = 1252, [77] = 10000, [128] = 932, [129] = 949, [134] = 936, [136] = 950, [161] = 1253, [162] = 1254, [163] = 1258,
        [177] = 1255, [178] = 1256, [186] = 1257, [204] = 1251, [222] = 874, [238] = 1250,
    };

    public static string Read(ReadOnlySpan<byte> rtf, int ansiCodePage) => new Run(ansiCodePage).Read(rtf);

    private sealed class GroupState
    {
        public bool Skip { get; set; }

        public int UnicodeSkip { get; set; } = 1;

        public int Font { get; set; } = -1;

        public bool InFontTable { get; set; }

        /// <summary>The text of a skipped field instruction (only the <c>\fldinst</c> group).</summary>
        public StringBuilder? Instruction { get; set; }

        /// <summary>The mailto target of the field this group belongs to.</summary>
        public string? FieldLink { get; set; }

        /// <summary>Where the result of a link field starts in the output (only the <c>\fldrslt</c> group), else -1.</summary>
        public int ResultStart { get; set; } = -1;

        public GroupState Clone() => new()
        {
            Skip = Skip, UnicodeSkip = UnicodeSkip, Font = Font, InFontTable = InFontTable, Instruction = Instruction, FieldLink = FieldLink,
        };
    }

    private sealed class Run(int ansiCodePage)
    {
        private readonly StringBuilder _output = new();
        private readonly List<byte> _pending = [];
        private readonly Dictionary<int, int> _fonts = [];
        private readonly Stack<GroupState> _stack = new();
        private GroupState _state = new();
        private int _documentCodePage = ansiCodePage;
        private int _defaultFont = -1;
        private int _fontEntry = -1;
        private int _skipChars;
        private int _overflow;

        public string Read(ReadOnlySpan<byte> rtf)
        {
            var newGroup = false;
            var i = 0;
            while (i < rtf.Length)
            {
                var b = rtf[i];
                if (b == (byte)'{')
                {
                    Flush();
                    if (_stack.Count < MaxGroupDepth)
                    {
                        _stack.Push(_state);
                        _state = _state.Clone();
                    }
                    else
                    {
                        _overflow++;
                    }

                    newGroup = true;
                    _skipChars = 0;
                    i++;
                    continue;
                }

                if (b == (byte)'}')
                {
                    CloseGroup();
                    newGroup = false;
                    i++;
                    continue;
                }

                if (b is (byte)'\r' or (byte)'\n')
                {
                    i++;
                    continue;
                }

                if (b != (byte)'\\')
                {
                    Character(b);
                    newGroup = false;
                    i++;
                    continue;
                }

                // A control symbol or word.
                i++;
                if (i >= rtf.Length)
                {
                    break;
                }

                var next = rtf[i];
                if (next == (byte)'\'')
                {
                    if (i + 2 < rtf.Length && Hex(rtf[i + 1]) is var high and >= 0 && Hex(rtf[i + 2]) is var low and >= 0)
                    {
                        Character((byte)((high << 4) | low));
                        i += 3;
                    }
                    else
                    {
                        i++;
                    }

                    newGroup = false;
                    continue;
                }

                if (!IsLetter(next))
                {
                    i++;
                    if (next == (byte)'*')
                    {
                        _state.Skip = true;   // {\*\destination …}: the destination keyword still follows
                        continue;
                    }

                    newGroup = false;
                    switch (next)
                    {
                        case (byte)'\\' or (byte)'{' or (byte)'}':
                            Character(next);
                            break;
                        case (byte)'~':
                            Text(" ");
                            break;
                        case (byte)'_':
                            Text("-");
                            break;
                        case (byte)'\r' or (byte)'\n':
                            Text("\n");
                            break;
                    }

                    continue;
                }

                var start = i;
                while (i < rtf.Length && IsLetter(rtf[i]) && i - start < 32)
                {
                    i++;
                }

                var word = Encoding.ASCII.GetString(rtf[start..i]);
                int? parameter = null;
                var negative = i < rtf.Length && rtf[i] == (byte)'-';
                if (negative)
                {
                    i++;
                }

                var digits = i;
                while (i < rtf.Length && rtf[i] is >= (byte)'0' and <= (byte)'9')
                {
                    i++;
                }

                if (i > digits)
                {
                    parameter = long.TryParse(rtf[digits..Math.Min(i, digits + 10)], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                        ? (int)Math.Clamp(negative ? -number : number, int.MinValue, int.MaxValue)
                        : null;
                }

                if (i < rtf.Length && rtf[i] == (byte)' ')
                {
                    i++;   // the delimiter space belongs to the control word
                }

                var first = newGroup;
                newGroup = false;
                if (word == "bin")
                {
                    Flush();
                    i = (int)Math.Min(rtf.Length, (long)i + Math.Max(0, parameter ?? 0));
                    continue;
                }

                ControlWord(word, parameter, first);
            }

            Flush();
            return _output.ToString();
        }

        private void ControlWord(string word, int? parameter, bool firstInGroup)
        {
            if (firstInGroup && SkippedDestinations.Contains(word))
            {
                Flush();
                _state.Skip = true;
                _state.InFontTable = word == "fonttbl";
                if (word == "fldinst")
                {
                    _state.Instruction = new StringBuilder();
                }

                return;
            }

            if (firstInGroup && word == "fldrslt" && _state.FieldLink is not null)
            {
                Flush();
                _state.ResultStart = _output.Length;
                return;
            }

            switch (word)
            {
                case "ansicpg" when parameter is > 0:
                    _documentCodePage = parameter.Value;
                    break;
                case "deff" when parameter is >= 0:
                    _defaultFont = parameter.Value;
                    break;
                case "f" when parameter is >= 0:
                    if (_state.InFontTable)
                    {
                        _fontEntry = parameter.Value;
                    }
                    else
                    {
                        Flush();
                        _state.Font = parameter.Value;
                    }

                    break;
                case "fcharset" when _state.InFontTable && _fontEntry >= 0 && parameter is >= 0:
                    if (CharsetCodePages.TryGetValue(parameter.Value, out var codePage))
                    {
                        _fonts[_fontEntry] = codePage;
                    }

                    break;
                case "uc" when parameter is >= 0:
                    _state.UnicodeSkip = Math.Min(parameter.Value, 16);
                    break;
                case "u" when parameter is not null:
                    var code = parameter.Value < 0 ? parameter.Value + 65536 : parameter.Value;
                    if (!_state.Skip && code is >= 0 and <= 0xFFFF)
                    {
                        Flush();
                        _output.Append((char)code);
                    }

                    _skipChars = _state.UnicodeSkip;
                    break;
                default:
                    if (Symbols.TryGetValue(word, out var symbol))
                    {
                        Text(symbol);
                    }

                    break;
            }
        }

        private void CloseGroup()
        {
            Flush();
            if (_overflow > 0)
            {
                _overflow--;
                return;
            }

            var closed = _state;
            _state = _stack.Count > 0 ? _stack.Pop() : new GroupState();
            _skipChars = 0;
            if (closed.Instruction is { } instruction && !ReferenceEquals(instruction, _state.Instruction) && LinkPattern().Match(instruction.ToString()) is { Success: true } link)
            {
                _state.FieldLink = "mailto:" + link.Groups[1].Value;   // the enclosing \field group
            }

            if (closed.ResultStart >= 0)
            {
                DocumentParts.AppendLinkAddress(_output, closed.ResultStart, closed.FieldLink);
            }
        }

        private void Character(byte b)
        {
            if (_skipChars > 0)
            {
                _skipChars--;
                return;
            }

            if (_state.Skip)
            {
                _state.Instruction?.Append((char)b);
                return;
            }

            if (b < 0x80)
            {
                Flush();
                _output.Append((char)b);
            }
            else
            {
                _pending.Add(b);   // \'hh and raw 8-bit text: decoded together (double-byte code pages)
            }
        }

        private void Text(string value)
        {
            if (_skipChars > 0)
            {
                _skipChars--;
                return;
            }

            if (_state.Skip)
            {
                return;
            }

            Flush();
            _output.Append(value);
        }

        private void Flush()
        {
            if (_pending.Count == 0)
            {
                return;
            }

            var font = _state.Font >= 0 ? _state.Font : _defaultFont;
            var codePage = font >= 0 && _fonts.TryGetValue(font, out var fontCodePage) ? fontCodePage : _documentCodePage;
            _output.Append(TextDecoding.Ansi(codePage).GetString([.. _pending]));
            _pending.Clear();
        }
    }

    private static int Hex(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };

    private static bool IsLetter(byte b) => b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z';

    [GeneratedRegex(@"HYPERLINK\s+""?\s*mailto:([^""\s\\]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();
}
