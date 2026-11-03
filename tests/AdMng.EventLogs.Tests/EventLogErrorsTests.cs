using System.ComponentModel;
using System.Diagnostics.Eventing.Reader;
using System.Net.Sockets;
using System.Runtime.Versioning;
using AdMng.Core.Computers.Remote;
using AdMng.Core.EventLogs;

namespace AdMng.EventLogs.Tests;

public class EventLogErrorsTests
{
    [Theory]
    [InlineData(5, EventLogAccessError.AccessDenied)]
    [InlineData(1314, EventLogAccessError.AccessDenied)]
    [InlineData(1326, EventLogAccessError.AccessDenied)]
    [InlineData(1909, EventLogAccessError.AccessDenied)]
    [InlineData(1722, EventLogAccessError.RpcUnavailable)]
    [InlineData(1753, EventLogAccessError.RpcUnavailable)]
    [InlineData(1717, EventLogAccessError.RpcUnavailable)]
    [InlineData(1225, EventLogAccessError.RpcUnavailable)]
    [InlineData(1231, EventLogAccessError.Unreachable)]
    [InlineData(1232, EventLogAccessError.Unreachable)]
    [InlineData(1460, EventLogAccessError.Unreachable)]
    [InlineData(11001, EventLogAccessError.Unreachable)]
    [InlineData(2, EventLogAccessError.ChannelNotFound)]
    [InlineData(15000, EventLogAccessError.ChannelNotFound)]
    [InlineData(15007, EventLogAccessError.ChannelNotFound)]
    [InlineData(15001, EventLogAccessError.InvalidQuery)]
    [InlineData(15019, EventLogAccessError.InvalidQuery)]
    [InlineData(15026, EventLogAccessError.InvalidQuery)]
    [InlineData(1500, EventLogAccessError.Failed)]
    [InlineData(1396, EventLogAccessError.Failed)]
    [InlineData(4242, EventLogAccessError.Failed)]
    public void Classify_AComputer_ByTheWindowsCode(int code, EventLogAccessError expected) =>
        Assert.Equal(expected, EventLogErrors.Classify(EventLogFailureKind.EventLog, code, file: false));

    [Theory]
    [InlineData(2, EventLogAccessError.FileUnreadable)]
    [InlineData(3, EventLogAccessError.FileUnreadable)]
    [InlineData(5, EventLogAccessError.FileUnreadable)]
    [InlineData(32, EventLogAccessError.FileUnreadable)]
    [InlineData(1500, EventLogAccessError.FileUnreadable)]
    [InlineData(15005, EventLogAccessError.FileUnreadable)]
    [InlineData(15001, EventLogAccessError.InvalidQuery)]
    [InlineData(1722, EventLogAccessError.Failed)]
    public void Classify_AFile_ByTheWindowsCode(int code, EventLogAccessError expected) =>
        Assert.Equal(expected, EventLogErrors.Classify(EventLogFailureKind.EventLog, code, file: true));

    [Theory]
    [InlineData("NotFound", false, EventLogAccessError.ChannelNotFound)]
    [InlineData("NotFound", true, EventLogAccessError.FileUnreadable)]
    [InlineData("InvalidData", false, EventLogAccessError.Failed)]
    [InlineData("InvalidData", true, EventLogAccessError.FileUnreadable)]
    [InlineData("AccessDenied", false, EventLogAccessError.AccessDenied)]
    [InlineData("AccessDenied", true, EventLogAccessError.FileUnreadable)]
    [InlineData("Io", false, EventLogAccessError.Failed)]
    [InlineData("Io", true, EventLogAccessError.FileUnreadable)]
    [InlineData("Network", false, EventLogAccessError.Unreachable)]
    [InlineData("Reading", false, EventLogAccessError.Failed)]
    [InlineData("ProviderDisabled", false, EventLogAccessError.Failed)]
    [InlineData("NotSupported", false, EventLogAccessError.NotSupported)]
    [InlineData("EventLog", false, EventLogAccessError.Failed)]
    public void Classify_WithoutACode_ByTheKind(string kind, bool file, EventLogAccessError expected) =>
        Assert.Equal(expected, EventLogErrors.Classify(Enum.Parse<EventLogFailureKind>(kind), null, file));

    [Fact]
    public void Classify_NotSupported_WinsOverAnyCode() =>
        Assert.Equal(EventLogAccessError.NotSupported, EventLogErrors.Classify(EventLogFailureKind.NotSupported, 5, file: false));

