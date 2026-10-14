using AdMng.Watchdog.Service.Notifications;

namespace AdMng.Watchdog.Service.Tests.Fakes;

/// <summary>Records token requests and answers with a fixed token.</summary>
public sealed class FakeEntraTokenClient : IEntraTokenClient
{
    private readonly Lock _gate = new();
    private readonly List<EntraTokenRequest> _requests = [];

    public string Token { get; set; } = "eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9.e30.c2lnbmF0dXJl";

    /// <summary>When set, every request fails with this exception (after being recorded).</summary>
    public Exception? Failure { get; set; }

    public IReadOnlyList<EntraTokenRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public Task<string> AcquireTokenAsync(EntraTokenRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _requests.Add(request);
        }

        return Failure is null ? Task.FromResult(Token) : Task.FromException<string>(Failure);
    }
}
