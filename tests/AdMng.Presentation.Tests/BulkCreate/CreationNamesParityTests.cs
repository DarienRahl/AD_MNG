using AdMng.Core.Batch.Creation;
using AdMng.Core.Model;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Infrastructure;

namespace AdMng.Presentation.Tests.BulkCreate;

/// <summary>
/// The golden test of the F3 contract (docs/dev/phase3-bulk-create.md 2.2): the copies in <see cref="CreationNames"/> (Core)
/// equal <see cref="AttributeOption.ClassesOf"/> and <see cref="AccountNames"/> (Presentation), which Core cannot reference.
/// </summary>
public sealed class CreationNamesParityTests
{
    public static TheoryData<ObjectKind> Kinds() => [.. Enum.GetValues<ObjectKind>()];

    [Theory]
    [MemberData(nameof(Kinds))]
    public void ClassChains_AreTheSame(ObjectKind kind) => Assert.Equal(AttributeOption.ClassesOf(kind), CreationNames.ClassChainOf(kind));

    [Fact]
    public void ForbiddenSamCharacters_AreTheSame()
    {
        for (var c = '\0'; c < '\u3000'; c++)
        {
            Assert.True(AccountNames.IsInvalidSamCharacter(c) == CreationNames.IsInvalidSamCharacter(c), $"U+{(int)c:X4}");
        }
    }

    [Fact]
    public void Limits_AreTheSame()
    {
        Assert.Equal(AccountNames.MaxSamAccountNameLength, CreationNames.MaxUserSamAccountName);
        Assert.Equal(AccountNames.MaxComputerNameLength, CreationNames.MaxComputerName);
        Assert.Equal(AccountNames.MaxComputerNameLength + 1, CreationNames.MaxSamAccountName(ObjectKind.Computer));
    }
}
