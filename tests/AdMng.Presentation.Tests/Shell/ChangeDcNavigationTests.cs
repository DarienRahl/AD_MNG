using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.Shell;

/// <summary>
/// "Change domain controller" together with the navigation track: a virtual list (VLV) and the well-known containers
/// are session state, so after the switch both come from the new DC and nothing touches the closed session.
/// </summary>
public class ChangeDcNavigationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AfterTheSwitch_TheVirtualListIsReadFromTheNewDc()
    {
        var harness = await B2Harness.CreateOnDomainControllerAsync();
        var ou = harness.Builder.AddOu("Masowe");
        for (var i = 0; i < 60; i++)
        {
            harness.Builder.AddUser("Użytkownik", $"{i:0000}", ou);
        }

        using var shell = harness.CreateShell();
        await shell.InitializeAsync();
        var document = shell.SelectedDocument!;
        document.VirtualThreshold = 50;
        document.VirtualPageSize = 10;
        await shell.NavigateAsync(ListLocation.ForContainer(ou));
        var before = document.VirtualRows;
        Assert.True(document.IsVirtual);
        Assert.NotNull(before);
        var oldSession = (FakeDomainControllerSession)harness.Sessions.Current!;

        await harness.Sessions.SwitchDomainControllerAsync("dc02.contoso.test", null, Ct);
        await B2Harness.WaitUntilAsync(() => !document.IsLoading && document.VirtualRows is { } rows && !ReferenceEquals(rows, before));

        Assert.True(oldSession.IsDisposed);
        Assert.Equal("dc02.contoso.test", harness.Sessions.Current!.Info.Server);
        Assert.Null(document.Error);
        Assert.True(document.IsVirtual);
        var after = document.VirtualRows!;
        Assert.Equal(60, after.Count);
        await after.Idle;
        Assert.NotNull(after.GetLoaded(0));
        Assert.False(after.GetLoaded(0)!.IsPlaceholder);

        // Rows the view asks for later are read through the new session too (the old one refuses every call).
        _ = after[55];
        await B2Harness.WaitUntilAsync(() => after.GetLoaded(55) is not null);
        Assert.Null(document.Error);
    }

    [Fact]
    public async Task AfterTheSwitch_WellKnownContainersAreReadAgain_AndNewUsersStillGoToTheRedirectedContainer()
    {
        var harness = await B2Harness.CreateOnDomainControllerAsync();
        var redirected = harness.Builder.AddOu("Nowi");
        harness.Builder.SetWellKnownContainer(WellKnownContainer.Users, redirected);
        using var shell = harness.CreateShell();
        await shell.InitializeAsync();
        await B2Harness.WaitUntilAsync(() => !shell.Tree.WellKnown.IsEmpty);
        Assert.Equal(redirected, shell.DefaultCreateContainer(ObjectKind.User));

        var mapBefore = shell.Tree.WellKnown;
        await harness.Sessions.SwitchDomainControllerAsync("dc02.contoso.test", null, Ct);
        await B2Harness.WaitUntilAsync(() => shell.Tree.DomainNode is { ChildrenLoaded: true }
            && !ReferenceEquals(shell.Tree.WellKnown, mapBefore) && !shell.Tree.WellKnown.IsEmpty);

        Assert.Equal(redirected, shell.Tree.WellKnown.Find(WellKnownContainer.Users));
        Assert.Equal(harness.Builder.ComputersDn, shell.DefaultCreateContainer(ObjectKind.Computer));
        Assert.Equal(redirected, shell.DefaultCreateContainer(ObjectKind.User));
    }
}
