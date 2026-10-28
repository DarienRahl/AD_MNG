using System.Collections.ObjectModel;
using AdMng.Core.Correspondence.Fields;
using AdMng.Core.Messaging.Images;

namespace AdMng.Core.Correspondence.Composition;

/// <summary>
/// The draft as edited: subject, body fragment (sanitized subset, cid images), images by content id, signature, custom
/// fields, list item template (groups) and the salutation.
/// </summary>
public sealed record CorrespondenceDraft
{
    public string Subject { get; init; } = "";

    public string BodyHtml { get; init; } = "";

    /// <summary>Body images by content id.</summary>
    public IReadOnlyDictionary<string, MessageImage> Images { get; init; } = ReadOnlyDictionary<string, MessageImage>.Empty;

    public CorrespondenceSignature? Signature { get; init; }

    public IReadOnlyList<CustomFieldDefinition> CustomFields { get; init; } = [];

    /// <summary>The HTML of one <c>{{lista}}</c> item of grouped messages.</summary>
    public string ListItemTemplate { get; init; } = "";

    public string Salutation { get; init; } = BuiltInFields.DefaultSalutation;

    public override string ToString() => nameof(CorrespondenceDraft);
}

/// <summary>One message: one row, or several rows of one address (a group, design note 3.4).</summary>
/// <param name="Email">The recipient's address.</param>
/// <param name="Name">The recipient's name (may be empty).</param>
/// <param name="Rows">The fields of each row; the first row gives the fields outside <c>{{lista}}</c>.</param>
public sealed record CorrespondenceUnit(string Email, string Name, IReadOnlyList<IReadOnlyDictionary<string, string>> Rows)
{
    public override string ToString() => $"{nameof(CorrespondenceUnit)} ({Rows.Count} rows)";
}

/// <summary>One message rendered for one unit.</summary>
/// <param name="Subject">The merged subject.</param>
/// <param name="HtmlDocument">The whole sanitized document (<c>cid:</c> images).</param>
/// <param name="Images">Only the images referenced by the document.</param>
/// <param name="UnknownFields">Fields that are not known for this unit (rendered empty).</param>
/// <param name="EmptyFields">Known fields that are empty for this unit.</param>
public sealed record RenderedCorrespondence(
    string Subject, string HtmlDocument, IReadOnlyList<MessageImage> Images,
    IReadOnlyList<string> UnknownFields, IReadOnlyList<string> EmptyFields)
{
    /// <summary>Bytes of <see cref="Images"/> together.</summary>
    public long ImageBytes => Images.Sum(i => (long)i.Data.Length);

    /// <summary>True when the images break a per-message limit (<see cref="CorrespondenceLimits.MaxImagesPerMessage"/>,
    /// <see cref="CorrespondenceLimits.MaxImagesBytesPerMessage"/>): an error before sending.</summary>
    public bool ExceedsImageLimits => Images.Count > CorrespondenceLimits.MaxImagesPerMessage || ImageBytes > CorrespondenceLimits.MaxImagesBytesPerMessage;

    public override string ToString() => nameof(RenderedCorrespondence);
}
