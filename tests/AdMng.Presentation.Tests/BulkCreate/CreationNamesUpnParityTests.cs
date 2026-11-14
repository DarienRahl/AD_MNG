using AdMng.Core.Batch.Creation;
using AdMng.Presentation.Infrastructure;

namespace AdMng.Presentation.Tests.BulkCreate;

/// <summary>
/// The UPN prefix rule of <see cref="CreationNames"/> (Core, F3 B1) equals <see cref="AccountNames.IsValidUpnPrefix"/>
/// (Presentation), which Core cannot reference – next to the contract's <c>CreationNamesParityTests</c>
/// (docs/dev/phase3-bulk-create.md 2.2).
/// </summary>
public sealed class CreationNamesUpnParityTests
{
    [Fact]
    public void EveryCharacter_IsJudgedTheSame()
    {
        for (var c = '\0'; c < '\u3001'; c++)
        {
            var prefix = "jan" + c + "kowalski";
            Assert.True(AccountNames.IsValidUpnPrefix(prefix) == CreationNames.IsValidUpnPrefix(prefix), $"U+{(int)c:X4}");
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("jan.kowalski")]
    [InlineData("łucja.żak")]
    [InlineData(" jan")]
    [InlineData("kd201")]
    public void Samples_AreJudgedTheSame(string prefix) => Assert.Equal(AccountNames.IsValidUpnPrefix(prefix), CreationNames.IsValidUpnPrefix(prefix));
}
