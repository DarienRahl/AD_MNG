namespace AdMng.Core.Correspondence.Composition;

/// <summary>Rows to messages (design note 3.4): one unit per row, or one per address with all its rows.</summary>
public static class RecipientGrouping
{
    /// <summary>
    /// The units in first-appearance order; without <paramref name="byAddress"/> one unit per row. Mailbox rows use it with
    /// the owner's address.
    /// </summary>
    public static IReadOnlyList<CorrespondenceUnit> Group(
        IEnumerable<(string Email, string Name, IReadOnlyDictionary<string, string> Fields)> rows, bool byAddress) =>
        throw new NotImplementedException("U5 T1 core");
}
