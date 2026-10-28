namespace AdMng.Core.Correspondence.Fields;

/// <summary>
/// The comparison key of field and column names (design note 3.2): trimmed, whitespace collapsed, lower-invariant,
/// diacritics removed, '-', '_' and spaces removed ("Kod pocztowy" == "kodpocztowy").
/// </summary>
public static class FieldKey
{
    /// <summary>The folded key of <paramref name="name"/> ("" for null).</summary>
    public static string Of(string? name) => throw new NotImplementedException("U5 T1 core");

    /// <summary>True when both names have the same key.</summary>
    public static bool Same(string? a, string? b) => throw new NotImplementedException("U5 T1 core");
}
