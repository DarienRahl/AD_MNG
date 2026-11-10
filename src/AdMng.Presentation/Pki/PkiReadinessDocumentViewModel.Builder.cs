using AdMng.Core.Pki;
using AdMng.Core.Pki.Tls;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Pki;

/// <summary>
/// Builds the areas of <i>Gotowość na silne mapowanie i NTAuth</i> from what the document has read (2.5): pure – nothing is
/// read here; the thresholds and the clock are passed in, so the expiry warnings follow the settings when they are shown.
/// </summary>
internal sealed class PkiReadinessRows(PkiServices pki, ILocalizer localizer, PkiSnapshot snapshot, PkiHealthThresholds thresholds, DateTimeOffset now)
{
    /// <summary>The LDAPS port (the global catalog's is 3269).</summary>
    private const int LdapsPort = 636;

    private readonly bool _hasPki = !snapshot.Notices.Any(n => n.Code is PkiReadNoticeCode.BaseNotFound or PkiReadNoticeCode.BaseAccessDenied);

    /// <summary>Accounts with weak or invalid <c>altSecurityIdentities</c> (Tier 0 first), and one OK row for the strong ones.</summary>
    public PkiReadinessGroupViewModel Mappings(PkiEnrollmentExpansion? expansion, string? error, bool reading)
    {
        var rows = new List<PkiReadinessRowViewModel>();
        if (error is not null)
        {
            rows.Add(Row(PkiReadinessStatus.NotChecked, localizer[L.PkiReadiness.MappingsTitle], localizer.Format(L.PkiReadiness.ReadFailed, error)));
        }
        else if (expansion is null)
        {
            rows.Add(Row(PkiReadinessStatus.NotChecked, localizer[L.PkiReadiness.MappingsTitle],
                localizer[reading ? L.PkiReadiness.Reading : L.PkiReadiness.NeverRun]));
        }
        else
        {
            var compare = StringComparer.Create(localizer.Culture, ignoreCase: true);
            var accounts = expansion.MappedAccounts
                .Select(a => (Account: a, Certificate: a.Mappings.Where(m => m.Strength != MappingStrength.NotCertificate).ToList()))
                .Where(a => a.Certificate.Count > 0)
                .ToList();
            foreach (var (account, mappings) in accounts
                .Where(a => a.Certificate.Any(m => m.Strength is MappingStrength.Weak or MappingStrength.Invalid))
                .OrderByDescending(a => a.Account.IsTier0)
                .ThenBy(a => a.Account.Name, compare))
            {
                var weak = mappings.Where(m => m.Strength == MappingStrength.Weak).Select(m => m.Kind).Distinct().Order().ToList();
                var notes = new List<string>();
                if (weak.Count > 0)
                {
                    notes.Add(localizer.Format(L.PkiReadiness.MappingsWeak,
                        string.Join(PkiFindingCodes.ListSeparator, weak.Select(k => localizer[PkiTextKeys.MappingKindKey(k)]))));
                }

                if (mappings.Any(m => m.Strength == MappingStrength.Invalid))
                {
                    notes.Add(localizer[L.PkiReadiness.MappingsInvalid]);
                }

                if (account.IsTier0)
                {
                    notes.Add(localizer[L.PkiReadiness.MappingsTier0]);
                }

                if (account.IsDisabled)
                {
                    notes.Add(localizer[L.PkiReadiness.MappingsDisabled]);
                }

                var status = weak.Count == 0 ? PkiReadinessStatus.Warning
                    : account.IsTier0 && !account.IsDisabled ? PkiReadinessStatus.Problem
                    : PkiReadinessStatus.Warning;
                rows.Add(Row(status, account.Name, string.Join(" ", notes),
                    localizer[weak.Count > 0 ? L.PkiReadiness.MappingsWeakRemedy : L.PkiReadiness.MappingsInvalidRemedy],
                    OpenAccount(account.Dn), localizer[L.PkiReadiness.OpenAccount]));
            }

            var strong = accounts.Count(a => a.Certificate.All(m => m.Strength == MappingStrength.Strong));
            if (strong > 0)
            {
                rows.Add(Row(PkiReadinessStatus.Ok, localizer.Format(L.PkiReadiness.MappingsStrong, strong), localizer[L.PkiReadiness.MappingsStrongDetail]));
            }
            else if (accounts.Count == 0)
            {
                rows.Add(Row(PkiReadinessStatus.Ok, localizer[L.PkiReadiness.MappingsNone], localizer[L.PkiReadiness.MappingsNoneDetail]));
            }
        }

        return Group(PkiReadinessArea.Mappings, L.PkiReadiness.MappingsTitle, L.PkiReadiness.MappingsDescription, rows);
    }

