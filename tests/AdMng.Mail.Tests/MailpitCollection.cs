namespace AdMng.Mail.Tests;

/// <summary>
/// The Mailpit test classes share one Mailpit: <see cref="MailpitTests"/> sets chaos triggers (every recipient refused
/// with 550 / 451 for a while), so a message of another class sent at that moment would be refused. They run one after
/// another.
/// </summary>
[CollectionDefinition(Name)]
public sealed class MailpitCollection
{
    public const string Name = "Mailpit";
}
