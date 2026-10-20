namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>
    /// Texts of the Watchdog stage B additions (docs/dev/watchdog-linux-design.md 18): the Status sections „Konto usługi”,
    /// „Kolektor WEF”, „Zawartość GPO”, „Host”, the certificate confirmation on first connection, the collector / GPO
    /// content / logon-record settings and new value texts. The dynamic families (health codes, tags, reasons, kinds,
    /// attribution sources, keytab results, secrets-key variants) are built by <c>WatchdogDisplay</c>.
    /// </summary>
    public static class WatchdogLinux
    {
        // Status sections (18 item 2)
        public const string CardServiceAccount = "Watchdog.Ui.CardServiceAccount";
        public const string CardCollector = "Watchdog.Ui.CardCollector";
        public const string CardGpoContent = "Watchdog.Ui.CardGpoContent";
        public const string CardHost = "Watchdog.Ui.CardHost";

        // „Konto usługi” (4.7)
        public const string AccountDn = "Watchdog.Ui.AccountDn";
        public const string KeytabKvno = "Watchdog.Ui.KeytabKvno";
        public const string DirectoryKvno = "Watchdog.Ui.DirectoryKvno";
        public const string LastPasswordChange = "Watchdog.Ui.LastPasswordChange";
        public const string NextPasswordChange = "Watchdog.Ui.NextPasswordChange";
        public const string RotationResult = "Watchdog.Ui.RotationResult";
        public const string EncryptionTypes = "Watchdog.Ui.EncryptionTypes";
        public const string PrivilegedMembership = "Watchdog.Ui.PrivilegedMembership";
        public const string KeytabMatchesDirectory = "Watchdog.Ui.KeytabMatchesDirectory";

        // „Kolektor WEF” (7.9)
        public const string CollectorLastRecord = "Watchdog.Ui.CollectorLastRecord";
        public const string CollectorLag = "Watchdog.Ui.CollectorLag";
        public const string CollectorBacklog = "Watchdog.Ui.CollectorBacklog";
        public const string CollectorRejected = "Watchdog.Ui.CollectorRejected";

        // „Zawartość GPO” (8)
        public const string GpoReadProgress = "Watchdog.Ui.GpoReadProgress";
        public const string GpoLastRead = "Watchdog.Ui.GpoLastRead";
        public const string GpoFailures = "Watchdog.Ui.GpoFailures";
        public const string GpoSysvolLag = "Watchdog.Ui.GpoSysvolLag";

        // „Host” (10, 4.7 item 6, 5.1)
        public const string HostOperatingSystem = "Watchdog.Ui.HostOperatingSystem";
        public const string HostRuntime = "Watchdog.Ui.HostRuntime";
        public const string HostCredentialKey = "Watchdog.Ui.HostCredentialKey";
        public const string HostFreeSpace = "Watchdog.Ui.HostFreeSpace";
        public const string HostClockSkew = "Watchdog.Ui.HostClockSkew";
        public const string HostLdapSsf = "Watchdog.Ui.HostLdapSsf";

        // certificate confirmation on first connection (18 item 1)
        public const string CertificateTitle = "Watchdog.Certificate.Title";
        public const string CertificateUntrusted = "Watchdog.Certificate.Untrusted";
        public const string CertificateChanged = "Watchdog.Certificate.Changed";
        public const string CertificateCompare = "Watchdog.Certificate.Compare";
        public const string CertificateFingerprint = "Watchdog.Certificate.Fingerprint";
        public const string CertificatePreviousFingerprint = "Watchdog.Certificate.PreviousFingerprint";
        public const string CertificateNewFingerprint = "Watchdog.Certificate.NewFingerprint";
        public const string CertificateSubject = "Watchdog.Certificate.Subject";
        public const string CertificateDnsNames = "Watchdog.Certificate.DnsNames";
        public const string CertificateValidity = "Watchdog.Certificate.Validity";
        public const string CertificateTrust = "Watchdog.Certificate.Trust";
        public const string CertificatePinned = "Watchdog.Certificate.Pinned";
        public const string CertificateRemovePin = "Watchdog.Certificate.RemovePin";

        // configuration (7.8, 8.5, 7.7)
        public const string DropComputerLogons = "Watchdog.Ui.DropComputerLogons";
        public const string DropAnonymousLogons = "Watchdog.Ui.DropAnonymousLogons";
        public const string CollectorSilenceWarning = "Watchdog.Ui.CollectorSilenceWarning";
        public const string CollectorSilenceError = "Watchdog.Ui.CollectorSilenceError";
        public const string GpoContentEnabled = "Watchdog.Ui.GpoContentEnabled";
        public const string GpoSysvolLagTolerance = "Watchdog.Ui.GpoSysvolLagTolerance";
        public const string GpoMaxFileMb = "Watchdog.Ui.GpoMaxFileMb";
        public const string GpoMaxTotalMb = "Watchdog.Ui.GpoMaxTotalMb";
        public const string GpoMaxFiles = "Watchdog.Ui.GpoMaxFiles";
        public const string LogonRecordsHours = "Watchdog.Ui.LogonRecordsHours";

        // values (8.4, 9.4)
        public const string ValueUnreadable = "Watchdog.Value.Unreadable";
        public const string ValueAuditSettingsChanged = "Watchdog.Value.AuditSettingsChanged";
    }
}
