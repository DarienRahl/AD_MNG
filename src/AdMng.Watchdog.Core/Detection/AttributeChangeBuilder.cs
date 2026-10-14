using System.Globalization;
using AdMng.Core.Schema;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Builds <see cref="AttributeChange"/> records from snapshot values: display values (DNs, ACE descriptions, flag
/// names), added/removed values of multi-valued attributes, and the caps of section 9.4 – values longer than
/// <c>Notifications.MaxValueLength</c> are truncated with "…", at most <c>Directory.MaxValuesPerAttribute</c> values per
/// side are kept (counts stay exact). Secret attributes are always redacted.
/// </summary>
internal sealed class AttributeChangeBuilder
{
    private const string Ellipsis = "…";

    private readonly int _maxValueLength;
    private readonly int _maxValuesPerAttribute;
    private readonly ISchemaCatalog? _schema;

    public AttributeChangeBuilder(int maxValueLength, int maxValuesPerAttribute, ISchemaCatalog? schema = null)
    {
        _maxValueLength = Math.Max(16, maxValueLength);
        _maxValuesPerAttribute = Math.Max(1, maxValuesPerAttribute);
        _schema = schema;
    }

    /// <summary>The change of one attribute delta.</summary>
    public AttributeChange FromDelta(AttributeDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        return Build(delta.Attribute, delta.Old, delta.New);
    }

    /// <summary>The change between two (possibly absent) snapshot attributes; also used for the final values of a
    /// created object (<paramref name="before"/> null).</summary>
    public AttributeChange Build(string attribute, SnapshotAttribute? before, SnapshotAttribute? after)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        var kind = after?.Kind ?? before?.Kind ?? AttributeValueKind.Text;
        if (SecretAttributes.IsSecret(attribute, _schema))
        {
            return new AttributeChange
            {
                Attribute = attribute,
                ValueKind = kind,
                Redacted = true,
                OldCount = before?.Values.Count ?? 0,
                NewCount = after?.Values.Count ?? 0,
            };
        }

        var oldDisplay = Display(before);
        var newDisplay = Display(after);
        var added = newDisplay.Except(oldDisplay, StringComparer.Ordinal).ToList();
        var removed = oldDisplay.Except(newDisplay, StringComparer.Ordinal).ToList();
        IReadOnlyList<string> flagsAdded = [];
        IReadOnlyList<string> flagsRemoved = [];
        if (FlagNames.IsFlagAttribute(attribute))
        {
            (flagsAdded, flagsRemoved) = FlagNames.Diff(attribute, Number(before), Number(after));
        }

        var truncated = false;
        var change = new AttributeChange
        {
            Attribute = attribute,
            ValueKind = kind,
            OldValues = Cap(oldDisplay, ref truncated),
            NewValues = Cap(newDisplay, ref truncated),
            Added = IsMultiValued(before, after) ? Cap(added, ref truncated) : [],
            Removed = IsMultiValued(before, after) ? Cap(removed, ref truncated) : [],
            FlagsAdded = flagsAdded,
            FlagsRemoved = flagsRemoved,
            OldCount = oldDisplay.Count,
            NewCount = newDisplay.Count,
        };
        return change with { Truncated = truncated };
    }

    /// <summary>A synthetic change (Changes of health and summary events: counts, reasons, names).</summary>
    public AttributeChange Text(string attribute, IReadOnlyList<string> newValues, IReadOnlyList<string>? oldValues = null)
    {
        var truncated = false;
        var oldList = oldValues ?? [];
        return new AttributeChange
        {
            Attribute = attribute,
            ValueKind = AttributeValueKind.Text,
            OldValues = Cap(oldList, ref truncated),
            NewValues = Cap(newValues, ref truncated),
            OldCount = oldList.Count,
            NewCount = newValues.Count,
            Truncated = truncated,
        };
    }

    /// <summary>Display values of a snapshot attribute (security descriptors: ACE descriptions without the hash).</summary>
    public static IReadOnlyList<string> Display(SnapshotAttribute? attribute)
    {
        if (attribute is null)
        {
            return [];
        }

        if (attribute.Values.Any(v => v.StartsWith(SecurityDescriptorSummary.OwnerPrefix, StringComparison.Ordinal)
            || v.StartsWith(SecurityDescriptorSummary.AcePrefix, StringComparison.Ordinal)))
        {
            return attribute.Values
                .Where(v => !v.StartsWith(SecurityDescriptorSummary.HashPrefix, StringComparison.Ordinal))
                .Select(SecurityDescriptorSummary.Describe)
                .ToList();
        }

        return attribute.DisplayValues.Count == attribute.Values.Count && attribute.DisplayValues.Count > 0
            ? attribute.DisplayValues
            : attribute.Values;
    }

    private static bool IsMultiValued(SnapshotAttribute? before, SnapshotAttribute? after) =>
        (before?.Values.Count ?? 0) > 1 || (after?.Values.Count ?? 0) > 1;

    private static long? Number(SnapshotAttribute? attribute) =>
        attribute is { Values.Count: > 0 } && long.TryParse(attribute.Values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private IReadOnlyList<string> Cap(IReadOnlyList<string> values, ref bool truncated)
    {
        var result = new List<string>(Math.Min(values.Count, _maxValuesPerAttribute));
        foreach (var value in values)
        {
            if (result.Count == _maxValuesPerAttribute)
            {
                truncated = true;
                break;
            }

            if (value.Length > _maxValueLength)
            {
                truncated = true;
                result.Add(value[..(_maxValueLength - 1)] + Ellipsis);
            }
            else
            {
                result.Add(value);
            }
        }

        return result;
    }
}
