using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Views;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

[Collection(UiTestCollection.Name)]
public sealed class DialogWindowTests
{
    [Fact]
    public Task MessageDialogs_RenderEveryKind_AndReportTheResult() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        var localizer = context.Services.GetRequiredService<ILocalizer>();
        var windows = context.Services.GetRequiredService<IWindowService>();

        var info = (MessageDialogWindow)windows.CreateWindow(MessageDialogViewModel.Information("Informacja", "Zażółć gęślą jaźń.", localizer));
        var error = (MessageDialogWindow)windows.CreateWindow(MessageDialogViewModel.Error("Błąd", "Nie można połączyć.", localizer));
        var confirm = (MessageDialogWindow)windows.CreateWindow(MessageDialogViewModel.Confirmation("Usuń", "Czy usunąć obiekt?", destructive: true, localizer));
        var typed = (MessageDialogWindow)windows.CreateWindow(MessageDialogViewModel.TypedConfirmation("Usuń", "Wpisz 120.", "120", localizer));
        foreach (var window in new[] { info, error, confirm, typed })
        {
            UiTestSupport.ShowOffScreen(window);
        }

        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal("Tak", confirm.ViewModel.ConfirmText);
        Assert.True(confirm.CancelButton.IsDefault);

        // The typed confirmation enables "Confirm" only for the exact text.
        Assert.False(typed.ViewModel.ConfirmCommand.CanExecute(null));
        typed.TypedInput.Text = "12";
        Assert.False(typed.ViewModel.ConfirmCommand.CanExecute(null));
        typed.TypedInput.Text = "120";
        Assert.True(typed.ViewModel.ConfirmCommand.CanExecute(null));
        typed.ViewModel.ConfirmCommand.Execute(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(typed.Result);
        Assert.False(typed.IsVisible);

        confirm.ViewModel.CancelCommand.Execute(null);
        Assert.False(confirm.Result);
        context.AssertHealthy();
    });

    [Fact]
    public Task SavedViewEditor_ValidatesTestsAndSaves() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        await context.ConnectAsync();
        var editor = context.Services.GetRequiredService<SavedViewEditorViewModel>();
        var window = (SavedViewEditorWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(editor);
        UiTestSupport.ShowOffScreen(window);
        await UiTestSupport.WaitForIdleAsync();

        editor.Name = "Księgowość – wyłączone";
        editor.Filter = "(&(objectCategory=person)(department=Księgowość)";
        await UiTestSupport.WaitForIdleAsync();
        Assert.NotNull(editor.FilterError);
        Assert.False(editor.SaveCommand.CanExecute(null));

        editor.Filter = "(&(objectCategory=person)(department=Księgowość))";
        await editor.TestCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Null(editor.FilterError);
        Assert.False(string.IsNullOrEmpty(editor.TestResult));

        editor.SaveCommand.Execute(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(window.Result);
        Assert.NotNull(editor.Result);
        context.AssertHealthy();
    });

    [Fact]
    public Task ColumnChooser_AddsAndReordersColumns() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        await context.ConnectAsync();
        var list = context.Services.GetRequiredService<ObjectListViewModel>();
        await list.NavigateAsync(ListLocation.ForContainer(context.Company.ItOu));
        var chooser = list.CreateColumnChooser();
        var window = (ColumnChooserWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(chooser);
        UiTestSupport.ShowOffScreen(window);
        await UiTestSupport.WaitForIdleAsync();

        Assert.NotEmpty(chooser.Available);
        chooser.FilterText = "zzz-no-such-attribute";
        await UiTestSupport.WaitForIdleAsync();
        Assert.Empty(chooser.Available);
        chooser.FilterText = string.Empty;
        await UiTestSupport.WaitForIdleAsync();
        chooser.SelectedAvailable = chooser.Available.FirstOrDefault(c => c.Attribute == "department") ?? chooser.Available[0];
        chooser.AddCommand.Execute(null);
        chooser.SelectedColumn = chooser.Selected[^1];
        chooser.MoveUpCommand.Execute(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Equal(4, chooser.Result.Count);

        window.OkButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(window.Result);
        list.Dispose();
        context.AssertHealthy();
    });
}
