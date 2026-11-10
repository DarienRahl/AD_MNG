using AdMng.Core.Pki;

namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>
    /// Certificates on accounts (P1, docs/dev/p-certificate-services.md 2.6, 2.8): the read-only property-sheet tabs
    /// <i>Opublikowane certyfikaty</i>, <i>Mapowania nazw</i> and <i>Poświadczenia kluczy</i>, their page titles, and the reason of
    /// the attribute editor's masked rows. Dynamic families (<see cref="AllKeys"/>): <c>Cert.KeySource.&lt;KeyCredentialSource&gt;</c>,
    /// <c>Cert.KeyUsage.&lt;KeyCredentialUsage&gt;</c>, <c>Cert.Validity.&lt;CertificateValidity&gt;</c>. The mapping strengths and
    /// kinds (<c>Cert.Mapping.*</c>, <c>Cert.MappingKind.*</c>) are in the contract's <c>PkiTextKeys</c>.
    /// </summary>
    public static class Certificates
    {
        public const string PublishedCertificatesPage = "Cert.Page.PublishedCertificates";
        public const string NameMappingsPage = "Cert.Page.NameMappings";
        public const string KeyCredentialsPage = "Cert.Page.KeyCredentials";

        /// <summary>The read-only reason of a masked row of the attribute editor (key material, decision 7).</summary>
        public const string MaskedReadOnly = "Cert.MaskedReadOnly";

        public const string PublishedIntro = "Cert.Published.Intro";
        public const string ColumnIssuedTo = "Cert.Published.ColumnIssuedTo";
        public const string ColumnIssuedBy = "Cert.Published.ColumnIssuedBy";
        public const string ColumnPurposes = "Cert.Published.ColumnPurposes";
        public const string ColumnValidTo = "Cert.Published.ColumnValidTo";
        public const string ColumnStatus = "Cert.Published.ColumnStatus";
        public const string ColumnTemplate = "Cert.Published.ColumnTemplate";
        public const string ColumnThumbprint = "Cert.Published.ColumnThumbprint";
        public const string ColumnAttribute = "Cert.Published.ColumnAttribute";

        /// <summary>{0} = the number of certificates.</summary>
        public const string PublishedCount = "Cert.Published.Count";
        public const string PublishedEmpty = "Cert.Published.Empty";

        /// <summary>The purpose of a certificate without an EKU extension.</summary>
        public const string AnyPurpose = "Cert.Published.AnyPurpose";

        /// <summary>A value of <c>userCertificate</c> / <c>userSMIMECertificate</c> that holds no readable certificate.</summary>
        public const string Unreadable = "Cert.Published.Unreadable";
        public const string CopyToFile = "Cert.Published.CopyToFile";
        public const string CopyToFileTitle = "Cert.Published.CopyToFileTitle";
        public const string CerFilter = "Cert.Published.CerFilter";

        /// <summary>{0} = the file name.</summary>
        public const string Saved = "Cert.Published.Saved";
        public const string CopyThumbprint = "Cert.Published.CopyThumbprint";
        public const string DetailsHeader = "Cert.Published.DetailsHeader";

        public const string DetailSubject = "Cert.Detail.Subject";
        public const string DetailIssuer = "Cert.Detail.Issuer";
        public const string DetailSerialNumber = "Cert.Detail.SerialNumber";
        public const string DetailValidFrom = "Cert.Detail.ValidFrom";
        public const string DetailValidTo = "Cert.Detail.ValidTo";
        public const string DetailUpn = "Cert.Detail.Upn";
        public const string DetailEmail = "Cert.Detail.Email";
        public const string DetailDns = "Cert.Detail.Dns";
        public const string DetailSanSid = "Cert.Detail.SanSid";
        public const string DetailSidExtension = "Cert.Detail.SidExtension";
        public const string DetailPublicKey = "Cert.Detail.PublicKey";
        public const string DetailSignatureAlgorithm = "Cert.Detail.SignatureAlgorithm";
        public const string DetailSha256 = "Cert.Detail.Sha256";

        /// <summary>{0} = the algorithm, {1} = the key size in bits.</summary>
        public const string KeyWithSize = "Cert.Detail.KeyWithSize";

        public const string MappingsIntro = "Cert.Mappings.Intro";
        public const string X509Header = "Cert.Mappings.X509Header";
        public const string KerberosHeader = "Cert.Mappings.KerberosHeader";
        public const string ColumnMapping = "Cert.Mappings.ColumnMapping";
        public const string ColumnStrength = "Cert.Mappings.ColumnStrength";
        public const string ColumnIssuer = "Cert.Mappings.ColumnIssuer";
        public const string ColumnSubjectOrIdentifier = "Cert.Mappings.ColumnSubjectOrIdentifier";
        public const string ColumnValue = "Cert.Mappings.ColumnValue";
        public const string ColumnPrincipal = "Cert.Mappings.ColumnPrincipal";
        public const string NoX509Mappings = "Cert.Mappings.NoX509";
        public const string NoKerberosNames = "Cert.Mappings.NoKerberos";

        /// <summary>{0} = the serial number (certificate byte order).</summary>
        public const string SerialNumber = "Cert.Mappings.SerialNumber";

        /// <summary>{0} = the number of weak mappings.</summary>
        public const string WeakWarning = "Cert.Mappings.WeakWarning";

        /// <summary>{0} = the number of invalid values.</summary>
        public const string InvalidWarning = "Cert.Mappings.InvalidWarning";

        public const string KeysIntro = "Cert.Keys.Intro";
        public const string KeysWarning = "Cert.Keys.Warning";
        public const string ColumnKeyId = "Cert.Keys.ColumnKeyId";
        public const string ColumnDeviceId = "Cert.Keys.ColumnDeviceId";
        public const string ColumnSource = "Cert.Keys.ColumnSource";
        public const string ColumnUsage = "Cert.Keys.ColumnUsage";
        public const string ColumnCreated = "Cert.Keys.ColumnCreated";
        public const string ColumnLastLogon = "Cert.Keys.ColumnLastLogon";
        public const string ColumnRemarks = "Cert.Keys.ColumnRemarks";

        /// <summary>{0} = the number of key credentials.</summary>
        public const string KeysCount = "Cert.Keys.Count";
        public const string KeysEmpty = "Cert.Keys.Empty";
        public const string KeyMalformed = "Cert.Keys.Malformed";
        public const string KeyHashMismatch = "Cert.Keys.HashMismatch";
        public const string CopyKeyId = "Cert.Keys.CopyKeyId";
        public const string CopyDeviceId = "Cert.Keys.CopyDeviceId";

        /// <summary><c>Cert.KeySource.&lt;KeyCredentialSource&gt;</c>.</summary>
        public static string KeySourceKey(KeyCredentialSource source) => "Cert.KeySource." + source;

        /// <summary><c>Cert.KeyUsage.&lt;KeyCredentialUsage&gt;</c>.</summary>
        public static string KeyUsageKey(KeyCredentialUsage usage) => "Cert.KeyUsage." + usage;

        /// <summary><c>Cert.Validity.&lt;CertificateValidity&gt;</c> (<c>Expiring</c> takes {0}: the whole days left).</summary>
        public static string ValidityKey(global::AdMng.Presentation.Pki.CertificateValidity validity) => "Cert.Validity." + validity;

        /// <summary>Every key of the dynamic families (the localization tests check both languages).</summary>
        public static IEnumerable<string> AllKeys() =>
        [
            .. Enum.GetValues<KeyCredentialSource>().Select(KeySourceKey),
            .. Enum.GetValues<KeyCredentialUsage>().Select(KeyUsageKey),
            .. Enum.GetValues<global::AdMng.Presentation.Pki.CertificateValidity>().Select(ValidityKey),
        ];
    }
}
