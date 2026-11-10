using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AdMng.App.Behaviors;

/// <summary>
/// How an element arrives (<see cref="Motion.EntranceProperty"/>; design note 5.2, rows 4, 5 and 7). Every kind reads
/// its time and its distance from Themes/Motion.xaml and eases with <c>Motion.Ease.Enter</c>.
/// </summary>
public enum MotionEntrance
{
    /// <summary>No entrance.</summary>
    None,

    /// <summary>
    /// A page: the element rises by <c>Motion.Offset.Page</c> to its place over <c>Motion.Entrance</c>. It is moved,
    /// never faded: a fade draws the whole subtree through an intermediate surface, and the text of a list loses
    /// ClearType meanwhile.
    /// </summary>
    Page,

    /// <summary>A panel: the element fades in and rises by <c>Motion.Offset.Panel</c> over <c>Motion.Fast</c>.</summary>
    Panel,

    /// <summary>A toast: the element fades in and slides in from the right by <c>Motion.Offset.Toast</c> over <c>Motion.Appear</c>.</summary>
    Toast,
}

/// <summary>
/// All motion of the application (track V, stage V1; design note docs/dev/v-interface-renewal.md 5). Nothing is
/// animated in XAML: these attached properties start every animation from code, with the durations, offsets and
/// easings of Themes/Motion.xaml read by key when a motion starts, behind the one switch <see cref="IsEnabled"/>
/// (set by <c>Services/MotionService</c>).
/// </summary>
/// <remarks>
/// <para>Why code and not storyboards or visual states in XAML: a storyboard inside a template is frozen with the
/// template, so it cannot take a duration that is read when the motion starts, and with motion switched off the final
/// values must be set directly instead of running a zero-length animation - which code does here, in one place.</para>
/// <para>Rules every property keeps (design note 5.3 and 5.4):</para>
/// <list type="bullet">
/// <item>With motion off the natural final state is set directly - no zero-length animation.</item>
/// <item>Nothing is started on an element that is not visible.</item>
/// <item>Only <see cref="UIElement.Opacity"/> and a <see cref="TranslateTransform"/> this class put on the element
/// move; never a width, a height or a margin. An element that has a render transform of its own is not moved.</item>
/// <item><see cref="UIElement.IsHitTestVisible"/> and <see cref="UIElement.IsEnabled"/> are never touched: the element
/// takes input from the first frame.</item>
/// <item>No clock is left behind: when a motion ends its clocks are removed and the element has its base values
/// again (the transform of an entrance is taken off). Only an element that is leaving keeps its end state, until its
/// owner removes it.</item>
/// <item>A new motion replaces a running one where it stands. The clock of a replaced animation still runs to its
/// end and raises Completed, so every motion carries an identity and a late Completed of an older one does nothing.</item>
/// <item>The switch is read when a motion starts: switching it off lets a running motion (at most a quarter of a
/// second) finish and applies from the next one.</item>
/// </list>
/// <para>An entrance or an exit owns the animation clocks of the element's opacity while it runs (it removes them all
/// when it ends), so <see cref="ShownProperty"/> and <see cref="EntranceProperty"/> are not put on the same element.</para>
/// </remarks>
public static class Motion
{
    // Keys of Themes/Motion.xaml.
    internal const string HoverKey = "Motion.Hover";
    internal const string FastKey = "Motion.Fast";
    internal const string AppearKey = "Motion.Appear";
    internal const string DisappearKey = "Motion.Disappear";
    internal const string EntranceKey = "Motion.Entrance";
    internal const string PageOffsetKey = "Motion.Offset.Page";
    internal const string PanelOffsetKey = "Motion.Offset.Panel";
    internal const string ToastOffsetKey = "Motion.Offset.Toast";
    internal const string EnterEaseKey = "Motion.Ease.Enter";
    internal const string ExitEaseKey = "Motion.Ease.Exit";
    internal const string ChangeEaseKey = "Motion.Ease.Change";

