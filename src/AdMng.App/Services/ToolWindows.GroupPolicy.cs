using System.Windows;
using AdMng.Presentation.GroupPolicy;

namespace AdMng.App.Services;

/// <summary>The modeless Group Policy windows (K2): the comparison of two GPOs, one window per comparison.</summary>
public sealed partial class ToolWindows
{
    /// <summary>Shows the comparison (modeless; the window disposes the view model when it closes).</summary>
    public Window ShowGpoComparison(GpoComparisonViewModel comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        try
        {
            return _windows.Show(comparison);
        }
        catch
        {
            comparison.Dispose();
            throw;
        }
    }
}
