using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AdMng.Core.EventLogs;

namespace AdMng.EventLogs;

/// <summary>
/// The streaming pipeline of a read (design note e-event-logs.md 2, rule 6): the blocking producer – the Windows event log
/// API has no asynchronous calls – runs on a dedicated (long-running) thread and feeds a bounded channel; the caller
/// consumes it asynchronously. Leaving the loop early or cancelling stops the producer (its token is cancelled, which the
/// readers turn into <c>CancelCurrentOperations</c> / <c>CancelReading</c>) and waits for it, so no native handle
/// outlives the enumeration. A failure of the producer reaches the caller after the records read before it.
/// </summary>
internal static class RecordStream
{
    /// <summary>Streams what <paramref name="produce"/> writes.</summary>
    /// <param name="produce">Runs on its own thread; writes with <see cref="Write"/> and throws on failure.</param>
    /// <param name="capacity">Items buffered between the producer and the consumer.</param>
    /// <param name="lifetime">Cancelled when the reader is disposed.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    public static async IAsyncEnumerable<T> RunAsync<T>(
        Action<ChannelWriter<T>, CancellationToken> produce,
        int capacity,
        CancellationToken lifetime,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(produce);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var producer = Task.Factory.StartNew(
            () =>
            {
                try
                {
                    produce(channel.Writer, stop.Token);
                }
                finally
                {
                    channel.Writer.TryComplete();
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
        try
        {
            while (await channel.Reader.WaitToReadAsync(stop.Token).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var item))
                {
                    yield return item;
                }
            }

            await producer.ConfigureAwait(false);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await ObserveAsync(producer).ConfigureAwait(false);
        }
    }

    /// <summary>Writes from the producer thread, blocking while the channel is full.</summary>
    /// <exception cref="OperationCanceledException">The read was cancelled or the consumer left.</exception>
    public static void Write<T>(ChannelWriter<T> writer, T item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        while (!writer.TryWrite(item))
        {
            if (!writer.WaitToWriteAsync(cancellationToken).AsTask().GetAwaiter().GetResult())
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
    }

    /// <summary>
    /// Converts and writes the items of <paramref name="source"/> until it ends or <paramref name="maxRecords"/> records
    /// were written; an item that does not convert (an event whose XML cannot be read) is skipped.
    /// </summary>
    /// <returns>The records written.</returns>
    public static int Pump<TSource>(
        IEnumerable<TSource> source,
        Func<TSource, EventLogRecord?> convert,
        int? maxRecords,
        ChannelWriter<EventLogRecord> writer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(convert);
        if (maxRecords is <= 0)
        {
            return 0;
        }

        var written = 0;
        foreach (var item in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (convert(item) is not { } record)
            {
                continue;
            }

            Write(writer, record, cancellationToken);
            if (++written == maxRecords)
            {
                break;
            }
        }

        return written;
    }

    private static async Task ObserveAsync(Task producer)
    {
        try
        {
            await producer.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Already reported to the caller, or the caller left early (the producer was cancelled).
        }
    }
}
