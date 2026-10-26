namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>Comparison of an object with the matching object of another connection (docs/dev/multi-window.md 8).</summary>
    public static class Comparison
    {
        /// <summary>{0} = object name, {1} = domain A, {2} = domain B.</summary>
        public const string WindowTitle = "Comparison.WindowTitle";

        /// <summary>{0} = object name: the title before the other connection is known.</summary>
        public const string WindowTitleNoTarget = "Comparison.WindowTitleNoTarget";

        /// <summary>Why the command is disabled: the selection.</summary>
        public const string DisabledSelectOne = "Comparison.DisabledSelectOne";

        /// <summary>Why the command is disabled: no other connection.</summary>
        public const string DisabledNoOtherConnection = "Comparison.DisabledNoOtherConnection";

        public const string Connection = "Comparison.Connection";
        public const string MatchBy = "Comparison.MatchBy";
        public const string ExplicitHint = "Comparison.ExplicitHint";
        public const string Compare = "Comparison.Compare";
        public const string Stop = "Comparison.Stop";
        public const string Refresh = "Comparison.Refresh";
        public const string ExportCsv = "Comparison.ExportCsv";
        public const string ExportTitle = "Comparison.ExportTitle";

        /// <summary>The prefix of the suggested CSV file name ("porownanie").</summary>
        public const string FileNamePrefix = "Comparison.FileNamePrefix";

        /// <summary>{0} = DN of the match.</summary>
        public const string StatusFound = "Comparison.StatusFound";

        /// <summary>{0} = the rules tried.</summary>
        public const string StatusNotFound = "Comparison.StatusNotFound";

        public const string StatusNotFoundExplicit = "Comparison.StatusNotFoundExplicit";

        /// <summary>{0} = number of candidates.</summary>
        public const string StatusAmbiguous = "Comparison.StatusAmbiguous";

        public const string StatusTooMany = "Comparison.StatusTooMany";

        /// <summary>{0} = the target's domain.</summary>
        public const string StatusOutside = "Comparison.StatusOutside";

        public const string StatusNoValue = "Comparison.StatusNoValue";
        public const string StatusNoExplicitText = "Comparison.StatusNoExplicitText";
        public const string StatusReading = "Comparison.StatusReading";
        public const string StatusChooseTarget = "Comparison.StatusChooseTarget";
        public const string StatusCancelled = "Comparison.StatusCancelled";
        public const string NoOtherConnection = "Comparison.NoOtherConnection";
        public const string TargetNotConnected = "Comparison.TargetNotConnected";

        /// <summary>{0} = domain: the source object is gone.</summary>
        public const string SourceNotFound = "Comparison.SourceNotFound";

        /// <summary>{0} = domain: the chosen candidate is gone.</summary>
        public const string TargetNotFound = "Comparison.TargetNotFound";

        public const string NotComparable = "Comparison.NotComparable";
        public const string SameObject = "Comparison.SameObject";

        /// <summary>{0} = connection label.</summary>
        public const string SideClosed = "Comparison.SideClosed";

        /// <summary>{0} = connection label.</summary>
        public const string SideChanged = "Comparison.SideChanged";

        public const string TabAttributes = "Comparison.TabAttributes";
        public const string TabMembership = "Comparison.TabMembership";
        public const string TabMembers = "Comparison.TabMembers";

        /// <summary>{0} = tab name, {1} = number of differences.</summary>
        public const string TabWithCount = "Comparison.TabWithCount";

        public const string OnlyDifferences = "Comparison.OnlyDifferences";
        public const string ShowVolatile = "Comparison.ShowVolatile";
        public const string AddressesWithoutDomain = "Comparison.AddressesWithoutDomain";
        public const string ShowNested = "Comparison.ShowNested";
        public const string ShowNestedMembers = "Comparison.ShowNestedMembers";
        public const string Filter = "Comparison.Filter";

        public const string ColumnSection = "Comparison.ColumnSection";
        public const string ColumnAttribute = "Comparison.ColumnAttribute";
        public const string ColumnLdapName = "Comparison.ColumnLdapName";
        public const string ColumnState = "Comparison.ColumnState";
        public const string ColumnGroup = "Comparison.ColumnGroup";
        public const string ColumnMember = "Comparison.ColumnMember";
        public const string ColumnName = "Comparison.ColumnName";
        public const string ColumnPath = "Comparison.ColumnPath";
        public const string ColumnMail = "Comparison.ColumnMail";
        public const string ColumnValue = "Comparison.ColumnValue";
        public const string ColumnSide = "Comparison.ColumnSide";
        public const string RowDistinguishedName = "Comparison.RowDistinguishedName";

        public const string StateSame = "Comparison.StateSame";
        public const string StateSameOtherDomain = "Comparison.StateSameOtherDomain";
        public const string StateDifferent = "Comparison.StateDifferent";
        public const string StateDifferentCase = "Comparison.StateDifferentCase";
        public const string StateOnlyA = "Comparison.StateOnlyA";
        public const string StateOnlyB = "Comparison.StateOnlyB";
        public const string StateNotInSchemaA = "Comparison.StateNotInSchemaA";
        public const string StateNotInSchemaB = "Comparison.StateNotInSchemaB";

        /// <summary>An empty side: no value, or no right to read it.</summary>
        public const string NoValue = "Comparison.NoValue";

        /// <summary>{0} = number of values not shown in the cell.</summary>
        public const string MoreValues = "Comparison.MoreValues";

        /// <summary>{0} = common, {1} = only A, {2} = only B.</summary>
        public const string ValueCounts = "Comparison.ValueCounts";

        public const string SecretSet = "Comparison.SecretSet";
        public const string Incomplete = "Comparison.Incomplete";
        public const string MarkBoth = "Comparison.MarkBoth";
        public const string MarkOnlyA = "Comparison.MarkOnlyA";
        public const string MarkOnlyB = "Comparison.MarkOnlyB";

        public const string KindDirect = "Comparison.KindDirect";
        public const string KindPrimary = "Comparison.KindPrimary";
        public const string KindNested = "Comparison.KindNested";

        public const string MembershipInBoth = "Comparison.MembershipInBoth";
        public const string MembershipInBothDifferently = "Comparison.MembershipInBothDifferently";
        public const string MembershipOnlyA = "Comparison.MembershipOnlyA";
        public const string MembershipOnlyB = "Comparison.MembershipOnlyB";

        public const string MembershipFootnote = "Comparison.MembershipFootnote";

        /// <summary>{0} = the cap.</summary>
        public const string ListTruncated = "Comparison.ListTruncated";

        /// <summary>{0} = number of skipped values.</summary>
        public const string MembersSkipped = "Comparison.MembersSkipped";

        public const string NestedLoading = "Comparison.NestedLoading";

        /// <summary>{0} = attribute name: the header of the details pane.</summary>
        public const string ValuesOf = "Comparison.ValuesOf";

        public const string CopiedRow = "Comparison.CopiedRow";
    }
}
