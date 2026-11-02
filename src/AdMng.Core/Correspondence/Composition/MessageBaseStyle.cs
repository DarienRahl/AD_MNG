using System.Globalization;
using AdMng.Core.Messaging.Html;

namespace AdMng.Core.Correspondence.Composition;

/// <summary>
/// The style of the <c>div</c> that wraps a Korespondencja message (<see cref="CorrespondenceDocument"/>): the font family
/// (a CSS family list), the size in points and the line height (<c>normal</c> or a number such as <c>1.4</c>). The body's
/// own styles override it; webmail and Outlook keep it because it is inline.
/// </summary>
/// <param name="FontFamily">A CSS family list ("Aptos,sans-serif").</param>
/// <param name="FontSizePoints">The font size in points.</param>
/// <param name="LineHeight"><c>normal</c>, a number ("1.4") or a length.</param>
public sealed record MessageBaseStyle(string FontFamily, double FontSizePoints, string LineHeight)
{
    /// <summary>The smallest font size of a message.</summary>
    public const double MinFontSizePoints = 6;

    /// <summary>The largest font size of a message.</summary>
    public const double MaxFontSizePoints = 72;

    /// <summary>The style messages had before it could be chosen (MailerWPiA's BuildMessage): Calibri 11 pt, 1.4.</summary>
    public static MessageBaseStyle Default { get; } = new("Calibri,'Segoe UI',Arial,sans-serif", 11, "1.4");

    /// <summary>
    /// <c>font-family:…;font-size:…pt;color:#000000;line-height:…</c>. A part that is not a valid value (a family list
    /// with characters CSS does not allow there, a size outside <see cref="MinFontSizePoints"/>–<see cref="MaxFontSizePoints"/>,
    /// an unknown line height) is the default's, so the style attribute never carries anything else.
    /// </summary>
    public string Css =>
        "font-family:" + (NormalizeFontFamily(FontFamily) ?? Default.FontFamily)
        + ";font-size:" + (NormalizeFontSize(FontSizePoints) ?? Default.FontSizePoints).ToString(CultureInfo.InvariantCulture)
        + "pt;color:#000000;line-height:" + (NormalizeLineHeight(LineHeight) ?? Default.LineHeight);

    /// <summary>A style of stored values (a template, the last draft): every part that is missing or invalid is the
    /// default's.</summary>
    public static MessageBaseStyle Of(string? fontFamily, double? fontSizePoints, string? lineHeight) => new(
        NormalizeFontFamily(fontFamily) ?? Default.FontFamily,
        NormalizeFontSize(fontSizePoints) ?? Default.FontSizePoints,
        NormalizeLineHeight(lineHeight) ?? Default.LineHeight);

    /// <summary>The family list as the message sanitizer writes it (identifiers, or names in single quotes), or null when
    /// it is blank or not allowed.</summary>
    public static string? NormalizeFontFamily(string? value) =>
        !string.IsNullOrWhiteSpace(value) && CssValues.Filter("font-family", value, out var normalized) == CssVerdict.Keep ? normalized : null;

    /// <summary>The size rounded to a tenth of a point, or null when it is not a number between
    /// <see cref="MinFontSizePoints"/> and <see cref="MaxFontSizePoints"/>.</summary>
    public static double? NormalizeFontSize(double? value) =>
        value is { } size && double.IsFinite(size) && size >= MinFontSizePoints && size <= MaxFontSizePoints ? Math.Round(size, 1) : null;

    /// <summary><c>normal</c>, a number or a length as the message sanitizer writes it, or null when it is blank or not
    /// allowed.</summary>
    public static string? NormalizeLineHeight(string? value) =>
        !string.IsNullOrWhiteSpace(value) && CssValues.Filter("line-height", value, out var normalized) == CssVerdict.Keep ? normalized : null;

    public override string ToString() => $"{nameof(MessageBaseStyle)} {Css}";
}
