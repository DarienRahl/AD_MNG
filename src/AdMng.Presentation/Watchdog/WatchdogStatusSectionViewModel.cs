using System.Globalization;
using AdMng.Presentation.Localization;
using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Presentation.Watchdog;

/// <summary>One label/value line of a status section ("Wersja klucza w AD", "7").</summary>
public sealed record WatchdogStatusField(string Label, string Value);

/// <summary>One DC of „Kolektor WEF” (stage B design 7.9): last record, lag, backlog, stored and rejected records.</summary>
public sealed record WatchdogCollectorDcRowViewModel(
    string Dc,
    HealthState State,
    string StateText,
    string LastRecordText,
    string LagText,
    string BacklogText,
    string StoredText,
    string RejectedText,
    string? ErrorCode);

/// <summary>
/// A stage B section of the "Stan" sub-tab (design 18 item 2): „Konto usługi”, „Kolektor WEF”, „Zawartość GPO”, „Host”
/// – state, the values the service reports, the problems of its health checks with their remedies, and (collector) one row
/// per DC. A section the service does not report (Windows service, older version, no data yet) says so instead of values.
/// </summary>
public sealed class WatchdogStatusSectionViewModel
{
    private WatchdogStatusSectionViewModel(string title)
    {
        Title = title;
    }

    public string Title { get; }

    /// <summary>The service reports the section (false: only <see cref="Message"/> and problems are shown).</summary>
    public bool IsReported { get; private init; }

    public HealthState State { get; private init; } = HealthState.Unknown;

    public string StateText { get; private init; } = string.Empty;

    /// <summary>"Usługa nie raportuje tej sekcji…", "Śledzenie treści GPO jest wyłączone." or empty.</summary>
    public string Message { get; private init; } = string.Empty;

    public bool HasMessage => Message.Length > 0;

    public IReadOnlyList<WatchdogStatusField> Fields { get; private init; } = [];

    /// <summary>Warning/Error checks of the section with their remedies (Polish in the Polish UI).</summary>
    public IReadOnlyList<WatchdogHealthCheckRowViewModel> Problems { get; private init; } = [];

    public bool HasProblems => Problems.Count > 0;

    /// <summary>„Kolektor WEF”: one row per writable DC.</summary>
    public IReadOnlyList<WatchdogCollectorDcRowViewModel> DomainControllers { get; private init; } = [];

    public bool HasDomainControllers => DomainControllers.Count > 0;

    /// <summary>„Konto usługi” (4.7): principal, DN, kvnos, password change, keytab tool result, enctypes, flags.</summary>
    public static WatchdogStatusSectionViewModel ServiceAccount(StatusDto status, ILocalizer localizer, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(localizer);
        var title = localizer[L.WatchdogLinux.CardServiceAccount];
        var problems = ProblemsOf(status, localizer, zone, "serviceAccount.");
        if (status.ServiceAccount is not { } account)
        {
            return NotReported(title, localizer, problems);
        }

        var culture = localizer.Culture;
        var fields = new List<WatchdogStatusField>();
        Add(fields, localizer[L.WatchdogLinux.Principal], account.Principal);
        Add(fields, localizer[L.WatchdogLinux.AccountDn], account.AccountDn);
        Add(fields, localizer[L.WatchdogLinux.KeytabKvno], account.KeytabKvno?.ToString(culture));
        Add(fields, localizer[L.WatchdogLinux.DirectoryKvno], account.DirectoryKvno?.ToString(culture));
        if (account.KeytabKvno is { } keytab && account.DirectoryKvno is { } directory)
        {
            var matches = keytab == directory || (directory == keytab + 1 && account.RotationResult == KeytabStatusResults.ReplicationPending);
            Add(fields, localizer[L.WatchdogLinux.KeytabMatchesDirectory], YesNo(localizer, matches));
        }

        Add(fields, localizer[L.WatchdogLinux.LastPasswordChange], localizer.FormatTime(account.PasswordLastSetAt ?? account.LastRotationAt, zone));
        Add(fields, localizer[L.WatchdogLinux.NextPasswordChange], localizer.FormatTime(account.NextRotationDueAt, zone));
        if (account.RotationResult is { } result)
        {
            var text = localizer.KeytabResultName(result);
            Add(fields, localizer[L.WatchdogLinux.RotationResult], string.IsNullOrEmpty(account.RotationErrorCode) ? text : $"{text} ({account.RotationErrorCode})");
        }

        Add(fields, localizer[L.WatchdogLinux.EncryptionTypes], account.SupportedEncryptionTypes is { } types ? EncryptionTypes(types) : null);
        Add(fields, localizer[L.WatchdogLinux.KeytabEncryptionTypes], account.KeytabEncryptionTypes.Count == 0 ? null : string.Join(", ", account.KeytabEncryptionTypes));
        Add(fields, localizer[L.WatchdogLinux.NotDelegated], YesNo(localizer, account.NotDelegated));
        Add(fields, localizer[L.WatchdogLinux.PrivilegedMembership], YesNo(localizer, account.Privileged));
        Add(fields, localizer[L.WatchdogLinux.DenySelfAce], YesNo(localizer, account.DenySelfAcePresent));
        Add(fields, localizer[L.WatchdogLinux.LastCheck], localizer.FormatTime(account.CheckedAt, zone));
        return Reported(title, account.State, localizer, fields, problems);
    }

