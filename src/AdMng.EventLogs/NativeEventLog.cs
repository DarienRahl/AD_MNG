using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.Versioning;
using EventLogRecord = AdMng.Core.EventLogs.EventLogRecord;
using WindowsEventLogQuery = System.Diagnostics.Eventing.Reader.EventLogQuery;

namespace AdMng.EventLogs;

/// <summary>
/// The calls of <c>System.Diagnostics.Eventing.Reader</c> shared by the computer and the file reader. Every
/// <see cref="EventRecord"/> is disposed as soon as its XML was rendered (each holds a native handle); only
/// <c>ToXml()</c> is read for records (the analyses never parse message text), the message is rendered on demand for
/// one record. Nothing is logged.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NativeEventLog
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    /// <summary>Runs a blocking call on a dedicated thread; the caller stops waiting when cancelled.</summary>
    public static async Task<T> RunAsync<T>(Func<CancellationToken, T> call, CancellationToken cancellationToken)
    {
        var task = Task.Factory.StartNew(
            () => call(cancellationToken),
            cancellationToken,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !task.IsCompleted)
        {
            // The call ends once its session or reader is cancelled; its outcome is not wanted any more.
            _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    /// <summary>The XML of each record of <paramref name="reader"/>, each record disposed before its XML is returned.</summary>
    public static IEnumerable<string> ReadXml(EventLogReader reader, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string xml;
            using (var record = reader.ReadEvent())
            {
                if (record is null)
                {
                    yield break;
                }

                xml = record.ToXml();
            }

            yield return xml;
        }
    }

    /// <summary>
    /// The time of the first record <paramref name="query"/> returns (forward: the oldest, reverse: the newest), null for an
    /// empty log or a direction the log does not support. Cancellation is passed on.
    /// </summary>
    public static DateTimeOffset? FirstRecordTime(WindowsEventLogQuery query)
    {
        try
        {
            using var reader = new EventLogReader(query) { BatchSize = 1 };
            using var record = reader.ReadEvent();
            return ReaderSupport.ToUtc(record?.TimeCreated);
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Cancels what runs on <paramref name="state"/> (an <see cref="EventLogSession"/> or <see cref="EventLogReader"/>).</summary>
    public static void Cancel(object? state)
    {
        try
        {
            switch (state)
            {
                case EventLogSession session:
                    session.CancelCurrentOperations();
                    break;
                case EventLogReader reader:
                    reader.CancelReading();
                    break;
            }
        }
        catch (Exception ex) when (ex is EventLogException or ObjectDisposedException or InvalidOperationException)
        {
            // Already finished or disposed.
        }
    }

    /// <summary>
    /// The message of the record <paramref name="query"/> finds (one record by its number), when it is the
    /// <paramref name="expected"/> event; null when it is gone, another event, or cannot be rendered.
    /// </summary>
    public static string? FormatMessage(WindowsEventLogQuery query, EventLogRecord expected, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new EventLogReader(query) { BatchSize = 1 };
            using var registration = cancellationToken.Register(Cancel, reader);
            using var found = reader.ReadEvent();
            if (found is null || !ReaderSupport.SameEvent(found.Id, found.ProviderName, found.MachineName, ReaderSupport.ToUtc(found.TimeCreated), expected))
            {
                return null;
            }

            return Describe(found);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex) when (EventLogErrors.IsTranslatable(ex) || ex is InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>FormatDescription()</c> in the UI language, then in English: the publisher metadata of the target may lack the
    /// UI language, and the API renders in <see cref="CultureInfo.CurrentCulture"/> of the calling thread.
    /// </summary>
    private static string? Describe(EventRecord record)
    {
        var text = DescribeIn(record, CultureInfo.CurrentUICulture);
        if (string.IsNullOrWhiteSpace(text) && !string.Equals(CultureInfo.CurrentUICulture.Name, English.Name, StringComparison.OrdinalIgnoreCase))
        {
            text = DescribeIn(record, English);
        }

        return string.IsNullOrWhiteSpace(text) ? null : text.TrimEnd();
    }

    private static string? DescribeIn(EventRecord record, CultureInfo culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            return record.FormatDescription();
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException or CultureNotFoundException)
        {
            return null;
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