    /// <summary>
    /// The hover layer of a template: true shows the element (opacity 1), false hides it (opacity 0), over
    /// <c>Motion.Hover</c>. The element declares the opacity of its initial state itself (<c>Opacity="0"</c> for a layer
    /// bound to <c>IsMouseOver</c>). The opacity is this property's alone - a trigger that wants the layer shown sets
    /// this property, not the opacity (a value set here outranks a template trigger).
    /// </summary>
    public static readonly DependencyProperty ShownProperty = DependencyProperty.RegisterAttached(
        "Shown", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnShownChanged));

    /// <summary>
    /// The entrance of the element: played when the element is visible as the property is set, whenever the element
    /// becomes visible (again) and when <see cref="EntranceTriggerProperty"/> changes to a value that is not null.
    /// </summary>
    public static readonly DependencyProperty EntranceProperty = DependencyProperty.RegisterAttached(
        "Entrance", typeof(MotionEntrance), typeof(Motion), new PropertyMetadata(MotionEntrance.None, OnEntranceChanged));

    /// <summary>
    /// Replays the entrance of a visible element when it changes to a value that is not null (what the page shows was
    /// replaced by something the operator asked for). Never bind it to live data: a refresh must not move anything.
    /// </summary>
    public static readonly DependencyProperty EntranceTriggerProperty = DependencyProperty.RegisterAttached(
        "EntranceTrigger", typeof(object), typeof(Motion), new PropertyMetadata(null, OnEntranceTriggerChanged));

    /// <summary>
    /// True while the element is on its way out (a dismissed toast): it fades and slides out to the right by
    /// <c>Motion.Offset.Toast</c> over <c>Motion.Disappear</c> and keeps that end state until its owner removes it.
    /// With motion off nothing happens here - the owner removes the element at once.
    /// </summary>
    public static readonly DependencyProperty LeavingProperty = DependencyProperty.RegisterAttached(
        "Leaving", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnLeavingChanged));

    /// <summary>The identity of the running <see cref="ShownProperty"/> animation of an element (null: none).</summary>
    private static readonly DependencyProperty ShownRunProperty = DependencyProperty.RegisterAttached(
        "ShownRun", typeof(object), typeof(Motion), new PropertyMetadata(null));

    /// <summary>What an entrance or an exit put on an element.</summary>
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(TravelState), typeof(Motion), new PropertyMetadata(null));

    /// <summary>
    /// The one switch: whether anything moves. False until <c>MotionService</c> says otherwise, so an element created
    /// without the application host (a test) never animates. UI thread.
    /// </summary>
    public static bool IsEnabled { get; internal set; }

    public static bool GetShown(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(ShownProperty);
    }

    public static void SetShown(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ShownProperty, value);
    }

    public static MotionEntrance GetEntrance(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (MotionEntrance)element.GetValue(EntranceProperty);
    }

    public static void SetEntrance(DependencyObject element, MotionEntrance value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(EntranceProperty, value);
    }

    public static object? GetEntranceTrigger(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(EntranceTriggerProperty);
    }

    public static void SetEntranceTrigger(DependencyObject element, object? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(EntranceTriggerProperty, value);
    }

    public static bool GetLeaving(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(LeavingProperty);
    }

    public static void SetLeaving(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(LeavingProperty, value);
    }

    /// <summary>Whether a motion of this class is running on (or, for a leaving element, held by) the element.</summary>
    internal static bool IsRunning(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(ShownRunProperty) is not null || element.GetValue(StateProperty) is TravelState { Run: not null };
    }

    /// <summary>
    /// The time of a duration token, or null when the token is missing or not a positive time span (there is no
    /// application, or the dictionary is not merged): the caller then does without the motion.
    /// </summary>
    internal static TimeSpan? TimeOf(string key) =>
        Application.Current?.TryFindResource(key) is Duration { HasTimeSpan: true } duration && duration.TimeSpan > TimeSpan.Zero
            ? duration.TimeSpan
            : null;

    private static double OffsetOf(string key) =>
        Application.Current?.TryFindResource(key) is double offset && double.IsFinite(offset) ? offset : 0d;

    private static IEasingFunction? EaseOf(string key) => Application.Current?.TryFindResource(key) as IEasingFunction;

    // ---- Shown ----------------------------------------------------------------------------------------------------

    private static void OnShownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        var target = e.NewValue is true ? 1d : 0d;
        if (IsEnabled && element.IsVisible && TimeOf(HoverKey) is { } time)
        {
            var run = new object();
            element.SetValue(ShownRunProperty, run);

            // From where the opacity stands (also in the middle of the opposite change) to the target. The clock holds
            // the end only until Completed, where the base value takes over and the clock is removed.
            var animation = new DoubleAnimation
            {
                To = target,
                Duration = new Duration(time),
                EasingFunction = EaseOf(ChangeEaseKey),
                FillBehavior = FillBehavior.HoldEnd,
            };
            animation.Completed += (_, _) =>
            {
                if (ReferenceEquals(element.GetValue(ShownRunProperty), run))
                {
                    element.ClearValue(ShownRunProperty);
                    element.Opacity = target;
                    element.BeginAnimation(UIElement.OpacityProperty, null);
                }
            };
            element.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
            return;
        }

        element.ClearValue(ShownRunProperty);
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = target;
    }

    // ---- Entrance -------------------------------------------------------------------------------------------------

    private static void OnEntranceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        var state = StateOf(element);
        if ((MotionEntrance)e.NewValue == MotionEntrance.None)
        {
            if (state.Follows)
            {
                state.Follows = false;
                element.IsVisibleChanged -= OnEntranceVisibleChanged;
                element.Loaded -= OnEntranceLoaded;
            }

            Stop(element, state);
            return;
        }

        if (!state.Follows)
        {
            state.Follows = true;
            element.IsVisibleChanged += OnEntranceVisibleChanged;
            element.Loaded += OnEntranceLoaded;
        }

        // Already on screen (a style applied to an element that is in the tree): the entrance starts now, before the
        // element is drawn for the first time. An element that is not visible yet enters when it becomes visible.
        if (element.IsVisible && !state.Entered)
        {
            Enter(element, state);
        }
    }

    private static void OnEntranceTriggerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not null
            && d is FrameworkElement { IsVisible: true } element
            && GetEntrance(element) != MotionEntrance.None)
        {
            Enter(element, StateOf(element));
        }
    }

    private static void OnEntranceVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.GetValue(StateProperty) is not TravelState state)
        {
            return;
        }

        if (e.NewValue is true)
        {
            // Synchronously with the change, so the first frame of the element already shows the start of its entrance.
            Enter(element, state);
        }
        else
        {
            state.Entered = false;
            Stop(element, state);
        }
    }

    /// <summary>
    /// The fallback of "loaded while visible": Loaded is raised after the first frame, so the entrance normally started
    /// earlier (when the element became visible) and this does nothing - also when a theme switch raises Loaded again.
    /// </summary>
    private static void OnEntranceLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { IsVisible: true } element
            && element.GetValue(StateProperty) is TravelState { Entered: false } state)
        {
            Enter(element, state);
        }
    }

    private static void Enter(FrameworkElement element, TravelState state)
    {
        state.Entered = true;

        // A new entrance replaces a running one; with motion off this leaves the element at its base values.
        Stop(element, state);
        if (!IsEnabled || !element.IsVisible || GetLeaving(element))
        {
            return;
        }

        string durationKey;
        string offsetKey;
        bool fades;
        bool sideways;
        switch (GetEntrance(element))
        {
            case MotionEntrance.Page:
                (durationKey, offsetKey, fades, sideways) = (EntranceKey, PageOffsetKey, false, false);
                break;
            case MotionEntrance.Panel:
                (durationKey, offsetKey, fades, sideways) = (FastKey, PanelOffsetKey, true, false);
                break;
            case MotionEntrance.Toast:
                (durationKey, offsetKey, fades, sideways) = (AppearKey, ToastOffsetKey, true, true);
                break;
            default:
                return;
        }

        if (TimeOf(durationKey) is not { } time)
        {
            return;
        }

        var duration = new Duration(time);
        var ease = EaseOf(EnterEaseKey);
        var offset = OffsetOf(offsetKey);
        var moves = offset != 0d && HasNoTransformOfItsOwn(element);
        if (!moves && !fades)
        {
            return;
        }

        var run = new object();
        state.Run = run;

        // Both animations start in this call and take the same time; the cleanup hangs on one of them. Completed must
        // be attached before the animation starts (the clock gets a frozen copy of the timeline).
        void OnCompleted(object? sender, EventArgs args)
        {
            if (ReferenceEquals(state.Run, run))
            {
                Stop(element, state);
            }
        }

        DoubleAnimation? move = null;
        if (moves)
        {
            // From the offset to the place of the element; FillBehavior.Stop: at the end the value is the base value 0.
            move = new DoubleAnimation
            {
                From = offset,
                To = 0d,
                Duration = duration,
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop,
            };
            move.Completed += OnCompleted;
        }

        DoubleAnimation? fade = null;
        if (fades)
        {
            // From transparent to the base opacity of the element (no To: the base value is the destination).
            fade = new DoubleAnimation
            {
                From = 0d,
                Duration = duration,
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop,
            };
            if (move is null)
            {
                fade.Completed += OnCompleted;
            }
        }

        if (move is not null)
        {
            var transform = new TranslateTransform();
            state.Transform = transform;
            element.RenderTransform = transform;
            transform.BeginAnimation(sideways ? TranslateTransform.XProperty : TranslateTransform.YProperty, move);
        }

        if (fade is not null)
        {
            state.Fades = true;
            element.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        }
    }

    // ---- Leaving --------------------------------------------------------------------------------------------------

    private static void OnLeavingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        var state = StateOf(element);
        if (e.NewValue is not true || !IsEnabled || !element.IsVisible || TimeOf(DisappearKey) is not { } time)
        {
            // Not leaving after all, or no motion (the owner removes the element at once): nothing of ours stays on it.
            Stop(element, state);
            return;
        }

        var duration = new Duration(time);
        var ease = EaseOf(ExitEaseKey);
        var offset = OffsetOf(ToastOffsetKey);

        // The exit takes over from a running entrance where it stands: the same transform, the opacity of this moment.
        state.Run = new object();
        var transform = state.Transform;
        if (transform is null && offset != 0d && HasNoTransformOfItsOwn(element))
        {
            transform = new TranslateTransform();
            state.Transform = transform;
            element.RenderTransform = transform;
        }

        transform?.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation { To = offset, Duration = duration, EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd },
            HandoffBehavior.SnapshotAndReplace);
        state.Fades = true;
        element.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation { To = 0d, Duration = duration, EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd },
            HandoffBehavior.SnapshotAndReplace);
    }

    // ---- Shared ---------------------------------------------------------------------------------------------------

    private static TravelState StateOf(DependencyObject element)
    {
        if (element.GetValue(StateProperty) is not TravelState state)
        {
            state = new TravelState();
            element.SetValue(StateProperty, state);
        }

        return state;
    }

    /// <summary>
    /// Whether this class may put its transform on the element: the element has no render transform of its own (a
    /// local value, or one from a style or template that is not the identity).
    /// </summary>
    private static bool HasNoTransformOfItsOwn(UIElement element) =>
        element.ReadLocalValue(UIElement.RenderTransformProperty) == DependencyProperty.UnsetValue
        && (element.RenderTransform is not { } transform || transform.Value.IsIdentity);

    /// <summary>Takes everything an entrance or an exit put on the element off again: clocks first, then the transform.</summary>
    private static void Stop(UIElement element, TravelState state)
    {
        if (state.Run is null)
        {
            return;
        }

        state.Run = null;
        if (state.Fades)
        {
            state.Fades = false;
            element.BeginAnimation(UIElement.OpacityProperty, null);
        }

        if (state.Transform is { } transform)
        {
            state.Transform = null;
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            if (ReferenceEquals(element.ReadLocalValue(UIElement.RenderTransformProperty), transform))
            {
                element.ClearValue(UIElement.RenderTransformProperty);
            }
        }
    }

    /// <summary>The bookkeeping of the entrance and the exit of one element.</summary>
    private sealed class TravelState
    {
        /// <summary>The element's visibility and Loaded are followed (an entrance is set).</summary>
        public bool Follows { get; set; }

        /// <summary>The entrance was played - or skipped because motion is off - since the element became visible.</summary>
        public bool Entered { get; set; }

        /// <summary>The identity of the running entrance or exit; null when nothing of this class is on the element.</summary>
        public object? Run { get; set; }

        /// <summary>The opacity of the element is animated by the running motion.</summary>
        public bool Fades { get; set; }

        /// <summary>The transform the running motion put on the element.</summary>
        public TranslateTransform? Transform { get; set; }
    }
}
