using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Pki;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.PropertySheets;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Pki;

/// <summary>The validity of a published certificate at the window's clock (<c>Cert.Validity.&lt;CertificateValidity&gt;</c>).</summary>
public enum CertificateValidity
{
    Valid,

    /// <summary>At most the end-entity threshold (<c>PkiSettings.CertificateExpiryWarningDays</c>) is left.</summary>
    Expiring,

    Expired,

    NotYetValid,
}

/// <summary>
/// <i>Opublikowane certyfikaty</i> (P1, docs/dev/p-certificate-services.md 2.6) of a user, computer or contact: the
/// certificates of <c>userCertificate</c> and <c>userSMIMECertificate</c> (a PKCS #7 bundle as Outlook publishes it) with
/// <i>Wystawiony dla</i>, <i>Wystawiony przez</i>, the purposes, the validity at the window's clock, the template (resolved by
/// one search of <c>CN=Certificate Templates</c> on the window's DC) and the thumbprint; the selected certificate's details.
/// Read only (rule 5). The values are read when the tab is first shown (the Attribute Editor pattern) and parsed on
/// <see cref="Task.Run(Action)"/>; they stay in memory while the sheet is open (rule 1). <i>Kopiuj do pliku…</i> writes the DER
/// of the selected certificate only to the file the operator picks – there is no temporary <c>.cer</c>.
/// </summary>
public sealed partial class PublishedCertificatesPageViewModel : PropertyPageViewModel
{
    public const string PageKey = L.Certificates.PublishedCertificatesPage;

    // The read that owns the page: a newer one replaces it, and only it ends "loading".
    private OperationScope? _reading;
    private string _dn = string.Empty;
    private bool _activated;
    private bool _readOnce;

    internal PublishedCertificatesPageViewModel(FieldContext context)
        : base(context, PageKey, context.Localizer[PageKey])
    {
    }

