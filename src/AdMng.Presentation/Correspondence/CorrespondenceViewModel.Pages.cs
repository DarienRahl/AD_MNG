namespace AdMng.Presentation.Correspondence;

/// <summary>The window's tab changes that concern the message, preview, send and responses pages.</summary>
public sealed partial class CorrespondenceViewModel
{
    /// <summary>
    /// Entering "3 · Podgląd" refreshes the preview (debounced), "4 · Wysyłka" the summary, "5 · Odpowiedzi" takes the voting
    /// options of the message page (their first option is "yes", the second "no").
    /// </summary>
    partial void OnSelectedTabChanged(CorrespondenceTab value)
    {
        // The line says what the tab that is shown says (a page says it again when it comes to the front).
        Status.Front = PageStatusOf(value);
        switch (value)
        {
            case CorrespondenceTab.Preview:
                Preview.ScheduleRefresh();
                break;
            case CorrespondenceTab.Send:
                Send.RefreshSummaryCommand.Execute(null);
                break;
            case CorrespondenceTab.Responses:
                if (MessagePageViewModel.NormalizeVotingOptions(Message.VotingOptions) is { } options)
                {
                    Responses.VotingOptions = options;
                }

                break;
        }
    }
}
