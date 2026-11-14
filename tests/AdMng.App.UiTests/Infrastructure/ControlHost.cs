using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using AdMng.App.Behaviors;
using AdMng.App.Infrastructure;

namespace AdMng.App.UiTests.Infrastructure;

/// <summary>
/// What the tests of a single control share (track V, stage V2): a window that hosts the control off-screen, a press
/// like the operator's, and the check that no motion is left on an element.
/// </summary>
internal static class ControlHost
{
    /// <summary>
    /// A window with the elements in a column, shown off-screen (an element is visible, and its template applied, only
    /// in a shown window). Close it in a <c>finally</c>.
    /// </summary>
    public static Window Show(params FrameworkElement[] elements)
    {
        var panel = new StackPanel { Background = Brushes.White };
        foreach (var element in elements)
        {
            element.HorizontalAlignment = HorizontalAlignment.Left;
            panel.Children.Add(element);
        }

        var window = new Window { Width = 800, Height = 300, Content = panel, WindowStyle = WindowStyle.ToolWindow };
        UiTestSupport.ShowOffScreen(window);
        return window;
    }

    /// <summary>
    /// Presses a button like the operator (UI Automation invoke). The click itself is dispatched: wait for an idle
    /// dispatcher before looking at what it did. Throws <see cref="System.Windows.Automation.ElementNotEnabledException"/>
    /// for a button that UI Automation reports as not enabled.
    /// </summary>
    public static void Press(Button button)
    {
        var peer = Assert.IsAssignableFrom<AutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(button));
        var invoke = Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
        invoke.Invoke();
    }

    /// <summary>A named element of the control's template (the template is applied once the control was laid out).</summary>
    public static T Part<T>(Control control, string name)
        where T : class
    {
        Assert.True(control.Template is not null, $"{control.GetType().Name} has no template");
        return Assert.IsAssignableFrom<T>(control.Template!.FindName(name, control));
    }

    /// <summary>
    /// The element is as if no motion had ever been on it: no motion of the behaviours runs, no animation clock is
    /// left, there is no render transform of the behaviour, and the opacity is the base value.
    /// </summary>
    public static void AssertAtRest(FrameworkElement element)
    {
        Assert.False(Motion.IsRunning(element), "a motion is still on the element");
        Assert.False(element.HasAnimatedProperties, "an animation clock is left on the element");
        Assert.Same(DependencyProperty.UnsetValue, element.ReadLocalValue(UIElement.RenderTransformProperty));
        Assert.True(element.RenderTransform.Value.IsIdentity, "the element is still moved");
        Assert.Equal(1d, element.Opacity);
    }

    /// <summary>The colour of a solid brush (the brushes of the theme used by the controls are solid).</summary>
    public static Color ColorOf(Brush? brush) => Assert.IsType<SolidColorBrush>(brush).Color;

    /// <summary>The colour of a brush of the running theme, as the element finds it.</summary>
    public static Color ThemeColor(FrameworkElement element, string key) => ColorOf(element.FindResource(key) as Brush);
}