    /// <summary>
    /// Published templates that name a client-authentication purpose: without the SID extension (<c>CT_FLAG_NO_SECURITY_EXTENSION</c>)
    /// a problem; with the subject supplied in the request a warning (the CA adds no SID: logon needs a strong explicit mapping).
    /// </summary>
    public PkiReadinessGroupViewModel AuthenticationTemplates()
    {
        var rows = new List<PkiReadinessRowViewModel>();
        if (!_hasPki)
        {
            rows.Add(NoPki());
            return Group(PkiReadinessArea.AuthenticationTemplates, L.PkiReadiness.TemplatesTitle, L.PkiReadiness.TemplatesDescription, rows);
        }

        var compare = StringComparer.Create(localizer.Culture, ignoreCase: true);
        var templates = snapshot.Templates.Where(t => IsPublished(t) && Authenticates(t)).OrderBy(Name, compare).ToList();
        var ok = 0;
        foreach (var template in templates)
        {
            if (template.NoSecurityExtension)
            {
                rows.Add(Row(PkiReadinessStatus.Problem, Name(template), localizer[L.PkiReadiness.TemplatesNoExtension],
                    localizer[L.PkiReadiness.TemplatesNoExtensionRemedy], OpenTemplate(template.Cn), localizer[L.PkiReadiness.OpenTemplate]));
            }
            else if (template.EnrolleeSuppliesSubject)
            {
                rows.Add(Row(PkiReadinessStatus.Warning, Name(template), localizer[L.PkiReadiness.TemplatesSupplied],
                    localizer[L.PkiReadiness.TemplatesSuppliedRemedy], OpenTemplate(template.Cn), localizer[L.PkiReadiness.OpenTemplate]));
            }
            else
            {
                ok++;
            }
        }

        if (ok > 0)
        {
            rows.Add(Row(PkiReadinessStatus.Ok, localizer.Format(L.PkiReadiness.TemplatesOk, ok), localizer[L.PkiReadiness.TemplatesOkDetail]));
        }
        else if (templates.Count == 0)
        {
            rows.Add(Row(PkiReadinessStatus.Ok, localizer[L.PkiReadiness.TemplatesNone], localizer[L.PkiReadiness.TemplatesNoneDetail]));
        }

        return Group(PkiReadinessArea.AuthenticationTemplates, L.PkiReadiness.TemplatesTitle, L.PkiReadiness.TemplatesDescription, rows);
    }

