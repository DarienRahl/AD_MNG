using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.ActiveDirectory;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Localization;
using AdMng.Presentation.PropertySheets;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// The Logon Hours dialog: the 7 × 24 grid with its headers, the keyboard and mouse handlers of the grid, the radio
/// buttons and OK; and the "Godziny logowania…" button of the Account tab applying the result through the sheet's change
/// plan. Every test ends with <see cref="UiTestContext.AssertHealthy"/> (no binding errors).
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class LogonHoursWindowTests
{
    private static readonly DayOfWeek[] Workdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    [Fact]
    public Task LogonHoursWindow_RendersTheGrid_AndEditsByKeyboardMouseAndHeaders() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        var localizer = context.Services.GetRequiredService<ILocalizer>();
        var office = LogonHours.FromLocalGrid(LogonHoursGrid.Create((day, hour) => Workdays.Contains(day) && hour is >= 8 and < 16), 0);
        var dialog = new LogonHoursViewModel(localizer, office, TimeZoneInfo.Utc, DateTimeOffset.UtcNow, "Jan Kowalski");
        var window = (LogonHoursWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(dialog);
        UiTestSupport.ShowOffScreen(window);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        Assert.Equal("Godziny logowania dla: Jan Kowalski", window.Title);
        var cells = UiWait.Descendants<Grid>(window.HourGrid).Where(g => g.DataContext is LogonHoursCellViewModel).ToList();
        Assert.Equal(LogonHours.HoursPerWeek, cells.Count);
        Assert.All(cells, c => Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(c))));
        var days = UiWait.Descendants<Button>(window.DayHeaders).ToList();
        Assert.Equal("Poniedziałek", days[0].Content);
        Assert.Equal(24, UiWait.Descendants<Button>(window.HourHeaders).Count());

        // Keyboard on the grid: Right moves the focus, Space switches the focused (denied) hour.
        window.HourGrid.Focus();
        Press(window.HourGrid, Key.Right);
        Assert.Equal(1, dialog.FocusedCell.Hour);
        Press(window.HourGrid, Key.Space);
        Assert.True(dialog.FocusedCell.IsPermitted);
        Press(window.HourGrid, Key.End);
        Assert.Equal(23, dialog.FocusedCell.Hour);

        // Mouse: the grid maps a point to its cell (row 2 = Wednesday in Polish, column 8), clamped at the edges.
        Assert.Equal((DayOfWeek.Wednesday, 8), Position(window.CellAt(new Point((8 * 24) + 5, (2 * 24) + 5))));
        Assert.Equal((DayOfWeek.Sunday, 23), Position(window.CellAt(new Point(5000, 5000))));
        dialog.BeginSelection(window.CellAt(new Point(5, 5))!, extend: false);
        dialog.ExtendSelection(window.CellAt(new Point((3 * 24) + 5, (1 * 24) + 5))!);
        Assert.Equal(8, dialog.Days.Sum(d => d.Cells.Count(c => c.IsSelected)));

        // Headers run their commands (bound through the proxy) and the radio buttons set the selection.
        var saturday = days.Single(b => b.DataContext is LogonHoursDayViewModel { Day: DayOfWeek.Saturday });
        Assert.True(saturday.Command.CanExecute(saturday.CommandParameter));
        saturday.Command.Execute(saturday.CommandParameter);
        window.PermittedRadio.IsChecked = true;
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(dialog.Days.Single(d => d.Day == DayOfWeek.Saturday).Cells.All(c => c.IsPermitted));
        Assert.Contains("Sobota", window.SelectionLine.Text, StringComparison.Ordinal);
        window.SelectAllButton.Command.Execute(null);
        window.DeniedRadio.IsChecked = true;
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(dialog.IsNonePermitted);
        Assert.Contains(UiWait.Descendants<TextBlock>(window), t => t.IsVisible && t.Text == localizer[L.LogonHours.NoneWarning]);

        window.OkButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(window.Result);
        Assert.False(window.IsVisible);
        Assert.Equal(LogonHours.None, dialog.Result);
        context.AssertHealthy();
    });

    [Fact]
    public Task LogonHoursWindow_ReadOnly_DisablesTheRadioButtons() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var localizer = context.Services.GetRequiredService<ILocalizer>();
        var dialog = new LogonHoursViewModel(localizer, null, TimeZoneInfo.Utc, DateTimeOffset.UtcNow, null, localizer[L.Fields.ReadOnlyPermission]);
        var window = (LogonHoursWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(dialog);
        UiTestSupport.ShowOffScreen(window);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        Assert.Equal("Logon Hours", window.Title);
        Assert.False(window.PermittedRadio.IsEnabled);
        Assert.False(window.DeniedRadio.IsEnabled);
        Assert.Contains(UiWait.Descendants<TextBlock>(window), t => t.IsVisible && t.Text == localizer[L.Fields.ReadOnlyPermission]);
        window.HourGrid.Focus();
        Press(window.HourGrid, Key.Space);
        Assert.True(dialog.FocusedCell.IsPermitted);

        window.CancelButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await UiTestSupport.WaitForIdleAsync();
        Assert.False(window.Result);
        context.AssertHealthy();
    });

    [Fact]
    public Task AccountTab_LogonHoursButton_OpensTheDialog_AndApplyWritesThroughThePipeline() => WpfTestHost.RunAsync(async () =>
    {
        var (created, _) = await TestDialogService.CreateContextAsync("pl");
        await using var context = created;
        await context.ConnectAsync();
        var jan = context.Company.JanKowalski;
        var services = context.Services;
        var window = (PropertiesWindow)services.GetRequiredService<IWindowService>()
            .CreateWindow(services.GetRequiredService<IDialogViewModelFactory>().CreatePropertySheet(jan));
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        var sheet = window.ViewModel;
        sheet.SelectedPage = sheet.Pages.Single(p => p.Key == L.Pages.Account);
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        var button = UiWait.Descendants<Button>(window).Single(b => b.DataContext is LogonHoursFieldViewModel);
        var field = (LogonHoursFieldViewModel)button.DataContext;
        Assert.Equal("Godziny logowania…", button.Content);
        Assert.Equal("Wszystkie godziny", field.Summary);
        Assert.Contains(UiWait.Descendants<TextBlock>(window), t => t.IsVisible && t.Text == field.Summary);

        var drive = UiWait.DriveModalAsync<LogonHoursWindow>(async w =>
        {
            await UiTestSupport.WaitForIdleAsync();
            var saturday = UiWait.Descendants<Button>(w.DayHeaders).Single(b => b.DataContext is LogonHoursDayViewModel { Day: DayOfWeek.Saturday });
            saturday.Command.Execute(saturday.CommandParameter);
            w.DeniedRadio.IsChecked = true;
            await UiTestSupport.WaitForIdleAsync();
            w.OkButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        });
        await field.EditCommand.ExecuteAsync(null);
        await drive;

        Assert.True(field.IsDirty);
        Assert.True(sheet.HasChanges);
        await sheet.ApplyCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();

        Assert.Null(sheet.ApplyError);
        var time = services.GetRequiredService<TimeProvider>();
        var expected = LogonHours.FromLocalGrid(
            LogonHoursGrid.Create((day, _) => day != DayOfWeek.Saturday),
            LogonHours.OffsetHours(time.LocalTimeZone, time.GetUtcNow()));
        Assert.Equal([expected.ToHex().ToUpperInvariant()], context.Directory.Session.GetValues(jan, Attr.LogonHours));
        Assert.False(sheet.HasChanges);
        context.AssertHealthy();
    });

    private static (DayOfWeek Day, int Hour) Position(LogonHoursCellViewModel? cell)
    {
        Assert.NotNull(cell);
        return (cell.Day, cell.Hour);
    }

    private static void Press(UIElement target, Key key) =>
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
}