    [Theory]
    [InlineData(1722, 1722)]
    [InlineData(15007, 15007)]
    [InlineData(unchecked((int)0x800706BA), 1722)]
    [InlineData(unchecked((int)0x80070005), 5)]
    [InlineData(unchecked((int)0x80131500), null)]
    [InlineData(unchecked((int)0x80004005), null)]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(0x10000, null)]
    public void Win32Code_PlainOrWrappedInAnHresult(int value, int? expected) => Assert.Equal(expected, EventLogErrors.Win32Code(value));

    [Fact]
    public void Translate_AccessDenied_KeepsTheCode_NoValueInTheMessageOrTheCause()
    {
        var cause = new UnauthorizedAccessException("dc01.contoso.test: konto adm.wiśniewski nie może czytać dziennika Security");

        var error = EventLogErrors.Translate(cause, file: false);

        Assert.Equal(EventLogAccessError.AccessDenied, error.Error);
        Assert.Equal(5, error.NativeErrorCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("dc01", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wiśniewski", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Security", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_SocketAndWin32Codes()
    {
        var notFound = EventLogErrors.Translate(new SocketException(11001), file: false);
        var endpointMapper = EventLogErrors.Translate(new Win32Exception(1753), file: false);

        Assert.Equal((EventLogAccessError.Unreachable, 11001), (notFound.Error, notFound.NativeErrorCode));
        Assert.Equal((EventLogAccessError.RpcUnavailable, 1753), (endpointMapper.Error, endpointMapper.NativeErrorCode));
    }

    [Fact]
    public void Translate_FileErrors_FromIo()
    {
        var missing = EventLogErrors.Translate(new FileNotFoundException("C:\\Logi\\Dziennik zabezpieczeń.evtx"), file: true);
        var locked = EventLogErrors.Translate(new IOException("zablokowany", unchecked((int)0x80070020)), file: true);

        Assert.Equal((EventLogAccessError.FileUnreadable, 2), (missing.Error, missing.NativeErrorCode));
        Assert.Equal((EventLogAccessError.FileUnreadable, 32), (locked.Error, locked.NativeErrorCode));
        Assert.DoesNotContain("zabezpieczeń", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_KeepsOnlyCausesWithoutValues()
    {
        var refusal = new RemoteActionException(RemoteErrorKind.NotSupported);

        Assert.Same(refusal, EventLogErrors.Translate(refusal, file: false).InnerException);
        Assert.Null(EventLogErrors.Translate(new IOException(@"C:\Logi\łódź.evtx"), file: true).InnerException);
        Assert.Null(EventLogErrors.Translate(new SocketException(11001), file: false).InnerException);
        Assert.Null(EventLogErrors.Translate(new Win32Exception(1722, "dc01.contoso.test"), file: false).InnerException);
    }

    [Fact]
    public void Translate_PlatformAndLogonRefusals_AreNotSupported()
    {
        Assert.Equal(EventLogAccessError.NotSupported, EventLogErrors.Translate(new PlatformNotSupportedException(), file: false).Error);
        Assert.Equal(EventLogAccessError.NotSupported, EventLogErrors.Translate(new RemoteActionException(RemoteErrorKind.NotSupported), file: false).Error);
        Assert.Equal(EventLogAccessError.Unreachable, EventLogErrors.Translate(new RemoteActionException(RemoteErrorKind.Timeout), file: false).Error);
    }

    [Fact]
    public void Translate_AnAccessException_IsReturnedAsItIs()
    {
        var existing = new EventLogAccessException(EventLogAccessError.InvalidQuery) { NativeErrorCode = 15001 };

        Assert.Same(existing, EventLogErrors.Translate(existing, file: true));
    }

    [Fact]
    public void IsTranslatable_LeavesProgrammingErrorsAlone()
    {
        Assert.True(EventLogErrors.IsTranslatable(new UnauthorizedAccessException()));
        Assert.True(EventLogErrors.IsTranslatable(new IOException()));
        Assert.True(EventLogErrors.IsTranslatable(new SocketException(11001)));
        Assert.False(EventLogErrors.IsTranslatable(new InvalidOperationException()));
        Assert.False(EventLogErrors.IsTranslatable(new NullReferenceException()));
        Assert.False(EventLogErrors.IsTranslatable(new OperationCanceledException()));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Translate_TheEventLogExceptions_OnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The event log exception types throw PlatformNotSupportedException elsewhere.");

        Assert.Equal(EventLogAccessError.ChannelNotFound, EventLogErrors.Translate(new EventLogNotFoundException("Microsoft-Windows-Brak/Operational"), file: false).Error);
        Assert.Equal(EventLogAccessError.FileUnreadable, EventLogErrors.Translate(new EventLogNotFoundException("brak pliku"), file: true).Error);
        Assert.Equal(EventLogAccessError.FileUnreadable, EventLogErrors.Translate(new EventLogInvalidDataException("uszkodzony"), file: true).Error);
        Assert.Equal(EventLogAccessError.Failed, EventLogErrors.Translate(new EventLogReadingException("nieaktualne"), file: false).Error);
        var cause = new EventLogException("błąd");
        var generic = EventLogErrors.Translate(cause, file: false);
        Assert.Equal(EventLogAccessError.Failed, generic.Error);
        Assert.Null(generic.NativeErrorCode);
        Assert.Same(cause, generic.InnerException);
    }
}
