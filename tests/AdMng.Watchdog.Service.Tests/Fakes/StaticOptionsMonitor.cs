using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Tests.Fakes;

/// <summary>An options monitor with a settable value.</summary>
public sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; set; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
