using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AdMng.App.Localization;
using AdMng.Presentation.Localization;

namespace AdMng.App.Controls;

/// <summary>What an <see cref="ActionButton"/> shows and does right now.</summary>
public enum ActionButtonState
{
    /// <summary>At rest: the text of the command; pressing it starts the operation.</summary>
    Idle,

    /// <summary>The operation runs: a progress ring and "Cancel"; pressing it cancels the operation.</summary>
    Running,

    /// <summary>Cancel was asked for: a progress ring and "Stopping…"; nothing to press until the operation ended.</summary>
    Cancelling,
}

/// <summary>
/// The one shape of a command that starts a long operation (track V, stage V2; design note
/// docs/dev/v-interface-renewal.md 6.7 and goal G3 - cancel where you started): at rest the button shows its content
/// and runs its <see cref="System.Windows.Controls.Primitives.ButtonBase.Command"/>; while the operation runs it shows
/// a small progress ring and "Cancel" and runs <see cref="CancelCommand"/>; after that it shows "Stopping…" and cannot
/// be pressed until the operation ended.
/// </summary>
/// <remarks>
/// <para><b>Binding.</b> Four plain properties, so a view model adopts the button without changing its commands:
/// <c>Command</c> (start), <see cref="CancelCommand"/>, <see cref="IsRunning"/> and, optionally,
/// <see cref="IsCancelling"/> (for a cancellation that was asked for elsewhere - the status line, the window closing).
/// With a <c>PageStatusHolder</c>: <c>IsRunning="{Binding Status.IsWorking}" IsCancelling="{Binding Status.IsCancelling}"
/// CancelCommand="{Binding Status.CancelCommand}"</c>. Without one, a view model's "busy" flag and its cancel command
/// are enough: the button itself remembers that Cancel was pressed.</para>
/// <para><b>Stays a button.</b> The look is the theme's button (the style <c>Ad.ActionButton</c> only gives it a content
/// template with the three contents), the size is the one of its widest content in every state (the contents share
/// one cell and differ only in opacity), and they swap with <c>b:Motion.Swap</c> when motion is on.</para>
/// <para><b>Keeps the keyboard focus.</b> A start command reports "cannot execute" while it runs, which would disable
/// the button and throw the focus away. While the operation runs the button therefore stays enabled whatever the start
/// command says - also in the moment between the press and the first sign that the operation runs, and in the moment
/// between the end of the operation and the end of its command - and "Stopping…" is an enabled button that ignores the
/// press (UI Automation reports it as not enabled).</para>
/// <para><b>Enter starts, it never stops.</b> As the default button it is pressed by Enter from anywhere in the window;
/// while the operation runs that Enter is ignored unless the button itself has the focus.</para>
/// </remarks>
public class ActionButton : Button
{
    /// <summary>The resource key of the style in Themes/Controls.xaml.</summary>
    public const string StyleKey = "Ad.ActionButton";

    /// <summary>The operation runs (a view model flag: "busy", "is loading", <c>Status.IsWorking</c>).</summary>
    public static readonly DependencyProperty IsRunningProperty = DependencyProperty.Register(
        nameof(IsRunning), typeof(bool), typeof(ActionButton), new PropertyMetadata(false, OnOperationChanged));

    /// <summary>The operation was asked to stop and has not ended yet (optional; pressing Cancel here counts by itself).</summary>
    public static readonly DependencyProperty IsCancellingProperty = DependencyProperty.Register(
        nameof(IsCancelling), typeof(bool), typeof(ActionButton), new PropertyMetadata(false, OnOperationChanged));

    /// <summary>What a press runs while the operation runs.</summary>
    public static readonly DependencyProperty CancelCommandProperty = DependencyProperty.Register(
        nameof(CancelCommand), typeof(ICommand), typeof(ActionButton), new PropertyMetadata(null));

