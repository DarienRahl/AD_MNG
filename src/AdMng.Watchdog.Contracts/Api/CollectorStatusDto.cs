namespace AdMng.Watchdog.Contracts.Api;

/// <summary>
/// Status section „Kolektor WEF” (stage B design 7.9, 18 item 2): OpenWEC files ingested per DC. The checks are health
/// checks <c>wec.*</c> (<see cref="HealthCheckCodes"/>) in <see cref="StatusDto.Checks"/>.
/// </summary>
public sealed record CollectorStatusDto
{
    public required HealthState State { get; init; }

    /// <summary>The collector kind (bootstrap <c>Watchdog:Collector:Kind</c>).</summary>
    public string Kind { get; init; } = "OpenWec";

    /// <summary><c>Watchdog:Collector:SubscriptionUuid</c> every accepted record carries (7.6).</summary>
    public Guid? SubscriptionUuid { get; init; }

    /// <summary>Unconsumed bytes of every DC together.</summary>
    public long BacklogBytes { get; init; }

    /// <summary>One row per writable DC of the domain (also those that never delivered).</summary>
    public IReadOnlyList<CollectorDcStatusDto> DomainControllers { get; init; } = [];
}

/// <summary>One DC in <see cref="CollectorStatusDto"/>: last record, lag, backlog, rejected records (7.9).</summary>
public sealed record CollectorDcStatusDto
{
    /// <summary>DC host name (<c>dNSHostName</c>, lower case).</summary>
    public required string Dc { get; init; }

    public required HealthState State { get; init; }

    /// <summary>OpenWEC client directory, e.g. <c>DC01@CONTOSO.COM</c>; null when the DC never delivered.</summary>
    public string? Client { get; init; }

    /// <summary><c>TimeCreated</c> of the newest stored record.</summary>
    public DateTimeOffset? LastRecordAt { get; init; }

    /// <summary><c>meta.TimeReceived</c> of the newest record.</summary>
    public DateTimeOffset? LastReceivedAt { get; init; }

    /// <summary>Median <c>TimeReceived − TimeCreated</c> over the last hour (Warning above 5 minutes).</summary>
    public TimeSpan? MedianLag { get; init; }

    /// <summary>Unconsumed bytes of this DC's files.</summary>
    public long BacklogBytes { get; init; }

    /// <summary>Creation time of the oldest rotated file not yet consumed.</summary>
    public DateTimeOffset? OldestUnconsumedFileAt { get; init; }

    /// <summary>Records stored in the last 24 hours.</summary>
    public long RecordsStored24h { get; init; }

    /// <summary>Records rejected by the identity checks of 7.6 in the last 24 hours (<c>wec.&lt;dc&gt;.identityMismatch</c>).</summary>
    public int RejectedRecords24h { get; init; }

    /// <summary>Malformed or oversized lines skipped in the last 24 hours (<c>wec.&lt;dc&gt;.malformedLines</c>).</summary>
    public int MalformedLines24h { get; init; }

    /// <summary>Logon and failure records over <c>MaxLogonRecordsPerDcPerMinute</c> – counted, not stored (7.5).</summary>
    public long DroppedLogonRecords24h { get; init; }

    /// <summary>Last error code (never a message).</summary>
    public string? ErrorCode { get; init; }
}
