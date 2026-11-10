using AdMng.Core.Model;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Shell;
using AdMng.Presentation.Tests.Dialogs;

namespace AdMng.Presentation.Tests.BulkCreate;

/// <summary>
/// The entry points of the bulk creation wizard (docs/dev/phase3-bulk-create.md 1 decision 1, 2.7): the palette command
/// <c>new.bulkCreate</c> (<em>Wiele kont…</em>, group <em>Nowy</em>), the list's <em>Nowy</em> submenu (the current container)
/// and the tree's (the right-clicked container), all opening the batch editor in create mode.
/// </summary>
public class BulkCreateEntryPointTests
{
    [Fact]
    public async Task Palette_OffersWieleKont_InTheNowyGroup_WhileConnected()
    {
        var harness = await B2Harness.CreateAsync(B4Wizard.Polish, null, bulkCreateSchema: true);
        using var shell = harness.CreateShell();
        await shell.InitializeAsync();
        Assert.True(ShellViewModel.IsBulkCreateAvailable);

        shell.CommandPalette.FilterText = "Wiele kont";

        var item = Assert.Single(shell.CommandPalette.Results, r => r.Descriptor.Id == "new.bulkCreate");
        Assert.Equal("Wiele kont…", item.Title);
        Assert.Equal(harness.Localizer[L.CommandCategories.New], item.Category);
        Assert.True(item.CanExecute);
    }

    [Fact]
    public async Task PaletteAndListMenu_OpenTheWizard_ForUsersInTheCurrentContainer()
    {
        var harness = await B2Harness.CreateAsync(B4Wizard.Polish, null, bulkCreateSchema: true);
        var sample = harness.Builder.AddBulkCreateSample(harness.Company);
        using var shell = harness.CreateShell();
        await shell.InitializeAsync();
        await shell.NavigateAsync(ListLocation.ForContainer(sample.NewEmployeesOu));

        var descriptor = shell.CommandPalette.Find("new.bulkCreate")!;
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)descriptor.Command).ExecuteAsync(descriptor.Parameter);

        var request = Assert.Single(harness.Dialogs.BatchEditorRequests);
        Assert.Empty(request.TargetDns);
        Assert.Equal(new BulkCreateRequestView(ObjectKind.User, sample.NewEmployeesOu), BulkCreateRequestView.Of(request));
    }

    [Fact]
    public async Task TreeMenu_PassesTheRightClickedContainer()
    {
        var harness = await B2Harness.CreateAsync(B4Wizard.Polish, null, bulkCreateSchema: true);
        var sample = harness.Builder.AddBulkCreateSample(harness.Company);
        using var shell = harness.CreateShell();
        await shell.InitializeAsync();
        await shell.NavigateAsync(ListLocation.ForContainer(harness.Company.ItOu));

        // The tree's Nowy submenu passes container.Dn (TreeMenu.NewMenu); the OU where users cannot be created is still
        // offered – the wizard reports CannotCreateChild for the chosen kind.
        Assert.True(shell.BulkCreateCommand.CanExecute(sample.BlockedOu));
        await shell.BulkCreateCommand.ExecuteAsync(sample.BlockedOu);

        var request = Assert.Single(harness.Dialogs.BatchEditorRequests);
        Assert.Equal(new BulkCreateRequestView(ObjectKind.User, sample.BlockedOu), BulkCreateRequestView.Of(request));
    }

    [Fact]
    public async Task Disconnected_TheCommandIsDisabled()
    {
        var harness = await B2Harness.CreateAsync(B4Wizard.Polish, null, bulkCreateSchema: true);
        using var shell = harness.CreateShell();
        await shell.InitializeAsync();

        await harness.Sessions.DisconnectAsync();

        Assert.False(shell.BulkCreateCommand.CanExecute(null));
        Assert.False(shell.CommandPalette.Find("new.bulkCreate")!.Command.CanExecute(null));
    }

    [Fact]
    public async Task TheWizard_OnABlockedOu_ReportsThatUsersCannotBeCreatedThere()
    {
        using var wizard = await B4Wizard.CreateAsync();
        var input = wizard.OperatorInput() with { ContainerDn = wizard.Sample.BlockedOu };
        wizard.Use(input);

        await wizard.EnterCheckAsync();

        Assert.True(wizard.Check.Computation!.HasRunErrors);
        Assert.NotEmpty(wizard.Check.RunIssues);
        Assert.False(wizard.Bulk.IsReadyToPlan);
        Assert.False(wizard.Editor.PreviewCommand.CanExecute(null));
    }

    /// <summary>The kind and the container of a request (the DN compared case-insensitively).</summary>
    private sealed record BulkCreateRequestView(ObjectKind Kind, string ParentDn)
    {
        public static BulkCreateRequestView Of(Services.BatchEditorRequest request) =>
            new(request.Create!.Kind, request.Create.ParentDn!.ToUpperInvariant());

        public bool Equals(BulkCreateRequestView? other) =>
            other is not null && Kind == other.Kind && string.Equals(ParentDn, other.ParentDn, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode() => HashCode.Combine(Kind, ParentDn.ToUpperInvariant());
    }
}
