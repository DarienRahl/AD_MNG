using AdMng.Watchdog.Service.Api;
using AdMng.Watchdog.Service.Hosting;
using Microsoft.Extensions.Logging;

namespace AdMng.Watchdog.Service.Tests.Hosting;

/// <summary>
/// The Application log (the only sink of the Windows service) takes Warning and above plus the Information entries that
/// are part of its contract: 1000 service started, 1006 configuration changed, 2301 job recovered (spec 19.8) and the
/// RODO access log of single events 2600/2601 (spec 17.1) – not the per-request lines or other diagnostics.
/// </summary>
public sealed class LoggingSetupTests
{
    [Theory]
    [InlineData(typeof(EventAccessLog))]
    [InlineData(typeof(ConfigChangeNotifier))]
    [InlineData(typeof(StartupChecks))]
    [InlineData(typeof(JobHost))]
    public void EventLog_AdmitsTheInformationEntriesOfItsContract(Type category)
    {
        Assert.True(LoggingSetup.EventLogFilter(category.FullName, LogLevel.Information));
        Assert.False(LoggingSetup.EventLogFilter(category.FullName, LogLevel.Debug));
    }

    [Theory]
    [InlineData(typeof(RequestLogMiddleware))]
    [InlineData(typeof(EventEndpoints))]
    public void EventLog_DropsOtherInformation_AndKeepsEveryWarning(Type category)
    {
        Assert.False(LoggingSetup.EventLogFilter(category.FullName, LogLevel.Information));
        Assert.False(LoggingSetup.EventLogFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Information));
        Assert.False(LoggingSetup.EventLogFilter(null, LogLevel.Information));
        Assert.True(LoggingSetup.EventLogFilter(category.FullName, LogLevel.Warning));
        Assert.True(LoggingSetup.EventLogFilter("Microsoft.Hosting.Lifetime", LogLevel.Error));
    }
}
