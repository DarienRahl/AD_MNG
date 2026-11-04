namespace AdMng.Core.Readiness;

/// <summary>How far the clocks of this computer and the DC are apart.</summary>
public enum ClockSkewLevel
{
    /// <summary>Within <see cref="ReadinessRules.ClockSkewWarning"/>.</summary>
    Ok,

    /// <summary>Above the warning threshold but below the Kerberos limit.</summary>
    Warning,

    /// <summary>At or above <see cref="ReadinessRules.KerberosMaxSkew"/>: Kerberos refuses tickets.</summary>
    Error,
}

/// <summary>Whether a functional level is enough for the pilot (Windows Server 2025 and 2022 domain controllers).</summary>
public enum FunctionalLevelVerdict
{
    /// <summary>Windows Server 2016 or later (what a Windows Server 2025 DC requires).</summary>
    Ok,

    /// <summary>Windows Server 2008 to 2012 R2: AD Manager works, a Windows Server 2025 DC cannot join.</summary>
    Old,

    /// <summary>Below Windows Server 2008: no fine-grained password policies, FRS for SYSVOL.</summary>
    TooOld,
}

/// <summary>Thresholds and names of the readiness check (design note docs/dev/s-pilot-readiness.md 2). Pure.</summary>
public static class ReadinessRules
{
    /// <summary>Kerberos rejects tickets of a clock more than 5 minutes off (default MaxClockSkew).</summary>
    public static readonly TimeSpan KerberosMaxSkew = TimeSpan.FromMinutes(5);

    /// <summary>A skew above this is reported before it breaks Kerberos.</summary>
    public static readonly TimeSpan ClockSkewWarning = TimeSpan.FromMinutes(2);

    /// <summary>msDS-Behavior-Version of Windows Server 2016 (also the level of 2019 and 2022).</summary>
    public const int WindowsServer2016Level = 7;

    /// <summary>msDS-Behavior-Version of Windows Server 2008.</summary>
    public const int WindowsServer2008Level = 3;

    /// <summary>The level of a clock difference (sign ignored).</summary>
    public static ClockSkewLevel ClassifySkew(TimeSpan skew)
    {
        var magnitude = skew.Duration();
        return magnitude >= KerberosMaxSkew ? ClockSkewLevel.Error : magnitude > ClockSkewWarning ? ClockSkewLevel.Warning : ClockSkewLevel.Ok;
    }

    /// <summary>The verdict of a domain or forest functional level (msDS-Behavior-Version).</summary>
    public static FunctionalLevelVerdict ClassifyLevel(int level) =>
        level >= WindowsServer2016Level ? FunctionalLevelVerdict.Ok
        : level >= WindowsServer2008Level ? FunctionalLevelVerdict.Old
        : FunctionalLevelVerdict.TooOld;

    /// <summary>
    /// The product name of a functional level ("Windows Server 2016"); level 7 is also the level of 2019 and 2022, 10 the
    /// level of 2025. Null for a value no Windows Server release defines.
    /// </summary>
    public static string? LevelName(int level) => level switch
    {
        0 => "Windows 2000",
        1 => "Windows Server 2003 interim",
        2 => "Windows Server 2003",
        3 => "Windows Server 2008",
        4 => "Windows Server 2008 R2",
        5 => "Windows Server 2012",
        6 => "Windows Server 2012 R2",
        7 => "Windows Server 2016",
        10 => "Windows Server 2025",
        _ => null,
    };
}
