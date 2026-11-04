using System.Globalization;
using AdMng.Core.EventLogs;
using AdMng.Core.EventLogs.Analysis;
using AdMng.Presentation.GroupPolicy.SecuritySettings;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.EventLogs;

/// <summary>A note of an analysis in the UI language.</summary>
/// <param name="Text">The note.</param>
/// <param name="IsWarning">A warning (shown first, with the warning glyph).</param>
public sealed record AnalysisNoteRow(string Text, bool IsWarning)
{
    public override string ToString() => IsWarning ? "warning note" : "note";
}

/// <summary>One requirement of the audit evidence on one host: seen, probably not audited, or unknown (never certain).</summary>
public sealed class AuditEvidenceRow
{
    public AuditEvidenceRow(AuditEvidenceEntry entry, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(localizer);
        Entry = entry;
        HostText = EventHostNames.ShortName(entry.Host) is { Length: > 0 } shortName ? shortName : entry.Host;
        EventText = localizer.Format(L.EventLogs.AuditEventLine, entry.Requirement.EventId.ToString(CultureInfo.InvariantCulture),
            localizer[entry.Requirement.Outcome == EventAuditOutcome.Success ? L.EventLogs.AuditSuccess : L.EventLogs.AuditFailure]);
        SubcategoryText = localizer[SecuritySettingsText.AuditSubcategoryKey(entry.Requirement.Subcategory.Id)];
        StateText = EventLogText.AuditState(localizer, entry.State);
        CountText = entry.RecordCount.ToString("N0", localizer.Culture);
    }

    public AuditEvidenceEntry Entry { get; }

    public string Host => Entry.Host;

    public string HostText { get; }

    /// <summary>"4771 (niepowodzenie)".</summary>
    public string EventText { get; }

    /// <summary>The advanced audit subcategory ("Inspekcja usługi uwierzytelniania Kerberos").</summary>
    public string SubcategoryText { get; }

    public string StateText { get; }

    public string CountText { get; }

    public AuditEvidenceState State => Entry.State;

    /// <summary>Not seen while the log covers the window: the subcategory is probably not audited (the links are offered).</summary>
    public bool IsProbablyNotAudited => Entry.State == AuditEvidenceState.NotSeenWhileCovered;

    public string AutomationName => $"{HostText}, {EventText}, {SubcategoryText}, {StateText}";

    public override string ToString() => Entry.ToString();
}

/// <summary>A ranked source of the lockout report.</summary>
public sealed class LockoutSourceRow
{
    public LockoutSourceRow(int rank, LockoutSource source, ILocalizer localizer, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(localizer);
        Rank = rank;
        Source = source;
        var culture = localizer.Culture;
        KindText = EventLogText.SourceKind(localizer, source.Source.Kind);
        Value = source.Source.Value;
        FailuresText = source.FailureCount.ToString("N0", culture);
        BadPasswordsText = source.BadPasswordCount.ToString("N0", culture);
        ContributingText = source.ContributingCount.ToString("N0", culture);
        LockoutsText = source.LockoutCount.ToString("N0", culture);
        FirstText = EventLogText.Moment(localizer, source.First, zone);
        LastText = EventLogText.Moment(localizer, source.Last, zone);
        DomainControllersText = string.Join(", ", source.DomainControllers.Select(d => EventHostNames.ShortName(d) ?? d));
        CodesText = string.Join(Environment.NewLine, source.Codes.Select(c => EventLogText.CodeCount(localizer, c)));
        RelatedText = source.RelatedNames.Count > 0 ? localizer.Format(L.EventLogsLockout.RelatedNames, string.Join(", ", source.RelatedNames)) : string.Empty;
        ProxyText = source.LikelyProxy && source.ProxyReasonKey is { } reason
            ? localizer.Format(L.EventLogsLockout.ProxyHint, localizer[EventLogText.ProxyReasonKey(reason)])
            : null;
        DomainControllerText = source.IsDomainController ? localizer[L.EventLogsLockout.SourceIsDomainController] : null;
    }

    public int Rank { get; }

    public LockoutSource Source { get; }

    public string KindText { get; }

    public string Value { get; }

    public string FailuresText { get; }

    public string BadPasswordsText { get; }

    /// <summary>Bad passwords in the observation window before a lockout (what locked the account).</summary>
    public string ContributingText { get; }

    public string LockoutsText { get; }

    public string FirstText { get; }

    public string LastText { get; }

    public string DomainControllersText { get; }

    /// <summary>One line per event ID and code with its meaning.</summary>
    public string CodesText { get; }

