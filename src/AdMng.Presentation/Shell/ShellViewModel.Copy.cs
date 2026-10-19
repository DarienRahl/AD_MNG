using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Shell;

/// <summary>
/// Copying values of the list selection (phase 1 "Kopiowanie wartości"): "Kopiuj jako…" (<see cref="CopyAsCommand"/>),
/// Ctrl+C with the visible columns (<see cref="CopyRowsCommand"/>) and the copy buttons of the details pane
/// (<see cref="CopyDetailsValueCommand"/>). Only the clipboard is written; values the formats read come live from the
/// session's DC.
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>Copy commands whose CanExecute follows the selection and the session (see <see cref="RefreshCommandStates"/>).</summary>
    private IRelayCommand[] CopyStateCommands => [CopyAsCommand, CopyRowsCommand, CopyRowsWithHeadersCommand];

    /// <summary>
    /// "Kopiuj jako…": the value of <paramref name="format"/> for every selected object, in list order. Formats other than
    /// name, DN and canonical path read the objects again (in batches) - the rows only hold the visible columns. Objects
    /// without the value are skipped and counted in the confirmation.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCopyAs))]
    private async Task CopyAsAsync(CopyFormat format)
    {
        var rows = OrderedSelection();
        var session = Sessions.Current;
        if (rows.Count == 0 || !CanCopyAs(format))
        {
            return;
        }

        var label = Localizer[ObjectCopyFormats.LabelKey(format)];
        var attributes = ObjectCopyFormats.Attributes(format);
        List<DirectoryObject?> objects;
        if (attributes.Count == 0)
        {
            objects = [.. rows.Select(r => r.Object)];
        }
        else
        {
            try
            {
                var dns = rows.Select(r => r.Dn).ToList();
                var found = await Task.Run(() => DirectoryLookup.ReadManyAsync(session!, dns, attributes, CancellationToken.None)).ConfigureAwait(true);
                objects = [.. rows.Select(r => found.GetValueOrDefault(DnKey.For(r.Dn)))];
            }
            catch (Exception ex) when (ex is DirectoryException or OperationCanceledException or ObjectDisposedException)
            {
                await _dialogs.ShowErrorAsync(label, Errors.Translate(ex)).ConfigureAwait(true);
                return;
            }
        }

        var netBios = session?.Info.DomainNetBiosName;
        var values = new List<string>();
        var skipped = 0;
        foreach (var obj in objects)
        {
            if (obj is not null && ObjectCopyFormats.ValueOf(obj, format, netBios) is { } value)
            {
                values.Add(value);
            }
            else
            {
                skipped++;
            }
        }

        var isMail = format == CopyFormat.Mail;
        if (isMail)
        {
            // One recipient once, whoever else has the same address (a contact and its user, a shared mailbox).
            values = [.. values.Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        ClipboardCopy.Values(_clipboard, _notifications, Localizer, values, skipped, ObjectCopyFormats.Separator(format), label, isMail);
    }

    private bool CanCopyAs(CopyFormat format)
    {
        if (CurrentSelection.Count == 0)
        {
            return false;
        }

        var session = Sessions.Current;
        if (ObjectCopyFormats.Attributes(format).Count > 0 && session is null)
        {
            return false;
        }

        if (format == CopyFormat.DomainLogin && string.IsNullOrWhiteSpace(session?.Info.DomainNetBiosName))
        {
            return false;
        }

        return ObjectCopyFormats.IsAllowed(format, session?.Schema);
    }

    /// <summary>Ctrl+C: the selected rows with the visible columns as tab-separated text (Excel, Teams, tickets).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyRows() => CopySelectedRows(includeHeaders: false);

    /// <summary>The selected rows with the column headers as the first line.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyRowsWithHeaders() => CopySelectedRows(includeHeaders: true);

    /// <summary>A copy button of the details pane: the line's value, confirmed with its label.</summary>
    [RelayCommand(CanExecute = nameof(CanCopyDetailsValue))]
    private void CopyDetailsValue(SummaryField? field)
    {
        if (field is { CanCopy: true, CopyText: { } text })
        {
            ClipboardCopy.Value(_clipboard, _notifications, Localizer, text, field.Label);
        }
    }

    private bool CanCopyDetailsValue(SummaryField? field) => field?.CanCopy == true;

    /// <remarks>
    /// Columns with secret values (passwords, LAPS, BitLocker, attributes the schema marks confidential - added in the
    /// column chooser or by a saved view) are left out: a secret is copied only after the explicit reveal of the Attribute
    /// Editor, which keeps it out of the clipboard history.
    /// </remarks>
    private void CopySelectedRows(bool includeHeaders)
    {
        var rows = OrderedSelection();
        if (SelectedDocument is not { } document || rows.Count == 0)
        {
            return;
        }

        var schema = Sessions.Current?.Schema;
        var columns = document.VisibleColumns.ToList();
        var secret = columns.Where(c => SecretAttributes.IsSecret(c.Attribute, schema)).ToList();
        var copied = columns.Where(c => !secret.Contains(c)).ToList();
        var leftOut = secret.Count == 0 ? null : Localizer.Format(L.Copy.SecretColumnsLeftOut, string.Join(", ", secret.Select(c => c.Header)));
        if (copied.Count == 0)
        {
            if (leftOut is not null)
            {
                _notifications.Show(NotificationSeverity.Warning, leftOut);
            }

            return;
        }

        var text = ListClipboardText.Format(
            [.. copied.Select(c => c.Header)],
            rows.Select(r => (IReadOnlyList<string>)[.. copied.Select(c => r[c.Attribute])]),
            includeHeaders);
        if (!_clipboard.SetText(text))
        {
            return;
        }

        var message = Localizer.Format(L.Copy.CopiedRows, rows.Count);
        _notifications.Show(
            leftOut is null ? NotificationSeverity.Info : NotificationSeverity.Warning,
            leftOut is null ? message : $"{message} {leftOut}");
    }

    /// <summary>The selection in the list's order (the view's selection is in click order).</summary>
    private List<ObjectRowViewModel> OrderedSelection()
    {
        var selection = CurrentSelection;
        if (selection.Count < 2 || SelectedDocument is not { } document)
        {
            return [.. selection];
        }

        var positions = new Dictionary<ObjectRowViewModel, int>(document.Items.Count);
        for (var i = 0; i < document.Items.Count; i++)
        {
            positions[document.Items[i]] = i;
        }

        return [.. selection.OrderBy(r => positions.TryGetValue(r, out var position) ? position : int.MaxValue)];
    }
}
