using System.Globalization;
using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Correspondence.Fields;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Correspondence;

/// <summary>
/// The Korespondencja window itself (design note 6.4): what it does with each request, the settings and the journal at
/// open, the Outlook connection in the background, help, closing (saving the options and the last draft), the end of the
/// session and disposal. The pages are tested on their own.
/// </summary>
public sealed class CorrespondenceWindowTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public async Task Open_WithoutObjects_StartsOnTheRecipients_AndConnectsOutlookInTheBackground()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);

        using var window = await scenario.OpenAsync();

        Assert.Equal($"Korespondencja – {FakeDirectoryBuilder.DefaultDomain}", window.WindowTitle);
        Assert.Equal(CorrespondenceTab.Recipients, window.SelectedTab);
        Assert.False(window.IsMailboxesTabVisible);
        Assert.False(window.IsBusy);
        Assert.Null(window.Error);
        Assert.Equal(string.Empty, window.StatusText);
        Assert.Equal(OutlookState.Connected, window.Outlook.State);
        Assert.Equal("Outlook: połączono (2 konta)", window.Outlook.StateText);
        Assert.Equal(1, scenario.Outlook.Connections);
        Assert.True(scenario.Journal.IsLoaded);
        Assert.Empty(scenario.Harness.Dialogs.Shown);
        Assert.Empty(scenario.Harness.Dialogs.Errors);
        Assert.Same(window.Initialization, window.InitializeAsync());     // once
    }

    [Fact]
    public async Task Open_WithSelectedUsers_StaysOnTheRecipients()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync();

        using var window = await scenario.OpenAsync(new CorrespondenceRequest([scenario.Company.JanKowalski, "  ", scenario.Company.PawelLewandowski]));

        Assert.Equal(CorrespondenceStart.Recipients, window.Request.Start);
        Assert.Equal(CorrespondenceTab.Recipients, window.SelectedTab);
        Assert.False(window.IsMailboxesTabVisible);
        Assert.Null(window.Error);
        Assert.Equal(OutlookState.Connected, window.Outlook.State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Open_ForMailboxOwners_ShowsTheMailboxesTab(bool withSelection)
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync();
        IReadOnlyList<string> dns = withSelection ? [scenario.Company.ItOu] : [];

        using var window = await scenario.OpenAsync(new CorrespondenceRequest(dns, CorrespondenceStart.MailboxOwners));

        Assert.True(window.IsMailboxesTabVisible);
        Assert.Equal(CorrespondenceTab.Mailboxes, window.SelectedTab);
        Assert.Null(window.Error);
    }

    [Fact]
    public async Task ShowMailboxes_ShowsAndSelectsTheTab()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        var changed = new List<string?>();
        window.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        window.ShowMailboxesCommand.Execute(null);

        Assert.True(window.IsMailboxesTabVisible);
        Assert.Equal(CorrespondenceTab.Mailboxes, window.SelectedTab);
        Assert.Contains(nameof(CorrespondenceViewModel.IsMailboxesTabVisible), changed);
    }

    [Fact]
    public async Task Open_TakesTheAccountOfTheSettings()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync();
        await scenario.Harness.Settings.UpdateAsync(s =>
        {
            s.Correspondence.AccountAddress = "sekretariat.lodz@contoso.test";
            s.Correspondence.OnBehalfOf = "kancelaria@contoso.test";
        }, TestContext.Current.CancellationToken);

        using var window = await scenario.OpenAsync();

        Assert.Equal("sekretariat.lodz@contoso.test", window.Outlook.SelectedAccount!.SmtpAddress);
        Assert.Equal("kancelaria@contoso.test", window.Outlook.OnBehalfOf);
    }

    [Fact]
    public async Task Open_PrunesTheJournalToTheRetentionOfTheSettings()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync();
        await scenario.Journal.LoadAsync(TimeSpan.FromDays(3650), TestContext.Current.CancellationToken);
        await scenario.Journal.AppendAsync("jan.kowalski@contoso.test", "Dawno temu", TestContext.Current.CancellationToken);
        scenario.Harness.Time.Advance(TimeSpan.FromDays(40));
        await scenario.Journal.AppendAsync("pawel.lewandowski@contoso.test", "Niedawno", TestContext.Current.CancellationToken);
        await scenario.Harness.Settings.UpdateAsync(s => s.Correspondence.JournalRetentionDays = 30, TestContext.Current.CancellationToken);

        using var window = await scenario.OpenAsync();

        Assert.Equal(1, scenario.Journal.Count);
        Assert.Null(scenario.Journal.Find("jan.kowalski@contoso.test"));
        Assert.NotNull(scenario.Journal.Find("pawel.lewandowski@contoso.test"));
    }

    [Fact]
    public async Task Open_WithAnUnreadableJournalKey_SaysSoInTheStatus()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        await scenario.Journal.LoadAsync(TimeSpan.FromDays(365), TestContext.Current.CancellationToken);
        await scenario.Journal.AppendAsync("jan.kowalski@contoso.test", "Temat", TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(scenario.Journal.KeyPath, [0, 1, 2], TestContext.Current.CancellationToken);

        using var window = await scenario.OpenAsync();

        Assert.StartsWith("Klucza dziennika „już wysłane” nie udało się odczytać", window.StatusText, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(scenario.Journal.Folder, "journal-"), window.StatusText, StringComparison.Ordinal);
        Assert.Null(window.Error);
        Assert.Equal(0, scenario.Journal.Count);
    }

    [Fact]
    public async Task Open_WithoutAKeyProtector_ShowsThatTheJournalIsUnavailable_AndStillWorks()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        scenario.Protector.Failure = new PlatformNotSupportedException("brak DPAPI");

        using var window = await scenario.OpenAsync(new CorrespondenceRequest([scenario.Company.JanKowalski]));

        Assert.Equal(
            "Dziennika „już wysłane” nie można użyć (na tym komputerze nie można zabezpieczyć jego klucza) – wysłane adresy nie będą pomijane przy kolejnych wysyłkach.",
            window.Error);
        Assert.False(scenario.Journal.IsLoaded);
        Assert.Equal(OutlookState.Connected, window.Outlook.State);
        Assert.False(window.IsBusy);
    }

    [Fact]
    public async Task Open_WithoutClassicOutlook_ShowsTheState_WithoutAModalError()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        scenario.Outlook.Availability = OutlookAvailability.NotInstalled;

        using var window = await scenario.OpenAsync();

        Assert.Equal(OutlookState.NotInstalled, window.Outlook.State);
        Assert.Equal("Outlook: brak klasycznego Outlooka", window.Outlook.StateText);
        Assert.Equal(0, scenario.Outlook.Connections);
        Assert.Null(window.Error);
        Assert.Empty(scenario.Harness.Dialogs.Errors);
        Assert.Empty(scenario.Harness.Dialogs.Shown);
    }

    [Fact]
    public async Task Open_OnAHostWithoutOutlook_Works()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(withOutlook: false);

        using var window = await scenario.OpenAsync(new CorrespondenceRequest([scenario.Company.JanKowalski]));

        Assert.Equal(OutlookState.NotSupported, window.Outlook.State);
        Assert.Null(window.Error);
    }

    [Fact]
    public async Task Help_ShowsTheHelpOfTheWindow()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        using var window = await scenario.OpenAsync();

        await window.HelpCommand.ExecuteAsync(null);

        var (title, text) = Assert.Single(scenario.Dialogs.Reports);
        Assert.Equal("Pomoc – Korespondencja", title);
        Assert.StartsWith("KOLEJNOŚĆ PRACY", text, StringComparison.Ordinal);
        foreach (var field in BuiltInFields.All)
        {
            Assert.Contains("{{" + field + "}}", text, StringComparison.Ordinal);
        }

        Assert.Contains("„Sprawdź Outlooka”", text, StringComparison.Ordinal);
        Assert.Contains("„już wysłane”", text, StringComparison.Ordinal);
        Assert.Contains("{{Pole|tekst}}", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Close_SavesTheOptionsAndTheLastDraft()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        window.Outlook.SelectedAccount = window.Outlook.Accounts[1];
        window.Outlook.OnBehalfOf = " sekretariat@contoso.test ";
        window.Message.Subject = "Zaproszenie – {{Miejscowość}}";
        window.Message.BodyHtml = "<p>{{zwrot}},</p><p>zapraszamy na spotkanie w Łodzi.</p>";
        window.Message.Salutation = "Szanowna Pani";
        window.Message.UseVoting = true;
        window.Message.VotingOptions = "Tak;Nie";
        window.Message.GroupByAddress = true;
        window.Message.ListItemTemplate = "{{Skrzynka}}";
        window.Message.CustomFields.Add(new CustomFieldViewModel("Termin", "15 listopada"));

        Assert.True(await window.CanCloseAsync());

        var settings = scenario.Harness.Settings.Current.Correspondence;
        Assert.Equal("sekretariat.lodz@contoso.test", settings.AccountAddress);
        Assert.Equal("sekretariat@contoso.test", settings.OnBehalfOf);
        var draft = scenario.Templates.LoadLastDraft()!;
        Assert.Equal("Zaproszenie – {{Miejscowość}}", draft.Subject);
        Assert.Equal("<p>{{zwrot}},</p><p>zapraszamy na spotkanie w Łodzi.</p>", draft.BodyHtml);
        Assert.Equal("Szanowna Pani", draft.Salutation);
        Assert.Equal([new CustomFieldDefinition("Termin", "15 listopada")], draft.CustomFields);
        Assert.Equal("{{Skrzynka}}", draft.ListItemTemplate);
        Assert.Equal((true, "Tak;Nie", true), (draft.VotingEnabled, draft.VotingOptions, draft.GroupByAddress));
        Assert.Empty(scenario.Harness.Dialogs.Shown);    // nothing was being sent: no question
        Assert.Empty(scenario.Templates.Saved);
    }

    [Fact]
    public async Task Open_RestoresTheLastDraft_WithItsImagesInTheStore()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        var image = CorrespondenceShellScenario.Image();
        await scenario.Templates.SaveLastDraftAsync(new CorrespondenceTemplate
        {
            Subject = "Spotkanie w Łodzi – {{Miejscowość}}",
            BodyHtml = $"<p>{{{{zwrot}}}},</p><p><img src=\"cid:{image.ContentId}\" alt=\"herb\"></p>",
            Salutation = "Szanowni Państwo Radni",
            CustomFields = [new CustomFieldDefinition("Termin", "15 listopada")],
            ListItemTemplate = "{{Skrzynka}}",
            VotingEnabled = true,
            VotingOptions = "Będę;Nie będę",
            GroupByAddress = true,
        }, [image]);

        using var window = await scenario.OpenAsync();

        var message = window.Message;
        Assert.Equal("Spotkanie w Łodzi – {{Miejscowość}}", message.Subject);
        Assert.Equal($"<p>{{{{zwrot}}}},</p><p><img src=\"cid:{image.ContentId}\" alt=\"herb\"></p>", message.BodyHtml);
        Assert.Equal("Szanowni Państwo Radni", message.Salutation);
        Assert.Equal([("Termin", "15 listopada")], message.CustomFields.Select(f => (f.Name, f.Value)));
        Assert.Equal("{{Skrzynka}}", message.ListItemTemplate);
        Assert.Equal((true, "Będę;Nie będę", true), (message.UseVoting, message.VotingOptions, message.GroupByAddress));
        var stored = Assert.Single(scenario.Templates.LoadLastDraft()!.Images);
        Assert.Equal(image.ContentId, stored.ContentId);
        Assert.NotNull(await scenario.Images.LoadAsync(stored, TestContext.Current.CancellationToken, CorrespondenceLimits.MaxImageBytes));
    }

    [Fact]
    public async Task Open_KeepsAMessageThePageAlreadyHolds()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync();
        await scenario.Templates.SaveLastDraftAsync(new CorrespondenceTemplate { Subject = "Stary szkic", BodyHtml = "<p>Stara treść</p>" });
        var window = scenario.Factory.Create(new CorrespondenceRequest([]));
        using var disposeWindow = window;
        window.Message.Subject = "Nowa wiadomość";

        await window.InitializeAsync();

        Assert.Equal("Nowa wiadomość", window.Message.Subject);
        Assert.True(window.Message.IsBodyEmpty);
    }

    [Fact]
    public async Task SessionEnd_DisablesTheDirectory_AndOutlookStays()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        Assert.True(window.Recipients.CanUseDirectory);

        await scenario.Harness.Sessions.DisconnectAsync();

        Assert.True(window.IsSessionEnded);
        Assert.False(window.Recipients.CanUseDirectory);
        Assert.Equal(OutlookState.Connected, window.Outlook.State);
        Assert.False(Assert.Single(scenario.Outlook.Sessions).IsDisposed);
    }

    [Fact]
    public async Task Dispose_ReleasesTheOutlookSession()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync();
        var window = await scenario.OpenAsync();
        var session = Assert.Single(scenario.Outlook.Sessions);

        window.Dispose();

        await B2Harness.WaitUntilAsync(() => session.IsDisposed);
        Assert.Null(await window.Outlook.EnsureConnectedAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AWindowOpenedWhileDisconnected_HasNoDomainInItsTitle()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync(Polish);
        await scenario.Harness.Sessions.DisconnectAsync();

        using var window = await scenario.OpenAsync();

        Assert.Equal("Korespondencja", window.WindowTitle);
        Assert.Equal(OutlookState.Connected, window.Outlook.State);
    }

    [Fact]
    public async Task LanguageChange_RefreshesTheTitleAndTheOutlookState()
    {
        using var scenario = await CorrespondenceShellScenario.CreateAsync();
        using var window = await scenario.OpenAsync();
        Assert.Equal("Outlook: connected (2 accounts)", window.Outlook.StateText);
        var changed = new List<string?>();
        window.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        scenario.Harness.Localizer.SetCulture(Polish);

        Assert.Contains(nameof(CorrespondenceViewModel.WindowTitle), changed);
        Assert.StartsWith("Korespondencja – ", window.WindowTitle, StringComparison.Ordinal);
        Assert.Equal("Outlook: połączono (2 konta)", window.Outlook.StateText);
    }

    [Fact]
    public void Request_ToStringHasNoDistinguishedNames()
    {
        var request = new CorrespondenceRequest(["CN=Jan Kowalski,OU=IT,DC=contoso,DC=test"], CorrespondenceStart.MailboxOwners);

        Assert.Equal("CorrespondenceRequest MailboxOwners (1)", request.ToString());
    }
}
