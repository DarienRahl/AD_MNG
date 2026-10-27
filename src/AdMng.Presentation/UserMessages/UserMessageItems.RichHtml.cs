using System.Globalization;
using AdMng.Core.Messaging;
using AdMng.Core.Messaging.Html;
using AdMng.Core.Messaging.Images;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.UserMessages;

/// <summary>
/// What the rendered preview shows (U1c, design note u1c-u1d section 10): an HTML body and its inline images by content id –
/// both from memory. The App's preview answers <c>cid:</c> references from <see cref="Images"/> and nothing else.
/// </summary>
public sealed record HtmlPreviewContent(string Html, IReadOnlyDictionary<string, MessageImage> Images)
{
    public static HtmlPreviewContent Empty { get; } = new(string.Empty, new Dictionary<string, MessageImage>());

    public bool IsEmpty => string.IsNullOrWhiteSpace(Html);

    public override string ToString() => $"HtmlPreviewContent({Html.Length} characters, {Images.Count} images)";
}

/// <summary>An image of the edited template in the image strip: thumbnail bytes, name, size, dimensions.</summary>
public sealed class MessageImageItemViewModel(MessageImage image, ILocalizer localizer)
{
    public MessageImage Image { get; } = image ?? throw new ArgumentNullException(nameof(image));

    public string ContentId => Image.ContentId;

    public string Name => Image.FileName;

    /// <summary>The image bytes (the view decodes a thumbnail).</summary>
    public byte[] Data => Image.Data;

    /// <summary>"640 × 120 px, 18 KB".</summary>
    public string SizeText => localizer.Format(L.UserMessagesRich.ImageSize, Image.Width, Image.Height, Bytes(Image.Data.Length, localizer.Culture));

    public override string ToString() => $"MessageImageItemViewModel({ContentId})";

    /// <summary>"18 KB", "1,2 MB".</summary>
    public static string Bytes(long bytes, CultureInfo culture) => bytes < 1024 * 1024
        ? string.Format(culture, "{0:N0} KB", Math.Max(1, (bytes + 1023) / 1024))
        : string.Format(culture, "{0:N1} MB", bytes / 1024d / 1024d);
}

/// <summary>A domain the operator may allow for a link outside the allowed domains ("Zezwól na domenę …").</summary>
public sealed record HtmlPasteDomainChoice(HtmlPasteProblemViewModel Problem, string Domain, string Text);

/// <summary>One line of the paste results: a problem kind with its detail, how often it occurred and its actions.</summary>
public sealed class HtmlPasteProblemViewModel
{
    public HtmlPasteProblemViewModel(HtmlImportProblem problem, int count, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(localizer);
        Problem = problem;
        Count = count;
        // The detail of an image or link problem is a reason code ("TooLarge", "network" …): shown in the operator's language.
        var detail = problem.Kind is HtmlImportProblemKind.ImageRejected or HtmlImportProblemKind.ImageUnreadable or HtmlImportProblemKind.LinkRemoved
            ? UserMessageText.Reason(localizer, problem.Detail)
            : problem.Detail ?? string.Empty;
        var text = localizer.Format(L.UserMessagesRich.ProblemKey(problem.Kind), detail);
        Text = count > 1 ? localizer.Format(L.UserMessagesRich.ProblemCount, text, count) : text;
        if (problem.Kind == HtmlImportProblemKind.LinkOutsideAllowedDomains && problem.Detail is { Length: > 0 } host)
        {
            DomainChoices = [.. LinkDomainSuggestions.For(host).Select(d => new HtmlPasteDomainChoice(this, d, localizer.Format(L.UserMessagesRich.AllowDomain, d)))];
        }
    }

    public HtmlImportProblem Problem { get; }

    public HtmlImportProblemKind Kind => Problem.Kind;

    public string? Detail => Problem.Detail;

    public int Count { get; }

    public string Text { get; }

    public MessageIssueSeverity Severity => Problem.Severity;

    public bool IsWarning => Problem.Severity != MessageIssueSeverity.Info;

    /// <summary>"Zezwól na domenę …" choices (the host and its parent domains).</summary>
    public IReadOnlyList<HtmlPasteDomainChoice> DomainChoices { get; } = [];

    public bool CanAllowDomain => DomainChoices.Count > 0;

    /// <summary>"Usuń łącze" applies (a link to a host).</summary>
    public bool CanRemoveLink => Problem.Kind is HtmlImportProblemKind.LinkOutsideAllowedDomains or HtmlImportProblemKind.LinkTextMismatch
                                 && !string.IsNullOrWhiteSpace(Problem.Detail);

    /// <summary>The problems of a paste, grouped by kind and detail (warnings first).</summary>
    public static IReadOnlyList<HtmlPasteProblemViewModel> Group(IEnumerable<HtmlImportProblem> problems, ILocalizer localizer) =>
        [.. problems
            .GroupBy(p => (p.Kind, p.Detail, p.Severity))
            .OrderByDescending(g => g.Key.Severity)
            .ThenBy(g => g.Key.Kind)
            .Select(g => new HtmlPasteProblemViewModel(g.First(), g.Count(), localizer))];

    public override string ToString() => $"HtmlPasteProblemViewModel({Kind}, {Count})";
}

/// <summary>U1c: the inline images and the rendered preview of a planned message.</summary>
public sealed partial class UserMessageItemViewModel
{
    /// <summary>The images that travel with the message (in order of first reference).</summary>
    public IReadOnlyList<MessageImage> InlineImages => Planned.Message?.InlineImages ?? [];

    public bool HasInlineImages => InlineImages.Count > 0;

    /// <summary>"Obrazy w treści: 2 (48 KB)", or empty.</summary>
    public string InlineImagesText => HasInlineImages
        ? _localizer.Format(L.UserMessagesRich.InlineImages, InlineImages.Count, MessageImageItemViewModel.Bytes(InlineImages.Sum(i => (long)i.Data.Length), _localizer.Culture))
        : string.Empty;

    /// <summary>What the "Widok" tab renders: the HTML body exactly as it is sent, with its images from memory.</summary>
    public HtmlPreviewContent HtmlPreview => Planned.Message is { } message
        ? new HtmlPreviewContent(message.HtmlBody, message.InlineImages.DistinctBy(i => i.ContentId, StringComparer.OrdinalIgnoreCase).ToDictionary(i => i.ContentId, StringComparer.OrdinalIgnoreCase))
        : HtmlPreviewContent.Empty;
}
