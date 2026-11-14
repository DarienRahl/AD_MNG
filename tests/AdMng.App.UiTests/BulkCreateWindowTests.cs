using System.Windows;
using System.Windows.Controls;
using AdMng.App.Behaviors;
using AdMng.App.BulkCreate;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Model;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.BulkCreate;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests;

/// <summary>
/// The batch editor in create mode (docs/dev/phase3-bulk-create.md 2.7, B4): the check grid with its generated columns, the
/// frozen name columns and the status glyphs, the in-place update that keeps the open editor, the filter, the created accounts
/// with masked passwords, the undo note and the entry points – without binding errors. Steps 1–3 are B3's
/// (BulkCreateInputUiTests); the end-to-end paste → results test waits for the wave-2 merge.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class BulkCreateWindowTests
{
    [Fact]
    public Task CheckStep_GeneratesItsColumns_WithFrozenNamesAndStatusGlyphs() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync("pl");
        var (window, editor) = await OpenAsync(context);
        var bulk = editor.BulkCreate!;

        bulk.ShowSample(BulkCreateSamples.Computation(), BulkCreateSamples.Plan());
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        var view = Assert.Single(BatchEditorWindowTests.Descendants<BulkCheckView>(window));
        var grid = view.CheckGrid;
        Assert.Equal(Visibility.Visible, window.BulkCreateEditPanel.Visibility);
        Assert.Equal(2 + bulk.Check.Columns.Count, grid.Columns.Count);
        Assert.Equal(2 + 3, grid.FrozenColumnCount);
        Assert.Equal(["Nr", "Imię", "Nazwisko"], view.ValueColumns.Take(3).Select(c => (string)c.Header));
        Assert.Equal(BulkCreateSamples.Rows.Length, grid.Items.Count);
        Assert.True(bulk.IsReadyToPlan);
        Assert.Contains("z błędami: 1", view.SummaryText.Text, StringComparison.Ordinal);
        Assert.Equal(Visibility.Visible, view.ScopeNoteText.Visibility);
        var glyphs = BatchEditorWindowTests.Descendants<TextBlock>(grid).Where(t => t.Name == "StatusGlyph").Select(t => t.Text).ToList();
        Assert.Contains(Controls.Glyphs.Error, glyphs);
        Assert.Contains(Controls.Glyphs.Warning, glyphs);
        context.AssertHealthy();
    });

    [Fact]
    public Task CheckStep_256Columns_RenderWithoutBindingErrors() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync("pl");
        var (window, editor) = await OpenAsync(context);

        editor.BulkCreate!.ShowSample(BulkCreateSamples.Computation(extraAttributes: 247), BulkCreateSamples.Plan());
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        var view = Assert.Single(BatchEditorWindowTests.Descendants<BulkCheckView>(window));
        Assert.True(view.ValueColumns.Count >= 256, $"{view.ValueColumns.Count} columns");
        view.CheckGrid.ScrollIntoView(view.CheckGrid.Items[^1], view.ValueColumns[^1]);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        context.AssertHealthy();
    });

    [Fact]
    public Task CheckStep_ARecomputeUpdatesInPlace_AndKeepsTheOpenEditor_AndTheFilterShowsTheErrors() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync("pl");
        var (window, editor) = await OpenAsync(context);
        var check = editor.BulkCreate!.Check;
        editor.BulkCreate.ShowSample(BulkCreateSamples.Computation(), BulkCreateSamples.Plan());
        await UiTestSupport.WaitForIdleAsync();
        var view = Assert.Single(BatchEditorWindowTests.Descendants<BulkCheckView>(window));
        var row = check.Rows[3];
        var cell = row.Cell(Attr.DisplayName)!;
        cell.IsEditing = true;
        cell.EditText = "Małgorzata D";

        // A newer computation of the same rows: kd211 becomes an error, the open editor keeps its text.
        var newer = BulkCreateSamples.Computation();
        newer = newer with
        {
            Rows = [.. newer.Rows.Select(r => r.Number == 4 ? r with { Status = BulkRowStatus.Error, Issues = newer.Rows[2].Issues } : r)],
        };
        check.Show(newer);
        await UiTestSupport.WaitForIdleAsync();

        Assert.Same(row, check.Rows[3]);
        Assert.Same(cell, row.Cell(Attr.DisplayName));
        Assert.Equal("Małgorzata D", cell.EditText);
        Assert.Equal(BulkRowStatus.Error, row.Status);

        check.Filter = BulkCheckFilter.Errors;
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(2, view.CheckGrid.Items.Count);
        check.Filter = BulkCheckFilter.All;
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(5, view.CheckGrid.Items.Count);
        context.AssertHealthy();
    });

    [Fact]
    public Task Preview_OfTheSamplePlan_HasNoUndoNote() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync("pl");
        var (window, editor) = await OpenAsync(context);
        editor.BulkCreate!.ShowSample(BulkCreateSamples.Computation(), BulkCreateSamples.Plan());
        await UiTestSupport.WaitForIdleAsync();

        await editor.PreviewCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();

        Assert.True(editor.IsPreviewStep, editor.Error);
        Assert.Equal(Visibility.Visible, window.PreviewPanel.Visibility);
        Assert.Equal(Visibility.Collapsed, window.BulkUndoNote.Visibility);
        Assert.Equal(4, editor.ChangeCount);
        context.AssertHealthy();
    });

    [Fact]
    public Task Results_ListTheCreatedAccounts_WithMaskedPasswords_AndCopyThemOneByOne() => WpfTestHost.RunAsync(async () =>
    {
        // The window's clipboard is a recording one: the CI desktop's clipboard may be busy, and a password never goes there.
        var dialogs = new ScriptedDialogService { ConfirmAnswer = true };
        var clipboard = new RecordingClipboard();
        await using var context = await UiTestContext.CreateAsync("pl", services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IDialogService>(dialogs));
            services.Replace(ServiceDescriptor.Singleton<IClipboardService>(clipboard));
        });
        var sample = context.Directory.AddBulkCreateSample(context.Company);
        await context.ConnectAsync();
        var (window, editor) = await OpenAsync(context, sample.NewEmployeesOu);
        var bulk = editor.BulkCreate!;

        // The check of the sample rows in the fake's container; the plan is computed for real (no session-less sample).
        bulk.ShowSample(BulkCreateSamples.Computation(sample.NewEmployeesOu, sample.GroupNew), BulkCreateSamples.Plan());
        await editor.PreviewCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(editor.IsPreviewStep, editor.Error);
        await editor.ExecuteCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => editor.IsResultsStep, "the results of the creation");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        var results = Assert.Single(BatchEditorWindowTests.Descendants<BulkResultsView>(window));
        Assert.Equal(Visibility.Visible, window.BulkResultsPanel.Visibility);
        Assert.Equal(editor.CreatedAccounts.Count, results.CreatedAccountsList.Items.Count);
        Assert.NotEmpty(editor.CreatedAccounts);
        var first = editor.CreatedAccounts[0];
        Assert.Contains(BatchEditorWindowTests.Descendants<TextBlock>(results.CreatedAccountsList), t => t.Name == "PasswordText" && t.Text == CreatedAccountViewModel.Mask);
        Assert.True(editor.HasUnsavedGeneratedPasswords);
        Assert.Equal(Visibility.Collapsed, results.RetryPasswordPanel.Visibility);

        first.CopyPasswordCommand.Execute(null);
        Assert.True(first.WasHandedOut);
        Assert.Equal(1, clipboard.Secrets);
        Assert.Equal(0, clipboard.Texts);
        first.IsPasswordVisible = true;
        await UiTestSupport.WaitForIdleAsync();
        Assert.Contains(BatchEditorWindowTests.Descendants<TextBlock>(results.CreatedAccountsList), t => t.Name == "PasswordText" && t.Text != CreatedAccountViewModel.Mask);
        context.AssertHealthy();
    });

    [Fact]
    public Task EntryPoints_NowyMenus_OfferWieleKont() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync("pl");
        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        await main.Shell.InitializeAsync();
        await UiTestSupport.WaitForIdleAsync();

        var items = AllMenuItems(main.MainMenu).ToList();
        var bulk = Assert.Single(items, i => CommandMenu.GetId(i) == "new.bulkCreate");
        Assert.Same(main.Shell.BulkCreateCommand, bulk.Command);
        Assert.True(bulk.Command.CanExecute(bulk.CommandParameter));
        var tree = TreeMenu.Create(main.Shell.Tree.DomainNode!, main.Shell, context.Services.GetRequiredService<Presentation.Views.ViewLibrary>(), context.Services.GetRequiredService<IDialogService>());
        Assert.Contains(tree.OfType<MenuItem>().SelectMany(AllMenuItems), i => ReferenceEquals(i.Command, main.Shell.BulkCreateCommand) && i.CommandParameter is string);
        context.AssertHealthy();
    });

    [Fact]
    public Task EndToEnd_PasteMapNameCheckPreviewExecute_ShowsThePasswords() => WpfTestHost.RunAsync(async () =>
    {
        var dialogs = new ScriptedDialogService { ConfirmAnswer = true };
        await using var context = await UiTestContext.CreateAsync("pl", services => services.Replace(ServiceDescriptor.Singleton<IDialogService>(dialogs)));
        var sample = context.Directory.AddBulkCreateSample(context.Company);
        await context.ConnectAsync();
        var (window, editor) = await OpenAsync(context, sample.NewEmployeesOu);
        var bulk = editor.BulkCreate!;
        System.Windows.Clipboard.SetText(BulkCreateSamples.Tsv);

        await bulk.Source.PasteCommand.ExecuteAsync(null);
        await bulk.NextStepCommand.ExecuteAsync(null);
        await bulk.NextStepCommand.ExecuteAsync(null);
        await bulk.NextStepCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => bulk.Check.Computation is not null && !bulk.Check.IsComputing, "the check");
        await editor.PreviewCommand.ExecuteAsync(null);
        await editor.ExecuteCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => editor.IsResultsStep, "the results");

        Assert.Equal(BulkCreateSamples.Rows.Length, Assert.Single(BatchEditorWindowTests.Descendants<BulkResultsView>(window)).CreatedAccountsList.Items.Count);
        context.AssertHealthy();
    });

    private static async Task<(BatchEditorWindow Window, BatchEditorViewModel Editor)> OpenAsync(UiTestContext context, string? container = null)
    {
        var editor = context.Services.GetRequiredService<IDialogViewModelFactory>().CreateBatchEditor(new BatchEditorRequest([])
        {
            Create = new BulkCreateRequest(ObjectKind.User, container ?? BulkCreateSamples.ContainerDn),
        });
        var window = (BatchEditorWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(editor);
        UiTestSupport.ShowOffScreen(window);
        await window.Initialization;
        await UiTestSupport.WaitForIdleAsync();
        return (window, editor);
    }

    private static IEnumerable<MenuItem> AllMenuItems(ItemsControl root)
    {
        foreach (var item in root.Items.OfType<MenuItem>())
        {
            yield return item;
            foreach (var child in AllMenuItems(item))
            {
                yield return child;
            }
        }
    }

    /// <summary>Counts the copies (the protected path for passwords) and keeps nothing.</summary>
    private sealed class RecordingClipboard : IClipboardService
    {
        public int Texts { get; private set; }

        public int Secrets { get; private set; }

        public bool SetText(string text)
        {
            Texts++;
            return true;
        }

        public bool SetSecret(string secret)
        {
            Secrets++;
            return true;
        }
    }
}
