using AdMng.Core.Comparison;
using AdMng.Core.Formatting;
using AdMng.Core.Ldap;
using AdMng.Core.Schema;
using AdMng.Presentation.Connections;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Shell;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.Comparison;

/// <summary>Opens the comparison window for an object (implemented by the WPF dialog service; modeless, one per launch).</summary>
public interface IObjectComparisonLauncher
{
    /// <summary>"Porównaj z obiektem w innym połączeniu…" for the object with <paramref name="distinguishedName"/>.</summary>
    Task CompareAsync(string distinguishedName);
}

/// <summary>Localized texts of the comparison.</summary>
public static class ComparisonText
{
    /// <summary>The key of a match rule's name ("Comparison.Rule.SamAccountName").</summary>
    public static string RuleKey(ObjectMatchRule rule) => $"Comparison.Rule.{rule}";

    public static string StateText(ILocalizer localizer, AttributeComparison row)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(row);
        return localizer[row.State switch
        {
            AttributeComparisonState.Same => L.Comparison.StateSame,
            AttributeComparisonState.SameOtherDomain => L.Comparison.StateSameOtherDomain,
            AttributeComparisonState.Different => row.IsCaseOnly ? L.Comparison.StateDifferentCase : L.Comparison.StateDifferent,
            AttributeComparisonState.OnlyA => L.Comparison.StateOnlyA,
            AttributeComparisonState.OnlyB => L.Comparison.StateOnlyB,
            AttributeComparisonState.NotInSchemaA => L.Comparison.StateNotInSchemaA,
            _ => L.Comparison.StateNotInSchemaB,
        }];
    }

    public static string StateText(ILocalizer localizer, MembershipComparisonState state)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[state switch
        {
            MembershipComparisonState.InBoth => L.Comparison.MembershipInBoth,
            MembershipComparisonState.InBothDifferently => L.Comparison.MembershipInBothDifferently,
            MembershipComparisonState.OnlyA => L.Comparison.MembershipOnlyA,
            _ => L.Comparison.MembershipOnlyB,
        }];
    }

    public static string KindText(ILocalizer localizer, MembershipKind? kind)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return kind switch
        {
            MembershipKind.Direct => localizer[L.Comparison.KindDirect],
            MembershipKind.Primary => localizer[L.Comparison.KindPrimary],
            MembershipKind.Nested => localizer[L.Comparison.KindNested],
            _ => string.Empty,
        };
    }

    public static string MarkText(ILocalizer localizer, ValueSide side)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[side switch
        {
            ValueSide.Both => L.Comparison.MarkBoth,
            ValueSide.OnlyA => L.Comparison.MarkOnlyA,
            _ => L.Comparison.MarkOnlyB,
        }];
    }

    /// <summary>A glyph next to the state: never colour alone.</summary>
    public static string Glyph(AttributeComparisonState state) => state switch
    {
        AttributeComparisonState.Same => string.Empty,
        AttributeComparisonState.SameOtherDomain => "≈",
        AttributeComparisonState.Different => "≠",
        AttributeComparisonState.OnlyA => "◀",
        AttributeComparisonState.OnlyB => "▶",
        _ => "?",
    };

    /// <summary>A glyph next to a membership state.</summary>
    public static string Glyph(MembershipComparisonState state) => state switch
    {
        MembershipComparisonState.InBoth => string.Empty,
        MembershipComparisonState.InBothDifferently => "≠",
        MembershipComparisonState.OnlyA => "◀",
        _ => "▶",
    };
}

/// <summary>One value of one side in the details pane of the attribute tab.</summary>
/// <param name="Side">"A" or "B".</param>
/// <param name="Text">The value as that side's formatter shows it (a secret is masked).</param>
/// <param name="Mark">Both, only A or only B.</param>
/// <param name="MarkText">"wspólne", "tylko A", "tylko B".</param>
public sealed record ComparisonValueViewModel(string Side, string Text, ValueSide Mark, string MarkText)
{
    /// <summary>The side and mark only (never the value).</summary>
    public override string ToString() => $"ComparisonValue({Side}, {Mark})";
}

/// <summary>One row of the "Atrybuty" tab: the attribute, the values of both sides (each with its own formatter) and the state.</summary>
public sealed class AttributeComparisonRowViewModel
{
    /// <summary>Values shown in a cell before "… i N więcej".</summary>
    public const int MaxCellValues = 20;

    /// <summary>Above this many values on a side the cell shows the counts instead of the values.</summary>
    public const int LargeValueCount = 1000;

    private readonly IAttributeFormatter _formatterA;
    private readonly IAttributeFormatter _formatterB;
    private readonly ILocalizer _localizer;
    private IReadOnlyList<ComparisonValueViewModel>? _details;

