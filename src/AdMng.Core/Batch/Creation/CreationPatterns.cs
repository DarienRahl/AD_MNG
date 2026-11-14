using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Core.Templates;

namespace AdMng.Core.Batch.Creation;

/// <summary>Problems of the naming patterns (docs/dev/phase3-bulk-create.md 2.3; UI text <c>BulkCreate.PatternError.&lt;code&gt;</c>).</summary>
public enum PatternErrorCode
{
    /// <summary>The template syntax is broken (unclosed token, unknown function …).</summary>
    Syntax,

    /// <summary><c>{$column:…}</c> names no column (or a password-like one).</summary>
    UnknownColumn,

    /// <summary>A <c>{$nr:…}</c> argument is out of range or not decimal digits.</summary>
    InvalidCounter,

    /// <summary>Counter tokens of the rule set disagree on start or step.</summary>
    CounterMismatch,

    /// <summary><c>{$dn}</c>, <c>{$rdn}</c> or <c>{$counter}</c> (use <c>{$nr}</c>).</summary>
    ForbiddenToken,

    /// <summary>A referenced attribute is neither allowed for the class nor provided (warning-level; renders empty).</summary>
    UnknownAttribute,

    /// <summary>The rule's attribute is blocked (<see cref="CreationValues.IsBlockedAttribute"/>).</summary>
    BlockedAttribute,

    /// <summary>A second rule for the same attribute.</summary>
    DuplicateRule,

    /// <summary>Rules reference each other in a cycle.</summary>
    Cycle,

    /// <summary><c>{$date:…}</c> with <c>MMM</c> or <c>ddd</c>: month and day names are invariant (warning-level).</summary>
    InvariantDateNames,

    /// <summary>A UPN or mail rule uses <c>sam</c>, which cuts every token to 20 characters (warning-level).</summary>
    SamInAddress,
}

/// <summary>
/// A pattern problem, built from the code, the 1-based position in the pattern and the token's source text – the engine's
/// English error strings (which list arguments and every allowed column header) are never kept.
/// </summary>
/// <param name="Code">What is wrong.</param>
/// <param name="Attribute">The attribute whose rule it is.</param>
/// <param name="Position">The 1-based position in the pattern (0 when it concerns the whole rule set).</param>
/// <param name="Token">The token's source text (<c>{1}</c> of the UI text).</param>
/// <param name="Suggestion">The did-you-mean suggestion (<c>{2}</c>), e.g. <c>{givenName}</c> for <c>{imie}</c>.</param>
public sealed record PatternError(PatternErrorCode Code, string Attribute, int Position, string? Token = null, string? Suggestion = null)
{
    /// <summary>Error level (blocks every row); <see cref="PatternErrorCode.UnknownAttribute"/>, <see cref="PatternErrorCode.InvariantDateNames"/> and <see cref="PatternErrorCode.SamInAddress"/> are warnings.</summary>
    public bool IsError => Code is not (PatternErrorCode.UnknownAttribute or PatternErrorCode.InvariantDateNames or PatternErrorCode.SamInAddress);

