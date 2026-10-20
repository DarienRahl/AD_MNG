using System.Globalization;
using System.Net;
using AdMng.Core.Directory;
using AdMng.Presentation.Connection;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.Connection;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Dialogs;

/// <summary>
/// "Change domain controller" dialog: the DC list, free text, "any writable DC in my site", the switch through the
/// session manager, errors, cancellation, the session log and the optional profile update.
/// </summary>
public class ChangeDomainControllerViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Connected to dc01 (Warszawa, PDC); the directory knows dc02 (Kraków) and an RODC in Gdańsk.</summary>
    private static async Task<B2Harness> HarnessAsync(CultureInfo? culture = null)
    {
        var harness = await B2Harness.CreateOnDomainControllerAsync("dc01.contoso.test", culture);
        DomainControllerSeed.Add(harness.Session, "dc01.contoso.test", "Warszawa", pdc: true);
        DomainControllerSeed.Add(harness.Session, "dc02.contoso.test", "Kraków", globalCatalog: false);
        DomainControllerSeed.Add(harness.Session, "rodc-gdańsk.contoso.test", "Gdańsk", readOnly: true);
        return harness;
    }

    private static async Task<ChangeDomainControllerViewModel> OpenAsync(B2Harness harness, params DomainControllerInfo[] located)
    {
        var dialog = new ChangeDomainControllerViewModel(harness.Services, harness.AuditLog, located.Length == 0 ? null : new LocatorConnector(located));
        await dialog.InitializeAsync();
        return dialog;
    }

    private static string Text(B2Harness harness, string key) => harness.Localizer[key];

    [Fact]
    public async Task Initialize_ListsTheDomainControllers_AndPreselectsTheCurrentOne()
    {
        var harness = await HarnessAsync();

        using var dialog = await OpenAsync(harness);

        Assert.Equal("Change Domain Controller", dialog.WindowTitle);
        Assert.Equal("Current domain controller: dc01.contoso.test", dialog.CurrentText);
        Assert.Equal("Domain: contoso.test", dialog.DomainText);
        Assert.False(dialog.IsLoading);
        Assert.Null(dialog.ErrorText);
        Assert.Null(dialog.ListNote);
        Assert.Equal(["dc01.contoso.test", "dc02.contoso.test", "rodc-gdańsk.contoso.test"], dialog.DomainControllers.Select(d => d.HostName));
        var current = dialog.DomainControllers[0];
        Assert.Same(current, dialog.SelectedDomainController);
        Assert.Equal("dc01.contoso.test", dialog.ServerText);
        Assert.Equal("Current, PDC emulator, Global catalog", current.RolesText);
        Assert.Equal("Warszawa", current.SiteText);
        Assert.Equal("Read-only, Global catalog", dialog.DomainControllers[2].RolesText);
        Assert.Equal(string.Empty, dialog.DomainControllers[1].RolesText);
        Assert.Equal("dc02.contoso.test, Kraków", dialog.DomainControllers[1].AutomationName);

        // Nothing to do while the current DC is selected.
        Assert.False(dialog.ChangeCommand.CanExecute(null));
        Assert.Equal(Text(harness, L.ChangeDc.IsCurrent), dialog.TargetHint);
    }

    [Fact]
    public async Task Change_ToASelectedDc_SwitchesTheSession_LogsIt_AndCloses()
    {
        var harness = await HarnessAsync();
        using var dialog = await OpenAsync(harness);
        bool? closed = null;
        dialog.CloseRequested += (_, result) => closed = result;

        dialog.SelectedDomainController = dialog.DomainControllers.Single(d => d.HostName == "dc02.contoso.test");
        Assert.Equal("dc02.contoso.test", dialog.ServerText);
        Assert.Null(dialog.TargetHint);
        Assert.True(dialog.ChangeCommand.CanExecute(null));
        await dialog.ChangeCommand.ExecuteAsync(null);

        Assert.Equal("dc02.contoso.test", harness.Sessions.Current!.Info.Server);
        Assert.Equal("dc02.contoso.test", dialog.Result);
        Assert.True(closed);
        Assert.Null(dialog.ErrorText);
        var entry = Assert.Single(harness.AuditLog.Entries);
        Assert.Equal(DomainControllerSwitch.SessionLogAction, entry.Action);
        Assert.Equal(@"CONTOSO\Administrator", entry.Operator);
        Assert.Equal("dc02.contoso.test", entry.Server);
        Assert.Equal("DC=contoso,DC=test", entry.DistinguishedName);
        Assert.Equal("dc01.contoso.test", entry.OldValue);
        Assert.Equal("dc02.contoso.test", entry.NewValue);
        Assert.True(entry.Succeeded);
        Assert.Contains(harness.Notifications.Items, n => n.Severity == NotificationSeverity.Success && n.Message == "Connected to domain controller dc02.contoso.test.");
        Assert.Equal(0, harness.Settings.SaveCount);
    }

    [Fact]
    public async Task Change_ToATypedName_NotInTheList()
    {
        var harness = await HarnessAsync();
        using var dialog = await OpenAsync(harness);

        dialog.ServerText = " dc05.contoso.test. ";

        Assert.Null(dialog.SelectedDomainController);
        Assert.Equal("dc05.contoso.test", dialog.TargetServer);
        await dialog.ChangeCommand.ExecuteAsync(null);
        Assert.Equal("dc05.contoso.test", harness.Sessions.Current!.Info.Server);
    }

    [Fact]
    public async Task TypedName_SelectsTheMatchingListEntry_AndWarnsAboutAReadOnlyDc()
    {
        var harness = await HarnessAsync();
        using var dialog = await OpenAsync(harness);

        dialog.ServerText = "RODC-GDAŃSK.contoso.test";

        Assert.Equal("rodc-gdańsk.contoso.test", dialog.SelectedDomainController?.HostName);
        Assert.Equal("RODC-GDAŃSK.contoso.test", dialog.ServerText);
        Assert.Equal(
            "RODC-GDAŃSK.contoso.test is a read-only domain controller (RODC): you can browse, but changes will be refused.",
            dialog.TargetHint);
        Assert.True(dialog.ChangeCommand.CanExecute(null));

        dialog.ServerText = "dc01";
        Assert.Equal("dc01.contoso.test", dialog.SelectedDomainController?.HostName);
        Assert.False(dialog.ChangeCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("dc02.contoso.test:636")]
    [InlineData("ldap://dc02.contoso.test")]
    [InlineData("dc 02")]
    public async Task InvalidName_DisablesChange_WithAHint(string text)
    {
        var harness = await HarnessAsync();
        using var dialog = await OpenAsync(harness);

        dialog.ServerText = text;

        Assert.False(dialog.ChangeCommand.CanExecute(null));
        Assert.Null(dialog.TargetServer);
        Assert.Equal(Text(harness, L.ChangeDc.InvalidName), dialog.TargetHint);
        await dialog.ChangeCommand.ExecuteAsync(null);
        Assert.Equal("dc01.contoso.test", harness.Sessions.Current!.Info.Server);
    }

    [Fact]
    public async Task EmptyName_DisablesChange_WithoutAHint()
    {
        var harness = await HarnessAsync();
        using var dialog = await OpenAsync(harness);

        dialog.ServerText = "  ";

        Assert.Null(dialog.SelectedDomainController);
        Assert.False(dialog.ChangeCommand.CanExecute(null));
        Assert.Null(dialog.TargetHint);
    }

    [Fact]
    public async Task Failure_ShowsTheError_KeepsTheSession_AndLogsTheAttempt()
    {
        var harness = await HarnessAsync();
        var previous = harness.Sessions.Current!;
        harness.ConnectToServer = (_, _, _) => throw new DirectoryException(DirectoryErrorKind.ServerUnavailable, "The server is not operational.");
        using var dialog = await OpenAsync(harness);
        bool? closed = null;
        dialog.CloseRequested += (_, result) => closed = result;

        dialog.ServerText = "dc02.contoso.test";
        await dialog.ChangeCommand.ExecuteAsync(null);

        Assert.Same(previous, harness.Sessions.Current);
        Assert.Null(closed);
        Assert.Null(dialog.Result);
        Assert.False(dialog.IsBusy);
        var expected = harness.Errors.TranslateConnectError(new DirectoryException(DirectoryErrorKind.ServerUnavailable, "x"))
            + Environment.NewLine + "The session stays connected to dc01.contoso.test.";
        Assert.Equal(expected, dialog.ErrorText);
        var entry = Assert.Single(harness.AuditLog.Entries);
        Assert.False(entry.Succeeded);
        Assert.Equal("dc01.contoso.test", entry.Server);
        Assert.Equal("dc01.contoso.test", entry.OldValue);
        Assert.Equal("dc02.contoso.test", entry.NewValue);
        Assert.Equal(harness.Errors.TranslateConnectError(new DirectoryException(DirectoryErrorKind.ServerUnavailable, "x")), entry.Error);

        // The operator can try another DC right away.
        harness.ConnectToServer = (_, server, _) => Task.FromResult<IDirectorySession>(new FakeDomainControllerSession(harness.Session, server));
        dialog.ServerText = "dc05.contoso.test";
        await dialog.ChangeCommand.ExecuteAsync(null);
        Assert.Equal("dc05.contoso.test", harness.Sessions.Current!.Info.Server);
        Assert.True(closed);
    }

    [Fact]
    public async Task DcOfAnotherDomain_IsExplained()
    {
        var harness = await HarnessAsync();
        harness.ConnectToServer = (_, server, _) => Task.FromResult<IDirectorySession>(new FakeDomainControllerSession(harness.Session, server, i => i with
        {
            DefaultNamingContext = "DC=oddział,DC=contoso,DC=test",
            DomainDnsName = "oddział.contoso.test",
        }));
        using var dialog = await OpenAsync(harness);

        dialog.ServerText = "dc01.oddział.contoso.test";
        await dialog.ChangeCommand.ExecuteAsync(null);

        Assert.Equal(
            "dc01.oddział.contoso.test is a domain controller of oddział.contoso.test, not of contoso.test." + Environment.NewLine
            + "The session stays connected to dc01.contoso.test.",
            dialog.ErrorText);
        Assert.Equal("dc01.contoso.test", harness.Sessions.Current!.Info.Server);
    }

    [Fact]
    public async Task AliasOfTheCurrentDc_SaysAlreadyConnected()
    {
        var harness = await HarnessAsync();
        harness.ConnectToServer = (_, _, _) => Task.FromResult<IDirectorySession>(new FakeDomainControllerSession(harness.Session, "dc01.contoso.test"));
        using var dialog = await OpenAsync(harness);

        dialog.ServerText = "10.0.0.11";
        await dialog.ChangeCommand.ExecuteAsync(null);

        Assert.StartsWith("Already connected to dc01.contoso.test.", dialog.ErrorText, StringComparison.Ordinal);
        Assert.Empty(harness.AuditLog.Entries);
    }

    [Fact]
    public async Task AnyWritableDcInMySite_PicksAWritableDcOfTheClientSite()
    {
        var harness = await HarnessAsync();
        using var dialog = await OpenAsync(
            harness,
            new DomainControllerInfo("rodc-krk.contoso.test", "Kraków", true, true, true),
            new DomainControllerInfo("dc02.contoso.test", "Kraków", false, false, true),
            new DomainControllerInfo("dc01.contoso.test", "Warszawa", true, false, false));

        Assert.True(dialog.CanUseAnyWritableInSite);
        Assert.Equal("Connects to dc02.contoso.test in your site Kraków.", dialog.AnyWritableText);
        Assert.Equal("Yes", dialog.DomainControllers.Single(d => d.HostName == "dc02.contoso.test").InClientSiteText);
        Assert.Equal(string.Empty, dialog.DomainControllers.Single(d => d.HostName == "dc01.contoso.test").InClientSiteText);
        Assert.Equal("dc02.contoso.test, Kraków, Your site", dialog.DomainControllers.Single(d => d.HostName == "dc02.contoso.test").AutomationName);
        Assert.Equal(
            "rodc-krk.contoso.test, Kraków, Your site, Read-only, Global catalog",
            dialog.DomainControllers.Single(d => d.HostName == "rodc-krk.contoso.test").AutomationName);

        dialog.UseAnyWritableInSite = true;

        Assert.False(dialog.UseSpecificDomainController);
        Assert.Equal("dc02.contoso.test", dialog.TargetServer);
        Assert.True(dialog.ChangeCommand.CanExecute(null));
        await dialog.ChangeCommand.ExecuteAsync(null);
        Assert.Equal("dc02.contoso.test", harness.Sessions.Current!.Info.Server);

        // Picking an entry of the list goes back to "this domain controller".
        dialog.UseAnyWritableInSite = true;
        dialog.SelectedDomainController = dialog.DomainControllers[0];
        Assert.True(dialog.UseSpecificDomainController);
    }

    [Fact]
    public async Task AnyWritableDcInMySite_WhenTheCurrentDcQualifies_HasNothingToDo()
    {
        var harness = await HarnessAsync();
        using var dialog = await OpenAsync(harness, new DomainControllerInfo("dc01.contoso.test", "Warszawa", true, false, true));

        dialog.UseAnyWritableInSite = true;

        Assert.Equal("The current domain controller dc01.contoso.test is already a writable domain controller in your site.", dialog.AnyWritableText);
        Assert.False(dialog.ChangeCommand.CanExecute(null));
    }

    [Fact]
    public async Task AnyWritableDcInMySite_WithoutTheLocator_IsUnavailable()
    {
        var harness = await HarnessAsync();
        using var dialog = await OpenAsync(harness);

        Assert.False(dialog.CanUseAnyWritableInSite);
        Assert.Equal(Text(harness, L.ChangeDc.ClientSiteUnknown), dialog.AnyWritableText);
        dialog.UseAnyWritableInSite = true;
        Assert.False(dialog.ChangeCommand.CanExecute(null));
    }

    [Fact]
    public async Task AnyWritableDcInMySite_WithOnlyAnRodcInTheSite_IsUnavailable()
    {
        var harness = await HarnessAsync();
        using var dialog = await OpenAsync(harness, new DomainControllerInfo("rodc-gdańsk.contoso.test", "Gdańsk", true, true, true));

        Assert.False(dialog.CanUseAnyWritableInSite);
        Assert.Equal("No writable domain controller of your site Gdańsk is known.", dialog.AnyWritableText);
    }

    [Fact]
    public async Task AnyWritableDcInMySite_WhileTheLocatorAnswers_SaysLoading_NotSiteUnknown()
    {
        var harness = await HarnessAsync();
        var locator = new HeldLocatorConnector([new DomainControllerInfo("dc02.contoso.test", "Kraków", true, false, true)]);
        using var dialog = new ChangeDomainControllerViewModel(harness.Services, harness.AuditLog, locator);
        var changed = new List<string?>();
        dialog.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        var initialization = dialog.InitializeAsync();

        // DsGetDcName may take seconds: until it answered the client's site is not "unknown".
        Assert.True(dialog.IsLoading);
        Assert.False(dialog.CanUseAnyWritableInSite);
        Assert.Equal("Loading…", dialog.AnyWritableText);

        locator.Release();
        await initialization;

        Assert.False(dialog.IsLoading);
        Assert.Equal("Connects to dc02.contoso.test in your site Kraków.", dialog.AnyWritableText);
        Assert.Contains(nameof(dialog.AnyWritableText), changed);
    }

    [Fact]
    public async Task Profile_IsUpdatedOnlyWhenAsked()
    {
        var harness = await HarnessAsync();
        await harness.Settings.UpdateAsync(s =>
        {
            s.ConnectionProfiles.Add(new ConnectionProfile { Name = "Biuro Kraków", Domain = "contoso.test", Server = "dc01.contoso.test" });
            s.ConnectionProfiles.Add(new ConnectionProfile { Name = "Inny", Domain = "contoso.test", Authentication = AuthenticationMode.Credentials, UserName = "jan" });
            s.LastProfileName = "Biuro Kraków";
        }, Ct);

        using (var dialog = await OpenAsync(harness))
        {
            Assert.True(dialog.CanUpdateProfile);
            Assert.False(dialog.UpdateProfile);
            Assert.Equal("Remember the choice in the connection profile 'Biuro Kraków'", dialog.UpdateProfileText);
            dialog.ServerText = "dc02.contoso.test";
            await dialog.ChangeCommand.ExecuteAsync(null);
        }

        Assert.Equal("dc01.contoso.test", harness.Settings.Current.ConnectionProfiles[0].Server);

        using (var dialog = await OpenAsync(harness))
        {
            dialog.ServerText = "dc05.contoso.test";
            dialog.UpdateProfile = true;
            await dialog.ChangeCommand.ExecuteAsync(null);
        }

        Assert.Equal("dc05.contoso.test", harness.Settings.Current.ConnectionProfiles[0].Server);
        Assert.Null(harness.Settings.Current.ConnectionProfiles[1].Server);

        // "Any writable DC in my site" is remembered as automatic location.
        using (var dialog = await OpenAsync(harness, new DomainControllerInfo("dc02.contoso.test", "Kraków", true, false, true)))
        {
            dialog.UseAnyWritableInSite = true;
            dialog.UpdateProfile = true;
            await dialog.ChangeCommand.ExecuteAsync(null);
        }

        Assert.Equal("dc02.contoso.test", harness.Sessions.Current!.Info.Server);
        Assert.Null(harness.Settings.Current.ConnectionProfiles[0].Server);
        Assert.Equal("Biuro Kraków", harness.Settings.Current.LastProfileName);
    }

    [Fact]
    public async Task Profile_OfAnotherConnection_IsNotOffered()
    {
        var harness = await HarnessAsync();
        await harness.Settings.UpdateAsync(s =>
        {
            s.ConnectionProfiles.Add(new ConnectionProfile { Name = "Fabrikam", Domain = "fabrikam.test" });
            s.ConnectionProfiles.Add(new ConnectionProfile { Name = "Hasło", Domain = "contoso.test", Authentication = AuthenticationMode.Credentials, UserName = "jan" });
            s.LastProfileName = "Fabrikam";
        }, Ct);

        using (var dialog = await OpenAsync(harness))
        {
            Assert.False(dialog.CanUpdateProfile);
        }

        await harness.Settings.UpdateAsync(s => s.LastProfileName = "Hasło", Ct);
        using (var dialog = await OpenAsync(harness))
        {
            Assert.False(dialog.CanUpdateProfile);
            dialog.UpdateProfile = true;
            dialog.ServerText = "dc02.contoso.test";
            await dialog.ChangeCommand.ExecuteAsync(null);
        }

        Assert.All(harness.Settings.Current.ConnectionProfiles, p => Assert.Null(p.Server));
    }

    [Fact]
    public async Task Profile_NamingOnlyADc_KeepsItsDomain_WhenAnyWritableDcIsRemembered()
    {
        var harness = await HarnessAsync();
        await harness.Settings.UpdateAsync(s =>
        {
            s.ConnectionProfiles.Add(new ConnectionProfile { Name = "Tylko DC", Server = "dc01.contoso.test" });
            s.LastProfileName = "Tylko DC";
        }, Ct);

        // A specific DC: the profile still needs no domain.
        using (var dialog = await OpenAsync(harness))
        {
            Assert.True(dialog.CanUpdateProfile);
            dialog.ServerText = "dc05.contoso.test";
            dialog.UpdateProfile = true;
            await dialog.ChangeCommand.ExecuteAsync(null);
        }

        var profile = Assert.Single(harness.Settings.Current.ConnectionProfiles);
        Assert.Equal("dc05.contoso.test", profile.Server);
        Assert.Null(profile.Domain);

        // Automatic location: without the domain the profile would locate a DC of this computer's domain instead.
        using (var dialog = await OpenAsync(harness, new DomainControllerInfo("dc02.contoso.test", "Kraków", true, false, true)))
        {
            Assert.True(dialog.CanUpdateProfile);
            dialog.UseAnyWritableInSite = true;
            dialog.UpdateProfile = true;
            await dialog.ChangeCommand.ExecuteAsync(null);
        }

        profile = Assert.Single(harness.Settings.Current.ConnectionProfiles);
        Assert.Equal("dc02.contoso.test", harness.Sessions.Current!.Info.Server);
        Assert.Null(profile.Server);
        Assert.Equal("contoso.test", profile.Domain);
    }

    [Fact]
    public async Task RunningOperations_AreConfirmed_AndCancelledOnlyAfterTheNewDcAnswered()
    {
        var harness = await HarnessAsync();
        var run = harness.Activities.Start("Zmiana działu", 3, async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null!; // Never reached: the operation ends only by cancellation.
        });
        var cancelledWhenConnecting = new List<bool>();
        var inner = harness.ConnectToServer;
        harness.ConnectToServer = (current, server, ct) =>
        {
            cancelledWhenConnecting.Add(run.Activity.IsCancelling || !run.Activity.IsRunning);
            return inner(current, server, ct);
        };
        using var dialog = await OpenAsync(harness);
        dialog.ServerText = "dc02.contoso.test";

        // Declined: nothing happens.
        harness.Dialogs.ConfirmAnswers.Enqueue(false);
        await dialog.ChangeCommand.ExecuteAsync(null);
        Assert.Contains($"confirm:{Text(harness, L.ChangeDc.RunningOperations)}", harness.Dialogs.Shown);
        Assert.Empty(cancelledWhenConnecting);
        Assert.True(run.Activity.IsRunning);
        Assert.Equal("dc01.contoso.test", harness.Sessions.Current!.Info.Server);

        // Confirmed: the DC is reached first, then the operation is cancelled, then the session is replaced.
        harness.Dialogs.ConfirmAnswers.Enqueue(true);
        await dialog.ChangeCommand.ExecuteAsync(null);
        Assert.Equal([false], cancelledWhenConnecting);
        Assert.False(run.Activity.IsRunning);
        Assert.False(harness.Activities.HasRunning);
        Assert.Equal("dc02.contoso.test", harness.Sessions.Current!.Info.Server);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Execution);
    }

    [Fact]
    public async Task Cancel_WhileConnecting_KeepsTheSession_AndTheDialogOpen()
    {
        var harness = await HarnessAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ConnectToServer = async (_, server, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new FakeDomainControllerSession(harness.Session, server);
        };
        using var dialog = await OpenAsync(harness);
        bool? closed = null;
        dialog.CloseRequested += (_, result) => closed = result;
        dialog.ServerText = "dc02.contoso.test";

        var change = dialog.ChangeCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(Ct);
        Assert.True(dialog.IsBusy);
        Assert.Equal("Connecting to dc02.contoso.test…", dialog.StatusText);
        Assert.False(dialog.ChangeCommand.CanExecute(null));
        Assert.False(dialog.RefreshCommand.CanExecute(null));

        dialog.CancelCommand.Execute(null);
        await change;

        Assert.False(dialog.IsBusy);
        Assert.Null(closed);
        Assert.Equal(Text(harness, L.Errors.Cancelled), dialog.StatusText);
        Assert.Null(dialog.ErrorText);
        Assert.Equal("dc01.contoso.test", harness.Sessions.Current!.Info.Server);
        Assert.Empty(harness.AuditLog.Entries);

        // Cancel when idle closes the dialog.
        dialog.CancelCommand.Execute(null);
        Assert.False(closed);
    }

    [Fact]
    public async Task ClosingTheWindowWhileConnecting_CancelsTheSwitch()
    {
        var harness = await HarnessAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ConnectToServer = async (_, server, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new FakeDomainControllerSession(harness.Session, server);
        };
        var dialog = await OpenAsync(harness);
        dialog.ServerText = "dc02.contoso.test";

        var change = dialog.ChangeCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(Ct);
        Assert.True(await dialog.CanCloseAsync());
        dialog.Dispose();
        await change;

        Assert.Equal("dc01.contoso.test", harness.Sessions.Current!.Info.Server);
    }

    [Fact]
    public async Task Disconnected_ShowsNotConnected()
    {
        var harness = await HarnessAsync();
        await harness.Sessions.DisconnectAsync();

        using var dialog = await OpenAsync(harness);

        Assert.Equal(Text(harness, L.Errors.NotConnected), dialog.ErrorText);
        Assert.Empty(dialog.DomainControllers);
        Assert.Equal(string.Empty, dialog.CurrentText);
        dialog.ServerText = "dc02.contoso.test";
        Assert.False(dialog.ChangeCommand.CanExecute(null));
        Assert.False(dialog.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task UnreadableDirectory_AndNoLocator_StillAllowAName()
    {
        var harness = await HarnessAsync();
        harness.Session.FailNext(FakeOperationKind.Search, "CN=Sites,CN=Configuration,DC=contoso,DC=test", DirectoryErrorKind.AccessDenied, times: 5);

        using var dialog = await OpenAsync(harness);

        Assert.Equal(Text(harness, L.ChangeDc.ListUnavailable), dialog.ListNote);
        var current = Assert.Single(dialog.DomainControllers);
        Assert.True(current.Entry.IsCurrent);
        dialog.ServerText = "dc02.contoso.test";
        await dialog.ChangeCommand.ExecuteAsync(null);
        Assert.Equal("dc02.contoso.test", harness.Sessions.Current!.Info.Server);
    }

    [Fact]
    public async Task UnreadableDirectory_AndOnlyTheLocatorFallback_SaysTheListIsUnavailable()
    {
        var harness = await HarnessAsync();
        harness.Session.FailNext(FakeOperationKind.Search, "CN=Sites,CN=Configuration,DC=contoso,DC=test", DirectoryErrorKind.AccessDenied, times: 5);

        // Without DsGetDcName (Linux), or when it found nothing, the locator returns only the domain name itself.
        using (var dialog = await OpenAsync(harness, new DomainControllerInfo("contoso.test", null, false, false, false)))
        {
            Assert.Equal(Text(harness, L.ChangeDc.ListUnavailable), dialog.ListNote);
            var current = Assert.Single(dialog.DomainControllers);
            Assert.True(current.Entry.IsCurrent);
            Assert.False(dialog.CanUseAnyWritableInSite);
        }

        // A locator that knows a DC fills the list: nothing to report.
        using (var dialog = await OpenAsync(harness, new DomainControllerInfo("dc02.contoso.test", "Kraków", true, false, true)))
        {
            Assert.Null(dialog.ListNote);
            Assert.Equal(["dc02.contoso.test", "dc01.contoso.test"], dialog.DomainControllers.Select(d => d.HostName));
        }
    }

    [Fact]
    public async Task SessionEndedWhileSwitching_DoesNotClaimTheSessionStaysConnected()
    {
        var harness = await HarnessAsync();
        harness.ConnectToServer = async (_, server, _) =>
        {
            await harness.Sessions.DisconnectAsync();
            return new FakeDomainControllerSession(harness.Session, server);
        };
        using var dialog = await OpenAsync(harness);
        dialog.ServerText = "dc02.contoso.test";

        await dialog.ChangeCommand.ExecuteAsync(null);

        Assert.Null(harness.Sessions.Current);
        Assert.Null(dialog.Result);
        Assert.Equal(Text(harness, L.WindowSession.Ended), dialog.ErrorText);
        Assert.False(dialog.ChangeCommand.CanExecute(null));
        Assert.False(dialog.RefreshCommand.CanExecute(null));
        Assert.False(Assert.Single(harness.AuditLog.Entries).Succeeded);
    }

    [Fact]
    public async Task Refresh_RereadsTheList()
    {
        var harness = await HarnessAsync();
        using var dialog = await OpenAsync(harness);
        DomainControllerSeed.Add(harness.Session, "dc03.contoso.test", "Kraków");

        await dialog.RefreshCommand.ExecuteAsync(null);

        Assert.Contains(dialog.DomainControllers, d => d.HostName == "dc03.contoso.test");
        Assert.Equal("dc01.contoso.test", dialog.SelectedDomainController?.HostName);
    }

    [Fact]
    public async Task Polish_TextsAndLanguageSwitch()
    {
        var harness = await HarnessAsync(CultureInfo.GetCultureInfo("pl-PL"));
        using var dialog = await OpenAsync(harness, new DomainControllerInfo("dc02.contoso.test", "Kraków", false, false, true));

        Assert.Equal("Zmień kontroler domeny", dialog.WindowTitle);
        Assert.Equal("Bieżący kontroler domeny: dc01.contoso.test", dialog.CurrentText);
        Assert.Equal("Bieżący, Emulator PDC, Wykaz globalny", dialog.DomainControllers.Single(d => d.Entry.IsCurrent).RolesText);
        Assert.Equal("Tak", dialog.DomainControllers.Single(d => d.HostName == "dc02.contoso.test").InClientSiteText);
        Assert.Equal("dc02.contoso.test, Kraków, Twoja lokacja", dialog.DomainControllers.Single(d => d.HostName == "dc02.contoso.test").AutomationName);
        Assert.Equal("Połączenie z dc02.contoso.test w Twojej lokacji Kraków.", dialog.AnyWritableText);

        harness.Localizer.SetCulture(CultureInfo.GetCultureInfo("en-US"));

        Assert.Equal("Change Domain Controller", dialog.WindowTitle);
        Assert.Equal("Current, PDC emulator, Global catalog", dialog.DomainControllers.Single(d => d.Entry.IsCurrent).RolesText);
        Assert.Equal("dc01.contoso.test", dialog.SelectedDomainController?.HostName);
    }

    [Fact]
    public async Task Factory_CreatesTheDialog()
    {
        var harness = await HarnessAsync();

        using var dialog = harness.Factory.CreateChangeDomainController();
        await dialog.InitializeAsync();

        Assert.Equal(3, dialog.DomainControllers.Count);
        Assert.False(dialog.CanUseAnyWritableInSite);
    }

    /// <summary>Connector double for the DC locator only.</summary>
    private sealed class LocatorConnector(IReadOnlyList<DomainControllerInfo> located) : IDirectoryConnector
    {
        public string? GetComputerDomain() => "contoso.test";

        public Task<IReadOnlyList<DomainControllerInfo>> FindDomainControllersAsync(string domain, CancellationToken cancellationToken) =>
            Task.FromResult(located);

        public Task<IDirectorySession> ConnectAsync(ConnectionOptions options, NetworkCredential? credential, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>DC locator that answers only after <see cref="Release"/> (DsGetDcName waiting for DNS).</summary>
    private sealed class HeldLocatorConnector(IReadOnlyList<DomainControllerInfo> located) : IDirectoryConnector
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public string? GetComputerDomain() => "contoso.test";

        public async Task<IReadOnlyList<DomainControllerInfo>> FindDomainControllersAsync(string domain, CancellationToken cancellationToken)
        {
            await _release.Task.WaitAsync(cancellationToken);
            return located;
        }

        public Task<IDirectorySession> ConnectAsync(ConnectionOptions options, NetworkCredential? credential, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
