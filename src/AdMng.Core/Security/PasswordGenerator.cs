using System.Security.Cryptography;

namespace AdMng.Core.Security;

/// <summary>
/// Generates random passwords that satisfy the Active Directory complexity rules: at least one upper-case letter,
/// lower-case letter, digit and symbol, drawn from a cryptographically secure RNG. Characters that are easily
/// confused when read aloud or printed (<c>0 O o 1 l I |</c>) are never used, and neither are quotes, backslashes,
/// spaces, CSV delimiters or characters that trigger spreadsheet formula injection.
/// </summary>
/// <remarks>Thread-safe and stateless. Never logs; generated passwords only exist in the returned strings.</remarks>
public sealed class PasswordGenerator
{
    /// <summary>Shortest password that may be generated.</summary>
    public const int MinimumLength = 12;

    /// <summary>Longest password that may be generated (unicodePwd accepts up to 256 characters).</summary>
    public const int MaximumLength = 256;

    /// <summary>Length used when none is specified.</summary>
    public const int DefaultLength = 16;

    /// <summary>Shortest account/name token that must not appear in the password (AD complexity rule).</summary>
    private const int MinimumForbiddenTokenLength = 3;

    private const int MaxAttempts = 100;

    internal const string UpperCase = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    internal const string LowerCase = "abcdefghijkmnpqrstuvwxyz";
    internal const string Digits = "23456789";
    internal const string Symbols = "!#$%&*?^_~";
    internal const string AllCharacters = UpperCase + LowerCase + Digits + Symbols;

    /// <summary>Generates a password of <paramref name="length"/> characters.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Length outside <see cref="MinimumLength"/>..<see cref="MaximumLength"/>.</exception>
    public string Generate(int length = DefaultLength) => Generate(length, []);

    /// <summary>
    /// Generates a password that also does not contain any of <paramref name="forbiddenTokens"/> (case-insensitive),
    /// e.g. the sAMAccountName and the parts of the display name - AD rejects such passwords when complexity is on.
    /// Tokens shorter than three characters are ignored, as AD does.
    /// </summary>
    public string Generate(int length, IEnumerable<string?> forbiddenTokens)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, MinimumLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, MaximumLength);
        ArgumentNullException.ThrowIfNull(forbiddenTokens);
        var tokens = forbiddenTokens
            .Where(t => t is { Length: >= MinimumForbiddenTokenLength })
            .Select(t => t!)
            .ToArray();

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var candidate = GenerateCore(length);
            if (!tokens.Any(t => candidate.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not generate a password that avoids the forbidden tokens.");
    }

    private static string GenerateCore(int length)
    {
        var chars = new char[length];
        try
        {
            chars[0] = Pick(UpperCase);
            chars[1] = Pick(LowerCase);
            chars[2] = Pick(Digits);
            chars[3] = Pick(Symbols);
            for (var i = 4; i < length; i++)
            {
                chars[i] = Pick(AllCharacters);
            }

            // Fisher-Yates shuffle so the guaranteed characters are not at predictable positions.
            for (var i = length - 1; i > 0; i--)
            {
                var j = RandomNumberGenerator.GetInt32(i + 1);
                (chars[i], chars[j]) = (chars[j], chars[i]);
            }

            return new string(chars);
        }
        finally
        {
            Array.Clear(chars);
        }
    }

    private static char Pick(string alphabet) => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
}
