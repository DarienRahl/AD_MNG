using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Rules.Validation;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>
/// Validation of the runtime configuration (section 8.3) – pure, no I/O. Errors reject a save (400 with
/// <c>issues</c>), warnings are shown but allow it. Every code is in <see cref="ConfigValidationCodes"/>; paths are JSON
/// paths of the camelCase document (<c>rules[3].delivery.channelIds[0]</c>). Explicit nulls in the document are
/// repaired first (<see cref="ConfigurationNormalizer"/>); null list entries are <c>required</c> errors.
/// </summary>
/// <remarks>The live-schema check of <c>additionalWatchedAttributes</c> (<c>schemaUnavailable</c>) is the service's;
/// <c>rolloutOff</c> uses the baseline time from <see cref="IHealthRegistry.Directory"/> when a registry is available
/// (unknown baseline = young).</remarks>
public sealed class ConfigurationValidator : IConfigurationValidator
{
    private readonly TimeProvider _timeProvider;
    private readonly IHealthRegistry? _healthRegistry;

    public ConfigurationValidator(TimeProvider timeProvider, IHealthRegistry? healthRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _healthRegistry = healthRegistry;
    }

    /// <inheritdoc/>
    public ConfigValidationResult Validate(WatchdogConfiguration configuration, ConfigValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(context);
        var run = new ValidationRun(ConfigurationNormalizer.Normalize(configuration), context, _timeProvider.GetUtcNow(), BaselineCompletedAt());
        GeneralValidator.Validate(run);
        RangeValidator.Validate(run);
        IdentifierValidator.Validate(run);
        DnAndSidValidator.Validate(run);
        AttributeNameValidator.Validate(run);
        Tier0SuppressionValidator.Validate(run);
        AttributionValidator.Validate(run);
        SmtpValidator.Validate(run);
        UserMessagingValidator.Validate(run);
        ChannelValidator.Validate(run);
        SecretFieldValidator.Validate(run);
        RecipientGroupValidator.Validate(run);
        RuleValidator.Validate(run);
        return run.ToResult();
    }

    /// <summary>
    /// Validation of a PUT body: a redacted envelope (<see cref="ConfigEnvelope.IsRedacted"/>, the Viewer view) is
    /// rejected with <c>redactedConfig</c> before anything else (8.2).
    /// </summary>
    public ConfigValidationResult Validate(ConfigEnvelope envelope, ConfigValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(context);
        if (envelope.IsRedacted)
        {
            return new ConfigValidationResult
            {
                Issues =
                [
                    new ConfigIssue
                    {
                        Path = "isRedacted",
                        Code = ConfigValidationCodes.RedactedConfig,
                        Severity = IssueSeverity.Error,
                        Message = "A redacted (Viewer) configuration can never be saved.",
                    },
                ],
            };
        }

        return envelope.Configuration is null
            ? new ConfigValidationResult
            {
                Issues = [new ConfigIssue { Path = "configuration", Code = ConfigValidationCodes.Required, Severity = IssueSeverity.Error, Message = "The configuration is missing." }],
            }
            : Validate(envelope.Configuration, context);
    }

    private DateTimeOffset? BaselineCompletedAt() => _healthRegistry?.Directory?.BaselineCompletedAt;
}
