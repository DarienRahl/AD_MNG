namespace AdMng.Watchdog.IntegrationTests.EndToEnd;

/// <summary>An e-mail as the SMTP sink received it, decoded (subject, recipients, bodies) plus its raw source.</summary>
public sealed record ReceivedMail(string Subject, IReadOnlyList<string> To, string Html, string Text, string Raw);
