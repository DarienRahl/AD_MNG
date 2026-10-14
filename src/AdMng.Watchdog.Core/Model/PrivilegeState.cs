namespace AdMng.Watchdog.Core.Model;

public enum PrivilegeTier { High = 3, Critical = 4 }

/// <summary>A principal that reaches a privileged group (directly, nested or through its primary group).</summary>
public sealed record PrivilegedPrincipal
{
    public required string PrincipalKey { get; init; }           // MemberReference.Key
    public required Guid GroupGuid { get; init; }                // privileged group reached
    public required PrivilegeTier Tier { get; init; }
    public IReadOnlyList<Guid> Path { get; init; } = [];        // intermediate groups (direct = empty)
    public bool ViaPrimaryGroup { get; init; }
}

/// <summary>The transitive privileged set computed by the PrivilegeEvaluator.</summary>
public sealed record PrivilegeState
{
    public IReadOnlyList<PrivilegedPrincipal> Principals { get; init; } = [];
    public DateTimeOffset ComputedAt { get; init; }
}
