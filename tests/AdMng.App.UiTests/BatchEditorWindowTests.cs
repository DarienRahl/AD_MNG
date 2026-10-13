using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdMng.App.UiTests;

[Collection(UiTestCollection.Name)]
public sealed class BatchEditorWindowTests
{
    [Fact]
    public Task BatchEditor_SetAttributeAndDisable_PreviewExecuteResultsAndUndo() => WpfTestHost.RunAsync(async () =>
    {
        var dialogs = new ScriptedDialogService { ConfirmAnswer = true };
        await using var context = await UiTestContext.CreateAsync(
            configure: services => services.Replace(ServiceDescriptor.Singleton<IDialogService>(dialogs)));
        await context.ConnectAsync();

        // Above the threshold every execution needs the typed confirmation.
        await context.Services.GetRequiredService<ISettingsStore>().UpdateAsync(s => s.ConfirmationThreshold = 1);
        var company = context.Company;
        var directory = context.Directory.Session;
        var (window, editor) = await OpenAsync(context, new BatchEditorRequest([company.JanKowalski, company.LukaszWisniewski]));

        Assert.Equal(Visibility.Visible, window.ObjectsEditPanel.Visibility);
        Assert.Equal(Visibility.Collapsed, window.CsvEditPanel.Visibility);
        Assert.Equal(2, window.TargetsList.Items.Count);
        Assert.False(editor.PreviewCommand.CanExecute(null));

        // Operations come from the "Add operation" menu (one entry per operation type).
        var menu = window.AddOperationButton.ContextMenu.Items.OfType<MenuItem>().ToList();
        Assert.Equal(Enum.GetValues<OperationType>().Length, menu.Count);
        Invoke(menu.Single(i => Equals(i.CommandParameter, OperationType.SetAttribute)));
        Invoke(menu.Single(i => Equals(i.CommandParameter, OperationType.EnableDisable)));
        await UiTestSupport.WaitForIdleAsync();

        var set = Assert.IsType<SetAttributeEditorViewModel>(editor.Operations[0]);
        var enable = Assert.IsType<EnableDisableEditorViewModel>(editor.Operations[1]);
        Assert.NotEmpty(set.AttributeOptions);
        set.Attribute = "Department [department]";
        set.ValueTemplate = "Rozwój oprogramowania";
        enable.Disable = true;
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(set.IsValid);
        Assert.Contains(Descendants<ComboBox>(window.OperationsList), c => c.Text == "Department [department]");
        Assert.Contains(Descendants<RadioButton>(window.OperationsList), r => r.IsChecked == true && Equals(r.Content, context.Localizer()[L.Commands.Disable]));
        Assert.True(editor.PreviewCommand.CanExecute(null));

        // Preview: two objects change; excluding one row excludes its object.
        await editor.PreviewCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(BatchEditorStep.Preview, editor.Step);
        Assert.Equal(Visibility.Collapsed, window.ObjectsEditPanel.Visibility);
        Assert.Equal(Visibility.Visible, window.PreviewPanel.Visibility);
        Assert.Equal(Visibility.Visible, window.ExecuteButton.Visibility);
        Assert.Equal(2, editor.ChangeCount);
        Assert.Equal(editor.PreviewRows.Count, window.PreviewList.Items.Count);
        Assert.Contains(editor.PreviewRows, r => r.Name == "Jan Kowalski" && r.Before == "IT" && r.After == "Rozwój oprogramowania");
        Assert.False(string.IsNullOrEmpty(window.PreviewSummaryText.Text));
        editor.PreviewRows.First(r => r.Name == "Łukasz Wiśniewski").IsIncluded = false;
        Assert.Equal(1, editor.ChangeCount);
        editor.SetAllIncludedCommand.Execute(true);
        Assert.Equal(2, editor.ChangeCount);

        // Execute (typed confirmation) → results.
        await editor.ExecuteCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Single(dialogs.Confirmations);
        Assert.Equal(BatchEditorStep.Results, editor.Step);
        Assert.Equal(Visibility.Visible, window.ResultsPanel.Visibility);
        Assert.Equal(2, editor.Result!.SucceededCount);
        Assert.Equal(2, window.ResultList.Items.Count);
        Assert.Equal(editor.ResultSummary, window.ResultSummaryText.Text);
        Assert.Equal(["Rozwój oprogramowania"], directory.GetValues(company.JanKowalski, Attr.Department));
        Assert.True(IsDisabled(directory, company.LukaszWisniewski));
        editor.ResultFilter = ResultFilter.Failed;
        await UiTestSupport.WaitForIdleAsync();
        Assert.Empty(window.ResultList.Items);
        editor.ResultFilter = ResultFilter.All;

        // Undo: the rollback plan is shown in the preview, then executed.
        Assert.True(editor.UndoCommand.CanExecute(null));
        editor.UndoCommand.Execute(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(editor.IsRollbackPreview);
        Assert.Equal(Visibility.Visible, window.RollbackBanner.Visibility);
        await editor.ExecuteCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(2, dialogs.Confirmations.Count);
        Assert.Equal(BatchEditorStep.Results, editor.Step);
        Assert.Equal(["IT"], directory.GetValues(company.JanKowalski, Attr.Department));
        Assert.False(IsDisabled(directory, company.LukaszWisniewski));
        context.AssertHealthy();
    });

    [Fact]
    public Task BatchEditor_RendersEveryOperationEditor_AndHandsThePasswordBoxesToTheViewModel() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        await context.ConnectAsync();
        var (window, editor) = await OpenAsync(context, new BatchEditorRequest([context.Company.JanKowalski]));

        foreach (var type in Enum.GetValues<OperationType>())
        {
            editor.AddOperationCommand.Execute(type);
        }

        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(Enum.GetValues<OperationType>().Length, editor.Operations.Count);
        Assert.NotNull(editor.OperationsError);
        Assert.False(editor.PreviewCommand.CanExecute(null));

        // Every editor has its own template (the default template would show the type name).
        Assert.DoesNotContain(Descendants<TextBlock>(window.OperationsList), t => t.Text.StartsWith("AdMng.", StringComparison.Ordinal));
        Assert.All(editor.Operations, op => Assert.NotNull(window.OperationsList.ItemContainerGenerator.ContainerFromItem(op)));

        // The password boxes of the reset operation are handed over as IPasswordInput.
        var reset = editor.Operations.OfType<ResetPasswordEditorViewModel>().Single();
        reset.UseFixedPassword = true;
        await UiTestSupport.WaitForIdleAsync();
        Assert.IsType<PasswordBoxInput>(reset.PasswordInput);
        Assert.IsType<PasswordBoxInput>(reset.ConfirmPasswordInput);
        var boxes = Descendants<PasswordBox>(window.OperationsList).ToList();
        Assert.Equal(2, boxes.Count);
        boxes[0].Password = "Hasło!2026xyz";
        boxes[1].Password = "Inne!2026xyz";
        Assert.Equal("Hasło!2026xyz", reset.PasswordInput!.GetPassword());

        // Only the reset left: Preview checks the boxes and shows the mismatch; typing clears it.
        foreach (var other in editor.Operations.Where(o => o is not ResetPasswordEditorViewModel).ToList())
        {
            editor.RemoveOperationCommand.Execute(other);
        }

        await UiTestSupport.WaitForIdleAsync();
        Assert.Null(editor.OperationsError);
        await editor.PreviewCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(BatchEditorStep.Edit, editor.Step);
        Assert.Equal(context.Localizer()[L.Batch.PasswordMismatch], reset.PasswordError);
        Assert.Contains(Descendants<TextBlock>(window.OperationsList), t => t.Text == reset.PasswordError && t.IsVisible);
        boxes[1].Password = "Hasło!2026xyz";
        Assert.Null(reset.PasswordError);

        window.Close();
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(editor.IsDisposed);
        Assert.Null(reset.PasswordInput);
        context.AssertHealthy();
    });

    [Fact]
    public Task BatchEditor_CsvImport_ShowsMappingAndFilePreview_ThenPlans() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        await context.ConnectAsync();
        var (window, editor) = await OpenAsync(context, new BatchEditorRequest([]));

        Assert.True(editor.IsCsvMode);
        Assert.Equal(Visibility.Visible, window.CsvEditPanel.Visibility);
        Assert.Equal(Visibility.Collapsed, window.ObjectsEditPanel.Visibility);
        var csv = editor.CsvImport!;
        var path = Path.Combine(Path.GetTempPath(), $"admng-uitest-{Guid.NewGuid():N}.csv");
        try
        {
            await File.WriteAllTextAsync(
                path,
                "sAMAccountName;Department;title\r\n"
                + "jan.kowalski;Rozwój;Kierownik zespołu\r\n"
                + "lukasz.wisniewski;Rozwój;Programista\r\n"
                + "nie.ma.takiego;IT;Ktoś\r\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            await csv.LoadFileAsync(path);
        }
        finally
        {
            File.Delete(path);
        }

        await UiTestSupport.WaitForIdleAsync();
        Assert.Null(csv.Error);
        Assert.Equal(3, csv.RowCount);
        Assert.Equal("sAMAccountName", csv.KeyColumn);
        Assert.Equal(Attr.Department, csv.Mappings[1].Attribute, ignoreCase: true);
        Assert.Equal(Path.GetFileName(path), window.FileNameText.Text);
        Assert.Equal(3, window.MappingList.Items.Count);
        Assert.Equal("sAMAccountName", window.KeyColumnBox.SelectedItem);

        // File preview: row number + one column per CSV column.
        var columns = ((GridView)window.CsvPreviewList.View).Columns;
        Assert.Equal(4, columns.Count);
        Assert.Equal(3, window.CsvPreviewList.Items.Count);
        Assert.Contains(Descendants<TextBlock>(window.CsvPreviewList), t => t.Text == "Kierownik zespołu");
        Assert.True(editor.PreviewCommand.CanExecute(null));

        await editor.PreviewCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(BatchEditorStep.Preview, editor.Step);
        Assert.Equal(3, editor.ObjectCount);
        Assert.Equal(1, editor.ErrorCount);
        Assert.Contains(editor.PreviewRows, r => r.SourceRow == 3 && r.Severity == RowSeverity.Error);
        Assert.Equal(editor.PreviewRows.Count, window.PreviewList.Items.Count);

        editor.BackToEditCommand.Execute(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(Visibility.Visible, window.CsvEditPanel.Visibility);
        context.AssertHealthy();
    });

    [Fact]
    public Task BatchEditor_CloseWithUnsavedGeneratedPasswords_AsksAfterTheClosingEvent() => WpfTestHost.RunAsync(async () =>
    {
        var dialogs = new ScriptedDialogService { ConfirmAnswer = true };
        await using var context = await UiTestContext.CreateAsync(
            configure: services => services.Replace(ServiceDescriptor.Singleton<IDialogService>(dialogs)));
        await context.ConnectAsync();
        var (window, editor) = await OpenAsync(context, new BatchEditorRequest([context.Company.ZanetaSzymanska])
        {
            Operations = [new ResetPasswordOperation(PasswordMode.GenerateUnique)],
        });

        await editor.PreviewCommand.ExecuteAsync(null);
        await editor.ExecuteCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(BatchEditorStep.Results, editor.Step);
        Assert.True(editor.HasUnsavedGeneratedPasswords);

        // The title bar close is cancelled first; the question comes after the Closing event.
        dialogs.ConfirmAnswer = false;
        window.Close();
        Assert.True(window.IsVisible);
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(window.IsVisible);
        Assert.False(editor.IsDisposed);
        Assert.Equal(2, dialogs.Confirmations.Count);

        dialogs.ConfirmAnswer = true;
        window.Close();
        await UiTestSupport.WaitForIdleAsync();
        Assert.False(window.IsVisible);
        Assert.True(editor.IsDisposed);
        Assert.Null(window.Result);
        context.AssertHealthy();
    });

    [Fact]
    public Task MainWindow_AsksBeforeClosing_WhenABatchEditorHoldsUnexportedPasswords() => WpfTestHost.RunAsync(async () =>
    {
        var dialogs = new ScriptedDialogService { ConfirmAnswer = true };
        await using var context = await UiTestContext.CreateAsync(
            configure: services => services.Replace(ServiceDescriptor.Singleton<IDialogService>(dialogs)));
        await context.ConnectAsync();
        var main = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(main);
        await main.Shell.InitializeAsync();
        var (_, editor) = await OpenAsync(context, new BatchEditorRequest([context.Company.BartlomiejKozlowski])
        {
            Operations = [new ResetPasswordOperation(PasswordMode.GenerateUnique)],
        });
        await editor.PreviewCommand.ExecuteAsync(null);
        await editor.ExecuteCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(context.Services.GetRequiredService<ToolWindows>().HasUnsavedGeneratedPasswords);
        var asked = dialogs.Confirmations.Count;

        // Owned batch editors would close with the main window without asking: the main window asks instead.
        dialogs.ConfirmAnswer = false;
        main.Close();
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(main.IsVisible);
        Assert.Equal(asked + 1, dialogs.Confirmations.Count);

        dialogs.ConfirmAnswer = true;
        main.Close();
        await UiTestSupport.WaitForIdleAsync();
        Assert.False(main.IsVisible);
        context.AssertHealthy();
    });

    [Fact]
    public Task DialogService_OpensTheBatchEditor_AndTheCsvImport() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        await context.ConnectAsync();
        var dialogs = context.Services.GetRequiredService<IDialogService>();

        await dialogs.ShowBatchEditorAsync(new BatchEditorRequest([context.Company.PawelLewandowski]) { Title = "Zmiana działu" });
        await dialogs.ShowBatchEditorAsync(new BatchEditorRequest([]));
        await UiTestSupport.WaitForIdleAsync();

        var windows = Application.Current.Windows.OfType<BatchEditorWindow>().ToList();
        Assert.Equal(2, windows.Count);
        var objects = windows.Single(w => !w.ViewModel.IsCsvMode);
        await objects.Initialization;
        Assert.Equal("Zmiana działu", objects.ViewModel.ChangeTitle);
        Assert.Single(objects.ViewModel.Targets);
        Assert.Contains(windows, w => w.ViewModel.IsCsvMode);

        // Close through the view model: the window closes and the view model is disposed.
        await objects.ViewModel.CloseCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.False(objects.IsVisible);
        Assert.True(objects.ViewModel.IsDisposed);
        Assert.False(objects.Result);
        context.AssertHealthy();
    });

    private static async Task<(BatchEditorWindow Window, BatchEditorViewModel Editor)> OpenAsync(UiTestContext context, BatchEditorRequest request)
    {
        var editor = context.Services.GetRequiredService<IDialogViewModelFactory>().CreateBatchEditor(request);
        var window = (BatchEditorWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(editor);
        UiTestSupport.ShowOffScreen(window);
        await window.Initialization;
        await UiTestSupport.WaitForIdleAsync();
        return (window, editor);
    }

    private static void Invoke(MenuItem item)
    {
        Assert.True(item.Command.CanExecute(item.CommandParameter));
        item.Command.Execute(item.CommandParameter);
    }

    private static bool IsDisabled(FakeDirectorySession directory, string dn) =>
        ((UserAccountControl)int.Parse(directory.GetValues(dn, Attr.UserAccountControl)[0], CultureInfo.InvariantCulture))
            .HasFlag(UserAccountControl.AccountDisable);

    internal static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}

/// <summary>Small helpers over the test host.</summary>
internal static class UiTestContextExtensions
{
    public static ILocalizer Localizer(this UiTestContext context) => context.Services.GetRequiredService<ILocalizer>();
}
