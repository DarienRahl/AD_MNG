using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>A secret released by <see cref="DestinationGuard"/> for one destination, or why it was not released
/// (<see cref="FailureCode"/>: <c>secret.missing</c>, <c>secret.rebindRequired</c>, <c>secret.unreadable</c>,
/// <c>oauthHostNotAllowed</c>, <c>teams.hostNotAllowed</c>).</summary>
public sealed record GuardedSecret
{
    public SecretValue? Value { get; init; }

    public string? FailureCode { get; init; }

    /// <summary>A <c>DeliveryHints</c> code for the operator.</summary>
    public string? Hint { get; init; }

    public bool IsAvailable => Value is not null;

    public static GuardedSecret Released(SecretValue value) => new() { Value = value };

    public static GuardedSecret Refused(string failureCode, string? hint = null) => new() { FailureCode = failureCode, Hint = hint };

    public override string ToString() => IsAvailable ? "GuardedSecret(released)" : $"GuardedSecret({FailureCode})";
}
