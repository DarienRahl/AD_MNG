namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// Bootstrap <c>Watchdog:Retention</c> (stage B design 14.2): the retention floor only root can lower – the runtime
/// validator rejects a lower <c>retention.eventsDays</c> with <c>retentionBelowFloor</c>.
/// </summary>
public sealed record RetentionBootstrapOptions
{
    /// <summary>3 years – the user decision of 2026-10-19 (ADR-0009).</summary>
    public const int DefaultMinEventsDays = 1095;

    public int MinEventsDays { get; init; } = DefaultMinEventsDays;
}
