using AdMng.Watchdog.Core.Configuration;

namespace AdMng.Watchdog.Core.Rules.Validation;

/// <summary>Section <c>general</c> (time zone, language, instance name, public base URL, rollout mode) and the
/// <c>tooManyWakeUpContainers</c> rule.</summary>
internal static class GeneralValidator
{
    /// <summary>AD allows five change-notification registrations per connection; one stays free (10.16).</summary>
    public const int MaxWakeUpContainers = 4;

    private static readonly TimeSpan RolloutRecommendedAfterBaseline = TimeSpan.FromDays(7);

    public static void Validate(ValidationRun run)
    {
        var general = run.Configuration.General;
        if (string.IsNullOrWhiteSpace(general.InstanceName))
        {
            run.Error("general.instanceName", ConfigValidationCodes.Required, "The instance name is required.");
        }

        if (!TimeZoneResolver.TryResolve(general.TimeZone, out _))
        {
            run.Error("general.timeZone", ConfigValidationCodes.TimeZone,
                "Unknown time zone – use an IANA id (Europe/Warsaw) or a Windows id (Central European Standard Time).");
        }

        if (general.Language is not ("pl" or "en"))
        {
            run.Error("general.language", ConfigValidationCodes.Language, "The notification language must be \"pl\" or \"en\".");
        }

        if (general.PublicBaseUrl is { Length: > 0 } baseUrl && !UrlRules.IsAllowedHttpsUrl(baseUrl, run.Context.IsDevelopment))
        {
            run.Error("general.publicBaseUrl", ConfigValidationCodes.InsecureUrl, "The public base URL must be an absolute https:// URL.");
        }

        var baselineYoung = run.BaselineCompletedAt is not { } baseline || run.Now - baseline < RolloutRecommendedAfterBaseline;
        if (general.RolloutDays == 0 && !general.RolloutEnded && baselineYoung)
        {
            run.Warning("general.rolloutDays", ConfigValidationCodes.RolloutOff,
                "Rollout mode is off although the baseline is younger than 7 days – every rule notifies immediately from the start.");
        }

        var wakeUp = run.Configuration.Directory.WakeUpContainers;
        if (wakeUp.Count > MaxWakeUpContainers)
        {
            run.Error("directory.wakeUpContainers", ConfigValidationCodes.TooManyWakeUpContainers,
                $"At most {MaxWakeUpContainers} wake-up containers are allowed.", ValidationRun.Arg(MaxWakeUpContainers));
        }
    }
}
