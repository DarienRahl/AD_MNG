namespace AdMng.Presentation.BulkCreate;

/// <summary>
/// One note above the accounts of a bulk creation (<see cref="BulkCreateViewModel.ResultNotes"/>): how the results below
/// are to be read, true for as long as they are shown. Most of them are cautions (the canary stop, accounts left
/// disabled, the forest UPN hint); the replication note is information, and the view gives it the glyph of an
/// information (V2b-5b) without becoming a different kind of note.
/// </summary>
/// <param name="Text">The sentence, in the language of the interface.</param>
/// <param name="IsInformation">The sentence informs and does not warn: the view shows the information glyph.</param>
public sealed record BulkResultNote(string Text, bool IsInformation = false)
{
    /// <summary>The kind of note, never the sentence: it may name a domain controller (the records of this namespace print no value).</summary>
    public override string ToString() => IsInformation ? "BulkResultNote (information)" : "BulkResultNote (caution)";
}
