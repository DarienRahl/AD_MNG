using AdMng.Core.Directory;

namespace AdMng.Presentation.Connections;

/// <summary>
/// The domain named in a confirmation of a destructive operation: that of the session that will EXECUTE it (a window's
/// pinned session, or the session a change was planned in) - never the live connection of the window, which may have
/// moved on to another domain meanwhile.
/// </summary>
public static class ConfirmationText
{
    /// <summary>"contoso.test" (the NetBIOS name or the DC when the DNS name is unknown).</summary>
    public static string Domain(IDirectorySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var info = session.Info;
        return !string.IsNullOrWhiteSpace(info.DomainDnsName) ? info.DomainDnsName
            : !string.IsNullOrWhiteSpace(info.DomainNetBiosName) ? info.DomainNetBiosName
            : info.Server;
    }
}
