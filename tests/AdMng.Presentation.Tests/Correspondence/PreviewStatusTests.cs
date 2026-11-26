using AdMng.Core.Correspondence.Outlook;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Presentation.Tests.GroupPolicy;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// The status line of "3 · Podgląd" (track V, stage V2b-4, unit 5; docs/dev/v-interface-renewal.md "V2b-4 as built"): "Podgląd
/// z Outlooka" - the one thing of the page that waits for something - is its operation, with Stop; why it failed is an error
/// with Retry and leaves the card as it was; the card, its findings and its information line are the content of the page.
/// How the card is made is covered, unchanged, by <see cref="PreviewPageTests"/>.
/// </summary>
public sealed class PreviewStatusTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ThePreviewFromOutlook_IsAnOperation_AndItsResultIsTheCardItself()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenWithMessageAsync();
        CorrespondencePagesScenario.AddRows(window, CorrespondencePagesScenario.Offices());
        window.SelectedTab = CorrespondenceTab.Preview;
        var preview = window.Preview;
        Assert.Same(preview.Status, window.Status.Front);
        var line = new StatusRecorder(window.Status);
        var rendering = new List<bool>();
        scenario.Outlook.BeforeRender = _ => rendering.Add(preview.IsRendering);

        await preview.PreviewViaOutlookCommand.ExecuteAsync(null);

        // Working, then the card: no message of its own - the page tells where the preview came from in its own note.
        Assert.Equal([(PageStatusKind.Working, "Buduję podgląd w Outlooku…"), (PageStatusKind.Idle, string.Empty)], line.Plain);
        Assert.Equal([true], rendering);
        Assert.True(preview.IsFromOutlook);
        Assert.False(preview.IsRendering);
        Assert.Null(preview.Status.Lasting);
    }

    [Fact]
    public async Task Stop_SaysStoppingAtOnce_AndTheAnswerThatComesAnywayIsNotShown()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenWithMessageAsync();
        CorrespondencePagesScenario.AddRows(window, CorrespondencePagesScenario.Offices());
        window.SelectedTab = CorrespondenceTab.Preview;
        var preview = window.Preview;
        using var entered = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        scenario.Outlook.BeforeRender = _ =>
        {
            entered.Release();
            release.Wait(TimeSpan.FromSeconds(30));
        };
        var run = Task.Run(() => preview.PreviewViaOutlookCommand.ExecuteAsync(null), Ct);
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(20), Ct));
        Assert.Equal("Zatrzymaj", window.Status.Current.Action?.Label);
        var line = new StatusRecorder(window.Status);

        // Stop says so at once; Outlook is not told - it finishes the item and deletes it as it always did (the fake builds it
        // inside the call, so the page can only stop waiting once the call has returned).
        window.Status.Current.Action!.Command.Execute(null);
        Assert.Equal((PageStatusKind.Working, "Zatrzymywanie…"), (window.Status.Current.Kind, window.Status.Current.Message));
        release.Release();
        await run.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        var stopped = window.Status.Current;
        Assert.Equal(PageStatusKind.Attention, stopped.Kind);
        Assert.Equal("Budowanie podglądu w Outlooku zatrzymano: podgląd nie został zmieniony.", stopped.Message);
        Assert.Equal("Correspondence.Preview.Status.OutlookStopped", stopped.Log.ToString());
        Assert.Equal("Ponów", stopped.Action?.Label);
        Assert.Same(preview.PreviewViaOutlookCommand, stopped.Action?.Command);
        Assert.False(preview.IsFromOutlook);
        Assert.Equal((PageStatusKind.Working, "Zatrzymywanie…"), (line.Seen[0].Kind, line.Seen[0].Message));
    }

    [Fact]
    public async Task APreviewThatFails_IsAnErrorWithRetry_AndTheKindOfTheFailureAsItsCode_NotAFindingAboutTheCard()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        using var window = await scenario.OpenWithMessageAsync();
        CorrespondencePagesScenario.AddRows(window, CorrespondencePagesScenario.Offices());
        window.SelectedTab = CorrespondenceTab.Preview;
        scenario.Outlook.RenderFailure = FakeOutlookClient.Failure(OutlookFailureKind.NotResponding, "render");

        await window.Preview.PreviewViaOutlookCommand.ExecuteAsync(null);

        var error = window.Status.Current;
        Assert.Equal(PageStatusKind.Error, error.Kind);
        Assert.Equal(
            "Nie udało się pobrać podglądu z Outlooka: Outlook nie odpowiada – sprawdź, czy nie czeka na hasło albo wybór profilu. Wybierz Ponów, aby spróbować ponownie. (NotResponding)",
            error.Message);
        Assert.Equal("Correspondence.Preview.Status.OutlookFailed [NotResponding]", error.Log.ToString());
        Assert.Empty(window.Preview.Warnings);
        Assert.Equal("Ponów", error.Action?.Label);

        // Retry builds it again - this time Outlook answers.
        scenario.Outlook.RenderFailure = null;
        error.Action!.Command.Execute(null);
        await B2Harness.WaitUntilAsync(() => window.Preview.IsFromOutlook);
        Assert.Equal(PageStatusKind.Idle, window.Status.Current.Kind);
    }

    [Fact]
    public async Task WithoutOutlook_ThePressSaysSo_AndThePageDoesNotCarryTheStateOfOutlook()
    {
        using var scenario = await CorrespondencePagesScenario.CreateAsync();
        scenario.Outlook.ConnectFailure = FakeOutlookClient.Failure(OutlookFailureKind.NotResponding, "connect");
        using var window = await scenario.OpenWithMessageAsync();
        window.SelectedTab = CorrespondenceTab.Preview;

        // The preview does not need Outlook: the page has nothing to say until its own button is pressed.
        Assert.Null(window.Preview.Status.Lasting);
        Assert.Equal(PageStatusKind.Idle, window.Status.Current.Kind);

        await window.Preview.PreviewViaOutlookCommand.ExecuteAsync(null);

        var error = window.Status.Current;
        Assert.Equal(PageStatusKind.Error, error.Kind);
        Assert.StartsWith("Nie udało się pobrać podglądu z Outlooka: Nie można użyć Outlooka: Outlook nie odpowiada", error.Message, StringComparison.Ordinal);
        Assert.EndsWith("(NotResponding)", error.Message, StringComparison.Ordinal);
        Assert.Equal("Correspondence.Preview.Status.OutlookFailed [NotResponding]", error.Log.ToString());
    }
}
