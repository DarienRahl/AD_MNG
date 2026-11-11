using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using AdMng.App.Behaviors;
using AdMng.App.Localization;
using AdMng.Presentation.Feedback;

namespace AdMng.App.Controls;

/// <summary>
/// The status line of a page (track V, stage V2; design note docs/dev/v-interface-renewal.md 4.5, 6.1 and 6.7): the one
/// place where a page says what it is doing and what happened. Always present and of a fixed height, so the content
/// under it never jumps. It shows a <see cref="PageStatus"/>: the picture of the severity (a progress ring while an
/// operation runs), the fixed title of the severity, the message on one line (the whole text in the tool tip), the
/// follow-up action as a link and a close button for what may be closed.
/// </summary>
/// <remarks>
/// <para>Use: <c>&lt;c:StatusLine x:Name="PageStatus" Status="{Binding Status.Current, Mode=OneWay}"
/// CloseCommand="{Binding Status.CloseCommand, Mode=OneWay}" /&gt;</c> over a <see cref="PageStatusHolder"/>; the
/// element is named <c>PageStatus</c> on every page (design note 6.1). The look is the style <c>Ad.StatusLine</c> of
/// Themes/Controls.xaml, which the control takes by itself.</para>
/// <para>A state is never told by colour alone: every severity has its picture (a glyph in the monochrome icon style
/// and in High Contrast) and its title in words. The line is a live region for screen readers - polite for the
/// summary, a running operation and "done", assertive for "attention" and "error" - with the name
/// "Error: what was tried…"; a new message is announced, a new count of the same message is not.</para>
/// <para>A new message enters with a fade and a short rise (design note 5.2, row 3) when motion is on and is simply
/// there when it is off; a new count of the same message and a changed summary never move.</para>
/// </remarks>
public class StatusLine : Control
{
    /// <summary>The resource key of the style in Themes/Controls.xaml.</summary>
    public const string StyleKey = "Ad.StatusLine";

    /// <summary>The template part that holds picture, title and message: what enters when the message changes.</summary>
    public const string ContentPartName = "PART_Content";

