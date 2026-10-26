using AdMng.Presentation.Shell;

namespace AdMng.Presentation.Tests.Shell;

public sealed class AppVersionTests
{
    [Theory]
    [InlineData("0.4.0+30d210ef8de9986815fe252712a9955088ebd883", "0.4.0", "30d210e")]
    [InlineData("0.5.0-beta.1+abc", "0.5.0-beta.1", "abc")]
    [InlineData("0.4.0", "0.4.0", null)]
    [InlineData("0.4.0+", "0.4.0", null)]
    public void Parse_SplitsTheVersionAndAShortCommit(string informational, string version, string? commit) =>
        Assert.Equal((version, commit), AppVersion.Parse(informational, null));

    [Fact]
    public void Parse_WithoutAnInformationalVersion_UsesTheAssemblyVersion() =>
        Assert.Equal(("1.2.3", (string?)null), AppVersion.Parse(null, new Version(1, 2, 3, 0)));

    [Fact]
    public void TheBuild_HasAVersion_AndTheDisplayNamesItsCommit()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+", AppVersion.Version);
        Assert.StartsWith(AppVersion.Version, AppVersion.Display, StringComparison.Ordinal);
        if (AppVersion.Commit is { } commit)
        {
            Assert.EndsWith($"({commit})", AppVersion.Display, StringComparison.Ordinal);
        }
    }
}
