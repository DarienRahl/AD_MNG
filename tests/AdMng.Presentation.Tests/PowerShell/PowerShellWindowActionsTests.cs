using AdMng.Core.PowerShell;
using AdMng.Presentation.PowerShell;

namespace AdMng.Presentation.Tests.PowerShell;

/// <summary>
/// What <c>Out-AdmGrid</c>, <c>Open-AdmObject</c> and <c>Export-AdmList</c> call back into (track R, decisions D4 and D6): the
/// plain table they pass, the contract of the interface as the fake of a host honours it, and the actions of a window whose host
/// gave none.
/// </summary>
public sealed class PowerShellWindowActionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PowerShellGridTable Table() =>
        new(["DistinguishedName", "Name", "mail"], [["CN=Jan,DC=a,DC=b", "Jan", "jan@a.b"], ["CN=Ola,DC=a,DC=b", "Ola", null]], "Users", "distinguishedname");

    [Fact]
    public async Task TheGrid_TheObject_AndTheExport_AreWhatAHostAnswers()
    {
        IPowerShellWindowActions actions = new FakePowerShellWindowActions { ChosenPath = @"C:\exports\users.csv" };
        var table = Table();

        await actions.ShowInGridAsync(table, Ct);
        var opened = await actions.OpenObjectAsync("CN=Jan,DC=a,DC=b", Ct);
        var written = await actions.ExportListAsync(table, null, null, Ct);
        var explicitPath = await actions.ExportListAsync(table, @"C:\x\y.csv", ';', Ct);

        var fake = (FakePowerShellWindowActions)actions;
        Assert.Same(table, Assert.Single(fake.Grids));
        Assert.Equal(PowerShellOpenOutcome.Opened, opened);
        Assert.Equal(["CN=Jan,DC=a,DC=b"], fake.Opened);
        // No path: the operator chooses in Save As; a path: exactly that one - never a default.
        Assert.Equal(@"C:\exports\users.csv", written);
        Assert.Equal(@"C:\x\y.csv", explicitPath);
        Assert.Equal([(table, (string?)null, (char?)null), (table, @"C:\x\y.csv", ';')], fake.Exports);
    }

    [Fact]
    public async Task ASaveAsThatIsCancelled_WritesNothing_AndSaysSo()
    {
        var actions = new FakePowerShellWindowActions { ChosenPath = null };

        var written = await actions.ExportListAsync(Table(), null, null, Ct);

        Assert.Null(written);
    }

    [Fact]
    public async Task AnObjectThatIsNotThere_IsNotFound()
    {
        var actions = new FakePowerShellWindowActions { Open = _ => PowerShellOpenOutcome.NotFound };

        Assert.Equal(PowerShellOpenOutcome.NotFound, await actions.OpenObjectAsync("{" + Guid.Empty + "}", Ct));
    }

    [Fact]
    public async Task AStoppedCommand_StopsTheActions()
    {
        var actions = new FakePowerShellWindowActions();
        using var stopped = new CancellationTokenSource();
        await stopped.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => actions.ShowInGridAsync(Table(), stopped.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => actions.OpenObjectAsync("x", stopped.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => actions.ExportListAsync(Table(), null, null, stopped.Token));
        Assert.Empty(actions.Grids);
        Assert.Empty(actions.Exports);
    }

    [Fact]
    public async Task AWindowWhoseHostGaveNoActions_SaysItCannot_InsteadOfDoingNothing()
    {
        var actions = new PowerShellServices(null, null, null).Actions;

        Assert.Same(UnavailablePowerShellWindowActions.Instance, actions);
        await Assert.ThrowsAsync<NotSupportedException>(() => actions.ShowInGridAsync(Table(), Ct));
        await Assert.ThrowsAsync<NotSupportedException>(() => actions.OpenObjectAsync("x", Ct));
        await Assert.ThrowsAsync<NotSupportedException>(() => actions.ExportListAsync(Table(), "p", null, Ct));
    }
}
