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

    [Fact]
    public async Task AGroupMarkedInOneDomain_DoesNotMarkTheSameNamedGroupOfAnotherDomain_WhichCanBeMarkedThere()
    {
        // Two domains with the same OU layout (a test clone) and their own domain SIDs.
        var contoso = new FakeDirectoryBuilder();
        var contosoSample = contoso.AddPrivilegedAccessSample(contoso.AddSampleCompany());
        var fabrikam = new FakeDirectoryBuilder(Fabrikam, domainSid: "S-1-5-21-1004336348-1177238915-682003330");
        var fabrikamSample = fabrikam.AddPrivilegedAccessSample(fabrikam.AddSampleCompany());
        await using var host = new ConnectionScopeHost(domains: [contoso.Build(), fabrikam.Build()]);
        await using var a = host.CreateConnection();
        await using var b = host.CreateConnection();
        await a.ConnectAsync(user: "jan.kowalski");
        await b.ConnectAsync(Fabrikam, "anna.zielińska");
        var settings = host.Provider.GetRequiredService<ISettingsStore>();

        // Contoso marks its payroll group.
        using var reviewOfA = await RunAsync(a);
        a.Dialogs.PickedObjects.Enqueue([contosoSample.Payroll]);
        await reviewOfA.Privileges.MarkGroupCommand.ExecuteAsync(null);
        reviewOfA.Privileges.Marks[0].Note = "Płace contoso";
        await reviewOfA.RunCommand.ExecuteAsync(null);
        Assert.Contains(MarkedGroupNotes(reviewOfA.Privileges, "Dorota Łukasiewicz"), n => n == "Płace contoso");

        // The fabrikam window: the mark's SID is contoso's, so its DN does not name fabrikam's group of the same name.
        using var reviewOfB = await RunAsync(b);
        Assert.Null(reviewOfB.Privileges.Error);
        Assert.Empty(MarkedGroupNotes(reviewOfB.Privileges, "Dorota Łukasiewicz"));

        // Fabrikam marks its own group: a second mark (not taken for the contoso one), which its members get.
        b.Dialogs.PickedObjects.Enqueue([fabrikamSample.Payroll]);
        await reviewOfB.Privileges.MarkGroupCommand.ExecuteAsync(null);
        var marks = settings.Current.AccountReview.Privileges.Marks;
        Assert.Equal(2, marks.Count);
        Assert.Equal(marks[0].Dn, marks[1].Dn);
        Assert.StartsWith("S-1-5-21-1004336348-1177238915-682003330-", marks[1].Sid, StringComparison.Ordinal);
        reviewOfB.Privileges.Marks[1].Note = "Płace fabrikam";
        await reviewOfB.RunCommand.ExecuteAsync(null);
        Assert.Equal(["Płace fabrikam"], MarkedGroupNotes(reviewOfB.Privileges, "Dorota Łukasiewicz"));

        // Marking contoso's group again is still recognised as a duplicate.
        a.Dialogs.PickedObjects.Enqueue([contosoSample.Payroll]);
        await reviewOfA.Privileges.MarkGroupCommand.ExecuteAsync(null);
        Assert.Equal(2, settings.Current.AccountReview.Privileges.Marks.Count);
    }

    private static List<string?> MarkedGroupNotes(PrivilegedAccessViewModel tab, string name) =>
        [.. tab.Items.Where(r => r.Name == name).SelectMany(r => r.Facts.Findings)
            .Where(f => f.Kind == PrivilegeFindingKind.MarkedGroup).Select(f => f.Note)];

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
