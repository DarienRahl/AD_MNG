namespace AdMng.Core.Computers;

/// <summary>Edition class of a Windows installation: end-of-support dates differ by it (K1 design note 4.1).</summary>
public enum EditionClass
{
    /// <summary>Home, Pro, Pro Education, Pro for Workstations (24 months per Windows 11 feature update).</summary>
    HomePro,

    /// <summary>Enterprise, Education, IoT Enterprise, Enterprise multi-session (36 months per Windows 11 feature update).</summary>
    EnterpriseEducation,

    /// <summary>Enterprise LTSC / LTSB.</summary>
    Ltsc,

    /// <summary>IoT Enterprise LTSC / LTSB.</summary>
    IotLtsc,

    /// <summary>Windows Server (any edition) and Hyper-V Server, which follows the Windows Server release of its build.</summary>
    Server,

    /// <summary>
    /// Azure Stack HCI / Azure Local: Windows Server based, but each version has its own end of service (six months after
    /// the next one), not that of the Windows Server release of the same build.
    /// </summary>
    AzureLocal,
}

/// <summary>Support state of a computer's operating system.</summary>
public enum SupportState
{
    Supported,

    /// <summary>The end of support is within the warning horizon (default 90 days).</summary>
    EndingSoon,

    Ended,

    /// <summary>A Windows build the catalogue does not know (or a release without a confirmed date).</summary>
    Unknown,

    /// <summary>The operating system string is not Windows (Samba, Linux, macOS …) or missing.</summary>
    NotWindows,
}

/// <summary>One row of the catalogue: a build and edition class → release name and end of support.</summary>
/// <param name="Build">OS build (operatingSystemVersion "10.0 (26100)" → 26100).</param>
/// <param name="Edition">Edition class the date applies to.</param>
/// <param name="Release">Release name, e.g. <c>Windows 11 24H2</c>.</param>
/// <param name="EndOfSupport">Last day of support (Microsoft Lifecycle, Pacific Time date), null when not confirmed.</param>
public sealed record WindowsRelease(int Build, EditionClass Edition, string Release, DateOnly? EndOfSupport);

/// <summary>An operator's override from the settings: build + edition class → date (e.g. ESU bought for Windows 10).</summary>
public sealed record WindowsReleaseOverride(int Build, EditionClass Edition, DateOnly EndOfSupport);

/// <summary>What the catalogue says about one computer.</summary>
/// <param name="Release">The catalogue row (null: unknown build or not Windows).</param>
/// <param name="Edition">The edition class from the product string (null when not Windows).</param>
/// <param name="EndOfSupport">The date that counts (override first), null when unknown.</param>
/// <param name="State">Support state at the evaluation date.</param>
/// <param name="IsOverridden">The date comes from an operator's override.</param>
public sealed record WindowsReleaseMatch(WindowsRelease? Release, EditionClass? Edition, DateOnly? EndOfSupport, SupportState State, bool IsOverridden = false)
{
    /// <summary>Days from the evaluation date to the end of support (negative after it), null without a date.</summary>
    public int? DaysLeft { get; init; }
}

/// <summary>
/// Built-in table of Windows releases (K1 design note 4.1): the BUILD decides the release, not the product string –
/// early Windows 11 builds wrote "Windows 10 …" into operatingSystem, so build ≥ 22000 on a client string is Windows 11.
/// The product string decides the family (Server) and the edition class. Dates were checked against Microsoft Lifecycle
/// on <see cref="ValidAsOf"/>; the operator's overrides (settings) win. Pure, immutable (overrides per evaluation).
/// </summary>
public sealed class WindowsReleaseCatalog
{
    /// <summary>The day the dates were last checked against Microsoft Lifecycle.</summary>
    public static readonly DateOnly ValidAsOf = new(2026, 10, 27);

    /// <summary>The window warns when the catalogue is older than this.</summary>
    public const int StaleAfterMonths = 9;

    /// <summary>Default warning horizon of <see cref="SupportState.EndingSoon"/>.</summary>
    public const int DefaultEndingSoonDays = 90;

    /// <summary>The first Windows 11 build (21H2).</summary>
    public const int FirstWindows11Build = 22000;

    private static readonly EditionClass[] LtscEditions = [EditionClass.Ltsc, EditionClass.IotLtsc];

    private readonly Dictionary<(int Build, EditionClass Edition), WindowsRelease> _rows;

    public WindowsReleaseCatalog()
        : this(BuiltIn())
    {
    }

    public WindowsReleaseCatalog(IEnumerable<WindowsRelease> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _rows = rows.ToDictionary(r => (r.Build, r.Edition));
    }

    /// <summary>Every row (for the settings and the details pane).</summary>
    public IReadOnlyCollection<WindowsRelease> Rows => _rows.Values;

