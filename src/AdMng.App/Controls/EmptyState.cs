using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using AdMng.Presentation.Icons;

namespace AdMng.App.Controls;

/// <summary>
/// What a list says when it has no rows (track V, stage V2b-5b; docs/dev/v2-status-controls.md; design note 6.7): a 48 px
/// picture, one sentence and at most one action - "No GPO matches the search. [Clear the search]". It is centred in its host
/// and collapsed when it has nothing to say (<see cref="Message"/> is empty), so the list shows it by binding the four
/// properties to the state of its view model and does not need a visibility of its own.
/// </summary>
/// <remarks>
/// <para>It is for a list that was read and has no rows: nothing here yet, or a filter that hides everything. "The list could not
/// be read" is not an empty state - that is an error and the status line of the page says it with Retry; a list that is being
/// read for the first time shows placeholder rows (<see cref="Skeleton"/>). The sentence says why the list is empty and the
/// action is the next step - "Clear the filter" when a filter hides the rows, "Refresh" otherwise. Where the list had a text
/// of its own for this, that text is replaced by the empty state, not repeated beside it.</para>
/// <para>The look is the style <c>Ad.EmptyState</c> of Themes/Controls.xaml, which the control takes by itself. The picture is an
/// <see cref="ObjectIcon"/> (the list's own picture, or the magnifier of "nothing matches"), drawn by
/// <see cref="ObjectIconView"/> in the colour or the monochrome style; the action is the link button of the status line, a real
/// button with a command, in the tab order. For a screen reader the control is a group named by its sentence.</para>
/// </remarks>
public class EmptyState : Control
{
    /// <summary>The resource key of the style in Themes/Controls.xaml.</summary>
    public const string StyleKey = "Ad.EmptyState";

    /// <summary>The template part of the action: a link button.</summary>
    public const string ActionPartName = "PART_ActionButton";

    /// <summary>The picture, 48 px: the list's own, or the magnifier when a filter hides the rows.</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(ObjectIcon), typeof(EmptyState), new PropertyMetadata(ObjectIcon.Folder));

    /// <summary>The one sentence; empty (the default) collapses the control.</summary>
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(EmptyState), new PropertyMetadata(string.Empty, OnMessageChanged, CoerceText));

    /// <summary>The label of the action, a verb ("Clear the filter"); empty: no action.</summary>
    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(
        nameof(ActionText), typeof(string), typeof(EmptyState), new PropertyMetadata(string.Empty, OnActionChanged, CoerceText));

    /// <summary>What the action runs; none: no action.</summary>
    public static readonly DependencyProperty ActionCommandProperty = DependencyProperty.Register(
        nameof(ActionCommand), typeof(ICommand), typeof(EmptyState), new PropertyMetadata(null, OnActionChanged));

    /// <summary>The parameter of the command, if it takes one.</summary>
    public static readonly DependencyProperty ActionParameterProperty = DependencyProperty.Register(
        nameof(ActionParameter), typeof(object), typeof(EmptyState), new PropertyMetadata(null));

    private static readonly DependencyPropertyKey HasMessagePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(HasMessage), typeof(bool), typeof(EmptyState), new PropertyMetadata(false));

    private static readonly DependencyPropertyKey HasActionPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(HasAction), typeof(bool), typeof(EmptyState), new PropertyMetadata(false));

    /// <summary>The control has a sentence to show.</summary>
    public static readonly DependencyProperty HasMessageProperty = HasMessagePropertyKey.DependencyProperty;

    /// <summary>The control offers its action: it has a label and a command.</summary>
    public static readonly DependencyProperty HasActionProperty = HasActionPropertyKey.DependencyProperty;

    static EmptyState()
    {
        // The control is a place for text; its action button is what takes the focus.
        FocusableProperty.OverrideMetadata(typeof(EmptyState), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(EmptyState), new FrameworkPropertyMetadata(false));
    }

    public EmptyState() => SetResourceReference(StyleProperty, StyleKey);

    public ObjectIcon Icon
    {
        get => (ObjectIcon)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public string ActionText
    {
        get => (string)GetValue(ActionTextProperty);
        set => SetValue(ActionTextProperty, value);
    }

    public ICommand? ActionCommand
    {
        get => (ICommand?)GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }

    public object? ActionParameter
    {
        get => GetValue(ActionParameterProperty);
        set => SetValue(ActionParameterProperty, value);
    }

    public bool HasMessage => (bool)GetValue(HasMessageProperty);

    public bool HasAction => (bool)GetValue(HasActionProperty);

    protected override AutomationPeer OnCreateAutomationPeer() => new EmptyStateAutomationPeer(this);

    private static object CoerceText(DependencyObject d, object? value) => value as string ?? string.Empty;

    private static void OnMessageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        d.SetValue(HasMessagePropertyKey, ((string)e.NewValue).Length > 0);

    private static void OnActionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var state = (EmptyState)d;
        state.SetValue(HasActionPropertyKey, state.ActionText.Length > 0 && state.ActionCommand is not null);
    }

    /// <summary>The control as a group named by its sentence; its children are the sentence and the action.</summary>
    private sealed class EmptyStateAutomationPeer(EmptyState owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

        protected override string GetClassNameCore() => nameof(EmptyState);

        protected override string GetNameCore()
        {
            var name = base.GetNameCore();
            return string.IsNullOrEmpty(name) ? owner.Message : name;
        }

        protected override bool IsControlElementCore() => owner.HasMessage;

        protected override bool IsContentElementCore() => owner.HasMessage;
    }
}
