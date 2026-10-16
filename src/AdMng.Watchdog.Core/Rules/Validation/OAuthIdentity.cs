using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Core.Rules.Validation;

/// <summary><c>required</c> for an Entra client-credentials identity (SMTP OAuth2, Teams OAuth): tenant id and client id
/// as GUIDs and a certificate thumbprint or a client secret (8.3).</summary>
internal static class OAuthIdentity
{
    public static void Validate(ValidationRun run, string path, string? tenantId, string? clientId, string? thumbprint,
        SecretField? clientSecret, string clientSecretName)
    {
        if (!Guid.TryParse(tenantId, out _))
        {
            run.Error(path + ".tenantId", ConfigValidationCodes.Required, "The tenant id (GUID) is required.");
        }

        if (!Guid.TryParse(clientId, out _))
        {
            run.Error(path + ".clientId", ConfigValidationCodes.Required, "The client (application) id (GUID) is required.");
        }

        if (string.IsNullOrWhiteSpace(thumbprint) && !SecretPresence.HasValue(clientSecret, clientSecretName, run.Context))
        {
            run.Error(path + ".clientSecret", ConfigValidationCodes.Required,
                "A certificate thumbprint (preferred) or a client secret is required.");
        }
    }
}