    /// <summary>The certificates, <c>userCertificate</c> first, each attribute in server order.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(CountText))]
    public partial IReadOnlyList<PublishedCertificateRowViewModel> Rows { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Details))]
    [NotifyCanExecuteChangedFor(nameof(CopyToFileCommand), nameof(CopyThumbprintCommand))]
    public partial PublishedCertificateRowViewModel? SelectedRow { get; set; }

    /// <summary>The selected certificate's fields (label, value).</summary>
    public IReadOnlyList<CertificateDetailViewModel> Details => SelectedRow?.Details ?? [];

    /// <summary>The page was read and holds no certificate.</summary>
    public bool IsEmpty => _readOnce && Rows.Count == 0;

    /// <summary><i>Opublikowane certyfikaty: 2</i>, or the empty text.</summary>
    public string CountText => !_readOnce ? string.Empty
        : Rows.Count == 0 ? Context.Localizer[L.Certificates.PublishedEmpty]
        : Context.Localizer.Format(L.Certificates.PublishedCount, Rows.Count);

    internal override IEnumerable<string> ReadAttributes => [];

    public override async Task ActivateAsync()
    {
        if (_activated)
        {
            return;
        }

        _activated = true;
        await ReloadAsync().ConfigureAwait(true);
    }

    public override void Revert()
    {
    }

    internal override void Load(DirectoryObject obj) => _dn = obj.DistinguishedName;

    internal override Task LoadDetailsAsync(DirectoryObject obj, CancellationToken cancellationToken) =>
        _activated ? ReloadAsync() : Task.CompletedTask;

    internal override IEnumerable<BatchOperation> GetOperations() => [];

    protected override bool ComputeHasChanges() => false;

    protected override bool ComputeHasErrors() => false;

    /// <summary>"Odśwież": reads the certificates again.</summary>
    [RelayCommand]
    private Task RefreshAsync() => ReloadAsync();

    /// <summary>
    /// "Kopiuj do pliku…": the DER of the selected certificate to a <c>.cer</c> file the operator picks (an operator export,
    /// rule 1). Nothing else is written.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCopyToFile))]
    private async Task CopyToFileAsync()
    {
        if (SelectedRow is not { Der: { } der } row)
        {
            return;
        }

        var localizer = Context.Localizer;
        var title = localizer[L.Certificates.CopyToFileTitle];
        var path = await Context.Services.Files.SaveFileAsync(title, row.SuggestedFileName, [new FileFilter(localizer[L.Certificates.CerFilter], "*.cer")])
            .ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllBytesAsync(path, der).ConfigureAwait(true);
            Context.Services.Notifications.Show(NotificationSeverity.Success, localizer.Format(L.Certificates.Saved, Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Context.Services.Dialogs.ShowErrorAsync(title, Context.Services.Errors.Translate(ex)).ConfigureAwait(true);
        }
    }

    private bool CanCopyToFile() => SelectedRow is { Der: not null };

    /// <summary>"Kopiuj odcisk palca" of the selected certificate.</summary>
    [RelayCommand(CanExecute = nameof(CanCopyThumbprint))]
    private void CopyThumbprint()
    {
        if (SelectedRow is { Thumbprint.Length: > 0 } row)
        {
            ClipboardCopy.Value(Context.Services.Clipboard, Context.Services.Notifications, Context.Localizer, row.Thumbprint, Context.Localizer[L.Certificates.ColumnThumbprint]);
        }
    }

    private bool CanCopyThumbprint() => SelectedRow is { Thumbprint.Length: > 0 };

    private async Task ReloadAsync()
    {
        if (_dn.Length == 0)
        {
            return;
        }

        // The read is the operation of the page (track V, stage V2b): the status line of the sheet says so while the
        // tab is in front, a newer read replaces this one, and the operator can stop it.
        using var scope = BeginReading();
        _reading = scope;
        var token = scope.Token;
        IsLoading = true;
        Error = null;
        Status.SetLasting(null);
        try
        {
            var session = Context.Session;
            var dn = _dn;
            string[] attributes = [.. PublishedCertificates.Attributes.Where(Context.IsKnown)];
            var read = await Task.Run(() => PublishedCertificates.ReadAsync(session, dn, attributes, token), token).ConfigureAwait(true);
            if (scope.IsEnded)
            {
                // Superseded by a newer read while the answer was on its way.
                return;
            }

            if (read is null)
            {
                Error = Context.Localizer[L.PropertySheet.NotFound];
                SetReadFailed(Error, ResultCode.None, RefreshCommand);
                return;
            }

            var selected = SelectedRow?.Thumbprint;
            _readOnce = true;
            Rows = [.. read.Select(c => new PublishedCertificateRowViewModel(Context, c))];
            SelectedRow = Rows.FirstOrDefault(r => selected is not null && string.Equals(r.Thumbprint, selected, StringComparison.Ordinal))
                ?? Rows.FirstOrDefault();

            // The count ("Published certificates: 2", or that there is none) is the summary of the page.
            OnPropertyChanged(nameof(CountText));
            Status.SetIdle(CountText);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Superseded by a newer load - or stopped by the operator: then the page says what it may lack.
            if (!scope.IsEnded)
            {
                scope.Complete(ReadStopped(RefreshCommand));
            }
        }
        catch (DirectoryException ex) when (ReferenceEquals(_reading, scope))
        {
            // The page stays as it was for as long as this lasts: said whenever the tab is in front.
            Error = Context.Services.Errors.Translate(ex);
            SetReadFailed(Error, ResultCode.Of(ex), RefreshCommand);
        }
        catch (DirectoryException)
        {
            // A read that was replaced failed: the newer one owns the page.
        }
        finally
        {
            if (ReferenceEquals(_reading, scope))
            {
                _reading = null;
                IsLoading = false;
            }
        }
    }
}

