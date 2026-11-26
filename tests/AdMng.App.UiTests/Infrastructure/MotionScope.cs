using AdMng.App.Behaviors;

namespace AdMng.App.UiTests.Infrastructure;

/// <summary>
/// Switches the one switch of the motion behaviours (<see cref="Motion.IsEnabled"/>) on for a part of a test and off
/// again whatever happens (the host keeps motion off; a test that left it on would let every later test and screenshot
/// see elements on their way). A test of a motion asserts that an animation was started, never what a frame looks like.
/// </summary>
internal sealed class MotionScope : IDisposable
{
    private MotionScope() => Motion.IsEnabled = true;

    public static MotionScope On()
    {
        Assert.False(Motion.IsEnabled, "motion must be off in the UI test host");
        return new MotionScope();
    }

    public void Dispose() => Motion.IsEnabled = false;
}