    /// <summary>What the line shows; null is the empty summary.</summary>
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(PageStatus), typeof(StatusLine), new PropertyMetadata(null, OnStatusChanged));

    /// <summary>What the close button runs (<see cref="PageStatusHolder.CloseCommand"/>).</summary>
    public static readonly DependencyProperty CloseCommandProperty = DependencyProperty.Register(
        nameof(CloseCommand), typeof(ICommand), typeof(StatusLine), new PropertyMetadata(null, OnCloseCommandChanged));

    private static readonly DependencyPropertyKey KindPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Kind), typeof(PageStatusKind), typeof(StatusLine), new PropertyMetadata(PageStatusKind.Idle));

    private static readonly DependencyPropertyKey TitlePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Title), typeof(string), typeof(StatusLine), new PropertyMetadata(string.Empty));

    private static readonly DependencyPropertyKey MessagePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Message), typeof(string), typeof(StatusLine), new PropertyMetadata(string.Empty));

    private static readonly DependencyPropertyKey MessageToolTipPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(MessageToolTip), typeof(object), typeof(StatusLine), new PropertyMetadata(null));

    private static readonly DependencyPropertyKey IconKeyPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IconKey), typeof(string), typeof(StatusLine), new PropertyMetadata(null));

    private static readonly DependencyPropertyKey IconGlyphPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IconGlyph), typeof(string), typeof(StatusLine), new PropertyMetadata(null));

    private static readonly DependencyPropertyKey HasIconPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(HasIcon), typeof(bool), typeof(StatusLine), new PropertyMetadata(false));

    private static readonly DependencyPropertyKey IsWorkingPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsWorking), typeof(bool), typeof(StatusLine), new PropertyMetadata(false));

    private static readonly DependencyPropertyKey ProgressFractionPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(ProgressFraction), typeof(double), typeof(StatusLine), new PropertyMetadata(double.NaN));

    private static readonly DependencyPropertyKey HasActionPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(HasAction), typeof(bool), typeof(StatusLine), new PropertyMetadata(false));

    private static readonly DependencyPropertyKey ActionLabelPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(ActionLabel), typeof(string), typeof(StatusLine), new PropertyMetadata(string.Empty));

    private static readonly DependencyPropertyKey ActionCommandPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(ActionCommand), typeof(ICommand), typeof(StatusLine), new PropertyMetadata(null));

    private static readonly DependencyPropertyKey ActionParameterPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(ActionParameter), typeof(object), typeof(StatusLine), new PropertyMetadata(null));

    private static readonly DependencyPropertyKey CanClosePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(CanClose), typeof(bool), typeof(StatusLine), new PropertyMetadata(false));

    /// <summary>The severity shown (the style gives each its surface).</summary>
    public static readonly DependencyProperty KindProperty = KindPropertyKey.DependencyProperty;

    /// <summary>The fixed title of the severity in the language of the interface; empty for the summary.</summary>
    public static readonly DependencyProperty TitleProperty = TitlePropertyKey.DependencyProperty;

    /// <summary>The text of the status.</summary>
    public static readonly DependencyProperty MessageProperty = MessagePropertyKey.DependencyProperty;

    /// <summary>The whole text for the tool tip of the message, which is cut to one line; null when there is no text.</summary>
    public static readonly DependencyProperty MessageToolTipProperty = MessageToolTipPropertyKey.DependencyProperty;

    /// <summary>The picture of the severity (a key of <see cref="AppIcons"/>); null when none is shown.</summary>
    public static readonly DependencyProperty IconKeyProperty = IconKeyPropertyKey.DependencyProperty;

    /// <summary>The glyph shown instead of the picture in the monochrome icon style and in High Contrast.</summary>
    public static readonly DependencyProperty IconGlyphProperty = IconGlyphPropertyKey.DependencyProperty;

    /// <summary>The place before the text is taken: by the picture of the severity or by the progress ring.</summary>
    public static readonly DependencyProperty HasIconProperty = HasIconPropertyKey.DependencyProperty;

    /// <summary>True while an operation runs: the progress ring takes the place of the picture.</summary>
    public static readonly DependencyProperty IsWorkingProperty = IsWorkingPropertyKey.DependencyProperty;

    /// <summary>The part of the running operation that is done, 0 to 1; not a number when it is not known.</summary>
    public static readonly DependencyProperty ProgressFractionProperty = ProgressFractionPropertyKey.DependencyProperty;

    /// <summary>The status offers a follow-up action.</summary>
    public static readonly DependencyProperty HasActionProperty = HasActionPropertyKey.DependencyProperty;

    public static readonly DependencyProperty ActionLabelProperty = ActionLabelPropertyKey.DependencyProperty;

    public static readonly DependencyProperty ActionCommandProperty = ActionCommandPropertyKey.DependencyProperty;

    public static readonly DependencyProperty ActionParameterProperty = ActionParameterPropertyKey.DependencyProperty;

    /// <summary>The close button is shown: the status may be closed and there is a command to close it with.</summary>
    public static readonly DependencyProperty CanCloseProperty = CanClosePropertyKey.DependencyProperty;

    /// <summary>
    /// The language of the interface (bound to <see cref="TranslationSource.Texts"/>): the line's own words - the fixed
    /// title and the name a screen reader hears - follow a language switch at once. The message is the page's text and
    /// changes when the page says it again.
    /// </summary>
    private static readonly DependencyProperty LanguageOfTextsProperty = DependencyProperty.Register(
        "LanguageOfTexts", typeof(CultureInfo), typeof(StatusLine), new PropertyMetadata(null, OnLanguageOfTextsChanged));

    private FrameworkElement? _content;

    static StatusLine()
    {
        // The line is not a tab stop; its two buttons are.
        FocusableProperty.OverrideMetadata(typeof(StatusLine), new FrameworkPropertyMetadata(false));
    }

    public StatusLine()
    {
        SetResourceReference(StyleProperty, StyleKey);
        Show(PageStatus.Empty);
        SetBinding(LanguageOfTextsProperty, new Binding(nameof(LocalizedTexts.Culture)) { Source = TranslationSource.Texts, Mode = BindingMode.OneWay });
    }

    public PageStatus? Status
    {
        get => (PageStatus?)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public ICommand? CloseCommand
    {
        get => (ICommand?)GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    public PageStatusKind Kind => (PageStatusKind)GetValue(KindProperty);

    public string Title => (string)GetValue(TitleProperty);

    public string Message => (string)GetValue(MessageProperty);

    public object? MessageToolTip => GetValue(MessageToolTipProperty);

    public string? IconKey => (string?)GetValue(IconKeyProperty);

    public string? IconGlyph => (string?)GetValue(IconGlyphProperty);

    public bool HasIcon => (bool)GetValue(HasIconProperty);

    public bool IsWorking => (bool)GetValue(IsWorkingProperty);

    public double ProgressFraction => (double)GetValue(ProgressFractionProperty);

    public bool HasAction => (bool)GetValue(HasActionProperty);

    public string ActionLabel => (string)GetValue(ActionLabelProperty);

    public ICommand? ActionCommand => (ICommand?)GetValue(ActionCommandProperty);

    public object? ActionParameter => GetValue(ActionParameterProperty);

    public bool CanClose => (bool)GetValue(CanCloseProperty);

    /// <summary>The picture of a severity and the glyph that stands in for it; none for a running operation (the ring).</summary>
    internal static (string? IconKey, string? Glyph) IconOf(PageStatusKind kind) => kind switch
    {
        PageStatusKind.Idle => (AppIcons.StatusInfo, Glyphs.Info),
        PageStatusKind.Done => (AppIcons.StatusOk, Glyphs.Completed),
        PageStatusKind.Attention => (AppIcons.StatusWarning, Glyphs.Warning),
        PageStatusKind.Error => (AppIcons.StatusError, Glyphs.Error),
        _ => (null, null),
    };

    /// <summary>How a screen reader is told about a severity: assertive for what needs the operator, polite otherwise.</summary>
    internal static AutomationLiveSetting LiveSettingOf(PageStatusKind kind) =>
        kind is PageStatusKind.Attention or PageStatusKind.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _content = GetTemplateChild(ContentPartName) as FrameworkElement;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new StatusLineAutomationPeer(this);

    private static void OnStatusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var line = (StatusLine)d;
        var previous = e.OldValue as PageStatus ?? PageStatus.Empty;
        var status = e.NewValue as PageStatus ?? PageStatus.Empty;
        line.Show(status);
        if (status.IsSameMessageAs(previous))
        {
            // A new count of the same message, or a summary that changed: no motion and no announcement.
            return;
        }

        if (line._content is { } content)
        {
            Motion.Play(content, MotionEntrance.Status);
        }

        line.Announce();
    }

    private static void OnCloseCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var line = (StatusLine)d;
        line.SetValue(CanClosePropertyKey, (line.Status?.CanClose ?? false) && e.NewValue is not null);
    }

    /// <summary>The language changed: the same status in the words of the new language - no motion, no announcement.</summary>
    private static void OnLanguageOfTextsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var line = (StatusLine)d;
        line.Show(line.Status ?? PageStatus.Empty);
    }

    /// <summary>Puts the parts of a status into the properties the template shows.</summary>
    private void Show(PageStatus status)
    {
        var localizer = TranslationSource.Localizer;
        var hasText = status.Message.Length > 0;
        var (iconKey, glyph) = status.IsIdle && !hasText ? (null, null) : IconOf(status.Kind);
        SetValue(KindPropertyKey, status.Kind);
        SetValue(TitlePropertyKey, PageStatusText.Title(localizer, status.Kind));
        SetValue(MessagePropertyKey, status.Message);
        SetValue(MessageToolTipPropertyKey, hasText ? status.Message : null);
        SetValue(IconKeyPropertyKey, iconKey);
        SetValue(IconGlyphPropertyKey, glyph);
        SetValue(HasIconPropertyKey, iconKey is not null || status.IsWorking);
        SetValue(IsWorkingPropertyKey, status.IsWorking);
        SetValue(ProgressFractionPropertyKey, status.Progress?.Fraction ?? double.NaN);
        SetValue(HasActionPropertyKey, status.Action is not null);
        SetValue(ActionLabelPropertyKey, status.Action?.Label ?? string.Empty);
        SetValue(ActionCommandPropertyKey, status.Action?.Command);
        SetValue(ActionParameterPropertyKey, status.Action?.Parameter);
        SetValue(CanClosePropertyKey, status.CanClose && CloseCommand is not null);

        // What a screen reader hears, and how urgently.
        AutomationProperties.SetName(this, PageStatusText.Name(localizer, status));
        AutomationProperties.SetLiveSetting(this, LiveSettingOf(status.Kind));
    }

    /// <summary>
    /// Tells screen readers that the line says something new (WPF raises no live region event by itself), after the
    /// template has taken the new values.
    /// </summary>
    private void Announce()
    {
        if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                UIElementAutomationPeer.CreatePeerForElement(this)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged));
        }
    }

    /// <summary>
    /// The line as a status bar with a name and a live setting; its children are the action and the close button (the
    /// picture and the texts of the template are not elements of their own - the name of the line says them).
    /// </summary>
    private sealed class StatusLineAutomationPeer(StatusLine owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.StatusBar;

        protected override string GetClassNameCore() => nameof(StatusLine);

        protected override bool IsControlElementCore() => true;

        protected override bool IsContentElementCore() => true;
    }
}
