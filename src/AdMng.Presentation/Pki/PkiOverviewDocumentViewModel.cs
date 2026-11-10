using System.Collections.ObjectModel;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Pki;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AdMng.Presentation.Services;

namespace AdMng.Presentation.Pki;

/// <summary>The tabs of <see cref="PkiOverviewDocumentViewModel"/> (in the view's order).</summary>
public enum PkiOverviewTab
{
    Authorities,
    Trust,
    Crls,
    Kra,
    Oids,
    Health,
    Ldaps,
}

/// <summary>
/// <i>Usługi kluczy publicznych</i> (P1, docs/dev/p-certificate-services.md 2.5, decision 9): the forest PKI of the window's
/// snapshot – the enterprise CAs with their certificate, path, templates and CES (<i>Otwórz komputer</i> / <i>Zdalne akcje</i>
/// by the CA host's computer DN), the trusted root and intermediate CAs and NTAuth matched to the enterprise CAs, the CRLs of
/// <c>CN=CDP</c>, the key recovery agents, the OIDs with their group links, a <i>pkiview</i>-style <i>Zdrowie</i> tab
/// (<c>PkiHealth.Evaluate</c> with the current thresholds and clock, re-evaluated when the tab is shown, on F5 and on a
/// settings change; <i>Sprawdź adresy HTTP</i> on the operator's button only) and a <i>Certyfikaty LDAPS</i> tab (the TLS
/// probe of the DCs on the tab's first activation or its button). Read-only (rule 5); everything stays in the window's
/// snapshot service and this document (rule 1).
/// </summary>
public sealed partial class PkiOverviewDocumentViewModel : PkiDocumentBase, IPkiRequestTarget
{
    private readonly CancellationTokenSource _actions;
    private PkiSnapshot? _snapshot;
    private string _server = string.Empty;
    private PkiDocumentRequest? _pending;
    private bool _ldapsActivated;

    public PkiOverviewDocumentViewModel(PkiServices pki, PkiDocumentRequest? request = null)
        : base(pki)
    {
        _pending = request;
        _actions = CancellationTokenSource.CreateLinkedTokenSource(pki.Lifetime);
        Pki.Snapshot.Changed += OnSnapshotChanged;
        LoadedText = string.Empty;
    }

    public override string Title => Localizer[L.Pki.OverviewTitle];

    public override ObjectIcon Icon => ObjectIcon.Certificate;

    public override string StatusText => Error ?? (IsLoading ? (ProgressText.Length > 0 ? ProgressText : Localizer[L.Common.Loading]) : LoadedText);

    public override string SelectionText => SelectedTab switch
    {
        PkiOverviewTab.Authorities => SelectedAuthority?.Name ?? string.Empty,
        PkiOverviewTab.Trust => SelectedCertificate?.Subject ?? string.Empty,
        _ => string.Empty,
    };

    /// <summary>The snapshot shown (null before the first load).</summary>
    public PkiSnapshot? Snapshot => _snapshot;

    /// <summary>"Odczyt obiektów PKI: 120…" while the snapshot is read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string ProgressText { get; private set; } = string.Empty;

    /// <summary>The selected tab (as an index of <see cref="PkiOverviewTab"/> for the view's tab control).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTab), nameof(SelectionText))]
    public partial int SelectedTabIndex { get; set; }

    public PkiOverviewTab SelectedTab
    {
        get => Enum.IsDefined((PkiOverviewTab)SelectedTabIndex) ? (PkiOverviewTab)SelectedTabIndex : PkiOverviewTab.Authorities;
        set => SelectedTabIndex = (int)value;
    }

    /// <summary>"Urzędy przedsiębiorstwa: 2 · szablony: 26 · …".</summary>
    [ObservableProperty]
    public partial string SummaryText { get; private set; } = string.Empty;

    /// <summary>"Odczytano z dc01.contoso.test, 13.10.2026 14:00.".</summary>
    [ObservableProperty]
    public partial string ReadAtText { get; private set; } = string.Empty;

