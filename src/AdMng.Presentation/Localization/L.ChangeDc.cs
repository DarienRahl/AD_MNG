namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>"Change domain controller" dialog (switch the session to another DC of the same domain).</summary>
    public static class ChangeDc
    {
        public const string WindowTitle = "ChangeDc.WindowTitle";

        /// <summary>{0} = DNS name of the domain.</summary>
        public const string Domain = "ChangeDc.Domain";

        /// <summary>{0} = the DC of the current session.</summary>
        public const string Current = "ChangeDc.Current";
        public const string ListLabel = "ChangeDc.ListLabel";
        public const string ColumnYourSite = "ChangeDc.ColumnYourSite";
        public const string ColumnRoles = "ChangeDc.ColumnRoles";
        public const string RoleCurrent = "ChangeDc.RoleCurrent";
        public const string RolePdcEmulator = "ChangeDc.RolePdcEmulator";
        public const string ListUnavailable = "ChangeDc.ListUnavailable";
        public const string ThisDomainController = "ChangeDc.ThisDomainController";
        public const string ServerPlaceholder = "ChangeDc.ServerPlaceholder";
        public const string AnyWritableInSite = "ChangeDc.AnyWritableInSite";

        /// <summary>{0} = DC host name, {1} = the client's site.</summary>
        public const string AnyWritableTarget = "ChangeDc.AnyWritableTarget";

        /// <summary>{0} = the current DC.</summary>
        public const string AnyWritableIsCurrent = "ChangeDc.AnyWritableIsCurrent";
        public const string ClientSiteUnknown = "ChangeDc.ClientSiteUnknown";

        /// <summary>{0} = the client's site.</summary>
        public const string NoWritableInSite = "ChangeDc.NoWritableInSite";
        public const string InvalidName = "ChangeDc.InvalidName";
        public const string IsCurrent = "ChangeDc.IsCurrent";

        /// <summary>{0} = DC host name.</summary>
        public const string ReadOnlyWarning = "ChangeDc.ReadOnlyWarning";

        /// <summary>{0} = connection profile name.</summary>
        public const string UpdateProfile = "ChangeDc.UpdateProfile";
        public const string ChangeButton = "ChangeDc.ChangeButton";

        /// <summary>{0} = the new DC (toast).</summary>
        public const string Changed = "ChangeDc.Changed";

        /// <summary>{0} = the current DC.</summary>
        public const string AlreadyConnected = "ChangeDc.AlreadyConnected";

        /// <summary>{0} = the DC that answered, {1} = its domain, {2} = the session's domain.</summary>
        public const string OtherDomain = "ChangeDc.OtherDomain";
        public const string NotSupported = "ChangeDc.NotSupported";

        /// <summary>{0} = the DC the session stays on after a failed switch.</summary>
        public const string StaysOn = "ChangeDc.StaysOn";
        public const string RunningOperations = "ChangeDc.RunningOperations";

        /// <summary>{0} = the error.</summary>
        public const string ProfileNotSaved = "ChangeDc.ProfileNotSaved";

        /// <summary>Session log action of a switch (<c>DomainControllerSwitch.SessionLogAction</c>).</summary>
        public const string SessionLogAction = "ChangeDc.SessionLogAction";
    }
}
