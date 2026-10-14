using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>Where the actor's session came from (<see cref="LogonSourceResolver"/>); fields map 1:1 to
/// <c>Attribution.IpAddress / Workstation / SourceMethod / SourceConfidence / LogonType / SourceReason</c>.</summary>
public sealed record LogonSource
{
    public string? IpAddress { get; init; }

    /// <summary>Client-supplied (NTLM WorkstationName) or DNS-derived – never authoritative.</summary>
    public string? Workstation { get; init; }

    /// <summary>"4624", "dns", "local".</summary>
    public string? SourceMethod { get; init; }

    public AttributionConfidence Confidence { get; init; }

    public int? LogonType { get; init; }

    /// <summary>SOURCE_LOGON_NOT_FOUND, SOURCE_NOT_COLLECTED, DC_UNREACHABLE when nothing was found.</summary>
    public string? Reason { get; init; }

    /// <summary>No source could be determined.</summary>
    public static LogonSource NotFound(string reason) => new() { Reason = reason };
}
