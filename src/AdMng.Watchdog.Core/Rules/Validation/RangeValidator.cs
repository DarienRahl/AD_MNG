using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Core.Rules.Validation;

/// <summary>
/// <c>range</c> errors of the global sections (8.3) plus the <c>pollFast</c> and <c>inactiveLow</c> warnings. Besides the
/// bounds the specification lists, intervals, windows and counts the jobs loop on get sanity bounds (no zero or
/// negative intervals, no unbounded windows). Channel and rule ranges are checked by their validators.
/// </summary>
internal static class RangeValidator
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    public static void Validate(ValidationRun run)
    {
        var c = run.Configuration;
        RangeCheck.Check(run, "general.rolloutDays", c.General.RolloutDays, 0, 60);
        Directory(run, c.Directory);
        Attribution(run, c.Attribution);
        Scans(run, c.Scans);
        Notifications(run, c.Notifications);
        Smtp(run, c.Smtp);
        RangeCheck.Check(run, "digest.maxRowsPerSection", c.Digest.MaxRowsPerSection, 1, 1000);
        RangeCheck.Check(run, "digest.upcomingExpiryDays", c.Digest.UpcomingExpiryDays, 0, 365);
        Retention(run, c.Retention);
        RetentionFloor(run, c.Retention);
    }

    /// <summary>
    /// <c>retentionBelowFloor</c> (stage B design 14.2): <c>retention.eventsDays</c> below the root-owned bootstrap floor
    /// <c>Watchdog:Retention:MinEventsDays</c> (<see cref="Abstractions.ConfigValidationContext.MinEventsDays"/>; 0 = no
    /// floor) – the 3-year history cannot be shortened through the API.
    /// </summary>
    private static void RetentionFloor(ValidationRun run, RetentionSettings r)
    {
        var floor = run.Context.MinEventsDays;
        if (floor > 0 && r.EventsDays < floor)
        {
            run.Error("retention.eventsDays", ConfigValidationCodes.RetentionBelowFloor,
                $"Events are kept at least {floor} days (bootstrap setting Watchdog:Retention:MinEventsDays, changeable only on the server).",
                ValidationRun.Arg(floor));
        }
    }

    private static void Directory(ValidationRun run, DirectorySettings d)
    {
        if (RangeCheck.Check(run, "directory.pollInterval", d.PollInterval, 5 * Second, 5 * Minute) && d.PollInterval < 10 * Second)
        {
            run.Warning("directory.pollInterval", ConfigValidationCodes.PollFast,
                "A poll interval below 10 seconds loads the domain controller without a practical benefit.");
        }

        RangeCheck.Check(run, "directory.usnOverlap", d.UsnOverlap, 0, 1_000_000);
        RangeCheck.Check(run, "directory.maxValuesPerAttribute", d.MaxValuesPerAttribute, 1, 1000);
        RangeCheck.Check(run, "directory.failoverAfter", d.FailoverAfter, Minute, Day);
        RangeCheck.Check(run, "directory.creationSettleWindow", d.CreationSettleWindow, TimeSpan.Zero, 10 * Minute);
    }

    private static void Attribution(ValidationRun run, AttributionSettings a)
    {
        RangeCheck.Check(run, "attribution.pollInterval", a.PollInterval, 10 * Second, 10 * Minute);
        RangeCheck.Check(run, "attribution.matchWindow", a.MatchWindow, Second, 30 * Second);
        RangeCheck.Check(run, "attribution.fallbackWindow", a.FallbackWindow, 30 * Second, 30 * Minute);
        RangeCheck.Check(run, "attribution.replicationLatencyBound", a.ReplicationLatencyBound, Minute, Day);
        RangeCheck.Check(run, "attribution.logonLookback", a.LogonLookback, Hour, 72 * Hour);
        RangeCheck.Check(run, "attribution.sourceQueryTimeout", a.SourceQueryTimeout, 2 * Second, 60 * Second);
        RangeCheck.Check(run, "attribution.maxRecordsPerPoll", a.MaxRecordsPerPoll, 1000, 500_000);
        if (RangeCheck.Check(run, "attribution.pendingRetries", a.PendingRetries.Count, 1, 10))
        {
            for (var i = 0; i < a.PendingRetries.Count; i++)
            {
                RangeCheck.Check(run, ValidationRun.Item("attribution.pendingRetries", i), a.PendingRetries[i], Second, Day);
            }
        }
    }

    private static void Scans(ValidationRun run, ScanSettings s)
    {
        RangeCheck.DistinctDays(run, "scans.accountExpiry.thresholdsDays", s.AccountExpiry.ThresholdsDays, 0, 365);
        RangeCheck.Check(run, "scans.accountExpiry.interval", s.AccountExpiry.Interval, Minute, Day);
        RangeCheck.DistinctDays(run, "scans.passwordExpiry.thresholdsDays", s.PasswordExpiry.ThresholdsDays, 0, 365);
        RangeCheck.Check(run, "scans.passwordExpiry.interval", s.PasswordExpiry.Interval, Minute, Day);
        RangeCheck.Check(run, "scans.lockoutRecheckInterval", s.LockoutRecheckInterval, Minute, Day);
        RangeCheck.Check(run, "scans.permissionSelfCheckInterval", s.PermissionSelfCheckInterval, Hour, 30 * Day);

        var inactive = s.InactiveAccounts;
        if (RangeCheck.Check(run, "scans.inactiveAccounts.days", inactive.Days, 30, 3650) && inactive.Days < 45)
        {
            run.Warning("scans.inactiveAccounts.days", ConfigValidationCodes.InactiveLow,
                "lastLogonTimestamp replicates with a lag of up to 14 days – fewer than 45 days reports active accounts as inactive.");
        }

        RangeCheck.Check(run, "scans.inactiveAccounts.privilegedDays", inactive.PrivilegedDays, 30, 3650);
        RangeCheck.Check(run, "scans.inactiveAccounts.minAgeDays", inactive.MinAgeDays, 0, 3650);

        Burst(run, "scans.massLockout", s.MassLockout);
        Burst(run, "scans.massDeletion", s.MassDeletion);
        Burst(run, "scans.massComputerDeletion", s.MassComputerDeletion);
        RangeCheck.Check(run, "scans.passwordSpray.distinctAccounts", s.PasswordSpray.DistinctAccounts, 2, 10_000);
        RangeCheck.Check(run, "scans.passwordSpray.window", s.PasswordSpray.Window, Minute, Day);
    }

    private static void Burst(ValidationRun run, string path, BurstSettings burst)
    {
        RangeCheck.Check(run, path + ".threshold", burst.Threshold, 1, 100_000);
        RangeCheck.Check(run, path + ".window", burst.Window, Minute, Day);
        RangeCheck.Check(run, path + ".percentOfEnabledUsers", burst.PercentOfEnabledUsers, 0, 100);
    }

    private static void Notifications(ValidationRun run, NotificationSettings n)
    {
        RangeCheck.Check(run, "notifications.attributionGrace", n.AttributionGrace, TimeSpan.Zero, 10 * Minute);
        RangeCheck.Check(run, "notifications.aggregationWindow", n.AggregationWindow, TimeSpan.Zero, 60 * Minute);
        RangeCheck.Check(run, "notifications.highSeverityFlushDelay", n.HighSeverityFlushDelay, TimeSpan.Zero, 10 * Minute);
        RangeCheck.Check(run, "notifications.maxItemsPerMessage", n.MaxItemsPerMessage, 1, 500);
        RangeCheck.Check(run, "notifications.maxMessagesPerRulePerHour", n.MaxMessagesPerRulePerHour, 1, 1000);
        RangeCheck.Check(run, "notifications.maxValueLength", n.MaxValueLength, 20, 10_000);
    }

    private static void Smtp(ValidationRun run, SmtpSettings s)
    {
        RangeCheck.Check(run, "smtp.port", s.Port, 1, 65535);
        RangeCheck.Check(run, "smtp.maxRecipientsPerMessage", s.MaxRecipientsPerMessage, 1, 500);
        RangeCheck.Check(run, "smtp.maxMessagesPerMinute", s.MaxMessagesPerMinute, 1, 10_000);
        RangeCheck.Check(run, "smtp.timeout", s.Timeout, 5 * Second, 10 * Minute);
    }

    private static void Retention(ValidationRun run, RetentionSettings r)
    {
        RangeCheck.Check(run, "retention.eventsDays", r.EventsDays, 7, 3650);
        RangeCheck.Check(run, "retention.securityEventsDays", r.SecurityEventsDays, 1, 3650);
        RangeCheck.Check(run, "retention.outboxSentDays", r.OutboxSentDays, 1, 3650);
        RangeCheck.Check(run, "retention.outboxDeadDays", r.OutboxDeadDays, 1, 3650);
        RangeCheck.Check(run, "retention.deletedSnapshotDays", r.DeletedSnapshotDays, 1, 3650);
        RangeCheck.Check(run, "retention.configAuditDays", r.ConfigAuditDays, 1, 3650);
        RangeCheck.Check(run, "retention.preMigrationBackupDays", r.PreMigrationBackupDays, 1, 3650);
        RangeCheck.Check(run, "retention.backupsToKeep", r.BackupsToKeep, 0, 1000);
        RangeCheck.Check(run, "retention.maxDatabaseMb", r.MaxDatabaseMb, 512, 1_048_576);
    }
}
