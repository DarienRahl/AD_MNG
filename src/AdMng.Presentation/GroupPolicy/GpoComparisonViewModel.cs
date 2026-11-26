using System.Globalization;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Comparison;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.Ldap;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Feedback;
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
/// <remarks>
/// The window has one status line (<see cref="Status"/>; track V, stage V2b-3, design note docs/dev/v-interface-renewal.md
/// "V2b-3 as built"). The comparison is its operation, with Stop: reading, then - once the rows are shown - the
/// administrative templates that name the policies. At rest the line says how many differences the rows hold (the
/// summary). A comparison that was stopped before it showed anything or failed is the state of the window until it is
/// repeated (Retry); one that was stopped while the templates were read is a result: the rows are there, under the
/// registry names of the settings. What the comparison could not compare - the settings and files, when SYSVOL cannot
/// be read - is a note above the rows, true for as long as they are shown. As the state that lasts: the session of the
/// window ended, or it was opened without one.
/// </remarks>
public sealed partial class GpoComparisonViewModel : WindowViewModelBase, IHasPageStatus
{
    private readonly GroupPolicyServices _gp;
    private readonly SerialCancellation _load = new();
    private IReadOnlyList<GpoComparisonRowViewModel> _all = [];

    /// <summary>The comparison was shown at least once: the line has its count of differences as the summary.</summary>
    private bool _compared;

    /// <summary>The comparison that is running (the one that owns <see cref="IsLoading"/>).</summary>
    private OperationScope? _comparing;

    /// <summary>What the last comparison left, when it left no rows: it was stopped or failed (null: nothing wrong).</summary>
    private PageStatus? _compareState;

    public GpoComparisonViewModel(GroupPolicyServices services, GpoInfo a, GpoInfo b)
        : base((services ?? throw new ArgumentNullException(nameof(services))).Ui)
    {
        _gp = services;
        A = a ?? throw new ArgumentNullException(nameof(a));
        B = b ?? throw new ArgumentNullException(nameof(b));
        Status = new PageStatusHolder(Localizer, Services.Dispatcher, Services.Lists.StatusLog);
        UpdateLasting();
    }

    /// <summary>
    /// The status of the window - what its one status line shows: the running comparison (with Stop), at rest how many
    /// differences the rows hold, why there are no rows after a comparison that was stopped or failed, and that the
    /// session of the window ended.
    /// </summary>
    public PageStatusHolder Status { get; }

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

    /// <summary>What could not be compared (SYSVOL) - a note above the rows: they are complete as far as it says.</summary>
    [ObservableProperty]
    public partial string? Warning { get; private set; }

    /// <summary>Why the comparison failed, or the last export did, as plain text: what the status line says (kept for callers that read it).</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>"12 różnic w 140 pozycjach": the summary of the status line (kept for callers that read it).</summary>
    [ObservableProperty]
    public partial string SummaryText { get; private set; } = string.Empty;

    public override Task InitializeAsync() => LoadAsync();

    partial void OnOnlyDifferencesChanged(bool value) => ApplyFilter();

    /// <summary>
    /// Reads both GPOs and compares them - the operation of the window: its line says "Comparing … with …" with Stop. The
    /// rows are the result, so a comparison that ended says nothing and the line shows the count of differences; one that
    /// was stopped before it showed anything, or failed, is the state of the window until it is repeated. Closing the
    /// window and the end of its session end the comparison without a word. Never throws.
    /// </summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        var latest = _load.Next();
        if (Session is not { } session)
        {
            Error = NoSessionMessage;
            UpdateLasting();
            return;
        }

        IsLoading = true;
        Error = null;
        using var comparing = Status.Begin(
            Localizer.Format(L.GpoComparisonStatus.Comparing, A.DisplayName, B.DisplayName), new PageStatusLog(L.GpoComparisonStatus.Comparing));
        _comparing = comparing;

        // What the last comparison left is over: this one decides.
        _compareState = null;
        UpdateLasting();

        // The operator's Stop and "the latest wins" (Refresh, closing the window) both end the comparison.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(latest, comparing.Token);
        var token = linked.Token;
        var shown = false;

