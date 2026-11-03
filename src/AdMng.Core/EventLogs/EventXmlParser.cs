using System.Diagnostics.CodeAnalysis;

namespace AdMng.Core.EventLogs;

// stub – replaced by B1 at merge
public static class EventXmlParser
{
    public static bool TryParse(string? xml, string host, [NotNullWhen(true)] out EventLogRecord? record) =>
        throw new NotImplementedException();
}
