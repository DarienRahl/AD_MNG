using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.GroupPolicy;

namespace AdMng.Core.Computers;

/// <summary>What a computer account is (K1 design note 4.1).</summary>
public enum ComputerType
{
    Workstation,
    Server,
    DomainController,
    ReadOnlyDomainController,

    /// <summary>No operatingSystem: prestaged and never joined, or a system that sets nothing (non-Windows).</summary>
    Unknown,
}

/// <summary>
/// Classifies computer accounts: userAccountControl SERVER_TRUST_ACCOUNT → DC, PARTIAL_SECRETS_ACCOUNT → RODC (primary group
/// 516 / 521 as a second signal), an operatingSystem naming a server → server, a Windows client (or another named system)
/// → workstation, no operatingSystem → unknown.
/// </summary>
public static class ComputerClassifier
{
    /// <summary>The type of a computer from its account flags, primary group and operating system string.</summary>
    public static ComputerType Classify(UserAccountControl accountControl, int? primaryGroupId, string? operatingSystem)
    {
        if (accountControl.HasFlag(UserAccountControl.PartialSecretsAccount) || primaryGroupId == (int)WellKnownRids.ReadOnlyDomainControllers)
        {
            return ComputerType.ReadOnlyDomainController;
        }

        if (accountControl.HasFlag(UserAccountControl.ServerTrustAccount) || primaryGroupId == (int)WellKnownRids.DomainControllers)
        {
            return ComputerType.DomainController;
        }

        if (string.IsNullOrWhiteSpace(operatingSystem))
        {
            return ComputerType.Unknown;
        }

        return operatingSystem.Contains("Server", StringComparison.OrdinalIgnoreCase) ? ComputerType.Server : ComputerType.Workstation;
    }

    /// <summary>A (read-only) domain controller.</summary>
    public static bool IsDomainController(this ComputerType type) =>
        type is ComputerType.DomainController or ComputerType.ReadOnlyDomainController;

    /// <summary>The role the Group Policy code of U4c uses (an unknown type counts as a workstation).</summary>
    public static ComputerRole ToRole(this ComputerType type) => type switch
    {
        ComputerType.DomainController or ComputerType.ReadOnlyDomainController => ComputerRole.DomainController,
        ComputerType.Server => ComputerRole.Server,
        _ => ComputerRole.Workstation,
    };
}

/// <summary>operatingSystemVersion as AD stores it: <c>10.0 (26100)</c>, <c>6.3 (9600)</c>, <c>10.0</c>.</summary>
/// <param name="Major">Major version.</param>
/// <param name="Minor">Minor version.</param>
/// <param name="Build">Build number, null when the value has none.</param>
public sealed record OperatingSystemVersion(int Major, int Minor, int? Build)
{
    /// <summary>Parses the value; false for anything else (Samba, non-Windows strings).</summary>
    public static bool TryParse(string? value, out OperatingSystemVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        int? build = null;
        var open = text.IndexOf('(', StringComparison.Ordinal);
        if (open >= 0)
        {
            var close = text.IndexOf(')', open + 1);
            if (close < 0 || !int.TryParse(text.AsSpan(open + 1, close - open - 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                return false;
            }

            build = parsed;
            text = text[..open].Trim();
        }

        var parts = text.Split('.');
        if (parts.Length < 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return false;
        }

        if (build is null && parts.Length >= 3 && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var third))
        {
            build = third;
        }

        version = new OperatingSystemVersion(major, minor, build);
        return true;
    }

    /// <summary><c>10.0.26100</c>.</summary>
    public override string ToString() => Build is { } build
        ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{build}")
        : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}");
}
