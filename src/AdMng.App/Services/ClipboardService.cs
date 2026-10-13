using System.Runtime.InteropServices;
using System.Windows;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using Microsoft.Extensions.Logging;

namespace AdMng.App.Services;

/// <summary>
/// Windows clipboard. Another process (RDP clipboard redirection, clipboard managers) may hold the clipboard open for
/// a moment; WPF retries internally and if it still fails the operator gets a warning toast instead of a crash.
/// </summary>
public sealed class ClipboardService(INotificationService notifications, ILocalizer localizer, ILogger<ClipboardService> logger)
    : IClipboardService
{
    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException ex)
        {
            // Never log the text (it may contain directory values) - only that the clipboard was busy.
            logger.LogWarning("The clipboard could not be opened (HRESULT 0x{HResult:X8}).", ex.HResult);
            notifications.Show(NotificationSeverity.Warning, localizer[L.Ui.ClipboardBusy]);
        }
    }
}
