using AdMng.Core.ActiveDirectory;
using AdMng.Core.Model;

namespace AdMng.Presentation.Session;

/// <summary>Lock state of an account as far as the client can tell from lockoutTime and the domain policy.</summary>
public enum LockState
{
    NotLocked,
    Locked,

    /// <summary>lockoutTime is set but the lockout duration is unknown (policy not readable, or fine-grained policy may apply).</summary>
    PossiblyLocked,
}

/// <summary>
/// Domain-wide account policy read once per session from the domain root object (lockoutDuration,
/// lockoutThreshold, maxPwdAge). Metadata only - kept in memory with the session.
/// </summary>
public sealed record DomainPolicy
{
    public const string LockoutDurationAttribute = "lockoutDuration";
    public const string LockoutThresholdAttribute = "lockoutThreshold";
    public const string MaxPasswordAgeAttribute = "maxPwdAge";

    /// <summary>Attributes to read from the domain root object.</summary>
    public static IReadOnlyList<string> Attributes { get; } = [LockoutDurationAttribute, LockoutThresholdAttribute, MaxPasswordAgeAttribute];

    /// <summary>Policy that could not be read: lock states are reported as <see cref="LockState.PossiblyLocked"/>.</summary>
    public static DomainPolicy Unknown { get; } = new();

    /// <summary>True when the values were read from the domain object.</summary>
    public bool IsKnown { get; init; }

    /// <summary>Lockout duration; null = locked until an administrator unlocks the account.</summary>
    public TimeSpan? LockoutDuration { get; init; }

    public int LockoutThreshold { get; init; }

    /// <summary>Maximum password age; null = passwords never expire by domain policy.</summary>
    public TimeSpan? MaxPasswordAge { get; init; }

    public static DomainPolicy FromDomainObject(DirectoryObject domain)
    {
        ArgumentNullException.ThrowIfNull(domain);
        var duration = domain.GetInt64(LockoutDurationAttribute);
        var maxAge = domain.GetInt64(MaxPasswordAgeAttribute);
        return new DomainPolicy
        {
            IsKnown = duration is not null,
            LockoutDuration = duration is { } d ? AdConvert.FromInterval(d) : null,
            LockoutThreshold = domain.GetInt32(LockoutThresholdAttribute) ?? 0,
            MaxPasswordAge = maxAge is { } m ? AdConvert.FromInterval(m) : null,
        };
    }

    /// <summary>Evaluates lockoutTime (FILETIME) against the lockout duration.</summary>
    public LockState GetLockState(long? lockoutTime, DateTimeOffset now)
    {
        if (lockoutTime is null or <= 0)
        {
            return LockState.NotLocked;
        }

        if (!IsKnown)
        {
            return LockState.PossiblyLocked;
        }

        if (LockoutDuration is not { } duration)
        {
            return LockState.Locked;
        }

        var lockedAt = AdConvert.FromFileTime(lockoutTime.Value);
        return lockedAt is null || lockedAt.Value + duration > now ? LockState.Locked : LockState.NotLocked;
    }
}
