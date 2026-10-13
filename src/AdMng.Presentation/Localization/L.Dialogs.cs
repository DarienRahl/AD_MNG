namespace AdMng.Presentation.Localization;

/// <summary>Resource keys of the property sheets, batch editor, CSV import and dialogs (wave B2).</summary>
public static partial class L
{
    public static class PropertySheet
    {
        public const string WindowTitle = "PropertySheet.WindowTitle";
        public const string DiscardChanges = "PropertySheet.DiscardChanges";
        public const string FixErrors = "PropertySheet.FixErrors";
        public const string ApplyTitle = "PropertySheet.ApplyTitle";
        public const string ApplyBlocked = "PropertySheet.ApplyBlocked";
        public const string ApplyFailed = "PropertySheet.ApplyFailed";
        public const string ChangedOnServer = "PropertySheet.ChangedOnServer";
        public const string NotFound = "PropertySheet.NotFound";
        public const string MembershipFailed = "PropertySheet.MembershipFailed";
        public const string Outdated = "PropertySheet.Outdated";
        public const string Deleted = "PropertySheet.Deleted";
        public const string Refresh = "PropertySheet.Refresh";
    }

    /// <summary>Validation and state texts of property fields.</summary>
    public static class Fields
    {
        public const string ReadOnlySystem = "Fields.ReadOnlySystem";
        public const string ReadOnlyPermission = "Fields.ReadOnlyPermission";
        public const string Required = "Fields.Required";
        public const string TooLong = "Fields.TooLong";
        public const string TooShort = "Fields.TooShort";
        public const string InvalidValue = "Fields.InvalidValue";
        public const string InvalidNumber = "Fields.InvalidNumber";
        public const string InvalidDate = "Fields.InvalidDate";
        public const string InvalidDn = "Fields.InvalidDn";
        public const string InvalidBoolean = "Fields.InvalidBoolean";
        public const string DigitsOnly = "Fields.DigitsOnly";
        public const string AsciiOnly = "Fields.AsciiOnly";
        public const string PrintableOnly = "Fields.PrintableOnly";
        public const string InvalidSamAccountName = "Fields.InvalidSamAccountName";
        public const string InvalidUpn = "Fields.InvalidUpn";
        public const string InvalidComputerName = "Fields.InvalidComputerName";
        public const string ComputerRequired = "Fields.ComputerRequired";
        public const string InvalidScopeChange = "Fields.InvalidScopeChange";
        public const string InvalidAddress = "Fields.InvalidAddress";
        public const string OnePrimarySmtp = "Fields.OnePrimarySmtp";
        public const string DuplicateValue = "Fields.DuplicateValue";
        public const string DateRequired = "Fields.DateRequired";
        public const string UncPathRequired = "Fields.UncPathRequired";
        public const string NoCountry = "Fields.NoCountry";
        public const string NotFound = "Fields.NotFound";
        public const string Copied = "Fields.Copied";
        public const string Other = "Fields.Other";
        public const string Never = "Fields.Never";
        public const string EndOf = "Fields.EndOf";
        public const string SetPrimary = "Fields.SetPrimary";
    }

