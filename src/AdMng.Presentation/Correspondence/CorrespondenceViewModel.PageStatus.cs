using System.ComponentModel;

namespace AdMng.Presentation.Correspondence;

/// <summary>The status lines of the recipients and mailbox owners pages go to the window's status bar.</summary>
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