    /// <summary>Every enterprise CA: in NTAuth (OK), publishing logon templates without being in NTAuth (problem), or not needing it.</summary>
    public PkiReadinessGroupViewModel NtAuth()
    {
        var rows = new List<PkiReadinessRowViewModel>();
        if (!_hasPki)
        {
            rows.Add(NoPki());
        }
        else if (snapshot.EnterpriseCas.Count == 0)
        {
            rows.Add(Row(PkiReadinessStatus.Ok, localizer[L.PkiReadiness.NtAuthNoCa], localizer[L.PkiReadiness.NtAuthNoCaDetail]));
        }
        else
        {
            var compare = StringComparer.Create(localizer.Culture, ignoreCase: true);
            if (snapshot.NtAuth is null)
            {
                rows.Add(Row(PkiReadinessStatus.Problem, localizer[L.PkiReadiness.NtAuthTitle], localizer[L.PkiReadiness.NtAuthMissing]));
            }

            foreach (var ca in snapshot.EnterpriseCas.OrderBy(CaName, compare))
            {
                var logon = snapshot.Templates
                    .Where(t => ca.PublishedTemplates.Contains(t.Cn, StringComparer.OrdinalIgnoreCase) && Authenticates(t))
                    .Select(Name)
                    .Order(compare)
                    .ToList();
                var open = OpenCa(ca.Dn);
                if (ca.InNtAuth)
                {
                    rows.Add(Row(PkiReadinessStatus.Ok, CaName(ca), localizer[L.PkiReadiness.NtAuthIn], string.Empty, open, localizer[L.PkiReadiness.OpenCa]));
                }
                else if (logon.Count > 0)
                {
                    rows.Add(Row(PkiReadinessStatus.Problem, CaName(ca),
                        localizer.Format(L.PkiReadiness.NtAuthNotIn, string.Join(PkiFindingCodes.ListSeparator, logon)),
                        localizer[L.PkiReadiness.NtAuthNotInRemedy], open, localizer[L.PkiReadiness.OpenCa]));
                }
                else
                {
                    rows.Add(Row(PkiReadinessStatus.Ok, CaName(ca), localizer[L.PkiReadiness.NtAuthNotNeeded], string.Empty, open, localizer[L.PkiReadiness.OpenCa]));
                }
            }
        }

        return Group(PkiReadinessArea.NtAuth, L.PkiReadiness.NtAuthTitle, L.PkiReadiness.NtAuthDescription, rows);
    }

    /// <summary>The DC LDAPS checks the operator ran (or the snapshot service cached): SAN, EKU, validity, chain, expiry.</summary>
    public PkiReadinessGroupViewModel DomainControllerLdaps(IReadOnlyList<DcTlsCheck>? checks, string? error, string domainDns)
    {
        var rows = new List<PkiReadinessRowViewModel>();
        if (pki.TlsProbe is null)
        {
            rows.Add(Row(PkiReadinessStatus.NotChecked, localizer[L.PkiReadiness.LdapsUnavailable], localizer[L.PkiReadiness.LdapsUnavailableDetail]));
        }
        else if (error is not null)
        {
            rows.Add(Row(PkiReadinessStatus.NotChecked, localizer[L.PkiReadiness.LdapsTitle], localizer.Format(L.PkiReadiness.ReadFailed, error)));
        }
        else if (checks is null)
        {
            rows.Add(Row(PkiReadinessStatus.NotChecked, localizer[L.PkiReadiness.LdapsNotRun], localizer[L.PkiReadiness.LdapsNotRunDetail]));
        }
        else if (checks.Count == 0)
        {
            rows.Add(Row(PkiReadinessStatus.NotChecked, localizer[L.PkiReadiness.LdapsNoDc], string.Empty));
        }
        else
        {
            foreach (var check in checks)
            {
                rows.Add(Ldaps(check, domainDns));
            }
        }

        return Group(PkiReadinessArea.DomainControllerLdaps, L.PkiReadiness.LdapsTitle, L.PkiReadiness.LdapsDescription, rows);
    }

