using System.Windows;
using System.Windows.Threading;

namespace AdMng.App.Infrastructure;

/// <summary>Helpers shared by the smoke test and the UI tests: off-screen windows and waiting for an idle dispatcher.</summary>
internal static class UiTestSupport
{
    /// <summary>Shows a window far outside the visible desktop without activating it, then forces a layout pass.</summary>
    public static void ShowOffScreen(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10000;
        window.Top = -10000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        window.UpdateLayout();
    }

    /// <summary>
    /// Lets the dispatcher process all queued work (layout, render, bindings, continuations of async loads) a few times,
    /// with short pauses for background tasks that post back to the UI thread.
    /// </summary>
    public static async Task WaitForIdleAsync(int rounds = 6, int pauseMilliseconds = 40)
    {
        for (var i = 0; i < rounds; i++)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(pauseMilliseconds).ConfigureAwait(true);
        }

        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Closes every window of the application except <paramref name="keep"/>.</summary>
    public static void CloseAllWindows(Window? keep = null)
    {
        if (Application.Current is not { } application)
        {
            return;
        }

        foreach (var window in application.Windows.OfType<Window>().ToList())
        {
            if (!ReferenceEquals(window, keep))
            {
                window.Close();
            }
        }
    }
}
