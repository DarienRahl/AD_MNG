namespace AdMng.Directory;

/// <summary>Form in which the operator typed a user name.</summary>
internal enum AccountNameForm
{
    /// <summary><c>jan.kowalski</c> - no domain part.</summary>
    Plain,

    /// <summary><c>CONTOSO\jan.kowalski</c> (NT4 / down-level logon name).</summary>
    DownLevel,

    /// <summary><c>jan.kowalski@contoso.com</c> (user principal name, explicit or implicit).</summary>
    UserPrincipalName,

    /// <summary><c>CN=Jan Kowalski,OU=IT,DC=contoso,DC=com</c> (only meaningful for simple bind).</summary>
    DistinguishedName,
}

/// <summary>A parsed logon name: <c>DOMAIN\user</c>, <c>user@domain</c>, <c>user</c> or a DN.</summary>
internal sealed record AccountName(AccountNameForm Form, string User, string? Domain)
{
    /// <summary>Parses the user name exactly as typed in the connection dialog.</summary>
    /// <exception cref="ArgumentException">The name is empty or malformed (e.g. <c>DOMAIN\</c>, <c>@contoso.com</c>).</exception>
    public static AccountName Parse(string userName)
    {
        ArgumentNullException.ThrowIfNull(userName);
        var name = userName.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("The user name is empty.", nameof(userName));
        }

        var backslash = name.IndexOf('\\', StringComparison.Ordinal);
        if (backslash >= 0)
        {
            var domain = name[..backslash].Trim();
            var user = name[(backslash + 1)..].Trim();
            if (domain.Length == 0 || user.Length == 0 || user.Contains('\\', StringComparison.Ordinal))
            {
                throw new ArgumentException($"'{name}' is not a valid DOMAIN\\user name.", nameof(userName));
            }

            return new AccountName(AccountNameForm.DownLevel, user, domain);
        }

        if (LooksLikeDistinguishedName(name))
        {
            return new AccountName(AccountNameForm.DistinguishedName, name, null);
        }

        var at = name.LastIndexOf('@');
        if (at >= 0)
        {
            var user = name[..at].Trim();
            var domain = name[(at + 1)..].Trim();
            if (user.Length == 0 || domain.Length == 0)
            {
                throw new ArgumentException($"'{name}' is not a valid user@domain name.", nameof(userName));
            }

            return new AccountName(AccountNameForm.UserPrincipalName, $"{user}@{domain}", domain);
        }

        return new AccountName(AccountNameForm.Plain, name, null);
    }

    /// <summary>
    /// The name sent in an LDAP simple bind. AD accepts a DN, a UPN and <c>DOMAIN\user</c>; a plain name is
    /// turned into the implicit UPN <c>user@dnsDomain</c> when the domain is known.
    /// </summary>
    public string ToSimpleBindName(string? dnsDomain) => Form switch
    {
        AccountNameForm.DownLevel => $"{Domain}\\{User}",
        AccountNameForm.Plain when !string.IsNullOrWhiteSpace(dnsDomain) => $"{User}@{dnsDomain}",
        _ => User,
    };

    /// <summary>User and domain parts for a Negotiate (Kerberos / NTLM) credential.</summary>
    public (string User, string? Domain) ToNegotiateParts(string? dnsDomain) => Form switch
    {
        AccountNameForm.DownLevel => (User, Domain),
        AccountNameForm.Plain => (User, string.IsNullOrWhiteSpace(dnsDomain) ? null : dnsDomain),
        _ => (User, null),
    };

    /// <summary>How the operator sees this account in the UI.</summary>
    public override string ToString() => Form == AccountNameForm.DownLevel ? $"{Domain}\\{User}" : User;

    private static bool LooksLikeDistinguishedName(string name)
    {
        var equals = name.IndexOf('=', StringComparison.Ordinal);
        return equals > 0
            && name.Contains(',', StringComparison.Ordinal)
            && name[..equals].All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    }
}
