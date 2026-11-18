using AdMng.Presentation.PowerShell;

namespace AdMng.Presentation.Tests.PowerShell;

/// <summary>The history of the window (track R, decision D5): in memory, without a plain-text password, without a repeat.</summary>
public sealed class PowerShellInputHistoryTests
{
    private static PowerShellInputHistory Create(Func<string, bool>? remember = null) => new(remember ?? (_ => true));

    [Fact]
    public void ALineIsRemembered_WithoutItsTrailingBlankSpace_AndABlankOneIsNot()
    {
        var history = Create();

        Assert.True(history.Add("Get-AdmUser jan  \n"));
        Assert.False(history.Add("   "));
        Assert.False(history.Add(string.Empty));
        Assert.False(history.Add(null!));

        Assert.Equal(["Get-AdmUser jan"], history.Entries);
    }

    [Fact]
    public void ALineThePolicyRefuses_IsNotRemembered()
    {
        var history = Create(line => !line.Contains("-AsPlainText", StringComparison.Ordinal));

        Assert.False(history.Add("ConvertTo-SecureString 'x' -AsPlainText -Force"));
        Assert.True(history.Add("Get-Date"));

        Assert.Equal(["Get-Date"], history.Entries);
    }

    [Fact]
    public void ARepeatOfTheLastLine_IsNotRemembered_ButAnEarlierLineIsRememberedAgain()
    {
        var history = Create();

        history.Add("a");
        history.Add("a");
        history.Add("b");
        history.Add("a");

        Assert.Equal(["a", "b", "a"], history.Entries);
    }

    [Fact]
    public void TheHistoryIsBounded_TheOldestGoFirst()
    {
        var history = Create();

        for (var index = 0; index < PowerShellInputHistory.Capacity + 25; index++)
        {
            history.Add("line " + index);
        }

        Assert.Equal(PowerShellInputHistory.Capacity, history.Entries.Count);
        Assert.Equal("line 25", history.Entries[0]);
        Assert.Equal("line " + (PowerShellInputHistory.Capacity + 24), history.Entries[^1]);
    }

    [Fact]
    public void UpAndDown_WalkTheHistory_AndDownEndsAtTheDraft()
    {
        var history = Create();
        history.Add("one");
        history.Add("two");
        history.Add("three");

        Assert.Equal("three", history.Previous("draft"));
        Assert.True(history.IsNavigating);
        Assert.Equal("two", history.Previous("ignored"));
        Assert.Equal("one", history.Previous("ignored"));
        Assert.Null(history.Previous("ignored"));
        Assert.Equal("two", history.Next());
        Assert.Equal("three", history.Next());
        Assert.Equal("draft", history.Next());
        Assert.False(history.IsNavigating);
        Assert.Null(history.Next());
    }

    [Fact]
    public void WithAnEmptyHistory_UpDoesNothing()
    {
        var history = Create();

        Assert.Null(history.Previous("draft"));
        Assert.False(history.IsNavigating);
    }

    [Fact]
    public void AddingALine_EndsTheNavigation_SoUpStartsFromTheNewestAgain()
    {
        var history = Create();
        history.Add("one");
        history.Add("two");
        Assert.Equal("two", history.Previous(string.Empty));
        Assert.Equal("one", history.Previous(string.Empty));

        history.Add("three");

        Assert.False(history.IsNavigating);
        Assert.Equal("three", history.Previous("new draft"));
        Assert.Equal("new draft", history.Next());
    }

    [Theory]
    [InlineData("user", 4, 3)]
    [InlineData("USER", 4, 3)]
    [InlineData("user", 3, 1)]
    [InlineData("user", 1, -1)]
    [InlineData("nothing", 4, -1)]
    [InlineData("", 4, -1)]
    [InlineData("user", 99, 3)]
    public void TheSearch_FindsTheNewestLineBeforeAPlace_IgnoringCase(string query, int before, int expected)
    {
        var history = Create();
        history.Add("Get-AdmGroup *");
        history.Add("Get-AdmUser jan");
        history.Add("Get-Date");
        history.Add("Get-AdmUser anna");

        Assert.Equal(expected, history.FindOlder(query, before));
    }
}
