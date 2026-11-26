using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace AdMng.PowerShell.Formats;

/// <summary>
/// The default display of the objects the read commands return (docs/dev/r1-powershell-window.md 12.2): one short table per
/// type name (<c>AdMng.User</c>, <c>AdMng.Computer</c>, <c>AdMng.Group</c>, <c>AdMng.Object</c>), registered in memory in the
/// initial session state - no format file is written or read. The distinguished name is always the last column, so the window
/// can make it a link ("Open in the program"); the other columns have fixed widths so the distinguished name gets the rest of the line.
/// </summary>
internal static class AdmFormats
{
    /// <summary>The type names that have a view.</summary>
    public static IReadOnlyList<string> TypeNames { get; } = ["AdMng.User", "AdMng.Computer", "AdMng.Group", "AdMng.Object"];

    /// <summary>Adds the views to <paramref name="state"/>.</summary>
    internal static void Apply(InitialSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        foreach (var definition in Definitions())
        {
            state.Formats.Add(new SessionStateFormatEntry(definition));
        }
    }

    internal static IEnumerable<ExtendedTypeDefinition> Definitions()
    {
        yield return Table(
            "AdMng.User",
            Column("Name", 22),
            Column("sAMAccountName", 20),
            Column("Enabled", 7),
            Column("DistinguishedName", 0));
        yield return Table(
            "AdMng.Computer",
            Column("Name", 16),
            Column("Enabled", 7),
            Column("operatingSystem", 22),
            Column("DistinguishedName", 0));
        yield return Table(
            "AdMng.Group",
            Column("Name", 24),
            Column("GroupScope", 12),
            Column("GroupCategory", 13),
            Column("DistinguishedName", 0));
        yield return Table(
            "AdMng.Object",
            Column("ObjectKind", 18),
            Column("Name", 24),
            Column("DistinguishedName", 0));
    }

    private static (string Property, int Width) Column(string property, int width) => (property, width);

    private static ExtendedTypeDefinition Table(string typeName, params (string Property, int Width)[] columns)
    {
        var builder = TableControl.Create();
        foreach (var (property, width) in columns)
        {
            builder.AddHeader(Alignment.Left, width, property);
        }

        var row = builder.StartRowDefinition();
        foreach (var (property, _) in columns)
        {
            row.AddPropertyColumn(property);
        }

        var control = row.EndRowDefinition().EndTable();
        return new ExtendedTypeDefinition(typeName, [new FormatViewDefinition(typeName + "View", control)]);
    }
}
