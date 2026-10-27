using AdMng.Core.Directory;

namespace AdMng.Presentation.Connections;

/// <summary>
/// Who a connection window is: domain, bound account, DC, profile and its identity colour. Immutable: child windows take
/// the snapshot of the moment they open (a property sheet of the old domain keeps naming it after a reconnect). Holds no
/// credential and no directory values beyond the session facts the title bar shows anyway.
/// </summary>
public sealed record ConnectionIdentity
{
    public required string DomainDnsName { get; init; }

    public string DomainNetBiosName { get; init; } = string.Empty;

    public string DefaultNamingContext { get; init; } = string.Empty;

    /// <summary>"CONTOSO\jan.kowalski" (empty when the DC did not say).</summary>
    public string BoundIdentity { get; init; } = string.Empty;

    /// <summary>The DC all operations of the window go to.</summary>
    public string Server { get; init; } = string.Empty;

    public TransportSecurity Security { get; init; }

    /// <summary>The connection profile the connect dialog used, or null.</summary>
    public string? ProfileName { get; init; }

    public ConnectionColor Color { get; init; }

    public ConnectionColorSource ColorSource { get; init; }

    /// <summary>1, or 2, 3… when a window of the identical domain and account is already open.</summary>
    public int Ordinal { get; init; } = 1;

    /// <summary>The account with the ordinal of a second identical window: "CONTOSO\jan.kowalski (2)".</summary>
    public string Account => Ordinal > 1
        ? $"{(BoundIdentity.Length > 0 ? BoundIdentity : DomainNetBiosName)} ({Ordinal})"
        : BoundIdentity;

    /// <summary>"contoso.test · CONTOSO\jan.kowalski" (the badge, the window switcher, dialog headers).</summary>
    public string Label => Account.Length > 0 ? $"{DomainDnsName} · {Account}" : DomainDnsName;

    /// <summary>Same domain and same account (the DC may differ).</summary>
    public bool IsSameIdentity(string domainDnsName, string boundIdentity) =>
        string.Equals(DomainDnsName, domainDnsName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(BoundIdentity, boundIdentity, StringComparison.OrdinalIgnoreCase);

    /// <summary>Never the account or the domain (diagnostic logs carry no directory values).</summary>
    public override string ToString() => $"ConnectionIdentity({Color}, {ColorSource}, ordinal {Ordinal})";

    /// <summary>The identity of a session (colour and ordinal assigned by <see cref="ConnectionRegistry"/>).</summary>
    internal static ConnectionIdentity From(SessionInfo info, string? profileName, ConnectionColor color, ConnectionColorSource source, int ordinal) => new()
    {
        DomainDnsName = info.DomainDnsName,
        DomainNetBiosName = info.DomainNetBiosName ?? string.Empty,
        DefaultNamingContext = info.DefaultNamingContext,
        BoundIdentity = info.BoundIdentity,
        Server = info.Server,
        Security = info.Security,
        ProfileName = profileName,
        Color = color,
        ColorSource = source,
        Ordinal = ordinal,
    };
}
