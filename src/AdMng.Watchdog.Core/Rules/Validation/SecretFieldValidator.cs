using AdMng.Watchdog.Core.Configuration;

namespace AdMng.Watchdog.Core.Rules.Validation;

/// <summary>
/// Secret fields (8.3, 18): <c>secretWithoutChannel</c> when <c>NewValue</c> and <c>Clear</c> are both set, and
/// <c>secretRebindRequired</c> when a stored secret would be used for another destination than the one it was entered
/// for (<see cref="SecretBindings.RequiresRebind"/> with the validator's view of the URL host,
/// <see cref="SecretBindings.DeclaredUrlHost"/>).
/// </summary>
internal static class SecretFieldValidator
{
    public static void Validate(ValidationRun run)
    {
        foreach (var secret in SecretBindings.Enumerate(run.Configuration))
        {
            if (!string.IsNullOrEmpty(secret.Field.NewValue) && secret.Field.Clear)
            {
                run.Error(secret.Path, ConfigValidationCodes.SecretWithoutChannel,
                    "A secret cannot be replaced and cleared in the same save.");
            }
            else if (SecretBindings.RequiresRebind(secret, run.Context.StoredSecrets))
            {
                run.Error(secret.Path, ConfigValidationCodes.SecretRebindRequired,
                    "The destination of this secret changed – enter the secret again (stored secrets are bound to their destination).");
            }
        }
    }
}