/// <summary>One published certificate (or one unreadable value) of <see cref="PublishedCertificatesPageViewModel"/>.</summary>
public sealed class PublishedCertificateRowViewModel
{
    internal PublishedCertificateRowViewModel(FieldContext context, PublishedCertificate certificate)
    {
        var localizer = context.Localizer;
        Attribute = certificate.Attribute;
        Der = certificate.Summary is null ? null : certificate.Der;
        if (certificate.Summary is not { } summary)
        {
            IssuedTo = localizer[L.Certificates.Unreadable];
            Details = [new CertificateDetailViewModel(localizer[L.Certificates.ColumnAttribute], Attribute)];
            return;
        }

        Summary = summary;
        var culture = localizer.Culture;
        var zone = context.Services.Time.LocalTimeZone;
        var now = context.Services.Time.GetUtcNow();
        var threshold = context.Services.Settings.Current.Pki.CertificateExpiryWarningDays;
        IssuedTo = certificate.SubjectName;
        IssuedBy = certificate.IssuerName;
        Purposes = summary.Ekus.Count == 0
            ? localizer[L.Certificates.AnyPurpose]
            : string.Join(", ", summary.Ekus.Select(o => PkiFlags.KnownEku.TryGetValue(o, out var name) ? name : o));
        ValidFrom = Date(summary.NotBefore, zone, culture);
        ValidTo = Date(summary.NotAfter, zone, culture);
        DaysLeft = (int)Math.Floor((summary.NotAfter - now).TotalDays);
        var validity = now < summary.NotBefore ? CertificateValidity.NotYetValid
            : now > summary.NotAfter ? CertificateValidity.Expired
            : DaysLeft <= threshold ? CertificateValidity.Expiring
            : CertificateValidity.Valid;
        Validity = validity;
        ValidityText = validity == CertificateValidity.Expiring
            ? localizer.Format(L.Certificates.ValidityKey(validity), Math.Max(DaysLeft, 0))
            : localizer[L.Certificates.ValidityKey(validity)];
        Template = certificate.TemplateName ?? summary.TemplateName ?? summary.TemplateOid ?? string.Empty;
        Thumbprint = summary.Thumbprint;
        SuggestedFileName = FileName(IssuedTo, Thumbprint);

        var details = new List<CertificateDetailViewModel>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                details.Add(new CertificateDetailViewModel(localizer[key], value));
            }
        }

        Add(L.Certificates.DetailSubject, summary.Subject);
        Add(L.Certificates.DetailIssuer, summary.Issuer);
        Add(L.Certificates.DetailSerialNumber, summary.SerialNumber);
        Add(L.Certificates.DetailValidFrom, Moment(summary.NotBefore, zone, culture));
        Add(L.Certificates.DetailValidTo, Moment(summary.NotAfter, zone, culture));
        Add(L.Certificates.ColumnStatus, ValidityText);
        Add(L.Certificates.ColumnPurposes, summary.Ekus.Count == 0
            ? Purposes
            : string.Join(Environment.NewLine, summary.Ekus.Select(o => PkiFlags.KnownEku.TryGetValue(o, out var name) ? $"{name} ({o})" : o)));
        Add(L.Certificates.ColumnTemplate, Template);
        Add(L.Certificates.DetailUpn, string.Join(Environment.NewLine, summary.SanUpn));
        Add(L.Certificates.DetailEmail, string.Join(Environment.NewLine, summary.SanEmail));
        Add(L.Certificates.DetailDns, string.Join(Environment.NewLine, summary.SanDns));
        Add(L.Certificates.DetailSanSid, summary.SanSid);
        Add(L.Certificates.DetailSidExtension, summary.SidExtension);
        Add(L.Certificates.DetailPublicKey, summary.KeySize is { } size
            ? localizer.Format(L.Certificates.KeyWithSize, summary.KeyAlgorithm, size.ToString(culture))
            : summary.KeyAlgorithm);
        Add(L.Certificates.DetailSignatureAlgorithm, summary.SignatureAlgorithm);
        Add(L.Certificates.ColumnThumbprint, summary.Thumbprint);
        Add(L.Certificates.DetailSha256, summary.Sha256);
        Add(L.Certificates.ColumnAttribute, Attribute);
        Details = details;
    }

    /// <summary><c>userCertificate</c> or <c>userSMIMECertificate</c>.</summary>
    public string Attribute { get; }

    /// <summary><i>Wystawiony dla</i>: the subject's simple name.</summary>
    public string IssuedTo { get; }

    /// <summary><i>Wystawiony przez</i>: the issuer's simple name.</summary>
    public string IssuedBy { get; } = string.Empty;

    /// <summary>The EKU names (<c>PkiFlags.KnownEku</c>, else the OID), or <i>wszystkie</i> without an EKU extension.</summary>
    public string Purposes { get; } = string.Empty;

    public string ValidFrom { get; } = string.Empty;

    public string ValidTo { get; } = string.Empty;

    /// <summary>Whole days to <c>NotAfter</c> (negative when expired).</summary>
    public int DaysLeft { get; }

    public CertificateValidity? Validity { get; }

    public string ValidityText { get; } = string.Empty;

    /// <summary>The template's display name (searched by OID or name), else the name or OID the certificate carries.</summary>
    public string Template { get; } = string.Empty;

    /// <summary>SHA-1 of the DER, upper-case hex.</summary>
    public string Thumbprint { get; } = string.Empty;

    /// <summary>The value could not be read as a certificate (the row stays, with the attribute only).</summary>
    public bool IsUnreadable => Summary is null;

    public bool IsExpired => Validity == CertificateValidity.Expired;

    public bool IsExpiring => Validity == CertificateValidity.Expiring;

    public IReadOnlyList<CertificateDetailViewModel> Details { get; }

    internal CertificateSummary? Summary { get; }

    /// <summary>The certificate's DER (for <i>Kopiuj do pliku…</i>), null for an unreadable value.</summary>
    internal byte[]? Der { get; }

    /// <summary>The <c>.cer</c> name offered by <i>Kopiuj do pliku…</i>: <i>Wystawiony dla</i>, else the thumbprint.</summary>
    internal string SuggestedFileName { get; } = string.Empty;

    /// <summary>The validity state only (never a name or thumbprint).</summary>
    public override string ToString() => $"{nameof(PublishedCertificateRowViewModel)}({Validity?.ToString() ?? "unreadable"})";

    private static string Date(DateTimeOffset value, TimeZoneInfo zone, CultureInfo culture) =>
        TimeZoneInfo.ConvertTime(value, zone).ToString("d", culture);

    private static string Moment(DateTimeOffset value, TimeZoneInfo zone, CultureInfo culture) =>
        TimeZoneInfo.ConvertTime(value, zone).ToString("g", culture);

    private static string FileName(string name, string thumbprint)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var clean = new string([.. name.Select(c => invalid.Contains(c) ? '_' : c)]).Trim().TrimEnd('.');
        return (clean.Length == 0 ? thumbprint : clean) + ".cer";
    }
}

