using System.Globalization;

namespace AdMng.Presentation.Readiness;

/// <summary>The outcome of one readiness check (design note docs/dev/s-pilot-readiness.md 2).</summary>
public enum ReadinessStatus
{
    Ok,

    /// <summary>Works, but something will limit a feature or the pilot ("Ostrzeżenie").</summary>
    Warning,

    /// <summary>A feature will not work until this is fixed ("Błąd").</summary>
    Error,

    /// <summary>Not run: a quick check, no test computer, not available here, cancelled, or the result could not be told.</summary>
    NotChecked,
}

/// <summary>The areas of the readiness check, in the order the document shows them.</summary>
public enum ReadinessArea
{
    Connection,
    Directory,
    Sysvol,
    RemoteActions,
    EventLogs,
    Watchdog,
    Correspondence,
    ThisComputer,
}

/// <summary>"Szybkie sprawdzenie" (LDAP and this computer) or "Pełne sprawdzenie" (also SYSVOL, the DCs' logs, a test computer, the Watchdog).</summary>
public enum ReadinessDepth
{
    Quick,
    Full,
}

/// <summary>
/// The stable codes of the checks (<c>&lt;Area&gt;.&lt;Check&gt;</c>): the export, <see cref="IReadinessResults"/> and the
/// problem report name a check by its code, never by a host or an account. A check of every DC (event logs) has one code;
/// its rows differ by host.
/// </summary>
public static class ReadinessCodes
{
    public const string ConnectionLogon = "Connection.Logon";
    public const string ConnectionTransport = "Connection.Transport";
    public const string ConnectionClockSkew = "Connection.ClockSkew";
    public const string ConnectionPdcEmulator = "Connection.PdcEmulator";
    public const string ConnectionReadOnlyDc = "Connection.ReadOnlyDc";
    public const string ConnectionGlobalCatalog = "Connection.GlobalCatalog";

    public const string DirectoryDomainLevel = "Directory.DomainLevel";
    public const string DirectoryForestLevel = "Directory.ForestLevel";
    public const string DirectoryRecycleBin = "Directory.RecycleBin";
    public const string DirectoryPasswordSettings = "Directory.PasswordSettings";
    public const string DirectoryWindowsLaps = "Directory.WindowsLaps";

    public const string SysvolReachable = "Sysvol.Reachable";
    public const string SysvolPoliciesWrite = "Sysvol.PoliciesWrite";
    public const string SysvolCentralStore = "Sysvol.CentralStore";
    public const string SysvolDefaultGpos = "Sysvol.DefaultGpos";
    public const string SysvolReplication = "Sysvol.Replication";

    public const string RemoteNameResolves = "Remote.NameResolves";
    public const string RemoteCimSession = "Remote.CimSession";

    public const string EventLogsSession = "EventLogs.Session";
    public const string EventLogsSecurity = "EventLogs.Security";
    public const string EventLogsCoverage = "EventLogs.Coverage";
    public const string EventLogsAuditLockout = "EventLogs.AuditLockout";
    public const string EventLogsAuditFailedLogons = "EventLogs.AuditFailedLogons";
    public const string EventLogsTestComputer = "EventLogs.TestComputer";

    public const string WatchdogConfigured = "Watchdog.Configured";
    public const string WatchdogReachable = "Watchdog.Reachable";
    public const string WatchdogAuthentication = "Watchdog.Authentication";
    public const string WatchdogRole = "Watchdog.Role";
    public const string WatchdogHealth = "Watchdog.Health";

    public const string CorrespondenceOutlook = "Correspondence.Outlook";
    public const string CorrespondenceProfile = "Correspondence.Profile";

    public const string ComputerWindows = "Computer.Windows";
    public const string ComputerWebView2 = "Computer.WebView2";

    /// <summary>Every code with its area, in the order the checks run and the document shows them.</summary>
    public static IReadOnlyList<(string Code, ReadinessArea Area)> All { get; } =
    [
        (ConnectionLogon, ReadinessArea.Connection),
        (ConnectionTransport, ReadinessArea.Connection),
        (ConnectionClockSkew, ReadinessArea.Connection),
        (ConnectionPdcEmulator, ReadinessArea.Connection),
        (ConnectionReadOnlyDc, ReadinessArea.Connection),
        (ConnectionGlobalCatalog, ReadinessArea.Connection),
        (DirectoryDomainLevel, ReadinessArea.Directory),
        (DirectoryForestLevel, ReadinessArea.Directory),
        (DirectoryRecycleBin, ReadinessArea.Directory),
        (DirectoryPasswordSettings, ReadinessArea.Directory),
        (DirectoryWindowsLaps, ReadinessArea.Directory),
        (SysvolReachable, ReadinessArea.Sysvol),
        (SysvolPoliciesWrite, ReadinessArea.Sysvol),
        (SysvolCentralStore, ReadinessArea.Sysvol),
        (SysvolDefaultGpos, ReadinessArea.Sysvol),
        (SysvolReplication, ReadinessArea.Sysvol),
        (RemoteNameResolves, ReadinessArea.RemoteActions),
        (RemoteCimSession, ReadinessArea.RemoteActions),
        (EventLogsSession, ReadinessArea.EventLogs),
        (EventLogsSecurity, ReadinessArea.EventLogs),
        (EventLogsCoverage, ReadinessArea.EventLogs),
        (EventLogsAuditLockout, ReadinessArea.EventLogs),
        (EventLogsAuditFailedLogons, ReadinessArea.EventLogs),
        (EventLogsTestComputer, ReadinessArea.EventLogs),
        (WatchdogConfigured, ReadinessArea.Watchdog),
        (WatchdogReachable, ReadinessArea.Watchdog),
        (WatchdogAuthentication, ReadinessArea.Watchdog),
        (WatchdogRole, ReadinessArea.Watchdog),
        (WatchdogHealth, ReadinessArea.Watchdog),
        (CorrespondenceOutlook, ReadinessArea.Correspondence),
        (CorrespondenceProfile, ReadinessArea.Correspondence),
        (ComputerWindows, ReadinessArea.ThisComputer),
        (ComputerWebView2, ReadinessArea.ThisComputer),
    ];

