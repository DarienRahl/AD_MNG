namespace AdMng.Watchdog.Core.Rules.Validation;

/// <summary><c>invalidAddress</c> (8.3) for every recipient group address.</summary>
internal static class RecipientGroupValidator
{
    public static void Validate(ValidationRun run)
    {
        var groups = run.Configuration.RecipientGroups;
        for (var i = 0; i < groups.Count; i++)
        {
            if (groups[i] is not { } group)
            {
                continue;
            }

            for (var j = 0; j < group.Addresses.Count; j++)
            {
                if (!EmailAddressRules.IsValid(group.Addresses[j]))
                {
                    run.Error(ValidationRun.Item(ValidationRun.Item("recipientGroups", i) + ".addresses", j),
                        ConfigValidationCodes.InvalidAddress, "Not a valid e-mail address.");
                }
            }
        }
    }
}
