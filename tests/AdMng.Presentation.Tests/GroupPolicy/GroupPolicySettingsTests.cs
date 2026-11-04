using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.TestDoubles;

namespace AdMng.Presentation.Tests.GroupPolicy;

/// <summary>What the Group Policy views remember (design note 8): visibility and backup defaults, never GPO data.</summary>
public sealed class GroupPolicySettingsTests : IDisposable
{
    private readonly TempAppPaths _paths = new();

    public void Dispose() => _paths.Dispose();

    [Fact]
    public async Task Settings_RoundTrip_AndHoldOnlyAPathAndSwitches()
    {
        var store = new JsonSettingsStore(_paths);
        await store.UpdateAsync(s =>
        {
            s.GroupPolicy.ShowInTree = false;
            s.GroupPolicy.LastBackupFolder = @"\\fs01.contoso.test\Kopie GPO\Łódź";
            s.GroupPolicy.BackupCommentTemplate = "Kopia z {0}";
            s.GroupPolicy.BackupBeforeDelete = false;
        }, TestContext.Current.CancellationToken);

        var reloaded = new JsonSettingsStore(_paths);
        await reloaded.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(reloaded.Current.GroupPolicy.ShowInTree);
        Assert.Equal(@"\\fs01.contoso.test\Kopie GPO\Łódź", reloaded.Current.GroupPolicy.LastBackupFolder);
        Assert.Equal("Kopia z {0}", reloaded.Current.GroupPolicy.BackupCommentTemplate);
        Assert.False(reloaded.Current.GroupPolicy.BackupBeforeDelete);
        Assert.Equal(["BackupBeforeDelete", "BackupCommentTemplate", "LastBackupFolder", "ShowInTree"],
            typeof(GroupPolicySettings).GetProperties().Where(p => p.CanWrite).Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Normalize_RepairsHandEditedValues()
    {
        var settings = new AppSettings { GroupPolicy = null! };
        settings.Normalize();
        Assert.True(settings.GroupPolicy.ShowInTree);
        Assert.True(settings.GroupPolicy.BackupBeforeDelete);

        settings.GroupPolicy.LastBackupFolder = "   ";
        settings.GroupPolicy.BackupCommentTemplate = new string('ż', GroupPolicySettings.MaxCommentLength + 20);
        settings.Normalize();
        Assert.Null(settings.GroupPolicy.LastBackupFolder);
        Assert.Equal(GroupPolicySettings.MaxCommentLength, settings.GroupPolicy.BackupCommentTemplate!.Length);
    }
}
