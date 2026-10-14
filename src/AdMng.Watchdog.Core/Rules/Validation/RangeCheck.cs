namespace AdMng.Watchdog.Core.Rules.Validation;

/// <summary>The <c>range</c> error of section 8.3 for numbers and time spans (bounds inclusive).</summary>
internal static class RangeCheck
{
    public static bool Check(ValidationRun run, string path, TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value >= min && value <= max)
        {
            return true;
        }

        run.Error(path, ConfigValidationCodes.Range, $"Must be between {ValidationRun.Arg(min)} and {ValidationRun.Arg(max)}.",
            ValidationRun.Arg(min), ValidationRun.Arg(max));
        return false;
    }

    public static bool Check(ValidationRun run, string path, long value, long min, long max)
    {
        if (value >= min && value <= max)
        {
            return true;
        }

        run.Error(path, ConfigValidationCodes.Range, $"Must be between {ValidationRun.Arg(min)} and {ValidationRun.Arg(max)}.",
            ValidationRun.Arg(min), ValidationRun.Arg(max));
        return false;
    }

    public static bool Check(ValidationRun run, string path, double value, double min, double max)
    {
        if (!double.IsNaN(value) && value >= min && value <= max)
        {
            return true;
        }

        run.Error(path, ConfigValidationCodes.Range, $"Must be between {ValidationRun.Arg(min)} and {ValidationRun.Arg(max)}.",
            ValidationRun.Arg(min), ValidationRun.Arg(max));
        return false;
    }

    /// <summary>Every entry in [min, max] and the entries distinct (expiry thresholds).</summary>
    public static void DistinctDays(ValidationRun run, string path, IReadOnlyList<int> values, int min, int max)
    {
        var seen = new HashSet<int>();
        for (var i = 0; i < values.Count; i++)
        {
            var itemPath = ValidationRun.Item(path, i);
            if (Check(run, itemPath, values[i], min, max) && !seen.Add(values[i]))
            {
                run.Error(itemPath, ConfigValidationCodes.Range, "Thresholds must be distinct.", ValidationRun.Arg(min), ValidationRun.Arg(max));
            }
        }
    }
}
