using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Messaging.Html;
using AdMng.Core.Messaging.Images;
using AdMng.Testing;

namespace AdMng.Core.Tests.Correspondence.Composition;

/// <summary>Drafts, units and images of the composition tests.</summary>
internal static class CompositionTestData
{
    public static readonly string[] Columns = ["Nazwa", "E-mail", "Miejscowość", "Kod pocztowy", "Telefon"];

    public static Dictionary<string, string> Row(string name = "Urząd Gminy Żórawina", string town = "Żórawina", string phone = "") => new(StringComparer.Ordinal)
    {
        ["Nazwa"] = name,
        ["E-mail"] = "ug@zorawina.pl",
        ["Miejscowość"] = town,
        ["Kod pocztowy"] = "55-020",
        ["Telefon"] = phone,
    };

    public static CorrespondenceUnit Unit(params IReadOnlyDictionary<string, string>[] rows) =>
        new("ug@zorawina.pl", "Urząd Gminy Żórawina", rows.Length == 0 ? [Row()] : rows);

    public static MessageImage Image(byte seed, int width = 4, int height = 3) =>
        MessageImage.Create(MessageImageSamples.Png(width, height, seed), $"obraz{seed}.png");

    public static IReadOnlyDictionary<string, MessageImage> ById(params MessageImage[] images) =>
        images.ToDictionary(i => i.ContentId, StringComparer.OrdinalIgnoreCase);

    /// <summary>The document sanitized again with its images: the renderer's output must not change.</summary>
    public static string Resanitized(RenderedCorrespondence rendered) =>
        MessageHtmlSanitizer.Sanitize(rendered.HtmlDocument, new HtmlSanitizeOptions
        {
            Mode = HtmlSanitizeMode.Document,
            InlineImageIds = rendered.Images.Select(i => i.ContentId).ToHashSet(StringComparer.OrdinalIgnoreCase),
        }).Html;
}
