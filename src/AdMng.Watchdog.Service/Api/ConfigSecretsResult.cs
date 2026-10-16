using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Service.Api;

/// <summary>The configuration to store (every <see cref="SecretField"/> stripped to <c>IsSet</c>/<c>Hint</c>) and the secret
/// writes that go into the same transaction.</summary>
public sealed record ConfigSecretsResult(WatchdogConfiguration Configuration, IReadOnlyList<SecretChange> Changes)
{
    public override string ToString() => $"ConfigSecretsResult({Changes.Count} secret change(s))";
}
