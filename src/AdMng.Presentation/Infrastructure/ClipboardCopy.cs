using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;

namespace AdMng.Presentation.Infrastructure;

/// <summary>
/// One-click copying with the short non-modal confirmation ("Skopiowano: E-mail") shared by the details pane, property
/// pages, lists and tool windows. Nothing is written anywhere but the clipboard; the copied text is never logged.
/// </summary>
public static class ClipboardCopy
{
    [ThreadStatic]
    private static int _confirmed;

    /// <summary>
    /// Copies confirmed on the calling thread so far. The view's check-mark feedback compares it before and after a copy
    /// button's command ran: a copy that did not reach the clipboard (busy, nothing to copy) shows no check mark.
    /// </summary>
    public static int ConfirmedCount => _confirmed;

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
    /// wording ("ready for Outlook"). A <paramref name="note"/> (e.g. selected rows that were still loading) is appended
    /// to the message and makes it a warning.
    /// </summary>
    public static bool Values(
        IClipboardService clipboard,
        INotificationService notifications,
        ILocalizer localizer,
        IReadOnlyList<string> values,
        int skipped,
        string separator,
        string label,
        bool isMail = false,
        string? note = null)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            notifications.Show(
                NotificationSeverity.Warning,
                Append(isMail ? localizer[L.Copy.NoMail] : localizer.Format(L.Copy.NoValue, label), note));
            return false;
        }

        if (!clipboard.SetText(string.Join(separator, values)))
        {
            return false;
        }

        // One value: "Copied: E-mail address." (also with skipped objects - never "Copied 1 values").
        var message = values.Count == 1 ? localizer.Format(L.Copy.CopiedValue, label)
            : isMail ? localizer.Format(L.Copy.CopiedMail, values.Count)
            : localizer.Format(L.Copy.CopiedValues, label, values.Count);
        if (skipped > 0)
        {
            message += " " + localizer.Format(isMail ? L.Copy.SkippedNoMail : L.Copy.SkippedNoValue, skipped);
        }

        notifications.Show(skipped > 0 || note is not null ? NotificationSeverity.Warning : NotificationSeverity.Info, Append(message, note));
        _confirmed++;
        return true;
    }

    /// <summary>
    /// Copies a text and shows <paramref name="confirmation"/> once it is on the clipboard (with <paramref name="note"/>
    /// appended as a warning, e.g. selected rows that were still loading).
    /// </summary>
    public static bool Text(IClipboardService clipboard, INotificationService notifications, string? text, string confirmation, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(notifications);
        if (string.IsNullOrEmpty(text) || !clipboard.SetText(text))
        {
            return false;
        }

        notifications.Show(note is null ? NotificationSeverity.Info : NotificationSeverity.Warning, Append(confirmation, note));
        _confirmed++;
        return true;
    }

    /// <summary>
    /// Copies a secret the protected way (<see cref="IClipboardService.SetSecret"/>: kept out of clipboard history and
    /// cleared after a while) and shows <paramref name="confirmation"/> once it is on the clipboard.
    /// </summary>
    public static bool Secret(IClipboardService clipboard, INotificationService notifications, string? secret, string confirmation)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(notifications);
        if (string.IsNullOrEmpty(secret) || !clipboard.SetSecret(secret))
        {
            return false;
        }

        notifications.Show(NotificationSeverity.Info, confirmation);
        _confirmed++;
        return true;
    }

    private static string Append(string message, string? note) => note is null ? message : $"{message} {note}";

    private static string Message(ILocalizer localizer, string key, string label)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer.Format(key, label);
    }
}
