using System.Text.RegularExpressions;
using AdMng.Core.Correspondence.Composition;
using AdMng.Presentation.Correspondence;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// The built-in mailbox templates render 1:1 as the operator's example message (design note, "Template after the
/// operator's example (1:1)"): the built-in is loaded into the message page of a window whose clock is fixed, a synthetic
/// signature is chosen and the message of a synthetic mailbox row is rendered. The body of the rendered document is
/// compared with the golden HTML – the embedded body with <c>{{Termin}}</c> as "15 listopada 2026r." and without its
/// <c>{{podpis}}</c> paragraph – semantically: the same element tree (the parser's <c>tbody</c> on both sides), the same
/// attributes, the same text runs (white space collapsed) and the same CSS value per property and element (the sanitizer
/// may reorder declarations). Until the message sanitizer allows Outlook's <c>mso-margin-*-alt</c> hints (inert in browsers)
/// a missing <c>mso-*</c> declaration is the only difference tolerated. The signature follows at once – no line break, no
/// empty paragraph – and the wrapping div carries the template's base style (Aptos 12 pt, line height normal).
/// </summary>
public sealed partial class GoldenTemplateRenderTests
{
    private const string Deadline = "15 listopada 2026r.";

    /// <summary>"Today" of the window: the built-ins' deadline (today + 14 days) is 15 November 2026.</summary>
    private static readonly DateTimeOffset Today = new(2026, 11, 1, 9, 30, 0, TimeSpan.Zero);