/// <summary>One field of the selected certificate (label, value).</summary>
public sealed class CertificateDetailViewModel(string label, string value)
{
    public string Label { get; } = label;

    public string Value { get; } = value;

    /// <summary>The label only (never the value).</summary>
    public override string ToString() => Label;
}

/// <summary>A certificate value of an account as read and parsed (memory only).</summary>
/// <param name="Attribute">The attribute it came from.</param>
/// <param name="Der">The certificate's DER (the value itself, or one certificate of a PKCS #7 bundle).</param>
/// <param name="Summary">Its fields, null when the value is not a readable certificate.</param>
internal sealed record PublishedCertificate(string Attribute, byte[] Der, CertificateSummary? Summary)
{
    /// <summary>The subject's simple name (CN, else the UPN, e-mail or DNS name of the SAN).</summary>
    public string SubjectName { get; init; } = string.Empty;

    /// <summary>The issuer's simple name.</summary>
    public string IssuerName { get; init; } = string.Empty;

    /// <summary>The template's display name found in <c>CN=Certificate Templates</c>, or null.</summary>
    public string? TemplateName { get; init; }

    /// <summary>Whether it parsed only (never a name, the attribute or a thumbprint).</summary>
    public override string ToString() => $"{nameof(PublishedCertificate)}({(Summary is null ? "unreadable" : "certificate")})";
}

