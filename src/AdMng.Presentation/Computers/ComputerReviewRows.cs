using System.Globalization;
using System.Net;
using AdMng.Core.Computers;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.Computers;

/// <summary>Where the site of a member computer came from (K1 design note 4.4).</summary>
public enum SiteResolutionState
{
    /// <summary>The DNS name resolved and a subnet contains the address.</summary>
    Resolved,

    /// <summary>The address is in no subnet of the forest ("brak podsieci").</summary>
    NoSubnet,

    /// <summary>The DNS name did not resolve ("nie rozwiązano"), or the computer has none.</summary>
    NotResolved,

    /// <summary>A domain controller: its server object's site.</summary>
    DomainController,
}

/// <summary>The site of one computer, resolved on demand and kept in memory only.</summary>
public sealed record SiteResolution(SiteResolutionState State, IPAddress? Address, string? Subnet, string? Site);

/// <summary>Everything the review knows about one computer: the facts, the evaluated state and what was resolved later.</summary>
public sealed record ComputerReviewFacts
{
    public required ComputerFacts Facts { get; init; }

    public required ComputerStatus Status { get; init; }

    /// <summary>The managedBy state (resolved after the stream).</summary>
    public OwnerState Owner { get; init; } = OwnerState.Unknown;

    /// <summary>The name of the creator (mS-DS-CreatorSID), resolved after the stream.</summary>
    public string? CreatorName { get; init; }

    public DateTimeOffset? ExactLastLogon { get; init; }

    public string? ExactLastLogonServer { get; init; }

    /// <summary>"Dokładne ostatnie logowanie" was read for this computer.</summary>
    public bool HasExactLastLogon { get; init; }

    public SiteResolution? Site { get; init; }

    public string DistinguishedName => Facts.DistinguishedName;
}

/// <summary>Columns of the computer review lists (ids are the binding keys of <see cref="ComputerReviewRowViewModel"/>).</summary>
public static class ComputerReviewColumns
{
    public const string Name = "Name";
    public const string DnsName = "DnsName";
    public const string Type = "Type";
    public const string System = "System";
    public const string Release = "Release";
    public const string Support = "Support";
    public const string LastLogon = "LastLogon";
    public const string LogonDays = "LogonDays";
    public const string ExactLastLogon = "ExactLastLogon";
    public const string Created = "Created";
    public const string Creator = "Creator";
    public const string Owner = "Owner";
    public const string Placement = "Placement";
    public const string Location = "Location";
    public const string Description = "Description";
    public const string Container = "Container";
    public const string Site = "Site";
    public const string Address = "Address";
    public const string Laps = "Laps";
    public const string AccountRenewed = "AccountRenewed";
    public const string DistinguishedName = "DistinguishedName";

    /// <summary>Every column id (localization keys <c>ComputerReview.Column.{id}</c>).</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Name, DnsName, Type, System, Release, Support, LastLogon, LogonDays, ExactLastLogon, Created, Creator, Owner, Placement,
        Location, Description, Container, Site, Address, Laps, AccountRenewed, DistinguishedName,
    ];

    /// <summary>The columns of a report's list (plus site and address once sites were resolved).</summary>
    public static IReadOnlyList<string> For(ComputerReport report, bool withSites)
    {
        IReadOnlyList<string> middle = report switch
        {
            ComputerReport.EndOfSupport => [Type, System, Release, Support, LastLogon, Owner],
            ComputerReport.Releases => [Type, System, Release, Support, LastLogon],
            ComputerReport.Inactive => [Type, System, LastLogon, LogonDays, ExactLastLogon, Created],
            ComputerReport.NeverLoggedOn => [Created, Creator, ExactLastLogon, Description],
            ComputerReport.ServersWithoutOwner => [DnsName, System, Owner, LastLogon, Description],
            ComputerReport.UnexpectedContainer => [Type, Placement, System, LastLogon],
            ComputerReport.Disabled => [Type, System, LastLogon, Description],
            ComputerReport.WithoutOperatingSystem => [Created, Creator, Description],
            _ => [Type, System],
        };
        return withSites ? [Name, .. middle, Site, Address, Container] : [Name, .. middle, Container];
    }
}

/// <summary>One column of the current list.</summary>
public sealed record ComputerReviewColumnViewModel(string Id, string Header);

/// <summary>Texts and sort keys of the review cells in the UI language and the operator's time zone.</summary>
internal sealed class ComputerReviewFormatter(ILocalizer localizer, TimeZoneInfo timeZone)
{
    public ILocalizer Localizer { get; } = localizer;

    private CultureInfo Culture => Localizer.Culture;

    public static string ReportKey(ComputerReport report) => $"ComputerReview.Report.{report}";

    public static string ReportHintKey(ComputerReport report) => $"ComputerReview.Hint.{report}";

    public static string AreaKey(ComputerReportArea area) => $"ComputerReview.Area.{area}";

    public static string ColumnKey(string column) => $"ComputerReview.Column.{column}";

    public static string TypeKey(ComputerType type) => $"Computers.Type.{type}";

