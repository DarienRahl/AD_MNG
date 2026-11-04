using System.Globalization;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Comparison;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.Ldap;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.GroupPolicy.Settings;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy;

/// <summary>
/// One row of <see cref="GpoComparisonViewModel"/> (localized; masked values never shown). A settings row may stand for a
/// preference item or an administrative template policy made of several setting rows (<see cref="Row"/> is the first).
/// </summary>
public sealed record GpoComparisonRowViewModel(GpoComparisonRow Row, string SectionText, string Name, string ValueA, string ValueB, string StateText)
{
    /// <summary>Same, different, only in A, only in B (of every setting the row stands for).</summary>
    public GpoComparisonState State { get; init; } = Row.State;

    /// <summary>Where GPMC lists a setting ("Konfiguracja komputera › Zasady › …"); empty for the other sections.</summary>
    public string Area { get; init; } = string.Empty;

    /// <summary>The end of <see cref="Area"/> for its narrow column (<see cref="GpoSettingText.ShortPath"/>).</summary>
    public string ShortArea => GpoSettingText.ShortPath(Area);

    /// <summary>The key behind a setting (tool tip, CSV); empty for the other sections.</summary>
    public string Technical { get; init; } = string.Empty;

    /// <summary>The name's tool tip: the name and, for a setting, its technical key.</summary>
    public string NameToolTip => Technical.Length == 0 ? Name : Name + Environment.NewLine + Technical;

    public bool IsDifferent => State != GpoComparisonState.Same;
}

/// <summary>
/// "Porównaj" two GPOs of the window's domain (design note 5.6, <c>GpoComparisonWindow</c>): status, versions, WMI filter,
/// extension names, security filtering and delegation, links, the settings of both folders in the operator's language (K6:
/// <see cref="GpoSettingPresenter"/>, preference items and administrative template policies as one row each) and their files,
/// read on the session's DC into memory. "Tylko różnice" filters the rows; masked settings (passwords) show only that they
/// are set. CSV only when the operator chooses a file (rule 1).
/// </summary>
public sealed partial class GpoComparisonViewModel : WindowViewModelBase
{
    private readonly GroupPolicyServices _gp;
    private readonly SerialCancellation _load = new();
    private IReadOnlyList<GpoComparisonRowViewModel> _all = [];

    public GpoComparisonViewModel(GroupPolicyServices services, GpoInfo a, GpoInfo b)
        : base((services ?? throw new ArgumentNullException(nameof(services))).Ui)
    {
        _gp = services;
        A = a ?? throw new ArgumentNullException(nameof(a));
        B = b ?? throw new ArgumentNullException(nameof(b));
    }

    public GpoInfo A { get; }

    public GpoInfo B { get; }

    public override string WindowTitle => Localizer.Format(L.GroupPolicy.CompareWindowTitle, A.DisplayName, B.DisplayName);

    public string NameA => A.DisplayName;

    public string NameB => B.DisplayName;

    [ObservableProperty]
    public partial IReadOnlyList<GpoComparisonRowViewModel> Rows { get; private set; } = [];