    /// <summary>"Nazwy stacji z tego adresu: WAW-PC001".</summary>
    public string RelatedText { get; }

    /// <summary>"Prawdopodobnie pośrednik: …", null when it does not look like one.</summary>
    public string? ProxyText { get; }

    public bool IsLikelyProxy => ProxyText is not null;

    /// <summary>"To kontroler domeny – …", null for other sources.</summary>
    public string? DomainControllerText { get; }

    /// <summary>The source names a computer (or an address that may resolve to one): "Sprawdź komputer źródłowy" is offered.</summary>
    public bool CanPivot => Source.Source.Kind is AttemptSourceKind.Workstation or AttemptSourceKind.CallerComputer or AttemptSourceKind.IpAddress
        && !Source.IsDomainController;

    public string AutomationName => $"{Rank}. {KindText} {Value}, {FailuresText}";

    public override string ToString() => Source.ToString();
}

/// <summary>One record of the account on the lockout timeline.</summary>
public sealed class LockoutTimelineRow
{
    public LockoutTimelineRow(LockoutTimelineEntry entry, ILocalizer localizer, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(localizer);
        Entry = entry;
        TimeText = EventLogText.Moment(localizer, entry.Time, zone);
        HostText = EventHostNames.ShortName(entry.Host) ?? entry.Host;
        KindText = EventLogText.TimelineKind(localizer, entry.Kind);
        EventIdText = entry.EventId.ToString(CultureInfo.InvariantCulture);
        SourceText = entry.Source is { } source ? EventLogText.Source(localizer, source) : entry.Actor ?? string.Empty;
        CodeText = entry.Code is { } code ? EventLogText.AnalysisCode(localizer, entry.EventId, code) : string.Empty;
    }

    public LockoutTimelineEntry Entry { get; }

    public string TimeText { get; }

    public string HostText { get; }

    public string KindText { get; }

    public string EventIdText { get; }

    public string SourceText { get; }

    public string CodeText { get; }

    public bool IsLockout => Entry.Kind == LockoutTimelineKind.Lockout;

    public string AutomationName => $"{TimeText}, {HostText}, {KindText}, {SourceText}, {CodeText}";

    public override string ToString() => Entry.ToString();
}

/// <summary>One DC of the lockout report: its state of the account, its log's coverage and what was read from it.</summary>
public sealed class LockoutDcRowViewModel
{
    public LockoutDcRowViewModel(LockoutDcRow row, ILocalizer localizer, TimeZoneInfo zone, DateTimeOffset now, Func<EventLogCoverage, string> coverage)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(localizer);
        Row = row;
        var culture = localizer.Culture;
        var roles = new List<string>();
        if (row.IsPdcEmulator)
        {
            roles.Add(localizer[L.EventLogs.KindPdc]);
        }

        if (row.IsOriginating)
        {
            roles.Add(localizer[L.EventLogsLockout.Originating]);
        }

        RolesText = string.Join(", ", roles);
        var state = row.State;
        IsLocked = state?.LockoutTime is { } locked && locked <= now;
        LockedText = state is null || state.ErrorKey is not null
            ? string.Empty
            : state.LockoutTime is { } time ? EventLogText.Moment(localizer, time, zone) : localizer[L.EventLogsLockout.NotLocked];
        BadPasswordCountText = state?.BadPasswordCount?.ToString(culture) ?? string.Empty;
        BadPasswordTimeText = EventLogText.Moment(localizer, state?.BadPasswordTime, zone);
        LastLogonText = EventLogText.Moment(localizer, state?.LastLogon, zone);
        CoverageText = row.ReadError is { } error
            ? EventLogText.AccessErrorShort(localizer, error)
            : row.Coverage is { } log ? coverage(log) : localizer[L.EventLogs.CoverageUnknown];
        RecordsText = localizer.Format(L.EventLogsLockout.DcRecords, row.LockoutsSeen, row.FailuresSeen);
        ErrorText = state?.ErrorKey is { } stateError ? stateError : row.ReadError is { } readError ? EventLogText.AccessError(localizer, readError) : null;
    }

    public LockoutDcRow Row { get; }

    public string Host => Row.Host;

    public string HostText => EventHostNames.ShortName(Row.Host) ?? Row.Host;

    /// <summary>"emulator PDC, tu powstała blokada".</summary>
    public string RolesText { get; }

    public bool IsLocked { get; }

    /// <summary>lockoutTime in local time, or "nie".</summary>
    public string LockedText { get; }

    public string BadPasswordCountText { get; }

    public string BadPasswordTimeText { get; }

    public string LastLogonText { get; }

    public string CoverageText { get; }

    /// <summary>"blokady: 1, niepowodzenia: 3".</summary>
    public string RecordsText { get; }

    public string? ErrorText { get; }

    public bool HasError => ErrorText is not null;

    public string AutomationName => $"{HostText}, {RolesText}, {LockedText}, {BadPasswordCountText}";

    public override string ToString() => Row.ToString();
}

