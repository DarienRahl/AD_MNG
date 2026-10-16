using AdMng.Core.Audit;
using AdMng.Core.Csv;

namespace AdMng.Core.Tests.Audit;

public class AuditLogExporterTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 13, 12, 30, 0, TimeSpan.Zero);

    private readonly CsvCodec _csv = new();

    private static AuditEntry Entry(string action, string? attribute, string? oldValue, string? newValue, bool succeeded = true, string? error = null) =>
        new(When, @"CONTOSO\jan.admin", "dc01.contoso.test", "CN=Jan Kowalski,OU=IT,DC=contoso,DC=test", action, attribute, oldValue, newValue, succeeded, error);

    [Fact]
    public void Write_ExportsAllEntriesOfTheLog()
    {
        var log = new InMemoryAuditLog();
        log.Record(Entry("Modify", "department", "IT", "Księgowość"));
        log.Record(Entry("Move", null, "CN=Jan Kowalski,OU=IT,DC=contoso,DC=test", "CN=Jan Kowalski,OU=Sprzedaż,DC=contoso,DC=test", false, "Access denied; insufficient rights"));

        using var writer = new StringWriter();
        new AuditLogExporter(_csv).Write(writer, log);
        var table = _csv.Read(new StringReader(writer.ToString()));

        Assert.Equal(["Timestamp", "Operator", "Server", "DN", "Action", "Attribute", "Old value", "New value", "Result", "Error"], table.Headers);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(
            [AuditLogExporter.FormatTimestamp(When), @"CONTOSO\jan.admin", "dc01.contoso.test", "CN=Jan Kowalski,OU=IT,DC=contoso,DC=test", "Modify", "department", "IT", "Księgowość", AuditLogExporter.ResultSucceeded, ""],
            table.Rows[0]);
        Assert.Equal(AuditLogExporter.ResultFailed, table.Rows[1][8]);
        Assert.Equal("Access denied; insufficient rights", table.Rows[1][9]);
        Assert.Equal(string.Empty, table.Rows[1][5]);
    }

    [Fact]
    public void Timestamps_AreLocalWithOffset()
    {
        var expected = TimeZoneInfo.ConvertTime(When, TimeZoneInfo.Local);

        var text = AuditLogExporter.FormatTimestamp(When);

        Assert.Equal(expected.ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture), text);
        Assert.Equal(When, DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Write_ProtectsAgainstFormulaInjection()
    {
        using var writer = new StringWriter();

        new AuditLogExporter(_csv).Write(writer, [Entry("Modify", "info", null, "=HYPERLINK(\"http://evil\")")], ',');

        Assert.Contains("\"'=HYPERLINK(\"\"http://evil\"\")\"", writer.ToString(), StringComparison.Ordinal);
    }
}
