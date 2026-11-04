namespace AdMng.Core.Correspondence.Fields;

/// <summary>The built-in <c>{{fields}}</c> of Korespondencja (design note 3.2).</summary>
public static class BuiltInFields
{
    /// <summary>The window's "Zwrot grzecznościowy" (default <see cref="DefaultSalutation"/>).</summary>
    public const string Salutation = "zwrot";

    /// <summary>The row's name, else the salutation (MailerWPiA).</summary>
    public const string Name = "nazwa";

    public const string Email = "email";

    /// <summary>Grouped messages: the list of the group's rows (design note 3.4).</summary>
    public const string List = "lista";

    /// <summary>Grouped messages: the number of rows.</summary>
    public const string Count = "liczba";

    /// <summary>
    /// The chosen Outlook signature, where the body places it (instead of after the body); "" without a signature, in the
    /// subject, in list items and in custom field values. Always the built-in (a column or custom field of that name never
    /// wins).
    /// </summary>
    public const string Signature = "podpis";

    public const string DefaultSalutation = "Szanowni Państwo";

    /// <summary>zwrot, nazwa, email, lista, liczba, podpis.</summary>
    public static IReadOnlyList<string> All { get; } = [Salutation, Name, Email, List, Count, Signature];

    /// <summary>Keys (<see cref="FieldKey"/>) that name the row's name.</summary>
    internal static IReadOnlySet<string> NameKeys { get; } = new HashSet<string>(StringComparer.Ordinal) { "nazwa", "name" };

    /// <summary>Keys (<see cref="FieldKey"/>) that name the address.</summary>
    internal static IReadOnlySet<string> EmailKeys { get; } = new HashSet<string>(StringComparer.Ordinal) { "email", "mail", "adresemail" };

    private static readonly HashSet<string> DuplicateColumns = new(StringComparer.Ordinal) { "nazwa", "name", "email", "mail", "adresemail", "zwrot" };

    /// <summary>
    /// True for columns that duplicate a built-in field (nazwa/name/email/e-mail/mail/adres e-mail/zwrot): they are hidden
    /// from "Wstaw pole".
    /// </summary>
    public static bool IsBuiltInColumn(string column) => DuplicateColumns.Contains(FieldKey.Of(column));
}
