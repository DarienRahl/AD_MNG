using System.Globalization;
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
    /// <summary><c>{$nr}</c>: start 1, no padding, step 1.</summary>
    public static CounterSpec Default { get; } = new(1, 0, 1);

    /// <summary>The number as the token writes it (zero-padded to <see cref="Width"/>; more digits are written in full).</summary>
    public string Format(int number) =>
        throw new NotImplementedException("F3 B2: CounterSpec.Format (2.3).");

    /// <summary>
    /// Parses the argument of <c>{$nr:…}</c> (null = <see cref="Default"/>); <paramref name="error"/> is the English text of
    /// the engine's <c>ValidateArgument</c>.
    /// </summary>
    public static bool TryParse(string? argument, out CounterSpec spec, out string? error) =>
        throw new NotImplementedException("F3 B2: CounterSpec.TryParse (2.3).");

    public override string ToString() =>
        $"Counter start={Start.ToString(CultureInfo.InvariantCulture)} width={Width.ToString(CultureInfo.InvariantCulture)} step={Step.ToString(CultureInfo.InvariantCulture)}";
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

    /// <summary>The formatted counter number, or the allocation sentinel U+E000 of 2.4; null = no number.</summary>
    public string? Number { get; init; }

    /// <summary>The UPN suffix of the run (<c>{$suffix}</c>).</summary>
    public string? UpnSuffix { get; init; }

    /// <summary>The mail domain of the run (<c>{$mailDomain}</c>).</summary>
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
public sealed class CreationPatternSet
{
    private CreationPatternSet()
    {
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
    public static CreationPatternSet Compile(IReadOnlyList<AttributeRule> rules, PatternScope scope) =>
        throw new NotImplementedException("F3 B2: CreationPatternSet.Compile (2.3).");

    /// <summary>The quick fix of 2.6: a literal ending in digits as a counter (<c>kd201</c> → <c>kd{$nr:201}</c>, <c>kd0201</c> → <c>kd{$nr:201:4}</c>); null otherwise.</summary>
    public static string? SuggestCounter(string pattern) =>
        throw new NotImplementedException("F3 B2: CreationPatternSet.SuggestCounter (2.3).");

    /// <summary>The attributes the rule of <paramref name="attribute"/> references, directly or indirectly.</summary>
    public IReadOnlyCollection<string> DependsOn(string attribute) =>
        throw new NotImplementedException("F3 B2: CreationPatternSet.DependsOn (2.3).");

    /// <summary>Renders the rule of <paramref name="attribute"/> for one row (before <see cref="CreationValues.Finish"/>).</summary>
    public string Render(string attribute, PatternRow row) =>
        throw new NotImplementedException("F3 B2: CreationPatternSet.Render (2.3).");

    public override string ToString() => $"Pattern set: {EvaluationOrder.Count} rules, {Errors.Count} problems, counter={(Counter is not null)}";
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

    /// <summary>Special tokens of the engine that creation patterns refuse (<see cref="PatternErrorCode.ForbiddenToken"/>).</summary>
    public static IReadOnlyList<string> Forbidden { get; } = ["dn", "rdn", "counter"];

    /// <summary>The extension tokens of an engine for <paramref name="columns"/> (the non-password-like headers and letters are the allowed arguments of <c>$column</c>).</summary>
    public static IReadOnlyList<TemplateExtensionToken> Extensions(IReadOnlyList<TableColumn> columns) =>
        throw new NotImplementedException("F3 B2: CreationPatternTokens.Extensions (2.3).");
}
