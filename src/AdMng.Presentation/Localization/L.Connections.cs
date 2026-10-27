namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>Several connection windows (design note docs/dev/multi-window.md): identity, switcher, new window.</summary>
    public static class Connections
    {
        /// <summary>The badge and the switcher of a window that is not connected.</summary>
        public const string NotConnected = "Connections.NotConnected";

        /// <summary>{0} = app title: the connect window of a new connection while other windows are open.</summary>
        public const string NewConnectionTitle = "Connections.NewConnectionTitle";

        /// <summary>{0} = the label of the other window: the chosen profile is already open there (information, not a block).</summary>
        public const string ProfileOpenElsewhere = "Connections.ProfileOpenElsewhere";

        /// <summary>Label of the colour choice in the connect dialog.</summary>
        public const string ColorLabel = "Connections.ColorLabel";

        /// <summary>The automatic colour (derived from the domain name).</summary>
        public const string ColorAutomatic = "Connections.ColorAutomatic";

        /// <summary>{0} = connection label: a palette entry "Przejdź do okna: …".</summary>
        public const string SwitchTo = "Connections.SwitchTo";

        /// <summary>Header of the window list in the badge menu.</summary>
        public const string OpenWindows = "Connections.OpenWindows";

        /// <summary>{0} = domain: a toast in the active window when another window's dialog waits for an answer.</summary>
        public const string DialogWaiting = "Connections.DialogWaiting";

        /// <summary>{0} = domain, {1} = account: the header line of message dialogs.</summary>
        public const string DomainHeader = "Connections.DomainHeader";

        /// <summary>{0} = domain: the header line when the account is unknown.</summary>
        public const string DomainHeaderNoAccount = "Connections.DomainHeaderNoAccount";

        /// <summary>A drag from another connection (or another process) onto the tree is refused.</summary>
        public const string CrossConnectionDrop = "Connections.CrossConnectionDrop";

        /// <summary>{0} = DC, {1} = channel security, {2} = profile: the badge tool tip.</summary>
        public const string BadgeToolTip = "Connections.BadgeToolTip";

        /// <summary>The profile part of the badge tool tip without a profile.</summary>
        public const string NoProfile = "Connections.NoProfile";

        /// <summary>Title of the error dialog of an unhandled exception (its connection is unknown).</summary>
        public const string ApplicationError = "Connections.ApplicationError";

        /// <summary>{0} = the service's domain, {1} = the window's domain: the Watchdog tab's information bar.</summary>
        public const string WatchdogOtherDomain = "Connections.WatchdogOtherDomain";

        /// <summary>{0} = domain: the title of the main window while closing it is confirmed.</summary>
        public const string CloseWindowTitle = "Connections.CloseWindowTitle";
    }
}