/// <summary>Reads and parses the published certificates of one object (no UI types; runs off the UI thread).</summary>
internal static class PublishedCertificates
{
    /// <summary>The attributes of the tab, in display order.</summary>
    public static IReadOnlyList<string> Attributes { get; } = [PkiAttributes.UserCertificate, PkiAttributes.UserSmimeCertificate];

    private const string SignedDataOid = "1.2.840.113549.1.7.2";

    /// <summary>
    /// The certificates of <paramref name="dn"/> (null when the object is gone): every value of <paramref name="attributes"/>
    /// (ranged values completed), PKCS #7 bundles unwrapped (their CA certificates left out when the bundle holds an end-entity
    /// certificate), and the templates named by one search of <c>CN=Certificate Templates</c> – by
    /// <c>msPKI-Cert-Template-OID</c> for the template information extension, by <c>cn</c> for the v1 template name.
    /// </summary>
    public static async Task<IReadOnlyList<PublishedCertificate>?> ReadAsync(IDirectorySession session, string dn, IReadOnlyList<string> attributes, CancellationToken cancellationToken)
    {
        var read = await session.GetAsync(dn, attributes, cancellationToken).ConfigureAwait(false);
        if (read is null)
        {
            return null;
        }

        read = await RangedValues.CompleteAsync(session, read, cancellationToken).ConfigureAwait(false);
        var certificates = new List<PublishedCertificate>();
        foreach (var attribute in attributes)
        {
            if (!read.TryGet(attribute, out var values))
            {
                continue;
            }

            foreach (var value in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                certificates.AddRange(Parse(attribute, value as byte[] ?? System.Text.Encoding.UTF8.GetBytes((string)value)));
            }
        }

        var names = await TemplateNamesAsync(session, certificates, cancellationToken).ConfigureAwait(false);
        return [.. certificates.Select(c => c.Summary is { } s && (s.TemplateOid is { } oid && names.TryGetValue(oid, out var byOid) ? byOid
            : s.TemplateName is { } name && names.TryGetValue(name, out var byName) ? byName : null) is { } found
                ? c with { TemplateName = found }
                : c)];
    }

    /// <summary>One value: a DER certificate, a PKCS #7 SignedData bundle (Outlook's <c>userSMIMECertificate</c>), or unreadable.</summary>
    internal static IEnumerable<PublishedCertificate> Parse(string attribute, byte[] value)
    {
        // A one-byte 0x00 is the placeholder AD keeps in emptied binary attributes: not a certificate, not a row.
        if (value.Length == 0 || value is [0])
        {
            return [];
        }

        if (Certificate(attribute, value) is { } single)
        {
            return [single];
        }

        if (Bundle(value) is { Count: > 0 } bundle)
        {
            var parsed = bundle.Select(der => Certificate(attribute, der)).OfType<PublishedCertificate>().ToList();
            var endEntities = parsed.Where(c => c.Summary is { IsCa: false }).ToList();
            if (parsed.Count > 0)
            {
                return endEntities.Count > 0 ? endEntities : parsed;
            }
        }

        return [new PublishedCertificate(attribute, value, null)];
    }