    /// <summary>"Tylko różnice" (default on).</summary>
    [ObservableProperty]
    public partial bool OnlyDifferences { get; set; } = true;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string? Warning { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>"12 różnic w 140 pozycjach".</summary>
    [ObservableProperty]
    public partial string SummaryText { get; private set; } = string.Empty;

    public override Task InitializeAsync() => LoadAsync();

    partial void OnOnlyDifferencesChanged(bool value) => ApplyFilter();

    [RelayCommand]
    private async Task LoadAsync()
    {
        var token = _load.Next();
        if (Session is not { } session)
        {
            Error = NoSessionMessage;
            return;
        }

        IsLoading = true;
        Error = null;
        Warning = null;
        try
        {
            var reader = _gp.Reader;
            var gpoA = await reader.ReadPolicyAsync(session, A.Id, token).ConfigureAwait(true) ?? A;
            var gpoB = await reader.ReadPolicyAsync(session, B.Id, token).ConfigureAwait(true) ?? B;
            var linksA = await reader.ReadLinksOfAsync(session, A.Id, token).ConfigureAwait(true);
            var linksB = await reader.ReadLinksOfAsync(session, B.Id, token).ConfigureAwait(true);
            var filters = await reader.ReadWmiFiltersAsync(session, token).ConfigureAwait(true);
            GpoFolderContent? contentA = null;
            GpoFolderContent? contentB = null;
            var access = await _gp.OpenSysvolAsync(session, token).ConfigureAwait(true);
            if (access.Connection is { } sysvol)
            {
                await using (sysvol.ConfigureAwait(true))
                {
                    try
                    {
                        gpoA = await reader.WithSysvolVersionAsync(sysvol, gpoA, token).ConfigureAwait(true);
                        gpoB = await reader.WithSysvolVersionAsync(sysvol, gpoB, token).ConfigureAwait(true);
                        contentA = await GroupPolicyReader.ReadFolderAsync(sysvol, A.Id, token).ConfigureAwait(true);
                        contentB = await GroupPolicyReader.ReadFolderAsync(sysvol, B.Id, token).ConfigureAwait(true);
                    }
                    catch (Exception ex) when (ex is GpoTooLargeException or SysvolWriteException)
                    {
                        contentA = contentB = null;
                        Warning = GroupPolicyText.Error(Localizer, Errors, ex) + " " + Localizer[L.GroupPolicy.CompareWithoutContent];
                    }
                }
            }
            else
            {
                Warning = _gp.SysvolText(access.Reason ?? SysvolUnavailableReason.Failed, session.Info.Server) + " " + Localizer[L.GroupPolicy.CompareWithoutContent];
            }

            var rows = await Task.Run(
                () => GroupPolicyComparer.Compare(new GpoComparisonSide(gpoA, contentA, linksA), new GpoComparisonSide(gpoB, contentB, linksB)), token)
                .ConfigureAwait(true);

            // One directory search for every name: the trustees of both GPOs and the principals their settings name.
            var settingSids = GpoSettingPresenter.Sids(rows.SelectMany(r => new[] { r.A, r.B }).OfType<GpoSetting>());
            var sids = GpoPermissionLevels.Read(gpoA.SecurityDescriptor).Concat(GpoPermissionLevels.Read(gpoB.SecurityDescriptor)).Select(p => p.Sid).Concat(settingSids);
            var names = await TrusteeNames.ResolveAsync(session, sids, Localizer, token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return;
            }

            var filterNames = filters.ToDictionary(f => f.Id, f => f.Name, StringComparer.OrdinalIgnoreCase);
            var linkMap = linksA.Concat(linksB).GroupBy(l => l.ContainerDn, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().ContainerName, StringComparer.OrdinalIgnoreCase);
            var other = rows.Where(r => r.Section != GpoComparisonSection.Settings).Select(r => ToRow(r, names, filterNames, linkMap, linksA, linksB)).ToList();
            var (presenterA, presenterB) = Presenters(Localizer, names, rows, contentA, contentB, null);
            ShowRows(other, SettingRows(rows, presenterA, presenterB));

            // The rows are shown with registry names; the administrative templates then name the policies.
            IsLoading = false;
            if (await DefinitionsAsync(session, rows, token).ConfigureAwait(true) is { } definitions && !token.IsCancellationRequested)
            {
                var localizer = Localizer;
                var named = await Task.Run(
                    () =>
                    {
                        var (a, b) = Presenters(localizer, names, rows, contentA, contentB, definitions);
                        return SettingRows(rows, a, b);
                    },
                    token).ConfigureAwait(true);
                if (!token.IsCancellationRequested)
                {
                    ShowRows(other, named);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Reloaded or closed.
        }
        catch (Exception ex) when (GroupPolicyText.IsExpected(ex))
        {
            Error = GroupPolicyText.Error(Localizer, Errors, ex);
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>Exports the shown rows to a CSV file the operator chooses (never automatically).</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        var path = await Services.Files.SaveFileAsync(Localizer[L.GroupPolicy.CompareExport], "gpo-comparison.csv",
            [new FileFilter(Localizer[L.Files.CsvFilter], "*.csv")]).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        var separator = CsvFiles.ListSeparator(Services.Regional.Culture).ToString();
        var rows = Rows;
        try
        {
            await CsvFiles.WriteAsync(path, writer =>
            {
                writer.WriteLine(string.Join(separator, new[]
                {
                    Localizer[L.GroupPolicy.ColumnSection], Localizer[L.GroupPolicyEditing.ColumnArea], Localizer[L.GroupPolicy.ColumnSetting], NameA, NameB,
                    Localizer[L.GroupPolicy.ColumnState], Localizer[L.GpoSettings.ColumnTechnical],
                }.Select(v => Csv(v, separator))));
                foreach (var row in rows)
                {
                    writer.WriteLine(string.Join(separator, new[] { row.SectionText, row.Area, row.Name, row.ValueA, row.ValueB, row.StateText, row.Technical }
                        .Select(v => Csv(v, separator))));
                }
            }).ConfigureAwait(true);
            Services.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.GroupPolicy.CompareExported, rows.Count));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = Localizer[L.GroupPolicy.FolderFailed];
        }
    }

    [RelayCommand]
    private void Close() => RequestClose(null);

    protected override void OnSessionEnded() => Error = SessionEndedMessage;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _load.Cancel();
        }

        base.Dispose(disposing);
    }

