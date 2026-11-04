using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;

namespace AdMng.Core.Computers;

/// <summary>A report of the computer review (K1 design note 4.3). The order is the order of the tiles.</summary>
public enum ComputerReport
{
    /// <summary>Operating system past (or within N days of) its end of support.</summary>
    EndOfSupport,

    /// <summary>Windows 11 and Server releases: one tile per release with its count.</summary>
    Releases,

    /// <summary>No logon for more than N days.</summary>
    Inactive,

    /// <summary>Never logged on and created more than N days ago (prestaged accounts nobody joined).</summary>
    NeverLoggedOn,

    /// <summary>Servers whose managedBy is missing, disabled or deleted.</summary>
    ServersWithoutOwner,

    /// <summary>Computers in unexpected containers (built-in and configured placement rules).</summary>
    UnexpectedContainer,

    /// <summary>Disabled computer accounts (informational).</summary>
    Disabled,

    /// <summary>No operatingSystem (prestaged or non-Windows).</summary>
    WithoutOperatingSystem,
}

/// <summary>Group of a report's tile.</summary>
public enum ComputerReportArea
{
    /// <summary>System: end of support, releases.</summary>
    System,

    /// <summary>Activity: inactive, never logged on, disabled.</summary>
    Activity,

    /// <summary>Order: servers without owner, unexpected containers, without operating system.</summary>
    Order,
}

/// <summary>The N-day parameters of the computer review (persisted in the settings).</summary>
public sealed record ComputerReportParameters
{
    public const int MaxDays = 3650;

    /// <summary>"Nieaktywne od N dni" and the creation grace of "never logged on".</summary>
    public int InactiveDays { get; init; } = 90;

    /// <summary>"Koniec wsparcia w ciągu N dni".</summary>
    public int EndingSoonDays { get; init; } = WindowsReleaseCatalog.DefaultEndingSoonDays;

    /// <summary>Values from a hand-edited settings file brought into range.</summary>
    public ComputerReportParameters Normalized() => new()
    {
        InactiveDays = Math.Clamp(InactiveDays, 1, MaxDays),
        EndingSoonDays = Math.Clamp(EndingSoonDays, 0, MaxDays),
    };
}

/// <summary>
/// The queries of the computer review (K1 design note 4.2/4.3): every computer account (sAMAccountType MACHINE_ACCOUNT)
/// except managed service accounts, ANDed with the scope and the OR of the enabled reports' server pre-filters (a report
/// without one reads the whole scope); the reports' exact conditions run on the client. Every filter through
/// <see cref="LdapFilter"/>.
/// </summary>
public static class ComputerReports
{
    /// <summary>sAMAccountType MACHINE_ACCOUNT.</summary>
    public const long MachineAccount = 805306369;

    private const long AccountDisable = (long)UserAccountControl.AccountDisable;

    /// <summary>The area of a report's tile.</summary>
    public static ComputerReportArea AreaOf(ComputerReport report) => report switch
    {
        ComputerReport.EndOfSupport or ComputerReport.Releases => ComputerReportArea.System,
        ComputerReport.Inactive or ComputerReport.NeverLoggedOn or ComputerReport.Disabled => ComputerReportArea.Activity,
        _ => ComputerReportArea.Order,
    };

    /// <summary>Computer accounts without the managed service account classes.</summary>
    public static string ComputersFilter => LdapFilter.And(MachineAccountFilter, NotManagedServiceAccountFilter);

    private static string MachineAccountFilter => LdapFilter.Equal(Attr.SamAccountType, MachineAccount);

