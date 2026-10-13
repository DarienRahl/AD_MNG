namespace AdMng.Presentation.Infrastructure;

/// <summary>Live (client-side) checks of account names; the change planner and the DC validate again.</summary>
public static class AccountNames
{
    /// <summary>The SAM limit of sAMAccountName for users and groups.</summary>
    public const int MaxSamAccountNameLength = 20;

    /// <summary>NetBIOS computer names are limited to 15 characters (sAMAccountName = NAME$).</summary>
    public const int MaxComputerNameLength = 15;

    /// <summary>Characters AD rejects in sAMAccountName: <c>" / \ [ ] : ; | = , + * ? &lt; &gt; @</c>.</summary>
    public static bool IsInvalidSamCharacter(char c) => c is '"' or '/' or '\\' or '[' or ']' or ':' or ';' or '|'
        or '=' or ',' or '+' or '*' or '?' or '<' or '>' or '@' || char.IsControl(c);

    /// <summary>True when the text is a syntactically valid sAMAccountName of at most <paramref name="maxLength"/> characters.</summary>
    public static bool IsValidSamAccountName(string? value, int maxLength = MaxSamAccountNameLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maxLength
        && !value.Any(IsInvalidSamCharacter)
        && !value.All(c => c is '.' or ' ');

    /// <summary>True when the UPN prefix (the part before '@') contains no characters AD rejects.</summary>
    public static bool IsValidUpnPrefix(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '@' or '"' or '(' or ')' or ',' or ':' or ';' or '<' or '>' or '[' or '\\' or ']');
}