    public static string SupportKey(SupportState state) => $"WindowsRelease.Support.{state}";

    public static string PlacementKey(PlacementFinding finding) => $"Computers.Placement.{finding}";

    public static string OwnerKey(OwnerState state) => $"Computers.Owner.{state}";

    public string Text(string column, ComputerReviewFacts row)
    {
        var f = row.Facts;
        var s = row.Status;
        return column switch
        {
            ComputerReviewColumns.Name => f.Name,
            ComputerReviewColumns.DnsName => f.DnsHostName ?? string.Empty,
            ComputerReviewColumns.Type => Localizer[TypeKey(f.Type)],
            ComputerReviewColumns.System => System(f),
            ComputerReviewColumns.Release => s.Release.Release?.Release ?? string.Empty,
            ComputerReviewColumns.Support => Support(s.Release),
            ComputerReviewColumns.LastLogon => f.LastLogonTimestamp is null ? Localizer[L.ComputerReview.Never] : Moment(f.LastLogonTimestamp),
            ComputerReviewColumns.LogonDays => s.DaysSinceLogon is { } days ? Localizer.Format(L.ComputerReview.DaysAgo, days.ToString("N0", Culture)) : string.Empty,
            ComputerReviewColumns.ExactLastLogon => ExactLastLogon(row),
            ComputerReviewColumns.Created => Moment(f.Created),
            ComputerReviewColumns.Creator => f.CreatorSidValue is null ? string.Empty : row.CreatorName ?? f.CreatorSidValue,
            ComputerReviewColumns.Owner => Owner(row),
            ComputerReviewColumns.Placement => string.Join(", ", s.Placement.Select(p => Localizer[PlacementKey(p)])),
            ComputerReviewColumns.Location => f.Location ?? string.Empty,
            ComputerReviewColumns.Description => f.Description ?? string.Empty,
            ComputerReviewColumns.Container => f.ParentDn is { } parent ? AddressResolver.ToDisplayPath(parent) : string.Empty,
            ComputerReviewColumns.Site => Site(row.Site),
            ComputerReviewColumns.Address => row.Site?.Address?.ToString() ?? string.Empty,
            ComputerReviewColumns.Laps => f.LapsExpiration is { } laps ? Localizer.Format(L.ComputerReview.LapsUntil, Moment(laps)) : string.Empty,
            ComputerReviewColumns.AccountRenewed => Moment(f.PasswordLastSet),
            ComputerReviewColumns.DistinguishedName => f.DistinguishedName,
            _ => string.Empty,
        };
    }

    /// <summary>Sort key: instants and numbers by value, the rest by text.</summary>
    public IComparable SortKey(string column, ComputerReviewFacts row)
    {
        var f = row.Facts;
        return column switch
        {
            ComputerReviewColumns.Support => row.Status.Release.EndOfSupport?.DayNumber ?? int.MaxValue,
            ComputerReviewColumns.LastLogon or ComputerReviewColumns.LogonDays => f.LastLogonTimestamp?.UtcTicks ?? long.MinValue,
            ComputerReviewColumns.ExactLastLogon => row.ExactLastLogon?.UtcTicks ?? long.MinValue,
            ComputerReviewColumns.Created => f.Created?.UtcTicks ?? long.MinValue,
            ComputerReviewColumns.AccountRenewed => f.PasswordLastSet?.UtcTicks ?? long.MinValue,
            ComputerReviewColumns.Laps => f.LapsExpiration?.UtcTicks ?? long.MinValue,
            _ => Text(column, row),
        };
    }

    public string Moment(DateTimeOffset? instant) =>
        instant is { } value ? TimeZoneInfo.ConvertTime(value, timeZone).ToString("g", Culture) : string.Empty;

    public string Date(DateOnly day) => day.ToString("d", Culture);

    /// <summary>"Windows 11 Pro (10.0.26100)".</summary>
    public string System(ComputerFacts facts)
    {
        if (string.IsNullOrWhiteSpace(facts.OperatingSystem))
        {
            return string.Empty;
        }

        return facts.Version is { } version ? $"{facts.OperatingSystem} ({version})" : facts.OperatingSystem;
    }

    /// <summary>"Zakończone 13.10.2026", "Kończy się 10.11.2026 (za 14 dni)", "Wspierane do …", "Nieznane".</summary>
    public string Support(WindowsReleaseMatch match)
    {
        var text = match.EndOfSupport is { } end
            ? match.State switch
            {
                SupportState.Ended => Localizer.Format(L.WindowsRelease.EndedOn, Date(end)),
                SupportState.EndingSoon => Localizer.Format(L.WindowsRelease.EndsOn, Date(end), (match.DaysLeft ?? 0).ToString("N0", Culture)),
                _ => Localizer.Format(L.WindowsRelease.SupportedUntil, Date(end)),
            }
            : Localizer[SupportKey(match.State)];
        return match.IsOverridden ? Localizer.Format(L.WindowsRelease.Overridden, text) : text;
    }

