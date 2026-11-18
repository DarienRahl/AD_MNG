using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using AdMng.Presentation.Feedback;

namespace AdMng.App.Controls;

/// <summary>
/// A small rounded label that tells the state of one row or one item (track V, stage V2b-5b; docs/dev/v2-status-controls.md): the
/// result of a check, the health of a part, the state of a channel. It shows the picture of the state at 16 px and a short
/// word - "OK", "Warning", "Not checked" - so the meaning is carried by the picture and the word and never by colour alone;
/// the colour only gives the pill its surface. <see cref="Kind"/> chooses the picture and the colours, <see cref="Text"/> is the
/// word, and it is also the name a screen reader hears.
/// </summary>
/// <remarks>
/// <para>Use: <c>&lt;c:StatusPill Kind="{Binding PillKind, Mode=OneWay}" Text="{Binding StatusText, Mode=OneWay}" /&gt;</c> in a row
/// that has a state of its own. A page that says what it is doing or has done uses its status line, a sentence that belongs to
/// one field uses a note: the pill is for the state of an <em>item</em> in a list or a card.</para>
/// <para>The look is the style <c>Ad.StatusPill</c> of Themes/Controls.xaml, which the control takes by itself: the Fluent
/// status colours (the same brushes the status line and the notes read, so Windows High Contrast gives system colours and a
/// stroke that outlines the pill), the pictures of the icon set in the colour style and the glyphs of the icon font in the
/// monochrome style (<see cref="AreaIconView"/>). The pill is not interactive: it takes no focus and no pointer.</para>
/// </remarks>
public class StatusPill : Control
{
    /// <summary>The resource key of the style in Themes/Controls.xaml.</summary>
    public const string StyleKey = "Ad.StatusPill";

    /// <summary>The state the pill tells; <see cref="StatusPillKind.Unknown"/> (not checked, not known) by default.</summary>
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(StatusPillKind), typeof(StatusPill), new PropertyMetadata(StatusPillKind.Unknown, OnKindChanged));

    /// <summary>The word: short, in the language of the interface ("OK", "Ostrzeżenie").</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(StatusPill), new PropertyMetadata(string.Empty, OnTextChanged, CoerceText));

    private static readonly DependencyPropertyKey IconKeyPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IconKey), typeof(string), typeof(StatusPill), new PropertyMetadata(null));

    private static readonly DependencyPropertyKey IconGlyphPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IconGlyph), typeof(string), typeof(StatusPill), new PropertyMetadata(null));

    private static readonly DependencyPropertyKey HasTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(HasText), typeof(bool), typeof(StatusPill), new PropertyMetadata(false));

    /// <summary>The picture of the kind (a key of <see cref="AppIcons"/>).</summary>
    public static readonly DependencyProperty IconKeyProperty = IconKeyPropertyKey.DependencyProperty;

    /// <summary>The glyph shown instead of the picture in the monochrome icon style and in High Contrast.</summary>
    public static readonly DependencyProperty IconGlyphProperty = IconGlyphPropertyKey.DependencyProperty;

    /// <summary>The pill has a word to show.</summary>
    public static readonly DependencyProperty HasTextProperty = HasTextPropertyKey.DependencyProperty;

    static StatusPill()
    {
        // A label: nothing to focus, nothing to press.
        FocusableProperty.OverrideMetadata(typeof(StatusPill), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(StatusPill), new FrameworkPropertyMetadata(false));
    }

    public StatusPill()
    {
        SetResourceReference(StyleProperty, StyleKey);
        ApplyKind(StatusPillKind.Unknown);
    }

    public StatusPillKind Kind
    {
        get => (StatusPillKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? IconKey => (string?)GetValue(IconKeyProperty);

    public string? IconGlyph => (string?)GetValue(IconGlyphProperty);

    public bool HasText => (bool)GetValue(HasTextProperty);

    /// <summary>The picture of a kind and the glyph that stands in for it. The same four as the status line and the notes, and "not known".</summary>
    internal static (string Key, string Glyph) IconOf(StatusPillKind kind) => kind switch
    {
        StatusPillKind.Ok => (AppIcons.StatusOk, Glyphs.Completed),
        StatusPillKind.Info => (AppIcons.StatusInfo, Glyphs.Info),
        StatusPillKind.Attention => (AppIcons.StatusWarning, Glyphs.Warning),
        StatusPillKind.Error => (AppIcons.StatusError, Glyphs.Error),
        _ => (AppIcons.StatusUnknown, Glyphs.Question),
    };

    protected override AutomationPeer OnCreateAutomationPeer() => new StatusPillAutomationPeer(this);

    private static object CoerceText(DependencyObject d, object? value) => value as string ?? string.Empty;

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((StatusPill)d).ApplyKind((StatusPillKind)e.NewValue);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var pill = (StatusPill)d;
        var text = (string)e.NewValue;
        pill.SetValue(HasTextPropertyKey, text.Length > 0);

        // What a screen reader hears is the word - the picture is decorative.
        AutomationProperties.SetName(pill, text);
    }

    private void ApplyKind(StatusPillKind kind)
    {
        var (key, glyph) = IconOf(kind);
        SetValue(IconKeyPropertyKey, key);
        SetValue(IconGlyphPropertyKey, glyph);
    }

    /// <summary>The pill as a text with the word as its name; the picture and the parts of the template are no elements of their own.</summary>
    private sealed class StatusPillAutomationPeer(StatusPill owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(StatusPill);

        protected override bool IsControlElementCore() => true;

        protected override bool IsContentElementCore() => true;

        protected override List<AutomationPeer> GetChildrenCore() => [];
    }
}
