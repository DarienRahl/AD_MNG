namespace AdMng.Presentation.Localization;

public static partial class L
{
    /// <summary>Windows pinned to the directory session they were opened in (see <c>WindowViewModelBase</c>).</summary>
    public static class WindowSession
    {
        /// <summary>The session of the window ended (disconnect or a new connection): reopen the window.</summary>
        public const string Ended = "WindowSession.Ended";
    }

    /// <summary>Commands that follow the operator's effective permissions (ADR-0006).</summary>
    public static class Permissions
    {
        /// <summary>{0} = object type ("User"): allowedChildClassesEffective of the container does not contain it.</summary>
        public const string CannotCreate = "Permissions.CannotCreate";
    }
}
