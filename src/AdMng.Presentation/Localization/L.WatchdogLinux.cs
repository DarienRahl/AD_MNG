namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>
    /// Texts of the Watchdog stage B additions (docs/dev/watchdog-linux-design.md 18): the Status sections „Konto usługi”,
    /// „Kolektor WEF”, „Zawartość GPO”, „Host”, the certificate confirmation on first connection, the collector / GPO
    /// content / logon-record settings (sub-tab „Źródła i retencja”) and new value texts. The dynamic families (health codes, tags, reasons, kinds,
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
        public const string HostSystemd = "Watchdog.Ui.HostSystemd";
        public const string CredentialKeyNone = "Watchdog.Ui.CredentialKeyNone";

        // every section
        public const string SectionNotReported = "Watchdog.Ui.SectionNotReported";
        public const string LastCheck = "Watchdog.Ui.LastCheck";
        public const string Principal = "Watchdog.Ui.Principal";
        public const string NotDelegated = "Watchdog.Ui.NotDelegated";
        public const string DenySelfAce = "Watchdog.Ui.DenySelfAce";
        public const string KeytabEncryptionTypes = "Watchdog.Ui.KeytabEncryptionTypes";
        public const string CollectorStored = "Watchdog.Ui.CollectorStored";
        public const string CollectorNoDcs = "Watchdog.Ui.CollectorNoDcs";
        public const string GpoReadProgressValue = "Watchdog.Ui.GpoReadProgressValue";
        public const string GpoReconciliation = "Watchdog.Ui.GpoReconciliation";
        public const string GpoContentDisabled = "Watchdog.Ui.GpoContentDisabled";

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
        public const string CertificateReview = "Watchdog.Certificate.Review";
        public const string CertificateService = "Watchdog.Certificate.Service";
        public const string CertificateNotPinned = "Watchdog.Certificate.NotPinned";
        public const string CertificatePinRemovedOnSave = "Watchdog.Certificate.PinRemovedOnSave";
        public const string CertificatePinHint = "Watchdog.Certificate.PinHint";
        public const string CertificateOutsideValidity = "Watchdog.Certificate.OutsideValidity";
        public const string ConnectionCertificateUntrusted = "Watchdog.Connection.CertificateUntrusted";
        public const string ConnectionCertificateChanged = "Watchdog.Connection.CertificateChanged";

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
        public const string AttributionSettings = "Watchdog.Ui.AttributionSettings";
        public const string AttributionEnabled = "Watchdog.Ui.AttributionEnabled";
        public const string AttributionSourceLabel = "Watchdog.Ui.AttributionSourceLabel";
        public const string AttributionSourceUnsupported = "Watchdog.Ui.AttributionSourceUnsupported";
        public const string RetentionSettings = "Watchdog.Ui.RetentionSettings";
        public const string UnitMinutes = "Watchdog.Ui.UnitMinutes";

        // values (8.4, 9.4)
        public const string ValueUnreadable = "Watchdog.Value.Unreadable";
        public const string ValueAuditSettingsChanged = "Watchdog.Value.AuditSettingsChanged";
        public const string ValueGpoMoreChangesName = "Watchdog.Value.GpoMoreChangesName";
        public const string ValueGpoMoreChanges = "Watchdog.Value.GpoMoreChanges";
    }
}
