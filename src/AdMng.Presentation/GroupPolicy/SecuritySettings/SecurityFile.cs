namespace AdMng.Presentation.GroupPolicy.SecuritySettings;

/// <summary>
/// One file of the security settings editor (GptTmpl.inf, audit.csv, Groups.xml): the bytes as read (the compare-and-swap of
/// the save), the document as read (never changed: what the rows compare with), the document as edited, and a localized
/// reason when the file cannot be read (its pages are then read-only). Memory only (CLAUDE.md rule 1).
/// </summary>
internal sealed class SecurityFile<T>
    where T : class
{
    private readonly Func<byte[]?, T> _parse;
    private readonly Func<T, byte[]> _serialize;

    private SecurityFile(string path, byte[]? original, T originalDocument, T document, string? error, Func<byte[]?, T> parse, Func<T, byte[]> serialize)
    {
        Path = path;
        Original = original;
        OriginalDocument = originalDocument;
        Document = document;
        Error = error;
        _parse = parse;
        _serialize = serialize;
        Baseline = serialize(originalDocument);
    }

    /// <summary>The file's path in the GPO folder.</summary>
    public string Path { get; }

    /// <summary>The content as read (null: there was none).</summary>
    public byte[]? Original { get; }

    /// <summary>The document as read.</summary>
    public T OriginalDocument { get; }

    /// <summary>The document as edited.</summary>
    public T Document { get; }

    /// <summary>Why the file cannot be read (localized), null when it was read.</summary>
    public string? Error { get; }

    /// <summary>The file changed on SYSVOL under the edits (the save is refused as a conflict).</summary>
    public bool Conflict { get; set; }

    /// <summary>The bytes of the document as read (what an unchanged document serializes to).</summary>
    private byte[] Baseline { get; }

    /// <summary>The bytes the save writes.</summary>
    public byte[] Content => _serialize(Document);

    /// <summary>The document differs from what was read.</summary>
    public bool IsChanged => Error is null && !Baseline.AsSpan().SequenceEqual(Content);

    /// <exception cref="Core.GroupPolicy.GpoParseException">The content is not a valid file.</exception>
    public static SecurityFile<T> Of(string path, byte[]? content, Func<byte[]?, T> parse, Func<T, byte[]> serialize) =>
        new(path, content, parse(content), parse(content), null, parse, serialize);

    public static SecurityFile<T> Unreadable(string path, string error, Func<byte[]?, T> parse, Func<T, byte[]> serialize) =>
        new(path, null, parse(null), parse(null), error, parse, serialize);

    /// <summary>Back to what was read (the edits are dropped).</summary>
    public SecurityFile<T> Discarded() => Error is null ? Of(Path, Original, _parse, _serialize) : this;

    /// <summary>The same edits on top of <paramref name="content"/>, now the file on SYSVOL (what this tab saved).</summary>
    public SecurityFile<T> Rebased(byte[]? content) => new(Path, content, _parse(content), Document, null, _parse, _serialize);

    /// <summary>Same content; a missing file and an empty one are the same.</summary>
    public static bool SameContent(byte[]? a, byte[]? b) => (a ?? []).AsSpan().SequenceEqual(b ?? []);
}
