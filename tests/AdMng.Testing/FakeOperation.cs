using AdMng.Core.Directory;

namespace AdMng.Testing;

/// <summary>Kinds of calls recorded by <see cref="FakeDirectorySession"/>.</summary>
public enum FakeOperationKind
{
    Search,
    VirtualListPage,
    Get,
    GetAllValues,
    GetEffectivePermissions,
    Modify,
    Add,
    Delete,
    Move,
    ResetPassword,
    RestoreDeleted,
    Watch,
}

/// <summary>
/// One call made against the fake session (recorded after it completed, with its outcome). Passwords are not part of
/// the record - see <see cref="FakeDirectorySession.PasswordResets"/>.
/// </summary>
public sealed record FakeOperation
{
    public required FakeOperationKind Kind { get; init; }

    /// <summary>Target DN (search base for searches).</summary>
    public required string DistinguishedName { get; init; }

    public DirectorySearch? Search { get; init; }

    /// <summary>Requested attributes (Get) or the attribute (GetAllValues).</summary>
    public IReadOnlyList<string> Attributes { get; init; } = [];

    public IReadOnlyList<AttributeModification> Modifications { get; init; } = [];

    public ModifyOptions? ModifyOptions { get; init; }

    public IReadOnlyDictionary<string, IReadOnlyList<object>>? AddedAttributes { get; init; }

    public string? NewParentDn { get; init; }

    public string? NewRdn { get; init; }

    public bool Subtree { get; init; }

    /// <summary>Restore target DN.</summary>
    public string? TargetDn { get; init; }

    /// <summary>Null when the call succeeded, otherwise the error it failed with.</summary>
    public DirectoryErrorKind? Error { get; init; }

    public bool Succeeded => Error is null;

    public bool IsWrite => Kind is FakeOperationKind.Modify or FakeOperationKind.Add or FakeOperationKind.Delete
        or FakeOperationKind.Move or FakeOperationKind.ResetPassword or FakeOperationKind.RestoreDeleted;

    public override string ToString() => $"{Kind} {DistinguishedName}{(Error is null ? string.Empty : $" -> {Error}")}";
}

/// <summary>A password reset performed against the fake (test-only record of the password that was set).</summary>
public sealed record FakePasswordReset(string DistinguishedName, string Password)
{
    /// <summary>Keeps passwords out of assertion messages and logs.</summary>
    public override string ToString() => $"ResetPassword {DistinguishedName}";
}
