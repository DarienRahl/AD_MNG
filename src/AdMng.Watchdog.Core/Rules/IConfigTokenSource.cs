namespace AdMng.Watchdog.Core.Rules;

/// <summary>Supplies the values of the configuration tokens (<c>{domain}</c>, <c>{domainSid}</c>,
/// <c>{rootDomainSid}</c>) for runtime expansion (section 8.1).</summary>
public interface IConfigTokenSource
{
    /// <summary>The last known values (<see cref="ConfigTokens.Unknown"/> before the first successful read).</summary>
    ConfigTokens Current { get; }

    /// <summary>Refreshes the values when the directory session changed and returns them. Never fails because the
    /// directory is unavailable – the last known values are returned then.</summary>
    ValueTask<ConfigTokens> GetAsync(CancellationToken cancellationToken);
}