    /// <summary>Code, attribute and position only (the token may quote operator text).</summary>
    public override string ToString() => $"{Code} in {Attribute} at {Position.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>The counter of a rule set: <c>{$nr:start[:width[:step]]}</c> (2.3).</summary>
/// <param name="Start">The first number, 0–999 999 999.</param>
/// <param name="Width">The minimum digits, zero-padded, 0–9 (0 = no padding).</param>
/// <param name="Step">The step, 1–1 000 000.</param>
public readonly record struct CounterSpec(int Start, int Width, int Step)
{
    /// <summary>The highest start, and the highest number a counter gives (2.4).</summary>
    public const int MaxNumber = 999_999_999;

    /// <summary>The widest zero padding.</summary>
    public const int MaxWidth = 9;

    /// <summary>The largest step.</summary>
    public const int MaxStep = 1_000_000;

    /// <summary><c>{$nr}</c>: start 1, no padding, step 1.</summary>
    public static CounterSpec Default { get; } = new(1, 0, 1);

    /// <summary>The number as the token writes it (zero-padded to <see cref="Width"/>; more digits are written in full).</summary>
    public string Format(int number)
    {
        var digits = number.ToString(CultureInfo.InvariantCulture);
        return Width > 0 && number >= 0 && digits.Length < Width ? digits.PadLeft(Width, '0') : digits;
    }

    /// <summary>The smallest number of this counter (<c>Start + k·Step</c>, k ≥ 0) at or above <paramref name="number"/>; null above <see cref="MaxNumber"/>.</summary>
    public long? GridAtOrAbove(long number)
    {
        if (number <= Start)
        {
            return Start;
        }

        var step = Math.Max(1, Step);
        var k = (number - Start + step - 1) / step;
        var grid = Start + (k * step);
        return grid > MaxNumber ? null : grid;
    }

    /// <summary>Whether <paramref name="number"/> is a number of this counter (<c>Start + k·Step</c>, k ≥ 0, at most <see cref="MaxNumber"/>).</summary>
    public bool IsOnGrid(long number) => number >= Start && number <= MaxNumber && (number - Start) % Math.Max(1, Step) == 0;

    /// <summary>
    /// Parses the argument of <c>{$nr:…}</c> (null = <see cref="Default"/>); <paramref name="error"/> is the English text of
    /// the engine's <c>ValidateArgument</c>. Decimal digits only (white space around them is ignored).
    /// </summary>
    public static bool TryParse(string? argument, out CounterSpec spec, out string? error)
    {
        spec = Default;
        error = null;
        if (argument is null)
        {
            return true;
        }

        var parts = argument.Split(':');
        if (parts.Length > 3)
        {
            error = "The counter takes at most three numbers: start, width and step.";
            return false;
        }

        if (!TryNumber(parts[0], 0, MaxNumber, out var start))
        {
            error = $"The counter start must be a number from 0 to {MaxNumber.ToString(CultureInfo.InvariantCulture)}.";
            return false;
        }

        var width = 0;
        if (parts.Length > 1 && !TryNumber(parts[1], 0, MaxWidth, out width))
        {
            error = $"The counter width must be a number from 0 to {MaxWidth.ToString(CultureInfo.InvariantCulture)}.";
            return false;
        }

        var step = 1;
        if (parts.Length > 2 && !TryNumber(parts[2], 1, MaxStep, out step))
        {
            error = $"The counter step must be a number from 1 to {MaxStep.ToString(CultureInfo.InvariantCulture)}.";
            return false;
        }

        spec = new CounterSpec(start, width, step);
        return true;
    }

    public override string ToString() =>
        $"Counter start={Start.ToString(CultureInfo.InvariantCulture)} width={Width.ToString(CultureInfo.InvariantCulture)} step={Step.ToString(CultureInfo.InvariantCulture)}";

    private static bool TryNumber(string text, int minimum, int maximum, out int value)
    {
        value = 0;
        var digits = text.Trim();
        if (digits.Length is 0 or > 10 || !digits.All(char.IsAsciiDigit)
            || !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < minimum || parsed > maximum)
        {
            return false;
        }

        value = (int)parsed;
        return true;
    }
}

/// <summary>What a rule set is compiled against (2.3).</summary>
/// <param name="Kind">The kind of the created objects.</param>
/// <param name="Columns">The table's columns (the non-password-like ones are the arguments of <c>{$column:…}</c>).</param>
/// <param name="ProvidedAttributes">Attributes fed by mapped columns (referenced attributes that are not rules).</param>
/// <param name="Schema">The session's schema (allowed attributes of the class).</param>
public sealed record PatternScope(ObjectKind Kind, IReadOnlyList<TableColumn> Columns, IReadOnlyCollection<string> ProvidedAttributes, ISchemaCatalog Schema)
{
    public override string ToString() => $"Pattern scope: {Kind}, {Columns.Count} columns, {ProvidedAttributes.Count} provided attributes";
}

/// <summary>The data one row renders with (2.3); value-free <see cref="ToString"/>.</summary>
public sealed record PatternRow
{
    /// <summary>The computed values of an attribute for this row (the first value is used).</summary>
    public required Func<string, IReadOnlyList<string>> Attribute { get; init; }

    /// <summary>The cell of a column by header or letter (password-like columns are unreachable).</summary>
    public required Func<string, string> Column { get; init; }

    /// <summary>
    /// The counter number – decimal digits, which every <c>{$nr…}</c> token writes with its own width (a number already
    /// zero-padded is accepted) – or the allocation sentinel U+E000 of 2.4 (<see cref="CreationPatternTokens.NumberSentinel"/>);
    /// null = no number.
    /// </summary>
    public string? Number { get; init; }

    /// <summary>The UPN suffix of the run (<c>{$suffix}</c>).</summary>
    public string? UpnSuffix { get; init; }

    /// <summary>The mail domain of the run (<c>{$mailDomain}</c>; null = the UPN suffix).</summary>
    public string? MailDomain { get; init; }

    /// <summary>The DNS name of the window's domain (<c>{$domain}</c>).</summary>
    public string Domain { get; init; } = string.Empty;

    /// <summary>The target container (<c>{$parent}</c>, <c>{$parentName}</c>).</summary>
    public string ContainerDn { get; init; } = string.Empty;

    /// <summary>The run's local time (<c>{$date:…}</c>).</summary>
    public DateTimeOffset Now { get; init; }

    public override string ToString() =>
        $"Pattern row (number={(Number is not null)}, suffix={(UpnSuffix is not null)}, mail domain={(MailDomain is not null)})";
}

/// <summary>
/// The compiled naming rules of a run (docs/dev/phase3-bulk-create.md 2.3, owner B2): errors, the counter, the evaluation
/// order and dependencies, and pure, thread-safe rendering per row.
/// </summary>
/// <remarks>
/// A rule set is split into tokens as the template engine of the batch editor splits it (one syntax, one function set), and
/// each token's own parse by that engine decides its validity; the problems are kept as codes with positions – never the
/// engine's English texts. Attributes are evaluated in the topological order of their references, ties in the fixed order
/// <c>cn</c>, <c>givenName</c>, <c>sn</c>, <c>initials</c>, <c>displayName</c>, <c>sAMAccountName</c>,
/// <c>userPrincipalName</c>, <c>mail</c>, then by name.
/// </remarks>
public sealed class CreationPatternSet
{
    /// <summary>The attributes whose ties are broken in this order (2.3); every other attribute follows by name.</summary>
    private static readonly string[] FixedOrder =
        [Attr.Cn, Attr.GivenName, Attr.Sn, Attr.Initials, Attr.DisplayName, Attr.SamAccountName, Attr.UserPrincipalName, Attr.Mail];

