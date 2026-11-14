using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Model;

namespace AdMng.Core.Pki;

/// <summary>
/// One <c>pKICertificateTemplate</c> object (docs/dev/p-certificate-services.md 2.2), in memory only (CLAUDE.md rule 1).
/// The contract commit fills the identity members (<see cref="Dn"/>, <see cref="ObjectGuid"/>, <see cref="Cn"/>,
/// <see cref="DisplayName"/>, <see cref="SecurityDescriptor"/>); C2 fills the rest in <see cref="Of"/>. Derived members
/// are computed from the masks. <see cref="ToString"/> carries the GUID only.
/// </summary>
public sealed record CertificateTemplate
{
    public required string Dn { get; init; }

    public Guid ObjectGuid { get; init; }

    /// <summary>The template's cn (what <c>certificateTemplates</c> of an enterprise CA lists).</summary>
    public required string Cn { get; init; }

    public string? DisplayName { get; init; }

    /// <summary>The raw <c>nTSecurityDescriptor</c> (owner + DACL); null when it was not returned (no READ_CONTROL – the rights are <i>nieocenione</i>).</summary>
    public byte[]? SecurityDescriptor { get; init; }

    /// <summary><c>msPKI-Template-Schema-Version</c> (1–4; absent = 1).</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary><c>revision</c>.</summary>
    public int Revision { get; init; }

    /// <summary><c>msPKI-Template-Minor-Revision</c>.</summary>
    public int MinorRevision { get; init; }

    /// <summary><c>msPKI-Cert-Template-OID</c>.</summary>
    public string? Oid { get; init; }

    /// <summary><c>pKIExtendedKeyUsage</c> (what Certipy and Locksmith read).</summary>
    public IReadOnlyList<string> Ekus { get; init; } = [];

    /// <summary><c>msPKI-Certificate-Application-Policy</c>.</summary>
    public IReadOnlyList<string> ApplicationPolicies { get; init; } = [];

    /// <summary>
    /// What the CA puts into issued certificates: <see cref="ApplicationPolicies"/> when <see cref="SchemaVersion"/> ≥ 2 and
    /// it is not empty, else <see cref="Ekus"/>.
    /// </summary>
    public IReadOnlyList<string> EffectiveEku => SchemaVersion >= 2 && ApplicationPolicies.Count > 0 ? ApplicationPolicies : Ekus;

    /// <summary><c>pKIKeyUsage</c> (raw bytes).</summary>
    public byte[]? KeyUsage { get; init; }

    /// <summary><c>msPKI-Certificate-Name-Flag</c> (absent = 0).</summary>
    public uint NameFlag { get; init; }

    /// <summary><c>msPKI-Enrollment-Flag</c> (absent = 0).</summary>
    public uint EnrollmentFlag { get; init; }

    /// <summary><c>msPKI-Private-Key-Flag</c> (absent = 0).</summary>
    public uint PrivateKeyFlag { get; init; }

    /// <summary>The general <c>flags</c> (absent = 0).</summary>
    public uint GeneralFlag { get; init; }

    /// <summary><c>msPKI-RA-Signature</c>: authorized signatures required (absent = 0).</summary>
    public int RaSignatures { get; init; }

    /// <summary>The application-policy OIDs of <c>msPKI-RA-Application-Policies</c> an authorized signature must carry.</summary>
    public IReadOnlyList<string> RaApplicationPolicies { get; init; } = [];