    /// <summary>„Kolektor WEF” (7.9): the total backlog and per DC last record, lag, backlog, stored and rejected records.</summary>
    public static WatchdogStatusSectionViewModel Collector(StatusDto status, ILocalizer localizer, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(localizer);
        var title = localizer[L.WatchdogLinux.CardCollector];
        var problems = ProblemsOf(status, localizer, zone, HealthCheckCodes.WecPrefix);
        if (status.Collector is not { } collector)
        {
            return NotReported(title, localizer, problems);
        }

        var culture = localizer.Culture;
        var fields = new List<WatchdogStatusField>
        {
            new(localizer[L.WatchdogLinux.CollectorBacklog], WatchdogStatusViewModel.FormatBytes(collector.BacklogBytes, culture)),
        };
        var rows = collector.DomainControllers
            .Select(dc => new WatchdogCollectorDcRowViewModel(
                dc.Dc,
                dc.State,
                localizer.HealthStateName(dc.State),
                localizer.FormatTime(dc.LastRecordAt, zone),
                dc.MedianLag is { } lag ? WatchdogDisplay.FormatDuration(lag, culture) : string.Empty,
                WatchdogStatusViewModel.FormatBytes(dc.BacklogBytes, culture),
                dc.RecordsStored24h.ToString("N0", culture),
                dc.RejectedRecords24h.ToString("N0", culture),
                string.IsNullOrEmpty(dc.ErrorCode) ? null : dc.ErrorCode))
            .ToList();
        return new WatchdogStatusSectionViewModel(title)
        {
            IsReported = true,
            State = collector.State,
            StateText = localizer.HealthStateName(collector.State),
            Message = rows.Count == 0 ? localizer[L.WatchdogLinux.CollectorNoDcs] : string.Empty,
            Fields = fields,
            Problems = problems,
            DomainControllers = rows,
        };
    }

    /// <summary>„Zawartość GPO” (8): DC, baseline progress and queue, last read, reconciliation, failures, SYSVOL lag.</summary>
    public static WatchdogStatusSectionViewModel GpoContent(StatusDto status, ILocalizer localizer, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(localizer);
        var title = localizer[L.WatchdogLinux.CardGpoContent];
        var problems = ProblemsOf(status, localizer, zone, HealthCheckCodes.GpoPrefix, "sysvol.");
        if (status.GpoContent is not { } gpo)
        {
            return NotReported(title, localizer, problems);
        }

        var culture = localizer.Culture;
        var fields = new List<WatchdogStatusField>();
        Add(fields, localizer[L.WatchdogUi.DomainController], gpo.Dc);
        Add(fields, localizer[L.WatchdogLinux.GpoReadProgress], localizer.Format(L.WatchdogLinux.GpoReadProgressValue, gpo.ReadGpos, gpo.TotalGpos, gpo.QueuedGpos));
        Add(fields, localizer[L.WatchdogLinux.GpoLastRead], localizer.FormatTime(gpo.LastReadAt, zone));
        Add(fields, localizer[L.WatchdogLinux.GpoReconciliation], localizer.FormatTime(gpo.LastReconciliationAt, zone));
        Add(fields, localizer[L.WatchdogLinux.GpoFailures], gpo.FailedGpos.ToString("N0", culture));
        Add(fields, localizer[L.WatchdogLinux.GpoSysvolLag], gpo.SysvolLaggingGpos.ToString("N0", culture));
        var section = Reported(title, gpo.Enabled ? gpo.State : HealthState.Disabled, localizer, fields, problems);
        return gpo.Enabled ? section : section.WithMessage(localizer[L.WatchdogLinux.GpoContentDisabled]);
    }

