namespace AdMng.Watchdog.Keytab;

/// <summary>
/// The service's computer account principal, e.g. <c>ADMNG-WD$@CONTOSO.COM</c>, and what follows from it: the AD salt
/// (design 4.3) and the principals of the keytab in their required order (account first – 4.4).
/// </summary>
internal sealed record AccountPrincipal
{
    private AccountPrincipal(string name, string realm)
    {
        Name = name;
        Realm = realm;
    }

    /// <summary>sAMAccountName, e.g. <c>ADMNG-WD$</c>.</summary>
    public string Name { get; }

    public string Realm { get; }

    public string Principal => Name + "@" + Realm;

    /// <summary>The account name without <c>$</c>, e.g. <c>ADMNG-WD</c>.</summary>
    public string ShortName => Name[..^1];

    /// <summary>AD salt of a computer account: <c>&lt;REALM&gt;host&lt;name lower-case&gt;.&lt;domain lower-case&gt;</c>.</summary>
    public string Salt => Realm + "host" + ShortName.ToLowerInvariant() + "." + Realm.ToLowerInvariant();

    /// <summary>Default DNS name of the host: <c>&lt;name&gt;.&lt;realm&gt;</c> in lower case.</summary>
    public string DefaultFqdn => ShortName.ToLowerInvariant() + "." + Realm.ToLowerInvariant();

    /// <summary>A computer account principal: one component ending in <c>$</c>, an explicit realm.</summary>
    /// <exception cref="ArgumentException">Not of that form.</exception>
    public static AccountPrincipal Parse(string principal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        var at = principal.LastIndexOf('@');
        if (at <= 1 || at == principal.Length - 1)
        {
            throw new ArgumentException("The principal must be NAME$@REALM.", nameof(principal));
        }

        var name = principal[..at];
        var realm = principal[(at + 1)..];
        if (!name.EndsWith('$') || name.IndexOfAny(['/', '@', '\\', ' ']) >= 0 || realm.IndexOfAny(['/', '@', ' ']) >= 0)
        {
            throw new ArgumentException("The principal must be a computer account (NAME$@REALM).", nameof(principal));
        }

        return new AccountPrincipal(name, realm);
    }

    /// <summary>The keytab principals for a host name: account, <c>HTTP/</c>, <c>host/</c> (design 4.3 order).</summary>
    public IReadOnlyList<string> PrincipalsFor(string fqdn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);
        var host = fqdn.Trim().TrimEnd('.').ToLowerInvariant();
        return [Principal, $"HTTP/{host}@{Realm}", $"host/{host}@{Realm}"];
    }
}
