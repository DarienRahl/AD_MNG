using System.Threading.Channels;
using AdMng.Core.PowerShell;

namespace AdMng.PowerShell.Hosting;

/// <summary>
/// The in-memory channel between the pipeline thread (the host's Write* calls, the prompts) and the consumer of a run. It is
/// bounded by its number of items and by the size of the text it holds (audit PS-09: by the count alone, 4096 strings of 100,000
/// characters held 790 MB): a producer that outruns the window waits, so <c>while ($true) { 1 }</c> cannot eat the memory before
/// Stop, and the wait ends when the run is stopped. One text larger than the whole bound still passes when the channel is empty. The
/// consumer reports what it took (<see cref="Taken"/>). Nothing here ever reaches a file.
/// </summary>
internal sealed class OutputSink
{
    private readonly ChannelWriter<PowerShellOutputItem> _writer;
    private readonly long _characterCapacity;
    private readonly object _sync = new();
    private long _characters;
    private int _errorItems;

    public OutputSink(ChannelWriter<PowerShellOutputItem> writer, CancellationToken stop, long characterCapacity)
    {
        _writer = writer;
        StopToken = stop;
        _characterCapacity = characterCapacity;

        // A producer waiting for room in the text bound wakes up when the run is stopped.
        stop.Register(() =>
        {
            lock (_sync)
            {
                Monitor.PulseAll(_sync);
            }
        });
    }

    /// <summary>Cancelled when the run is stopped.</summary>
    public CancellationToken StopToken { get; }

    /// <summary>True once an error text was posted (a non-terminating error, formatted by the engine, counts).</summary>
    public bool HadErrorText => Volatile.Read(ref _errorItems) > 0;

    /// <summary>The characters of text posted and not yet taken by the consumer.</summary>
    public long BufferedCharacters
    {
        get
        {
            lock (_sync)
            {
                return _characters;
            }
        }
    }

    /// <summary>Posts an item from the pipeline thread; blocks while the channel is full; drops it when the run was stopped.</summary>
    public void Post(PowerShellOutputItem item)
    {
        if (item is PowerShellTextItem { Stream: PowerShellStream.Error })
        {
            Interlocked.Increment(ref _errorItems);
        }

        var size = SizeOf(item);
        lock (_sync)
        {
            while (size > 0 && _characters > 0 && _characters + size > _characterCapacity)
            {
                if (StopToken.IsCancellationRequested)
                {
                    // Stopped while waiting for room: the item is dropped, the pipeline is ending anyway.
                    return;
                }

                Monitor.Wait(_sync);
            }

            _characters += size;
        }

        var written = false;
        try
        {
            while (!_writer.TryWrite(item))
            {
                if (!_writer.WaitToWriteAsync(StopToken).AsTask().GetAwaiter().GetResult())
                {
                    return;
                }
            }

            written = true;
        }
        catch (OperationCanceledException)
        {
            // Stopped while waiting for room: the item is dropped, the pipeline is ending anyway.
        }
        catch (ChannelClosedException)
        {
            // The run already ended.
        }
        finally
        {
            if (!written)
            {
                Taken(item);
            }
        }
    }

    /// <summary>The consumer took <paramref name="item"/> off the channel: its text no longer counts, and a producer waiting for room goes on.</summary>
    public void Taken(PowerShellOutputItem item)
    {
        var size = SizeOf(item);
        if (size == 0)
        {
            return;
        }

        lock (_sync)
        {
            _characters -= size;
            Monitor.PulseAll(_sync);
        }
    }

    private static long SizeOf(PowerShellOutputItem item) => item is PowerShellTextItem text ? text.Text.Length : 0;
}
