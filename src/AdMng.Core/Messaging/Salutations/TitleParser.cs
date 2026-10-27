namespace AdMng.Core.Messaging.Salutations;

/// <summary>A word of a title or a display name with its comparison key (<see cref="SalutationKey"/>).</summary>
public sealed record TitleToken(string Text, string Key)
{
    public bool EndsWithDot => Text.EndsWith('.');
}

/// <summary>
/// One recognized element of a title chain: a row of the table in its canonical spelling, or an absorbed qualifier
/// (<c>n. med.</c>) or university (<c>UW</c>) as written (<see cref="Rule"/> null).
/// </summary>
public sealed record TitlePart(string Text, TitleRule? Rule);

/// <summary>The title of a person (U1d): the recognized chain, where it came from and what the salutation can use.</summary>
public sealed record TitleInfo
{
    /// <summary>No title, no honorific.</summary>
    public static TitleInfo None { get; } = new();

    /// <summary>Titles before the name ("prof. dr hab."), or the whole title from the title attribute.</summary>
    public IReadOnlyList<TitlePart> Prefix { get; init; } = [];

    /// <summary>Titles after the last comma of the display name ("prof. UW").</summary>
    public IReadOnlyList<TitlePart> Suffix { get; init; } = [];

    /// <summary><see cref="DerivedValueSource.TitleAttribute"/>, <see cref="DerivedValueSource.DisplayName"/> or none.</summary>
    public DerivedValueSource Source { get; init; }

    /// <summary>Gender set by an honorific word of the title attribute ("Pani", "Mr").</summary>
    public Gender HonorificGender { get; init; }

    /// <summary>Words of the title attribute that are not titles of the table (warning <c>TitleUnrecognized</c>).</summary>
    public IReadOnlyList<string> Unrecognized { get; init; } = [];

    /// <summary>The display name without its title (when the title came from it).</summary>
    public string? NameWithoutTitle { get; init; }

    /// <summary>The rows of the table occurring in the title, highest rank first (salutation selection).</summary>
    public IReadOnlyList<TitleRule> Candidates { get; init; } = [];

    public bool HasTitle => Prefix.Count > 0 || Suffix.Count > 0;

    /// <summary>"prof. dr hab."</summary>
    public string PrefixText => string.Join(' ', Prefix.Select(p => p.Text));

    /// <summary>"prof. UW"</summary>
    public string SuffixText => string.Join(' ', Suffix.Select(p => p.Text));

    /// <summary><c>{$title}</c>: "prof. dr hab.", "dr hab., prof. UW", "prof. UW"; empty without a title.</summary>
    public string Text => Suffix.Count == 0 ? PrefixText : Prefix.Count == 0 ? SuffixText : PrefixText + ", " + SuffixText;

    /// <summary><c>{$title:highest}</c>: the canonical spelling of the highest-ranked title, or empty.</summary>
    public string Highest => Candidates.FirstOrDefault()?.Title ?? string.Empty;

    /// <summary>
    /// The highest-ranked title having a Polish form (the one a Polish salutation starts from), or null.
    /// </summary>
    public TitleRule? FirstWithPolishForm => Candidates.FirstOrDefault(r => HasForm(r.PolishMasculine) || HasForm(r.PolishFeminine));

    /// <summary>
    /// The gender the title implies: <see cref="FirstWithPolishForm"/> has a form for one gender only ("ks." – masculine);
    /// else unknown.
    /// </summary>
    public Gender ImpliedGender => FirstWithPolishForm is { } row && HasForm(row.PolishMasculine) != HasForm(row.PolishFeminine)
        ? HasForm(row.PolishMasculine) ? Gender.Masculine : Gender.Feminine
        : Gender.Unknown;

    private static bool HasForm(string? form) => !string.IsNullOrWhiteSpace(form);
}

/// <summary>
/// Recognizes titles (U1d, design note section 15): in the title attribute (default <c>personalTitle</c>), else as a
/// prefix and/or a suffix after the last comma of the display name – accepted only when the rest still contains the given
/// name or the surname. Words are compared case- and diacritics-insensitively, ignoring a trailing dot; a word with inner
/// dots is split after each dot. The longest table spelling at each position is taken; <c>n.</c> + up to two words of
/// which the last ends in a dot (<c>dr n. med.</c>) and a university abbreviation after a row with <c>prof.</c> in its
/// title (<c>prof. UW</c>, <c>prof. uczelni AGH</c>, <c>PWr</c>) are absorbed as written. Pure.
/// </summary>
public static class TitleParser
{
    private static readonly HashSet<string> MasculineHonorifics = [.. SalutationData.MasculineHonorifics.Select(SalutationKey.Of)];
    private static readonly HashSet<string> FeminineHonorifics = [.. SalutationData.FeminineHonorifics.Select(SalutationKey.Of)];

