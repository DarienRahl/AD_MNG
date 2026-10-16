namespace AdMng.Watchdog.Core.Tests.Notifications;

/// <summary>A <see cref="Random"/> returning fixed samples: 0.5 = zero jitter, 0 = −20 %, just below 1 = +20 %.</summary>
internal sealed class FixedRandom(double sample = 0.5) : Random
{
    public override double NextDouble() => sample;

    protected override double Sample() => sample;
}