    /// <summary>The cryptography settings of a schema-4 <c>msPKI-RA-Application-Policies</c> (<c>msPKI-Asymmetric-Algorithm</c> …, name → value).</summary>
    public IReadOnlyDictionary<string, string> CryptoSettings { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary><c>msPKI-RA-Policies</c>: issuance policies an authorized signature must carry.</summary>
    public IReadOnlyList<string> RaIssuancePolicies { get; init; } = [];

    /// <summary><c>msPKI-Certificate-Policy</c>: the issuance policies of issued certificates (ESC13).</summary>
    public IReadOnlyList<string> IssuancePolicies { get; init; } = [];

    /// <summary><c>pKIExpirationPeriod</c>.</summary>
    public PkiPeriod? Validity { get; init; }

    /// <summary><c>pKIOverlapPeriod</c>.</summary>
    public PkiPeriod? RenewalOverlap { get; init; }

    /// <summary><c>msPKI-Minimal-Key-Size</c>.</summary>
    public int? MinimalKeySize { get; init; }

    /// <summary>
    /// <c>pKIDefaultCSPs</c>: the provider names in priority order (each stored value is <c>&lt;priority&gt;,&lt;name&gt;</c>; the
    /// prefix is removed, a value without one keeps its place after the numbered ones).
    /// </summary>
    public IReadOnlyList<string> CryptoProviders { get; init; } = [];

    /// <summary><c>msPKI-Supersede-Templates</c>.</summary>
    public IReadOnlyList<string> SupersededTemplates { get; init; } = [];

    /// <summary>The enrollee supplies the subject (name flag <c>0x1</c>, <c>CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT</c>).</summary>
    public bool EnrolleeSuppliesSubject => (NameFlag & 0x1) != 0;

    /// <summary>CA certificate manager approval (enrollment flag <c>0x2</c>, <c>CT_FLAG_PEND_ALL_REQUESTS</c>).</summary>
    public bool RequiresManagerApproval => (EnrollmentFlag & 0x2) != 0;

    /// <summary>No SID security extension in issued certificates (enrollment flag <c>0x80000</c>, ESC9).</summary>
    public bool NoSecurityExtension => (EnrollmentFlag & 0x80000) != 0;

    /// <summary>At least one authorized signature is required (<see cref="RaSignatures"/> ≥ 1).</summary>
    public bool RequiresAuthorizedSignature => RaSignatures >= 1;

    /// <summary>The raw mask of one flag attribute.</summary>
    public uint Mask(PkiFlagAttribute attribute) => attribute switch
    {
        PkiFlagAttribute.NameFlag => NameFlag,
        PkiFlagAttribute.EnrollmentFlag => EnrollmentFlag,
        PkiFlagAttribute.PrivateKeyFlag => PrivateKeyFlag,
        PkiFlagAttribute.GeneralFlag => GeneralFlag,
        _ => throw new ArgumentOutOfRangeException(nameof(attribute), attribute, "Not a template flag attribute."),
    };

    /// <summary>One flag attribute decoded with <see cref="PkiFlags.Decode"/> (the general <c>flags</c> by the template table – this is a template).</summary>
    public PkiDecodedFlags DecodeFlags(PkiFlagAttribute attribute) => PkiFlags.Decode(attribute, Mask(attribute));

    /// <summary>The minimum CA version of <c>msPKI-Private-Key-Flag</c> (bits 16–19; 0 on schema-1 templates).</summary>
    public int MinimumCaVersion => (int)((PrivateKeyFlag & PkiFlags.MinimumCaVersion.Mask) >> PkiFlags.MinimumCaVersion.Shift);

    /// <summary>The minimum client version of <c>msPKI-Private-Key-Flag</c> (bits 24–27; 0 on schema-1 templates).</summary>
    public int MinimumClientVersion => (int)((PrivateKeyFlag & PkiFlags.MinimumClientVersion.Mask) >> PkiFlags.MinimumClientVersion.Shift);

    /// <summary>
    /// Builds the record from one <c>pKICertificateTemplate</c> object read with <see cref="PkiAttributes.TemplateAttributes"/>
    /// (2.2): multi-valued strings with <c>GetStrings</c> (trimmed, empty and repeated values dropped, server order kept), the
    /// masks with <c>GetInt32</c> cast to <c>uint</c> (the negative <c>msPKI-*</c> masks parse; absent = 0), the schema version
    /// absent = 1, <c>msPKI-RA-Signature</c> absent = 0, <c>msPKI-RA-Application-Policies</c> in both formats, the periods
    /// with <see cref="PkiPeriod.Decode"/>. Never throws on template data: a malformed value reads as absent.
    /// </summary>
    public static CertificateTemplate Of(DirectoryObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var (raPolicies, cryptoSettings) = ParseRaApplicationPolicies(obj.GetStrings(PkiAttributes.RaApplicationPolicies));
        return new CertificateTemplate
        {
            Dn = obj.DistinguishedName,
            ObjectGuid = obj.ObjectGuid ?? Guid.Empty,
            Cn = obj.GetString(Attr.Cn) ?? obj.Dn.Name,
            DisplayName = obj.GetString(Attr.DisplayName),
            SecurityDescriptor = obj.GetBytes(Attr.NtSecurityDescriptor),
            SchemaVersion = obj.GetInt32(PkiAttributes.TemplateSchemaVersion) is { } schema && schema >= 1 ? schema : 1,
            Revision = obj.GetInt32(PkiAttributes.Revision) ?? 0,
            MinorRevision = obj.GetInt32(PkiAttributes.TemplateMinorRevision) ?? 0,
            Oid = Values(obj, PkiAttributes.TemplateOid) is [var oid, ..] ? oid : null,
            Ekus = Values(obj, PkiAttributes.ExtendedKeyUsage),
            ApplicationPolicies = Values(obj, PkiAttributes.ApplicationPolicy),
            KeyUsage = obj.GetBytes(PkiAttributes.KeyUsage),
            NameFlag = MaskOf(obj, PkiAttributes.CertificateNameFlag),
            EnrollmentFlag = MaskOf(obj, PkiAttributes.EnrollmentFlag),
            PrivateKeyFlag = MaskOf(obj, PkiAttributes.PrivateKeyFlag),
            GeneralFlag = MaskOf(obj, PkiAttributes.Flags),
            RaSignatures = obj.GetInt32(PkiAttributes.RaSignature) ?? 0,
            RaApplicationPolicies = raPolicies,
            CryptoSettings = cryptoSettings,
            RaIssuancePolicies = Values(obj, PkiAttributes.RaPolicies),
            IssuancePolicies = Values(obj, PkiAttributes.CertificatePolicy),
            Validity = Period(obj, PkiAttributes.ExpirationPeriod),
            RenewalOverlap = Period(obj, PkiAttributes.OverlapPeriod),
            MinimalKeySize = obj.GetInt32(PkiAttributes.MinimalKeySize),
            CryptoProviders = Providers(Values(obj, PkiAttributes.DefaultCsps)),
            SupersededTemplates = Values(obj, PkiAttributes.SupersedeTemplates),
        };
    }

    /// <summary>
    /// Splits <c>msPKI-RA-Application-Policies</c> (2.2): a schema-4 value is a backquote-delimited list of
    /// <c>name`type`value</c> triples (usually ending with a backquote) – the <c>msPKI-RA-Application-Policies</c> triples are
    /// the OIDs (a value may list several, comma-separated), every other name (<c>msPKI-Asymmetric-Algorithm</c>,
    /// <c>msPKI-Hash-Algorithm</c>, <c>msPKI-Key-Usage</c>, <c>msPKI-Symmetric-Algorithm</c> …) is a cryptography setting
    /// (first value wins); an older value is one plain OID. The format is chosen by the value, not by the schema version.
    /// An incomplete trailing triple is ignored.
    /// </summary>
    public static (IReadOnlyList<string> Policies, IReadOnlyDictionary<string, string> CryptoSettings) ParseRaApplicationPolicies(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var policies = new List<string>();
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in values)
        {
            var value = raw?.Trim() ?? string.Empty;
            if (value.Length == 0)
            {
                continue;
            }

            if (!value.Contains('`', StringComparison.Ordinal))
            {
                AddOnce(policies, value);
                continue;
            }

            var parts = (value.EndsWith('`') ? value[..^1] : value).Split('`');
            for (var i = 0; i + 2 < parts.Length; i += 3)
            {
                var name = parts[i].Trim();
                var setting = parts[i + 2].Trim();
                if (name.Equals(PkiAttributes.RaApplicationPolicies, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var oid in setting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        AddOnce(policies, oid);
                    }
                }
                else if (name.Length > 0)
                {
                    settings.TryAdd(name, setting);
                }
            }
        }

        return (policies, settings);
    }

