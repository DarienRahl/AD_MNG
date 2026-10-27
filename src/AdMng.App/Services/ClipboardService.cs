using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using Microsoft.Extensions.Logging;

namespace AdMng.App.Services;

/// <summary>
/// Windows clipboard. Another process (RDP clipboard redirection, clipboard managers) may hold the clipboard open for
/// a moment; WPF retries internally and if it still fails the operator gets a warning toast instead of a crash.
/// </summary>
/// <remarks>
/// Secrets (<see cref="SetSecret"/>) are put on the clipboard with the formats Windows and clipboard managers honour to
/// keep them out of clipboard history, cloud clipboard and clipboard monitors, and are cleared after
/// <see cref="SecretLifetime"/> when the clipboard still holds them. Call from the UI thread.
/// </remarks>
public sealed class ClipboardService(INotificationService notifications, ILocalizer localizer, ILogger<ClipboardService> logger)
    : IClipboardService
{
    /// <summary>Any data in this format tells clipboard monitors (history, cloud clipboard, managers) to ignore the content.</summary>
    internal const string ExcludeFromMonitoringFormat = "ExcludeClipboardContentFromMonitorProcessing";

    /// <summary>DWORD 0: Windows clipboard history (Win+V) does not keep the content.</summary>
    internal const string CanIncludeInHistoryFormat = "CanIncludeInClipboardHistory";

    /// <summary>DWORD 0: the content is not synchronized to other devices by cloud clipboard.</summary>
    internal const string CanUploadToCloudFormat = "CanUploadToCloudClipboard";

    /// <summary>How long a secret stays on the clipboard.</summary>
    internal static readonly TimeSpan SecretLifetime = TimeSpan.FromSeconds(60);

    private DispatcherTimer? _clearTimer;
    private string? _secret;

    public bool SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (ExternalException ex)
        {
            ReportBusy(ex);
            return false;
        }
    }

    public bool SetSecret(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        try
        {
            Clipboard.SetDataObject(CreateSecretData(secret), copy: true);
        }
        catch (ExternalException ex)
        {
            ReportBusy(ex);
            return false;
        }

        ScheduleClear(secret);
        return true;
    }

    /// <summary>The formatted content for "Wklej sformatowaną treść" (U1c, <see cref="ClipboardHtmlReader"/>); a busy
    /// clipboard is reported like a failed copy.</summary>
    public ClipboardContent? ReadFormatted()
    {
        try
        {
            return ClipboardHtmlReader.Read();
        }
        catch (ExternalException ex)
        {
            ReportBusy(ex);
            return null;
        }
    }

    /// <summary>The text plus the formats that keep it out of clipboard history, cloud clipboard and clipboard monitors.</summary>
    internal static DataObject CreateSecretData(string secret)
    {
        var data = new DataObject();
        data.SetText(secret, TextDataFormat.UnicodeText);

        // Streams are copied to the clipboard as raw bytes (an HGLOBAL), which is what these formats expect.
        data.SetData(ExcludeFromMonitoringFormat, Dword(0));
        data.SetData(CanIncludeInHistoryFormat, Dword(0));
        data.SetData(CanUploadToCloudFormat, Dword(0));
        return data;
    }

    private static MemoryStream Dword(int value) => new(BitConverter.GetBytes(value));

    /// <summary>Clears the clipboard after <see cref="SecretLifetime"/> if it still holds the secret (and only the newest secret).</summary>
    private void ScheduleClear(string secret)
    {
        // The new secret replaced the previous one on the clipboard, so only the new one needs clearing.
        _clearTimer?.Stop();
        _secret = secret;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SecretLifetime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(_clearTimer, timer))
            {
                _clearTimer = null;
                ClearSecret();
            }
        };
        _clearTimer = timer;
        timer.Start();
    }

    private void ClearSecret()
    {
        var secret = _secret;
        _secret = null;
        if (secret is null)
        {
            return;
        }

        try
        {
            // Something the operator copied since then stays.
            if (Clipboard.ContainsText() && string.Equals(Clipboard.GetText(), secret, StringComparison.Ordinal))
            {
                Clipboard.Clear();
            }
        }
        catch (ExternalException ex)
        {
            logger.LogWarning("The clipboard could not be cleared (HRESULT 0x{HResult:X8}).", ex.HResult);
        }
    }

    private void ReportBusy(ExternalException ex)
    {
        // Never log the text (it may contain directory values, a password or a pasted message) - only that the clipboard was busy.
        logger.LogWarning("The clipboard could not be opened (HRESULT 0x{HResult:X8}).", ex.HResult);
        notifications.Show(NotificationSeverity.Warning, localizer[L.Ui.ClipboardBusy]);
    }
}
