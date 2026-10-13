using AdMng.Core.Security;

namespace AdMng.Core.Tests.Security;

public class PasswordGeneratorTests
{
    private readonly PasswordGenerator _generator = new();

    [Theory]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(64)]
    [InlineData(256)]
    public void Generate_HasRequestedLengthAndAllCharacterClasses(int length)
    {
        for (var i = 0; i < 200; i++)
        {
            var password = _generator.Generate(length);

            Assert.Equal(length, password.Length);
            Assert.Contains(password, char.IsAsciiLetterUpper);
            Assert.Contains(password, char.IsAsciiLetterLower);
            Assert.Contains(password, char.IsAsciiDigit);
            Assert.Contains(password, c => PasswordGenerator.Symbols.Contains(c));
        }
    }

    [Fact]
    public void Generate_DefaultsToSixteenCharacters()
    {
        Assert.Equal(PasswordGenerator.DefaultLength, _generator.Generate().Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(257)]
    public void Generate_RejectsUnsafeLengths(int length)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _generator.Generate(length));
    }

    [Fact]
    public void Generate_NeverUsesAmbiguousOrProblematicCharacters()
    {
        var all = string.Concat(Enumerable.Range(0, 2000).Select(_ => _generator.Generate(32)));

        foreach (var forbidden in "0Oo1lI| \"'`\\;,=+-@")
        {
            Assert.DoesNotContain(forbidden, all);
        }

        Assert.All(all, c => Assert.Contains(c, PasswordGenerator.AllCharacters));
    }

    [Fact]
    public void Generate_ProducesUniquePasswords()
    {
        var passwords = Enumerable.Range(0, 5000).Select(_ => _generator.Generate()).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(5000, passwords.Count);
    }

    [Fact]
    public void Generate_UsesTheWholeAlphabet()
    {
        var seen = string.Concat(Enumerable.Range(0, 3000).Select(_ => _generator.Generate(16))).ToHashSet();

        Assert.Equal(PasswordGenerator.AllCharacters.Length, seen.Count);
    }

    [Fact]
    public void Generate_AvoidsForbiddenTokensCaseInsensitively()
    {
        // Every 3-letter combination of a, b, c: without the check about 1% of passwords would contain one.
        var letters = "abc";
        var tokens = (from x in letters from y in letters from z in letters select $"{x}{y}{z}").ToList();

        for (var i = 0; i < 3000; i++)
        {
            var password = _generator.Generate(16, [.. tokens, "ab", null]);

            Assert.DoesNotContain(tokens, t => password.Contains(t, StringComparison.OrdinalIgnoreCase));
        }
    }
}
