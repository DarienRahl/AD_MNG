namespace AdMng.Core.Batch.Creation;

/// <summary>What a column of the pasted table feeds (docs/dev/phase3-bulk-create.md 2.1, 2.6).</summary>
public enum ColumnTargetKind
{
    /// <summary><em>Pomiń</em>: not used.</summary>
    Ignore,

    /// <summary>Any writable attribute of the class (<see cref="ColumnMapping.Attribute"/>, 2.2).</summary>
    Attribute,

    /// <summary><em>Tylko we wzorcach</em>: only reachable through <c>{$column:…}</c> (2.3).</summary>
    PatternOnly,

    /// <summary><em>Imię i nazwisko</em>: split into <c>givenName</c> / <c>sn</c> by <see cref="ColumnMapping.Order"/>.</summary>
    FullName,

    /// <summary><c>manager</c> of users and contacts, <c>managedBy</c> of groups and computers (2.5).</summary>
    Manager,

    /// <summary>Per-row group membership (2.5).</summary>
    Groups,

    /// <summary><c>c</c>, <c>co</c> and <c>countryCode</c> together from a name or code (2.5).</summary>
    Country,

    /// <summary>Per-row <c>accountExpires</c> of users (2.5).</summary>
    ExpiresAt,
}

/// <summary>The order of the parts of a full name.</summary>
public enum NameOrder
{
    /// <summary><em>Jan Kowalski</em>: the last token is the surname.</summary>
    GivenNameFirst,

    /// <summary><em>Kowalski Jan</em>: the first token is the surname.</summary>
    SurnameFirst,
}

/// <summary>The operator's explicit per-column case choice (<em>Wielkość liter</em>), applied after normalisation.</summary>
public enum TextCase
{
    /// <summary><em>bez zmian</em>.</summary>
    AsIs,

    /// <summary><em>Jak W Nazwie</em>: each word and each part of a hyphenated name capitalized (<c>pl-PL</c> casing).</summary>
    Title,

    /// <summary><em>małe</em>.</summary>
    Lower,

    /// <summary><em>WIELKIE</em>.</summary>
    Upper,
}

/// <summary>Why a column suggestion was made beyond the header (shown as <c>BulkCreate.SuggestionNote.&lt;note&gt;</c>).</summary>
public enum SuggestionNote
{
    None,

    /// <summary>The samples are Polish mobile numbers (a <em>Telefon</em> column became <c>mobile</c>).</summary>
    MobileNumbersDetected,

    /// <summary>Phone samples are written as <c>48xxxxxxxxx</c> without <c>+</c> (2.6 offers <em>Format międzynarodowy</em>).</summary>
    PhonesWithoutPlus,

    /// <summary>Suggested from the content of a column without a header.</summary>
    ContentBased,
}

/// <summary>The operator's (or the auto-mapping's) choice for one column.</summary>
/// <param name="Column">The 0-based column index (<see cref="TableColumn.Index"/>).</param>
/// <param name="Kind">What the column feeds.</param>
/// <param name="Attribute">The LDAP display name for <see cref="ColumnTargetKind.Attribute"/>.</param>
/// <param name="Order">The name order for <see cref="ColumnTargetKind.FullName"/>.</param>
/// <param name="Case">The case choice for text targets.</param>
public sealed record ColumnMapping(
    int Column,
    ColumnTargetKind Kind,
    string? Attribute = null,
    NameOrder Order = NameOrder.GivenNameFirst,
    TextCase Case = TextCase.AsIs)
{
    public override string ToString() => $"Column {Column}: {Kind}{(Attribute is null ? string.Empty : " " + Attribute)} ({Order}, {Case})";
}

/// <summary>A suggested target of a column (<see cref="ColumnSynonyms.Suggest"/>).</summary>
/// <param name="Kind">What the column should feed.</param>
/// <param name="Attribute">The LDAP display name for <see cref="ColumnTargetKind.Attribute"/>.</param>
/// <param name="Order">The name order for <see cref="ColumnTargetKind.FullName"/>.</param>
/// <param name="Note">Why, beyond the header.</param>
public sealed record ColumnSuggestion(
    ColumnTargetKind Kind,
    string? Attribute = null,
    NameOrder Order = NameOrder.GivenNameFirst,
    SuggestionNote Note = SuggestionNote.None)
{
    public override string ToString() => $"{Kind}{(Attribute is null ? string.Empty : " " + Attribute)} ({Order}, {Note})";
}
