using System.Collections.ObjectModel;
using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Pki;
using AdMng.Core.PrivilegedAccess;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AdMng.Presentation.Services;

namespace AdMng.Presentation.Pki;

/// <summary>
/// <i>Szablony certyfikatów</i> (P1, docs/dev/p-certificate-services.md 2.5): every template of the window's snapshot with the
/// README columns (names, schema, version, purposes, validity and renewal, key, subject source, manager approval, signatures,
/// publishing in AD, superseded templates, issuance policies), the CAs that issue it and the templates no CA issues; the
/// selected template's flags as sentences (technical bit name in the tooltip), and who can enroll in it and who can change it
/// – the trustee level at once (C3's <c>TrusteeLevel</c>), the account counts, the first accounts and the disabled ones when
/// the window's enrollment expansion (<c>GetExpansionAsync</c>, started after the trustee level is on screen) completes.
/// Accounts and groups open their property sheet; a broad trustee shows its count only. A compare pane puts two templates side
/// by side; the list is exported on demand to an operator-chosen file (CSV, HTML). Read-only (rule 5), in memory only (rule 1).
/// </summary>
public sealed partial class PkiTemplatesDocumentViewModel : PkiDocumentBase, ISearchableDocument, IPkiRequestTarget
{
    private const int NameChunk = 50;

    private List<PkiTemplateRowViewModel> _all = [];
    private PkiSnapshot? _snapshot;
    private PkiEnrollmentExpansion? _expansion;
    private IReadOnlyDictionary<string, Trustee> _trustees = new Dictionary<string, Trustee>(StringComparer.OrdinalIgnoreCase);
    private string _server = string.Empty;
    private PkiDocumentRequest? _pending;

    public PkiTemplatesDocumentViewModel(PkiServices pki, PkiDocumentRequest? request = null)
        : base(pki)
    {
        _pending = request;
        Pki.Snapshot.Changed += OnSnapshotChanged;
    }

    public event EventHandler? SearchFocusRequested;

    public override string Title => Localizer[L.PkiTemplates.Title];

    public override ObjectIcon Icon => ObjectIcon.Certificate;

    public override string StatusText => Error ?? (IsLoading ? (ProgressText.Length > 0 ? ProgressText : Localizer[L.Common.Loading]) : LoadedText);

    public override string SelectionText => SelectedRow?.DisplayName ?? string.Empty;

    /// <summary>"Odczyt obiektów PKI…" / "Liczenie kont: 500…" while the document loads.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string ProgressText { get; private set; } = string.Empty;

    // ---- List and filters -------------------------------------------------------------------------------------------------