    /// <summary>The values of a multi-valued string attribute: trimmed, empty and repeated (ordinal) values dropped, server order kept.</summary>
    private static List<string> Values(DirectoryObject obj, string attribute)
    {
        var values = new List<string>();
        foreach (var value in obj.GetStrings(attribute))
        {
            var trimmed = value.Trim();
            if (trimmed.Length > 0)
            {
                AddOnce(values, trimmed);
            }
        }

        return values;
    }

    private static void AddOnce(List<string> list, string value)
    {
        if (!list.Contains(value, StringComparer.Ordinal))
        {
            list.Add(value);
        }
    }

    private static uint MaskOf(DirectoryObject obj, string attribute) => unchecked((uint)(obj.GetInt32(attribute) ?? 0));

    private static PkiPeriod? Period(DirectoryObject obj, string attribute) =>
        obj.GetBytes(attribute) is { } value ? PkiPeriod.Decode(value) : null;

    /// <summary><c>pKIDefaultCSPs</c> values <c>&lt;priority&gt;,&lt;name&gt;</c> → the names by priority (unnumbered after them, in server order).</summary>
    private static List<string> Providers(List<string> values)
    {
        var providers = new List<(int Priority, int Index, string Name)>(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];
            var comma = value.IndexOf(',', StringComparison.Ordinal);
            if (comma > 0 && int.TryParse(value.AsSpan(0, comma).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var priority))
            {
                var name = value[(comma + 1)..].Trim();
                if (name.Length > 0)
                {
                    providers.Add((priority, i, name));
                }
            }
            else
            {
                providers.Add((int.MaxValue, i, value));
            }
        }

        var names = new List<string>(providers.Count);
        foreach (var (_, _, name) in providers.OrderBy(p => p.Priority).ThenBy(p => p.Index))
        {
            AddOnce(names, name);
        }

        return names;
    }

    /// <summary>The GUID only (never a name, DN or flag value).</summary>
    public override string ToString() => $"{nameof(CertificateTemplate)} {ObjectGuid:B}";
}
