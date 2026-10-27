using System.Collections.ObjectModel;
using System.Globalization;
using AdMng.Core.Computers;
using AdMng.Core.Computers.Remote;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Computers;

/// <summary>An operator's end-of-support date in the "Komputery" tab of the settings.</summary>
/// <param name="Build">The build number.</param>
/// <param name="Edition">The edition class.</param>
/// <param name="EndOfSupport">The date the operator set.</param>
/// <param name="Text">"19045 – Home/Pro – 13.10.2026".</param>
public sealed record ComputerReleaseOverrideRow(int Build, EditionClass Edition, DateOnly EndOfSupport, string Text);

/// <summary>
/// The "Komputery" tab of the settings window (K1 design note 8): WinRM (timeout, HTTPS, port, refreshing the users'
/// policy), the restart defaults (delay, message), the placement rules (allowed containers of workstations and servers,
/// portable DNs with <c>{domain}</c>, one per line) and the catalogue overrides (build, edition class, date). Never computer
/// names or results.
/// </summary>
public sealed partial class ComputerSettingsViewModel : ObservableObject
{
    private readonly DialogServices _services;

    public ComputerSettingsViewModel(DialogServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        Editions = [.. Enum.GetValues<EditionClass>().Select(e => new Option<EditionClass>(e, Localizer[EditionKey(e)]))];
        var current = services.Settings.Current.Computers;
        RemoteTimeoutSeconds = current.RemoteTimeoutSeconds;
        RemoteUseHttps = current.RemoteUseHttps;
        RemotePortText = current.RemotePort?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        RefreshUserPolicy = current.RefreshUserPolicy;
        RestartDelayMinutes = current.RestartDelayMinutes;
        RestartMessage = current.RestartMessage ?? string.Empty;
        EndingSoonDays = current.Parameters.EndingSoonDays;
        WorkstationContainers = Containers(current, ComputerType.Workstation);
        ServerContainers = Containers(current, ComputerType.Server);
        foreach (var o in current.ReleaseOverrides.OrderBy(o => o.Build).ThenBy(o => o.Edition))
        {
            Overrides.Add(Row(o.Build, o.Edition, o.EndOfSupport));
        }

        NewEdition = Editions[0];
        Overrides.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasOverrides));
    }

    private ILocalizer Localizer => _services.Localizer;

    public IReadOnlyList<Option<EditionClass>> Editions { get; }

    // ---- WinRM ---------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial int RemoteTimeoutSeconds { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PortHint))]
    public partial bool RemoteUseHttps { get; set; }

    /// <summary>The WinRM port; empty = 5985 (HTTP) or 5986 (HTTPS).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial string RemotePortText { get; set; }

    /// <summary>"Puste: 5985 (HTTP) / 5986 (HTTPS)".</summary>
    public string PortHint => Localizer.Format(L.Computers.SettingsPortHint, RemoteUseHttps ? 5986 : 5985);

    [ObservableProperty]
    public partial bool RefreshUserPolicy { get; set; }

    // ---- Restart -------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial int RestartDelayMinutes { get; set; }

    /// <summary>The default restart message; empty = the built-in text of the UI language.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial string RestartMessage { get; set; }

    /// <summary>"Koniec wsparcia w ciągu N dni" (also changed in the review window).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial int EndingSoonDays { get; set; }

    // ---- Placement rules -----------------------------------------------------------------------------------------

    /// <summary>Allowed containers of workstations, one portable DN per line (empty: not checked).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial string WorkstationContainers { get; set; }

    /// <summary>Allowed containers of member servers, one portable DN per line (empty: not checked).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial string ServerContainers { get; set; }

    // ---- Catalogue overrides -------------------------------------------------------------------------------------

    public ObservableCollection<ComputerReleaseOverrideRow> Overrides { get; } = [];

    public bool HasOverrides => Overrides.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddOverrideCommand))]
    [NotifyPropertyChangedFor(nameof(OverrideError))]
    public partial string NewBuild { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddOverrideCommand))]
    public partial Option<EditionClass> NewEdition { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddOverrideCommand))]
    [NotifyPropertyChangedFor(nameof(OverrideError))]
    public partial DateTime? NewEndOfSupport { get; set; }

    /// <summary>Why the typed override cannot be added (only once something was typed).</summary>
    public string? OverrideError =>
        NewBuild.Length > 0 && ParseBuild(NewBuild) is null ? Localizer[L.Computers.SettingsBuildInvalid]
        : Overrides.Count >= ComputerSettings.MaxOverrides ? Localizer.Format(L.Computers.SettingsTooManyOverrides, ComputerSettings.MaxOverrides)
        : null;

    /// <summary>Localized error of the tab, or null.</summary>
    public string? Error =>
        Range(L.Computers.SettingsTimeout, RemoteTimeoutSeconds, RemoteOptions.MinTimeoutSeconds, RemoteOptions.MaxTimeoutSeconds)
        ?? (ParsePort(RemotePortText, out _) ? null : Localizer.Format(L.Settings.OutOfRange, Localizer[L.Computers.SettingsPort], 1, 65535))
        ?? Range(L.Computers.SettingsRestartDelay, RestartDelayMinutes, 0, ComputerSettings.MaxRestartDelayMinutes)
        ?? ((RestartMessage ?? string.Empty).Length > ComputerSettings.MaxRestartMessageLength
            ? Localizer.Format(L.RemoteActions.RestartMessageTooLong, ComputerSettings.MaxRestartMessageLength) : null)
        ?? Range(L.Computers.SettingsEndingSoonDays, EndingSoonDays, 0, ComputerReportParameters.MaxDays)
        ?? InvalidContainer(L.Computers.SettingsWorkstationContainers, WorkstationContainers)
        ?? InvalidContainer(L.Computers.SettingsServerContainers, ServerContainers);

    [RelayCommand(CanExecute = nameof(CanAddOverride))]
    private void AddOverride()
    {
        if (ParseBuild(NewBuild) is not { } build || NewEndOfSupport is not { } date)
        {
            return;
        }

        var edition = NewEdition.Value;
        var existing = Overrides.FirstOrDefault(o => o.Build == build && o.Edition == edition);
        if (existing is not null)
        {
            Overrides.Remove(existing);
        }

        var row = Row(build, edition, DateOnly.FromDateTime(date));
        var index = Overrides.TakeWhile(o => o.Build < build || (o.Build == build && o.Edition < edition)).Count();
        Overrides.Insert(index, row);
        NewBuild = string.Empty;
        NewEndOfSupport = null;
    }

    private bool CanAddOverride() => ParseBuild(NewBuild) is not null && NewEndOfSupport is not null && Overrides.Count < ComputerSettings.MaxOverrides;

    [RelayCommand]
    private void RemoveOverride(ComputerReleaseOverrideRow? row)
    {
        if (row is not null)
        {
            Overrides.Remove(row);
            AddOverrideCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(OverrideError));
        }
    }

    /// <summary>Writes the tab into <paramref name="settings"/> (inside the settings store's update).</summary>
    public void Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var computers = settings.Computers;
        computers.RemoteTimeoutSeconds = RemoteTimeoutSeconds;
        computers.RemoteUseHttps = RemoteUseHttps;
        computers.RemotePort = ParsePort(RemotePortText, out var port) ? port : null;
        computers.RefreshUserPolicy = RefreshUserPolicy;
        computers.RestartDelayMinutes = RestartDelayMinutes;
        computers.RestartMessage = string.IsNullOrWhiteSpace(RestartMessage) ? null : RestartMessage;
        computers.Parameters = computers.Parameters with { EndingSoonDays = EndingSoonDays };
        computers.PlacementRules =
        [
            .. new[] { (ComputerType.Workstation, WorkstationContainers), (ComputerType.Server, ServerContainers) }
                .Select(r => new ComputerPlacementSetting { Type = r.Item1, Containers = [.. Lines(r.Item2)] })
                .Where(r => r.Containers.Count > 0),
            .. computers.PlacementRules.Where(r => r.Type is not (ComputerType.Workstation or ComputerType.Server)),
        ];
        computers.ReleaseOverrides = [.. Overrides.Select(o => new ComputerReleaseOverrideSetting { Build = o.Build, Edition = o.Edition, EndOfSupport = o.EndOfSupport })];
        computers.Normalize();
    }

    /// <summary>The resource key of an edition class (<c>WindowsRelease.Edition.{EditionClass}</c>).</summary>
    internal static string EditionKey(EditionClass edition) => $"WindowsRelease.Edition.{edition}";

    private ComputerReleaseOverrideRow Row(int build, EditionClass edition, DateOnly date) =>
        new(build, edition, date, string.Create(Localizer.Culture, $"{build} – {Localizer[EditionKey(edition)]} – {date.ToString("d", Localizer.Culture)}"));

    private static string Containers(ComputerSettings settings, ComputerType type) =>
        string.Join(Environment.NewLine, settings.PlacementRules.FirstOrDefault(r => r.Type == type)?.Containers ?? []);

    private static IEnumerable<string> Lines(string? text) =>
        (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase);

    private string? InvalidContainer(string labelKey, string? text)
    {
        // The rules are portable: {domain} stands for the naming context of whichever domain the window is connected to.
        var invalid = Lines(text).FirstOrDefault(line => PlacementRules.Resolve(line, "DC=example,DC=test") is null);
        return invalid is null ? null : Localizer.Format(L.Computers.SettingsContainerInvalid, Localizer[labelKey], invalid);
    }

    private static int? ParseBuild(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var build) && build is > 0 and < 1_000_000 ? build : null;

    private static bool ParsePort(string? text, out int? port)
    {
        port = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is > 0 and <= 65535)
        {
            port = value;
            return true;
        }

        return false;
    }

    private string? Range(string labelKey, int value, int min, int max) =>
        value < min || value > max ? Localizer.Format(L.Settings.OutOfRange, Localizer[labelKey], min, max) : null;
}