    /// <summary>The built-in rows (dates from Microsoft Lifecycle, see <see cref="ValidAsOf"/>).</summary>
    public static IReadOnlyList<WindowsRelease> BuiltIn()
    {
        var rows = new List<WindowsRelease>();
        void Client(int build, string release, string? homePro, string? enterprise)
        {
            rows.Add(new(build, EditionClass.HomePro, release, Date(homePro)));
            rows.Add(new(build, EditionClass.EnterpriseEducation, release, Date(enterprise)));
        }

        void Ltsc(int build, string release, string? ltsc, string? iot, string? iotRelease = null)
        {
            rows.Add(new(build, EditionClass.Ltsc, release, Date(ltsc)));
            rows.Add(new(build, EditionClass.IotLtsc, iotRelease ?? release.Replace("Enterprise", "IoT Enterprise", StringComparison.Ordinal), Date(iot)));
        }

        void Server(int build, string release, string? end) => rows.Add(new(build, EditionClass.Server, release, Date(end)));

        void AzureLocal(int build, string release, string? end) => rows.Add(new(build, EditionClass.AzureLocal, release, Date(end)));

        // Windows 11 (feature updates: Home/Pro 24 months, Enterprise/Education 36 months).
        Client(28000, "Windows 11 26H1", null, null);
        Client(26200, "Windows 11 25H2", "2027-10-12", "2028-10-10");
        Client(26100, "Windows 11 24H2", "2026-10-13", "2027-10-12");
        Client(22631, "Windows 11 23H2", "2025-11-11", "2026-11-10");
        Client(22621, "Windows 11 22H2", "2024-10-08", "2025-10-14");
        Client(22000, "Windows 11 21H2", "2023-10-10", "2024-10-08");
        Ltsc(26100, "Windows 11 Enterprise LTSC 2024", "2029-10-09", "2034-10-10");

        // Windows 10.
        Client(19045, "Windows 10 22H2", "2025-10-14", "2025-10-14");
        Client(19044, "Windows 10 21H2", "2023-06-13", "2024-06-11");
        Client(19043, "Windows 10 21H1", "2022-12-13", "2022-12-13");
        Client(19042, "Windows 10 20H2", "2022-05-10", "2023-05-09");
        Client(19041, "Windows 10 2004", "2021-12-14", "2021-12-14");
        Ltsc(19044, "Windows 10 Enterprise LTSC 2021", "2027-01-12", "2032-01-13");
        Ltsc(17763, "Windows 10 Enterprise LTSC 2019", "2029-01-09", "2029-01-09");
        Ltsc(14393, "Windows 10 Enterprise LTSB 2016", "2026-10-13", "2026-10-13");
        Ltsc(10240, "Windows 10 Enterprise LTSB 2015", "2025-10-14", "2025-10-14");

        // Older clients (ended).
        Client(9600, "Windows 8.1", "2023-01-10", "2023-01-10");
        Client(9200, "Windows 8", "2016-01-12", "2016-01-12");
        Client(7601, "Windows 7 SP1", "2020-01-14", "2020-01-14");
        Client(7600, "Windows 7", "2013-04-09", "2013-04-09");

        // Windows Server (fixed lifecycle: extended end; the annual channel 23H2 has its own date).
        Server(26100, "Windows Server 2025", "2034-11-14");
        Server(25398, "Windows Server 23H2", "2025-10-24");
        Server(20348, "Windows Server 2022", "2031-10-14");
        Server(17763, "Windows Server 2019", "2029-01-09");
        Server(14393, "Windows Server 2016", "2027-01-12");
        Server(9600, "Windows Server 2012 R2", "2023-10-10");
        Server(9200, "Windows Server 2012", "2023-10-10");
        Server(7601, "Windows Server 2008 R2 SP1", "2020-01-14");
        Server(7600, "Windows Server 2008 R2", "2020-01-14");
        Server(6003, "Windows Server 2008", "2020-01-14");
        Server(6002, "Windows Server 2008", "2020-01-14");

        // Azure Stack HCI / Azure Local (end of service per version; from 23H2 each solution version is supported for a
        // limited time, which the OS build does not tell – no date).
        AzureLocal(26100, "Azure Local 24H2", null);
        AzureLocal(25398, "Azure Local 23H2", null);
        AzureLocal(20349, "Azure Stack HCI 22H2", "2025-05-31");
        AzureLocal(20348, "Azure Stack HCI 21H2", "2023-11-14");
        AzureLocal(17784, "Azure Stack HCI 20H2", "2022-12-13");
        return rows;

        static DateOnly? Date(string? value) => value is null ? null : DateOnly.ParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The edition class of an operatingSystem value (null when it is not Windows): Azure Stack HCI / Azure Local, Server
    /// (also Hyper-V Server), IoT LTSC, LTSC/LTSB, Enterprise/Education (Pro Education counts as Pro), else Home/Pro.
    /// </summary>
    public static EditionClass? EditionOf(string? operatingSystem)
    {
        if (!IsWindows(operatingSystem))
        {
            return null;
        }

        var os = operatingSystem!;
        bool Has(string text) => os.Contains(text, StringComparison.OrdinalIgnoreCase);
        if (Has("Azure Stack HCI") || Has("Azure Local"))
        {
            return EditionClass.AzureLocal;
        }

        if (Has("Server"))
        {
            return EditionClass.Server;
        }

        var ltsc = Has("LTSC") || Has("LTSB");
        if (ltsc)
        {
            return Has("IoT") ? EditionClass.IotLtsc : EditionClass.Ltsc;
        }

        if (Has("Pro Education") || Has("Pro for Workstations"))
        {
            return EditionClass.HomePro;
        }

        return Has("Enterprise") || Has("Education") ? EditionClass.EnterpriseEducation : EditionClass.HomePro;
    }

    /// <summary>
    /// The value names Windows or a Windows Server based Microsoft product whose string lacks "Windows" (Hyper-V Server,
    /// Azure Stack HCI, Azure Local); everything else – Samba, Linux, macOS, empty – is not.
    /// </summary>
    public static bool IsWindows(string? operatingSystem) =>
        !string.IsNullOrWhiteSpace(operatingSystem)
        && (operatingSystem.Contains("Windows", StringComparison.OrdinalIgnoreCase)
            || operatingSystem.Contains("Hyper-V Server", StringComparison.OrdinalIgnoreCase)
            || operatingSystem.Contains("Azure Stack HCI", StringComparison.OrdinalIgnoreCase)
            || operatingSystem.Contains("Azure Local", StringComparison.OrdinalIgnoreCase));

    /// <summary>The catalogue row of a build and edition class, or null.</summary>
    public WindowsRelease? Find(int build, EditionClass edition)
    {
        if (_rows.TryGetValue((build, edition), out var row))
        {
            return row;
        }

        // LTSC builds without an LTSC row (e.g. a client build only listed for Home/Pro) fall back to Enterprise dates.
        return LtscEditions.Contains(edition) && _rows.TryGetValue((build, EditionClass.EnterpriseEducation), out var enterprise)
            ? enterprise
            : null;
    }

    /// <summary>
    /// Matches a computer's operatingSystem / operatingSystemVersion against the catalogue at <paramref name="today"/>
    /// (the operator's local date). Overrides (build + edition class) win over the catalogue's date.
    /// </summary>
    public WindowsReleaseMatch Match(
        string? operatingSystem,
        string? operatingSystemVersion,
        DateOnly today,
        int endingSoonDays = DefaultEndingSoonDays,
        IReadOnlyCollection<WindowsReleaseOverride>? overrides = null)
    {
        if (EditionOf(operatingSystem) is not { } edition)
        {
            return new WindowsReleaseMatch(null, null, null, SupportState.NotWindows);
        }

        if (!OperatingSystemVersion.TryParse(operatingSystemVersion, out var version) || version!.Build is not { } build)
        {
            return new WindowsReleaseMatch(null, edition, null, SupportState.Unknown);
        }

        var row = Find(build, edition);
        var overridden = overrides?.FirstOrDefault(o => o.Build == build && o.Edition == edition);
        var end = overridden?.EndOfSupport ?? row?.EndOfSupport;
        if (end is not { } endDate)
        {
            // A build the table does not list that is older than a release of its edition class whose support has ended
            // (Windows 10 1809 Pro, Server 1903 …) has ended too: releases end in build order.
            var ended = row is null && IsOlderThanAnEndedRelease(build, edition, today);
            return new WindowsReleaseMatch(row, edition, null, ended ? SupportState.Ended : SupportState.Unknown, overridden is not null);
        }

        var days = endDate.DayNumber - today.DayNumber;
        var state = days < 0 ? SupportState.Ended : days <= Math.Max(0, endingSoonDays) ? SupportState.EndingSoon : SupportState.Supported;
        return new WindowsReleaseMatch(row, edition, endDate, state, overridden is not null) { DaysLeft = days };
    }

    private bool IsOlderThanAnEndedRelease(int build, EditionClass edition, DateOnly today)
    {
        var editions = edition switch
        {
            EditionClass.Ltsc or EditionClass.IotLtsc => [edition, EditionClass.EnterpriseEducation],
            _ => new[] { edition },
        };
        return _rows.Values.Any(r => editions.Contains(r.Edition) && r.Build > build && r.EndOfSupport is { } end && end < today);
    }
}
