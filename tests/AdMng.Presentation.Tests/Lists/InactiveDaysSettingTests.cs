using AdMng.Presentation.Lists;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.TestDoubles;
using static AdMng.Presentation.Tests.TestDoubles.TestDirectory;

namespace AdMng.Presentation.Tests.Lists;

/// <summary>The Inactive chip follows the InactiveDays setting: title and, when active, the listed rows.</summary>
public class InactiveDaysSettingTests
{
    [Fact]
    public async Task ChangedDays_WhileTheChipIsActive_ReloadWithTheNewThreshold()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = await harness.OpenContainerAsync(It);
        var chip = list.FilterChips.Single(c => c.Kind == QuickFilterKind.Inactive);
        chip.IsActive = true;
        await list.CurrentLoad;
        Assert.Contains(Threshold(90), list.LastSearch!.Filter, StringComparison.Ordinal);
        var searches = harness.Directory.Searches.Count;

        await harness.Settings.UpdateAsync(s => s.InactiveDays = 30, TestContext.Current.CancellationToken);
        await list.CurrentLoad;

        Assert.Equal("Inactive 30 days", chip.Title);
        Assert.Contains(Threshold(30), list.LastSearch!.Filter, StringComparison.Ordinal);
        Assert.Equal(searches + 1, harness.Directory.Searches.Count);
    }

    [Fact]
    public async Task ChangedDays_WithTheChipOff_OnlyRetitleIt()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = await harness.OpenContainerAsync(It);
        var searches = harness.Directory.Searches.Count;

        await harness.Settings.UpdateAsync(s => s.InactiveDays = 45, TestContext.Current.CancellationToken);

        Assert.Equal("Inactive 45 days", list.FilterChips.Single(c => c.Kind == QuickFilterKind.Inactive).Title);
        Assert.Equal(searches, harness.Directory.Searches.Count);
    }

    [Fact]
    public async Task OtherSettingsChanges_DoNotReloadAListWithTheActiveChip()
    {
        var harness = await TestHarness.ConnectedAsync();
        var list = await harness.OpenContainerAsync(It);
        list.FilterChips.Single(c => c.Kind == QuickFilterKind.Inactive).IsActive = true;
        await list.CurrentLoad;
        var searches = harness.Directory.Searches.Count;

        await harness.Settings.UpdateAsync(s => s.ColumnLayouts["container"] = [new ColumnLayoutEntry("name", 300)], TestContext.Current.CancellationToken);
        await harness.Settings.UpdateAsync(s => s.AddRecentContainer(It), TestContext.Current.CancellationToken);

        Assert.Equal(searches, harness.Directory.Searches.Count);
    }

    private static string Threshold(int days) => $"(lastLogonTimestamp<={TestHarness.Now.AddDays(-days).ToFileTime()})";
}
