using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;

namespace AdMng.Presentation.Infrastructure;

/// <summary>
/// One-click copying with the short non-modal confirmation ("Skopiowano: E-mail") shared by the details pane, property
/// pages, lists and tool windows. Nothing is written anywhere but the clipboard; the copied text is never logged.
/// </summary>
public static class ClipboardCopy
{
    /// <summary>
    /// Copies one value and confirms it with its label. Empty values are not copied. Returns false when nothing was
    /// copied (empty value, or the clipboard was busy - the clipboard service warned then).
    /// </summary>
    public static bool Value(IClipboardService clipboard, INotificationService notifications, ILocalizer localizer, string? value, string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        return Text(clipboard, notifications, value, Message(localizer, L.Copy.CopiedValue, label));
    }

    /// <summary>
    /// Copies several values joined by <paramref name="separator"/>; <paramref name="skipped"/> objects had no value. The
    /// confirmation says how many values were copied and how many objects were skipped; when there is nothing to copy the
    /// operator is warned and the clipboard is left alone. E-mail addresses (<paramref name="isMail"/>) get their own
    /// wording ("ready for Outlook").
    /// </summary>
    public static bool Values(
        IClipboardService clipboard,
        INotificationService notifications,
        ILocalizer localizer,
        IReadOnlyList<string> values,
        int skipped,
        string separator,
        string label,
        bool isMail = false)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            notifications.Show(NotificationSeverity.Warning, isMail ? localizer[L.Copy.NoMail] : localizer.Format(L.Copy.NoValue, label));
            return false;
        }

        if (!clipboard.SetText(string.Join(separator, values)))
        {
            return false;
        }

        var message = values.Count == 1 && skipped == 0 ? localizer.Format(L.Copy.CopiedValue, label)
            : isMail ? localizer.Format(L.Copy.CopiedMail, values.Count)
            : localizer.Format(L.Copy.CopiedValues, label, values.Count);
        if (skipped > 0)
        {
            message += " " + localizer.Format(isMail ? L.Copy.SkippedNoMail : L.Copy.SkippedNoValue, skipped);
        }

        notifications.Show(skipped > 0 ? NotificationSeverity.Warning : NotificationSeverity.Info, message);
        return true;
    }

    /// <summary>Copies a text and shows <paramref name="confirmation"/> once it is on the clipboard.</summary>
    public static bool Text(IClipboardService clipboard, INotificationService notifications, string? text, string confirmation)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(notifications);
        if (string.IsNullOrEmpty(text) || !clipboard.SetText(text))
        {
            return false;
        }

        notifications.Show(NotificationSeverity.Info, confirmation);
        return true;
    }

    private static string Message(ILocalizer localizer, string key, string label)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer.Format(key, label);
    }
}
