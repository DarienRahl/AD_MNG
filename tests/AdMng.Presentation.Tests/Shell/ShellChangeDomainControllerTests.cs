using AdMng.Core.Audit;
using AdMng.Presentation.Connection;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Shell;

/// <summary>
/// The shell side of "Change domain controller": the command (menus, palette), and what happens after the switch – the
/// open tabs stay and reload from the new DC, the header shows it, windows of the previous DC say so.
/// </summary>
public class ShellChangeDomainControllerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Command_IsInThePalette_AndOpensTheDialogWhileConnected()
    {
        var harness = await B2Harness.CreateOnDomainControllerAsync();
        using var shell = harness.CreateShell();
        await shell.InitializeAsync();

        var descriptor = Assert.Single(shell.CommandDescriptors, d => d.Id == "session.changeDc");
        Assert.Equal(L.Commands.ChangeDomainController, descriptor.TitleKey);
        Assert.Equal(L.CommandCategories.Session, descriptor.CategoryKey);
        Assert.Same(shell.ChangeDomainControllerCommand, descriptor.Command);
        Assert.True(shell.ChangeDomainControllerCommand.CanExecute(null));

        await shell.ChangeDomainControllerCommand.ExecuteAsync(null);
        Assert.Contains("changeDc", harness.Dialogs.Shown);

        await harness.Sessions.DisconnectAsync();
        Assert.False(shell.ChangeDomainControllerCommand.CanExecute(null));
        Assert.Equal("Change Domain Controller...", harness.Localizer[L.Commands.ChangeDomainController]);
    }

    [Fact]
    public async Task AfterTheSwitch_TabsStay_AndReloadFromTheNewDc()
    {
        var harness = await B2Harness.CreateOnDomainControllerAsync();
        using var shell = harness.CreateShell();
        await shell.InitializeAsync();
        await shell.NavigateAsync(ListLocation.ForContainer(harness.Company.ItOu));
        await shell.OpenLocationInNewTabAsync(ListLocation.ForContainer(harness.Company.GroupsOu));
        var tabs = shell.ListDocuments.ToList();
        var oldDomainNode = shell.Tree.DomainNode;
        Assert.Equal(2, tabs.Count);
        Assert.Equal("dc01.contoso.test", shell.ServerText);

        var searchesBefore = harness.Session.Operations.Count(o => o.Kind == FakeOperationKind.Search);
        await harness.Sessions.SwitchDomainControllerAsync("dc02.contoso.test", null, Ct);
        await B2Harness.WaitUntilAsync(() => tabs.All(t => !t.IsLoading) && shell.Tree.DomainNode is { ChildrenLoaded: true });

        Assert.Equal("dc02.contoso.test", shell.ServerText);
        Assert.Equal(tabs, shell.ListDocuments);
        Assert.Same(tabs[1], shell.SelectedDocument);
        Assert.Equal(harness.Company.ItOu, tabs[0].Location!.BaseDn);
        Assert.Equal(harness.Company.GroupsOu, tabs[1].Location!.BaseDn);
        Assert.NotEmpty(tabs[0].Items);
        Assert.NotEmpty(tabs[1].Items);
        Assert.NotSame(oldDomainNode, shell.Tree.DomainNode);
        Assert.Equal(@"contoso.test – CONTOSO\Administrator – Active Directory Users and Computers", shell.WindowTitle);
        Assert.True(shell.IsConnected);
        Assert.True(shell.ChangeDomainControllerCommand.CanExecute(null));

        // Both tabs and the tree were read again (through the new session).
        Assert.Contains(tabs[0].Items, r => r.Dn == harness.Company.JanKowalski);
        Assert.True(harness.Session.Operations.Count(o => o.Kind == FakeOperationKind.Search) >= searchesBefore + 3);
    }

    [Fact]
    public async Task AfterAReconnectWithAnotherIdentity_TheShellStillStartsOver()
    {
        var harness = await B2Harness.CreateOnDomainControllerAsync();
        using var shell = harness.CreateShell();
        await shell.InitializeAsync();
        await shell.OpenLocationInNewTabAsync(ListLocation.ForContainer(harness.Company.GroupsOu));
        Assert.Equal(2, shell.ListDocuments.Count());

        // Not a DC switch: another identity on another DC replaces the session directly.
        harness.ConnectToServer = (_, server, _) => Task.FromResult<Core.Directory.IDirectorySession>(
            new FakeDomainControllerSession(harness.Session, server, i => i with { BoundIdentity = @"CONTOSO\jan.kowalski" }));
        await harness.Sessions.SwitchDomainControllerAsync("dc02.contoso.test", null, Ct);
        await B2Harness.WaitUntilAsync(() => shell.ListDocuments.Count() == 1 && !shell.ListDocuments.Single().IsLoading);

        Assert.Equal("contoso.test", shell.ListDocuments.Single().Title);
    }

    [Fact]
    public async Task WindowsOfThePreviousDc_SayWhereTheyBelong()
    {
        var harness = await B2Harness.CreateOnDomainControllerAsync();
        using var sheet = harness.Factory.CreatePropertySheet(harness.Company.JanKowalski);
        await sheet.InitializeAsync();

        await harness.Sessions.SwitchDomainControllerAsync("dc02.contoso.test", null, Ct);

        var expected = "This window belongs to the previous domain controller dc01.contoso.test. "
            + "The session is now connected to dc02.contoso.test – close this window and open it again.";
        Assert.True(sheet.IsSessionEnded);
        Assert.Equal(expected, sheet.SessionEndedMessage);
        Assert.Equal(expected, sheet.LoadError);
        Assert.False(sheet.ApplyCommand.CanExecute(null));

        // Reopened, the sheet works on the new DC.
        using var reopened = harness.Factory.CreatePropertySheet(harness.Company.JanKowalski);
        await reopened.InitializeAsync();
        Assert.False(reopened.IsSessionEnded);
        Assert.Null(reopened.LoadError);
    }

    [Fact]
    public async Task WindowsOfThePreviousDc_InPolish()
    {
        var harness = await B2Harness.CreateOnDomainControllerAsync(culture: System.Globalization.CultureInfo.GetCultureInfo("pl-PL"));
        using var sheet = harness.Factory.CreatePropertySheet(harness.Company.JanKowalski);
        await sheet.InitializeAsync();

        await harness.Sessions.SwitchDomainControllerAsync("dc02.contoso.test", null, Ct);

        Assert.Equal(
            "To okno należy do poprzedniego kontrolera domeny dc01.contoso.test. "
            + "Sesja jest teraz połączona z dc02.contoso.test – zamknij to okno i otwórz je ponownie.",
            sheet.SessionEndedMessage);
    }

    [Fact]
    public async Task SessionLog_ShowsTheSwitch_Localized()
    {
        var harness = await B2Harness.CreateOnDomainControllerAsync();
        harness.AuditLog.Record(new AuditEntry(
            harness.Time.GetUtcNow(), @"CONTOSO\Administrator", "dc02.contoso.test", "DC=contoso,DC=test",
            DomainControllerSwitch.SessionLogAction, null, "dc01.contoso.test", "dc02.contoso.test", true, null));

        using var log = harness.Factory.CreateSessionLog();

        var entry = Assert.Single(log.Entries);
        Assert.Equal("Change domain controller", entry.Action);
        Assert.Equal("dc01.contoso.test", entry.OldValue);
        Assert.Equal("dc02.contoso.test", entry.NewValue);
        Assert.Equal("dc02.contoso.test", entry.Server);
    }

    [Fact]
    public async Task SessionLog_CopiedEntryOfTheSwitch_NamesBothControllers()
    {
        var harness = await B2Harness.CreateOnDomainControllerAsync();
        harness.AuditLog.Record(new AuditEntry(
            harness.Time.GetUtcNow(), @"CONTOSO\Administrator", "dc02.contoso.test", "DC=contoso,DC=test",
            DomainControllerSwitch.SessionLogAction, null, "dc01.contoso.test", "dc02.contoso.test", true, null));
        using var log = harness.Factory.CreateSessionLog();
        log.SelectedEntry = Assert.Single(log.Entries);

        log.CopyEntryCommand.Execute(null);

        // The entry has no attribute, but its values say where the session came from and went to (as the grid shows).
        var lines = harness.Clipboard.Text!.Split(Environment.NewLine);
        Assert.Contains("Action: Change domain controller", lines);
        Assert.Contains("Before: dc01.contoso.test", lines);
        Assert.Contains("After: dc02.contoso.test", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("Attribute:", StringComparison.Ordinal));
    }
}