    public ObservableCollection<PkiTemplateRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectionText))]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand), nameof(CopyDetailsCommand))]
    public partial PkiTemplateRowViewModel? SelectedRow { get; set; }

    public bool HasSelection => SelectedRow is not null;

    /// <summary>The rows selected in the list (two of them start the comparison with each other).</summary>
    public IReadOnlyList<PkiTemplateRowViewModel> SelectedRows { get; private set; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary><i>Tylko szablony niewystawiane przez żaden urząd</i>.</summary>
    [ObservableProperty]
    public partial bool UnpublishedOnly { get; set; }

    /// <summary>"Szablony: 26, niewystawiane przez żaden urząd: 1".</summary>
    [ObservableProperty]
    public partial string SummaryText { get; private set; } = string.Empty;

    /// <summary>"Pokazane szablony: 3 z 26" while a filter is active, else empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFiltered))]
    public partial string FilterText { get; private set; } = string.Empty;

    public bool IsFiltered => FilterText.Length > 0;

    /// <summary>The account pass: "Liczenie kont…", "Policzone konta: 1 234.", or why it failed.</summary>
    [ObservableProperty]
    public partial string CountStatusText { get; private set; } = string.Empty;

    /// <summary>The account pass runs (the counts say <i>liczenie…</i>).</summary>
    [ObservableProperty]
    public partial bool IsCounting { get; private set; }

    /// <summary>The account counts are shown (the expansion completed).</summary>
    public bool IsExpanded => _expansion is not null;

    public string ExportNote => Localizer[L.PkiTemplates.ExportNote];

    // ---- Details of the selected template ---------------------------------------------------------------------------------

    /// <summary>The selected template's general properties (OID, purposes, policies, providers …).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PkiDetailLine> Details { get; private set; } = [];

    /// <summary>The decoded flags of the selected template (one group per attribute).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PkiFlagGroupViewModel> FlagGroups { get; private set; } = [];

    /// <summary>Who can enroll (Enroll, AutoEnroll – FullControl implies both).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PkiTrusteeRowViewModel> EnrollTrustees { get; private set; } = [];

    /// <summary>Who can change the template (full control, ACL, owner, property writes).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PkiTrusteeRowViewModel> ModifyTrustees { get; private set; } = [];

    /// <summary>The account totals of the enroll rights (after the expansion).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PkiRightTotalViewModel> EnrollTotals { get; private set; } = [];

    /// <summary>The account totals of the modify rights (after the expansion; rights nobody holds are left out).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PkiRightTotalViewModel> ModifyTotals { get; private set; } = [];

    /// <summary>The selected template's rights were not evaluated (no descriptor): <i>nieoceniony</i>, never "safe".</summary>
    [ObservableProperty]
    public partial bool IsNotEvaluated { get; private set; }

    public string NotEvaluatedText => Localizer[L.PkiTemplates.NotEvaluated];

    public string BroadNote => Localizer[L.PkiTemplates.BroadNote];

    // ---- Requests, search -------------------------------------------------------------------------------------------------

    /// <summary>Selects a template by its cn (<i>Otwórz szablon</i>) – now when it is listed, else after the load.</summary>
    public void Apply(PkiDocumentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _pending = request;
        ApplyPending();
    }

    public void FocusSearch() => SearchFocusRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The view's list selection (two rows start the comparison of each other).</summary>
    public void SetSelection(IReadOnlyList<PkiTemplateRowViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        SelectedRows = rows;
        CompareCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Selects the template <paramref name="cn"/> (the filters are cleared when they hide it). False when there is none.</summary>
    public bool SelectTemplate(string cn)
    {
        var row = _all.FirstOrDefault(r => string.Equals(r.Cn, cn, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return false;
        }

        if (!Rows.Contains(row))
        {
            SearchText = string.Empty;
            UnpublishedOnly = false;
        }

        SelectedRow = row;
        return true;
    }

    partial void OnSearchTextChanged(string value) => ApplyFilters();

    partial void OnUnpublishedOnlyChanged(bool value) => ApplyFilters();

    partial void OnSelectedRowChanged(PkiTemplateRowViewModel? value) => ShowDetails(value);

    /// <summary>The trustees of the shown template say <i>liczenie…</i> while the accounts are counted, and stop saying it after a failed count.</summary>
    partial void OnIsCountingChanged(bool value) => ShowDetails(SelectedRow);

    // ---- Load -------------------------------------------------------------------------------------------------------------

    protected override async Task LoadCoreAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        try
        {
            ProgressText = Localizer.Format(L.Pki.ReadProgress, 0);
            var readProgress = Pki.Dispatcher.CreateProgress<PkiReadProgress>(p => ProgressText = Localizer.Format(L.Pki.ReadProgress, PkiDisplay.Count(Localizer, p.Objects)));
            var refresh = IsRefresh;
            var snapshot = await Pki.Snapshot.GetAsync(refresh, readProgress, cancellationToken).ConfigureAwait(true);
            var pageSize = Math.Clamp(Pki.Settings.Current.PageSize, 1, 5000);
            var overrides = Pki.Settings.Current.AccountReview.Privileges.OverrideMap();

            // The trustee level at once: the object types, the domain SIDs, the classifier without a graph (SID and RID rules),
            // the names of the trustees (one OR search per 50 SIDs on the window's DC).
            (IReadOnlyList<TemplateRights> level, IReadOnlyDictionary<string, Trustee> trustees) = await Task.Run(async () =>
            {
                var types = await AclObjectTypes.ReadAsync(session, pageSize, cancellationToken).ConfigureAwait(false);
                var domainSid = (await session.GetAsync(session.Info.DefaultNamingContext, [Attr.ObjectSid], cancellationToken).ConfigureAwait(false))?.ObjectSid;
                var rootSid = await RootDomainSidAsync(session, domainSid, cancellationToken).ConfigureAwait(false);
                var options = new PrivilegedAccessOptions { DomainSid = domainSid, RootDomainSid = rootSid, LevelOverrides = overrides, PageSize = pageSize };
                var classifier = new PrincipalClassifier(domainSid, rootSid, options, null, [.. snapshot.EnterpriseCas.Select(c => c.ComputerSid).OfType<string>()]);
                var rights = TemplateRightsEvaluator.TrusteeLevel(snapshot.Templates, types, classifier);
                var names = await ResolveTrusteesAsync(session, rights.SelectMany(r => r.Trustees).Select(t => t.Sid), cancellationToken).ConfigureAwait(false);
                return (rights, names);
            }, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            _server = session.Info.Server;
            _trustees = trustees;
            Show(snapshot, level);

            // Then the one account pass of the window (shared with the security review and the readiness report).
            if (!refresh && Pki.Snapshot.Expansion is { } cached && ReferenceEquals(Pki.Snapshot.Current, snapshot))
            {
                ApplyExpansion(cached);
                return;
            }

            await CountAsync(refresh, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            ProgressText = string.Empty;
        }
    }

    protected override void OnNotConnected()
    {
        _snapshot = null;
        _expansion = null;
        _all = [];
        Rows.Clear();
        SelectedRow = null;
        SummaryText = string.Empty;
        FilterText = string.Empty;
        CountStatusText = string.Empty;
        LoadedText = string.Empty;
        CloseCompare();
        RefreshExportCommands();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Pki.Snapshot.Changed -= OnSnapshotChanged;
        }
    }

    /// <summary>
    /// The window's cache replaced the snapshot (F5 in another PKI document: this document shows the new one from the cache) or
    /// the expansion (another document counted the accounts: the counts are shown).
    /// </summary>
    private void OnSnapshotChanged(object? sender, EventArgs e) => _ = Pki.Dispatcher.RunAsync(() =>
    {
        if (IsDisposed || IsLoading || _snapshot is null || Pki.Snapshot.Current is not { } current)
        {
            return;
        }

        if (!ReferenceEquals(current, _snapshot))
        {
            LoadAsync().Forget();
        }
        else if (Pki.Snapshot.Expansion is { } expansion && !ReferenceEquals(expansion, _expansion))
        {
            ApplyExpansion(expansion);
        }
    });

    protected override void RefreshCommands()
    {
        CompareCommand.NotifyCanExecuteChanged();
        CopyDetailsCommand.NotifyCanExecuteChanged();
        RefreshExportCommands();
    }

    /// <summary>The account pass; a failure leaves the trustee level on screen and says why the counts are missing.</summary>
    private async Task CountAsync(bool refresh, CancellationToken cancellationToken)
    {
        IsCounting = true;
        CountStatusText = Localizer[L.PkiTemplates.CountingStarted];
        var progress = Pki.Dispatcher.CreateProgress<PkiExpansionProgress>(p =>
        {
            CountStatusText = Localizer.Format(L.PkiTemplates.Counting, PkiDisplay.Count(Localizer, p.Accounts));
            ProgressText = CountStatusText;
        });
        try
        {
            var expansion = await Pki.Snapshot.GetExpansionAsync(refresh, progress, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            ApplyExpansion(expansion);
        }
        catch (Exception ex) when (IsExpected(ex) && !cancellationToken.IsCancellationRequested)
        {
            CountStatusText = Localizer.Format(L.PkiTemplates.CountFailed, Pki.Errors.Translate(ex));
            UpdateEnrollTexts();
        }
        finally
        {
            IsCounting = false;
        }
    }

    private void Show(PkiSnapshot snapshot, IReadOnlyList<TemplateRights> level)
    {
        var keep = SelectedRow?.Cn;
        _snapshot = snapshot;
        _expansion = null;
        var byCn = level.ToDictionary(r => r.TemplateCn, StringComparer.OrdinalIgnoreCase);
        var oidNames = snapshot.Oids.GroupBy(o => o.Oid, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().DisplayName ?? g.Key, StringComparer.Ordinal);
        _all = [.. snapshot.Templates.Select(t => Row(snapshot, t, oidNames, byCn.GetValueOrDefault(t.Cn)))];
        var unpublished = _all.Count(r => !r.IsPublished);
        SummaryText = Localizer.Format(L.PkiTemplates.Summary, _all.Count, unpublished);
        LoadedText = SummaryText;
        UpdateEnrollTexts();
        ApplyFilters();
        SelectedRow = (keep is null ? null : Rows.FirstOrDefault(r => string.Equals(r.Cn, keep, StringComparison.OrdinalIgnoreCase))) ?? Rows.FirstOrDefault();
        ShowDetails(SelectedRow);
        ApplyPending();
        RefreshCompare();
        RefreshExportCommands();
        OnPropertyChanged(nameof(IsExpanded));
    }

    /// <summary>The expanded rights replace the trustee level (counts, first accounts, the classifier with the graph).</summary>
    private void ApplyExpansion(PkiEnrollmentExpansion expansion)
    {
        _expansion = expansion;
        foreach (var row in _all)
        {
            if (expansion.Templates.TryGetValue(row.Cn, out var rights))
            {
                row.Rights = rights;
            }
        }

        CountStatusText = Localizer.Format(L.PkiTemplates.CountsDone, PkiDisplay.Count(Localizer, expansion.AccountsScanned));
        UpdateEnrollTexts();
        ShowDetails(SelectedRow);
        RefreshCompare();
        OnPropertyChanged(nameof(IsExpanded));
    }

    private PkiTemplateRowViewModel Row(PkiSnapshot snapshot, CertificateTemplate template, IReadOnlyDictionary<string, string> oidNames, TemplateRights? rights)
    {
        var cas = snapshot.EnterpriseCas
            .Where(ca => ca.PublishedTemplates.Contains(template.Cn, StringComparer.OrdinalIgnoreCase))
            .Select(ca => ca.DisplayName ?? ca.Name)
            .ToList();
        var key = template.MinimalKeySize is { } bits ? Localizer.Format(L.PkiTemplates.KeyBits, bits.ToString(Localizer.Culture)) : Localizer[L.Pki.None];
        if (template.CryptoProviders.Count > 0)
        {
            key = Localizer.Format(L.PkiTemplates.ValueList, key, template.CryptoProviders[0]);
        }

        var row = new PkiTemplateRowViewModel(template, cas)
        {
            VersionText = Localizer.Format(L.PkiTemplates.Version, template.Revision.ToString(CultureInfo.InvariantCulture), template.MinorRevision.ToString(CultureInfo.InvariantCulture)),
            PurposeText = PkiDisplay.Purposes(Localizer, template.EffectiveEku),
            ValidityText = PkiDisplay.Period(Localizer, template.Validity),
            RenewalText = PkiDisplay.Period(Localizer, template.RenewalOverlap),
            KeyText = key,
            SubjectText = Localizer[template.EnrolleeSuppliesSubject ? L.PkiTemplates.SubjectFromRequest : L.PkiTemplates.SubjectFromAd],
            ApprovalText = Localizer[template.RequiresManagerApproval ? L.Common.Yes : L.Common.No],
            PublishText = Localizer[(template.EnrollmentFlag & PublishToDs) != 0 ? L.Common.Yes : L.Common.No],
            SupersededText = PkiDisplay.List(Localizer, template.SupersededTemplates),
            IssuancePoliciesText = PkiDisplay.List(Localizer, template.IssuancePolicies.Select(oid => oidNames.GetValueOrDefault(oid) ?? oid)),
            CasText = cas.Count > 0 ? string.Join(", ", cas) : Localizer[L.PkiTemplates.NotIssued],
        };
        row.Rights = rights;
        return row;
    }

    /// <summary><c>CT_FLAG_PUBLISH_TO_DS</c> of <c>msPKI-Enrollment-Flag</c> (<i>Publikuj certyfikat w usłudze Active Directory</i>).</summary>
    private const uint PublishToDs = 0x8;

    /// <summary>The <i>Kto może się zarejestrować</i> column and the search keys of every row.</summary>
    private void UpdateEnrollTexts()
    {
        foreach (var row in _all)
        {
            var rights = row.Rights;
            if (rights is null || !rights.IsEvaluated)
            {
                row.EnrollText = Localizer[L.PkiTemplates.NotEvaluated];
                row.EnrollClass = null;
            }
            else if (rights.Totals.FirstOrDefault(t => t.Right == TemplateRight.Enroll) is { } total)
            {
                row.EnrollText = total.AccountCount + total.DisabledCount == 0 && total.HighestClass is null
                    ? Localizer[L.PkiTemplates.Nobody]
                    : AccountCountText(total.AccountCount, total.DisabledCount) + (total.HighestClass is { } c ? " · " + Localizer[PkiTextKeys.ClassKey(c)] : string.Empty);
                row.EnrollClass = total.HighestClass;
            }
            else
            {
                var enrollers = rights.Trustees.Where(t => t.Right == TemplateRight.Enroll).ToList();
                row.EnrollText = enrollers.Count == 0
                    ? Localizer[L.PkiTemplates.Nobody]
                    : string.Join(", ", enrollers.Select(t => TrusteeName(t)).Distinct(StringComparer.OrdinalIgnoreCase));
                row.EnrollClass = enrollers.Count == 0 ? null
                    : enrollers.Any(t => t.Class == PrincipalClass.Broad) ? PrincipalClass.Broad
                    : enrollers.Any(t => t.Class == PrincipalClass.Other) ? PrincipalClass.Other
                    : PrincipalClass.Tier0;
            }

            row.SearchKey = string.Join('\n', new[] { row.DisplayName, row.Cn, row.Template.Oid ?? string.Empty, row.PurposeText, row.CasText, row.IssuancePoliciesText }
                .Concat(rights?.Trustees.Select(t => TrusteeName(t)) ?? []));
        }
    }

    private void ApplyFilters()
    {
        var text = SearchText.Trim();
        var shown = _all.Where(r => (!UnpublishedOnly || !r.IsPublished)
            && (text.Length == 0 || r.SearchKey.Contains(text, StringComparison.CurrentCultureIgnoreCase))).ToList();
        var selected = SelectedRow;
        Rows.Clear();
        foreach (var row in shown)
        {
            Rows.Add(row);
        }

        FilterText = shown.Count == _all.Count ? string.Empty : Localizer.Format(L.PkiTemplates.Filtered, shown.Count, _all.Count);
        if (selected is not null && !shown.Contains(selected))
        {
            SelectedRow = shown.FirstOrDefault();
        }
        else if (selected is not null)
        {
            SelectedRow = selected;
        }

        RefreshExportCommands();
    }

    private void ApplyPending()
    {
        if (_pending is not { TemplateCn: { } cn } || _all.Count == 0)
        {
            return;
        }

        SelectTemplate(cn);
        _pending = null;
    }

    // ---- Details -------------------------------------------------------------------------------------------------------------

    private void ShowDetails(PkiTemplateRowViewModel? row)
    {
        if (row is null)
        {
            Details = [];
            FlagGroups = [];
            EnrollTrustees = [];
            ModifyTrustees = [];
            EnrollTotals = [];
            ModifyTotals = [];
            IsNotEvaluated = false;
            return;
        }

        var template = row.Template;
        var lines = new List<PkiDetailLine>
        {
            new(Localizer[L.PkiTemplates.ColumnCn], template.Cn),
            new(Localizer[L.PkiTemplates.Oid], template.Oid ?? Localizer[L.Pki.None]),
            new(Localizer[L.PkiTemplates.ColumnSchema], row.SchemaText),
            new(Localizer[L.PkiTemplates.ColumnVersion], row.VersionText),
            new(Localizer[L.PkiTemplates.ApplicationPolicies], PkiDisplay.List(Localizer, template.ApplicationPolicies.Select(PkiDisplay.Purpose))),
            new(Localizer[L.PkiTemplates.Ekus], PkiDisplay.List(Localizer, template.Ekus.Select(PkiDisplay.Purpose))),
            new(Localizer[L.PkiTemplates.ColumnValidity], row.ValidityText),
            new(Localizer[L.PkiTemplates.ColumnRenewal], row.RenewalText),
            new(Localizer[L.PkiTemplates.ColumnKey], row.KeyText),
            new(Localizer[L.PkiTemplates.CryptoProviders], PkiDisplay.List(Localizer, template.CryptoProviders)),
            new(Localizer[L.PkiTemplates.ColumnSubject], row.SubjectText),
            new(Localizer[L.PkiTemplates.ColumnApproval], row.ApprovalText),
            new(Localizer[L.PkiTemplates.ColumnSignatures], row.SignaturesText),
            new(Localizer[L.PkiTemplates.RaPolicies], PkiDisplay.List(Localizer, template.RaApplicationPolicies.Concat(template.RaIssuancePolicies).Select(PkiDisplay.Purpose))),
            new(Localizer[L.PkiTemplates.ColumnPublish], row.PublishText),
            new(Localizer[L.PkiTemplates.ColumnSuperseded], row.SupersededText),
            new(Localizer[L.PkiTemplates.ColumnIssuancePolicies], row.IssuancePoliciesText),
            new(Localizer[L.PkiTemplates.ColumnCas], row.CasText),
        };
        Details = lines;
        FlagGroups = Flags(template);

        var rights = row.Rights;
        IsNotEvaluated = rights is null || !rights.IsEvaluated;
        var trustees = rights?.Trustees ?? [];
        EnrollTrustees = [.. trustees.Where(t => IsEnrollRight(t.Right)).Select(TrusteeRow)];
        ModifyTrustees = [.. trustees.Where(t => !IsEnrollRight(t.Right)).Select(TrusteeRow)];
        var totals = rights?.Totals ?? [];
        EnrollTotals = [.. totals.Where(t => IsEnrollRight(t.Right)).Select(TotalRow)];
        ModifyTotals = [.. totals.Where(t => !IsEnrollRight(t.Right) && t.AccountCount + t.DisabledCount > 0).Select(TotalRow)];
    }

    private static bool IsEnrollRight(TemplateRight right) => right is TemplateRight.Enroll or TemplateRight.AutoEnroll;

    /// <summary>The flag sentences of the four attributes (technical name in the tooltip), the version fields and the unknown bits.</summary>
    private IReadOnlyList<PkiFlagGroupViewModel> Flags(CertificateTemplate template)
    {
        var groups = new List<PkiFlagGroupViewModel>();
        foreach (var attribute in Enum.GetValues<PkiFlagAttribute>())
        {
            var decoded = template.DecodeFlags(attribute);
            var items = new List<PkiFlagItemViewModel>();
            foreach (var entry in decoded.Set)
            {
                var text = Localizer[L.PkiTemplates.FlagKey(attribute, entry.Name)];
                items.Add(new PkiFlagItemViewModel(entry.IsReserved ? Localizer.Format(L.PkiTemplates.ReservedFlag, text) : text, $"{entry.Name} ({PkiFlags.BitName(entry.Mask)})"));
            }

            foreach (var field in decoded.Fields.Where(f => f.Value != 0 || template.SchemaVersion >= 2))
            {
                items.Add(new PkiFlagItemViewModel(Localizer.Format(L.PkiTemplates.FieldKey(field.Field.Name), field.Value), $"{field.Field.Name} ({PkiFlags.BitName(field.Field.Mask)})"));
            }

            if (decoded.UnknownBitNames.Count > 0)
            {
                var bits = string.Join(", ", decoded.UnknownBitNames);
                items.Add(new PkiFlagItemViewModel(Localizer.Format(L.PkiTemplates.UnknownBits, bits), bits, isUnknown: true));
            }

            if (items.Count == 0)
            {
                items.Add(new PkiFlagItemViewModel(Localizer[L.PkiTemplates.NoFlags], PkiFlags.BitName(0)));
            }

            groups.Add(new PkiFlagGroupViewModel(Localizer[L.PkiTemplates.FlagGroupKey(attribute)], items));
        }

        return groups;
    }

    private PkiTrusteeRowViewModel TrusteeRow(TemplateTrustee trustee)
    {
        var right = Localizer[PkiTextKeys.RightKey(trustee.Right)];
        if (trustee.Attribute is { Length: > 0 } attribute)
        {
            right = Localizer.Format(L.PkiTemplates.RightOnAttribute, right, attribute);
        }

        var known = _trustees.GetValueOrDefault(trustee.Sid);
        var node = _expansion?.Groups.FindBySid(trustee.Sid);
        var dn = known?.Dn ?? node?.Dn;
        var isGroup = known?.IsGroup ?? node is not null;
        string count;
        if (!trustee.IsResolved)
        {
            count = Localizer[L.PkiTemplates.CountUnknown];
        }
        else if (trustee.AccountCount < 0)
        {
            count = IsCounting ? Localizer[L.PkiTemplates.CountPending] : string.Empty;
        }
        else
        {
            count = AccountCountText(trustee.AccountCount, trustee.DisabledCount);
        }

        var first = trustee.Class == PrincipalClass.Broad ? [] : AccountLinks(trustee.FirstAccounts);
        return new PkiTrusteeRowViewModel(trustee, right, TrusteeName(trustee), dn, isGroup, Localizer[PkiTextKeys.ClassKey(trustee.Class)], count, first);
    }

    private PkiRightTotalViewModel TotalRow(TemplateRightTotal total) => new(
        total,
        Localizer[PkiTextKeys.RightKey(total.Right)],
        total.AccountCount + total.DisabledCount == 0 ? Localizer[L.PkiTemplates.Nobody] : AccountCountText(total.AccountCount, total.DisabledCount),
        total.HighestClass is { } c ? Localizer[PkiTextKeys.ClassKey(c)] : string.Empty,
        total.HighestClass == PrincipalClass.Broad ? [] : AccountLinks(total.FirstAccounts));

    private IReadOnlyList<PkiAccountLinkViewModel> AccountLinks(IReadOnlyList<PkiAccountRef> accounts) =>
        [.. accounts.Select(a => new PkiAccountLinkViewModel(a, a.IsDisabled ? Localizer.Format(L.PkiTemplates.Disabled, a.Name) : a.Name))];

    private string AccountCountText(int accounts, int disabled) => disabled > 0
        ? Localizer.Format(L.PkiTemplates.AccountCountDisabled, PkiDisplay.Count(Localizer, accounts), PkiDisplay.Count(Localizer, disabled))
        : Localizer.Format(L.PkiTemplates.AccountCount, PkiDisplay.Count(Localizer, accounts));

    /// <summary>The trustee's name: resolved on the window's DC, else the expansion's, else the SID.</summary>
    private string TrusteeName(TemplateTrustee trustee) =>
        _trustees.GetValueOrDefault(trustee.Sid)?.Name ?? (string.IsNullOrWhiteSpace(trustee.Name) ? trustee.Sid : trustee.Name!);

    // ---- Navigation -------------------------------------------------------------------------------------------------------

    /// <summary>A trustee: a group opens its property sheet (<i>Członkowie</i> lists all members), an account its own; broad trustees do not open.</summary>
    [RelayCommand]
    private Task OpenTrusteeAsync(PkiTrusteeRowViewModel? trustee) =>
        trustee is { CanOpen: true, Dn: { } dn } ? Pki.Ui.Dialogs.ShowPropertiesAsync(dn) : Task.CompletedTask;

    /// <summary>One of the first accounts: its property sheet.</summary>
    [RelayCommand]
    private Task OpenAccountAsync(PkiAccountLinkViewModel? account) =>
        account is { Dn.Length: > 0 } ? Pki.Ui.Dialogs.ShowPropertiesAsync(account.Dn) : Task.CompletedTask;

    // ---- Names ------------------------------------------------------------------------------------------------------------

    /// <summary>The name, object and kind of a trustee read on the window's DC (memory only).</summary>
    private sealed record Trustee(string Name, string? Dn, bool IsGroup)
    {
        public override string ToString() => $"{nameof(Trustee)}({(IsGroup ? "group" : "account")})";
    }

    /// <summary>
    /// The trustees of the domain partition by <c>objectSid</c> (one OR search per 50 SIDs, <c>LdapFilter</c>); well-known
    /// principals by their localized name; a SID nothing names stays a SID (never an error – names are a convenience).
    /// </summary>
    private async Task<IReadOnlyDictionary<string, Trustee>> ResolveTrusteesAsync(IDirectorySession session, IEnumerable<string> sids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, Trustee>(StringComparer.OrdinalIgnoreCase);
        var unknown = new List<string>();
        foreach (var sid in sids.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (WellKnownName(sid) is { } name)
            {
                result[sid] = new Trustee(name, null, IsGroup: true);
            }
            else if (sid.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
            {
                unknown.Add(sid);
            }
        }

        var netBios = string.IsNullOrWhiteSpace(session.Info.DomainNetBiosName) ? session.Info.DomainDnsName : session.Info.DomainNetBiosName;
        foreach (var chunk in unknown.Chunk(NameChunk))
        {
            try
            {
                await foreach (var obj in session.SearchAsync(new DirectorySearch
                {
                    BaseDn = session.Info.DefaultNamingContext,
                    Scope = SearchScope.Subtree,
                    Filter = LdapFilter.Or(chunk.Select(sid => LdapFilter.Equal(Attr.ObjectSid, sid))),
                    Attributes = [Attr.ObjectSid, Attr.Name, Attr.SamAccountName, Attr.ObjectClass],
                    PageSize = NameChunk,
                }, cancellationToken).ConfigureAwait(false))
                {
                    if (obj.ObjectSid is not { } sid)
                    {
                        continue;
                    }

                    var account = obj.GetString(Attr.SamAccountName) ?? obj.Name;
                    var prefix = sid.StartsWith("S-1-5-32-", StringComparison.OrdinalIgnoreCase) ? "BUILTIN" : netBios;
                    var isGroup = obj.GetStrings(Attr.ObjectClass).Contains("group", StringComparer.OrdinalIgnoreCase);
                    result[sid] = new Trustee(string.IsNullOrEmpty(prefix) ? account : prefix + "\\" + account, obj.DistinguishedName, isGroup);
                }
            }
            catch (DirectoryException ex) when (!ex.IsTransient)
            {
                // Names are a convenience: the SIDs stay.
            }
        }

        return result;
    }

    /// <summary>The localized name of a well-known trustee of a template ACL (Everyone, Authenticated Users, SYSTEM …), or null.</summary>
    private string? WellKnownName(string sid) => TrusteeNames.WellKnown(Localizer, sid);

    /// <summary>The forest root's domain SID: the window's own in a single-domain forest, else read from the root's head (null when not readable).</summary>
    private static async Task<string?> RootDomainSidAsync(IDirectorySession session, string? domainSid, CancellationToken cancellationToken)
    {
        var root = session.Info.RootDomainNamingContext;
        if (root is null || DistinguishedName.AreEqual(root, session.Info.DefaultNamingContext))
        {
            return domainSid;
        }

        try
        {
            return (await session.GetAsync(root, [Attr.ObjectSid], cancellationToken).ConfigureAwait(false))?.ObjectSid;
        }
        catch (DirectoryException ex) when (!ex.IsTransient)
        {
            return null;
        }
    }
}
