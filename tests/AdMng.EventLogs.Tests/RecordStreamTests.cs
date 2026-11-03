using System.Threading.Channels;
using AdMng.Core.EventLogs;

namespace AdMng.EventLogs.Tests;

public class RecordStreamTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RunAsync_StreamsEverythingInOrder_OffTheCallingThread()
    {
        var records = Samples.File("dc01.contoso.test", 0, 1, 2, 3, 4, 5, 6, 7, 8, 9);
        var callerThread = Environment.CurrentManagedThreadId;
        var producerThread = 0;

        var read = new List<EventLogRecord>();
        await foreach (var record in RecordStream.RunAsync<EventLogRecord>(
            (writer, token) =>
            {
                producerThread = Environment.CurrentManagedThreadId;
                foreach (var record in records)
                {
                    RecordStream.Write(writer, record, token);
                }
            },
            capacity: 3,
            CancellationToken.None,
            Ct))
        {
            read.Add(record);
        }

        Assert.Equal(records, read);
        Assert.NotEqual(callerThread, producerThread);
    }

    [Fact]
    public async Task RunAsync_AFailure_ArrivesAfterTheRecordsReadBeforeIt()
    {
        var records = Samples.File("dc01.contoso.test", 0, 1, 2);
        var read = new List<EventLogRecord>();

        var error = await Assert.ThrowsAsync<EventLogAccessException>(async () =>
        {
            await foreach (var record in RecordStream.RunAsync<EventLogRecord>(
                (writer, token) =>
                {
                    foreach (var record in records)
                    {
                        RecordStream.Write(writer, record, token);
                    }

                    throw new EventLogAccessException(EventLogAccessError.RpcUnavailable) { NativeErrorCode = 1722 };
                },
                capacity: 8,
                CancellationToken.None,
                Ct))
            {
                read.Add(record);
            }
        });

        Assert.Equal(EventLogAccessError.RpcUnavailable, error.Error);
        Assert.Equal(records, read);
    }

    [Fact]
    public async Task RunAsync_LeavingEarly_StopsAndAwaitsTheProducer()
    {
        var producerEnded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = 0;

        await foreach (var record in RecordStream.RunAsync<EventLogRecord>(
            (writer, token) =>
            {
                try
                {
                    for (var i = 0; ; i++)
                    {
                        RecordStream.Write(writer, Samples.Record("dc01.contoso.test", i, Samples.Morning.AddSeconds(i)), token);
                        written++;
                    }
                }
                catch (OperationCanceledException)
                {
                    producerEnded.SetResult(token.IsCancellationRequested);
                    throw;
                }
            },
            capacity: 2,
            CancellationToken.None,
            Ct))
        {
            if (record.RecordId == 1)
            {
                break;
            }
        }

        // The enumerator's disposal waited for the producer, which saw its token cancelled.
        Assert.True(producerEnded.Task.IsCompleted);
        Assert.True(await producerEnded.Task);
        Assert.InRange(written, 2, 5);
    }

    [Fact]
    public async Task RunAsync_TheCallersCancellation_EndsTheReadAndTheProducer()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var producerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producerCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var reading = Task.Run(
            async () =>
            {
                await foreach (var _ in RecordStream.RunAsync<EventLogRecord>(
                    (_, token) =>
                    {
                        producerStarted.SetResult();
                        try
                        {
                            // A blocking native call that ends once it is cancelled (CancelCurrentOperations).
                            token.WaitHandle.WaitOne();
                            token.ThrowIfCancellationRequested();
                        }
                        finally
                        {
                            producerCancelled.SetResult(token.IsCancellationRequested);
                        }
                    },
                    capacity: 4,
                    CancellationToken.None,
                    cancel.Token))
                {
                }
            },
            Ct);

        await producerStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(30), Ct));
        Assert.True(await producerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct));
    }

    [Fact]
    public async Task RunAsync_DisposingTheReader_CancelsARunningRead()
    {
        using var lifetime = new CancellationTokenSource();
        var producerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var reading = Task.Run(
            async () =>
            {
                await foreach (var _ in RecordStream.RunAsync<EventLogRecord>(
                    (_, token) =>
                    {
                        producerStarted.SetResult();
                        token.WaitHandle.WaitOne();
                        token.ThrowIfCancellationRequested();
                    },
                    capacity: 4,
                    lifetime.Token,
                    Ct))
                {
                }
            },
            Ct);

        await producerStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await lifetime.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(30), Ct));
    }

    [Fact]
    public async Task Write_BlocksWhileTheChannelIsFull()
    {
        var channel = Channel.CreateBounded<int>(new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.Wait });
        RecordStream.Write(channel.Writer, 1, Ct);
        RecordStream.Write(channel.Writer, 2, Ct);

        var third = Task.Run(() => RecordStream.Write(channel.Writer, 3, Ct), Ct);
        await Task.Delay(100, Ct);
        Assert.False(third.IsCompleted);

        Assert.True(channel.Reader.TryRead(out var first));
        await third.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(1, first);
    }

    [Fact]
    public void Pump_ConvertsSkipsAndStopsAtTheLimit()
    {
        var channel = Channel.CreateUnbounded<EventLogRecord>();
        var source = new[] { "4740", "nie-xml", "4771", "4776", "4625" };

        var written = RecordStream.Pump(
            source,
            text => int.TryParse(text, out var id) ? Samples.Record("dc01.contoso.test", id, Samples.Morning, eventId: id) : null,
            maxRecords: 3,
            channel.Writer,
            Ct);

        Assert.Equal(3, written);
        var ids = new List<int>();
        while (channel.Reader.TryRead(out var record))
        {
            ids.Add(record.EventId);
        }

        Assert.Equal([4740, 4771, 4776], ids);
    }

    [Fact]
    public void Pump_NoLimit_AndAZeroLimit()
    {
        var all = Channel.CreateUnbounded<EventLogRecord>();
        var none = Channel.CreateUnbounded<EventLogRecord>();
        var records = Samples.File("DC01.evtx", 0, 1, 2, 3);

        Assert.Equal(4, RecordStream.Pump(records, r => r, null, all.Writer, Ct));
        Assert.Equal(0, RecordStream.Pump(records, r => r, 0, none.Writer, Ct));
        Assert.False(none.Reader.TryRead(out _));
    }
}
