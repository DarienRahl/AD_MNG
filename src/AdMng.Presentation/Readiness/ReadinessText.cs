using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Readiness;

/// <summary>The localization keys of the readiness check: the title and the documentation chapter of every code, the areas and the statuses.</summary>
public static class ReadinessText
{
    private static readonly Dictionary<string, (string Title, string Documentation)> Checks = new(StringComparer.Ordinal)
    {
        [ReadinessCodes.ConnectionLogon] = (L.Readiness.TitleLogon, L.Readiness.DocComputersRemote),
        [ReadinessCodes.ConnectionTransport] = (L.Readiness.TitleTransport, L.Readiness.DocArchitectureSecurity),
        [ReadinessCodes.ConnectionClockSkew] = (L.Readiness.TitleClockSkew, L.Readiness.DocWatchdogTime),
        [ReadinessCodes.ConnectionPdcEmulator] = (L.Readiness.TitlePdc, L.Readiness.DocGpoDc),
        [ReadinessCodes.ConnectionReadOnlyDc] = (L.Readiness.TitleReadOnlyDc, L.Readiness.DocGpoDc),
        [ReadinessCodes.ConnectionGlobalCatalog] = (L.Readiness.TitleGlobalCatalog, L.Readiness.DocArchitectureSession),
        [ReadinessCodes.DirectoryDomainLevel] = (L.Readiness.TitleDomainLevel, L.Readiness.DocWatchdogForest),
        [ReadinessCodes.DirectoryForestLevel] = (L.Readiness.TitleForestLevel, L.Readiness.DocWatchdogForest),
        [ReadinessCodes.DirectoryRecycleBin] = (L.Readiness.TitleRecycleBin, L.Readiness.DocArchitectureSession),
        [ReadinessCodes.DirectoryPasswordSettings] = (L.Readiness.TitlePasswordSettings, L.Readiness.DocEventLogsLockout),
        [ReadinessCodes.DirectoryWindowsLaps] = (L.Readiness.TitleWindowsLaps, L.Readiness.DocComputersProperties),
        [ReadinessCodes.SysvolReachable] = (L.Readiness.TitleSysvolReachable, L.Readiness.DocGpoDc),
        [ReadinessCodes.SysvolPoliciesWrite] = (L.Readiness.TitlePoliciesWrite, L.Readiness.DocGpoOperations),
        [ReadinessCodes.SysvolCentralStore] = (L.Readiness.TitleCentralStore, L.Readiness.DocGpoCentralStore),
        [ReadinessCodes.SysvolDefaultGpos] = (L.Readiness.TitleDefaultGpos, L.Readiness.DocGpoBackup),
        [ReadinessCodes.SysvolReplication] = (L.Readiness.TitleReplication, L.Readiness.DocGpoDc),
        [ReadinessCodes.RemoteNameResolves] = (L.Readiness.TitleNameResolves, L.Readiness.DocComputersRemote),
        [ReadinessCodes.RemoteCimSession] = (L.Readiness.TitleCimSession, L.Readiness.DocComputersWinRm),
        [ReadinessCodes.EventLogsSession] = (L.Readiness.TitleEventLogSession, L.Readiness.DocEventLogsRequirements),
        [ReadinessCodes.EventLogsSecurity] = (L.Readiness.TitleEventLogSecurity, L.Readiness.DocEventLogsRequirements),
        [ReadinessCodes.EventLogsCoverage] = (L.Readiness.TitleEventLogCoverage, L.Readiness.DocWatchdogAudit),
        [ReadinessCodes.EventLogsAuditLockout] = (L.Readiness.TitleAuditLockout, L.Readiness.DocEventLogsRequirements),
        [ReadinessCodes.EventLogsAuditFailedLogons] = (L.Readiness.TitleAuditFailedLogons, L.Readiness.DocEventLogsRequirements),
        [ReadinessCodes.EventLogsTestComputer] = (L.Readiness.TitleTestComputerLog, L.Readiness.DocEventLogsRequirements),
        [ReadinessCodes.WatchdogConfigured] = (L.Readiness.TitleWatchdogConfigured, L.Readiness.DocWatchdogOrder),
        [ReadinessCodes.WatchdogReachable] = (L.Readiness.TitleWatchdogReachable, L.Readiness.DocWatchdogDns),
        [ReadinessCodes.WatchdogAuthentication] = (L.Readiness.TitleWatchdogAuthentication, L.Readiness.DocWatchdogDns),
        [ReadinessCodes.WatchdogRole] = (L.Readiness.TitleWatchdogRole, L.Readiness.DocWatchdogRoles),
        [ReadinessCodes.WatchdogHealth] = (L.Readiness.TitleWatchdogHealth, L.Readiness.DocWatchdogOrder),
        [ReadinessCodes.CorrespondenceOutlook] = (L.Readiness.TitleOutlook, L.Readiness.DocCorrespondenceRequirements),
        [ReadinessCodes.CorrespondenceProfile] = (L.Readiness.TitleOutlookProfile, L.Readiness.DocCorrespondenceTroubleshooting),
        [ReadinessCodes.ComputerWindows] = (L.Readiness.TitleWindows, L.Readiness.DocReadmeRun),
        [ReadinessCodes.ComputerWebView2] = (L.Readiness.TitleWebView2, L.Readiness.DocCorrespondenceRequirements),
    };

    /// <summary>The title of a check.</summary>
    public static string TitleKey(string code) => Checks[code].Title;

    /// <summary>The product documentation chapter of a check (a check may name another one for a particular outcome).</summary>
    public static string DocumentationKey(string code) => Checks[code].Documentation;

    public static string AreaKey(ReadinessArea area) => area switch
    {
        ReadinessArea.Connection => L.Readiness.AreaConnection,
        ReadinessArea.Directory => L.Readiness.AreaDirectory,
        ReadinessArea.Sysvol => L.Readiness.AreaSysvol,
        ReadinessArea.RemoteActions => L.Readiness.AreaRemoteActions,
        ReadinessArea.EventLogs => L.Readiness.AreaEventLogs,
        ReadinessArea.Watchdog => L.Readiness.AreaWatchdog,
        ReadinessArea.Correspondence => L.Readiness.AreaCorrespondence,
        _ => L.Readiness.AreaThisComputer,
    };

    public static string StatusKey(ReadinessStatus status) => status switch
    {
        ReadinessStatus.Ok => L.Readiness.StatusOk,
        ReadinessStatus.Warning => L.Readiness.StatusWarning,
        ReadinessStatus.Error => L.Readiness.StatusError,
        _ => L.Readiness.StatusNotChecked,
    };

    /// <summary>Every code has a title and a documentation chapter (the localization tests check the keys).</summary>
    public static IEnumerable<string> AllKeys() =>
        ReadinessCodes.All.SelectMany(c => new[] { TitleKey(c.Code), DocumentationKey(c.Code) })
            .Concat(Enum.GetValues<ReadinessArea>().Select(AreaKey))
            .Concat(Enum.GetValues<ReadinessStatus>().Select(StatusKey));
}