        // What this comparison could not compare: the note of its rows once they are shown. Until then the rows of the
        // comparison before stay - after a Stop or a failure too - and so does their note.
        string? warning = null;
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
                        warning = GroupPolicyText.Error(Localizer, Errors, ex) + " " + Localizer[L.GroupPolicy.CompareWithoutContent];
                    }
                }
            }
            else
            {
                warning = _gp.SysvolText(access.Reason ?? SysvolUnavailableReason.Failed, session.Info.Server) + " " + Localizer[L.GroupPolicy.CompareWithoutContent];
            }

            var rows = await Task.Run(
                () => GroupPolicyComparer.Compare(new GpoComparisonSide(gpoA, contentA, linksA), new GpoComparisonSide(gpoB, contentB, linksB)), token)
                .ConfigureAwait(true);

            // One directory search for every name: the trustees of both GPOs and the principals their settings name.
            var settingSids = GpoSettingPresenter.Sids(rows.SelectMany(r => new[] { r.A, r.B }).OfType<GpoSetting>());
            var sids = GpoPermissionLevels.Read(gpoA.SecurityDescriptor).Concat(GpoPermissionLevels.Read(gpoB.SecurityDescriptor)).Select(p => p.Sid).Concat(settingSids);
            var names = await TrusteeNames.ResolveAsync(session, sids, Localizer, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();

            var filterNames = filters.ToDictionary(f => f.Id, f => f.Name, StringComparer.OrdinalIgnoreCase);
            var linkMap = linksA.Concat(linksB).GroupBy(l => l.ContainerDn, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().ContainerName, StringComparer.OrdinalIgnoreCase);
            var other = rows.Where(r => r.Section != GpoComparisonSection.Settings).Select(r => ToRow(r, names, filterNames, linkMap, linksA, linksB)).ToList();
            var (presenterA, presenterB) = Presenters(Localizer, names, rows, contentA, contentB, null);
            Warning = warning;
            ShowRows(other, SettingRows(rows, presenterA, presenterB));
            shown = true;

            // The rows are shown with registry names; the administrative templates then name the policies.
            IsLoading = false;
            if (await DefinitionsAsync(session, rows, comparing, token).ConfigureAwait(true) is { } definitions && !token.IsCancellationRequested)
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

            if (comparing.IsCancelling)
            {
                // The templates were not read although the rows are there, and Stop was pressed: the rows say so.
                TemplatesStopped(comparing);
            }
        }
        catch (OperationCanceledException)
        {
            // Reloaded or closed - or the operator's Stop.
            if (comparing.IsCancelling)
            {
                if (shown)
                {
                    TemplatesStopped(comparing);
                }
                else
                {
                    // Nothing is shown (the first comparison), or the rows of the comparison before this one are.
                    var refresh = Localizer[L.Commands.Refresh];
                    var key = _compared ? L.GpoComparisonStatus.StoppedStale : L.GpoComparisonStatus.Stopped;
                    _compareState = PageStatus.Attention(
                        Localizer.Format(key, refresh), new PageStatusLog(key), new PageStatusAction(refresh, LoadCommand));
                }
            }
        }
        catch (Exception ex) when (GroupPolicyText.IsExpected(ex))
        {
            if (!comparing.IsEnded)
            {
                Error = GroupPolicyText.Error(Localizer, Errors, ex);
                var code = ResultCode.Of(ex);
                _compareState = PageStatus.Error(
                    PageStatusText.Failure(
                        Localizer.Format(L.GpoComparisonStatus.CompareFailed, A.DisplayName, B.DisplayName), Error, Localizer[L.Feedback.RetryNext], code),
                    new PageStatusLog(L.GpoComparisonStatus.CompareFailed).WithCode(code),
                    new PageStatusAction(Localizer[L.Common.Retry], LoadCommand),
                    canClose: false);
            }
        }
        finally
        {
            if (ReferenceEquals(_comparing, comparing))
            {
                _comparing = null;
                IsLoading = false;
            }

            UpdateLasting();
        }
    }

    /// <summary>
    /// The operator stopped the comparison while the administrative templates were read: the rows are all there, the
    /// policies under their registry names; "Refresh" compares again. A result that can be closed.
    /// </summary>
    private void TemplatesStopped(OperationScope comparing)
    {
        var refresh = Localizer[L.Commands.Refresh];
        comparing.CompleteAttention(
            Localizer.Format(L.GpoComparisonStatus.TemplatesStopped, refresh),
            new PageStatusLog(L.GpoComparisonStatus.TemplatesStopped),
            new PageStatusAction(refresh, LoadCommand));
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
            // The answer to the press: nothing was exported, and why - in the line, or as a toast while the comparison runs
            // (the rows are there while the templates are read): it keeps the line and its Stop.
            Error = Localizer[L.GroupPolicy.FolderFailed];
            var code = ResultCode.Of(ex);
            var text = PageStatusText.Failure(Localizer[L.GpoComparisonStatus.ExportFailed], Error, null, code);
            if (Status.IsWorking)
            {
                Services.Notifications.Show(NotificationSeverity.Error, text);
                return;
            }

            Status.Show(PageStatus.Error(text, new PageStatusLog(L.GpoComparisonStatus.ExportFailed).WithCode(code)));
        }
    }

    [RelayCommand]
    private Task CloseAsync() => CloseByOperatorAsync(null);

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();

        // The window's own sentences in the new language (why a comparison was stopped or failed stays as it was said).
        UpdateLasting();
        UpdateSummary();
    }

    protected override void OnSessionEnded()
    {
        // The line says for good that the session ended; a comparison that still runs ends without a word of its own
        // (the state first, so the line goes straight from "working" to it), and "busy" ends with it at once.
        Error = SessionEndedMessage;
        UpdateLasting();
        _load.Cancel();
        Status.Operation?.Dispose();
        _comparing = null;
        IsLoading = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _load.Cancel();

            // What still runs says nothing any more.
            Status.Operation?.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// The state of the window that lasts, the most important first: its session ended, or it was opened without one (an
    /// error that cannot be closed - nothing can be compared), else why there are no rows after a comparison that did not end.
    /// </summary>
    private void UpdateLasting() => Status.SetLasting(
        SessionEndedStatus ?? (Session is null ? PageStatus.Error(NoSessionMessage, new PageStatusLog(L.Errors.NotConnected), canClose: false) : _compareState));

    /// <summary>The summary: how many differences the rows hold (nothing before they were shown once).</summary>
    private void UpdateSummary()
    {
        SummaryText = _compared ? Localizer.Format(L.GroupPolicy.CompareSummary, _all.Count(r => r.IsDifferent), _all.Count) : string.Empty;
        Status.SetIdle(_compared ? SummaryText : null);
    }

    /// <summary>The rows in the comparer's section order: core, permissions, links, settings, files.</summary>
    private void ShowRows(IReadOnlyList<GpoComparisonRowViewModel> other, IReadOnlyList<GpoComparisonRowViewModel> settings)
    {
        _all = [.. other.Where(r => r.Row.Section < GpoComparisonSection.Settings), .. settings, .. other.Where(r => r.Row.Section > GpoComparisonSection.Settings)];
        _compared = true;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        Rows = OnlyDifferences ? [.. _all.Where(r => r.IsDifferent)] : _all;
        UpdateSummary();
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