    /// <summary>What the read observed (localized, one line per notice).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotices))]
    public partial IReadOnlyList<string> Notices { get; private set; } = [];

    public bool HasNotices => Notices.Count > 0;

    /// <summary>No enterprise CA and no trust store: the forest has no AD CS (or it was not readable).</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    public string ReadOnlyNote => Localizer[L.Pki.ReadOnlyNote];

    // ---- Enterprise CAs ---------------------------------------------------------------------------------------------------

    public ObservableCollection<PkiCaRowViewModel> Authorities { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedAuthority), nameof(SelectionText))]
    [NotifyCanExecuteChangedFor(nameof(OpenComputerCommand), nameof(OpenRemoteActionsCommand))]
    public partial PkiCaRowViewModel? SelectedAuthority { get; set; }

    public bool HasSelectedAuthority => SelectedAuthority is not null;

    /// <summary>The window can open the K1 remote actions of the CA host.</summary>
    public bool CanOpenRemoteActionsAtAll => Pki.CanOpenRemoteActions;

    // ---- Trust stores ------------------------------------------------------------------------------------------------------

    public ObservableCollection<PkiCertificateRowViewModel> TrustRows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedCertificate), nameof(SelectionText))]
    public partial PkiCertificateRowViewModel? SelectedCertificate { get; set; }

    public bool HasSelectedCertificate => SelectedCertificate is not null;

    /// <summary>The NTAuth store's summary ("certyfikaty NTAuth: 1 · urzędy przedsiębiorstwa w NTAuth: 1 z 2").</summary>
    [ObservableProperty]
    public partial string NtAuthText { get; private set; } = string.Empty;

    // ---- CRLs, KRA, OIDs ---------------------------------------------------------------------------------------------------

    public ObservableCollection<PkiCrlRowViewModel> CrlRows { get; } = [];

    public ObservableCollection<PkiKraRowViewModel> KraRows { get; } = [];

    public ObservableCollection<PkiOidRowViewModel> OidRows { get; } = [];

    public bool HasKra => KraRows.Count > 0;

    public bool HasCrls => CrlRows.Count > 0;

    public bool HasOids => OidRows.Count > 0;

    // ---- Load -------------------------------------------------------------------------------------------------------------

    /// <summary>Points the overview at a CA (<i>Otwórz urząd</i>) – now when it is shown, else after the load.</summary>
    public void Apply(PkiDocumentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _pending = request;
        ApplyPending();
    }

    protected override async Task LoadCoreAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        ProgressText = Localizer.Format(L.Pki.ReadProgress, 0);
        var progress = Pki.Dispatcher.CreateProgress<PkiReadProgress>(p => ProgressText = Localizer.Format(L.Pki.ReadProgress, PkiDisplay.Count(Localizer, p.Objects)));
        try
        {
            var snapshot = await Pki.Snapshot.GetAsync(IsRefresh, progress, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            _server = session.Info.Server;
            Show(snapshot);
            if (_ldapsActivated || SelectedTab == PkiOverviewTab.Ldaps)
            {
                // The LDAPS tab was seen (or opened while the snapshot was read): its rows again – probed on F5 or for another
                // DC (the probe results were dropped with the snapshot), from the cache after a language switch.
                _ldapsActivated = true;
                try
                {
                    await LoadLdapsAsync(IsRefresh, cancellationToken).ConfigureAwait(true);
                }
                catch (Exception ex) when (IsExpected(ex) && !cancellationToken.IsCancellationRequested)
                {
                    // The overview stays; the tab says why its checks are missing.
                    LdapsStatusText = Pki.Errors.Translate(ex);
                }
            }
        }
        finally
        {
            ProgressText = string.Empty;
        }
    }

    protected override void OnNotConnected()
    {
        _snapshot = null;
        Authorities.Clear();
        TrustRows.Clear();
        CrlRows.Clear();
        KraRows.Clear();
        OidRows.Clear();
        HealthRows.Clear();
        HttpRows.Clear();
        LdapsRows.Clear();
        Notices = [];
        SummaryText = string.Empty;
        ReadAtText = string.Empty;
        LoadedText = string.Empty;
        OnPropertyChanged(nameof(Snapshot));
        RaiseCollectionFlags();
    }

    /// <summary>The thresholds changed: the states are evaluated again (nothing is read).</summary>
    protected override void OnSettingsChanged() => Reevaluate();

    protected override void RefreshCommands()
    {
        OpenComputerCommand.NotifyCanExecuteChanged();
        OpenRemoteActionsCommand.NotifyCanExecuteChanged();
        CheckHttpCommand.NotifyCanExecuteChanged();
        ProbeLdapsCommand.NotifyCanExecuteChanged();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Pki.Snapshot.Changed -= OnSnapshotChanged;
            _actions.Cancel();
            _actions.Dispose();
        }
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (SelectedTab == PkiOverviewTab.Health)
        {
            // "Re-evaluated on activation" (2.5): the clock may have moved since the snapshot was shown.
            Reevaluate();
        }
        else if (SelectedTab == PkiOverviewTab.Ldaps && !_ldapsActivated && _snapshot is not null && !IsLoading)
        {
            // The first activation of the tab probes (decision 1: only on explicit navigation or the button).
            _ldapsActivated = true;
            ProbeOnActivationAsync().Forget();
        }
    }

    /// <summary>Builds every tab from <paramref name="snapshot"/> (the states at the current thresholds and clock).</summary>
    private void Show(PkiSnapshot snapshot)
    {
        var keepCa = SelectedAuthority?.Dn;
        var keepCertificate = SelectedCertificate?.Certificate.Summary?.Sha256;
        _snapshot = snapshot;
        OnPropertyChanged(nameof(Snapshot));
        var thresholds = Pki.Settings.Current.Pki.Thresholds();
        var now = Pki.Time.GetUtcNow();

        Authorities.Clear();
        foreach (var ca in snapshot.EnterpriseCas)
        {
            Authorities.Add(CaRow(snapshot, ca, now));
        }

        TrustRows.Clear();
        foreach (var (store, objects) in new[] { (PkiCaStore.RootCa, snapshot.RootCas), (PkiCaStore.Aia, snapshot.AiaObjects) })
        {
            foreach (var obj in objects)
            {
                AddTrustRows(snapshot, store, obj, now);
            }
        }

        if (snapshot.NtAuth is { } ntAuth)
        {
            AddTrustRows(snapshot, PkiCaStore.NtAuth, ntAuth, now);
        }

        CrlRows.Clear();
        foreach (var list in snapshot.CrlLists)
        {
            AddCrlRows(snapshot, list);
        }

        KraRows.Clear();
        foreach (var agent in snapshot.KraAgents)
        {
            foreach (var certificate in agent.Certificates)
            {
                KraRows.Add(new PkiKraRowViewModel(agent, certificate)
                {
                    ValidToText = PkiDisplay.Day(Localizer, certificate.Summary?.NotAfter),
                });
            }
        }

        OidRows.Clear();
        foreach (var oid in snapshot.Oids)
        {
            OidRows.Add(new PkiOidRowViewModel(oid, oid.Flags switch
            {
                1 => Localizer[L.Pki.OidTemplate],
                2 => Localizer[L.Pki.OidIssuancePolicy],
                3 => Localizer[L.Pki.OidApplicationPolicy],
                _ => Localizer.Format(L.Pki.OidOther, oid.Flags),
            }));
        }

        Notices = [.. snapshot.Notices.Select(NoticeText).Distinct(StringComparer.Ordinal)];
        IsEmpty = snapshot.EnterpriseCas.Count == 0 && snapshot.RootCas.Count == 0 && snapshot.NtAuth is null;
        SummaryText = Localizer.Format(L.Pki.Summary, snapshot.EnterpriseCas.Count, snapshot.Templates.Count, snapshot.RootCas.Count, snapshot.NtAuth?.Certificates.Count ?? 0);
        ReadAtText = Localizer.Format(L.Pki.ReadAt, _server, PkiDisplay.Date(Localizer, snapshot.ReadAt));
        LoadedText = SummaryText;
        NtAuthText = snapshot.NtAuth is null
            ? Localizer[PkiTextKeys.NoticeKey(PkiReadNoticeCode.ContainerMissing)]
            : Localizer.Format(L.Pki.NtAuthSummary, snapshot.NtAuth.Certificates.Count, snapshot.EnterpriseCas.Count(c => c.InNtAuth), snapshot.EnterpriseCas.Count);
        Evaluate(thresholds, now);
        EvaluateLdaps();
        ShowHttpResults();
        RaiseCollectionFlags();

        SelectedAuthority = Authorities.FirstOrDefault(a => keepCa is not null && DistinguishedName.AreEqual(a.Dn, keepCa)) ?? Authorities.FirstOrDefault();
        SelectedCertificate = TrustRows.FirstOrDefault(r => keepCertificate is not null && r.Certificate.Summary?.Sha256 == keepCertificate) ?? TrustRows.FirstOrDefault();
        ApplyPending();
    }

    /// <summary>The states of the shown rows at the current thresholds and clock (the snapshot is not read again).</summary>
    private void Reevaluate()
    {
        if (_snapshot is null || IsDisposed)
        {
            return;
        }

        Evaluate(Pki.Settings.Current.Pki.Thresholds(), Pki.Time.GetUtcNow());
        EvaluateLdaps();
    }

    private void Evaluate(PkiHealthThresholds thresholds, DateTimeOffset now)
    {
        foreach (var row in TrustRows)
        {
            SetStatus(row, PkiDisplay.CertificateStatus(row.Summary, isCa: true, thresholds, now));
        }

        foreach (var row in CrlRows)
        {
            SetStatus(row, PkiDisplay.CrlStatus(row.Crl.Summary, now));
        }

        foreach (var row in KraRows)
        {
            SetStatus(row, PkiDisplay.CertificateStatus(row.Certificate.Summary, isCa: false, thresholds, now));
        }

        foreach (var row in Authorities)
        {
            SetStatus(row, PkiDisplay.CertificateStatus(row.Current, isCa: true, thresholds, now));
            row.Details = CaDetails(row, now);
        }

        foreach (var row in TrustRows)
        {
            row.Details = CertificateDetails(row.Summary, now);
        }

        EvaluateHealth(thresholds, now);
    }

    private void SetStatus(PkiStatusRowViewModel row, PkiHealthStatus status)
    {
        row.Status = status;
        row.StatusText = PkiDisplay.Status(Localizer, status);
    }

    private PkiCaRowViewModel CaRow(PkiSnapshot snapshot, PkiEnrollmentService ca, DateTimeOffset now)
    {
        var current = ca.Certificates.Select(c => c.Summary).OfType<CertificateSummary>().OrderByDescending(s => s.NotAfter).FirstOrDefault();
        var note = ca.ComputerDn is not null
            ? string.Empty
            : snapshot.Notices.Any(n => n.Code == PkiReadNoticeCode.CaComputerInOtherDomain && n.ObjectGuid == ca.ObjectGuid)
                ? Localizer[L.Pki.ComputerOtherDomain]
                : Localizer[L.Pki.ComputerUnresolved];
        var logonTemplates = ca.InNtAuth
            ? []
            : snapshot.Templates
                .Where(t => ca.PublishedTemplates.Contains(t.Cn, StringComparer.OrdinalIgnoreCase) && PkiFlags.IsClientAuth(t.EffectiveEku))
                .Select(t => t.Cn)
                .ToList();
        var row = new PkiCaRowViewModel(ca, current)
        {
            ValidToText = PkiDisplay.Day(Localizer, current?.NotAfter),
            NtAuthText = Localizer[ca.InNtAuth ? L.Pki.InNtAuth : L.Pki.NotInNtAuth],
            ComputerNote = note,
            NtAuthWarning = logonTemplates.Count > 0 ? Localizer.Format(L.Pki.LogonCaNotInNtAuth, ca.DisplayName ?? ca.Name, string.Join(", ", logonTemplates)) : string.Empty,
            Chain = Chain(snapshot, current),
        };
        row.Details = CaDetails(row, now);
        return row;
    }

    private IReadOnlyList<PkiDetailLine> CaDetails(PkiCaRowViewModel row, DateTimeOffset now)
    {
        var lines = new List<PkiDetailLine>
        {
            new(Localizer[L.Pki.ColumnHost], row.Host.Length > 0 ? row.Host : Localizer[L.Pki.None]),
        };
        lines.AddRange(CertificateDetails(row.Current, now));
        lines.Add(new(Localizer[L.Pki.ColumnNtAuth], row.NtAuthText));
        lines.Add(new(Localizer[L.Pki.CaCertificate], Localizer.Format(L.Pki.Renewals, row.Ca.Certificates.Count)));
        return lines;
    }

    /// <summary>The fields of a certificate (subject, issuer, validity, key, signature, serial, SHA-256, SAN, EKU, CDP, AIA).</summary>
    private IReadOnlyList<PkiDetailLine> CertificateDetails(CertificateSummary? summary, DateTimeOffset now)
    {
        if (summary is null)
        {
            return [new(Localizer[L.Pki.CaCertificate], Localizer[L.Pki.Unreadable])];
        }

        var lines = new List<PkiDetailLine>
        {
            new(Localizer[L.Pki.ColumnSubject], summary.Subject),
            new(Localizer[L.Pki.ColumnIssuer], summary.Issuer),
            new(Localizer[L.Pki.Validity], PkiDisplay.Validity(Localizer, summary, now)),
            new(Localizer[L.Pki.Key], PkiDisplay.Key(Localizer, summary)),
            new(Localizer[L.Pki.SignatureAlgorithm], summary.SignatureAlgorithm),
            new(Localizer[L.Pki.SerialNumber], summary.SerialNumber),
            new(Localizer[L.Pki.ColumnThumbprint], summary.Thumbprint),
            new(Localizer[L.Pki.Sha256], summary.Sha256),
        };
        var san = summary.SanDns.Concat(summary.SanUpn).Concat(summary.SanEmail).Concat(summary.SanUri).ToList();
        if (san.Count > 0)
        {
            lines.Add(new(Localizer[L.Pki.SanText], string.Join(", ", san)));
        }

        if (summary.Ekus.Count > 0)
        {
            lines.Add(new(Localizer[L.Pki.EkuText], PkiDisplay.Purposes(Localizer, summary.Ekus)));
        }

        if (summary.CrlDistributionPoints.Count > 0)
        {
            lines.Add(new(Localizer[L.Pki.CdpText], string.Join("\n", summary.CrlDistributionPoints)));
        }

        if (summary.AiaCaIssuers.Count + summary.AiaOcsp.Count > 0)
        {
            lines.Add(new(Localizer[L.Pki.AiaText], string.Join("\n", summary.AiaCaIssuers.Concat(summary.AiaOcsp))));
        }

        return lines;
    }

    /// <summary>
    /// The certification path of a CA certificate as AD holds it: each issuer found among the snapshot's CA certificates by
    /// subject = issuer and SKI = AKI (when both carry one), up to a self-signed root; nothing is downloaded (decision 1).
    /// </summary>
    private IReadOnlyList<string> Chain(PkiSnapshot snapshot, CertificateSummary? leaf)
    {
        if (leaf is null)
        {
            return [];
        }

        var roots = snapshot.RootCas.SelectMany(r => r.Certificates).Select(c => c.Summary).OfType<CertificateSummary>().ToList();
        var known = roots
            .Concat(snapshot.AiaObjects.SelectMany(a => a.Certificates.Concat(a.CrossCertificates)).Select(c => c.Summary).OfType<CertificateSummary>())
            .Concat(snapshot.EnterpriseCas.SelectMany(c => c.Certificates).Select(c => c.Summary).OfType<CertificateSummary>())
            .ToList();
        var chain = new List<string>();
        var current = leaf;
        for (var depth = 0; depth < 8; depth++)
        {
            var name = PkiDisplay.CommonName(current.Subject);
            if (current.IsSelfSigned)
            {
                chain.Add(roots.Any(r => r.Sha256 == current.Sha256) ? Localizer.Format(L.Pki.ChainRootInAd, name) : name);
                break;
            }

            chain.Add(name);
            var issuer = known.FirstOrDefault(k => k.Sha256 != current.Sha256 && string.Equals(k.Subject, current.Issuer, StringComparison.Ordinal)
                && (k.SubjectKeyIdentifier is null || current.AuthorityKeyIdentifier is null
                    || string.Equals(k.SubjectKeyIdentifier, current.AuthorityKeyIdentifier, StringComparison.OrdinalIgnoreCase)));
            if (issuer is null)
            {
                chain.Add(Localizer.Format(L.Pki.ChainIssuerNotFound, PkiDisplay.CommonName(current.Issuer)));
                break;
            }

            current = issuer;
        }

        return chain;
    }

    private void AddTrustRows(PkiSnapshot snapshot, PkiCaStore store, PkiCaObject obj, DateTimeOffset now)
    {
        var storeText = Localizer[store switch
        {
            PkiCaStore.RootCa => L.Pki.StoreRoot,
            PkiCaStore.Aia => L.Pki.StoreAia,
            _ => L.Pki.StoreNtAuth,
        }];
        foreach (var certificate in obj.Certificates)
        {
            TrustRows.Add(TrustRow(snapshot, store, obj, certificate, storeText, now));
        }

        foreach (var certificate in obj.CrossCertificates)
        {
            TrustRows.Add(TrustRow(snapshot, store, obj, certificate, Localizer[L.Pki.StoreCross], now));
        }
    }

    private PkiCertificateRowViewModel TrustRow(PkiSnapshot snapshot, PkiCaStore store, PkiCaObject obj, PkiCertificate certificate, string storeText, DateTimeOffset now)
    {
        var sha = certificate.Summary?.Sha256;
        var enterprise = sha is null ? null : snapshot.EnterpriseCas.FirstOrDefault(ca => ca.Certificates.Any(c => c.Summary?.Sha256 == sha));
        return new PkiCertificateRowViewModel(store, obj, certificate)
        {
            StoreText = storeText,
            ValidToText = PkiDisplay.Day(Localizer, certificate.Summary?.NotAfter),
            EnterpriseCaText = enterprise is null ? Localizer[L.Pki.NotEnterpriseCa] : enterprise.DisplayName ?? enterprise.Name,
            Details = CertificateDetails(certificate.Summary, now),
        };
    }

    private void AddCrlRows(PkiSnapshot snapshot, PkiCrlList list)
    {
        var ca = list.IssuerCaGuid is { } id ? snapshot.EnterpriseCas.FirstOrDefault(c => c.ObjectGuid == id) : null;
        var caText = ca is null ? Localizer[L.Pki.UnknownCa] : ca.DisplayName ?? ca.Name;
        void Add(PkiCrl? crl, string kindKey)
        {
            if (crl is null)
            {
                return;
            }

            var summary = crl.Summary;
            var kind = Localizer[kindKey];
            if (summary is { IsDelta: true, BaseCrlNumber: { } baseNumber })
            {
                kind = Localizer.Format(L.Pki.DeltaOf, kind, PkiDisplay.Number(baseNumber));
            }

            CrlRows.Add(new PkiCrlRowViewModel(list, crl, kind)
            {
                CaText = caText,
                NumberText = summary is null ? Localizer[L.Pki.Unreadable] : PkiDisplay.Number(summary.CrlNumber),
                ThisUpdateText = PkiDisplay.Date(Localizer, summary?.ThisUpdate),
                NextUpdateText = PkiDisplay.Date(Localizer, summary?.NextUpdate),
                RevokedText = summary is null ? Localizer[L.Pki.None] : PkiDisplay.Count(Localizer, summary.RevokedCount),
            });
        }

        Add(list.Base, L.Pki.CrlBase);
        Add(list.Delta, L.Pki.CrlDelta);
        Add(list.Authority, L.Pki.CrlAuthority);
    }

    private string NoticeText(PkiReadNotice notice)
    {
        var text = Localizer[PkiTextKeys.NoticeKey(notice.Code)];
        return notice.ObjectDn is { } dn ? Localizer.Format(L.Pki.NoticeLine, text, PkiDisplay.LeafName(dn)) : text;
    }

    private void ApplyPending()
    {
        if (_pending is not { CaDn: { } caDn } || Authorities.Count == 0)
        {
            return;
        }

        if (Authorities.FirstOrDefault(a => DistinguishedName.AreEqual(a.Dn, caDn)) is { } row)
        {
            SelectedAuthority = row;
            SelectedTab = PkiOverviewTab.Authorities;
        }

        _pending = null;
    }

    private void RaiseCollectionFlags()
    {
        OnPropertyChanged(nameof(HasKra));
        OnPropertyChanged(nameof(HasCrls));
        OnPropertyChanged(nameof(HasOids));
        OnPropertyChanged(nameof(HasHealthRows));
        OnPropertyChanged(nameof(HasHttpRows));
        OnPropertyChanged(nameof(HasLdapsRows));
    }

    /// <summary>
    /// The window's cache replaced the snapshot (F5 in another PKI document): this document shows the new one (from the cache,
    /// nothing is read again). Its own HTTP check and probes update the document directly.
    /// </summary>
    private void OnSnapshotChanged(object? sender, EventArgs e) => _ = Pki.Dispatcher.RunAsync(() =>
    {
        if (!IsDisposed && !IsLoading && _snapshot is not null && Pki.Snapshot.Current is { } current && !ReferenceEquals(current, _snapshot))
        {
            LoadAsync().Forget();
        }
    });

    // ---- Navigation -------------------------------------------------------------------------------------------------------

    /// <summary><i>Otwórz komputer</i>: the CA host's computer object.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenComputer))]
    private Task OpenComputerAsync() => SelectedAuthority is { Ca.ComputerDn: { } dn } ? Pki.OpenComputerAsync(dn) : Task.CompletedTask;

    private bool CanOpenComputer() => SelectedAuthority is { CanOpenComputer: true } && HasSession;

    /// <summary><i>Zdalne akcje</i> of the CA host (K1).</summary>
    [RelayCommand(CanExecute = nameof(CanOpenRemoteActions))]
    private Task OpenRemoteActionsAsync() => SelectedAuthority is { Ca.ComputerDn: { } dn } ? Pki.OpenRemoteActionsAsync(dn) : Task.CompletedTask;

    private bool CanOpenRemoteActions() => CanOpenComputer() && Pki.CanOpenRemoteActions;

    /// <summary><i>Otwórz szablon</i>: the templates document with the template (its cn) selected.</summary>
    [RelayCommand]
    private void OpenTemplate(string? cn)
    {
        if (!string.IsNullOrWhiteSpace(cn))
        {
            Pki.RequestDocument(new PkiDocumentRequest(PkiDocumentKind.Templates) { TemplateCn = cn });
        }
    }

    /// <summary>The group an OID object links (ESC13): its property sheet.</summary>
    [RelayCommand]
    private Task OpenGroupAsync(PkiOidRowViewModel? row) =>
        row?.Oid.GroupLinkDn is { } dn ? Pki.Ui.Dialogs.ShowPropertiesAsync(dn) : Task.CompletedTask;
}
