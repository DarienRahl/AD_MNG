using System.Windows.Controls;

namespace AdMng.App.PowerShell;

/// <summary>
/// A check box that shows a state and asks for its change: a click or Space runs its command and never turns the box by itself, so
/// the box says what the view model says - also when the change is refused (the question of the trusted mode was answered "no", a
/// system policy forbids it). Its <c>IsChecked</c> is bound one way.
/// </summary>
internal sealed class StateCheckBox : CheckBox
{
    protected override void OnToggle()
    {
        // The view model decides, and tells through the binding.
    }
}