    /// <summary>„Host” (10, 4.7 item 6, 5.1): OS, runtime, secrets-key variant, free space, clock skew, LDAP SSF.</summary>
    public static WatchdogStatusSectionViewModel Host(StatusDto status, ILocalizer localizer, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(localizer);
        var title = localizer[L.WatchdogLinux.CardHost];
        var checks = status.Checks.Where(c => c.Code.StartsWith("host.", StringComparison.Ordinal) || c.Code == HealthCheckCodes.DirectoryNotSealed).ToList();
        var problems = ProblemsOf(checks, localizer, zone);
        if (status.Host is not { } host)
        {
            return NotReported(title, localizer, problems);
        }

        var culture = localizer.Culture;
        var fields = new List<WatchdogStatusField>();
        Add(fields, localizer[L.WatchdogLinux.HostOperatingSystem], host.OperatingSystem);
        Add(fields, localizer[L.WatchdogLinux.HostRuntime], host.Runtime is null ? null : host.Systemd ? $"{host.Runtime} · {localizer[L.WatchdogLinux.HostSystemd]}" : host.Runtime);
        Add(fields, localizer[L.WatchdogLinux.HostCredentialKey], host.CredentialKey is { } key ? localizer.CredentialKeyName(key) : localizer[L.WatchdogLinux.CredentialKeyNone]);
        Add(fields, localizer[L.WatchdogLinux.HostFreeSpace], WatchdogStatusViewModel.FormatBytes(status.Storage.FreeBytes, culture));
        Add(fields, localizer[L.WatchdogLinux.HostClockSkew], host.ClockSkew is { } skew ? FormatSkew(skew, culture) : null);
        Add(fields, localizer[L.WatchdogLinux.HostLdapSsf], host.LdapSsf is { } ssf
            ? string.IsNullOrEmpty(host.LdapSecurity) ? ssf.ToString(culture) : $"{ssf.ToString(culture)} · {host.LdapSecurity}"
            : host.LdapSecurity);
        var state = checks.Count == 0 ? HealthState.Ok : checks.Max(c => Rank(c.State)) switch
        {
            3 => HealthState.Error,
            2 => HealthState.Warning,
            _ => HealthState.Ok,
        };
        return Reported(title, state, localizer, fields, problems);
    }

    /// <summary>"+2 s", "−75 s" (positive: the DC is ahead).</summary>
    public static string FormatSkew(TimeSpan skew, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var seconds = (long)Math.Round(skew.TotalSeconds, MidpointRounding.AwayFromZero);
        var sign = seconds > 0 ? "+" : seconds < 0 ? "−" : string.Empty;
        return $"{sign}{Math.Abs(seconds).ToString("N0", culture)} s";
    }

    /// <summary>"AES128, AES256 (0x18)" for <c>msDS-SupportedEncryptionTypes</c>.</summary>
    public static string EncryptionTypes(int value)
    {
        var names = new List<string>();
        if ((value & 0x1) != 0)
        {
            names.Add("DES-CBC-CRC");
        }

        if ((value & 0x2) != 0)
        {
            names.Add("DES-CBC-MD5");
        }

        if ((value & 0x4) != 0)
        {
            names.Add("RC4");
        }

        if ((value & 0x8) != 0)
        {
            names.Add("AES128");
        }

        if ((value & 0x10) != 0)
        {
            names.Add("AES256");
        }

        var hex = "0x" + value.ToString("X", CultureInfo.InvariantCulture);
        return names.Count == 0 ? hex : $"{string.Join(", ", names)} ({hex})";
    }

    /// <summary>A section before the first status was read (title only).</summary>
    public static WatchdogStatusSectionViewModel Empty(string title) => new(title ?? string.Empty);

    private WatchdogStatusSectionViewModel WithMessage(string message) => new(Title)
    {
        IsReported = IsReported,
        State = State,
        StateText = StateText,
        Message = message,
        Fields = Fields,
        Problems = Problems,
        DomainControllers = DomainControllers,
    };

    private static WatchdogStatusSectionViewModel NotReported(string title, ILocalizer localizer, IReadOnlyList<WatchdogHealthCheckRowViewModel> problems) => new(title)
    {
        State = HealthState.Unknown,
        StateText = localizer.HealthStateName(HealthState.Unknown),
        Message = localizer[L.WatchdogLinux.SectionNotReported],
        Problems = problems,
    };

    private static WatchdogStatusSectionViewModel Reported(
        string title, HealthState state, ILocalizer localizer, IReadOnlyList<WatchdogStatusField> fields, IReadOnlyList<WatchdogHealthCheckRowViewModel> problems) => new(title)
    {
        IsReported = true,
        State = state,
        StateText = localizer.HealthStateName(state),
        Fields = fields,
        Problems = problems,
    };

    private static List<WatchdogHealthCheckRowViewModel> ProblemsOf(StatusDto status, ILocalizer localizer, TimeZoneInfo zone, params string[] prefixes) =>
        ProblemsOf(status.Checks.Where(c => prefixes.Any(p => c.Code.StartsWith(p, StringComparison.Ordinal))), localizer, zone);

    private static List<WatchdogHealthCheckRowViewModel> ProblemsOf(IEnumerable<HealthCheckDto> checks, ILocalizer localizer, TimeZoneInfo zone) =>
    [
        .. checks
            .Where(c => c.State is HealthState.Warning or HealthState.Error)
            .OrderByDescending(c => Rank(c.State))
            .ThenBy(c => c.Code, StringComparer.Ordinal)
            .Select(c => new WatchdogHealthCheckRowViewModel(c, localizer, zone)),
    ];

    private static int Rank(HealthState state) => state switch
    {
        HealthState.Error => 3,
        HealthState.Warning => 2,
        _ => 1,
    };

    private static void Add(List<WatchdogStatusField> fields, string label, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            fields.Add(new WatchdogStatusField(label, value));
        }
    }

    private static string? YesNo(ILocalizer localizer, bool? value) => value switch
    {
        true => localizer[L.Common.Yes],
        false => localizer[L.Common.No],
        null => null,
    };
}
