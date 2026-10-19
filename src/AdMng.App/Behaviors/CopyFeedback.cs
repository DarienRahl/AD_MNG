using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using AdMng.App.Controls;

namespace AdMng.App.Behaviors;

/// <summary>
/// Visual confirmation of a one-click copy button: after the click its glyph turns into a check mark for
/// <see cref="Duration"/> (the toast "Skopiowano: …" confirms the copy as well, also for screen readers). Set by the
/// <c>Ad.CopyButton</c> style; <c>&lt;Button b:CopyFeedback.IsEnabled="True"/&gt;</c> on other glyph buttons. View only:
/// nothing is copied here, the button's command does that.
/// </summary>
public static class CopyFeedback
{
    /// <summary>How long the check mark is shown.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(1.5);

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(CopyFeedback), new PropertyMetadata(false, OnIsEnabledChanged));

    /// <summary>True while the check mark is shown (styles, UI tests).</summary>
    public static readonly DependencyProperty IsShowingProperty = DependencyProperty.RegisterAttached(
        "IsShowing", typeof(bool), typeof(CopyFeedback), new PropertyMetadata(false));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(FeedbackState), typeof(CopyFeedback), new PropertyMetadata(null));

    public static bool GetIsEnabled(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsEnabledProperty);
    }

    public static void SetIsEnabled(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsEnabledProperty, value);
    }

    public static bool GetIsShowing(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsShowingProperty);
    }

    private static void SetIsShowing(DependencyObject element, bool value) => element.SetValue(IsShowingProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button)
        {
            return;
        }

        button.Click -= OnClick;
        if (e.NewValue is true)
        {
            button.Click += OnClick;
        }
    }

    // Click is raised before the button executes its command (and only while the command can execute).
    private static void OnClick(object sender, RoutedEventArgs e)
    {
        var button = (ButtonBase)sender;
        if (button.GetValue(StateProperty) is FeedbackState running)
        {
            // Clicked again while the check mark shows: keep it a little longer.
            running.Timer.Stop();
            running.Timer.Start();
            return;
        }

        var state = new FeedbackState(button.Content, new DispatcherTimer(DispatcherPriority.Background, button.Dispatcher) { Interval = Duration });
        state.Timer.Tick += (_, _) => Restore(button, state);
        button.SetValue(StateProperty, state);
        button.SetCurrentValue(ContentControl.ContentProperty, Glyphs.Completed);
        SetIsShowing(button, true);
        state.Timer.Start();
    }

    private static void Restore(ButtonBase button, FeedbackState state)
    {
        state.Timer.Stop();
        button.ClearValue(StateProperty);
        button.SetCurrentValue(ContentControl.ContentProperty, state.Content);
        SetIsShowing(button, false);
    }

    private sealed record FeedbackState(object? Content, DispatcherTimer Timer);
}