    /// <summary>Labels of property sheet fields and sections that are not plain attribute names.</summary>
    public static class Labels
    {
        public const string UserLogonName = "Labels.UserLogonName";
        public const string UnlockAccount = "Labels.UnlockAccount";
        public const string AccountOptions = "Labels.AccountOptions";
        public const string MustChangePassword = "Labels.MustChangePassword";
        public const string KerberosAes = "Labels.KerberosAes";
        public const string Aes128 = "Labels.Aes128";
        public const string Aes256 = "Labels.Aes256";
        public const string AccountExpires = "Labels.AccountExpires";
        public const string LogOnTo = "Labels.LogOnTo";
        public const string AllComputers = "Labels.AllComputers";
        public const string TheseComputers = "Labels.TheseComputers";
        public const string UserProfile = "Labels.UserProfile";
        public const string HomeFolder = "Labels.HomeFolder";
        public const string LocalPath = "Labels.LocalPath";
        public const string Connect = "Labels.Connect";
        public const string To = "Labels.To";
        public const string Country = "Labels.Country";
        public const string ComputerName = "Labels.ComputerName";
        public const string ComputerNamePre2000 = "Labels.ComputerNamePre2000";
        public const string ComputerRole = "Labels.ComputerRole";
        public const string RoleWorkstation = "Labels.RoleWorkstation";
        public const string RoleDomainController = "Labels.RoleDomainController";
        public const string RoleReadOnlyDomainController = "Labels.RoleReadOnlyDomainController";
        public const string Delegation = "Labels.Delegation";
        public const string DelegationNone = "Labels.DelegationNone";
        public const string DelegationUnconstrained = "Labels.DelegationUnconstrained";
        public const string DelegationConstrainedKerberos = "Labels.DelegationConstrainedKerberos";
        public const string DelegationConstrainedAnyProtocol = "Labels.DelegationConstrainedAnyProtocol";
        public const string GroupName = "Labels.GroupName";
        public const string GroupNamePre2000 = "Labels.GroupNamePre2000";
        public const string GroupScope = "Labels.GroupScope";
        public const string GroupType = "Labels.GroupType";
        public const string Security = "Labels.Security";
        public const string Distribution = "Labels.Distribution";
        public const string CanonicalName = "Labels.CanonicalName";
        public const string UsnOriginal = "Labels.UsnOriginal";
        public const string UsnCurrent = "Labels.UsnCurrent";
        public const string PrimaryGroup = "Labels.PrimaryGroup";
        public const string AllNested = "Labels.AllNested";
        public const string Folder = "Labels.Folder";
        public const string ShowOnlyWithValues = "Labels.ShowOnlyWithValues";
        public const string ShowOnlyWritable = "Labels.ShowOnlyWritable";
        public const string Syntax = "Labels.Syntax";
        public const string Value = "Labels.Value";
        public const string Attribute = "Labels.Attribute";
        public const string SetPrimaryAddress = "Labels.SetPrimaryAddress";
        public const string AddressType = "Labels.AddressType";
        public const string Address = "Labels.Address";
    }

    public static class Membership
    {
        public const string PendingAdd = "Membership.PendingAdd";
        public const string PendingRemove = "Membership.PendingRemove";
        public const string PrimaryGroup = "Membership.PrimaryGroup";
        public const string GroupCount = "Membership.GroupCount";
        public const string MemberCount = "Membership.MemberCount";
        public const string Direct = "Membership.Direct";
        public const string Nested = "Membership.Nested";
    }

    public static class AttributeEditor
    {
        public const string NotSet = "AttributeEditor.NotSet";
        public const string BinaryReadOnly = "AttributeEditor.BinaryReadOnly";
        public const string EditorTitle = "AttributeEditor.EditorTitle";
        public const string FileTimeHint = "AttributeEditor.FileTimeHint";
        public const string DateTimeHint = "AttributeEditor.DateTimeHint";
        public const string IntegerHint = "AttributeEditor.IntegerHint";
        public const string DnHint = "AttributeEditor.DnHint";
    }