/// <summary>A finding of the failed-logon analysis.</summary>
public sealed class FailedLogonFindingRow
{
    public FailedLogonFindingRow(FailedLogonFinding finding, ILocalizer localizer, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(localizer);
        Finding = finding;
        SeverityText = EventLogText.Severity(localizer, finding.Severity);
        KindText = EventLogText.FindingKind(localizer, finding.Kind);
        Explanation = EventLogText.FindingExplanation(localizer, finding.Kind);
        SourceText = EventLogText.Source(localizer, finding.Source);
        AccountsText = string.Join(", ", finding.Accounts.Take(5).Select(a => AccountText(localizer, a, revealed: false)))
            + (finding.Accounts.Count > 5 ? localizer.Format(L.EventLogsFailedLogons.MoreAccounts, finding.Accounts.Count - 5) : string.Empty);
        CountText = finding.Count.ToString("N0", localizer.Culture);
        FirstText = EventLogText.Moment(localizer, finding.First, zone);
        LastText = EventLogText.Moment(localizer, finding.Last, zone);
        CodesText = string.Join(Environment.NewLine, finding.Codes.Select(c => EventLogText.CodeCount(localizer, c)));
        RelatedText = finding.RelatedNames.Count > 0 ? localizer.Format(L.EventLogsLockout.RelatedNames, string.Join(", ", finding.RelatedNames)) : string.Empty;
        Title = localizer.Format(L.EventLogsFailedLogons.FindingTitle, KindText, SourceText);
    }

    public FailedLogonFinding Finding { get; }

    public FailedLogonSeverity Severity => Finding.Severity;

    public FailedLogonFindingKind Kind => Finding.Kind;

    public string SeverityText { get; }

    public string KindText { get; }

    /// <summary>What the finding means and the caveats of its kind.</summary>
    public string Explanation { get; }

    public string SourceText { get; }

    /// <summary>The first accounts (unknown names masked).</summary>
    public string AccountsText { get; }

    public string CountText { get; }

    public string FirstText { get; }

    public string LastText { get; }

    public string CodesText { get; }

    public string RelatedText { get; }

    /// <summary>"Rozpylanie haseł z 10.99.0.66".</summary>
    public string Title { get; }

    public string AutomationName => $"{SeverityText}, {KindText}, {SourceText}, {CountText}";

    /// <summary>
    /// An account of a finding: unknown names (possibly a password typed into the user name field) masked until revealed.
    /// </summary>
    public static string AccountText(ILocalizer localizer, FailedLogonAccountCount account, bool revealed)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(account);
        var name = account.IsUnknownAccount && !revealed ? localizer[L.EventLogsFailedLogons.MaskedAccount] : account.Account;
        return localizer.Format(L.EventLogsFailedLogons.AccountCount, name, account.Count.ToString("N0", localizer.Culture));
    }

    public override string ToString() => Finding.ToString();
}

/// <summary>An account of the selected finding (unknown names masked until "Pokaż").</summary>
public sealed partial class FailedLogonAccountRow(FailedLogonAccountCount account, ILocalizer localizer) : ObservableObject
{
    public FailedLogonAccountCount Account { get; } = account;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text), nameof(CanReveal))]
    public partial bool IsRevealed { get; set; }

    public string Text => FailedLogonFindingRow.AccountText(localizer, Account, IsRevealed);

    /// <summary>The name is masked (an unknown account) and may be shown.</summary>
    public bool CanReveal => Account.IsUnknownAccount && !IsRevealed;

    public override string ToString() => Account.ToString();
}

/// <summary>A severity chip of the failed-logon findings ("Wysoka: 2"); clicking it filters to that severity.</summary>
public sealed partial class FailedLogonSeverityChip(FailedLogonSeverity severity, string text, int count) : ObservableObject
{
    public FailedLogonSeverity Severity { get; } = severity;

    public string Text { get; } = text;

    public int Count { get; } = count;

    public bool HasFindings => Count > 0;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public override string ToString() => Text;
}
