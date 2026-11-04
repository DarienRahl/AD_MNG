using System.Globalization;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Presentation.GroupPolicy.AdministrativeTemplates;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.GroupPolicy;

public class PolicyDefinitionsServiceTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PolicyDefinitionsService Service(ScriptedPolicyDefinitionSource source) =>
        new(new TestLocalPolicyDefinitions { Folder = @"C:\Windows\PolicyDefinitions", Source = source });

    [Fact]
    public async Task ALoadThatStartedBeforeInvalidate_IsNotKept()
    {
        var harness = await GroupPolicyHarness.CreateAsync();
        var source = new ScriptedPolicyDefinitionSource(SampleAdministrativeTemplates.Files()) { HoldFirstList = true };
        var service = Service(source);

        var first = service.GetAsync(harness.Session, null, English, reload: false, Ct);
        await source.ListEntered.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        service.Invalidate(); // the store was updated (or "Wczytaj szablony ponownie") while it was read
        source.ReleaseList();
        Assert.Equal(PolicyDefinitionOrigin.LocalStore, (await first).Origin);

        var second = await service.GetAsync(harness.Session, null, English, reload: false, Ct);
        Assert.Equal(2, source.ListCount); // read again: what was read before the update is not kept

        Assert.Same(second, await service.GetAsync(harness.Session, null, English, reload: false, Ct));
        Assert.Equal(2, source.ListCount);
    }

    [Fact]
    public async Task ALoadWithAFailedRead_IsReturned_ButTheNextLoadReadsTheStoreAgain()
    {
        var harness = await GroupPolicyHarness.CreateAsync();
        var source = new ScriptedPolicyDefinitionSource(SampleAdministrativeTemplates.Files()) { FailOnce = { "contoso.admx" } };
        var service = Service(source);

        var first = await service.GetAsync(harness.Session, null, English, reload: false, Ct);

        Assert.True(first.HasReadFailures);
        Assert.Equal(PolicyFileReadProblem.ReadFailed, first.FileProblems["contoso.admx"]);
        Assert.Contains(first.Set.Issues, i => i is { Kind: PolicyDefinitionIssueKind.FileUnreadable, FileName: "contoso.admx" });
        Assert.Null(first.Set.Find(SampleAdministrativeTemplates.Updates));

        var second = await service.GetAsync(harness.Session, null, English, reload: false, Ct);
        Assert.Equal(2, source.ListCount);
        Assert.False(second.HasReadFailures);
        Assert.Empty(second.FileProblems);
        Assert.NotNull(second.Set.Find(SampleAdministrativeTemplates.Updates));
        Assert.Same(second, await service.GetAsync(harness.Session, null, English, reload: false, Ct)); // a complete set is kept
    }

    [Fact]
    public async Task AFileTooLargeToRead_IsNamedAsSuch_AndTheSetIsKept()
    {
        var harness = await GroupPolicyHarness.CreateAsync();
        var source = new ScriptedPolicyDefinitionSource(SampleAdministrativeTemplates.Files())
        {
            Lengths = { ["contoso.admx"] = PolicyDefinitionLoader.MaxFileBytes + 1 },
        };
        var service = Service(source);

        var first = await service.GetAsync(harness.Session, null, English, reload: false, Ct);

        Assert.Equal(PolicyFileReadProblem.TooLarge, first.FileProblems["contoso.admx"]);
        Assert.False(first.HasReadFailures); // not by chance: the next load would find the same
        Assert.Same(first, await service.GetAsync(harness.Session, null, English, reload: false, Ct));
        Assert.Equal(1, source.ListCount);
    }
}