    private PkiReadinessRowViewModel Ldaps(DcTlsCheck check, string domainDns)
    {
        var title = localizer.Format(L.PkiReadiness.LdapsHost, check.Host, check.Port);
        if (check.Outcome != TlsProbeOutcome.Ok || check.Certificate is not { } info)
        {
            // LDAPS (636) is what strong mapping and the LDAP clients need; a closed global catalog port (3269) is often a
            // firewall decision, so it only warns.
            var status = check.Outcome switch
            {
                TlsProbeOutcome.PortClosed when check.Port != LdapsPort => PkiReadinessStatus.Warning,
                TlsProbeOutcome.HandshakeFailed or TlsProbeOutcome.PortClosed => PkiReadinessStatus.Problem,
                TlsProbeOutcome.Timeout or TlsProbeOutcome.NameNotResolved => PkiReadinessStatus.Warning,
                _ => PkiReadinessStatus.NotChecked,
            };
            return Row(status, title, localizer[PkiTextKeys.TlsKey(check.Outcome)],
                status == PkiReadinessStatus.Problem ? localizer[L.PkiReadiness.LdapsRemedy] : string.Empty);
        }

        var problems = new List<string>();
        var warnings = new List<string>();
        if (info.Leaf is null)
        {
            problems.Add(localizer[L.PkiReadiness.LdapsUnreadable]);
        }
        else
        {
            if (!info.NameMatchesDc)
            {
                problems.Add(localizer[L.PkiReadiness.LdapsNameMismatch]);
            }

            if (!info.HasServerAuth)
            {
                problems.Add(localizer[L.PkiReadiness.LdapsNoServerAuth]);
            }

            if (!info.IsTimeValid)
            {
                problems.Add(localizer[L.PkiReadiness.LdapsNotTimeValid]);
            }
            else if (info.DaysLeft <= thresholds.EndEntityWarningDays)
            {
                warnings.Add(localizer.Format(L.PkiReadiness.LdapsExpiring, info.DaysLeft));
            }

            if (!info.ChainsToAdRoot)
            {
                warnings.Add(localizer[L.PkiReadiness.LdapsNotChained]);
            }

            if (!info.NameMatchesDomain && domainDns.Length > 0)
            {
                warnings.Add(localizer[L.PkiReadiness.LdapsNoDomainName]);
            }
        }

        if (problems.Count > 0 || warnings.Count > 0)
        {
            return Row(problems.Count > 0 ? PkiReadinessStatus.Problem : PkiReadinessStatus.Warning, title,
                string.Join(" ", problems.Concat(warnings)), localizer[L.PkiReadiness.LdapsRemedy]);
        }

        var leaf = info.Leaf!;
        return Row(PkiReadinessStatus.Ok, title, localizer.Format(L.PkiReadiness.LdapsOk, IssuerName(leaf),
            leaf.NotAfter.ToLocalTime().ToString("d", localizer.Culture), info.DaysLeft));
    }

    /// <summary>The KDC indicator of every DC (from the certificates published on its object).</summary>
    public PkiReadinessGroupViewModel KdcCertificates(IReadOnlyList<PkiDomainController>? domainControllers, string? error, bool reading)
    {
        var rows = new List<PkiReadinessRowViewModel>();
        if (error is not null)
        {
            rows.Add(Row(PkiReadinessStatus.NotChecked, localizer[L.PkiReadiness.KdcTitle], localizer.Format(L.PkiReadiness.ReadFailed, error)));
        }
        else if (domainControllers is null)
        {
            rows.Add(Row(PkiReadinessStatus.NotChecked, localizer[L.PkiReadiness.KdcTitle], localizer[reading ? L.PkiReadiness.Reading : L.PkiReadiness.NeverRun]));
        }
        else if (domainControllers.Count == 0)
        {
            rows.Add(Row(PkiReadinessStatus.NotChecked, localizer[L.PkiReadiness.KdcNoDc], string.Empty));
        }
        else
        {
            foreach (var dc in domainControllers)
            {
                var host = dc.HostName ?? PkiFindingRows.LeafName(dc.Dn);
                var kdc = KdcCertificateEvaluation.Evaluate(dc.Certificates, dc.HostName ?? string.Empty, snapshot.TrustMaterial, now);
                var parts = new List<string> { localizer[PkiTextKeys.KdcKey(kdc.State)] };
                var status = kdc.State switch
                {
                    KdcReadinessState.Ready => PkiReadinessStatus.Ok,
                    KdcReadinessState.NoKdcEku => PkiReadinessStatus.Warning,
                    KdcReadinessState.IssuerNotInNtAuth or KdcReadinessState.Expired => PkiReadinessStatus.Problem,
                    _ => PkiReadinessStatus.NotChecked,
                };
                if (kdc.State == KdcReadinessState.NoKdcEku)
                {
                    parts.Add(localizer[L.PkiReadiness.KdcNoKdcEkuDetail]);
                }

                if (kdc.Best is { } best)
                {
                    parts.Add(localizer.Format(L.PkiReadiness.KdcCertificate, IssuerName(best), best.NotAfter.ToLocalTime().ToString("d", localizer.Culture)));
                    var days = (int)Math.Floor((best.NotAfter - now).TotalDays);
                    if (kdc.State == KdcReadinessState.Ready && days <= thresholds.EndEntityWarningDays)
                    {
                        status = PkiReadinessStatus.Warning;
                        parts.Add(localizer.Format(L.PkiReadiness.KdcExpiring, days));
                    }
                }

                if (dc.IsReadOnly)
                {
                    parts.Add(localizer[L.PkiReadiness.KdcReadOnly]);
                }

                // The state is a noun phrase ("Gotowy (…)"): a full stop before the sentences that follow it.
                if (parts.Count > 1)
                {
                    parts[0] += ".";
                }

                rows.Add(Row(status, host, string.Join(" ", parts),
                    status is PkiReadinessStatus.Problem or PkiReadinessStatus.Warning ? localizer[L.PkiReadiness.KdcRemedy] : string.Empty));
            }
        }

        return Group(PkiReadinessArea.KdcCertificates, L.PkiReadiness.KdcTitle, L.PkiReadiness.KdcDescription, rows);
    }

