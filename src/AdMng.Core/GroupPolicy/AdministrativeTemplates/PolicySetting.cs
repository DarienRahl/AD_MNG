namespace AdMng.Core.GroupPolicy.AdministrativeTemplates;

/// <summary>The state of a policy in a GPO ("Nie skonfigurowano", "Włączone", "Wyłączone").</summary>
public enum PolicyState
{
    NotConfigured,
    Enabled,
    Disabled,

    /// <summary>Registry.pol holds values of the policy that match neither state (written by another tool, or a newer ADMX).</summary>
    Inconsistent,
}

/// <summary>The value of one element of an enabled policy.</summary>
public abstract record PolicyElementValue;

/// <summary>A check box.</summary>
public sealed record BooleanElementValue(bool Value) : PolicyElementValue;

/// <summary>A decimal or long decimal number.</summary>
public sealed record NumberElementValue(ulong Value) : PolicyElementValue;

/// <summary>A text.</summary>
public sealed record TextElementValue(string Value) : PolicyElementValue;

/// <summary>Lines of a multi-line text.</summary>
public sealed record MultiTextElementValue(IReadOnlyList<string> Lines) : PolicyElementValue
{
    public bool Equals(MultiTextElementValue? other) => other is not null && Lines.SequenceEqual(other.Lines, StringComparer.Ordinal);

    public override int GetHashCode() => Lines.Count;
}

/// <summary>The chosen item of an enum (0-based index into <see cref="EnumElement.Items"/>).</summary>
public sealed record EnumElementValue(int Index) : PolicyElementValue;

/// <summary>An entry of a list element: its value name (explicit lists only; otherwise empty) and data.</summary>
/// <param name="Name">The value name of an explicit list; empty otherwise.</param>
/// <param name="Value">The data.</param>
public sealed record ListElementItem(string Name, string Value);

/// <summary>The entries of a list element.</summary>
public sealed record ListElementValue(IReadOnlyList<ListElementItem> Items) : PolicyElementValue
{
    public bool Equals(ListElementValue? other) => other is not null && Items.SequenceEqual(other.Items);

    public override int GetHashCode() => Items.Count;
}

/// <summary>A policy's state and, when enabled, its element values (element id → value; ids compared without case).</summary>
public sealed record PolicySetting(PolicyState State, IReadOnlyDictionary<string, PolicyElementValue> Values)
{
    // Declared first: static initializers run in order, and the states below use it.
    private static IReadOnlyDictionary<string, PolicyElementValue> Empty { get; } = new Dictionary<string, PolicyElementValue>(StringComparer.OrdinalIgnoreCase);

    public static PolicySetting NotConfigured { get; } = new(PolicyState.NotConfigured, Empty);

    public static PolicySetting Disabled { get; } = new(PolicyState.Disabled, Empty);

    public static PolicySetting Enabled(IReadOnlyDictionary<string, PolicyElementValue> values) =>
        new(PolicyState.Enabled, new Dictionary<string, PolicyElementValue>(values ?? Empty, StringComparer.OrdinalIgnoreCase));

    public PolicyElementValue? ValueOf(string elementId) => Values.TryGetValue(elementId, out var value) ? value : null;

    public bool Equals(PolicySetting? other) =>
        other is not null && State == other.State && Values.Count == other.Values.Count
        && Values.All(v => other.Values.TryGetValue(v.Key, out var value) && Equals(value, v.Value));

    public override int GetHashCode() => HashCode.Combine(State, Values.Count);
}

/// <summary>Why an element value cannot be written.</summary>
public enum PolicyValueProblemKind
{
    /// <summary>A required element has no value.</summary>
    Required,

    /// <summary>A number is outside the element's range (arguments: min, max).</summary>
    OutOfRange,

    /// <summary>A text is longer than the element allows (argument: the limit).</summary>
    TooLong,

    /// <summary>More lines than the element allows (argument: the limit).</summary>
    TooManyLines,

    /// <summary>The enum item does not exist.</summary>
    UnknownItem,

    /// <summary>A list entry has no name (explicit lists) or no value.</summary>
    EmptyListEntry,

    /// <summary>Two list entries have the same name (or, for lists named by their data, the same data).</summary>
    DuplicateListEntry,

    /// <summary>A text holds a NUL character.</summary>
    InvalidCharacter,
}

/// <summary>A problem of one element's value.</summary>
/// <param name="ElementId">The element.</param>
/// <param name="Kind">What is wrong.</param>
/// <param name="Arguments">The limits involved (invariant text).</param>
public sealed record PolicyValueProblem(string ElementId, PolicyValueProblemKind Kind, IReadOnlyList<string> Arguments)
{
    public PolicyValueProblem(string elementId, PolicyValueProblemKind kind)
        : this(elementId, kind, [])
    {
    }
}
