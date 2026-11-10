namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>
    /// AD CS (P1, docs/dev/p-certificate-services.md 2.5, 2.8; prefix <c>Pki.</c>): the tree node, the commands, the overview
    /// document with its health and LDAPS tabs and the settings section, and the dynamic families <c>Pki.HealthItem.*</c>,
    /// <c>Pki.Http.*</c> and <c>Pki.UrlKind.*</c> (<see cref="HealthItemKey"/>, <see cref="HttpKey"/>, <see cref="UrlKindKey"/>, <see cref="AllDynamicKeys"/>).
    /// </summary>
    public static class Pki
    {
        public const string NodeTitle = "Pki.NodeTitle";

        public const string NodeHelp = "Pki.NodeHelp";

        public const string OverviewTitle = "Pki.OverviewTitle";

        public const string OverviewCommand = "Pki.OverviewCommand";

        public const string SecurityReviewCommand = "Pki.SecurityReviewCommand";

        public const string SecurityReviewNode = "Pki.SecurityReviewNode";

        public const string ReadinessCommand = "Pki.ReadinessCommand";

        public const string ReadinessNode = "Pki.ReadinessNode";

        public const string ToggleTreeCommand = "Pki.ToggleTreeCommand";

        public const string MenuHeader = "Pki.MenuHeader";

        public const string OpenCommand = "Pki.OpenCommand";

        public const string RefreshCommand = "Pki.RefreshCommand";

        public const string SectionAuthorities = "Pki.SectionAuthorities";

        public const string SectionTrust = "Pki.SectionTrust";

        public const string SectionCrls = "Pki.SectionCrls";

        public const string SectionKra = "Pki.SectionKra";

        public const string SectionOids = "Pki.SectionOids";

        public const string SectionHealth = "Pki.SectionHealth";

        public const string SectionLdaps = "Pki.SectionLdaps";

        /// <summary>{0} enterprise CAs · {1} templates · {2} trusted root CAs · {3} NTAuth certificates</summary>
        public const string Summary = "Pki.Summary";

        /// <summary>Read from {0} at {1}.</summary>
        public const string ReadAt = "Pki.ReadAt";

        /// <summary>Reading the PKI objects: {0}…</summary>
        public const string ReadProgress = "Pki.ReadProgress";

        public const string NoneFound = "Pki.NoneFound";

        public const string None = "Pki.None";

        public const string ReadOnlyNote = "Pki.ReadOnlyNote";

        public const string NoticesHeading = "Pki.NoticesHeading";

        /// <summary>{0}: {1}</summary>
        public const string NoticeLine = "Pki.NoticeLine";

        public const string ColumnName = "Pki.ColumnName";

        public const string ColumnHost = "Pki.ColumnHost";

        public const string ColumnValidTo = "Pki.ColumnValidTo";

        public const string ColumnStatus = "Pki.ColumnStatus";

        public const string ColumnNtAuth = "Pki.ColumnNtAuth";

        public const string ColumnTemplates = "Pki.ColumnTemplates";

        public const string ColumnSubject = "Pki.ColumnSubject";

        public const string ColumnIssuer = "Pki.ColumnIssuer";

        public const string ColumnThumbprint = "Pki.ColumnThumbprint";

        public const string ColumnStore = "Pki.ColumnStore";

        public const string ColumnEnterpriseCa = "Pki.ColumnEnterpriseCa";

        public const string ColumnCa = "Pki.ColumnCa";

        public const string ColumnKind = "Pki.ColumnKind";

        public const string ColumnNumber = "Pki.ColumnNumber";

        public const string ColumnThisUpdate = "Pki.ColumnThisUpdate";

        public const string ColumnNextUpdate = "Pki.ColumnNextUpdate";

        public const string ColumnRevoked = "Pki.ColumnRevoked";

        public const string ColumnAgent = "Pki.ColumnAgent";

        public const string ColumnOid = "Pki.ColumnOid";

        public const string ColumnDisplayName = "Pki.ColumnDisplayName";

        public const string ColumnGroupLink = "Pki.ColumnGroupLink";

        public const string ColumnItem = "Pki.ColumnItem";

        public const string ColumnUrl = "Pki.ColumnUrl";

        public const string ColumnOutcome = "Pki.ColumnOutcome";

        public const string ColumnPort = "Pki.ColumnPort";

        public const string ColumnDc = "Pki.ColumnDc";

        public const string ColumnChecks = "Pki.ColumnChecks";

        public const string ColumnKdc = "Pki.ColumnKdc";

        public const string DetailsHeading = "Pki.DetailsHeading";

        public const string CaDetailsEmpty = "Pki.CaDetailsEmpty";

        public const string CertificateDetailsEmpty = "Pki.CertificateDetailsEmpty";

        public const string CaCertificate = "Pki.CaCertificate";

        public const string Chain = "Pki.Chain";

        /// <summary>{0} (trusted root CA in AD)</summary>
        public const string ChainRootInAd = "Pki.ChainRootInAd";

        /// <summary>{0} (the issuer's certificate is not in AD)</summary>
        public const string ChainIssuerNotFound = "Pki.ChainIssuerNotFound";

        public const string PublishedTemplates = "Pki.PublishedTemplates";

        public const string EnrollmentServers = "Pki.EnrollmentServers";

        /// <summary>{0} to {1}</summary>
        public const string ValidityRange = "Pki.ValidityRange";

        /// <summary>{0} days left</summary>
        public const string DaysLeft = "Pki.DaysLeft";

        /// <summary>expired {0} days ago</summary>
        public const string ExpiredAgo = "Pki.ExpiredAgo";

        public const string NotYetValid = "Pki.NotYetValid";

        public const string Validity = "Pki.Validity";

        public const string Key = "Pki.Key";

        /// <summary>{0} ({1} bits)</summary>
        public const string KeyText = "Pki.KeyText";

        public const string SignatureAlgorithm = "Pki.SignatureAlgorithm";

        public const string SerialNumber = "Pki.SerialNumber";

        public const string Sha256 = "Pki.Sha256";

        /// <summary>Certificates in cACertificate (renewals included): {0}</summary>
        public const string Renewals = "Pki.Renewals";

        public const string InNtAuth = "Pki.InNtAuth";

        public const string NotInNtAuth = "Pki.NotInNtAuth";

        /// <summary>NTAuth certificates: {0} · enterprise CAs in NTAuth: {1} of {2}</summary>
        public const string NtAuthSummary = "Pki.NtAuthSummary";

        public const string OpenComputer = "Pki.OpenComputer";

        public const string RemoteActions = "Pki.RemoteActions";

        public const string ComputerOtherDomain = "Pki.ComputerOtherDomain";

        public const string ComputerUnresolved = "Pki.ComputerUnresolved";

        public const string OpenTemplate = "Pki.OpenTemplate";

        /// <summary>{0} issues logon certificates ({1}) but is not in NTAuth: smart-card and certificate logon with its certificates fails.</summary>
        public const string LogonCaNotInNtAuth = "Pki.LogonCaNotInNtAuth";

        public const string SanText = "Pki.SanText";

        public const string EkuText = "Pki.EkuText";

        public const string CdpText = "Pki.CdpText";

        public const string AiaText = "Pki.AiaText";

        public const string StoreRoot = "Pki.StoreRoot";

        public const string StoreAia = "Pki.StoreAia";

        public const string StoreCross = "Pki.StoreCross";

        public const string StoreNtAuth = "Pki.StoreNtAuth";

        public const string NotEnterpriseCa = "Pki.NotEnterpriseCa";

        public const string CrlBase = "Pki.CrlBase";

        public const string CrlDelta = "Pki.CrlDelta";

        public const string CrlAuthority = "Pki.CrlAuthority";

        public const string Unreadable = "Pki.Unreadable";

        public const string UnknownCa = "Pki.UnknownCa";

        /// <summary>{0} (delta of {1})</summary>
        public const string DeltaOf = "Pki.DeltaOf";

        public const string OidTemplate = "Pki.OidTemplate";

        public const string OidIssuancePolicy = "Pki.OidIssuancePolicy";

        public const string OidApplicationPolicy = "Pki.OidApplicationPolicy";

        /// <summary>Other ({0})</summary>
        public const string OidOther = "Pki.OidOther";

        public const string OpenGroup = "Pki.OpenGroup";

        /// <summary>{0} #{1}</summary>
        public const string HealthItemNumbered = "Pki.HealthItemNumbered";

        public const string CheckHttp = "Pki.CheckHttp";

        public const string CheckHttpHint = "Pki.CheckHttpHint";

        public const string HttpNotChecked = "Pki.HttpNotChecked";

        /// <summary>HTTP URLs checked: {0} ({1} OK).</summary>
        public const string HttpChecked = "Pki.HttpChecked";

        /// <summary>Checking HTTP URLs: {0} of {1}…</summary>
        public const string HttpChecking = "Pki.HttpChecking";

        public const string HttpUnavailable = "Pki.HttpUnavailable";

        public const string HttpNoUrls = "Pki.HttpNoUrls";

        /// <summary>{0} (HTTP {1})</summary>
        public const string HttpStatus = "Pki.HttpStatus";

        public const string HttpResultsHeading = "Pki.HttpResultsHeading";

        /// <summary>OK: {0} · expiring: {1} · expired: {2} · unavailable: {3} · unknown: {4}</summary>
        public const string HealthSummary = "Pki.HealthSummary";

        /// <summary>Expiry warning: certificates {0} days, CA certificates {1} days (Settings → General → Certificates).</summary>
        public const string HealthThresholds = "Pki.HealthThresholds";

        public const string HealthEmpty = "Pki.HealthEmpty";

        public const string ProbeLdaps = "Pki.ProbeLdaps";

        public const string ProbeLdapsHint = "Pki.ProbeLdapsHint";

        public const string LdapsUnavailable = "Pki.LdapsUnavailable";

        public const string LdapsNotChecked = "Pki.LdapsNotChecked";

        public const string LdapsChecking = "Pki.LdapsChecking";

        /// <summary>Ports checked: {0}; OK: {1}; with a problem: {2}.</summary>
        public const string LdapsSummary = "Pki.LdapsSummary";

        public const string LdapsNoDcs = "Pki.LdapsNoDcs";

        public const string CheckNameMismatch = "Pki.CheckNameMismatch";

        /// <summary>the domain name is not in the SAN (ldaps://{0} fails)</summary>
        public const string CheckNoDomainName = "Pki.CheckNoDomainName";

        public const string CheckNoServerAuth = "Pki.CheckNoServerAuth";

        public const string CheckExpired = "Pki.CheckExpired";

        /// <summary>expires in {0} days</summary>
        public const string CheckExpiring = "Pki.CheckExpiring";

        public const string CheckNoAdRoot = "Pki.CheckNoAdRoot";

        public const string CheckIssuerNotInNtAuth = "Pki.CheckIssuerNotInNtAuth";

        public const string CheckOk = "Pki.CheckOk";

        public const string CheckUnparsable = "Pki.CheckUnparsable";

        public const string KdcNote = "Pki.KdcNote";

        /// <summary>Chain status: {0}</summary>
        public const string ChainStatus = "Pki.ChainStatus";

        /// <summary>{0} (global catalog)</summary>
        public const string PortText = "Pki.PortText";

        public const string SettingsSection = "Pki.SettingsSection";

        public const string SettingsCertificateDays = "Pki.SettingsCertificateDays";

        public const string SettingsCaDays = "Pki.SettingsCaDays";

        public const string SettingsHint = "Pki.SettingsHint";

        /// <summary>A health row's item (<c>Pki.HealthItem.&lt;PkiHealthItem&gt;</c>).</summary>
        public static string HealthItemKey(Core.Pki.PkiHealthItem item) => "Pki.HealthItem." + item;

        /// <summary>An HTTP check's outcome (<c>Pki.Http.&lt;PkiHttpOutcome&gt;</c>).</summary>
        public static string HttpKey(Core.Pki.PkiHttpOutcome outcome) => "Pki.Http." + outcome;

        /// <summary>What an HTTP URL is for (<c>Pki.UrlKind.&lt;PkiUrlKind&gt;</c>).</summary>
        public static string UrlKindKey(Core.Pki.PkiUrlKind kind) => "Pki.UrlKind." + kind;

        /// <summary>Every key of the dynamic families (both languages must have them).</summary>
        public static IEnumerable<string> AllDynamicKeys() =>
        [
            .. Enum.GetValues<Core.Pki.PkiHealthItem>().Select(HealthItemKey),
            .. Enum.GetValues<Core.Pki.PkiHttpOutcome>().Select(HttpKey),
            .. Enum.GetValues<Core.Pki.PkiUrlKind>().Select(UrlKindKey),
        ];
    }
}
