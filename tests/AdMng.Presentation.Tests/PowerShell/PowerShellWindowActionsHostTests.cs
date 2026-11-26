using System.Globalization;
using AdMng.Core.PowerShell;
using AdMng.Presentation.PowerShell;
using AdMng.Presentation.Tests.Dialogs;

namespace AdMng.Presentation.Tests.PowerShell;

/// <summary>
/// The actions of the program (<see cref="PowerShellWindowActions"/>): what <c>Out-AdmGrid</c>, <c>Open-AdmObject</c> and
/// <c>Export-AdmList</c> do through the connection window's services - the grid window, the property sheet, the CSV file.
/// </summary>
public sealed class PowerShellWindowActionsHostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PowerShellGridTable Table() => new(
        ["DistinguishedName", "Name", "note"],
        [["CN=Jan,DC=a,DC=b", "Jan Kowalski", "=1+1"], ["CN=Ola,DC=a,DC=b", "Ola; \"Nowak\"", null]],
        "Users",
        "DistinguishedName");

    private static PowerShellWindowActions Create(B2Harness harness) => new(harness.Services, harness.Engines.Csv);

    [Fact]
    public async Task TheGrid_IsShownByTheDialogsOfTheConnection()
    {
        var harness = await B2Harness.CreateAsync();
        var table = Table();

        await Create(harness).ShowInGridAsync(table, Ct);

        Assert.Same(table, Assert.Single(harness.Dialogs.PowerShellGrids));
    }

    /// <summary>
    /// Owner answer 12 (audit W-notes-out-admgrid-cap): <c>Out-AdmGrid</c> opened a window for every call, so a loop of a thousand calls
    /// was a thousand windows. At most ten are open at a time, like the windows of one Open-AdmObject; the eleventh is refused.
    /// </summary>
    [Fact]
    public async Task TheEleventhGrid_IsRefused_WhileTenAreOpen()
    {
        var harness = await B2Harness.CreateAsync();
        var actions = Create(harness);

        for (var index = 0; index < PowerShellWindowActions.MaxGridWindows; index++)
        {
            await actions.ShowInGridAsync(Table(), Ct);
        }

        var refused = await Assert.ThrowsAsync<PowerShellGridLimitException>(() => actions.ShowInGridAsync(Table(), Ct));
        Assert.Equal(10, refused.Limit);
        Assert.Equal(10, PowerShellWindowActions.MaxGridWindows);
        Assert.Equal(10, harness.Dialogs.PowerShellGrids.Count);
    }

    [Fact]
    public async Task AClosedGrid_FreesItsPlace_Once()
    {
        var harness = await B2Harness.CreateAsync();
        var actions = Create(harness);
        for (var index = 0; index < PowerShellWindowActions.MaxGridWindows; index++)
        {
            await actions.ShowInGridAsync(Table(), Ct);
        }

        // The operator closes the third grid: one more opens, and the next is refused again. A second "closed" of the same grid frees nothing.
        harness.Dialogs.PowerShellGridClosers[2]();
        harness.Dialogs.PowerShellGridClosers[2]();
        await actions.ShowInGridAsync(Table(), Ct);
        await Assert.ThrowsAsync<PowerShellGridLimitException>(() => actions.ShowInGridAsync(Table(), Ct));
        Assert.Equal(11, harness.Dialogs.PowerShellGrids.Count);
    }

    [Fact]
    public async Task AGridThatCouldNotBeShown_OrWasRefused_HoldsNoPlace()
    {
        var harness = await B2Harness.CreateAsync();
        var actions = Create(harness);

        harness.Dialogs.FailNextPowerShellGrid = new InvalidOperationException("no window");
        await Assert.ThrowsAsync<InvalidOperationException>(() => actions.ShowInGridAsync(Table(), Ct));
        for (var index = 0; index < PowerShellWindowActions.MaxGridWindows; index++)
        {
            await actions.ShowInGridAsync(Table(), Ct);
        }

        // Refusals do not count either: after three of them, closing one grid is enough for the next.
        for (var index = 0; index < 3; index++)
        {
            await Assert.ThrowsAsync<PowerShellGridLimitException>(() => actions.ShowInGridAsync(Table(), Ct));
        }

        harness.Dialogs.PowerShellGridClosers[0]();
        await actions.ShowInGridAsync(Table(), Ct);
        Assert.Equal(11, harness.Dialogs.PowerShellGrids.Count);
    }

    [Fact]
    public async Task AnObject_IsOpenedByItsDistinguishedName_AndNothingElseIsGuessed()
    {
        var harness = await B2Harness.CreateAsync();
        var actions = Create(harness);

        var opened = await actions.OpenObjectAsync("CN=Jan Kowalski,OU=Firma,DC=contoso,DC=test", Ct);
        var guid = await actions.OpenObjectAsync(Guid.NewGuid().ToString("D"), Ct);
        var empty = await actions.OpenObjectAsync("   ", Ct);
        var root = await actions.OpenObjectAsync("", Ct);

        Assert.Equal(PowerShellOpenOutcome.Opened, opened);
        Assert.Equal(PowerShellOpenOutcome.NotFound, guid);
        Assert.Equal(PowerShellOpenOutcome.NotFound, empty);
        Assert.Equal(PowerShellOpenOutcome.NotFound, root);
        Assert.Equal(["CN=Jan Kowalski,OU=Firma,DC=contoso,DC=test"], harness.Dialogs.PropertiesShown);
    }

    /// <summary>
    /// Audit ps-commands-4: Export-AdmList wrote the clear-text LAPS password and the BitLocker recovery password of every computer the
    /// account may read (<c>-Properties *</c> returns them) without the question the program's list export asks.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnExportOfColumnsWithSecrets_AsksFirst_AndLeavesThemOutUnlessTheAnswerIsYes(bool include)
    {
        var harness = await B2Harness.CreateAsync();
        harness.Dialogs.ConfirmAnswers.Enqueue(include);
        var table = new PowerShellGridTable(
            ["DistinguishedName", "Name", "ms-Mcs-AdmPwd", "msFVE-RecoveryPassword"],
            [["CN=LAPS01,DC=contoso,DC=test", "LAPS01", "Cleartext!Laps1", "111111-222222"]],
            null,
            "DistinguishedName");
        var path = Path.Combine(Path.GetTempPath(), "admng-actions-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            var written = await Create(harness).ExportListAsync(table, path, ';', Ct);

            Assert.Equal(path, written);
            var confirmation = Assert.Single(harness.Dialogs.Shown, shown => shown.StartsWith("confirm:", StringComparison.Ordinal));
            Assert.Contains("ms-Mcs-AdmPwd", confirmation, StringComparison.Ordinal);
            Assert.Contains("msFVE-RecoveryPassword", confirmation, StringComparison.Ordinal);
            var text = await File.ReadAllTextAsync(path, Ct);
            Assert.Equal(include, text.Contains("Cleartext!Laps1", StringComparison.Ordinal));
            Assert.Equal(include, text.Contains("111111-222222", StringComparison.Ordinal));
            Assert.Contains("LAPS01", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AnExportWithAPath_WritesExactlyThatFile_WithTheDelimiterOfTheCommand_AndProtectedFormulas()
    {
        var harness = await B2Harness.CreateAsync();
        var path = Path.Combine(Path.GetTempPath(), "admng-actions-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            var written = await Create(harness).ExportListAsync(Table(), path, ',', Ct);

            Assert.Equal(path, written);
            var lines = await File.ReadAllLinesAsync(path, Ct);
            Assert.Equal("DistinguishedName,Name,note", lines[0]);
            Assert.Equal("CN=Jan,DC=a,DC=b,Jan Kowalski,'=1+1", lines[1].Replace("\"CN=Jan,DC=a,DC=b\"", "CN=Jan,DC=a,DC=b", StringComparison.Ordinal));
            Assert.Contains("\"Ola; \"\"Nowak\"\"\"", lines[2], StringComparison.Ordinal);
            Assert.Null(harness.Files.LastDefaultFileName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AnExportWithoutAPath_AsksInSaveAs_AndUsesTheSeparatorOfTheRegion()
    {
        var harness = await B2Harness.CreateAsync(regional: CultureInfo.GetCultureInfo("pl-PL"));
        var path = Path.Combine(Path.GetTempPath(), "admng-actions-" + Guid.NewGuid().ToString("N") + ".csv");
        harness.Files.NextSavePath = path;
        try
        {
            var written = await Create(harness).ExportListAsync(Table(), null, null, Ct);

            Assert.Equal(path, written);
            Assert.StartsWith("DistinguishedName;Name;note", (await File.ReadAllLinesAsync(path, Ct))[0], StringComparison.Ordinal);
            Assert.StartsWith("Users", harness.Files.LastDefaultFileName, StringComparison.Ordinal);
            Assert.EndsWith(".csv", harness.Files.LastDefaultFileName, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AnExport_WhenSaveAsIsCancelled_WritesNothingAndReturnsNull()
    {
        var harness = await B2Harness.CreateAsync();
        harness.Files.NextSavePath = null;

        var written = await Create(harness).ExportListAsync(Table(), null, null, Ct);

        Assert.Null(written);
    }

    [Fact]
    public async Task AnExport_WhenTheFileCannotBeWritten_ThrowsSoTheCommandReportsIt()
    {
        var harness = await B2Harness.CreateAsync();
        var path = Path.Combine(Path.GetTempPath(), "admng-no-such-" + Guid.NewGuid().ToString("N"), "x.csv");

        await Assert.ThrowsAnyAsync<IOException>(() => Create(harness).ExportListAsync(Table(), path, null, Ct));
    }

    [Fact]
    public async Task AStoppedCommand_StopsEveryAction_BeforeItDoesAnything()
    {
        var harness = await B2Harness.CreateAsync();
        var actions = Create(harness);
        using var stopped = new CancellationTokenSource();
        await stopped.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => actions.ShowInGridAsync(Table(), stopped.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => actions.OpenObjectAsync("CN=Jan,DC=a,DC=b", stopped.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => actions.ExportListAsync(Table(), "x.csv", null, stopped.Token));
        Assert.Empty(harness.Dialogs.PowerShellGrids);
        Assert.Empty(harness.Dialogs.PropertiesShown);
    }
}