    private static readonly Dictionary<string, string> MailboxRow = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Skrzynka"] = "Sekretariat Wydziału Łączności",
        ["Adres skrzynki"] = "sekretariat.lacznosci@contoso.test",
    };

    [Fact]
    public async Task PerMailbox_RendersTheGoldenHtml_WithTheSignatureRightAfterIt_InTheBaseStyle()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        scenario.Harness.Time.SetUtcNow(Today);
        using var window = await OpenWithBuiltInAsync(scenario, CorrespondenceTemplateLibrary.MailboxStillNeededId);
        var unit = new CorrespondenceUnit("jan.kowalski@contoso.test", "Jan Kowalski", [MailboxRow]);

        var rendered = CorrespondenceRenderer.Render(await window.Message.DraftAsync(), unit, [.. MailboxRow.Keys]);

        Assert.Equal("Weryfikacja skrzynki pocztowej: Sekretariat Wydziału Łączności", rendered.Subject);
        Assert.Empty(rendered.UnknownFields);
        Assert.Empty(rendered.EmptyFields);
        var golden = Golden(scenario.Templates.Find(CorrespondenceTemplateLibrary.MailboxStillNeededId)!.BodyHtml);
        AssertRendersGolden(golden, rendered, scenario.Signatures.Logo.ContentId);
    }

    [Fact]
    public async Task Grouped_RendersTheGoldenHtml_WithTheListInPlaceOfItsParagraph()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        scenario.Harness.Time.SetUtcNow(Today);
        using var window = await OpenWithBuiltInAsync(scenario, CorrespondenceTemplateLibrary.MailboxStillNeededGroupedId);
        var second = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Skrzynka"] = "Rekrutacja <2026>",
            ["Adres skrzynki"] = "rekrutacja@contoso.test",
        };
        var unit = new CorrespondenceUnit("jan.kowalski@contoso.test", "Jan Kowalski", [MailboxRow, second]);

        var rendered = CorrespondenceRenderer.Render(await window.Message.DraftAsync(), unit, [.. MailboxRow.Keys]);

        Assert.Equal("Weryfikacja skrzynek pocztowych", rendered.Subject);
        Assert.Empty(rendered.UnknownFields);
        Assert.Empty(rendered.EmptyFields);
        var list = "<ul style=\"margin:0 0 8pt 0\"><li>Sekretariat Wydziału Łączności (sekretariat.lacznosci@contoso.test)</li>"
            + "<li>Rekrutacja &lt;2026&gt; (rekrutacja@contoso.test)</li></ul>";
        var body = scenario.Templates.Find(CorrespondenceTemplateLibrary.MailboxStillNeededGroupedId)!.BodyHtml;
        Assert.Contains("<p style=\"margin:0\">{{lista}}</p>", body, StringComparison.Ordinal);
        AssertRendersGolden(Golden(body.Replace("<p style=\"margin:0\">{{lista}}</p>", list, StringComparison.Ordinal)), rendered,
            scenario.Signatures.Logo.ContentId);
    }

    [Fact]
    public async Task WithoutASignature_TheGoldenHtmlEndsTheMessage()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        scenario.Harness.Time.SetUtcNow(Today);
        using var window = await OpenWithBuiltInAsync(scenario, CorrespondenceTemplateLibrary.MailboxStillNeededId, signature: null);
        var unit = new CorrespondenceUnit("jan.kowalski@contoso.test", "Jan Kowalski", [MailboxRow]);

        var rendered = CorrespondenceRenderer.Render(await window.Message.DraftAsync(), unit, [.. MailboxRow.Keys]);

        var wrapper = Wrapper(rendered.HtmlDocument);
        var golden = Golden(scenario.Templates.Find(CorrespondenceTemplateLibrary.MailboxStillNeededId)!.BodyHtml);
        Assert.Equal(golden.Count, Significant(wrapper.ChildNodes).Count);   // nothing after the golden body
        Assert.Empty(rendered.Images);
        Assert.Empty(rendered.EmptyFields);
    }

    /// <summary>A window with the built-in <paramref name="id"/> loaded into its message page and the signature chosen.</summary>
    private static async Task<CorrespondenceViewModel> OpenWithBuiltInAsync(CorrespondencePagesScenario scenario, string id,
        string? signature = CorrespondencePagesScenario.ParsingSignatures.Secretariat)
    {
        var window = await scenario.OpenAsync();
        Assert.True(await window.Message.LoadTemplateAsync(id));
        window.Message.SelectedSignature = window.Message.Signatures.Single(s => s.Name == signature);
        await (window.Message.SignatureLoading ?? Task.CompletedTask);
        return window;
    }

    /// <summary>The nodes of the golden HTML as the message shows them: the deadline and the help desk of the operator's
    /// example filled in (the link exactly as the example has it), the signature's paragraph left out (the signature itself
    /// stands there).</summary>
    private static IReadOnlyList<INode> Golden(string body)
    {
        Assert.Contains("<p>{{podpis}}</p>", body, StringComparison.Ordinal);
        var html = body.Replace("{{Termin}}", Deadline, StringComparison.Ordinal)
            .Replace("{{Link do systemu zgłoszeń}}", "https://hd.uni.lodz.pl", StringComparison.Ordinal)
            .Replace("{{System zgłoszeń}}", "hd.uni.lodz.pl", StringComparison.Ordinal)
            .Replace("<p>{{podpis}}</p>", string.Empty, StringComparison.Ordinal);
        Assert.Contains(
            "<a href=\"https://hd.uni.lodz.pl\" style=\"color:blue;text-decoration:underline\"><b><span style=\"color:#0056B3\">hd.uni.lodz.pl</span></b></a>",
            html, StringComparison.Ordinal);
        return Significant(new HtmlParser().ParseDocument("<!DOCTYPE html><html><body>" + html + "</body></html>").Body!.ChildNodes);
    }

    private static void AssertRendersGolden(IReadOnlyList<INode> golden, RenderedCorrespondence rendered, string logoId)
    {
        var wrapper = Wrapper(rendered.HtmlDocument);

        // The wrapping div: the template's base style.
        Assert.Equal(new Dictionary<string, string>
        {
            ["font-family"] = "aptos,sans-serif",
            ["font-size"] = "12pt",
            ["color"] = "#000000",
            ["line-height"] = "normal",
        }, Declarations(wrapper.GetAttribute("style")));

        // The body: the golden HTML, node by node.
        var nodes = Significant(wrapper.ChildNodes);
        Assert.True(nodes.Count > golden.Count, "the signature follows the body");
        for (var i = 0; i < golden.Count; i++)
        {
            AssertSameNode(golden[i], nodes[i], $"body/{i}");
        }

        // Then the signature at once (the scenario's parsed signature: its name and logo paragraphs), nothing appended.
        var signature = nodes.Skip(golden.Count).ToList();
        Assert.Equal(["p", "p"], signature.Select(n => Assert.IsAssignableFrom<IElement>(n).LocalName));
        Assert.Equal("color:#1f3864", Normalize(((IElement)signature[0]).GetAttribute("style")!));
        Assert.StartsWith(CorrespondencePagesScenario.ParsingSignatures.Secretariat, signature[0].TextContent, StringComparison.Ordinal);
        Assert.Equal("cid:" + logoId, ((IElement)signature[1]).QuerySelector("img")?.GetAttribute("src"));
        Assert.DoesNotContain(wrapper.Children, e => e.LocalName == "br");
        Assert.DoesNotContain(wrapper.Children, e => e.LocalName == "p" && Collapse(e.TextContent).Length == 0 && e.Children.Length == 0);
        Assert.Equal([logoId], rendered.Images.Select(i => i.ContentId));
    }

    /// <summary>The same element (name, attributes, CSS values) or text (white space collapsed), recursively.</summary>
    private static void AssertSameNode(INode expected, INode actual, string path)
    {
        if (expected is IText expectedText)
        {
            var text = Assert.IsAssignableFrom<IText>(actual);
            Assert.True(Collapse(expectedText.Data) == Collapse(text.Data), $"{path}: text \"{Collapse(text.Data)}\" instead of \"{Collapse(expectedText.Data)}\"");
            return;
        }

        var element = Assert.IsAssignableFrom<IElement>(expected);
        var other = Assert.IsAssignableFrom<IElement>(actual);
        path += "/" + element.LocalName;
        Assert.True(element.LocalName == other.LocalName, $"{path}: <{other.LocalName}> instead of <{element.LocalName}>");
        Assert.Equal(Attributes(element), Attributes(other));
        var expectedStyle = Declarations(element.GetAttribute("style"));
        var actualStyle = Declarations(other.GetAttribute("style"));
        foreach (var (property, value) in expectedStyle)
        {
            if (actualStyle.TryGetValue(property, out var actualValue))
            {
                Assert.True(value == actualValue, $"{path}: {property}:{actualValue} instead of {property}:{value}");
            }
            else
            {
                // Outlook's hints (mso-margin-top-alt…) are inert in browsers: the sanitizer may not allow them yet.
                Assert.True(property.StartsWith("mso-", StringComparison.Ordinal), $"{path}: {property}:{value} is missing");
            }
        }

        Assert.Empty(actualStyle.Keys.Except(expectedStyle.Keys));
        var expectedChildren = Significant(element.ChildNodes);
        var actualChildren = Significant(other.ChildNodes);
        Assert.True(expectedChildren.Count == actualChildren.Count, $"{path}: {actualChildren.Count} children instead of {expectedChildren.Count}");
        for (var i = 0; i < expectedChildren.Count; i++)
        {
            AssertSameNode(expectedChildren[i], actualChildren[i], $"{path}/{i}");
        }
    }

    private static IElement Wrapper(string document)
    {
        var parsed = new HtmlParser().ParseDocument(document);
        var wrapper = Assert.Single(parsed.Body!.Children);
        Assert.Equal("div", wrapper.LocalName);
        return wrapper;
    }

    /// <summary>Elements and text with visible characters (white space between blocks is no content).</summary>
    private static List<INode> Significant(INodeList nodes) =>
        [.. nodes.Where(n => n is IElement || (n is IText text && Collapse(text.Data).Length > 0))];

    /// <summary>The attributes other than <c>style</c> (names and values as written).</summary>
    private static SortedDictionary<string, string> Attributes(IElement element) =>
        new(element.Attributes.Where(a => a.Name != "style").ToDictionary(a => a.Name, a => a.Value), StringComparer.Ordinal);

    /// <summary>The declarations of a style attribute: property → value, lower case, white space collapsed.</summary>
    private static Dictionary<string, string> Declarations(string? style)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var declaration in (style ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = declaration.IndexOf(':', StringComparison.Ordinal);
            Assert.True(colon > 0, $"a declaration without a value: {declaration}");
            result.Add(Normalize(declaration[..colon]), Normalize(declaration[(colon + 1)..]));
        }

        return result;
    }

    private static string Normalize(string value) => Whitespace().Replace(value.Trim(), " ").Replace(", ", ",", StringComparison.Ordinal).ToLowerInvariant();

    private static string Collapse(string text) => Whitespace().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