    private readonly TemplateEngine _engine;
    private readonly Dictionary<string, CompiledRule> _rules;
    private readonly Dictionary<string, IReadOnlyCollection<string>> _dependsOn;
    private readonly ConcurrentDictionary<string, string?> _parentProbes = new(StringComparer.Ordinal);

    private CreationPatternSet(TemplateEngine engine, Dictionary<string, CompiledRule> rules, Dictionary<string, IReadOnlyCollection<string>> dependsOn)
    {
        _engine = engine;
        _rules = rules;
        _dependsOn = dependsOn;
    }

    /// <summary>The pattern problems (error- and warning-level, <see cref="PatternError.IsError"/>).</summary>
    public IReadOnlyList<PatternError> Errors { get; private init; } = [];

    /// <summary>No error-level problem.</summary>
    public bool IsValid => Errors.All(e => !e.IsError);

    /// <summary>The one counter of the rule set; null when no rule uses <c>{$nr}</c>.</summary>
    public CounterSpec? Counter { get; private init; }

    /// <summary>The rule attributes in evaluation order (topological; ties in the fixed order of 2.3).</summary>
    public IReadOnlyList<string> EvaluationOrder { get; private init; } = [];

    /// <summary>Attributes whose rule uses <c>{$nr}</c> directly or through references.</summary>
    public IReadOnlyCollection<string> CounterAttributes { get; private init; } = [];

    /// <summary>Compiles <paramref name="rules"/> with an engine built for the table (<see cref="CreationPatternTokens.Extensions"/>).</summary>
    public static CreationPatternSet Compile(IReadOnlyList<AttributeRule> rules, PatternScope scope)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(scope);
        var reachableColumns = CreationPatternTokens.ReachableColumnNames(scope.Columns);
        var engine = CreationPatternTokens.EngineFor(reachableColumns);
        var errors = new List<PatternError>();
        var compiled = new Dictionary<string, CompiledRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            ArgumentNullException.ThrowIfNull(rule);
            var attribute = Canonical(rule.Attribute?.Trim() ?? string.Empty, scope.Schema);
            if (attribute.Length == 0 || CreationValues.IsBlockedAttribute(attribute, scope.Kind, scope.Schema))
            {
                errors.Add(new PatternError(PatternErrorCode.BlockedAttribute, attribute, 0));
                continue;
            }

            if (compiled.ContainsKey(attribute))
            {
                errors.Add(new PatternError(PatternErrorCode.DuplicateRule, attribute, 0));
                continue;
            }

            compiled[attribute] = CompileRule(attribute, rule, engine, reachableColumns, errors);
        }

        var known = KnownAttributes(scope);
        var provided = new HashSet<string>(scope.ProvidedAttributes, StringComparer.OrdinalIgnoreCase);
        foreach (var rule in compiled.Values)
        {
            ResolveReferences(rule, compiled, provided, known, scope, errors);
        }

