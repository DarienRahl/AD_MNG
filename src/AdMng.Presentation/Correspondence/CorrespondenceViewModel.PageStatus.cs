using System.ComponentModel;

namespace AdMng.Presentation.Correspondence;

/// <summary>The last sentence of the recipients and mailbox owners pages is kept in <see cref="StatusText"/> (the line says it through the pages' holders).</summary>
public sealed partial class CorrespondenceViewModel
{
    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RecipientsPageViewModel.StatusText))
        {
            return;
        }

        var text = sender switch
        {
            RecipientsPageViewModel recipients => recipients.StatusText,
            MailboxOwnersPageViewModel mailboxes => mailboxes.StatusText,
            _ => null,
        };
        if (!string.IsNullOrEmpty(text))
        {
            StatusText = text;
        }
    }
}