    /// <summary>The KDC events 39/40/41/45/21: their analysis is P5 (the event-log reader has no preset query in P1).</summary>
    public PkiReadinessGroupViewModel KdcEvents() => new(PkiReadinessArea.KdcEvents, localizer[L.PkiReadiness.EventsTitle], string.Empty,
        [Row(PkiReadinessStatus.NotChecked, localizer[L.PkiReadiness.EventsRow], localizer[L.PkiReadiness.EventsDetail])], localizer);

    /// <summary>A published template: an enterprise CA lists it in <c>certificateTemplates</c>.</summary>
    private bool IsPublished(CertificateTemplate template) =>
        snapshot.EnterpriseCas.Any(ca => ca.PublishedTemplates.Contains(template.Cn, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// The template names a client-authentication purpose (Client Authentication, Smart-Card Logon, PKINIT or Any Purpose) in
    /// its application policies or <c>pKIExtendedKeyUsage</c>; an empty EKU (the CA templates) is the ESC2 review's business.
    /// </summary>
    private static bool Authenticates(CertificateTemplate template) =>
        (template.EffectiveEku.Count > 0 && PkiFlags.IsClientAuth(template.EffectiveEku)) || (template.Ekus.Count > 0 && PkiFlags.IsClientAuth(template.Ekus));

    private static string Name(CertificateTemplate template) => string.IsNullOrWhiteSpace(template.DisplayName) ? template.Cn : template.DisplayName;

    private static string CaName(PkiEnrollmentService ca) => string.IsNullOrWhiteSpace(ca.DisplayName) ? ca.Name : ca.DisplayName;

    /// <summary>The issuer's common name (else the whole issuer) of a certificate.</summary>
    private static string IssuerName(CertificateSummary certificate)
    {
        var issuer = certificate.Issuer;
        foreach (var part in issuer.Split(',', StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            {
                return part[3..];
            }
        }

        return issuer;
    }

    private PkiReadinessRowViewModel NoPki() => Row(PkiReadinessStatus.NotChecked, localizer[L.PkiReadiness.NoPki], string.Empty);

    private PkiReadinessGroupViewModel Group(PkiReadinessArea area, string title, string description, IReadOnlyList<PkiReadinessRowViewModel> rows) =>
        new(area, localizer[title], localizer[description], rows, localizer);

    private PkiReadinessRowViewModel Row(PkiReadinessStatus status, string title, string detail, string remedy = "", IRelayCommand? open = null, string openText = "") => new()
    {
        Status = status,
        StatusText = localizer[PkiReadinessDocumentViewModel.StatusKey(status)],
        Title = title,
        Detail = detail,
        RemedyText = remedy.Length == 0 ? string.Empty : localizer.Format(L.PkiReadiness.RemedyLine, remedy),
        OpenCommand = open,
        OpenText = openText,
    };

    private AsyncRelayCommand OpenAccount(string dn) => new(() => pki.Ui.Dialogs.ShowPropertiesAsync(dn));

    private RelayCommand OpenTemplate(string cn) => new(() => pki.RequestDocument(new PkiDocumentRequest(PkiDocumentKind.Templates) { TemplateCn = cn }));

    private RelayCommand OpenCa(string dn) => new(() => pki.RequestDocument(new PkiDocumentRequest(PkiDocumentKind.Overview) { CaDn = dn }));
}