        var counter = ResolveCounter(compiled.Values, errors);
        var dependsOn = compiled.Keys.ToDictionary(a => a, a => Closure(a, compiled), StringComparer.OrdinalIgnoreCase);
        var counterAttributes = compiled.Values
            .Where(r => r.UsesCounter || dependsOn[r.Attribute].Any(d => compiled.TryGetValue(d, out var other) && other.UsesCounter))
            .Select(r => r.Attribute)
            .ToList();
        var order = Order(compiled, errors);
        return new CreationPatternSet(engine, compiled, dependsOn)
        {
            Errors = errors,
            Counter = counter,
            EvaluationOrder = order,
            CounterAttributes = counterAttributes,
        };
    }

    /// <summary>The quick fix of 2.6: a literal ending in digits as a counter (<c>kd201</c> → <c>kd{$nr:201}</c>, <c>kd0201</c> → <c>kd{$nr:201:4}</c>); null otherwise.</summary>
    public static string? SuggestCounter(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.IndexOfAny(['{', '}']) >= 0)
        {
            return null;
        }

        var start = pattern.Length;
        while (start > 0 && char.IsAsciiDigit(pattern[start - 1]))
        {
            start--;
        }

        var digits = pattern[start..];
        if (digits.Length is 0 or > 9)
        {
            return null;
        }

        var number = int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        var argument = digits.Length > 1 && digits[0] == '0' ? $"{number}:{digits.Length.ToString(CultureInfo.InvariantCulture)}" : number;
        return pattern[..start] + "{$" + CreationPatternTokens.Number + ":" + argument + "}";
    }

    /// <summary>The attributes the rule of <paramref name="attribute"/> references, directly or indirectly.</summary>
    public IReadOnlyCollection<string> DependsOn(string attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        return _dependsOn.TryGetValue(attribute, out var closure) ? closure : [];
    }

    /// <summary>The attributes the rule of <paramref name="attribute"/> references itself (not through other rules).</summary>
    public IReadOnlyList<string> DirectReferences(string attribute) =>
        _rules.TryGetValue(attribute ?? throw new ArgumentNullException(nameof(attribute)), out var rule) ? rule.References : [];

    /// <summary>The rule of <paramref name="attribute"/> exists (it may still have problems).</summary>
    public bool HasRule(string attribute) => _rules.ContainsKey(attribute ?? throw new ArgumentNullException(nameof(attribute)));

    /// <summary>The rule of <paramref name="attribute"/> comes before a mapped column (<see cref="AttributeRule.OverridesColumn"/>).</summary>
    public bool OverridesColumn(string attribute) =>
        _rules.TryGetValue(attribute ?? throw new ArgumentNullException(nameof(attribute)), out var rule) && rule.OverridesColumn;

    /// <summary>The rule of <paramref name="attribute"/> has no token: a fixed value (<see cref="BulkValueSource.Fixed"/>).</summary>
    public bool IsFixed(string attribute) =>
        _rules.TryGetValue(attribute ?? throw new ArgumentNullException(nameof(attribute)), out var rule) && !rule.HasTokens;

    /// <summary>The own pattern of <paramref name="attribute"/> contains <c>{$nr}</c> (not only through references).</summary>
    public bool UsesCounterDirectly(string attribute) =>
        _rules.TryGetValue(attribute ?? throw new ArgumentNullException(nameof(attribute)), out var rule) && rule.UsesCounter;

    /// <summary>The rule of <paramref name="attribute"/> renders (no error-level problem in its own pattern, not in a cycle).</summary>
    public bool CanRender(string attribute) =>
        _rules.TryGetValue(attribute ?? throw new ArgumentNullException(nameof(attribute)), out var rule) && rule.IsRenderable;

    /// <summary>Renders the rule of <paramref name="attribute"/> for one row (before <see cref="CreationValues.Finish"/>).</summary>
    public string Render(string attribute, PatternRow row) => Render(attribute, row, null);

    /// <summary>
    /// Renders the rule of <paramref name="attribute"/> for one row (before <see cref="CreationValues.Finish"/>);
    /// <paramref name="emptyToken"/> receives the source text of every token that rendered empty (the row warning
    /// <c>EmptyToken</c> of 2.3). An attribute without a rule, or whose rule cannot render (<see cref="CanRender"/>), renders
    /// empty.
    /// </summary>
    public string Render(string attribute, PatternRow row, Action<string>? emptyToken)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(row);
        if (!_rules.TryGetValue(attribute, out var rule) || !rule.IsRenderable)
        {
            return string.Empty;
        }

        var context = new TemplateContext
        {
            Attribute = name => row.Attribute(name),
            DistinguishedName = ParentProbe(row.ContainerDn),
            Domain = string.IsNullOrEmpty(row.Domain) ? null : row.Domain,
            Now = row.Now,
            Extension = (name, argument) => EvaluateExtension(rule, name, argument, row),
            EmptyValue = emptyToken,
        };
        return _engine.Render(rule.Pattern, context);
    }

    public override string ToString() => $"Pattern set: {EvaluationOrder.Count} rules, {Errors.Count} problems, counter={(Counter is not null)}";

    private static string Canonical(string attribute, ISchemaCatalog schema) =>
        attribute.Length > 0 && schema.FindAttribute(attribute) is { } definition ? definition.LdapName : attribute;

    /// <summary>The attributes of the class (empty without a schema: references are then not checked).</summary>
    private static HashSet<string> KnownAttributes(PatternScope scope) =>
        scope.Schema.Attributes.Count == 0
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(scope.Schema.GetAllowedAttributes(CreationNames.ClassChainOf(scope.Kind)), StringComparer.OrdinalIgnoreCase);

    private static CompiledRule CompileRule(string attribute, AttributeRule rule, TemplateEngine engine, IReadOnlyList<string> reachableColumns, List<PatternError> errors)
    {
        var pattern = rule.Pattern ?? string.Empty;
        var compiled = new CompiledRule(attribute, pattern, rule.OverridesColumn);
        var problems = new List<PatternError>();
        var tokens = PatternScanner.Scan(pattern, attribute, problems);
        compiled.HasTokens = tokens.Count > 0;
        var isAddress = string.Equals(attribute, Attr.UserPrincipalName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(attribute, Attr.Mail, StringComparison.OrdinalIgnoreCase);
        foreach (var token in tokens)
        {
            if (token.IsBroken)
            {
                continue;
            }

            var parsed = engine.Parse(token.Source);
            if (token.SpecialName is { } special)
            {
                CheckSpecial(compiled, token, special, parsed, reachableColumns, problems);
            }
            else if (!parsed.IsValid)
            {
                problems.Add(new PatternError(PatternErrorCode.Syntax, attribute, token.Position, token.Source));
            }
            else
            {
                compiled.Tokens.Add(token);
            }

            if (isAddress && token.Functions.Contains("sam", StringComparer.Ordinal))
            {
                problems.Add(new PatternError(PatternErrorCode.SamInAddress, attribute, token.Position, token.Source));
            }
        }

        // The engine judges the whole pattern too: a problem the token scan did not see still blocks rendering.
        if (problems.All(p => !p.IsError) && !engine.Parse(pattern).IsValid)
        {
            problems.Add(new PatternError(PatternErrorCode.Syntax, attribute, 1));
        }

        compiled.IsRenderable = problems.All(p => !p.IsError);
        errors.AddRange(problems);
        return compiled;
    }

    private static void CheckSpecial(CompiledRule rule, PatternToken token, string special, TemplateParseResult parsed, IReadOnlyList<string> reachableColumns, List<PatternError> problems)
    {
        if (CreationPatternTokens.Forbidden.Contains(special, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add(new PatternError(PatternErrorCode.ForbiddenToken, rule.Attribute, token.Position, token.Source));
            return;
        }

        if (string.Equals(special, CreationPatternTokens.Number, StringComparison.OrdinalIgnoreCase))
        {
            if (!CounterSpec.TryParse(token.Argument, out var spec, out _))
            {
                problems.Add(new PatternError(PatternErrorCode.InvalidCounter, rule.Attribute, token.Position, token.Source));
            }
            else if (!parsed.IsValid)
            {
                problems.Add(new PatternError(PatternErrorCode.Syntax, rule.Attribute, token.Position, token.Source));
            }
            else
            {
                rule.UsesCounter = true;
                rule.CounterTokens.Add(new CounterToken(token, token.Argument is null ? null : spec));
            }

            return;
        }

        if (string.Equals(special, CreationPatternTokens.Column, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(token.Argument) || !CreationPatternTokens.IsReachable(token.Argument, reachableColumns)))
        {
            problems.Add(new PatternError(PatternErrorCode.UnknownColumn, rule.Attribute, token.Position, token.Source));
            return;
        }

        if (!parsed.IsValid)
        {
            problems.Add(new PatternError(PatternErrorCode.Syntax, rule.Attribute, token.Position, token.Source));
            return;
        }

        if (string.Equals(special, "date", StringComparison.OrdinalIgnoreCase) && token.Argument is { } format
            && (format.Contains("MMM", StringComparison.Ordinal) || format.Contains("ddd", StringComparison.Ordinal)))
        {
            problems.Add(new PatternError(PatternErrorCode.InvariantDateNames, rule.Attribute, token.Position, token.Source));
        }
    }

    private static void ResolveReferences(
        CompiledRule rule,
        Dictionary<string, CompiledRule> rules,
        HashSet<string> provided,
        HashSet<string> known,
        PatternScope scope,
        List<PatternError> errors)
    {
        foreach (var token in rule.Tokens)
        {
            var name = token.Head;
            string reference;
            if (rules.TryGetValue(name, out var target))
            {
                reference = target.Attribute;
            }
            else if (provided.TryGetValue(name, out var mapped))
            {
                reference = mapped;
            }
            else
            {
                reference = Canonical(name, scope.Schema);
                if (known.Count > 0 && !known.Contains(reference))
                {
                    errors.Add(new PatternError(PatternErrorCode.UnknownAttribute, rule.Attribute, token.Position, token.Source,
                        PatternSuggestions.For(name, scope, rules.Values.Select(r => r.Attribute), provided, known)));
                }
            }

            if (!rule.References.Contains(reference, StringComparer.OrdinalIgnoreCase))
            {
                rule.References.Add(reference);
            }
        }
    }

    /// <summary>One counter per rule set: tokens with an argument agree on start and step (2.3); the others take that spec.</summary>
    private static CounterSpec? ResolveCounter(IEnumerable<CompiledRule> rules, List<PatternError> errors)
    {
        CounterSpec? first = null;
        var any = false;
        foreach (var rule in rules)
        {
            foreach (var counter in rule.CounterTokens)
            {
                any = true;
                if (counter.Spec is not { } explicitSpec)
                {
                    continue;
                }

                if (first is not { } defined)
                {
                    first = explicitSpec;
                }
                else if (defined.Start != explicitSpec.Start || defined.Step != explicitSpec.Step)
                {
                    errors.Add(new PatternError(PatternErrorCode.CounterMismatch, rule.Attribute, counter.Token.Position, counter.Token.Source));
                    rule.IsRenderable = false;
                }
            }

            rule.CounterWidth = rule.CounterTokens.Select(t => t.Spec?.Width).FirstOrDefault(w => w is not null);
        }

        return any ? first ?? CounterSpec.Default : null;
    }

    private static IReadOnlyCollection<string> Closure(string attribute, Dictionary<string, CompiledRule> rules)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { attribute };
        var queue = new Queue<string>([attribute]);
        while (queue.Count > 0)
        {
            if (!rules.TryGetValue(queue.Dequeue(), out var rule))
            {
                continue;
            }

            foreach (var reference in rule.References)
            {
                if (seen.Add(reference))
                {
                    result.Add(reference);
                    queue.Enqueue(reference);
                }
            }
        }

        return result;
    }

    /// <summary>The rules in topological order (a rule after the rules it references); the members of a cycle get <see cref="PatternErrorCode.Cycle"/> and come last.</summary>
    private static List<string> Order(Dictionary<string, CompiledRule> rules, List<PatternError> errors)
    {
        var dependencies = rules.Values.ToDictionary(
            r => r.Attribute,
            r => r.References.Where(rules.ContainsKey).Select(d => rules[d].Attribute).ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (dependencies.Keys
                   .Where(a => !done.Contains(a) && dependencies[a].All(d => done.Contains(d) && !string.Equals(d, a, StringComparison.OrdinalIgnoreCase)))
                   .OrderBy(Rank).ThenBy(a => a, StringComparer.OrdinalIgnoreCase)
                   .FirstOrDefault() is { } ready)
        {
            order.Add(ready);
            done.Add(ready);
        }

        var remaining = dependencies.Keys.Where(a => !done.Contains(a)).OrderBy(Rank).ThenBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
        if (remaining.Count > 0)
        {
            // An attribute that only waits for a cycle is not part of it: the members of a cycle reach themselves.
            var cyclic = remaining.Where(a => Reaches(a, a, dependencies, done)).ToList();
            var names = string.Join(" → ", cyclic.Append(cyclic[0]));
            foreach (var attribute in cyclic)
            {
                errors.Add(new PatternError(PatternErrorCode.Cycle, attribute, 0, names));
                rules[attribute].IsRenderable = false;
            }

            order.AddRange(remaining);
        }

        return order;
    }

    private static bool Reaches(string from, string target, Dictionary<string, HashSet<string>> dependencies, HashSet<string> done)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>(dependencies[from]);
        while (stack.Count > 0)
        {
            var next = stack.Pop();
            if (string.Equals(next, target, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!done.Contains(next) && seen.Add(next) && dependencies.TryGetValue(next, out var further))
            {
                foreach (var dependency in further)
                {
                    stack.Push(dependency);
                }
            }
        }

        return false;
    }

    private static int Rank(string attribute)
    {
        var index = Array.FindIndex(FixedOrder, a => string.Equals(a, attribute, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? FixedOrder.Length : index;
    }

    private string? ParentProbe(string containerDn) =>
        string.IsNullOrEmpty(containerDn)
            ? null
            : _parentProbes.GetOrAdd(containerDn, dn => Ldap.DistinguishedName.TryParse(dn, out var parsed) ? parsed.Child("CN", "_").ToString() : null);

    private string EvaluateExtension(CompiledRule rule, string name, string? argument, PatternRow row)
    {
        if (string.Equals(name, CreationPatternTokens.Number, StringComparison.OrdinalIgnoreCase))
        {
            return FormatNumber(rule, row.Number, argument);
        }

        if (string.Equals(name, CreationPatternTokens.Column, StringComparison.OrdinalIgnoreCase))
        {
            return argument is null ? string.Empty : row.Column(argument) ?? string.Empty;
        }

        if (string.Equals(name, CreationPatternTokens.Suffix, StringComparison.OrdinalIgnoreCase))
        {
            return row.UpnSuffix ?? string.Empty;
        }

        return string.Equals(name, CreationPatternTokens.MailDomain, StringComparison.OrdinalIgnoreCase)
            ? row.MailDomain ?? row.UpnSuffix ?? string.Empty
            : string.Empty;
    }

    private string FormatNumber(CompiledRule rule, string? number, string? argument)
    {
        if (number is null || number == CreationPatternTokens.NumberSentinel
            || !int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return number ?? string.Empty;
        }

        var width = argument is not null && CounterSpec.TryParse(argument, out var spec, out _) ? spec.Width : rule.CounterWidth ?? Counter?.Width ?? 0;
        return (CounterSpec.Default with { Width = width }).Format(value);
    }

    /// <summary>A counter token of a rule and its explicit spec (null: <c>{$nr}</c> without an argument).</summary>
    private sealed class CounterToken(PatternToken token, CounterSpec? spec)
    {
        public PatternToken Token { get; } = token;

        public CounterSpec? Spec { get; } = spec;
    }

    /// <summary>One compiled rule (a class: it holds patterns, never a directory value).</summary>
    private sealed class CompiledRule(string attribute, string pattern, bool overridesColumn)
    {
        public string Attribute { get; } = attribute;

        public string Pattern { get; } = pattern;

        public bool OverridesColumn { get; } = overridesColumn;

        public bool HasTokens { get; set; }

        public bool IsRenderable { get; set; }

        public bool UsesCounter { get; set; }

        /// <summary>The width of the rule's first <c>{$nr:…}</c> with an argument (null: the set's width).</summary>
        public int? CounterWidth { get; set; }

        /// <summary>The valid attribute tokens (the references).</summary>
        public List<PatternToken> Tokens { get; } = [];

        public List<string> References { get; } = [];

        public List<CounterToken> CounterTokens { get; } = [];
    }
}

/// <summary>The creation tokens of the template engine (2.3).</summary>
public static class CreationPatternTokens
{
    /// <summary><c>{$nr}</c>, <c>{$nr:start[:width[:step]]}</c>.</summary>
    public const string Number = "nr";

    /// <summary><c>{$column:Header}</c> / <c>{$column:C}</c>.</summary>
    public const string Column = "column";

    /// <summary><c>{$suffix}</c>: the UPN suffix of the run.</summary>
    public const string Suffix = "suffix";

    /// <summary><c>{$mailDomain}</c>: the mail domain of the run.</summary>
    public const string MailDomain = "mailDomain";

    /// <summary>
    /// The allocation sentinel U+E000 of 2.4 (a private-use character): rendered in place of the counter number, it splits a
    /// counter value into its prefix and suffix.
    /// </summary>
    public const string NumberSentinel = "";

    /// <summary>Special tokens of the engine that creation patterns refuse (<see cref="PatternErrorCode.ForbiddenToken"/>).</summary>
    public static IReadOnlyList<string> Forbidden { get; } = ["dn", "rdn", "counter"];

    /// <summary>The extension tokens of an engine for <paramref name="columns"/> (the non-password-like headers and letters are the allowed arguments of <c>$column</c>).</summary>
    public static IReadOnlyList<TemplateExtensionToken> Extensions(IReadOnlyList<TableColumn> columns) =>
        TokensFor(ReachableColumnNames(columns ?? throw new ArgumentNullException(nameof(columns))));

    /// <summary>
    /// The headers and Excel letters <c>{$column:…}</c> may name (2.3): those of every column that is not password-like
    /// (<see cref="ColumnSynonyms.IsPasswordLike"/>) – neither by header nor by letter; a column without a header is reached
    /// by its letter.
    /// </summary>
    public static IReadOnlyList<string> ReachableColumnNames(IReadOnlyList<TableColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var names = new List<string>();
        foreach (var column in columns)
        {
            var header = column.Header.Trim();
            if (header.Length > 0)
            {
                if (ColumnSynonyms.IsPasswordLike(header))
                {
                    continue;
                }

                names.Add(header);
            }

            names.Add(column.Letter);
        }

        return names;
    }

    /// <summary>Whether <paramref name="argument"/> is one of <paramref name="names"/> as the engine compares arguments (trimmed, case- and diacritics-insensitive).</summary>
    internal static bool IsReachable(string argument, IReadOnlyList<string> names)
    {
        var folded = Diacritics.Remove(argument.Trim());
        return names.Any(n => string.Equals(Diacritics.Remove(n.Trim()), folded, StringComparison.OrdinalIgnoreCase));
    }

    private static List<TemplateExtensionToken> TokensFor(IReadOnlyList<string> reachableColumns) =>
    [
        new TemplateExtensionToken(Number, TemplateTokenArgument.Optional)
        {
            ValidateArgument = argument => CounterSpec.TryParse(argument, out _, out var error) ? null : error,
            ReportsEmpty = false,
        },
        new TemplateExtensionToken(Column, TemplateTokenArgument.Required) { AllowedArguments = reachableColumns },
        new TemplateExtensionToken(Suffix, TemplateTokenArgument.None),
        new TemplateExtensionToken(MailDomain, TemplateTokenArgument.None),
    ];

    /// <summary>The engine of a compiled set (its column names already filtered).</summary>
    internal static TemplateEngine EngineFor(IReadOnlyList<string> reachableColumns) => new(TokensFor(reachableColumns));
}

/// <summary>One token of a pattern as the engine splits it (position, source text, head, functions); prints its position only.</summary>
internal sealed class PatternToken
{
    /// <summary>The 1-based position of its <c>{</c>.</summary>
    public required int Position { get; init; }

    public required string Source { get; init; }

    /// <summary>The attribute name (attribute tokens) or the name after <c>$</c>.</summary>
    public string Head { get; init; } = string.Empty;

    /// <summary>The name after <c>$</c>, or null for an attribute token.</summary>
    public string? SpecialName { get; init; }

    /// <summary>The argument after the first <c>:</c> of a special token (unescaped), or null.</summary>
    public string? Argument { get; init; }

    /// <summary>The function names (lower case).</summary>
    public IReadOnlyList<string> Functions { get; init; } = [];

    /// <summary>The scan reported it already (an unescaped <c>{</c> inside, a dangling escape, unclosed).</summary>
    public bool IsBroken { get; init; }

    public override string ToString() => $"Token at {Position.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>Splits a pattern into tokens exactly as <see cref="TemplateEngine"/> does (the grammar of 2.3).</summary>
internal static class PatternScanner
{
    public static List<PatternToken> Scan(string pattern, string attribute, List<PatternError> problems)
    {
        var tokens = new List<PatternToken>();
        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            if (c == '{' && i + 1 < pattern.Length && pattern[i + 1] == '{')
            {
                i += 2;
            }
            else if (c == '}' && i + 1 < pattern.Length && pattern[i + 1] == '}')
            {
                i += 2;
            }
            else if (c == '}')
            {
                problems.Add(new PatternError(PatternErrorCode.Syntax, attribute, i + 1, "}"));
                i++;
            }
            else if (c == '{')
            {
                i = ScanToken(pattern, i, attribute, tokens, problems);
            }
            else
            {
                i++;
            }
        }

        return tokens;
    }

    private static int ScanToken(string pattern, int start, string attribute, List<PatternToken> tokens, List<PatternError> problems)
    {
        var chars = new List<TokenChar>();
        var i = start + 1;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            if (c == '\\')
            {
                if (i + 1 >= pattern.Length)
                {
                    break;
                }

                chars.Add(new TokenChar(pattern[i + 1], true));
                i += 2;
                continue;
            }

            if (c == '}')
            {
                tokens.Add(Build(pattern[start..(i + 1)], start + 1, chars));
                return i + 1;
            }

            if (c == '{')
            {
                var end = pattern.IndexOf('}', i);
                var stop = end < 0 ? pattern.Length : end + 1;
                problems.Add(new PatternError(PatternErrorCode.Syntax, attribute, start + 1, pattern[start..stop]));
                tokens.Add(new PatternToken { Position = start + 1, Source = pattern[start..stop], IsBroken = true });
                return stop;
            }

            chars.Add(new TokenChar(c, false));
            i++;
        }

        problems.Add(new PatternError(PatternErrorCode.Syntax, attribute, start + 1, pattern[start..]));
        tokens.Add(new PatternToken { Position = start + 1, Source = pattern[start..], IsBroken = true });
        return pattern.Length;
    }

    private static PatternToken Build(string source, int position, List<TokenChar> chars)
    {
        var parts = Split(chars, '|');
        var head = Trim(parts[0]);
        var functions = parts.Skip(1).Select(p => Text(Split(Trim(p), ':')[0]).Trim().ToLowerInvariant()).ToList();
        if (head.Count > 0 && head[0] is { Value: '$', Escaped: false })
        {
            var nameAndArgument = Split(head.Skip(1).ToList(), ':');
            var name = Text(nameAndArgument[0]).Trim();
            return new PatternToken
            {
                Position = position,
                Source = source,
                Head = name,
                SpecialName = name,
                Argument = nameAndArgument.Count > 1 ? string.Join(':', nameAndArgument.Skip(1).Select(Text)) : null,
                Functions = functions,
            };
        }

        return new PatternToken { Position = position, Source = source, Head = Text(head), Functions = functions };
    }

    private static List<List<TokenChar>> Split(List<TokenChar> chars, char separator)
    {
        var result = new List<List<TokenChar>> { new() };
        foreach (var c in chars)
        {
            if (c.Value == separator && !c.Escaped)
            {
                result.Add([]);
            }
            else
            {
                result[^1].Add(c);
            }
        }

        return result;
    }

    private static List<TokenChar> Trim(List<TokenChar> chars)
    {
        var start = 0;
        var end = chars.Count;
        while (start < end && !chars[start].Escaped && char.IsWhiteSpace(chars[start].Value))
        {
            start++;
        }

        while (end > start && !chars[end - 1].Escaped && char.IsWhiteSpace(chars[end - 1].Value))
        {
            end--;
        }

        return chars.GetRange(start, end - start);
    }

    private static string Text(List<TokenChar> chars) => string.Concat(chars.Select(c => c.Value));

    /// <summary>A character of a token and whether it was escaped with <c>\</c>.</summary>
    private readonly struct TokenChar(char value, bool escaped)
    {
        public char Value { get; } = value;

        public bool Escaped { get; } = escaped;
    }
}

/// <summary>The did-you-mean of an unknown attribute (2.3): <c>{imie}</c> → <c>{givenName}</c>.</summary>
internal static class PatternSuggestions
{
    public static string? For(string name, PatternScope scope, IEnumerable<string> rules, IEnumerable<string> provided, IEnumerable<string> known)
    {
        var folded = ColumnSynonyms.Fold(name);
        if (folded.Length == 0)
        {
            return null;
        }

        var candidates = rules.Concat(provided).Concat(known).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var synonym in ColumnSynonyms.Entries)
        {
            if (synonym.Folded == folded && (synonym.OnlyFor is null || synonym.OnlyFor == scope.Kind)
                && synonym.Target is { Kind: ColumnTargetKind.Attribute, Attribute: { } attribute }
                && candidates.TryGetValue(attribute, out var canonical))
            {
                return "{" + canonical + "}";
            }
        }

        var reachable = CreationPatternTokens.ReachableColumnNames(scope.Columns);
        foreach (var column in scope.Columns)
        {
            var header = column.Header.Trim();
            if (header.Length > 0 && ColumnSynonyms.Fold(header) == folded && CreationPatternTokens.IsReachable(header, reachable))
            {
                return "{$" + CreationPatternTokens.Column + ":" + EscapeArgument(header) + "}";
            }
        }

        return candidates.FirstOrDefault(c => ColumnSynonyms.Fold(c) == folded) is { } match ? "{" + match + "}" : null;
    }

    /// <summary><c>|</c>, <c>{</c>, <c>}</c> and <c>\</c> are escaped with <c>\</c> inside a token.</summary>
    private static string EscapeArgument(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '|' or '{' or '}' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
