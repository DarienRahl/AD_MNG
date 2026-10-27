using System.Collections.Concurrent;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.PrivilegedAccess;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Settings;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.Presentation.Tests.Connections;

/// <summary>
/// The privileged access review (README U4) with one DI scope per connection window: the grant sources and the SYSVOL reader
/// are application-wide, but a run reads only through the session (DC, credential) of the window that runs it; the tab's
/// settings are shared by every window and a change saved in one is never undone by another.
/// </summary>
public sealed class PrivilegedAccessConnectionTests
{
    private const string Fabrikam = "fabrikam.test";

    [Fact]
    public async Task GrantSourcesAndSysvol_ReadThroughTheSessionOfTheWindowThatRunsTheReview()
    {
        var contoso = new FakeDirectoryBuilder();
        var company = contoso.AddSampleCompany();
        var sample = contoso.AddPrivilegedAccessSample(company);
        var sysvol = new FakeSysvolFileReader();
        contoso.AddGroupPolicySample(company, sample, sysvol);
        var recorder = new RecordingGrantSource();
        await using var host = new ConnectionScopeHost(
            services =>
            {
                // As AppHost registers them: one reader and one GPO source for the application.
                services.AddSingleton<ISysvolFileReader>(sysvol);
                services.AddSingleton<IPrivilegeGrantSource>(sp => new GroupPolicyGrantSource(sp.GetRequiredService<ISysvolFileReader>()));
                services.AddSingleton<IPrivilegeGrantSource>(recorder);
            },
            [contoso.Build(), SampleDomain.Create(Fabrikam).Session]);
        await using var a = host.CreateConnection();
        await using var b = host.CreateConnection();
        var inA = await a.ConnectAsync(user: "jan.kowalski");
        var inB = await b.ConnectAsync(Fabrikam, "anna.zielińska");

        using var reviewOfB = await RunAsync(b);
        Assert.Null(reviewOfB.Privileges.Error);
        var readInB = Assert.Single(recorder.Sessions);
        Assert.Same(inB, readInB);
        Assert.Equal(@"FABRIKAM\anna.zielińska", readInB.Info.BoundIdentity);

        // Fabrikam has no GPO that reaches a computer, so SYSVOL was never opened - in particular not on contoso's DC.
        Assert.Empty(sysvol.OpenedOn);

        using var reviewOfA = await RunAsync(a);
        Assert.Null(reviewOfA.Privileges.Error);
        Assert.Equal(new[] { inB, inA }, recorder.Sessions);
        Assert.Equal([inA.Info.Server], sysvol.OpenedOn);
        Assert.Equal(0, sysvol.OpenConnections);
        Assert.Contains(reviewOfA.Privileges.Items, r => r.Name == "Ewa Błaszczyk");
    }

    [Fact]
    public async Task PrivilegeSettings_SavedInOneWindow_AreShownInTheOther_AndNotUndoneByItsNextSave()
    {
        await using var host = new ConnectionScopeHost(domains: [SampleDomain.Create(FakeDirectoryBuilder.DefaultDomain).Session, SampleDomain.Create(Fabrikam).Session]);
        await using var a = host.CreateConnection();
        await using var b = host.CreateConnection();
        await a.ConnectAsync(user: "jan.kowalski");
        await b.ConnectAsync(Fabrikam, "anna.zielińska");
        var settings = host.Provider.GetRequiredService<ISettingsStore>();
        using var reviewOfA = a.Get<IDialogViewModelFactory>().CreateAccountReview();
        using var reviewOfB = b.Get<IDialogViewModelFactory>().CreateAccountReview();
        var tabOfA = reviewOfA.Privileges;
        var tabOfB = reviewOfB.Privileges;

        // A changes the level of a catalogue group: its own tab is not rebuilt, B's tab shows the stored level.
        var groupInA = tabOfA.GroupLevels[0];
        var raised = groupInA.Levels.First(l => l.Value != groupInA.Group.DefaultLevel);
        groupInA.Level = raised;
        Assert.Same(groupInA, tabOfA.GroupLevels[0]);
        Assert.Equal(raised.Value, Assert.Single(settings.Current.AccountReview.Privileges.LevelOverrides).Level);
        Assert.Equal(raised.Value, tabOfB.GroupLevels[0].Level.Value);

        // A write of other settings (another window's recent container) rebuilds nothing.
        var levelsOfB = tabOfB.GroupLevels;
        await settings.UpdateAsync(s => s.AddRecentContainer("OU=Sprzedaż,DC=fabrikam,DC=test"), TestContext.Current.CancellationToken);
        Assert.Same(levelsOfB, tabOfB.GroupLevels);

        // B's next save keeps A's level, and A shows B's change.
        tabOfB.IncludeComputers = !tabOfB.IncludeComputers;
        var stored = settings.Current.AccountReview.Privileges;
        Assert.Equal(raised.Value, Assert.Single(stored.LevelOverrides).Level);
        Assert.Equal(tabOfB.IncludeComputers, stored.IncludeComputers);
        Assert.Equal(tabOfB.IncludeComputers, tabOfA.IncludeComputers);
        Assert.Equal(raised.Value, tabOfA.GroupLevels[0].Level.Value);
    }

    private static async Task<AccountReviewViewModel> RunAsync(TestConnection window)
    {
        var review = window.Get<IDialogViewModelFactory>().CreateAccountReview();
        await review.InitializeAsync();
        review.SelectedTabIndex = AccountReviewViewModel.PrivilegesTab;
        await review.RunCommand.ExecuteAsync(null);
        return review;
    }

    /// <summary>A grant source that records the session of every run and grants nothing.</summary>
    private sealed class RecordingGrantSource : IPrivilegeGrantSource
    {
        private readonly ConcurrentQueue<IDirectorySession> _sessions = new();

        public PrivilegeSource Source => PrivilegeSource.OperatorRules;

        public IReadOnlyList<IDirectorySession> Sessions => [.. _sessions];

        public Task<IReadOnlyList<PrivilegeGrant>> CollectAsync(PrivilegeScanContext context, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(context);
            _sessions.Enqueue(context.Session);
            return Task.FromResult<IReadOnlyList<PrivilegeGrant>>([]);
        }
    }
}
