using System.Globalization;
using System.Resources;

namespace AdMng.Core.Messaging;

/// <summary>
/// The built-in message templates (README U1) with their Polish and English texts from
/// <c>Messaging/Resources/MessageTemplates(.pl).resx</c> (keys <c>{id}.Name</c>, <c>{id}.Subject</c>, <c>{id}.Body</c>) and
/// the fixed footer (<c>Footer</c>). Built-ins cannot be changed; the operator saves a copy.
/// </summary>
public static class BuiltInMessageTemplates
{
    public const string PasswordExpiring = "password-expiring";
    public const string PasswordExpired = "password-expired";
    public const string AccountExpiring = "account-expiring";
    public const string AccountExpired = "account-expired";
    public const string AccountLocked = "account-locked";
    public const string AccountUnlocked = "account-unlocked";
    public const string MustChangePassword = "must-change-password";
    public const string InactiveDisable = "inactive-disable";
    public const string Custom = "custom";

    /// <summary>Base name of the resources.</summary>
    public const string ResourceBaseName = "AdMng.Core.Messaging.Resources.MessageTemplates";

    private static readonly ResourceManager Resources = new(ResourceBaseName, typeof(BuiltInMessageTemplates).Assembly);

    private static readonly (string Id, MessageConditionKind Condition, int Days, MessageRecipientMode Recipients)[] Definitions =
    [
        (PasswordExpiring, MessageConditionKind.PasswordExpiresWithin, 14, MessageRecipientMode.User),
        (PasswordExpired, MessageConditionKind.PasswordExpired, 0, MessageRecipientMode.User),
        (AccountExpiring, MessageConditionKind.AccountExpiresWithin, 30, MessageRecipientMode.UserAndManager),
        (AccountExpired, MessageConditionKind.AccountExpired, 0, MessageRecipientMode.UserAndManager),
        (AccountLocked, MessageConditionKind.Locked, 0, MessageRecipientMode.User),
        (AccountUnlocked, MessageConditionKind.NotLocked, 0, MessageRecipientMode.User),
        (MustChangePassword, MessageConditionKind.MustChangePassword, 0, MessageRecipientMode.User),
        (InactiveDisable, MessageConditionKind.InactiveFor, 90, MessageRecipientMode.UserAndManager),
        (Custom, MessageConditionKind.None, 0, MessageRecipientMode.User),
    ];

    /// <summary>Ids in display order.</summary>
    public static IReadOnlyList<string> Ids { get; } = [.. Definitions.Select(d => d.Id)];

    /// <summary>True for the id of a built-in template.</summary>
    public static bool IsBuiltIn(string? id) => id is not null && Ids.Contains(id, StringComparer.Ordinal);

    /// <summary>The built-in templates; <see cref="MessageTemplate.Name"/> in <paramref name="uiLanguage"/>.</summary>
    public static IReadOnlyList<MessageTemplate> All(MessageLanguage uiLanguage) => [.. Ids.Select(id => Get(id, uiLanguage)!)];

    /// <summary>A built-in template, or null for another id.</summary>
    public static MessageTemplate? Get(string id, MessageLanguage uiLanguage)
    {
        foreach (var (definitionId, condition, days, recipients) in Definitions)
        {
            if (definitionId == id)
            {
                return new MessageTemplate
                {
                    Id = id,
                    Name = Text($"{id}.Name", uiLanguage),
                    Polish = TextOf(id, MessageLanguage.Polish),
                    English = TextOf(id, MessageLanguage.English),
                    Condition = condition,
                    ConditionDays = days,
                    Recipients = recipients,
                };
            }
        }

        return null;
    }

    /// <summary>Default display name of the sender ("Dział IT").</summary>
    public static string SenderName(MessageLanguage language) => Text("SenderName", language);

    /// <summary>The fixed footer of every message ("we never ask for your password").</summary>
    public static string Footer(MessageLanguage language) => Text("Footer", language);

    private static MessageTemplateText TextOf(string id, MessageLanguage language) => new()
    {
        Subject = Text($"{id}.Subject", language),
        Body = Text($"{id}.Body", language),
    };

    private static string Text(string key, MessageLanguage language) =>
        (Resources.GetString(key, Culture(language)) ?? string.Empty).ReplaceLineEndings("\n");

    private static CultureInfo Culture(MessageLanguage language) =>
        language == MessageLanguage.English ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo("pl");
}
