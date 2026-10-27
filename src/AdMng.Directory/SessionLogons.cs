using System.Security;
using AdMng.Core.Computers.Remote;
using AdMng.Core.Directory;

namespace AdMng.Directory;

/// <summary>
/// The Windows logon of a connection window (K1 design note 4.7): the process identity (a Windows-account window), or the
/// user and domain of the window's credential with a read-only <see cref="SecureString"/> copy of its in-memory password.
/// The copy lives as long as this object – one CIM session or one tool launch – and is never written anywhere
/// (CLAUDE.md rule 2); <see cref="ToString"/> never shows it.
/// </summary>
public sealed class SessionLogon : IDisposable
{
    private SessionLogon(string? userName, string? domain, SecureString? password)
    {
        UserName = userName;
        Domain = domain;
        Password = password;
    }

    /// <summary>The process identity (no credential).</summary>
    public static SessionLogon WindowsAccount => new(null, null, null);

    public bool IsWindowsAccount => UserName is null;

    /// <summary><c>jan.kowalski</c>, or a UPN <c>jan.kowalski@contoso.test</c> (then <see cref="Domain"/> is null).</summary>
    public string? UserName { get; }

    /// <summary><c>CONTOSO</c> or <c>contoso.test</c>; null for a UPN and the Windows account.</summary>
    public string? Domain { get; }

    /// <summary>Read-only copy of the window's password; null for the Windows account.</summary>
    public SecureString? Password { get; }

    /// <summary><c>CONTOSO\jan.kowalski</c>, the UPN, or <c>(Windows account)</c>.</summary>
    public string DisplayName => UserName is null ? "(Windows account)" : Domain is null ? UserName : $"{Domain}\\{UserName}";

    /// <summary>A logon for a typed user name and its password (tests and callers holding a credential).</summary>
    /// <exception cref="RemoteActionException"><see cref="RemoteErrorKind.NotSupported"/> for a distinguished name.</exception>
    public static SessionLogon ForCredential(string userName, string? dnsDomain, SecureString password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var (user, domain) = SessionLogons.LogonParts(userName, dnsDomain) ?? throw new RemoteActionException(RemoteErrorKind.NotSupported);
        var copy = password.Copy();
        copy.MakeReadOnly();
        return new SessionLogon(user, domain, copy);
    }

    public void Dispose() => Password?.Dispose();

    /// <summary>Never the password.</summary>
    public override string ToString() => DisplayName;
}

/// <summary>Finds the Windows logon of a directory session (public API of K1 for AdMng.Remote).</summary>
public static class SessionLogons
{
    /// <summary>The logon of <paramref name="session"/>: the Windows account or a copy of its credential.</summary>
    /// <exception cref="RemoteActionException">
    /// <see cref="RemoteErrorKind.NotSupported"/>: a simple bind with a distinguished name (no Windows logon), a session whose
    /// credential was released, or a session of another implementation that does not run as the Windows account.
    /// </exception>
    public static SessionLogon Of(IDirectorySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session is not LdapDirectorySession ldap || ldap.Pool.Factory.Settings is not { } settings)
        {
            return session.Info.Authentication == AuthenticationMode.CurrentUser
                ? SessionLogon.WindowsAccount
                : throw new RemoteActionException(RemoteErrorKind.NotSupported);
        }

        if (settings.Options.Authentication == AuthenticationMode.CurrentUser)
        {
            return SessionLogon.WindowsAccount;
        }

        if (settings.Account is not { } account || account.Form == AccountNameForm.DistinguishedName || settings.Credential is not { } credential)
        {
            throw new RemoteActionException(RemoteErrorKind.NotSupported);
        }

        using var password = credential.SecurePassword;
        return SessionLogon.ForCredential(account.ToString(), settings.Options.Domain, password);
    }

    /// <summary>
    /// User and domain of a typed logon name for Kerberos: <c>DOMAIN\user</c> → (user, DOMAIN), a UPN → (UPN, null), a plain
    /// name → (name, the DNS domain); null for a distinguished name (a simple bind has no Windows logon).
    /// </summary>
    public static (string User, string? Domain)? LogonParts(string userName, string? dnsDomain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        var account = AccountName.Parse(userName);
        return account.Form == AccountNameForm.DistinguishedName ? null : account.ToNegotiateParts(dnsDomain);
    }
}
