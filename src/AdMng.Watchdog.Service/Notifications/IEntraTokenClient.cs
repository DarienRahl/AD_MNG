namespace AdMng.Watchdog.Service.Notifications;

/// <summary>Acquires app-only access tokens (MSAL confidential client in production, fakes in tests). Callers run the
/// <see cref="DestinationGuard"/> first – a request is only made for an allowed destination with a matching binding.</summary>
public interface IEntraTokenClient
{
    /// <summary>The access token (in memory only).</summary>
    Task<string> AcquireTokenAsync(EntraTokenRequest request, CancellationToken cancellationToken);
}