    public string Site(SiteResolution? site) => site switch
    {
        null => string.Empty,
        { State: SiteResolutionState.NotResolved } => Localizer[L.ComputerReview.SiteNotResolved],
        { State: SiteResolutionState.NoSubnet } => Localizer[L.ComputerReview.NoSubnet],
        { Subnet: { } subnet, Site: { } name } => $"{name} ({subnet})",
        _ => site.Site ?? string.Empty,
    };

    private string ExactLastLogon(ComputerReviewFacts row)
    {
        if (!row.HasExactLastLogon)
        {
            return string.Empty;
        }

        return row.ExactLastLogon is { } exact
            ? Localizer.Format(L.ComputerReview.ExactOn, Moment(exact), row.ExactLastLogonServer ?? string.Empty)
            : Localizer[L.ComputerReview.Never];
    }

    private string Owner(ComputerReviewFacts row)
    {
        if (row.Facts.ManagedBy is not { } owner)
        {
            return row.Owner == OwnerState.Unknown ? string.Empty : Localizer[OwnerKey(OwnerState.Missing)];
        }

        var name = Core.Ldap.DistinguishedName.TryParse(owner, out var parsed) && !parsed.IsRoot ? parsed.Name : owner;
        return row.Owner is OwnerState.Disabled or OwnerState.Deleted ? Localizer.Format(L.ComputerReview.OwnerWithState, name, Localizer[OwnerKey(row.Owner)]) : name;
    }
}

/// <summary>One computer in the review lists (it may be a finding of several reports).</summary>
public sealed class ComputerReviewRowViewModel : ObservableObject
{
    private ComputerReviewFormatter _formatter;

    internal ComputerReviewRowViewModel(ComputerReviewFacts facts, IReadOnlySet<ComputerReport> reports, ComputerReviewFormatter formatter)
    {
        Facts = facts;
        Reports = reports;
        _formatter = formatter;
    }

    public ComputerReviewFacts Facts { get; private set; }

    /// <summary>The reports this computer is a finding of.</summary>
    public IReadOnlySet<ComputerReport> Reports { get; private set; }

    public string Dn => Facts.DistinguishedName;

    public string Name => Facts.Facts.Name;

    /// <summary>The release group of the releases report.</summary>
    public string ReleaseGroup => ComputerReports.ReleaseGroup(Facts.Facts, Facts.Status);

    public ObjectIcon Icon => !Facts.Facts.IsEnabled ? ObjectIcon.ComputerDisabled
        : Facts.Facts.Type.IsDomainController() ? ObjectIcon.DomainController
        : Facts.Facts.Type == ComputerType.Server ? ObjectIcon.Server
        : ObjectIcon.Computer;

    /// <summary>The support state (the list colours the cell).</summary>
    public SupportState Support => Facts.Status.Release.State;

    /// <summary>Text of a column (<see cref="ComputerReviewColumns"/>); bound as <c>[ColumnId]</c>.</summary>
    public string this[string column] => _formatter.Text(column, Facts);

    internal IComparable SortKey(string column) => _formatter.SortKey(column, Facts);

    internal void Update(ComputerReviewFacts facts, IReadOnlySet<ComputerReport> reports)
    {
        Facts = facts;
        Reports = reports;
        RaiseAll();
    }

    internal void UseFormatter(ComputerReviewFormatter formatter)
    {
        _formatter = formatter;
        RaiseAll();
    }

    private void RaiseAll()
    {
        OnPropertyChanged("Item[]");
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(Support));
        OnPropertyChanged(nameof(Facts));
    }
}

/// <summary>A tile: a report (or one release of the releases report) with its number of computers.</summary>
public sealed partial class ComputerReportTileViewModel(ComputerReport report, string? release = null) : ObservableObject
{
    public ComputerReport Report { get; } = report;

    /// <summary>The release group of a releases tile, null for the other reports.</summary>
    public string? Release { get; } = release;

    public ComputerReportArea Area => ComputerReports.AreaOf(Report);

    /// <summary>The tile marks a problem when it has findings (the releases tiles are informational).</summary>
    public bool IsProblem => Report is not (ComputerReport.Releases or ComputerReport.Disabled);

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AreaTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Hint { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFindings))]
    public partial int Count { get; private set; }

    [ObservableProperty]
    public partial bool IsComplete { get; private set; }

    [ObservableProperty]
    public partial string CountText { get; private set; } = "0";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public bool HasFindings => Count > 0;

    /// <summary>The row belongs to this tile's list.</summary>
    public bool Contains(ComputerReviewRowViewModel row) =>
        row.Reports.Contains(Report) && (Release is null || string.Equals(row.ReleaseGroup, Release, StringComparison.Ordinal));

    internal void SetCount(int count, bool complete, CultureInfo culture)
    {
        Count = count;
        IsComplete = complete;
        var text = count.ToString("N0", culture);
        CountText = complete ? text : $"{text}…";
    }
}

/// <summary>A report in the parameters: run it or not.</summary>
public sealed partial class ComputerReportOptionViewModel(ComputerReport report) : ObservableObject
{
    public ComputerReport Report { get; } = report;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;
}

/// <summary>One line of the details pane.</summary>
public sealed record ComputerDetailViewModel(string Label, string Value);
