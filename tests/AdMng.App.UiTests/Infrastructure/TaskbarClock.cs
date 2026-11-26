using AdMng.App.Behaviors;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.App.UiTests.Infrastructure;

/// <summary>
/// The clock of the delay before progress reaches a taskbar button (<see cref="TaskbarProgress.Time"/>), moved by the test: create
/// it before the window is shown (a window takes the clock when it loads) and move it by <see cref="AdvanceDelay"/> when the
/// test has seen that the operation runs. The previous clock comes back when it is disposed.
/// </summary>
internal sealed class TaskbarClock : IDisposable
{
    private readonly TimeProvider _previous = TaskbarProgress.Time;

    public TaskbarClock()
    {
        Time = new FakeTimeProvider();
        TaskbarProgress.Time = Time;
    }

    public FakeTimeProvider Time { get; }

    /// <summary>The delay of the status line - the same token, so the button never shows what the line does not.</summary>
    public static TimeSpan Delay => Motion.TimeOf(Motion.WorkingDelayKey) ?? throw new InvalidOperationException("Motion.WorkingDelay is not defined.");

    public void AdvanceDelay() => Time.Advance(Delay);

    public void Dispose() => TaskbarProgress.Time = _previous;
}
