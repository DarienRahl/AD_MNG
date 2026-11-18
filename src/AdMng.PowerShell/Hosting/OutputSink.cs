using System.Threading.Channels;
using AdMng.Core.PowerShell;

namespace AdMng.PowerShell.Hosting;

/// <summary>
/// The in-memory channel between the pipeline thread (the host's Write* calls, the prompts) and the consumer of a run. It is
/// bounded: a producer that outruns the window waits (so <c>while ($true) { 1 }</c> cannot eat the memory before Stop), and
/// the wait ends when the run is stopped. Nothing here ever reaches a file.
/// </summary>
internal sealed class OutputSink
{
    private readonly ChannelWriter<PowerShellOutputItem> _writer;
    private int _errorItems;

    public OutputSink(ChannelWriter<PowerShellOutputItem> writer, CancellationToken stop)
    {
        _writer = writer;
        StopToken = stop;
    }

    /// <summary>Cancelled when the run is stopped.</summary>
    public CancellationToken StopToken { get; }

    /// <summary>True once an error text was posted (a non-terminating error, formatted by the engine, counts).</summary>
    public bool HadErrorText => Volatile.Read(ref _errorItems) > 0;

    /// <summary>Posts an item from the pipeline thread; blocks while the channel is full; drops it when the run was stopped.</summary>
    public void Post(PowerShellOutputItem item)
    {
        if (item is PowerShellTextItem { Stream: PowerShellStream.Error })
        {
            Interlocked.Increment(ref _errorItems);
        }

        try
        {
            while (!_writer.TryWrite(item))
            {
                if (!_writer.WaitToWriteAsync(StopToken).AsTask().GetAwaiter().GetResult())
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped while waiting for room: the item is dropped, the pipeline is ending anyway.
        }
        catch (ChannelClosedException)
        {
            // The run already ended.
        }
    }
}