    private static PublishedCertificate? Certificate(string attribute, byte[] der)
    {
        if (CertificateSummary.Of(der) is not { } summary)
        {
            return null;
        }

        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(der);
            var subject = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            if (string.IsNullOrWhiteSpace(subject))
            {
                // An empty subject (the Domain Controller / Kerberos / Workstation Authentication templates): the SAN names it.
                subject = summary.SanUpn.FirstOrDefault() ?? summary.SanEmail.FirstOrDefault() ?? summary.SanDns.FirstOrDefault() ?? summary.Subject;
            }

            return new PublishedCertificate(attribute, der, summary)
            {
                SubjectName = subject,
                IssuerName = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true) is { Length: > 0 } issuer ? issuer : summary.Issuer,
            };
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The certificates of a PKCS #7 <c>ContentInfo</c> holding <c>SignedData</c> (RFC 5652 5.1), else null.</summary>
    internal static IReadOnlyList<byte[]>? Bundle(byte[] value)
    {
        try
        {
            var contentInfo = new AsnReader(value, AsnEncodingRules.BER).ReadSequence();
            if (contentInfo.ReadObjectIdentifier() != SignedDataOid)
            {
                return null;
            }

            var signedData = contentInfo.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
            _ = signedData.ReadInteger();
            _ = signedData.ReadSetOf();
            _ = signedData.ReadSequence();
            var certificates = new List<byte[]>();
            var tag = new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
            if (signedData.HasData && signedData.PeekTag().HasSameClassAndValue(tag))
            {
                var set = signedData.ReadSetOf(skipSortOrderValidation: true, tag);
                while (set.HasData)
                {
                    // CertificateChoices: only the plain Certificate (a SEQUENCE); the tagged alternatives are skipped.
                    var isCertificate = set.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence);
                    var encoded = set.ReadEncodedValue();
                    if (isCertificate)
                    {
                        certificates.Add(encoded.ToArray());
                    }
                }
            }

            return certificates;
        }
        catch (AsnContentException)
        {
            return null;
        }
    }

    private static async Task<IReadOnlyDictionary<string, string>> TemplateNamesAsync(IDirectorySession session, IReadOnlyList<PublishedCertificate> certificates, CancellationToken cancellationToken)
    {
        var oids = certificates.Select(c => c.Summary?.TemplateOid).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var names = certificates.Where(c => c.Summary?.TemplateOid is null).Select(c => c.Summary?.TemplateName).OfType<string>()
            .Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if ((oids.Count == 0 && names.Count == 0) || session.Info.ConfigurationNamingContext is not { Length: > 0 } configuration)
        {
            return found;
        }

        var search = new DirectorySearch
        {
            BaseDn = DistinguishedName.Parse(configuration).Child("CN", "Services").Child("CN", "Public Key Services").Child("CN", "Certificate Templates").ToString(),
            Scope = SearchScope.OneLevel,
            Filter = LdapFilter.And(
                LdapFilter.Equal(Attr.ObjectClass, PkiClasses.CertificateTemplate),
                LdapFilter.Or([.. oids.Select(o => LdapFilter.Equal(PkiAttributes.TemplateOid, o)), .. names.Select(n => LdapFilter.Equal(Attr.Cn, n))])),
            Attributes = [Attr.Cn, Attr.DisplayName, PkiAttributes.TemplateOid],
            SizeLimit = 100,
        };
        try
        {
            await foreach (var template in session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
            {
                var display = template.GetString(Attr.DisplayName) is { Length: > 0 } shown ? shown : template.GetString(Attr.Cn) ?? template.Name;
                if (template.GetString(PkiAttributes.TemplateOid) is { Length: > 0 } oid)
                {
                    found.TryAdd(oid, display);
                }

                if (template.GetString(Attr.Cn) is { Length: > 0 } cn)
                {
                    found.TryAdd(cn, display);
                }
            }
        }
        catch (DirectoryException)
        {
            // No PKI container, no right to read it, a busy DC: the column shows what the certificate carries.
        }

        return found;
    }
}
