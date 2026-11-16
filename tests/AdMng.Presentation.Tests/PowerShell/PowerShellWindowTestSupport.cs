using System.Collections.Specialized;
using System.ComponentModel;
using AdMng.Core.PowerShell;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.Tests.PowerShell;

/// <summary>Helpers of the window tests: waiting for a state without a sleep, and scripted runs that need a thread of their own.</summary>
internal static class PowerShellWindowTestSupport
{
    /// <summary>The longest a test waits for something that happens by itself.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    /// <summary>Completes when <paramref name="condition"/> is true, checked at every change of the view model (also at once).</summary>
    public static async Task UntilAsync(ObservableObject source, Func<bool> condition)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(object? sender, PropertyChangedEventArgs e)
        {
            if (condition())
            {
                done.TrySetResult();
            }
        }

        source.PropertyChanged += Check;
        try
        {
            Check(null, new PropertyChangedEventArgs(string.Empty));
            await done.Task.WaitAsync(Limit);
        }
        finally
        {
            source.PropertyChanged -= Check;
        }
    }

    /// <summary>Completes when <paramref name="condition"/> is true, checked at every change of <paramref name="source"/> (also at once).</summary>
    public static async Task UntilAsync(INotifyCollectionChanged source, Func<bool> condition)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (condition())
            {
                done.TrySetResult();
            }
        }

        source.CollectionChanged += Check;
        try
        {
            Check(null, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            await done.Task.WaitAsync(Limit);
        }
        finally
        {
            source.CollectionChanged -= Check;
        }
    }

    /// <summary>A run that asks <paramref name="prompt"/>, waits for the answer on its own thread, and writes what it got.</summary>
    public static async IAsyncEnumerable<PowerShellOutputItem> Ask(FakeRun run, PowerShellPromptItem prompt, Action<object?> answered, Func<object?, string> reply)
    {
        yield return prompt;
        var answer = prompt.Wait(run.Token);
        answered(answer);
        yield return FakePowerShellSession.Text(reply(answer) + "\n");
        yield return FakePowerShellSession.End();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>A run that takes <paramref name="time"/> on the clock of the test and then writes <paramref name="items"/>.</summary>
    public static async IAsyncEnumerable<PowerShellOutputItem> TakesTime(AdMng.Testing.ManualTimeProvider clock, TimeSpan time, params PowerShellOutputItem[] items)
    {
        clock.Advance(time);
        foreach (var item in items)
        {
            yield return item;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