    /// <summary>Degrees that look like a university abbreviation ("prof. MBA" is no "prof. UW"); compared case-insensitively.</summary>
    private static readonly HashSet<string> Degrees = new(StringComparer.OrdinalIgnoreCase)
    {
        "MBA", "EMBA", "DBA", "PhD", "DSc", "ScD", "MSc", "BSc", "MA", "BA", "MD", "LLM", "LLB", "LLD", "MPhil", "DPhil", "MEng",
        "BEng", "MPH", "MFA", "EdD",
    };

    /// <summary>The title of a person.</summary>
    /// <param name="attributeValue">Value of the title attribute (or null).</param>
    /// <param name="displayName">The display name (parsed when the attribute yields no title).</param>
    /// <param name="givenName">Given name (the display name must still contain it or the surname).</param>
    /// <param name="surname">Surname.</param>
    /// <param name="table">The title table.</param>
    /// <param name="fromDisplayName">Parse the display name at all.</param>
    public static TitleInfo Parse(string? attributeValue, string? displayName, string? givenName, string? surname, TitleTable table, bool fromDisplayName = true)
    {
        ArgumentNullException.ThrowIfNull(table);
        var attribute = ParseAttribute(attributeValue, table);
        if (attribute.HasTitle || !fromDisplayName)
        {
            return attribute;
        }

        return ParseDisplayName(displayName, givenName, surname, table) is { } fromName
            ? fromName with { HonorificGender = attribute.HonorificGender, Unrecognized = attribute.Unrecognized }
            : attribute;
    }

    /// <summary>The title attribute: every word is a title, an honorific word (sets the gender) or unrecognized.</summary>
    public static TitleInfo ParseAttribute(string? value, TitleTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var honorific = Gender.Unknown;
        var words = new List<TitleToken>();
        foreach (var token in Tokenize(value))
        {
            if (MasculineHonorifics.Contains(token.Key) || FeminineHonorifics.Contains(token.Key))
            {
                if (honorific == Gender.Unknown)
                {
                    honorific = MasculineHonorifics.Contains(token.Key) ? Gender.Masculine : Gender.Feminine;
                }

                continue;
            }

            words.Add(token);
        }

        var cover = Cover(words, table, stopAtUnknown: false, isName: _ => false);
        return new TitleInfo
        {
            Prefix = cover.Parts,
            Source = cover.Parts.Count > 0 ? DerivedValueSource.TitleAttribute : DerivedValueSource.None,
            HonorificGender = honorific,
            Unrecognized = cover.Unrecognized,
            Candidates = table.Candidates([cover.Keys]),
        };
    }

    /// <summary>A title prefix and/or suffix of the display name; null when there is none or the rest has no name.</summary>
    public static TitleInfo? ParseDisplayName(string? displayName, string? givenName, string? surname, TitleTable table) =>
        ParseDisplayName(displayName, givenName, surname, table, requireName: true);

    /// <summary>
    /// The display name without a title prefix and/or suffix ("dr Jan Kowalski" → "Jan Kowalski") for a person without given
    /// name and surname – the name check of <see cref="ParseDisplayName(string?, string?, string?, TitleTable)"/> is not
    /// possible, so the title found this way is never used as the title; null when there is none.
    /// </summary>
    public static string? WithoutTitles(string? displayName, TitleTable table) =>
        ParseDisplayName(displayName, null, null, table, requireName: false)?.NameWithoutTitle;

    private static TitleInfo? ParseDisplayName(string? displayName, string? givenName, string? surname, TitleTable table, bool requireName)
    {
        ArgumentNullException.ThrowIfNull(table);
        var text = displayName?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var givenKeys = Tokenize(givenName).Select(t => t.Key).ToList();
        var surnameKeys = Tokenize(surname).Select(t => t.Key).ToList();
        var nameKeys = givenKeys.Concat(surnameKeys).ToHashSet(StringComparer.Ordinal);
        bool IsName(TitleToken token) => nameKeys.Contains(token.Key);

        var main = text;
        Coverage suffix = Coverage.Empty;
        var comma = text.LastIndexOf(',');
        if (comma >= 0)
        {
            var suffixTokens = Tokenize(text[(comma + 1)..]);
            var covered = Cover(suffixTokens, table, stopAtUnknown: true, IsName);
            if (covered.Parts.Count > 0 && covered.End == suffixTokens.Count)
            {
                suffix = covered;
                main = text[..comma];
            }
        }

        var mainTokens = Tokenize(main);
        var prefix = Cover(mainTokens, table, stopAtUnknown: true, IsName);
        var rest = mainTokens.Skip(prefix.End).ToList();
        if (prefix.Parts.Count + suffix.Parts.Count == 0 || rest.Count == 0)
        {
            return null;
        }

        var restKeys = rest.Select(t => t.Key).ToList();
        if (requireName && !ContainsSequence(restKeys, givenKeys) && !ContainsSequence(restKeys, surnameKeys))
        {
            return null;
        }

        return new TitleInfo
        {
            Prefix = prefix.Parts,
            Suffix = suffix.Parts,
            Source = DerivedValueSource.DisplayName,
            NameWithoutTitle = string.Join(' ', rest.Select(t => t.Text)),
            Candidates = table.Candidates([prefix.Keys, suffix.Keys]),
        };
    }