    /// <summary>The rows in the comparer's section order: core, permissions, links, settings, files.</summary>
    private void ShowRows(IReadOnlyList<GpoComparisonRowViewModel> other, IReadOnlyList<GpoComparisonRowViewModel> settings)
    {
        _all = [.. other.Where(r => r.Row.Section < GpoComparisonSection.Settings), .. settings, .. other.Where(r => r.Row.Section > GpoComparisonSection.Settings)];
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        Rows = OnlyDifferences ? [.. _all.Where(r => r.IsDifferent)] : _all;
        SummaryText = Localizer.Format(L.GroupPolicy.CompareSummary, _all.Count(r => r.IsDifferent), _all.Count);
    }

    private GpoComparisonRowViewModel ToRow(
        GpoComparisonRow row,
        IReadOnlyDictionary<string, string> names,
        IReadOnlyDictionary<string, string> filters,
        IReadOnlyDictionary<string, string> containers,
        IReadOnlyList<GpoLinkInfo> linksA,
        IReadOnlyList<GpoLinkInfo> linksB)
    {
        string name;
        string a;
        string b;
        switch (row.Section)
        {
            case GpoComparisonSection.Core:
                name = GroupPolicyText.CoreKey(Localizer, row.Key);
                a = CoreValue(row.Key, row.ValueA, filters);
                b = CoreValue(row.Key, row.ValueB, filters);
                break;
            case GpoComparisonSection.Permissions:
                name = TrusteeNames.NameOf(names, row.Key);
                a = Level(row.ValueA);
                b = Level(row.ValueB);
                break;
            case GpoComparisonSection.Links:
                name = containers.TryGetValue(row.Key, out var container) ? container + " (" + AddressResolver.ToDisplayPath(row.Key) + ")" : row.Key;
                a = LinkText(linksA.FirstOrDefault(l => DistinguishedName.AreEqual(l.ContainerDn, row.Key)));
                b = LinkText(linksB.FirstOrDefault(l => DistinguishedName.AreEqual(l.ContainerDn, row.Key)));
                break;
            default:
                name = row.Key;
                a = row.ValueA ?? Localizer[L.GroupPolicy.Absent];
                b = row.ValueB ?? Localizer[L.GroupPolicy.Absent];
                break;
        }

        return new GpoComparisonRowViewModel(row, GroupPolicyText.Section(Localizer, row.Section), name, a, b, GroupPolicyText.State(Localizer, row.State));
    }

    private string CoreValue(string key, string? value, IReadOnlyDictionary<string, string> filters)
    {
        if (value is null)
        {
            return Localizer[L.GroupPolicy.Absent];
        }

        return key switch
        {
            GroupPolicyComparer.StatusKey when Enum.TryParse<GpoStatus>(value, out var status) => GroupPolicyText.Status(Localizer, status),
            GroupPolicyComparer.WmiFilterKey => filters.TryGetValue(value, out var filter) ? filter : value,
            _ => value,
        };
    }

    private string Level(string? value) => value is null
        ? Localizer[L.GroupPolicy.Absent]
        : Enum.TryParse<GpoPermissionLevel>(value, out var level) ? GroupPolicyText.Level(Localizer, level) : value;

    private string LinkText(GpoLinkInfo? link) => link is null
        ? Localizer[L.GroupPolicy.Absent]
        : Localizer.Format(L.GroupPolicy.LinkSummary, link.Order.ToString(CultureInfo.CurrentCulture),
            GroupPolicyText.YesNo(Localizer, link.Enforced), GroupPolicyText.YesNo(Localizer, link.Enabled));

    /// <summary>A CSV field: quoted when needed; text a spreadsheet would run as a formula (=, +, -, @) is prefixed with an apostrophe.</summary>
    private static string Csv(string value, string separator)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.Contains(separator, StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal)
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }
}