    internal AttributeComparisonRowViewModel(
        AttributeComparison comparison,
        ILocalizer localizer,
        ISchemaCatalog schemaA,
        ISchemaCatalog schemaB,
        IAttributeFormatter formatterA,
        IAttributeFormatter formatterB)
    {
        Comparison = comparison;
        _localizer = localizer;
        _formatterA = formatterA;
        _formatterB = formatterB;
        DisplayName = localizer.AttributeName(comparison.Name, schemaA.FindAttribute(comparison.Name) is not null ? schemaA : schemaB);
        StateText = ComparisonText.StateText(localizer, comparison);
        Glyph = ComparisonText.Glyph(comparison.State);
        IsLarge = comparison.ValuesA.Count > LargeValueCount || comparison.ValuesB.Count > LargeValueCount;
        ValueTextA = CellText(comparison.ValuesA, formatterA);
        ValueTextB = CellText(comparison.ValuesB, formatterB);
    }

    public AttributeComparison Comparison { get; }

    /// <summary>The LDAP name (tool tip, CSV).</summary>
    public string LdapName => Comparison.Name;

    /// <summary>The localized display name (display specifiers of the side that has the attribute, A first).</summary>
    public string DisplayName { get; }

    public AttributeComparisonState State => Comparison.State;

    public string StateText { get; }

    /// <summary>"≠", "◀", "▶", "≈", "?" or empty: a difference is never shown by colour alone.</summary>
    public string Glyph { get; }

    public bool IsDifference => Comparison.IsDifference;

    public bool IsVolatile => Comparison.IsVolatile;

    public bool IsSecret => Comparison.IsSecret;

    /// <summary>More than <see cref="LargeValueCount"/> values on a side: the cells show counts, the details the differences.</summary>
    public bool IsLarge { get; }

    /// <summary>The cell of side A: up to 20 values (one per line), the counts of a large attribute, or "brak wartości".</summary>
    public string ValueTextA { get; }

    /// <summary>The cell of side B.</summary>
    public string ValueTextB { get; }

    public bool HasValueA => Comparison.ValuesA.Count > 0;

    public bool HasValueB => Comparison.ValuesB.Count > 0;

    /// <summary>All values of both sides with their marks (of a large attribute only the differences), formatted on demand.</summary>
    public IReadOnlyList<ComparisonValueViewModel> Details => _details ??= BuildDetails();

    /// <summary>The values of side A as text, one per line (all of them: CSV, clipboard; a secret is masked).</summary>
    public string AllValuesTextA => string.Join('\n', Comparison.ValuesA.Select(v => Format(v, _formatterA)));

    /// <summary>The values of side B as text, one per line.</summary>
    public string AllValuesTextB => string.Join('\n', Comparison.ValuesB.Select(v => Format(v, _formatterB)));

    /// <summary>The attribute and state only (never values).</summary>
    public override string ToString() => $"AttributeComparisonRow({LdapName}, {State})";

    private string CellText(IReadOnlyList<ComparedValue> values, IAttributeFormatter formatter)
    {
        if (values.Count == 0)
        {
            return _localizer[L.Comparison.NoValue];
        }

        if (Comparison.IsSecret)
        {
            return $"{SecretAttributes.Mask} ({_localizer[L.Comparison.SecretSet]})";
        }

        if (IsLarge)
        {
            return _localizer.Format(L.Comparison.ValueCounts, Comparison.CommonCount, Comparison.OnlyACount, Comparison.OnlyBCount);
        }

        var lines = values.Take(MaxCellValues).Select(v => Format(v, formatter)).ToList();
        if (values.Count > MaxCellValues)
        {
            lines.Add(_localizer.Format(L.Comparison.MoreValues, values.Count - MaxCellValues));
        }

        var text = string.Join('\n', lines);
        return Comparison.IsTruncated ? $"{text}\n{_localizer[L.Comparison.Incomplete]}" : text;
    }

    private IReadOnlyList<ComparisonValueViewModel> BuildDetails()
    {
        IEnumerable<ComparisonValueViewModel> Side(string side, IReadOnlyList<ComparedValue> values, IAttributeFormatter formatter) => values
            .Where(v => !IsLarge || v.Side != ValueSide.Both)
            .Select(v => new ComparisonValueViewModel(side, Format(v, formatter), v.Side, ComparisonText.MarkText(_localizer, v.Side)));

        return [.. Side("A", Comparison.ValuesA, _formatterA), .. Side("B", Comparison.ValuesB, _formatterB)];
    }

    /// <summary>A value as its side shows it: DNs as the canonical path of that side, everything else through the formatter.</summary>
    private string Format(ComparedValue value, IAttributeFormatter formatter)
    {
        if (Comparison.IsSecret)
        {
            return SecretAttributes.Mask;
        }

        if (Comparison.IsDnValued && value.Value is string dn && DistinguishedName.TryParse(dn, out var parsed) && !parsed.IsRoot
            && parsed.DomainComponent.Rdns.Count > 0)
        {
            return parsed.ToCanonicalName();
        }

        try
        {
            return formatter.FormatValue(Comparison.Name, value.Value, _localizer.Culture);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidCastException or OverflowException)
        {
            return value.Value as string ?? Convert.ToHexString((byte[])value.Value);
        }
    }
}