    private static readonly Dictionary<string, ReadinessArea> Areas = All.ToDictionary(c => c.Code, c => c.Area, StringComparer.Ordinal);

    /// <summary>The area of a code.</summary>
    public static ReadinessArea AreaOf(string code) =>
        code is not null && Areas.TryGetValue(code, out var area) ? area : throw new ArgumentException("Unknown readiness code.", nameof(code));

    /// <summary>The codes of one area in check order.</summary>
    public static IReadOnlyList<string> Of(ReadinessArea area) => [.. All.Where(c => c.Area == area).Select(c => c.Code)];

    /// <summary>The areas a quick check runs; the others are listed as not checked.</summary>
    public static bool IsQuick(ReadinessArea area) => area is ReadinessArea.Connection or ReadinessArea.Directory or ReadinessArea.Correspondence
        or ReadinessArea.ThisComputer;

    /// <summary>A code the quick check runs (Outlook's profile needs a connection to Outlook: full only).</summary>
    public static bool IsQuick(string code) => IsQuick(AreaOf(code)) && code != CorrespondenceProfile;
}

/// <summary>
/// One row of the readiness check: a check's code, area and status with the explanation, the remedy and the product
/// documentation chapter to read (localized texts). <see cref="Host"/> names the DC or computer of a per-host check; it is
/// shown and exported (the export says that it names hosts), never handed to <see cref="IReadinessResults"/>. In memory only.
/// </summary>
/// <param name="Code">The stable code (<see cref="ReadinessCodes"/>).</param>
/// <param name="Status">The outcome.</param>
/// <param name="Title">The check's name ("Różnica czasu względem DC").</param>
/// <param name="Explanation">What was found ("Zegar tego komputera różni się od zegara DC o 7 s.").</param>
public sealed record ReadinessResult(string Code, ReadinessStatus Status, string Title, string Explanation)
{
    /// <summary>The area of <see cref="Code"/>.</summary>
    public ReadinessArea Area => ReadinessCodes.AreaOf(Code);

    /// <summary>The DC or computer of a per-host row, null otherwise.</summary>
    public string? Host { get; init; }

    /// <summary>What to do (empty when nothing has to be done).</summary>
    public string Remedy { get; init; } = string.Empty;

    /// <summary>The product documentation chapter to read ("DZIENNIKI-ZDARZEN.md, rozdział 2 „Wymagania”"), empty when none.</summary>
    public string Documentation { get; init; } = string.Empty;

    /// <summary>The code and status only (no host, no text with directory values).</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Code}: {Status}");
}

/// <summary>What the operator asked for: the depth and the test computer of the remote checks (a DN from the picker or a typed name).</summary>
/// <param name="Depth">Quick or full.</param>
/// <param name="TestComputer">The computer's DN or name; null or empty: no test computer.</param>
public sealed record ReadinessRequest(ReadinessDepth Depth, string? TestComputer = null)
{
    /// <summary>The depth only (the test computer is a directory value).</summary>
    public override string ToString() => Depth.ToString();
}

/// <summary>The progress of a check run: the area being checked and how many areas are done.</summary>
/// <param name="Area">The area being checked.</param>
/// <param name="Done">Areas done.</param>
/// <param name="Total">Areas of the run.</param>
public sealed record ReadinessProgress(ReadinessArea Area, int Done, int Total);

/// <summary>A finished (or cancelled) run.</summary>
/// <param name="Depth">Quick or full.</param>
/// <param name="Results">Every row, in check order (with the not-checked rows of a quick or cancelled run).</param>
/// <param name="At">When it ended.</param>
/// <param name="Cancelled">The operator cancelled it: the rows not reached are not checked.</param>
/// <param name="Server">The DC of the window's session.</param>
public sealed record ReadinessReport(ReadinessDepth Depth, IReadOnlyList<ReadinessResult> Results, DateTimeOffset At, bool Cancelled, string Server)
{
    /// <summary>Counts only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Depth}: {Results.Count} results{(Cancelled ? ", cancelled" : string.Empty)}");
}
