using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AdMng.App.BulkCreate;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Model;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.BulkCreate;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Services;
using AdMng.Testing.BulkCreate;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests;

/// <summary>
/// Steps 1–3 of the bulk creation wizard in the batch editor window (docs/dev/phase3-bulk-create.md 2.6, 2.11 App B3): a sheet
/// pasted with Ctrl+V renders in the source grid, the columns step shows the automatic mapping with the attribute combo, the
/// rules step shows the numbering and hands its password boxes to the view model – without binding errors – and a 5 000-row
/// paste stays responsive (virtualized grid).
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class BulkCreateInputUiTests
{
    [Fact]
    public Task Steps1To3_RenderThePastedSheet_WithoutBindingErrors() => WpfTestHost.RunAsync(async () =>
    {
        var clipboard = new TableClipboard();
        await using var context = await CreateContextAsync(clipboard);
        var sample = context.Directory.AddBulkCreateSample(context.Company);
        await context.ConnectAsync();
        var (window, editor) = await OpenAsync(context, sample.NewEmployeesOu);
        var wizard = Assert.IsType<BulkCreateViewModel>(editor.BulkCreate);
        Assert.Equal(Visibility.Visible, window.BulkCreateEditPanel.Visibility);

        // 1 Dane: Ctrl+V is the step's key binding.
        var source = Single<BulkSourceView>(window);
        Assert.Equal(Visibility.Visible, source.EmptyHint.Visibility);
        clipboard.Content = new ClipboardContent(null, null, null, ExcelSamples.OperatorScenarioTsv);
        var paste = source.InputBindings.OfType<KeyBinding>().Single(b => b.Key == Key.V && b.Modifiers == ModifierKeys.Control);
        Assert.Same(wizard.Source.PasteCommand, paste.Command);
        paste.Command.Execute(null);
        await UiWait.UntilAsync(() => wizard.Source.Rows.Count == 5, "the pasted rows");
        await UiTestSupport.WaitForIdleAsync();

        Assert.Equal(6, source.SourceGrid.Columns.Count);
        Assert.Equal(5, source.SourceGrid.Items.Count);
        Assert.Equal("Wiersze: 5, kolumny: 5 – nagłówki rozpoznane", source.SummaryText.Text);
        Assert.Equal(Visibility.Collapsed, source.EmptyHint.Visibility);
        Assert.True(wizard.NextStepCommand.CanExecute(null));
        Assert.True(Single<BulkCreateView>(window).NextStepButton.IsEnabled, "Dalej is enabled once the paste is done");

        // Ctrl+C with the header row copies the column names, never the header panels' type names.
        var copied = new DataGridRowClipboardEventArgs(new object(), 0, source.SourceGrid.Columns.Count - 1, isColumnHeadersRow: true);
        foreach (var column in source.SourceGrid.Columns)
        {
            copied.ClipboardRowContent.Add(new DataGridClipboardCellContent(new object(), column, column.Header));
        }

        BulkSourceView.UseHeaderNames(copied);
        Assert.Equal(["Wiersz", "Telefon komórkowy", "Imię", "Nazwisko", "Dział", "E-mail"], copied.ClipboardRowContent.Select(c => c.Content as string));

        // 2 Kolumny: the automatic mapping and the attribute combo of the class's attributes.
        await wizard.NextStepCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        var columns = Single<BulkColumnsView>(window);
        Assert.Equal(5, columns.MappingList.Items.Count);
        var attributeBoxes = UiWait.Descendants<ComboBox>(columns.MappingList).Where(c => c.Name == "AttributeBox").ToList();
        Assert.Contains(attributeBoxes, c => c.Text == "Telefon komórkowy [mobile]");
        Assert.All(attributeBoxes, c => Assert.NotEmpty(c.Items));
        Assert.DoesNotContain(wizard.Columns.AttributeOptions, o => o.LdapName is "unicodePwd" or "memberOf" or "objectSid");

        // 3 Nazwy i opcje: the numbered profile, its first free number and samples; the password boxes are the view model's.
        await wizard.NextStepCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        var rules = Single<BulkRulesView>(window);
        var model = wizard.Rules;
        model.Profile = model.Profiles.Single(p => p.Value == BulkCreateProfileStore.NumberedId);
        await UiWait.UntilAsync(() => model.FirstFreeText?.StartsWith("Pierwszy wolny numer: kd208", StringComparison.Ordinal) == true, "the first free number");
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(Visibility.Visible, rules.NumberingPanel.Visibility);
        Assert.Equal(model.FirstFreeText, rules.FirstFreeText.Text);
        Assert.Equal(model.Rules.Count, rules.RuleList.Items.Count);
        Assert.Contains(UiWait.Descendants<TextBox>(rules.RuleList), t => t.Text == BulkCreateProfileStore.NumberedLoginPattern);
        Assert.IsType<PasswordBoxInput>(model.PasswordInput);
        Assert.IsType<PasswordBoxInput>(model.ConfirmPasswordInput);

        model.PasswordMode = InitialPasswordMode.SameForAll;
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(model.NeedsSharedPassword);
        rules.SharedPasswordBox.Password = "Start!2026Łąka";
        rules.ConfirmPasswordBox.Password = "Start!2026Łąka";
        Assert.False(model.NeedsSharedPassword);
        Assert.Equal("Start!2026Łąka", model.ReadSharedPassword());
        Assert.Empty(rules.SharedPasswordBox.Password);
        Assert.Empty(rules.ConfirmPasswordBox.Password);

        // Wstaw pole lists the fields by their Polish labels.
        Assert.NotEmpty(rules.InsertFieldMenu.Items);
        Assert.Contains(model.InsertableFields, f => f.Text == "Imię" && f.Token == "{givenName}");
        context.AssertHealthy();
    });

    [Fact]
    public Task KindAndProfile_SwitchWithoutBindingErrors() => WpfTestHost.RunAsync(async () =>
    {
        var clipboard = new TableClipboard();
        await using var context = await CreateContextAsync(clipboard);
        var sample = context.Directory.AddBulkCreateSample(context.Company);
        await context.ConnectAsync();
        var (window, editor) = await OpenAsync(context, sample.NewEmployeesOu);
        var wizard = editor.BulkCreate!;
        clipboard.Content = new ClipboardContent(null, null, null, ExcelSamples.Tsv(
            ["Nazwa komputera", "Opis", "Lokalizacja"],
            ["PC-KSIEG-01", "Stanowisko księgowej", "Warszawa, pokój 12"],
            ["PC-KSIEG-02", "Stanowisko kadr", "Warszawa, pokój 14"]));
        await wizard.Source.PasteCommand.ExecuteAsync(null);

        foreach (var kind in new[] { ObjectKind.Computer, ObjectKind.Group, ObjectKind.Contact, ObjectKind.User })
        {
            wizard.Kind = wizard.Kinds.Single(k => k.Value == kind);
            await UiTestSupport.WaitForIdleAsync();
            Assert.Equal(kind, wizard.Rules.Kind);
        }

        await wizard.NextStepCommand.ExecuteAsync(null);
        await wizard.NextStepCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        var rules = Single<BulkRulesView>(window);
        Assert.Equal(Visibility.Visible, rules.UserOptions.Visibility);
        Assert.Equal(Visibility.Collapsed, rules.ComputerOptions.Visibility);
        Assert.False(rules.AdvancedSection.IsExpanded);
        Assert.Equal(wizard.Rules.AdvancedSummary, rules.AdvancedSummaryText.Text);
        context.AssertHealthy();
    });

    [Fact]
    public Task FiveThousandRowPaste_StaysResponsive_AndTheGridIsVirtualized() => WpfTestHost.RunAsync(async () =>
    {
        var clipboard = new TableClipboard();
        await using var context = await CreateContextAsync(clipboard);
        var sample = context.Directory.AddBulkCreateSample(context.Company);
        await context.ConnectAsync();
        var (window, editor) = await OpenAsync(context, sample.NewEmployeesOu);
        var wizard = editor.BulkCreate!;
        clipboard.Content = new ClipboardContent(null, null, null, ExcelSamples.Rows(5000));
        var source = Single<BulkSourceView>(window);

        var watch = Stopwatch.StartNew();
        var paste = wizard.Source.PasteCommand.ExecuteAsync(null);

        // The UI thread keeps pumping while the sheet is parsed on the thread pool.
        while (!paste.IsCompleted)
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 1, pauseMilliseconds: 10);
        }

        await paste;
        await UiTestSupport.WaitForIdleAsync();
        watch.Stop();

        Assert.Equal(5000, wizard.Source.Rows.Count);
        Assert.Equal(5000, source.SourceGrid.Items.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"The paste took {watch.Elapsed}.");
        Assert.True(UiWait.Descendants<DataGridRow>(source.SourceGrid).Count() < 200, "The source grid realizes only the visible rows.");

        await wizard.NextStepCommand.ExecuteAsync(null);
        await wizard.NextStepCommand.ExecuteAsync(null);
        await UiWait.UntilAsync(() => wizard.Rules.Rules.Any(r => r.Sample.Length > 0), "the live samples of 5 000 rows");
        context.AssertHealthy();
    });

    internal static Task<UiTestContext> CreateContextAsync(TableClipboard clipboard) =>
        UiTestContext.CreateAsync("pl", services => services.Replace(ServiceDescriptor.Scoped<IClipboardService>(_ => clipboard)));

    internal static async Task<(BatchEditorWindow Window, BatchEditorViewModel Editor)> OpenAsync(UiTestContext context, string container)
    {
        var editor = context.Services.GetRequiredService<IDialogViewModelFactory>()
            .CreateBatchEditor(new BatchEditorRequest([]) { Create = new BulkCreateRequest(ObjectKind.User, container) });
        var window = (BatchEditorWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(editor);
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync();
        return (window, editor);
    }

    internal static T Single<T>(DependencyObject root)
        where T : FrameworkElement =>
        UiWait.Descendants<T>(root).Single(e => e.IsVisible);
}

/// <summary>A clipboard double that offers a pasted table (text and optional HTML) and records copies.</summary>
internal sealed class TableClipboard : IClipboardService
{
    public ClipboardContent? Content { get; set; }

    public string? Text { get; private set; }

    public ClipboardContent? ReadTable() => Content;

    public ClipboardContent? ReadFormatted() => Content;

    public bool SetText(string text)
    {
        Text = text;
        return true;
    }

    public bool SetSecret(string secret)
    {
        Text = secret;
        return true;
    }

    /// <summary>Never prints the clipboard.</summary>
    public override string ToString() => nameof(TableClipboard);
}
