namespace AdMng.Presentation.Localization;

public static partial class DisplayNames
{
    /// <summary>Full day name of the Logon Hours grid ("Poniedziałek").</summary>
    public static string LogonHoursDayKey(DayOfWeek day) => $"LogonHours.Day.{day}";

    /// <summary>Short day name of the logon hours summaries ("pon").</summary>
    public static string LogonHoursShortDayKey(DayOfWeek day) => $"LogonHours.DayShort.{day}";
}
