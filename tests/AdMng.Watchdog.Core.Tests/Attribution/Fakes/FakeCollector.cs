using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Tests.Attribution.Fakes;

/// <summary>A WEF collector whose availability the test sets (collector mode, stage B design 7.8).</summary>
internal sealed class FakeCollector : ISecurityEventCollector
{
    public bool IsAvailable { get; set; }
}
