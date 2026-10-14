using System.ComponentModel;
using System.Globalization;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using AdMng.Core.Directory;
using Microsoft.Extensions.Logging;

namespace AdMng.Watchdog.Core.Diagnostics;

/// <summary>
/// The only way the watchdog logs an exception (section 0.1). Exception messages, <c>ToString()</c> and <c>Data</c>
/// carry directory values, addresses or secrets (<c>DirectoryOperationException</c>, <c>FormatException</c> includes
/// the input since .NET 8, <c>XmlException</c>, MailKit <c>SmtpCommandException</c>), so they are never written. What
/// is written: the exception type chain, <c>HResult</c>, numeric/enum result codes (LDAP result code, Win32 error,
/// socket error, HTTP status, SMTP status, SQLite error code …) and the stack traces.
/// </summary>
/// <remarks>Never pass the exception object itself to an <see cref="ILogger"/> – logger providers format it with
/// <c>ToString()</c>, which includes the message.</remarks>
public static class SafeExceptionLog
{
    private const int MaxDepth = 8;

    /// <summary>Public numeric / enum / bool properties that are safe to log (no strings are ever read).</summary>
    private static readonly HashSet<string> CodePropertyNames = new(StringComparer.Ordinal)
    {
        "ResultCode", "ErrorCode", "NativeErrorCode", "SocketErrorCode", "Win32Error", "LdapResultCode", "Kind",
        "StatusCode", "HttpRequestError", "SqliteErrorCode", "SqliteExtendedErrorCode", "IsTransient",
    };

    /// <summary>Writes one log entry: <paramref name="operation"/> plus <see cref="Describe"/> of the exception.</summary>
    /// <param name="logger">Target logger.</param>
    /// <param name="level">Log level.</param>
    /// <param name="exception">The exception (its message is never written).</param>
    /// <param name="operation">Constant text naming what failed, e.g. "detection cycle" – never a directory value.</param>
    /// <param name="eventId">Optional event id (Application log ids of the service).</param>
    public static void Log(ILogger logger, LogLevel level, Exception exception, string operation, EventId eventId = default)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(exception);
        if (!logger.IsEnabled(level))
        {
            return;
        }

        logger.Log(level, eventId, "{Operation} failed: {Details}", operation, Describe(exception));
    }

    /// <summary><see cref="Log"/> at <see cref="LogLevel.Error"/>.</summary>
    public static void LogError(ILogger logger, Exception exception, string operation, EventId eventId = default) =>
        Log(logger, LogLevel.Error, exception, operation, eventId);

    /// <summary><see cref="Log"/> at <see cref="LogLevel.Warning"/>.</summary>
    public static void LogWarning(ILogger logger, Exception exception, string operation, EventId eventId = default) =>
        Log(logger, LogLevel.Warning, exception, operation, eventId);

    /// <summary>
    /// The safe multi-line description: for the exception and each inner exception (aggregate exceptions: every inner
    /// exception, depth ≤ 8) one header line "Type (HResult 0x…, Code=Value, …)" followed by its stack trace.
    /// </summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var sb = new StringBuilder();
        Append(sb, exception, 0);
        return sb.ToString().TrimEnd();
    }

    /// <summary>A short, stable code for health checks and job results, e.g. "DirectoryException/ServerUnavailable",
    /// "SocketException/ConnectionRefused", "HttpRequestException/429" – type name plus the first known code.</summary>
    public static string ErrorCode(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var code = exception switch
        {
            DirectoryException directory => directory.Kind.ToString(),
            SocketException socket => socket.SocketErrorCode.ToString(),
            Win32Exception win32 => win32.NativeErrorCode.ToString(CultureInfo.InvariantCulture),
            HttpRequestException { StatusCode: { } status } => ((int)status).ToString(CultureInfo.InvariantCulture),
            HttpRequestException http => http.HttpRequestError.ToString(),
            _ => Codes(exception).Select(c => c.Value).FirstOrDefault(),
        };

        return code is null ? exception.GetType().Name : $"{exception.GetType().Name}/{code}";
    }

    private static void Append(StringBuilder sb, Exception exception, int depth)
    {
        if (depth > 0)
        {
            sb.Append(" ---> ");
        }

        sb.Append(exception.GetType().FullName)
            .Append(" (HResult 0x")
            .Append(exception.HResult.ToString("X8", CultureInfo.InvariantCulture));
        foreach (var (name, value) in Codes(exception))
        {
            sb.Append(", ").Append(name).Append('=').Append(value);
        }

        sb.Append(')').AppendLine();
        if (exception.StackTrace is { } stackTrace)
        {
            sb.AppendLine(stackTrace);
        }

        if (depth >= MaxDepth)
        {
            return;
        }

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                Append(sb, inner, depth + 1);
            }
        }
        else if (exception.InnerException is { } inner)
        {
            Append(sb, inner, depth + 1);
        }
    }

    private static IEnumerable<(string Name, string Value)> Codes(Exception exception)
    {
        foreach (var property in exception.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (CodePropertyNames.Contains(property.Name) && TryReadCode(property, exception, out var value))
            {
                yield return (property.Name, value);
            }
        }

        // DirectoryOperationException (S.DS.P): Response.ResultCode – read by name so Core stays free of S.DS.P.
        var response = exception.GetType().GetProperty("Response", BindingFlags.Public | BindingFlags.Instance);
        if (response is not null && response.GetIndexParameters().Length == 0)
        {
            object? responseValue;
            try
            {
                responseValue = response.GetValue(exception);
            }
            catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException or NotSupportedException)
            {
                responseValue = null;
            }

            var resultCode = responseValue?.GetType().GetProperty("ResultCode", BindingFlags.Public | BindingFlags.Instance);
            if (resultCode is not null && TryReadCode(resultCode, responseValue!, out var code))
            {
                yield return ("Response.ResultCode", code);
            }
        }
    }

    private static bool TryReadCode(PropertyInfo property, object instance, out string value)
    {
        value = string.Empty;
        if (property.GetIndexParameters().Length != 0)
        {
            return false;
        }

        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (!(type.IsEnum || type.IsPrimitive) || type == typeof(char) || type == typeof(IntPtr) || type == typeof(UIntPtr))
        {
            return false;   // strings and objects may carry directory values – never read them
        }

        object? raw;
        try
        {
            raw = property.GetValue(instance);
        }
        catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }

        if (raw is null)
        {
            return false;
        }

        value = raw is IFormattable formattable && !type.IsEnum
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : raw.ToString() ?? string.Empty;
        return value.Length > 0;
    }
}