    /// <summary>
    /// Words of a title or name: split on white space and commas, a word with inner dots split after each dot
    /// (<c>dr.hab.inż.</c> → <c>dr.</c> <c>hab.</c> <c>inż.</c>); words without letters or digits are dropped.
    /// </summary>
    public static IReadOnlyList<TitleToken> Tokenize(string? text)
    {
        var tokens = new List<TitleToken>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return tokens;
        }

        foreach (var word in text.Split([' ', '\t', '\r', '\n', ' ', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            var start = 0;
            for (var i = 0; i < word.Length; i++)
            {
                if (word[i] == '.' && i < word.Length - 1)
                {
                    Add(word[start..(i + 1)]);
                    start = i + 1;
                }
            }

            Add(word[start..]);
        }

        return tokens;

        void Add(string piece)
        {
            var key = SalutationKey.Of(piece);
            if (key.Length > 0 && key.Any(char.IsLetterOrDigit))
            {
                tokens.Add(new TitleToken(piece, key));
            }
        }
    }

    /// <summary>
    /// A university abbreviation after "prof.": 2–8 letters, upper-case first, at least two upper-case letters, not a degree
    /// (MBA, PhD, DSc … – those stay unrecognized words of the title attribute).
    /// </summary>
    public static bool IsUniversity(string word) =>
        word.Length is >= 2 and <= 8 && word.All(char.IsLetter) && char.IsUpper(word[0]) && word.Count(char.IsUpper) >= 2 && !Degrees.Contains(word);

    private static Coverage Cover(IReadOnlyList<TitleToken> tokens, TitleTable table, bool stopAtUnknown, Func<TitleToken, bool> isName)
    {
        var keys = tokens.Select(t => t.Key).ToList();
        var parts = new List<TitlePart>();
        var covered = new List<string>();
        var unrecognized = new List<string>();
        var i = 0;
        while (i < tokens.Count)
        {
            if (stopAtUnknown && isName(tokens[i]))
            {
                break;
            }

            if (table.LongestAt(keys, i) is { } spelling)
            {
                var rule = table.Rules[spelling.Rule];
                parts.Add(new TitlePart(rule.Title.Trim(), rule));
                covered.AddRange(keys.Skip(i).Take(spelling.Keys.Count));
                i += spelling.Keys.Count;
                continue;
            }

            if (parts.Count > 0 && Absorbed(tokens, i, parts[^1], isName) is { } length and > 0)
            {
                parts.Add(new TitlePart(string.Join(' ', tokens.Skip(i).Take(length).Select(t => t.Text)), null));
                covered.AddRange(keys.Skip(i).Take(length));
                i += length;
                continue;
            }

            if (stopAtUnknown)
            {
                break;
            }

            unrecognized.Add(tokens[i].Text);
            i++;
        }

        return new Coverage(parts, covered, unrecognized, i);
    }

    /// <summary>Length of an absorbed qualifier (<c>n. med.</c>, <c>n. o zdr.</c>) or university at <paramref name="i"/>; 0 = none.</summary>
    private static int Absorbed(IReadOnlyList<TitleToken> tokens, int i, TitlePart previous, Func<TitleToken, bool> isName)
    {
        var token = tokens[i];
        if (token.Key == "n" && token.EndsWithDot)
        {
            if (i + 1 < tokens.Count && !isName(tokens[i + 1]) && tokens[i + 1].EndsWithDot)
            {
                return 2;
            }

            if (i + 2 < tokens.Count && !isName(tokens[i + 1]) && !isName(tokens[i + 2]) && tokens[i + 2].EndsWithDot)
            {
                return 3;
            }

            return 0;
        }

        // After every professor row: "prof. UW", "ks. prof. KUL", "prof. uczelni PW", "prof. nadzw. PŁ".
        var afterProfessor = previous.Rule is { } rule && Tokenize(rule.Title).Any(t => t.Key == "prof");
        return afterProfessor && !isName(token) && IsUniversity(token.Text) ? 1 : 0;
    }

    private static bool ContainsSequence(IReadOnlyList<string> keys, IReadOnlyList<string> sequence)
    {
        if (sequence.Count == 0 || sequence.Count > keys.Count)
        {
            return false;
        }

        for (var i = 0; i + sequence.Count <= keys.Count; i++)
        {
            if (sequence.Select((s, j) => string.Equals(keys[i + j], s, StringComparison.Ordinal)).All(b => b))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record Coverage(IReadOnlyList<TitlePart> Parts, IReadOnlyList<string> Keys, IReadOnlyList<string> Unrecognized, int End)
    {
        public static Coverage Empty { get; } = new([], [], [], 0);
    }
}