    public static class Batch
    {
        public const string WindowTitle = "Batch.WindowTitle";
        public const string DiscardPasswords = "Batch.DiscardPasswords";
        public const string CsvWindowTitle = "Batch.CsvWindowTitle";
        public const string DefaultChangeTitle = "Batch.DefaultChangeTitle";
        public const string CsvChangeTitle = "Batch.CsvChangeTitle";
        public const string StepOperations = "Batch.StepOperations";
        public const string StepPreview = "Batch.StepPreview";
        public const string StepExecute = "Batch.StepExecute";
        public const string StepResults = "Batch.StepResults";
        public const string AddOperation = "Batch.AddOperation";
        public const string Targets = "Batch.Targets";
        public const string Sample = "Batch.Sample";
        public const string AttributeRequired = "Batch.AttributeRequired";
        public const string InvalidAttribute = "Batch.InvalidAttribute";
        public const string UnknownAttribute = "Batch.UnknownAttribute";
        public const string InvalidTemplate = "Batch.InvalidTemplate";
        public const string ValuesRequired = "Batch.ValuesRequired";
        public const string FindRequired = "Batch.FindRequired";
        public const string InvalidRegex = "Batch.InvalidRegex";
        public const string OptionRequired = "Batch.OptionRequired";
        public const string PasswordLength = "Batch.PasswordLength";
        public const string PasswordRequired = "Batch.PasswordRequired";
        public const string PasswordMismatch = "Batch.PasswordMismatch";
        public const string TargetRequired = "Batch.TargetRequired";
        public const string NameRequired = "Batch.NameRequired";
        public const string GroupsRequired = "Batch.GroupsRequired";
        public const string DeleteAlone = "Batch.DeleteAlone";
        public const string OnlyOnce = "Batch.OnlyOnce";
        public const string NoChange = "Batch.NoChange";
        public const string NotExecuted = "Batch.NotExecuted";
        public const string Container = "Batch.Container";
        public const string Password = "Batch.Password";
        public const string PreviewSummary = "Batch.PreviewSummary";
        public const string ResultSummary = "Batch.ResultSummary";
        public const string ConfirmExecute = "Batch.ConfirmExecute";
        public const string ExecutedNotification = "Batch.ExecutedNotification";
        public const string ExportPreview = "Batch.ExportPreview";
        public const string ExportResults = "Batch.ExportResults";
        public const string Exported = "Batch.Exported";
        public const string ExportPasswordsTitle = "Batch.ExportPasswordsTitle";
        public const string ExportPasswordsWarning = "Batch.ExportPasswordsWarning";
        public const string CopyPowerShell = "Batch.CopyPowerShell";
        public const string PowerShellCopied = "Batch.PowerShellCopied";
        public const string Execute = "Batch.Execute";
        public const string RetryFailed = "Batch.RetryFailed";
        public const string Undo = "Batch.Undo";
        public const string NothingToUndo = "Batch.NothingToUndo";
        public const string RollbackPreview = "Batch.RollbackPreview";
        public const string Include = "Batch.Include";
        public const string IncludeAll = "Batch.IncludeAll";
        public const string ExcludeAll = "Batch.ExcludeAll";
        public const string Before = "Batch.Before";
        public const string After = "Batch.After";
        public const string Action = "Batch.Action";
        public const string Status = "Batch.Status";
        public const string Message = "Batch.Message";
        public const string Planning = "Batch.Planning";
    }

    public static class Csv
    {
        public const string OpenTitle = "Csv.OpenTitle";
        public const string TextFilter = "Csv.TextFilter";
        public const string InvalidFile = "Csv.InvalidFile";
        public const string NoFile = "Csv.NoFile";
        public const string NoHeader = "Csv.NoHeader";
        public const string NoRows = "Csv.NoRows";
        public const string KeyColumnRequired = "Csv.KeyColumnRequired";
        public const string MappingRequired = "Csv.MappingRequired";
        public const string DuplicateMapping = "Csv.DuplicateMapping";
        public const string UnknownAttribute = "Csv.UnknownAttribute";
        public const string EmptyIgnore = "Csv.EmptyIgnore";
        public const string EmptyClear = "Csv.EmptyClear";
        public const string AnyKind = "Csv.AnyKind";
        public const string EntireDomain = "Csv.EntireDomain";
        public const string TabDelimiter = "Csv.TabDelimiter";
        public const string TemplateTitle = "Csv.TemplateTitle";
        public const string TemplateSaved = "Csv.TemplateSaved";
        public const string KeyColumn = "Csv.KeyColumn";
        public const string KeyAttribute = "Csv.KeyAttribute";
        public const string Column = "Csv.Column";
        public const string MapsTo = "Csv.MapsTo";
        public const string Ignore = "Csv.Ignore";
        public const string EmptyCells = "Csv.EmptyCells";
        public const string MultiValueSeparator = "Csv.MultiValueSeparator";
        public const string RowsLoaded = "Csv.RowsLoaded";
        public const string DownloadTemplate = "Csv.DownloadTemplate";
    }

