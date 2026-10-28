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

    public const string DefaultSalutation = "Szanowni Państwo";

    /// <summary>zwrot, nazwa, email, lista, liczba.</summary>
    public static IReadOnlyList<string> All { get; } = [Salutation, Name, Email, List, Count];

    /// <summary>
    /// True for columns that duplicate a built-in field (nazwa/name/email/e-mail/mail/adres e-mail/zwrot): they are hidden
    /// from "Wstaw pole".
    /// </summary>
    public static bool IsBuiltInColumn(string column) => throw new NotImplementedException("U5 T1 core");
}
