using System.Windows.Input;
using AdMng.Presentation.Icons;

namespace AdMng.Presentation.Feedback;

/// <summary>
/// What a list says when it has no rows (track V, stage V2b-5b; docs/dev/v2-status-controls.md): one sentence, a picture
/// and at most one action - "No GPO matches the search. [Clear the search]". A list with rows, a list that is still being
/// read and a list that could not be read have no empty state (<see cref="None"/>): the first has its rows, the second its
/// status line and placeholder rows, the third says why in its status line - "could not be read" is an error, not an empty
/// list.
/// </summary>
/// <param name="Message">The sentence; empty (<see cref="None"/>) when the list has nothing to say.</param>
/// <param name="Icon">The picture: the list's own picture when there is nothing here, <see cref="ObjectIcon.Query"/> when a filter hides everything.</param>
/// <param name="Action">The one next step, if any: "Clear the filter" when a filter hides the rows, "Refresh" otherwise.</param>
public sealed record EmptyListState(string Message, ObjectIcon Icon = ObjectIcon.Folder, PageStatusAction? Action = null)
{
    /// <summary>The list has nothing to say (it shows rows, or it is not read, or it could not be read).</summary>
    public static EmptyListState None { get; } = new(string.Empty);

    /// <summary>True when there is a sentence to show.</summary>
    public bool HasMessage => Message.Length > 0;

    /// <summary>The label of the action, empty when there is none.</summary>
    public string ActionText => Action?.Label ?? string.Empty;

    /// <summary>What the action runs; null when there is none.</summary>
    public ICommand? ActionCommand => Action?.Command;

    /// <summary>The parameter of the command, if it takes one.</summary>
    public object? ActionParameter => Action?.Parameter;
}