    public static class ResetPassword
    {
        public const string WindowTitleOne = "ResetPassword.WindowTitleOne";
        public const string WindowTitleMany = "ResetPassword.WindowTitleMany";
        public const string NewPassword = "ResetPassword.NewPassword";
        public const string ConfirmPassword = "ResetPassword.ConfirmPassword";
        public const string Generate = "ResetPassword.Generate";
        public const string MustChange = "ResetPassword.MustChange";
        public const string Unlock = "ResetPassword.Unlock";
        public const string ConfirmMany = "ResetPassword.ConfirmMany";
        public const string Done = "ResetPassword.Done";
        public const string NothingToReset = "ResetPassword.NothingToReset";
        public const string PasswordCopied = "ResetPassword.PasswordCopied";
        public const string Reveal = "ResetPassword.Reveal";
    }

    public static class NewObject
    {
        public const string WindowTitle = "NewObject.WindowTitle";
        public const string CreateIn = "NewObject.CreateIn";
        public const string ChangeTitle = "NewObject.ChangeTitle";
        public const string NameRequired = "NewObject.NameRequired";
        public const string AlreadyExists = "NewObject.AlreadyExists";
        public const string PasswordRequired = "NewObject.PasswordRequired";
        public const string NeverExpiresWarning = "NewObject.NeverExpiresWarning";
        public const string ProtectionComingSoon = "NewObject.ProtectionComingSoon";
        public const string ProtectFromDeletion = "NewObject.ProtectFromDeletion";
        public const string CannotChangePassword = "NewObject.CannotChangePassword";
        public const string SamChecking = "NewObject.SamChecking";
        public const string SamAvailable = "NewObject.SamAvailable";
        public const string SamTaken = "NewObject.SamTaken";
        public const string SamInvalid = "NewObject.SamInvalid";
        public const string Created = "NewObject.Created";
        public const string PartiallyCreated = "NewObject.PartiallyCreated";
        public const string Failed = "NewObject.Failed";
        public const string FullName = "NewObject.FullName";
    }

    public static class Find
    {
        public const string WindowTitle = "Find.WindowTitle";
        public const string ResultsTitle = "Find.ResultsTitle";
        public const string In = "Find.In";
        public const string FindNow = "Find.FindNow";
        public const string ClearAll = "Find.ClearAll";
        public const string Advanced = "Find.Advanced";
        public const string Field = "Find.Field";
        public const string Condition = "Find.Condition";
        public const string IncludeSubcontainers = "Find.IncludeSubcontainers";
        public const string LdapFilter = "Find.LdapFilter";
    }

    public static class Picker
    {
        public const string WindowTitle = "Picker.WindowTitle";
        public const string ContainerWindowTitle = "Picker.ContainerWindowTitle";
        public const string ObjectTypes = "Picker.ObjectTypes";
        public const string Location = "Picker.Location";
        public const string EnterNames = "Picker.EnterNames";
        public const string CheckNames = "Picker.CheckNames";
        public const string Found = "Picker.Found";
        public const string TooMany = "Picker.TooMany";
        public const string NotFound = "Picker.NotFound";
        public const string Ambiguous = "Picker.Ambiguous";
    }

    public static class SessionLog
    {
        public const string WindowTitle = "SessionLog.WindowTitle";
        public const string ExportTitle = "SessionLog.ExportTitle";
        public const string Succeeded = "SessionLog.Succeeded";
        public const string Failed = "SessionLog.Failed";
        public const string Count = "SessionLog.Count";
        public const string FailuresOnly = "SessionLog.FailuresOnly";
    }

    public static class PowerShellHistory
    {
        public const string WindowTitle = "PowerShellHistory.WindowTitle";
        public const string SaveTitle = "PowerShellHistory.SaveTitle";
        public const string ScriptFilter = "PowerShellHistory.ScriptFilter";
        public const string Empty = "PowerShellHistory.Empty";
    }
}
