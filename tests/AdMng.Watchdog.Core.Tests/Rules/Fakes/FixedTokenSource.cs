using AdMng.Watchdog.Core.Rules;

namespace AdMng.Watchdog.Core.Tests.Rules.Fakes;

/// <summary>Token values fixed by the test.</summary>
internal sealed class FixedTokenSource(ConfigTokens tokens) : IConfigTokenSource
{
    public ConfigTokens Current { get; set; } = tokens;

    public int Calls { get; private set; }

    public ValueTask<ConfigTokens> GetAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return ValueTask.FromResult(Current);
    }
}