    private static string NotManagedServiceAccountFilter => LdapFilter.Not(LdapFilter.Or(
        LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.GroupManagedServiceAccount),
        LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.ManagedServiceAccount),
        LdapFilter.Equal(Attr.ObjectClass, ObjectClasses.DelegatedManagedServiceAccount)));

    /// <summary>The server pre-filter of a report, null when it needs every computer of the scope.</summary>
    public static string? PreFilter(ComputerReport report, ComputerReportParameters parameters, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var enabled = LdapFilter.Not(LdapFilter.BitAnd(Attr.UserAccountControl, AccountDisable));
        var cutoff = now.AddDays(-parameters.InactiveDays);
        return report switch
        {
            ComputerReport.EndOfSupport => LdapFilter.And(enabled, LdapFilter.Present(Attr.OperatingSystem)),
            ComputerReport.Releases => LdapFilter.Present(Attr.OperatingSystem),
            ComputerReport.Inactive => LdapFilter.And(enabled, LdapFilter.LessOrEqual(Attr.LastLogonTimestamp, AdConvert.ToFileTime(cutoff))),
            ComputerReport.NeverLoggedOn => LdapFilter.And(
                enabled,
                LdapFilter.NotPresent(Attr.LastLogonTimestamp),
                LdapFilter.LessOrEqual(Attr.WhenCreated, AdConvert.ToGeneralizedTime(cutoff))),
            ComputerReport.ServersWithoutOwner => LdapFilter.And(enabled, LdapFilter.Contains(Attr.OperatingSystem, "Server")),
            ComputerReport.Disabled => LdapFilter.BitAnd(Attr.UserAccountControl, AccountDisable),
            ComputerReport.WithoutOperatingSystem => LdapFilter.NotPresent(Attr.OperatingSystem),
            _ => null,
        };
    }

    /// <summary>The search filter of a run: computers AND scope AND (pre-filter of report 1 OR …).</summary>
    public static string Filter(IReadOnlyCollection<ComputerReport> reports, string? scopeFilter, ComputerReportParameters parameters, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reports);
        var prefilters = reports.Select(r => PreFilter(r, parameters, now)).ToList();
        var reportFilter = prefilters.Count == 0 || prefilters.Any(p => p is null) ? null : LdapFilter.Or(prefilters);
        return LdapFilter.And(MachineAccountFilter, NotManagedServiceAccountFilter, scopeFilter, reportFilter);
    }

    /// <summary>Whether a computer is a finding of <paramref name="report"/> (the client-side condition).</summary>
    public static bool Matches(ComputerReport report, ComputerFacts facts, ComputerStatus status, OwnerState owner, ComputerReportParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(parameters);
        return report switch
        {
            ComputerReport.EndOfSupport => facts.IsEnabled && status.Release.State is SupportState.Ended or SupportState.EndingSoon,
            ComputerReport.Releases => !string.IsNullOrWhiteSpace(facts.OperatingSystem),
            ComputerReport.Inactive => facts.IsEnabled && status.DaysSinceLogon > parameters.InactiveDays,
            ComputerReport.NeverLoggedOn => facts.IsEnabled && status.NeverLoggedOn && status.DaysSinceCreated >= parameters.InactiveDays,
            ComputerReport.ServersWithoutOwner => facts.IsEnabled && facts.Type == ComputerType.Server
                && owner is OwnerState.Missing or OwnerState.Disabled or OwnerState.Deleted,
            ComputerReport.UnexpectedContainer => status.Placement.Count > 0,
            ComputerReport.Disabled => !facts.IsEnabled,
            ComputerReport.WithoutOperatingSystem => string.IsNullOrWhiteSpace(facts.OperatingSystem),
            _ => false,
        };
    }

    /// <summary>The group of a computer in the releases report: the catalogue's release, else "Windows (build N)", else the OS string.</summary>
    public static string ReleaseGroup(ComputerFacts facts, ComputerStatus status)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(status);
        if (status.Release.Release is { } release)
        {
            return release.Release;
        }

        return WindowsReleaseCatalog.IsWindows(facts.OperatingSystem) && facts.Version?.Build is { } build
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{facts.OperatingSystem} ({build})")
            : facts.OperatingSystem ?? string.Empty;
    }
}
