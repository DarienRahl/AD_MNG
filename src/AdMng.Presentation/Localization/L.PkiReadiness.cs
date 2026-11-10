namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>
    /// <i>Gotowość na silne mapowanie i NTAuth</i> (P1, docs/dev/p-certificate-services.md 2.5, W6): the strong-mapping and NTAuth
    /// readiness report of the PKI area (not the stage-S readiness check) – its areas, statuses, row texts and actions. The
    /// TLS outcomes and KDC states are the shared <c>Pki.Tls.*</c> / <c>Pki.Kdc.*</c> families of <c>PkiTextKeys</c>.
    /// </summary>
    public static class PkiReadiness
    {
        public const string Title = "PkiReady.Title";
        public const string CheckAgain = "PkiReady.CheckAgain";
        public const string CheckAgainHint = "PkiReady.CheckAgainHint";
        public const string CheckLdaps = "PkiReady.CheckLdaps";
        public const string CheckLdapsHint = "PkiReady.CheckLdapsHint";
        public const string Copy = "PkiReady.Copy";
        public const string Copied = "PkiReady.Copied";
        public const string ExportCsv = "PkiReady.ExportCsv";
        public const string ReadOnlyNote = "PkiReady.ReadOnlyNote";
        public const string NeverRun = "PkiReady.NeverRun";

        /// <summary>{0} = the time, {1} = the DC.</summary>
        public const string LastRun = "PkiReady.LastRun";
        public const string StageSnapshot = "PkiReady.StageSnapshot";
        public const string StageDomainControllers = "PkiReady.StageDomainControllers";

        /// <summary>{0} = the accounts streamed so far.</summary>
        public const string StageAccounts = "PkiReady.StageAccounts";
        public const string StageLdaps = "PkiReady.StageLdaps";

        /// <summary>{0} = the count (the summary line, only the statuses that occur).</summary>
        public const string SummaryOk = "PkiReady.Summary.Ok";
        public const string SummaryWarning = "PkiReady.Summary.Warning";
        public const string SummaryProblem = "PkiReady.Summary.Problem";
        public const string SummaryNotChecked = "PkiReady.Summary.NotChecked";
        public const string ColumnArea = "PkiReady.ColumnArea";
        public const string ColumnStatus = "PkiReady.ColumnStatus";
        public const string ColumnItem = "PkiReady.ColumnItem";
        public const string ColumnDetail = "PkiReady.ColumnDetail";
        public const string ColumnRemedy = "PkiReady.ColumnRemedy";

        /// <summary>{0} = what to do.</summary>
        public const string RemedyLine = "PkiReady.RemedyLine";

        /// <summary>{0} = the number of further rows (shown in the export).</summary>
        public const string MoreRows = "PkiReady.MoreRows";
        public const string MoreRowsDetail = "PkiReady.MoreRowsDetail";
        public const string OpenAccount = "PkiReady.OpenAccount";
        public const string OpenTemplate = "PkiReady.OpenTemplate";
        public const string OpenCa = "PkiReady.OpenCa";
        public const string NoPki = "PkiReady.NoPki";

        /// <summary>{0} = the failure.</summary>
        public const string ReadFailed = "PkiReady.ReadFailed";
        public const string Reading = "PkiReady.Reading";

        // Mappings
        public const string MappingsTitle = "PkiReady.Mappings.Title";
        public const string MappingsDescription = "PkiReady.Mappings.Description";

        /// <summary>{0} = the weak mapping kinds.</summary>
        public const string MappingsWeak = "PkiReady.Mappings.Weak";
        public const string MappingsWeakRemedy = "PkiReady.Mappings.WeakRemedy";
        public const string MappingsInvalid = "PkiReady.Mappings.Invalid";
        public const string MappingsInvalidRemedy = "PkiReady.Mappings.InvalidRemedy";
        public const string MappingsTier0 = "PkiReady.Mappings.Tier0";
        public const string MappingsDisabled = "PkiReady.Mappings.Disabled";

        /// <summary>{0} = the number of accounts with strong mappings only.</summary>
        public const string MappingsStrong = "PkiReady.Mappings.Strong";
        public const string MappingsStrongDetail = "PkiReady.Mappings.StrongDetail";
        public const string MappingsNone = "PkiReady.Mappings.None";
        public const string MappingsNoneDetail = "PkiReady.Mappings.NoneDetail";

        // Authentication templates
        public const string TemplatesTitle = "PkiReady.Templates.Title";
        public const string TemplatesDescription = "PkiReady.Templates.Description";
        public const string TemplatesNoExtension = "PkiReady.Templates.NoExtension";
        public const string TemplatesNoExtensionRemedy = "PkiReady.Templates.NoExtensionRemedy";
        public const string TemplatesSupplied = "PkiReady.Templates.Supplied";
        public const string TemplatesSuppliedRemedy = "PkiReady.Templates.SuppliedRemedy";

        /// <summary>{0} = the number of published authentication templates that carry the SID extension.</summary>
        public const string TemplatesOk = "PkiReady.Templates.Ok";
        public const string TemplatesOkDetail = "PkiReady.Templates.OkDetail";
        public const string TemplatesNone = "PkiReady.Templates.None";
        public const string TemplatesNoneDetail = "PkiReady.Templates.NoneDetail";

        // NTAuth
        public const string NtAuthTitle = "PkiReady.NtAuth.Title";
        public const string NtAuthDescription = "PkiReady.NtAuth.Description";
        public const string NtAuthMissing = "PkiReady.NtAuth.Missing";

        /// <summary>{0} = the logon templates the CA publishes.</summary>
        public const string NtAuthNotIn = "PkiReady.NtAuth.NotIn";
        public const string NtAuthNotInRemedy = "PkiReady.NtAuth.NotInRemedy";
        public const string NtAuthIn = "PkiReady.NtAuth.In";
        public const string NtAuthNotNeeded = "PkiReady.NtAuth.NotNeeded";
        public const string NtAuthNoCa = "PkiReady.NtAuth.NoCa";
        public const string NtAuthNoCaDetail = "PkiReady.NtAuth.NoCaDetail";

        // DC LDAPS
        public const string LdapsTitle = "PkiReady.Ldaps.Title";
        public const string LdapsDescription = "PkiReady.Ldaps.Description";
        public const string LdapsNotRun = "PkiReady.Ldaps.NotRun";
        public const string LdapsNotRunDetail = "PkiReady.Ldaps.NotRunDetail";
        public const string LdapsUnavailable = "PkiReady.Ldaps.Unavailable";
        public const string LdapsUnavailableDetail = "PkiReady.Ldaps.UnavailableDetail";
        public const string LdapsNoDc = "PkiReady.Ldaps.NoDc";

        /// <summary>{0} = the DC, {1} = the port.</summary>
        public const string LdapsHost = "PkiReady.Ldaps.Host";
        public const string LdapsUnreadable = "PkiReady.Ldaps.Unreadable";
        public const string LdapsNameMismatch = "PkiReady.Ldaps.NameMismatch";
        public const string LdapsNoServerAuth = "PkiReady.Ldaps.NoServerAuth";
        public const string LdapsNotTimeValid = "PkiReady.Ldaps.NotTimeValid";
        public const string LdapsNotChained = "PkiReady.Ldaps.NotChained";
        public const string LdapsNoDomainName = "PkiReady.Ldaps.NoDomainName";

        /// <summary>{0} = the days left.</summary>
        public const string LdapsExpiring = "PkiReady.Ldaps.Expiring";

        /// <summary>{0} = the issuer, {1} = valid until, {2} = the days left.</summary>
        public const string LdapsOk = "PkiReady.Ldaps.Ok";
        public const string LdapsRemedy = "PkiReady.Ldaps.Remedy";

        // KDC
        public const string KdcTitle = "PkiReady.Kdc.Title";
        public const string KdcDescription = "PkiReady.Kdc.Description";
        public const string KdcNoDc = "PkiReady.Kdc.NoDc";

        /// <summary>{0} = the issuer, {1} = valid until.</summary>
        public const string KdcCertificate = "PkiReady.Kdc.Certificate";

        /// <summary>{0} = the days left.</summary>
        public const string KdcExpiring = "PkiReady.Kdc.Expiring";
        public const string KdcNoKdcEkuDetail = "PkiReady.Kdc.NoKdcEkuDetail";
        public const string KdcRemedy = "PkiReady.Kdc.Remedy";
        public const string KdcReadOnly = "PkiReady.Kdc.ReadOnly";

        // KDC events (P5)
        public const string EventsTitle = "PkiReady.Events.Title";
        public const string EventsRow = "PkiReady.Events.Row";
        public const string EventsDetail = "PkiReady.Events.Detail";
    }
}