    public static readonly DependencyProperty CancelCommandParameterProperty = DependencyProperty.Register(
        nameof(CancelCommandParameter), typeof(object), typeof(ActionButton), new PropertyMetadata(null));

    /// <summary>An optional glyph of the icon font before the text at rest (a constant of <see cref="Glyphs"/>).</summary>
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(ActionButton), new PropertyMetadata(null));

    private static readonly DependencyPropertyKey StatePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(State), typeof(ActionButtonState), typeof(ActionButton), new PropertyMetadata(ActionButtonState.Idle, OnStateChanged));

    private static readonly DependencyPropertyKey IsBusyPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsBusy), typeof(bool), typeof(ActionButton), new PropertyMetadata(false));

    /// <summary>What the button shows and does right now (the content template follows it).</summary>
    public static readonly DependencyProperty StateProperty = StatePropertyKey.DependencyProperty;

    /// <summary>The operation runs or is stopping: the content at rest is not shown.</summary>
    public static readonly DependencyProperty IsBusyProperty = IsBusyPropertyKey.DependencyProperty;

    // Cancel was pressed on this button: "Stopping…" until the operation ended, whatever IsCancelling says.
    private bool _cancelPressed;

    // The button does not follow its start command into "cannot execute" for a moment (see HoldEnabled).
    private bool _holdsEnabled;

    public ActionButton()
    {
        SetResourceReference(StyleProperty, StyleKey);
    }

    public bool IsRunning
    {
        get => (bool)GetValue(IsRunningProperty);
        set => SetValue(IsRunningProperty, value);
    }

    public bool IsCancelling
    {
        get => (bool)GetValue(IsCancellingProperty);
        set => SetValue(IsCancellingProperty, value);
    }

    public ICommand? CancelCommand
    {
        get => (ICommand?)GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }

    public object? CancelCommandParameter
    {
        get => GetValue(CancelCommandParameterProperty);
        set => SetValue(CancelCommandParameterProperty, value);
    }

    public string? Glyph
    {
        get => (string?)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public ActionButtonState State => (ActionButtonState)GetValue(StateProperty);

    public bool IsBusy => (bool)GetValue(IsBusyProperty);

    /// <summary>
    /// The start command decides only at rest. While the operation runs - and in the moment after the press, before the
    /// operation said so, and in the moment after its end, before its command said so - the button stays enabled, so it
    /// keeps the keyboard focus and can be pressed to cancel.
    /// </summary>
    protected override bool IsEnabledCore => State != ActionButtonState.Idle || _holdsEnabled || base.IsEnabledCore;

    /// <summary>
    /// A named element of the content template (<c>IdleContent</c>, <c>RunningContent</c>, <c>StoppingContent</c>,
    /// <c>RunningRing</c>, <c>StoppingRing</c>, <c>CancelText</c>, <c>StoppingText</c>), or null before the template
    /// was applied. For tests.
    /// </summary>
    internal FrameworkElement? FindContentPart(string name)
    {
        var template = ContentTemplate;
        return template is not null && FindPresenter(this, template) is { } presenter
            ? template.FindName(name, presenter) as FrameworkElement
            : null;
    }

    /// <summary>What pressing the button does now, for a screen reader: the content at rest, "Cancel: …", "Stopping…".</summary>
    internal string DescribeAction(string restName)
    {
        var localizer = TranslationSource.Localizer;
        return State switch
        {
            ActionButtonState.Running => string.IsNullOrWhiteSpace(restName) ? localizer[L.Common.Cancel] : $"{localizer[L.Common.Cancel]}: {restName}",
            ActionButtonState.Cancelling => localizer[L.Feedback.Stopping],
            _ => restName,
        };
    }

    protected override void OnClick()
    {
        switch (State)
        {
            case ActionButtonState.Idle:
                Start();
                break;
            case ActionButtonState.Running:
                Cancel();
                break;
            default:
                // Stopping: there is nothing to press until the operation ended.
                break;
        }
    }

    /// <summary>
    /// Enter (the default button) and Escape (the cancel button) pressed elsewhere in the window reach the button as
    /// its access key. At rest they press it; while the operation runs they do nothing unless the button has the
    /// focus - Enter typed twice into a text box must not stop the search it started.
    /// </summary>
    protected override void OnAccessKey(AccessKeyEventArgs e)
    {
        if (State == ActionButtonState.Idle || IsKeyboardFocused)
        {
            base.OnAccessKey(e);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ActionButtonAutomationPeer(this);

    private static void OnOperationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var button = (ActionButton)d;
        if (e.Property == IsCancellingProperty && e.NewValue is false)
        {
            // The cancellation the view model reported is over. When the operation still runs, it is another one (the
            // cancelled one was replaced): the press on this button belonged to the old one.
            button._cancelPressed = false;
        }

        button.UpdateState();
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var button = (ActionButton)d;
        var state = (ActionButtonState)e.NewValue;
        if (state == ActionButtonState.Idle)
        {
            // The operation says that it ended; its command may say so a moment later (the page status of a load ends
            // before the command that started the load returns). Until then the button must not be disabled.
            button.HoldEnabled();
        }

        button.SetValue(IsBusyPropertyKey, state != ActionButtonState.Idle);

        // The state is part of "is enabled" (see IsEnabledCore), and of the name and the enabled state UI Automation
        // reports: a screen reader hears "Cancel: Find Now", then "Stopping…".
        button.CoerceValue(IsEnabledProperty);
        UIElementAutomationPeer.FromElement(button)?.InvalidatePeer();
    }

    private static ContentPresenter? FindPresenter(DependencyObject root, DataTemplate template)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ContentPresenter presenter && ReferenceEquals(presenter.ContentTemplate, template))
            {
                return presenter;
            }

            if (FindPresenter(child, template) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Runs the start command (and raises Click) like any button.</summary>
    private void Start()
    {
        // An operation that reports "running" a moment after its command started must not disable the button in
        // between.
        HoldEnabled();
        base.OnClick();
    }

    /// <summary>
    /// From now until the dispatcher has nothing more urgent to do, the button does not follow the start command into
    /// "cannot execute". A disabled button loses the keyboard focus - WPF looks for a new focus at input priority - so
    /// the hold ends at background priority, after that look: by then the operation and its command agree, and the
    /// button is enabled or disabled for good.
    /// </summary>
    private void HoldEnabled()
    {
        if (_holdsEnabled)
        {
            return;
        }

        _holdsEnabled = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _holdsEnabled = false;
            CoerceValue(IsEnabledProperty);
        });
    }

    /// <summary>Runs the cancel command; the button says "Stopping…" before the command returns.</summary>
    private void Cancel()
    {
        var command = CancelCommand;
        var parameter = CancelCommandParameter;
        if (command is null || !command.CanExecute(parameter))
        {
            return;
        }

        _cancelPressed = true;
        UpdateState();
        command.Execute(parameter);
    }

    private void UpdateState()
    {
        if (!IsRunning)
        {
            _cancelPressed = false;
        }

        var state = !IsRunning ? ActionButtonState.Idle
            : IsCancelling || _cancelPressed ? ActionButtonState.Cancelling
            : ActionButtonState.Running;
        SetValue(StatePropertyKey, state);
    }

    /// <summary>
    /// A normal button for UI Automation: its name says what pressing it does now, it is reported as not enabled while
    /// there is nothing to press, and it is a leaf (the three contents of the template are not elements of their own).
    /// </summary>
    private sealed class ActionButtonAutomationPeer(ActionButton owner) : ButtonAutomationPeer(owner)
    {
        protected override string GetNameCore() => owner.DescribeAction(base.GetNameCore());

        protected override bool IsEnabledCore() => base.IsEnabledCore() && owner.State != ActionButtonState.Cancelling;

        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}
