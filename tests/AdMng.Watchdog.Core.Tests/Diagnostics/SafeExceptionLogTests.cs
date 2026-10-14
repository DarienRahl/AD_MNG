using System.ComponentModel;
using System.Net.Sockets;
using AdMng.Core.Directory;
using AdMng.Testing;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AdMng.Watchdog.Core.Tests.Diagnostics;

public class SafeExceptionLogTests
{
    private const string DirectoryValue = "CN=Zofia Żak,OU=Księgowość,DC=contoso,DC=test";

    [Fact]
    public void Describe_KeepsTypesCodesAndStack_ButNeverMessagesOrData()
    {
        var exception = Capture(() =>
        {
            try
            {
                throw new FormatException($"The input '{DirectoryValue}' was not in a correct format.");
            }
            catch (FormatException inner)
            {
                throw new DirectoryException(DirectoryErrorKind.NoSuchObject, $"No such object: {DirectoryValue}", inner)
                {
                    LdapResultCode = 32,
                    ServerMessage = $"0000208D: NameErr: DSID-03100241, best match of: '{DirectoryValue}'",
                    DistinguishedName = DirectoryValue,
                };
            }
        });
        exception.Data["dn"] = DirectoryValue;

        var text = SafeExceptionLog.Describe(exception);

        Assert.DoesNotContain("Żak", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Księgowość", text, StringComparison.Ordinal);
        Assert.DoesNotContain("not in a correct format", text, StringComparison.Ordinal);
        Assert.Contains("AdMng.Core.Directory.DirectoryException", text, StringComparison.Ordinal);
        Assert.Contains("Kind=NoSuchObject", text, StringComparison.Ordinal);
        Assert.Contains("LdapResultCode=32", text, StringComparison.Ordinal);
        Assert.Contains("---> System.FormatException", text, StringComparison.Ordinal);
        Assert.Contains(nameof(Describe_KeepsTypesCodesAndStack_ButNeverMessagesOrData), text, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_ListsEveryInnerExceptionOfAnAggregate()
    {
        var aggregate = new AggregateException(
            new SocketException((int)SocketError.ConnectionRefused),
            new SecurityLogAccessException("dc02.contoso.test", 1722, $"RPC failed for {DirectoryValue}"));

        var text = SafeExceptionLog.Describe(aggregate);

        Assert.Contains("SocketErrorCode=ConnectionRefused", text, StringComparison.Ordinal);
        Assert.Contains("Win32Error=1722", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Żak", text, StringComparison.Ordinal);
        Assert.DoesNotContain("dc02", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Log_WritesOneEntry_WithoutTheExceptionObject()
    {
        var logger = new ListLogger<SafeExceptionLogTests>();

        SafeExceptionLog.LogWarning(logger, new InvalidOperationException(DirectoryValue), "detection cycle");

        var (level, message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.StartsWith("detection cycle failed: System.InvalidOperationException (HResult 0x80131509", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Żak", logger.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorCode_IsShortAndStable()
    {
        Assert.Equal(
            "DirectoryException/ServerUnavailable",
            SafeExceptionLog.ErrorCode(new DirectoryException(DirectoryErrorKind.ServerUnavailable, DirectoryValue)));
        Assert.Equal("SocketException/TimedOut", SafeExceptionLog.ErrorCode(new SocketException((int)SocketError.TimedOut)));
        Assert.Equal("Win32Exception/5", SafeExceptionLog.ErrorCode(new Win32Exception(5)));
        Assert.Equal("HttpRequestException/429", SafeExceptionLog.ErrorCode(
            new HttpRequestException(DirectoryValue, null, System.Net.HttpStatusCode.TooManyRequests)));
        Assert.Equal("InvalidOperationException", SafeExceptionLog.ErrorCode(new InvalidOperationException(DirectoryValue)));
    }

    private static Exception Capture(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("Expected an exception.");
    }
}
