namespace AdMng.Core.Correspondence.Import;

/// <summary>E-mail addresses and names in imported text (MailerWPiA's rules, design note 3.1).</summary>
public static class EmailAddressText
{
    private const string Track = "U5 T1 core";

    /// <summary>MailerWPiA's pattern, a top-level domain of 2–63 characters and not junk (example.*, sentry.io, schemas.*, w3.org).</summary>
    public static bool IsValid(string? address) => throw new NotImplementedException(Track);

    /// <summary>Trims "mailto:", "?…", punctuation and quotes; lower-invariant.</summary>
    public static string Normalize(string? address) => throw new NotImplementedException(Track);

    /// <summary>Every address of <paramref name="text"/> in order: normalized, valid, de-duplicated.</summary>
    public static IReadOnlyList<string> Extract(string? text) => throw new NotImplementedException(Track);

    /// <summary>
    /// Collapses whitespace, turns "Kontakt: X" into "X", trims bullets, dashes and quotes; "" when the name is an address;
    /// at most 120 characters.
    /// </summary>
    public static string CleanName(string? name) => throw new NotImplementedException(Track);

    /// <summary>The first valid address of a cell such as "a@x.pl; b@y.pl", or null.</summary>
    public static string? FirstValid(string? cell) => throw new NotImplementedException(Track);
}