/// <summary>One row of the "Członkostwo" or "Członkowie" tab.</summary>
public sealed class MembershipComparisonRowViewModel
{
    internal MembershipComparisonRowViewModel(MembershipComparison comparison, ILocalizer localizer)
    {
        Comparison = comparison;
        NameA = comparison.A?.Name ?? string.Empty;
        NameB = comparison.B?.Name ?? string.Empty;
        KindTextA = ComparisonText.KindText(localizer, comparison.A?.Kind);
        KindTextB = ComparisonText.KindText(localizer, comparison.B?.Kind);
        StateText = ComparisonText.StateText(localizer, comparison.State);
        Glyph = ComparisonText.Glyph(comparison.State);
    }

    public MembershipComparison Comparison { get; }

    /// <summary>The name on each side; both when they differ ("Domain Users / Użytkownicy domeny").</summary>
    public string Name => NameA.Length > 0 && NameB.Length > 0 && !string.Equals(NameA, NameB, StringComparison.Ordinal)
        ? $"{NameA} / {NameB}"
        : NameA.Length > 0 ? NameA : NameB;

    public string NameA { get; }

    public string NameB { get; }

    /// <summary>"bezpośrednio", "grupa podstawowa", "zagnieżdżone" or empty (not on side A).</summary>
    public string KindTextA { get; }

    public string KindTextB { get; }

    public MembershipComparisonState State => Comparison.State;

    public string StateText { get; }

    public string Glyph { get; }

    public bool IsDifference => Comparison.IsDifference;

    /// <summary>The state only (never names).</summary>
    public override string ToString() => $"MembershipComparisonRow({State})";
}

/// <summary>One side of the header: identity strip and badge, the object's summary, and whether the side's session ended.</summary>
public sealed partial class ComparisonSideViewModel : ObservableObject
{
    internal ComparisonSideViewModel(string side)
    {
        Side = side;
    }

    /// <summary>"A" (source) or "B" (target).</summary>
    public string Side { get; }

    /// <summary>"contoso.test · CONTOSO\jan.kowalski".</summary>
    [ObservableProperty]
    public partial string Label { get; internal set; } = string.Empty;

    /// <summary>The identity colour of the side's connection (null: neutral).</summary>
    [ObservableProperty]
    public partial ConnectionColor? Color { get; internal set; }

    /// <summary>The object as that side reads it (its formatter, schema and policy), or null before a run.</summary>
    [ObservableProperty]
    public partial ObjectSummaryViewModel? Summary { get; internal set; }

    /// <summary>The side's session ended: its results stay visible and read-only.</summary>
    [ObservableProperty]
    public partial bool IsEnded { get; internal set; }

    /// <summary>Why the side ended ("Połączenie … zostało zamknięte"), or null.</summary>
    [ObservableProperty]
    public partial string? EndedMessage { get; internal set; }

    internal void SetIdentity(string label, ConnectionColor? color)
    {
        Label = label;
        Color = color;
    }

    internal void MarkEnded(string message)
    {
        IsEnded = true;
        EndedMessage = message;
    }

    internal void ClearEnded()
    {
        IsEnded = false;
        EndedMessage = null;
    }

    /// <summary>The side only.</summary>
    public override string ToString() => $"ComparisonSide({Side})";
}

/// <summary>Another open connection in the "Połączenie" combo (colour dot, domain, account).</summary>
public sealed record ComparisonTargetViewModel(Guid Id, string Label, ConnectionColor? Color, string Server)
{
    /// <summary>The id only.</summary>
    public override string ToString() => $"ComparisonTarget({Id:N})";
}

/// <summary>A candidate of the other connection in the candidate list (name, path, sAMAccountName, mail).</summary>
public sealed record MatchCandidateViewModel(MatchCandidate Candidate, string Path)
{
    public string Name => Candidate.Name;

    public string DistinguishedName => Candidate.DistinguishedName;

    public string SamAccountName => Candidate.SamAccountName ?? string.Empty;

    public string Mail => Candidate.Mail ?? string.Empty;

    internal static MatchCandidateViewModel From(MatchCandidate candidate) => new(
        candidate,
        Core.Ldap.DistinguishedName.TryParse(candidate.DistinguishedName, out var dn) && dn.Parent is { IsRoot: false } parent
            ? parent.ToCanonicalName()
            : string.Empty);

    /// <summary>The kind only.</summary>
    public override string ToString() => $"MatchCandidateRow({Candidate.Kind})";
}
