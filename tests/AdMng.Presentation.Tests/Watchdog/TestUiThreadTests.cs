namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>The UI thread of the view model tests itself: work runs on it, its awaits come back to it, a result is returned.</summary>
public sealed class TestUiThreadTests
{
    [Fact]
    public async Task RunAsync_RunsTheBodyOnTheUiThread()
    {
        using var ui = new TestUiThread();
        var ran = false;

        await ui.RunAsync(async () =>
        {
            Assert.True(ui.CheckAccess());
            await Task.Yield();
            Assert.True(ui.CheckAccess());
            ran = true;
        });

        Assert.True(ran);
        Assert.False(ui.CheckAccess());
        Assert.Empty(ui.Errors);
    }

    [Fact]
    public async Task RunAsync_WithAResult_ReturnsIt_FromTheUiThread()
    {
        using var ui = new TestUiThread();

        var result = await ui.RunAsync(async () =>
        {
            Assert.True(ui.CheckAccess());
            await Task.Yield();
            Assert.True(ui.CheckAccess());                 // the continuation came back to the UI thread
            return "Łódź";
        });

        Assert.Equal("Łódź", result);
        Assert.Empty(ui.Errors);
    }

    [Fact]
    public async Task RunAsync_WithAResult_PassesTheBodysException()
    {
        using var ui = new TestUiThread();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ui.RunAsync<int>(async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("Brak skrzynki „Sekretariat IT”.");
        }));

        Assert.Equal("Brak skrzynki „Sekretariat IT”.", error.Message);
        Assert.Empty(ui.Errors);
    }
}
