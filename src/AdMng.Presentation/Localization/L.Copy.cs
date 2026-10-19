namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>Copying values (phase 1 "Kopiowanie wartości"): Copy as…, Ctrl+C on lists, copy buttons, confirmations.</summary>
    public static class Copy
    {
        /// <summary>Command category of the "Copy as" commands (palette, submenu).</summary>
        public const string Category = "CommandCategories.CopyAs";

        // "Copy as" formats (titles of the commands and labels of the confirmations).
        public const string Mail = "Copy.Mail";
        public const string Upn = "Copy.Upn";
        public const string Login = "Copy.Login";
        public const string DomainLogin = "Copy.DomainLogin";
        public const string Name = "Copy.Name";
        public const string DisplayName = "Copy.DisplayName";
        public const string DistinguishedName = "Copy.DistinguishedName";
        public const string CanonicalPath = "Copy.CanonicalPath";
        public const string Guid = "Copy.Guid";
        public const string Sid = "Copy.Sid";

        // Commands.
        public const string Rows = "Copy.Rows";
        public const string RowsWithHeaders = "Copy.RowsWithHeaders";
        public const string CopyName = "Copy.CopyName";
        public const string CopyMail = "Copy.CopyMail";
        public const string CopyAllMail = "Copy.CopyAllMail";
        public const string CopyAddress = "Copy.CopyAddress";
        public const string CopyEntry = "Copy.CopyEntry";

        /// <summary>{0} = what is copied (tool tip and automation name of a copy button: "Copy: E-mail").</summary>
        public const string CopyValue = "Copy.CopyValue";

        /// <summary>Details pane line with NETBIOS\sAMAccountName.</summary>
        public const string DomainLogon = "Copy.DomainLogon";

        /// <summary>Label of a copied session log entry.</summary>
        public const string Entry = "Copy.Entry";

        /// <summary>Label of copied Watchdog event details.</summary>
        public const string EventDetails = "Copy.EventDetails";

        // Confirmations (toasts).

        /// <summary>{0} = what was copied.</summary>
        public const string CopiedValue = "Copy.CopiedValue";

        /// <summary>{0} = what was copied, {1} = number of values.</summary>
        public const string CopiedValues = "Copy.CopiedValues";

        /// <summary>{0} = number of addresses.</summary>
        public const string CopiedMail = "Copy.CopiedMail";

        /// <summary>{0} = number of rows.</summary>
        public const string CopiedRows = "Copy.CopiedRows";

        /// <summary>{0} = number of objects skipped.</summary>
        public const string SkippedNoValue = "Copy.SkippedNoValue";

        /// <summary>{0} = number of objects skipped.</summary>
        public const string SkippedNoMail = "Copy.SkippedNoMail";

        /// <summary>{0} = what was to be copied.</summary>
        public const string NoValue = "Copy.NoValue";

        public const string NoMail = "Copy.NoMail";

        /// <summary>{0} = headers of the columns left out.</summary>
        public const string SecretColumnsLeftOut = "Copy.SecretColumnsLeftOut";
    }
}
