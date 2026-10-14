namespace AdMng.Watchdog.Core.Attribution;

/// <summary>Reverse DNS of a logon source address (section 11.4 #6, SourceMethod <c>dns</c> – never authoritative).</summary>
public interface IReverseDnsResolver
{
    /// <summary>The host name of <paramref name="ipAddress"/>, or null (no PTR record, timeout, invalid address).</summary>
    Task<string?> ResolveAsync(string ipAddress, CancellationToken cancellationToken);
}
