using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Connection;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Tests.TestDoubles;

namespace AdMng.Presentation.Tests.Session;

public class SessionAndConnectTests
{
    [Fact]
    public async Task SessionManager_ConnectAndDisconnect_RaiseChangesAndDisposeSession()
    {
        var harness = TestHarness.Disconnected();
        var changes = 0;
        harness.Sessions.SessionChanged += (_, _) => changes++;
        Assert.Throws<InvalidOperationException>(() => harness.Sessions.RequireSession());

        await harness.Sessions.ConnectAsync(new ConnectionOptions(), null, TestContext.Current.CancellationToken);

        Assert.Same(harness.Directory, harness.Sessions.Current);
        Assert.True(harness.Sessions.IsConnected);
        Assert.Equal("(never)", harness.Sessions.Formatter.Format(Attr.AccountExpires, new AttributeValues(Attr.AccountExpires, ["0"]), harness.Localizer.Culture));

        await harness.Sessions.DisconnectAsync();

        Assert.Null(harness.Sessions.Current);
        Assert.True(harness.Directory.Disposed);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task DomainPolicy_IsReadOncePerSession()
    {
        var harness = await TestHarness.ConnectedAsync();
        var ct = TestContext.Current.CancellationToken;

        var first = await harness.Sessions.GetDomainPolicyAsync(ct);
        var second = await harness.Sessions.GetDomainPolicyAsync(ct);

        Assert.True(first.IsKnown);
        Assert.Equal(TestDirectory.LockoutDuration, first.LockoutDuration);
        Assert.Equal(TimeSpan.FromDays(90), first.MaxPasswordAge);
        Assert.Same(first, second);
        Assert.Equal(1, harness.Directory.GetCount);
    }

    [Fact]
    public void Initialize_PrefillsTheComputersDomain()
    {
        var (vm, _, _) = CreateConnect();

        vm.Initialize();

        Assert.Equal("contoso.test", vm.Domain);
        Assert.Equal(AuthenticationMode.CurrentUser, vm.Authentication);
        Assert.Equal(TransportSecurity.SignAndSeal, vm.Security);
        Assert.Equal(3, vm.SecurityModes.Count);
    }

    [Fact]
    public async Task Connect_WithCredentials_ReadsPasswordOnlyAtConnectTime_AndClearsIt()
    {
        var (vm, harness, password) = CreateConnect();
        vm.Initialize();
        vm.Authentication = AuthenticationMode.Credentials;
        vm.UserName = @"CONTOSO\admin.łukasz";
        var connected = 0;
        vm.Connected += (_, _) => connected++;
        Assert.Equal(0, password.Reads);

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(1, connected);
        Assert.Equal(1, password.Reads);
        Assert.True(password.Cleared);
        Assert.Equal("admin.łukasz", harness.Connector.LastCredential!.UserName);
        Assert.Equal("CONTOSO", harness.Connector.LastCredential.Domain);
        Assert.Equal("Tajne!Hasło1", harness.Connector.LastCredential.Password);
        Assert.Equal(@"CONTOSO\admin.łukasz", harness.Connector.LastOptions!.UserName);
        Assert.Null(vm.ErrorText);

        // The view model never exposes the password through a property.
        var values = typeof(ConnectViewModel).GetProperties().Where(p => p.GetIndexParameters().Length == 0).Select(p => p.GetValue(vm) as string);
        Assert.DoesNotContain("Tajne!Hasło1", values);
    }

    [Fact]
    public async Task Connect_WithEmptyPassword_IsRejectedBeforeBinding()
    {
        var (vm, harness, password) = CreateConnect();
        vm.Initialize();
        vm.Authentication = AuthenticationMode.Credentials;
        vm.UserName = "jan@contoso.test";
        password.Password = string.Empty;

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(0, harness.Connector.ConnectCount);
        Assert.Equal("Enter the password.", vm.ErrorText);
    }

    [Fact]
    public async Task Connect_Failure_ShowsLocalizedErrorWithHint()
    {
        var (vm, harness, _) = CreateConnect();
        vm.Initialize();
        harness.Connector.ConnectError = new DirectoryException(DirectoryErrorKind.StrongAuthRequired, "00002028: LdapErr: DSID-0C090259, comment: The server requires binds to turn on integrity checking");

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.False(vm.IsBusy);
        Assert.Contains("requires a signed or encrypted connection", vm.ErrorText, StringComparison.Ordinal);
        Assert.Contains("Sign and seal", vm.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_SimpleBindWithoutTls_IsRejected()
    {
        var (vm, harness, _) = CreateConnect();
        vm.Initialize();
        vm.Authentication = AuthenticationMode.SimpleBind;
        vm.UserName = "Administrator@contoso.test";

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(0, harness.Connector.ConnectCount);
        Assert.Contains("LDAPS", vm.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveProfile_PersistsConnectionWithoutPassword_AndSelectingItFillsFields()
    {
        var (vm, harness, _) = CreateConnect();
        vm.Initialize();
        vm.Server = "dc02.contoso.test";
        vm.Authentication = AuthenticationMode.Credentials;
        vm.UserName = "admin@contoso.test";
        vm.Security = TransportSecurity.Tls;
        vm.SaveProfile = true;
        vm.ProfileName = "Produkcja";

        await vm.ConnectCommand.ExecuteAsync(null);

        var profile = Assert.Single(harness.Settings.Current.ConnectionProfiles);
        Assert.Equal("Produkcja", profile.Name);
        Assert.Equal("dc02.contoso.test", profile.Server);
        Assert.Equal(TransportSecurity.Tls, profile.Security);
        Assert.Equal("Produkcja", harness.Settings.Current.LastProfileName);
        Assert.DoesNotContain("Tajne", JsonSettingsStore.Serialize(harness.Settings.Current), StringComparison.Ordinal);

        var next = new ConnectViewModel(harness.Connector, harness.Sessions, harness.Settings, harness.Localizer, harness.Errors);
        next.Initialize();
        Assert.Equal("Produkcja", next.SelectedProfile?.Name);
        Assert.Equal("dc02.contoso.test", next.Server);
        Assert.Equal(AuthenticationMode.Credentials, next.Authentication);
        Assert.Equal("admin@contoso.test", next.UserName);
    }

    [Fact]
    public async Task FindDomainControllers_ListsClientSiteFirst_AndSelectingOneSetsServer()
    {
        var (vm, _, _) = CreateConnect();
        vm.Initialize();

        await vm.FindDomainControllersCommand.ExecuteAsync(null);
        vm.SelectedDomainController = vm.DomainControllers[0];

        Assert.Equal(["dc01.contoso.test", "dc02.contoso.test"], vm.DomainControllers.Select(d => d.HostName));
        Assert.Equal("dc01.contoso.test", vm.Server);
    }

    [Fact]
    public async Task Connect_WithoutDomainAndNotDomainJoined_AsksForDomain()
    {
        var (vm, harness, _) = CreateConnect();
        harness.Connector.ComputerDomain = null;
        vm.Initialize();

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(0, harness.Connector.ConnectCount);
        Assert.Contains("domain", vm.ErrorText, StringComparison.OrdinalIgnoreCase);
    }

    private static (ConnectViewModel Vm, TestHarness Harness, FakePasswordInput Password) CreateConnect()
    {
        var harness = TestHarness.Disconnected();
        var password = new FakePasswordInput("Tajne!Hasło1");
        var vm = new ConnectViewModel(harness.Connector, harness.Sessions, harness.Settings, harness.Localizer, harness.Errors)
        {
            PasswordInput = password,
        };
        return (vm, harness, password);
    }
}
