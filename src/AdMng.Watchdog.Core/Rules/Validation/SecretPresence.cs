using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Rules.Validation;

/// <summary>Whether a secret will have a value after the save: a new value is supplied, or one is stored
/// (<see cref="ConfigValidationContext.StoredSecrets"/>) and not being cleared. <see cref="SecretField.IsSet"/> of the
/// document is not trusted – a client cannot create a secret by claiming it is set.</summary>
internal static class SecretPresence
{
    public static bool HasValue(SecretField? field, string secretName, ConfigValidationContext context)
    {
        if (field is null)
        {
            return context.StoredSecrets.ContainsKey(secretName);
        }

        return !string.IsNullOrEmpty(field.NewValue) || (!field.Clear && context.StoredSecrets.ContainsKey(secretName));
    }
}
