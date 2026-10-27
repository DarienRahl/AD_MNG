namespace AdMng.Core.Computers;

/// <summary>The state of a computer's owner (managedBy), resolved after the stream.</summary>
public enum OwnerState
{
    /// <summary>Not read yet.</summary>
    Unknown,

    /// <summary>No managedBy.</summary>
    Missing,

    Present,

    /// <summary>managedBy names a disabled account.</summary>
    Disabled,

    /// <summary>managedBy names an object that does not exist (any more).</summary>
    Deleted,
}

/// <summary>Options of <see cref="ComputerInventory.Evaluate"/>.</summary>
public sealed record ComputerInventoryOptions
{
    public WindowsReleaseCatalog Catalog { get; init; } = new();

    /// <summary>"Kończy się w ciągu N dni" (default 90).</summary>
    public int EndingSoonDays { get; init; } = WindowsReleaseCatalog.DefaultEndingSoonDays;

    /// <summary>The operator's catalogue overrides (settings).</summary>
    public IReadOnlyCollection<WindowsReleaseOverride> Overrides { get; init; } = [];

    /// <summary>The placement rules of the domain (null: no placement findings).</summary>
    public PlacementRules? Placement { get; init; }
}

/// <summary>The evaluated state of one computer at a reference time (pure, in memory).</summary>
public sealed record ComputerStatus
{
    public required WindowsReleaseMatch Release { get; init; }

    /// <summary>Calendar days since lastLogonTimestamp (or the exact last logon, when read); null when never logged on.</summary>
    public int? DaysSinceLogon { get; init; }

    /// <summary>The logon that counts: the exact one when read on every DC, else lastLogonTimestamp.</summary>
    public DateTimeOffset? LastLogon { get; init; }

    /// <summary>Calendar days since whenCreated.</summary>
    public int? DaysSinceCreated { get; init; }

    public IReadOnlyList<PlacementFinding> Placement { get; init; } = [];

    /// <summary>The computer never logged on (no lastLogonTimestamp and no exact logon found).</summary>
    public bool NeverLoggedOn => LastLogon is null;
}

/// <summary>
/// Evaluates computers (K1 design note 4.1): support state from the release catalogue at the operator's local date, days
/// since the last logon (replicated value, or the exact one from every DC when it was read), placement findings.
/// </summary>
public static class ComputerInventory
{
    /// <summary>The state of <paramref name="facts"/> at <paramref name="now"/> in <paramref name="timeZone"/>.</summary>
    /// <param name="facts">The computer.</param>
    /// <param name="now">Reference time.</param>
    /// <param name="timeZone">The operator's time zone (calendar days).</param>
    /// <param name="options">Catalogue, horizon, overrides, placement rules.</param>
    /// <param name="exactLastLogon">The newest lastLogon of every DC when it was read (null: not read or never).</param>
    /// <param name="hasExactLastLogon">The exact value was read (then it replaces lastLogonTimestamp, also "never").</param>
    public static ComputerStatus Evaluate(
        ComputerFacts facts,
        DateTimeOffset now,
        TimeZoneInfo timeZone,
        ComputerInventoryOptions options,
        DateTimeOffset? exactLastLogon = null,
        bool hasExactLastLogon = false)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(timeZone);
        ArgumentNullException.ThrowIfNull(options);
        var today = LocalDate(now, timeZone);
        var release = options.Catalog.Match(facts.OperatingSystem, facts.OperatingSystemVersionText, today, options.EndingSoonDays, options.Overrides);
        var lastLogon = hasExactLastLogon ? Newest(exactLastLogon, facts.LastLogonTimestamp) : facts.LastLogonTimestamp;
        return new ComputerStatus
        {
            Release = release,
            LastLogon = lastLogon,
            DaysSinceLogon = lastLogon is { } logon ? today.DayNumber - LocalDate(logon, timeZone).DayNumber : null,
            DaysSinceCreated = facts.Created is { } created ? today.DayNumber - LocalDate(created, timeZone).DayNumber : null,
            Placement = options.Placement?.Evaluate(facts.DistinguishedName, facts.Type) ?? [],
        };
    }

    /// <summary>The local calendar date of an instant.</summary>
    public static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, timeZone).DateTime);

    /// <summary>
    /// lastLogon is not replicated but lastLogonTimestamp is: the exact value is the newest of every DC's lastLogon, and
    /// a replicated timestamp newer than all of them (a DC that did not answer) still counts.
    /// </summary>
    private static DateTimeOffset? Newest(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;
}
