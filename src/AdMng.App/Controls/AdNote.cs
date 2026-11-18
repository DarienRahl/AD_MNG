using System.Windows;

namespace AdMng.App.Controls;

/// <summary>
/// A note may choose its glyph (track V, stage V2b-5b, owner's decision 4; docs/dev/v2-status-controls.md): the attached
/// property <see cref="IconProperty"/> names an icon of the closed set of status icons (<see cref="Icons"/>, keys of
/// <see cref="AppIcons"/>) and puts the glyph of that icon in place of the glyph of the style. Nothing else of the note
/// changes - its colour, its text and its template stay those of its style (<c>Ad.Note.Info</c>, <c>Ad.Note.Attention</c>,
/// <c>Ad.Note.Error</c> in Themes/Controls.xaml) - and a note that does not set it is as it was.
/// </summary>
/// <remarks>
/// <para>Use: <c>&lt;ContentControl Style="{StaticResource Ad.Note.Attention}"
/// c:AdNote.Icon="{x:Static c:AppIcons.StatusInfo}"&gt;</c>. The case it is for: a sentence that is information but
/// belongs to a section whose notes are mostly cautions (the replication note of the results of the bulk creation).
/// A severity is told by shape and by colour, so choose the glyph of the severity the sentence has; the style is the
/// severity of the colour and of the weight of the text.</para>
/// <para>The style carries its glyph in <see cref="FrameworkElement.Tag"/>, which the template shows; this property sets
/// the <c>Tag</c> of the note as a local value and takes it away again when it is cleared (or set to a key outside the
/// set, which changes nothing). The set is closed on purpose - <c>XamlSourceTests</c> fails a view that names a key
/// outside it - and every glyph in it is the one the status line shows for the same severity
/// (<c>StatusLine.IconOf</c>).</para>
/// </remarks>
public static class AdNote
{
    /// <summary>The icon of the note, a key of <see cref="AppIcons"/> from <see cref="Icons"/>; null (the default) is the glyph of the style.</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(string), typeof(AdNote), new PropertyMetadata(null, OnIconChanged));

    /// <summary>The icons a note may choose: the four of the status line, information, done, attention and error.</summary>
    internal static IReadOnlyList<string> Icons { get; } = [AppIcons.StatusInfo, AppIcons.StatusOk, AppIcons.StatusWarning, AppIcons.StatusError];

    public static string? GetIcon(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (string?)element.GetValue(IconProperty);
    }

    public static void SetIcon(DependencyObject element, string? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IconProperty, value);
    }

    /// <summary>The glyph of the icon font for an icon of the set; null for null and for a key outside the set.</summary>
    internal static string? GlyphOf(string? icon) => icon switch
    {
        AppIcons.StatusInfo => Glyphs.Info,
        AppIcons.StatusOk => Glyphs.Completed,
        AppIcons.StatusWarning => Glyphs.Warning,
        AppIcons.StatusError => Glyphs.Error,
        _ => null,
    };

    private static void OnIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement note)
        {
            return;
        }

        if (GlyphOf(e.NewValue as string) is { } glyph)
        {
            note.Tag = glyph;
        }
        else
        {
            // No icon, or one outside the set: the glyph of the style.
            note.ClearValue(FrameworkElement.TagProperty);
        }
    }
}
