using AdMng.Core.Model;

namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>
    /// Step 4 <em>Sprawdzenie</em>, the change plan and the results of the bulk creation wizard (docs/dev/phase3-bulk-create.md
    /// 2.7, 2.9; owner B4). The status, source, scope and filter families are in
    /// <see cref="global::AdMng.Presentation.BulkCreate.BulkCreateTextKeys"/> (contract block).
    /// </summary>
    public static class BulkCreateCheck
    {
        // ---- The check grid ----

        /// <summary><em>Nr</em>: the row of the Excel sheet.</summary>
        public const string ColumnNumber = "BulkCheck.ColumnNumber";

        /// <summary><em>Uwagi</em>: the reasons of a row's status.</summary>
        public const string ColumnNotes = "BulkCheck.ColumnNotes";

        public const string ColumnManager = "BulkCheck.ColumnManager";
        public const string ColumnGroups = "BulkCheck.ColumnGroups";

        /// <summary>The include check box's header and name.</summary>
        public const string ColumnInclude = "BulkCheck.ColumnInclude";

        public const string ColumnStatus = "BulkCheck.ColumnStatus";

        /// <summary><c>{0}</c> rows, <c>{1}</c> OK, <c>{2}</c> warnings, <c>{3}</c> errors, <c>{4}</c> excluded.</summary>
        public const string Summary = "BulkCheck.Summary";

        /// <summary>As <see cref="Summary"/> with <c>{5}</c> created.</summary>
        public const string SummaryWithCreated = "BulkCheck.SummaryWithCreated";

        /// <summary><em>Unikalność UPN i adresów: {0}</em> (<c>BulkCheck.Scope.*</c>).</summary>
        public const string ScopeNote = "BulkCheck.ScopeNote";

        /// <summary>The progress line: <c>{0}</c> rows computed, <c>{1}</c> rows.</summary>
        public const string Computing = "BulkCheck.Computing";

        public const string Recheck = "BulkCheck.Recheck";
        public const string RecheckHint = "BulkCheck.RecheckHint";
        public const string Renumber = "BulkCheck.Renumber";
        public const string RenumberHint = "BulkCheck.RenumberHint";
        public const string NextError = "BulkCheck.NextError";
        public const string CopyErrorRows = "BulkCheck.CopyErrorRows";
        public const string CopyErrorRowsHint = "BulkCheck.CopyErrorRowsHint";

        /// <summary><c>{0}</c> rows copied.</summary>
        public const string ErrorRowsCopied = "BulkCheck.ErrorRowsCopied";

        public const string NoErrorRows = "BulkCheck.NoErrorRows";

        /// <summary>An earlier step is incomplete: nothing can be computed.</summary>
        public const string InputIncomplete = "BulkCheck.InputIncomplete";

        /// <summary>The grid shows an older computation (data, rules or created rows changed since).</summary>
        public const string StaleNote = "BulkCheck.StaleNote";

        /// <summary>How to fix rows inline.</summary>
        public const string EditHint = "BulkCheck.EditHint";

        /// <summary>The run issues above the grid.</summary>
        public const string RunIssuesHeading = "BulkCheck.RunIssuesHeading";

        /// <summary>The cell's tool tip: <c>{0}</c> the source (<c>BulkCheck.Source.*</c>).</summary>
        public const string CellSource = "BulkCheck.CellSource";

        public const string CellAdjusted = "BulkCheck.CellAdjusted";
        public const string CellSanitized = "BulkCheck.CellSanitized";
        public const string CellTruncated = "BulkCheck.CellTruncated";

        /// <summary><em>Przywróć wartość wyliczoną</em> (cell menu).</summary>
        public const string RevertCell = "BulkCheck.RevertCell";

        /// <summary>The prompt of an ambiguous manager's drop-down.</summary>
        public const string ChooseManager = "BulkCheck.ChooseManager";

        public const string NoRowsMatch = "BulkCheck.NoRowsMatch";

        // ---- The change plan ----

        /// <summary>The change title per kind (<see cref="ChangeTitleKey"/>): <c>{0}</c> objects, <c>{1}</c> the container phrase (<see cref="InUnit"/> …).</summary>
        private const string ChangeTitlePrefix = "BulkCheck.ChangeTitle.";

        /// <summary><em>jednostce „{0}”</em>.</summary>
        public const string InUnit = "BulkCheck.InUnit";

        /// <summary><em>kontenerze „{0}”</em>.</summary>
        public const string InContainer = "BulkCheck.InContainer";

        /// <summary><em>domenie {0}</em>.</summary>
        public const string InDomain = "BulkCheck.InDomain";

        /// <summary>
        /// The typed confirmation of a create plan with initial passwords: <c>{0}</c> objects, <c>{1}</c> the title, <c>{2}</c>
        /// the text to type, <c>{3}</c> the domain.
        /// </summary>
        public const string ConfirmCreate = "BulkCheck.ConfirmCreate";

        /// <summary>As <see cref="ConfirmCreate"/> for contacts, groups, computers and users without a password.</summary>
        public const string ConfirmCreateWithoutPasswords = "BulkCheck.ConfirmCreateWithoutPasswords";

        /// <summary>The shared password boxes are empty (cleared after the last plan): <em>wpisz hasło ponownie</em>.</summary>
        public const string ReenterPassword = "BulkCheck.ReenterPassword";

        /// <summary>The undo's change title: <c>{0}</c> the creation's title.</summary>
        public const string UndoTitle = "BulkCheck.UndoTitle";

        /// <summary><c>{0}</c> objects the undo deletes.</summary>
        public const string UndoNote = "BulkCheck.UndoNote";

        /// <summary>The step label of the batch editor's first step in create mode.</summary>
        public const string StepPrepare = "BulkCheck.StepPrepare";

        // ---- Execution and results ----

        /// <summary>The canary stopped the run.</summary>
        public const string CanaryStopped = "BulkCheck.CanaryStopped";

        /// <summary>
        /// <c>{0}</c> the DC, <c>{1}</c> <see cref="PdcYes"/> / <see cref="PdcNo"/> / <see cref="PdcUnknown"/>.
        /// </summary>
        public const string ReplicationNote = "BulkCheck.ReplicationNote";

        public const string PdcYes = "BulkCheck.PdcYes";
        public const string PdcNo = "BulkCheck.PdcNo";
        public const string PdcUnknown = "BulkCheck.PdcUnknown";

        /// <summary>A created account whose password could not be set stays disabled.</summary>
        public const string DisabledWithoutPassword = "BulkCheck.DisabledWithoutPassword";

        /// <summary>A Create refused with a constraint violation while the UPN was checked in the domain only.</summary>
        public const string UpnMaybeInOtherDomain = "BulkCheck.UpnMaybeInOtherDomain";

        public const string ResultsHeading = "BulkCheck.ResultsHeading";
        public const string ResultsHint = "BulkCheck.ResultsHint";
        public const string ColumnName = "BulkCheck.ColumnName";
        public const string ColumnLogin = "BulkCheck.ColumnLogin";
        public const string ColumnUpn = "BulkCheck.ColumnUpn";
        public const string ColumnMail = "BulkCheck.ColumnMail";
        public const string ColumnPassword = "BulkCheck.ColumnPassword";
        public const string ColumnHandedOut = "BulkCheck.ColumnHandedOut";
        public const string ShowPassword = "BulkCheck.ShowPassword";
        public const string CopyPassword = "BulkCheck.CopyPassword";
        public const string PasswordCopied = "BulkCheck.PasswordCopied";

        public const string AccountCreated = "BulkCheck.AccountCreated";
        public const string AccountPartiallyCreated = "BulkCheck.AccountPartiallyCreated";
        public const string AccountUndone = "BulkCheck.AccountUndone";

        /// <summary><em>Kopiuj tabelę z hasłami…</em>.</summary>
        public const string CopyTableWithPasswords = "BulkCheck.CopyTableWithPasswords";

        /// <summary>The confirmation before the table with the passwords goes to the clipboard.</summary>
        public const string CopyPasswordsTitle = "BulkCheck.CopyPasswordsTitle";

        public const string CopyPasswordsWarning = "BulkCheck.CopyPasswordsWarning";

        /// <summary><em>Kopiuj bez haseł</em>.</summary>
        public const string CopyTableWithoutPasswords = "BulkCheck.CopyTableWithoutPasswords";

        /// <summary><c>{0}</c> accounts.</summary>
        public const string TableCopiedWithPasswords = "BulkCheck.TableCopiedWithPasswords";

        /// <summary><c>{0}</c> accounts.</summary>
        public const string TableCopied = "BulkCheck.TableCopied";

        /// <summary>The sheet's columns added to the copied table.</summary>
        public const string CopyColumns = "BulkCheck.CopyColumns";

        public const string NoAccounts = "BulkCheck.NoAccounts";

        // ---- Retry with a new shared password ----

        public const string RetryPasswordHeading = "BulkCheck.RetryPasswordHeading";
        public const string RetryPasswordHint = "BulkCheck.RetryPasswordHint";
        public const string RetryPassword = "BulkCheck.RetryPassword";
        public const string RetryConfirmPassword = "BulkCheck.RetryConfirmPassword";

        /// <summary>The retry needs the new password typed twice.</summary>
        public const string RetryPasswordRequired = "BulkCheck.RetryPasswordRequired";

        /// <summary>The kinds with a change title (the wizard's four kinds).</summary>
        public static IReadOnlyList<ObjectKind> ChangeTitleKinds { get; } = [ObjectKind.User, ObjectKind.Contact, ObjectKind.Group, ObjectKind.Computer];

        /// <summary>
        /// <em>Utworzenie kont ({0}) w {1}</em> per kind (<see cref="ChangeTitleKinds"/>; any other kind uses the user's): <c>{0}</c>
        /// objects, <c>{1}</c> the container phrase.
        /// </summary>
        public static string ChangeTitleKey(ObjectKind kind) => ChangeTitlePrefix + (ChangeTitleKinds.Contains(kind) ? kind : ObjectKind.User);
    }
}
