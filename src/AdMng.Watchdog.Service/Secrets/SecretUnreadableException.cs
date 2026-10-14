namespace AdMng.Watchdog.Service.Secrets;

/// <summary>
/// A stored secret cannot be decrypted (DR: the gMSA was recreated or the key ring lost, spec 18). The service keeps
/// running; the affected channel reports health <c>secret.unreadable</c> until an admin re-enters the secret.
/// The message names the secret (e.g. "smtp.password"), never its value.
/// </summary>
public sealed class SecretUnreadableException(string name, Exception? inner = null)
    : Exception($"The stored secret '{name}' cannot be decrypted with the current key ring.", inner)
{
    /// <summary>Health check code reported for the affected channel.</summary>
    public const string HealthCode = "secret.unreadable";

    public string SecretName { get; } = name;
}
