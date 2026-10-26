using System.Security.Cryptography;

namespace AdMng.Watchdog.Keytab;

/// <summary>The account password (design 4.1): 120 printable ASCII characters (0x21–0x7E) from the OS CSPRNG, with at
/// least one upper-case letter, lower-case letter, digit and symbol (AD complexity), straight into a pinned buffer.</summary>
internal static class PasswordGenerator
{
    public const int Length = 120;

    public static SecretBuffer Generate()
    {
        var buffer = new SecretBuffer(Length);
        try
        {
            while (true)
            {
                buffer.Clear();
                for (var i = 0; i < Length; i++)
                {
                    buffer.Append((byte)RandomNumberGenerator.GetInt32(0x21, 0x7F));
                }

                if (IsComplex(buffer.Span))
                {
                    return buffer;
                }
            }
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    internal static bool IsComplex(ReadOnlySpan<byte> password)
    {
        bool upper = false, lower = false, digit = false, symbol = false;
        foreach (var c in password)
        {
            switch (c)
            {
                case >= (byte)'A' and <= (byte)'Z': upper = true; break;
                case >= (byte)'a' and <= (byte)'z': lower = true; break;
                case >= (byte)'0' and <= (byte)'9': digit = true; break;
                default: symbol = true; break;
            }
        }

        return upper && lower && digit && symbol;
    }
}
