using AdMng.Core.GroupPolicy.Search;

namespace AdMng.Presentation.GroupPolicy.Search;

/// <summary>
/// K7 contract (docs/dev/k-computers-and-gpo.md 21.3) – implementation: W1. Scoped per connection window (registered with
/// <see cref="IGpoSettingsIndexService"/> forwarded to the same instance); subscribes to <see cref="GroupPolicyServices.Changed"/>
/// and the session manager, and unsubscribes in <see cref="Dispose"/>.
/// </summary>
public sealed class GpoSettingsIndexService(GroupPolicyServices services) : IGpoSettingsIndexService, IDisposable
{
    private readonly GroupPolicyServices _services = services ?? throw new ArgumentNullException(nameof(services));

    public GpoIndexView? Current => null;

    public bool IsStale => false;

    public bool IsBuilding => false;

    public GpoIndexProgress? Progress => null;

#pragma warning disable CS0067 // raised once W1 implements the service
    public event EventHandler? StateChanged;
#pragma warning restore CS0067

    public Task<GpoIndexView> GetAsync(GpoIndexRefresh refresh, CancellationToken cancellationToken) =>
        throw new NotImplementedException("K7 W1: GpoSettingsIndexService.GetAsync (" + _services.GetType().Name + ")");

    public void CancelBuild()
    {
    }

    public void Invalidate()
    {
    }

    public void Dispose()
    {
    }
}
