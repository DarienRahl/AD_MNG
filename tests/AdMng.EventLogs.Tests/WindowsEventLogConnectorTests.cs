using System.Diagnostics.Eventing.Reader;
using System.Net;
using System.Runtime.Versioning;
using AdMng.Core.Computers.Remote;
using AdMng.Core.Directory;
using AdMng.Core.EventLogs;
using AdMng.Testing;
using EventLogMode = AdMng.Core.EventLogs.EventLogMode;

namespace AdMng.EventLogs.Tests;

public class WindowsEventLogConnectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void IsSupported_OnlyOnWindows() => Assert.Equal(OperatingSystem.IsWindows(), new WindowsEventLogConnector().IsSupported);

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("fe80::1")]
    [InlineData("0x7f000001")]
    [InlineData("dc01")]
    [InlineData("dc01.contoso.test;Security")]
    public async Task OpenComputer_RefusesAnythingButAnFqdn_BeforeAnyConnection(string host)
    {
        var resolver = new RecordingResolver();
        var connector = new WindowsEventLogConnector(resolver);

        var error = await Assert.ThrowsAsync<EventLogAccessException>(() => connector.OpenComputerAsync(host, new FakeDirectoryBuilder().Session, Ct));

        Assert.Equal(EventLogAccessError.NotSupported, error.Error);
        Assert.Equal(123, error.NativeErrorCode);
        Assert.Empty(resolver.Hosts);
    }

    [Fact]
    public async Task OpenComputer_NeedsAHostAndASession()
    {
        var connector = new WindowsEventLogConnector();

        await Assert.ThrowsAsync<ArgumentNullException>(() => connector.OpenComputerAsync(null!, new FakeDirectoryBuilder().Session, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => connector.OpenComputerAsync("dc01.contoso.test", null!, Ct));
    }

    [Fact]
    public async Task OpenComputer_IsNotSupportedOffWindows()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Checks the guard of the other platforms.");
        var resolver = new RecordingResolver();

        var error = await Assert.ThrowsAsync<EventLogAccessException>(
            () => new WindowsEventLogConnector(resolver).OpenComputerAsync("dc01.contoso.test", new FakeDirectoryBuilder().Session, Ct));

        Assert.Equal(EventLogAccessError.NotSupported, error.Error);
        Assert.Null(error.NativeErrorCode);
        Assert.Empty(resolver.Hosts);
    }

    [Fact]
    public async Task OpenComputer_ASimpleBindWindow_IsNotSupported()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The logon is checked after the platform guard.");
        var session = new FakeDirectoryBuilder().Session;
        session.Info = session.Info with { Authentication = AuthenticationMode.Credentials };
        var resolver = new RecordingResolver();

        var error = await Assert.ThrowsAsync<EventLogAccessException>(() => new WindowsEventLogConnector(resolver).OpenComputerAsync("dc01.contoso.test", session, Ct));

        Assert.Equal(EventLogAccessError.NotSupported, error.Error);
        Assert.Empty(resolver.Hosts);
    }

    [Fact]
    public async Task OpenComputer_ANameThatDoesNotResolve_IsUnreachable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The name is resolved after the platform guard.");
        var resolver = new RecordingResolver();

        var error = await Assert.ThrowsAsync<EventLogAccessException>(
            () => new WindowsEventLogConnector(resolver).OpenComputerAsync("Stary-DC.Contoso.Test.", new FakeDirectoryBuilder().Session, Ct));

        Assert.Equal(EventLogAccessError.Unreachable, error.Error);
        Assert.Equal(WindowsEventLogConnector.HostNotFoundError, error.NativeErrorCode);
        Assert.Equal(["stary-dc.contoso.test"], resolver.Hosts);
    }

    [Fact]
    public async Task OpenComputer_AResolverTimeout_IsUnreachable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The name is resolved after the platform guard.");
        var resolver = new RecordingResolver { Failure = new RemoteActionException(RemoteErrorKind.Timeout) };

        var error = await Assert.ThrowsAsync<EventLogAccessException>(
            () => new WindowsEventLogConnector(resolver).OpenComputerAsync("dc01.contoso.test", new FakeDirectoryBuilder().Session, Ct));

        Assert.Equal(EventLogAccessError.Unreachable, error.Error);
    }

    [Fact]
    public void OpenFiles_NeedsFullyQualifiedPaths()
    {
        var connector = new WindowsEventLogConnector();

        Assert.Throws<ArgumentNullException>(() => connector.OpenFiles(null!));
        Assert.Throws<ArgumentException>(() => connector.OpenFiles([]));
        Assert.Throws<ArgumentException>(() => connector.OpenFiles(["Dziennik zabezpieczeń.evtx"]));
        Assert.Throws<ArgumentException>(() => connector.OpenFiles([Path.Combine(Path.GetTempPath(), "dc01.evtx"), " "]));
    }

    [Fact]
    public void OpenFiles_IsNotSupportedOffWindows()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Checks the guard of the other platforms.");

        var error = Assert.Throws<EventLogAccessException>(() => new WindowsEventLogConnector().OpenFiles([Path.Combine(Path.GetTempPath(), "dc01.evtx")]));

        Assert.Equal(EventLogAccessError.NotSupported, error.Error);
    }

    [Fact]
    public async Task OpenFiles_AMissingFile_IsUnreadable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Reads a file through the Windows event log API.");
        var missing = Path.Combine(Path.GetTempPath(), "admng-" + Guid.NewGuid().ToString("N"), "Dziennik zabezpieczeń.evtx");
        await using var reader = new WindowsEventLogConnector().OpenFiles([missing]);

        Assert.Equal("Dziennik zabezpieczeń.evtx", reader.Host);
        Assert.Equal(EventLogAccessError.FileUnreadable, (await Assert.ThrowsAsync<EventLogAccessException>(() => reader.GetCoverageAsync("Security", Ct))).Error);
        Assert.Equal(EventLogAccessError.FileUnreadable, (await Assert.ThrowsAsync<EventLogAccessException>(() => reader.GetChannelsAsync(Ct))).Error);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task OpenFiles_AnExportedLog_ChannelsAndCoverage()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Exports and reads a log through the Windows event log API.");
        var folder = System.IO.Directory.CreateTempSubdirectory("admng-evtx-");
        try
        {
            var file = Path.Combine(folder.FullName, "Aplikacja – kopia.evtx");
            try
            {
                EventLogSession.GlobalSession.ExportLog("Application", PathType.LogName, "*", file);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or EventLogException)
            {
                Assert.Skip("The Application log cannot be exported here.");
            }

            await using var reader = new WindowsEventLogConnector().OpenFiles([file]);
            var channels = await reader.GetChannelsAsync(Ct);
            var coverage = await reader.GetCoverageAsync("Application", Ct);

            Assert.Equal("Aplikacja – kopia.evtx", coverage.Host);
            Assert.Equal(EventLogMode.Unknown, coverage.Mode);
            Assert.NotNull(coverage.FileSizeBytes);
            if (coverage.RecordCount is > 0)
            {
                Assert.Equal(["Application"], channels);
                Assert.NotNull(coverage.Oldest);
                Assert.NotNull(coverage.Newest);
                Assert.True(coverage.Oldest <= coverage.Newest);
            }
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    /// <summary>A resolver that records the names it was asked for and resolves none (or fails).</summary>
    private sealed class RecordingResolver : IHostAddressResolver
    {
        public List<string> Hosts { get; } = [];

        public Exception? Failure { get; init; }

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            Hosts.Add(host);
            return Failure is null ? Task.FromResult<IReadOnlyList<IPAddress>>([]) : Task.FromException<IReadOnlyList<IPAddress>>(Failure);
        }
    }
}
