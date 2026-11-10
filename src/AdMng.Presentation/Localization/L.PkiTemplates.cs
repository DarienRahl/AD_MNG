namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>
    /// Certificate templates (P1, docs/dev/p-certificate-services.md 2.2, 2.3, 2.5, 2.8; prefix <c>PkiTpl.</c>): the templates
    /// document, its columns, the enrollment rights and the compare pane, and the dynamic families <c>PkiTpl.Flag.*</c> (one per
    /// bit of <c>PkiFlags.Table</c>), <c>PkiTpl.FlagGroup.*</c>, <c>PkiTpl.Field.*</c> and <c>PkiTpl.Unit.*</c> (<see cref="AllDynamicKeys"/>).
    /// </summary>
    public static class PkiTemplates
    {
        public const string Title = "PkiTpl.Title";

        public const string Command = "PkiTpl.Command";

        public const string SearchPlaceholder = "PkiTpl.SearchPlaceholder";

        public const string UnpublishedOnly = "PkiTpl.UnpublishedOnly";

        /// <summary>{0} templates, {1} not issued by any CA</summary>
        public const string Summary = "PkiTpl.Summary";

        /// <summary>{0} of {1} templates shown</summary>
        public const string Filtered = "PkiTpl.Filtered";

        public const string ColumnName = "PkiTpl.ColumnName";

        public const string ColumnCn = "PkiTpl.ColumnCn";

        public const string ColumnSchema = "PkiTpl.ColumnSchema";

        public const string ColumnVersion = "PkiTpl.ColumnVersion";

        public const string ColumnPurpose = "PkiTpl.ColumnPurpose";

        public const string ColumnValidity = "PkiTpl.ColumnValidity";

        public const string ColumnRenewal = "PkiTpl.ColumnRenewal";

        public const string ColumnKey = "PkiTpl.ColumnKey";

        public const string ColumnSubject = "PkiTpl.ColumnSubject";

        public const string ColumnApproval = "PkiTpl.ColumnApproval";

        public const string ColumnSignatures = "PkiTpl.ColumnSignatures";

        public const string ColumnPublish = "PkiTpl.ColumnPublish";

        public const string ColumnSuperseded = "PkiTpl.ColumnSuperseded";

        public const string ColumnIssuancePolicies = "PkiTpl.ColumnIssuancePolicies";

        public const string ColumnCas = "PkiTpl.ColumnCas";

        public const string ColumnEnroll = "PkiTpl.ColumnEnroll";

        public const string SubjectFromRequest = "PkiTpl.SubjectFromRequest";

        public const string SubjectFromAd = "PkiTpl.SubjectFromAd";

        public const string AnyPurpose = "PkiTpl.AnyPurpose";

        public const string NotIssued = "PkiTpl.NotIssued";

        /// <summary>{0} bits</summary>
        public const string KeyBits = "PkiTpl.KeyBits";

        /// <summary>{0}, {1}</summary>
        public const string ValueList = "PkiTpl.ValueList";

        /// <summary>{0}.{1}</summary>
        public const string Version = "PkiTpl.Version";

        public const string DetailsEmpty = "PkiTpl.DetailsEmpty";

        public const string General = "PkiTpl.General";

        public const string Oid = "PkiTpl.Oid";

        public const string ApplicationPolicies = "PkiTpl.ApplicationPolicies";

        public const string Ekus = "PkiTpl.Ekus";

        public const string RaPolicies = "PkiTpl.RaPolicies";

        public const string CryptoProviders = "PkiTpl.CryptoProviders";

        public const string FlagsHeading = "PkiTpl.FlagsHeading";

        public const string NoFlags = "PkiTpl.NoFlags";

        /// <summary>Unknown bits: {0}</summary>
        public const string UnknownBits = "PkiTpl.UnknownBits";

        /// <summary>{0} (obsolete, ignored)</summary>
        public const string ReservedFlag = "PkiTpl.ReservedFlag";

        public const string EnrollHeading = "PkiTpl.EnrollHeading";

        public const string ModifyHeading = "PkiTpl.ModifyHeading";

        public const string NotEvaluated = "PkiTpl.NotEvaluated";

        /// <summary>Counting accounts: {0}…</summary>
        public const string Counting = "PkiTpl.Counting";

        public const string CountingStarted = "PkiTpl.CountingStarted";

        /// <summary>The accounts could not be counted: {0}</summary>
        public const string CountFailed = "PkiTpl.CountFailed";

        /// <summary>Accounts counted: {0}.</summary>
        public const string CountsDone = "PkiTpl.CountsDone";

        /// <summary>{0} accounts</summary>
        public const string AccountCount = "PkiTpl.AccountCount";

        /// <summary>{0} accounts (+ {1} disabled)</summary>
        public const string AccountCountDisabled = "PkiTpl.AccountCountDisabled";

        public const string CountUnknown = "PkiTpl.CountUnknown";

        public const string CountPending = "PkiTpl.CountPending";

        public const string Nobody = "PkiTpl.Nobody";

        public const string Total = "PkiTpl.Total";

        public const string FirstAccounts = "PkiTpl.FirstAccounts";

        /// <summary>{0} (disabled)</summary>
        public const string Disabled = "PkiTpl.Disabled";

        public const string OpenTrustee = "PkiTpl.OpenTrustee";

        /// <summary>{0}: {1}</summary>
        public const string RightOnAttribute = "PkiTpl.RightOnAttribute";

        public const string BroadNote = "PkiTpl.BroadNote";

        public const string Compare = "PkiTpl.Compare";

        public const string CompareTitle = "PkiTpl.CompareTitle";

        public const string CompareWith = "PkiTpl.CompareWith";

        public const string CompareClose = "PkiTpl.CompareClose";

        /// <summary>Differences: {0}</summary>
        public const string CompareDifferences = "PkiTpl.CompareDifferences";

        public const string CompareSame = "PkiTpl.CompareSame";

        public const string CompareOnlyDifferences = "PkiTpl.CompareOnlyDifferences";

        public const string CompareProperty = "PkiTpl.CompareProperty";

        public const string CompareHint = "PkiTpl.CompareHint";

        public const string ExportCsv = "PkiTpl.ExportCsv";

        public const string ExportHtml = "PkiTpl.ExportHtml";

        public const string HtmlFilter = "PkiTpl.HtmlFilter";

        /// <summary>Certificate templates – {0}</summary>
        public const string ReportTitle = "PkiTpl.ReportTitle";

        /// <summary>Read from {0} at {1}</summary>
        public const string ReportSubtitle = "PkiTpl.ReportSubtitle";

        /// <summary>Filtered: {0} of {1} templates.</summary>
        public const string ReportFiltered = "PkiTpl.ReportFiltered";

        public const string ReportPrivacy = "PkiTpl.ReportPrivacy";

        public const string ExportNote = "PkiTpl.ExportNote";

        public const string Copied = "PkiTpl.Copied";

        public const string CopyDetails = "PkiTpl.CopyDetails";

        /// <summary>The sentence of one bit (<c>PkiTpl.Flag.&lt;PkiFlagAttribute&gt;.&lt;Name&gt;</c>; the name is the technical one of <c>PkiFlags.Table</c>).</summary>
        public static string FlagKey(Core.Pki.PkiFlagAttribute attribute, string name) => $"PkiTpl.Flag.{attribute}.{name}";

        /// <summary>The heading of a flag attribute (<c>PkiTpl.FlagGroup.&lt;PkiFlagAttribute&gt;</c>).</summary>
        public static string FlagGroupKey(Core.Pki.PkiFlagAttribute attribute) => "PkiTpl.FlagGroup." + attribute;

        /// <summary>A multi-bit field (<c>PkiTpl.Field.&lt;Name&gt;</c>, takes {0}: the number).</summary>
        public static string FieldKey(string name) => "PkiTpl.Field." + name;

        /// <summary>A period in a unit (<c>PkiTpl.Unit.&lt;PkiPeriodUnit&gt;.One|Few|Many</c>, takes {0}: the count).</summary>
        public static string UnitKey(Core.Pki.PkiPeriodUnit unit, string form) => $"PkiTpl.Unit.{unit}.{form}";

        /// <summary>The plural forms of <see cref="UnitKey"/>.</summary>
        public static IReadOnlyList<string> PluralForms { get; } = ["One", "Few", "Many"];

        /// <summary>Every key of the dynamic families: one per bit of every flag table, the fields, the groups and the units.</summary>
        public static IEnumerable<string> AllDynamicKeys()
        {
            foreach (var attribute in Enum.GetValues<Core.Pki.PkiFlagAttribute>())
            {
                yield return FlagGroupKey(attribute);
                foreach (var entry in Core.Pki.PkiFlags.Table(attribute))
                {
                    yield return FlagKey(attribute, entry.Name);
                }

                foreach (var field in Core.Pki.PkiFlags.Fields(attribute))
                {
                    yield return FieldKey(field.Name);
                }
            }

            foreach (var unit in Enum.GetValues<Core.Pki.PkiPeriodUnit>())
            {
                foreach (var form in PluralForms)
                {
                    yield return UnitKey(unit, form);
                }
            }
        }
    }
}
