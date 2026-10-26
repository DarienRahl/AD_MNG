namespace AdMng.Watchdog.Core.Rules.Validation;

/// <summary>
/// Section <c>userMessages</c> (README U1b): <c>range</c> of the hourly (1–10 000) and daily (1–100 000) limits,
/// <c>required</c> / <c>invalidAddress</c> for every allowed sender (a whole address or <c>@domain</c>) and the warning
/// <c>smtpDisabledButUsed</c> when messages to users are enabled while e-mail (SMTP) is disabled.
/// </summary>
internal static class UserMessagingValidator
{
    public static void Validate(ValidationRun run)
    {
        var settings = run.Configuration.UserMessages;
        RangeCheck.Check(run, "userMessages.maxMessagesPerHour", settings.MaxMessagesPerHour, 1, 10_000);
        RangeCheck.Check(run, "userMessages.maxMessagesPerDay", settings.MaxMessagesPerDay, 1, 100_000);
        for (var i = 0; i < settings.AllowedSenders.Count; i++)
        {
            var path = $"userMessages.allowedSenders[{i}]";
            var entry = settings.AllowedSenders[i];
            if (string.IsNullOrWhiteSpace(entry))
            {
                run.Error(path, ConfigValidationCodes.Required, "An allowed sender must not be empty.");
            }
            else if (!IsSenderPattern(entry))
            {
                run.Error(path, ConfigValidationCodes.InvalidAddress, "An allowed sender is an e-mail address or @domain.");
            }
        }

        if (settings.Enabled && !run.Configuration.Smtp.Enabled)
        {
            run.Warning("userMessages.enabled", ConfigValidationCodes.SmtpDisabledButUsed,
                "Messages to users are enabled, but e-mail (SMTP) is disabled – nothing can be sent.");
        }
    }

    /// <summary>"helpdesk@contoso.com" (no display name) or "@contoso.com".</summary>
    public static bool IsSenderPattern(string entry)
    {
        var value = entry.Trim();
        if (value.StartsWith('@'))
        {
            var domain = value[1..];
            return domain.Length is > 0 and <= 253 && !domain.StartsWith('.') && !domain.EndsWith('.')
                && !domain.Contains("..", StringComparison.Ordinal) && domain.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-');
        }

        return EmailAddressRules.IsValid(value) && !value.Contains('<', StringComparison.Ordinal) && !value.Contains(' ', StringComparison.Ordinal);
    }
}
