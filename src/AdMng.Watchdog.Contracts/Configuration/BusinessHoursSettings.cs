namespace AdMng.Watchdog.Contracts.Configuration;

/// <summary>Business hours and holidays (quiet hours of DeliveryHours.BusinessHours, digest coverage).</summary>
public sealed record BusinessHoursSettings
{
    public IReadOnlyList<DayOfWeek> Days { get; init; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
    public TimeOnly Start { get; init; } = new(7, 0);
    public TimeOnly End { get; init; } = new(17, 0);
    public string HolidayCalendar { get; init; } = "PL";          // "PL" | "None"
    public IReadOnlyList<DateOnly> ExtraHolidays { get; init; } = [];
}
