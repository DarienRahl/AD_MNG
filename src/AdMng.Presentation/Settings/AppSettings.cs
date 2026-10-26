using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Presentation.Views;

namespace AdMng.Presentation.Settings;

/// <summary>Light/dark theme choice.</summary>
public enum ThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>
/// User settings persisted as JSON. Contains UI preferences, connection profiles WITHOUT passwords and view
/// definitions (names, DNs, LDAP filters) only - never directory content (CLAUDE.md rule 1) and never passwords
/// (rule 2; a unit test walks this type graph and the serialized JSON to enforce it).
/// </summary>
public sealed class AppSettings
{
    /// <summary>Maximum number of entries in <see cref="RecentContainers"/>.</summary>
    public const int MaxRecentContainers = 10;

    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>"" = system language, "pl" or "en".</summary>
    public string Language { get; set; } = string.Empty;

    public ThemePreference Theme { get; set; } = ThemePreference.System;

    public List<ConnectionProfile> ConnectionProfiles { get; set; } = [];

    /// <summary>Profile preselected in the connect window.</summary>
    public string? LastProfileName { get; set; }

    /// <summary>Operator defined views, in the order shown in the navigation tree.</summary>
    public List<SavedView> SavedViews { get; set; } = [];

    public List<PinnedContainer> PinnedContainers { get; set; } = [];

    /// <summary>Column layout key (see <see cref="Lists.ListLocation.ColumnLayoutKey"/>) -> visible columns in order.</summary>
    public Dictionary<string, List<ColumnLayoutEntry>> ColumnLayouts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>ADUC "View > Advanced Features" (shows showInAdvancedViewOnly objects such as System, LostAndFound).</summary>
    public bool ShowAdvancedFeatures { get; set; }

    public bool ShowDetailsPane { get; set; } = true;

    /// <summary>Subscribe to directory change notifications for the open container and refresh changed rows.</summary>
    public bool LiveUpdates { get; set; } = true;

    /// <summary>LDAP paged results page size (AD MaxPageSize defaults to 1000).</summary>
    public int PageSize { get; set; } = 500;

    public int BatchMaxParallelism { get; set; } = 4;

    /// <summary>Above this number of objects simple actions open the batch preview and need a typed confirmation.</summary>
    public int ConfirmationThreshold { get; set; } = 25;

    /// <summary>"Inactive N days" quick filters and views.</summary>
    public int InactiveDays { get; set; } = 90;

    /// <summary>Most recently opened containers (DNs only), newest first.</summary>
    public List<string> RecentContainers { get; set; } = [];

    /// <summary>The Watchdog tab: service address, last sub-tab and named filters (never watchdog data).</summary>
    public WatchdogClientSettings Watchdog { get; set; } = new();

    /// <summary>The account review: N-day parameters, enabled checks, scope and named check sets (never results).</summary>
    public AccountReviewSettings AccountReview { get; set; } = new();

    /// <summary>Messages to users (U1): sender, limits, SMTP connection without a password, operator templates (never messages).</summary>
    public UserMessageSettings UserMessages { get; set; } = new();

    /// <summary>Puts <paramref name="distinguishedName"/> first in <see cref="RecentContainers"/> (deduplicated, max 10).</summary>
    public void AddRecentContainer(string distinguishedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distinguishedName);
        RecentContainers.RemoveAll(d => DistinguishedName.AreEqual(d, distinguishedName));
        RecentContainers.Insert(0, distinguishedName);
        if (RecentContainers.Count > MaxRecentContainers)
        {
            RecentContainers.RemoveRange(MaxRecentContainers, RecentContainers.Count - MaxRecentContainers);
        }
    }

    /// <summary>Repairs values that may come from a hand-edited or older settings file.</summary>
    public void Normalize()
    {
        Language = Language is "pl" or "en" ? Language : string.Empty;
        ConnectionProfiles ??= [];
        SavedViews ??= [];
        PinnedContainers ??= [];
        ColumnLayouts ??= new(StringComparer.Ordinal);
        RecentContainers ??= [];
        Watchdog ??= new();
        Watchdog.Normalize();
        AccountReview ??= new();
        AccountReview.Normalize();
        UserMessages ??= new();
        UserMessages.Normalize();
        ConnectionProfiles.RemoveAll(p => p is null || string.IsNullOrWhiteSpace(p.Name));
        SavedViews.RemoveAll(v => v is null || string.IsNullOrWhiteSpace(v.Id));
        PinnedContainers.RemoveAll(p => p is null || string.IsNullOrWhiteSpace(p.DistinguishedName));
        RecentContainers.RemoveAll(string.IsNullOrWhiteSpace);
        if (RecentContainers.Count > MaxRecentContainers)
        {
            RecentContainers.RemoveRange(MaxRecentContainers, RecentContainers.Count - MaxRecentContainers);
        }

        PageSize = Math.Clamp(PageSize, 50, 5000);
        BatchMaxParallelism = Math.Clamp(BatchMaxParallelism, 1, 16);
        ConfirmationThreshold = Math.Clamp(ConfirmationThreshold, 1, 100_000);
        InactiveDays = Math.Clamp(InactiveDays, 1, 3650);
    }
}

/// <summary>A saved connection (never contains a password).</summary>
public sealed record ConnectionProfile
{
    public string Name { get; init; } = string.Empty;

    public string? Domain { get; init; }

    /// <summary>Specific DC or null/empty for automatic location.</summary>
    public string? Server { get; init; }

    public int? Port { get; init; }

    public AuthenticationMode Authentication { get; init; } = AuthenticationMode.CurrentUser;

    public TransportSecurity Security { get; init; } = TransportSecurity.SignAndSeal;

    /// <summary>User name for explicit credentials (DOMAIN\user or UPN). The password is never saved.</summary>
    public string? UserName { get; init; }
}

/// <summary>A container pinned to the "Views" section of the navigation tree.</summary>
public sealed record PinnedContainer(string DistinguishedName, string Name);

/// <summary>A visible column and its width (device independent pixels; 0 = automatic).</summary>
public sealed record ColumnLayoutEntry(string Attribute, double Width);
