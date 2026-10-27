using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Csv;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Comparison;

/// <summary>
/// The CSV of "Eksportuj CSV…" (design note docs/dev/multi-window.md 8.10): <c>Sekcja; Atrybut; Nazwa LDAP; Stan; &lt;A&gt;;
/// &lt;B&gt;</c> with the connection labels as the value headers. The first data row is the DN of each object, then the rows
/// the tabs show with their current filters. Multi-values are one per line inside the cell; secrets are masked (the rows
/// never carry them). Formula protection as every export of the application.
/// </summary>
internal sealed class ComparisonCsv(ILocalizer localizer, string labelA, string labelB)
{
    public required string DistinguishedNameA { get; init; }

    public required string DistinguishedNameB { get; init; }

    public IReadOnlyList<AttributeComparisonRowViewModel> Attributes { get; init; } = [];

    public IReadOnlyList<MembershipComparisonRowViewModel> Membership { get; init; } = [];

    public IReadOnlyList<MembershipComparisonRowViewModel> Members { get; init; } = [];

    /// <summary>Data rows written (the DN row included).</summary>
    public int RowCount => 1 + Attributes.Count + Membership.Count + Members.Count;

    /// <summary>"porownanie-Łukasz Wiśniewski-contoso.test-fabrikam.test.csv" without characters a file name cannot hold.</summary>
    public static string SuggestedFileName(string prefix, string name, string domainA, string domainB)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var text = new StringBuilder();
        foreach (var c in $"{prefix}-{name}-{domainA}-{domainB}")
        {
            text.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
        }

        return text.ToString().Trim().TrimEnd('.') + ".csv";
    }

    public void Write(ICsvCodec csv, TextWriter writer, char delimiter)
    {
        ArgumentNullException.ThrowIfNull(csv);
        ArgumentNullException.ThrowIfNull(writer);
        string[] headers =
        [
            localizer[L.Comparison.ColumnSection],
            localizer[L.Comparison.ColumnAttribute],
            localizer[L.Comparison.ColumnLdapName],
            localizer[L.Comparison.ColumnState],
            labelA,
            labelB,
        ];
        csv.Write(writer, headers, Rows(), delimiter, protectFormulas: true);
    }

    private IEnumerable<IReadOnlyList<string>> Rows()
    {
        var attributes = localizer[L.Comparison.TabAttributes];
        yield return [attributes, localizer[L.Comparison.RowDistinguishedName], Attr.DistinguishedName, string.Empty, DistinguishedNameA, DistinguishedNameB];
        foreach (var row in Attributes)
        {
            yield return [attributes, row.DisplayName, row.LdapName, row.StateText, Cell(row, row.HasValueA, row.AllValuesTextA), Cell(row, row.HasValueB, row.AllValuesTextB)];
        }

        var membership = localizer[L.Comparison.TabMembership];
        foreach (var row in Membership)
        {
            yield return [membership, row.Name, string.Empty, row.StateText, row.KindTextA, row.KindTextB];
        }

        var members = localizer[L.Comparison.TabMembers];
        foreach (var row in Members)
        {
            yield return [members, row.Name, string.Empty, row.StateText, row.KindTextA, row.KindTextB];
        }
    }

    /// <summary>All values (a large attribute too: the operator asked for the file); a secret is only "set".</summary>
    private string Cell(AttributeComparisonRowViewModel row, bool hasValue, string values) => !hasValue
        ? string.Empty
        : row.IsSecret ? $"{Core.Schema.SecretAttributes.Mask} ({localizer[L.Comparison.SecretSet]})" : values;
}
