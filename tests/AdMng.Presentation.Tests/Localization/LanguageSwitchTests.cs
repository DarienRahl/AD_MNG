using System.Globalization;
using AdMng.Core.Batch;
using AdMng.Core.Model;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Tests.TestDoubles;
using AdMng.Presentation.Views;
using static AdMng.Presentation.Tests.TestDoubles.TestDirectory;

namespace AdMng.Presentation.Tests.Localization;

/// <summary>A live language switch reaches every text the main window shows: status bar, tab headers, address, activities.</summary>
public class LanguageSwitchTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public async Task StatusBarSelectionText_IsRaisedInTheNewLanguage()
    {
        var (shell, harness) = await CreateShellAsync(It);
        var document = shell.SelectedDocument!;
        document.SelectedItems = document.Items.Take(3).ToList();
        Assert.Equal("3 selected", shell.SelectionText);
        var raised = Track(shell);

        harness.Localizer.SetCulture(Polish);

        Assert.Contains(nameof(ShellViewModel.SelectionText), raised);
        Assert.Equal("Zaznaczono: 3", shell.SelectionText);
    }

    [Fact]
    public async Task BuiltInViewTab_TitleAndAddress_FollowTheLanguage()
    {
        var (shell, harness) = await CreateShellAsync();
        var document = await OpenBuiltInViewAsync(shell, BuiltInViewKind.LockedUsers);
        Assert.Equal("Locked out users", document.Title);
        Assert.Equal("Locked out users", shell.AddressText);
        var raised = Track(document);

        harness.Localizer.SetCulture(Polish);

        Assert.Contains(nameof(ObjectListViewModel.Title), raised);
        Assert.Equal("Zablokowani użytkownicy", document.Title);
        Assert.Equal("Zablokowani użytkownicy", document.Location!.Title);
        Assert.Equal("Zablokowani użytkownicy", shell.AddressText);
    }

    [Fact]
    public async Task BuiltInViewWithArguments_KeepsThemInTheNewLanguage()
    {
        var (shell, harness) = await CreateShellAsync();
        var document = await OpenBuiltInViewAsync(shell, BuiltInViewKind.InactiveUsers);
        Assert.Equal("Inactive users (90 days)", document.Title);

        harness.Localizer.SetCulture(Polish);

        Assert.Equal("Nieaktywni użytkownicy (90 dni)", document.Title);
    }

    [Fact]
    public async Task SearchTab_FollowsTheLanguage()
    {
        var (shell, harness) = await CreateShellAsync();
        shell.SearchText = "kowal";
        await shell.ShowAllSearchResultsCommand.ExecuteAsync(null);
        var search = shell.SelectedDocument!;
        Assert.Equal("Search: kowal", search.Title);

        harness.Localizer.SetCulture(Polish);

        Assert.Equal("Wyszukiwanie: kowal", search.Title);
        Assert.Equal("Wyszukiwanie: kowal", shell.AddressText);
    }

    [Fact]
    public async Task FindResults_FollowTheLanguage()
    {
        var harness = await B2Harness.CreateAsync();
        using var find = harness.Factory.CreateFind(harness.Company.EmployeesOu);
        find.NameText = "kowal";
        await find.FindNowCommand.ExecuteAsync(null);
        Assert.Equal("Find results", find.Results.Title);

        harness.Localizer.SetCulture(Polish);

        Assert.Equal("Wyniki wyszukiwania", find.Results.Title);
    }

    [Fact]
    public async Task HistoryEntryFromBeforeTheSwitch_IsShownInTheNewLanguage()
    {
        var (shell, harness) = await CreateShellAsync();
        var document = await OpenBuiltInViewAsync(shell, BuiltInViewKind.LockedUsers);
        await shell.NavigateAsync(ListLocation.ForContainer(It));

        harness.Localizer.SetCulture(Polish);
        await document.GoBackCommand.ExecuteAsync(null);

        Assert.Equal(ListLocationKind.View, document.Location!.Kind);
        Assert.Equal("Zablokowani użytkownicy", document.Title);
        Assert.Equal("Zablokowani użytkownicy", shell.AddressText);
    }

    [Fact]
    public async Task ContainerTitles_AreNamesAndStayAsTheyAre()
    {
        var (shell, harness) = await CreateShellAsync(Ksiegowosc);
        var location = shell.SelectedDocument!.Location;

        harness.Localizer.SetCulture(Polish);

        Assert.Same(location, shell.SelectedDocument!.Location);
        Assert.Equal("Księgowość", shell.SelectedDocument.Title);
    }

    [Fact]
    public async Task ActivitySummaryAndStatus_AreRaisedInTheNewLanguage()
    {
        var harness = await TestHarness.ConnectedAsync();
        var plan = new ChangePlan
        {
            Title = "Opis",
            Objects = [new ObjectPlan { DistinguishedName = Ewa, DisplayName = "Ewa Kowalska", Kind = ObjectKind.User, Changes = [] }],
        };
        var (finished, execution) = harness.Activities.Start(plan.Title, 1, (_, _) => Task.FromResult(Succeeded(plan)));
        await execution;
        await finished.Completion;
        var gate = new TaskCompletionSource<BatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (_, running) = harness.Activities.Start("Druga", 1, (_, token) => gate.Task.WaitAsync(token));
        Assert.Equal("Running operations: 1", harness.Activities.Summary);
        Assert.Equal("Completed (1 objects)", finished.StatusText);
        var centerRaised = Track(harness.Activities);
        var activityRaised = Track(finished);

        harness.Localizer.SetCulture(Polish);

        Assert.Contains(nameof(ActivityCenterViewModel.Summary), centerRaised);
        Assert.Equal("Trwające operacje: 1", harness.Activities.Summary);
        Assert.Contains(nameof(ActivityViewModel.StatusText), activityRaised);
        Assert.Equal("Zakończono (obiektów: 1)", finished.StatusText);
        gate.SetResult(Succeeded(plan));
        await running;
    }

    private static List<string?> Track(System.ComponentModel.INotifyPropertyChanged source)
    {
        var raised = new List<string?>();
        source.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        return raised;
    }

    private static async Task<ObjectListViewModel> OpenBuiltInViewAsync(ShellViewModel shell, BuiltInViewKind kind)
    {
        var quick = (FolderNodeViewModel)shell.Tree.ViewsNode.Children[0];
        quick.Children.OfType<ViewNodeViewModel>().Single(v => v.BuiltIn!.Kind == kind).IsSelected = true;
        var document = shell.SelectedDocument!;
        await document.CurrentLoad;
        return document;
    }

    private static async Task<(ShellViewModel Shell, TestHarness Harness)> CreateShellAsync(string? container = null)
    {
        var harness = await TestHarness.ConnectedAsync();
        var shell = harness.CreateShell();
        await shell.InitializeAsync();
        if (container is not null)
        {
            await shell.NavigateAsync(ListLocation.ForContainer(container));
        }

        return (shell, harness);
    }

    private static BatchResult Succeeded(ChangePlan plan)
    {
        var now = TestHarness.Now;
        return new BatchResult(
            plan,
            [.. plan.Objects.Select(o => new ObjectResult(o, ObjectResultStatus.Succeeded, [], o.DistinguishedName, now, now))],
            "dc01.contoso.test",
            "CONTOSO\\helpdesk",
            now,
            now,
            WasCancelled: false);
    }
}
