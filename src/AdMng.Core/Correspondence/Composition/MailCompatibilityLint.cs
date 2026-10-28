namespace AdMng.Core.Correspondence.Composition;

/// <summary>What may break in mail clients (MailerWPiA's compatibility warnings of the preview).</summary>
public enum MailLintKind
{
    Script,
    RemoteImage,
    DataImage,
    AbsolutePosition,
    BackgroundImage,
    TooWide,
    StyleBlock,
    LinkTextMismatch,
    LargeMessage,
}

/// <summary>One finding of <see cref="MailCompatibilityLint"/>.</summary>
/// <param name="Kind">The finding.</param>
/// <param name="Detail">Pixels for <see cref="MailLintKind.TooWide"/>, the host for a link mismatch, bytes for
/// <see cref="MailLintKind.LargeMessage"/>; else null.</param>
public sealed record MailLintFinding(MailLintKind Kind, string? Detail);

/// <summary>Mail client compatibility checks of a message document (the preview's warnings).</summary>
public static class MailCompatibilityLint
{
    /// <summary>The findings of <paramref name="html"/>; <paramref name="messageBytes"/> (images + attachments) adds
    /// <see cref="MailLintKind.LargeMessage"/> above <see cref="CorrespondenceLimits.MessageWarningBytes"/>.</summary>
    public static IReadOnlyList<MailLintFinding> Check(string? html, long messageBytes = 0) => throw new NotImplementedException("U5 T1 core");
}
