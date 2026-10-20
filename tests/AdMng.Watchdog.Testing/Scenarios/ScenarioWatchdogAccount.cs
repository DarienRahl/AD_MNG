using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Testing.Scenarios;

/// <summary>
/// The service's own account for detection tests (stage B design 9.3): a settable identity and keytab-tool record; the
/// key versions asked for are recorded.
/// </summary>
public sealed class ScenarioWatchdogAccount : IWatchdogAccountSource
{
    private readonly List<int> _requests = [];

    public WatchdogAccountIdentity? Identity { get; set; } = new() { SamAccountName = "ADMNG-WD$" };

    /// <summary>What <c>status.json</c> says (null: no file).</summary>
    public KeytabRotationRecord? Rotation { get; set; }

    /// <summary>The key versions the detection asked for.</summary>
    public IReadOnlyList<int> Requests => _requests;

    public Task<KeytabRotationRecord?> GetRotationAsync(int keyVersionNumber, CancellationToken cancellationToken)
    {
        _requests.Add(keyVersionNumber);
        return Task.FromResult(Rotation);
    }
}
