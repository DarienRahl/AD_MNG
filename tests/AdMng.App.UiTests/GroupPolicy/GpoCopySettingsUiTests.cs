using System.Windows;
using System.Windows.Controls;
using AdMng.App.GroupPolicy;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Presentation.GroupPolicy;
using AdMng.Presentation.GroupPolicy.Copy;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests.GroupPolicy;

/// <summary>
/// "Kopiuj ustawienia" (K7, design note docs/dev/k-computers-and-gpo.md 21.5): the copy mode with a setting that cannot be
/// copied (disabled, with the MS14-025 reason), the conflict modes and the source limit; the merge mode with the order text,
/// the blocking settings and the warnings; headers wider than their texts; binding-error free.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class GpoCopySettingsUiTests
{
    private static readonly Guid Source = new("2C3D4E5F-6A7B-4C8D-9E0F-1A2B3C4D5E6F");

    private static IReadOnlyList<GpoCopyItem> Items() =>
    [
        new(new GpoUnitSelection(Source, "S|Computer|System Access|MinimumPasswordLength"), "Stacje robocze – Zasady haseł", "Minimalna długość hasła",
            "Konfiguracja komputera › Zasady › Ustawienia systemu Windows › Ustawienia zabezpieczeń › Zasady konta › Zasady haseł", "12 znaków", null),
        new(new GpoUnitSelection(Source, "I|Computer|Groups|User|serwis_łódź", "{7A1B2C3D-4E5F-4061-8273-94A5B6C7D8E9}"), "Stacje robocze – Zasady haseł",
            "serwis_łódź", "Konfiguracja komputera › Preferencje › Ustawienia panelu sterowania › Użytkownicy i grupy lokalne", "(ustawione – ukryte)",
            GpoUnitRefusal.Secret),
    ];

    [Fact]
    public Task CopyMode_ShowsTheRefusedSettingDisabled_TheConflictModes_AndConfirms() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        var gp = context.Services.GetRequiredService<GroupPolicyServices>();
        var windows = context.Services.GetRequiredService<IWindowService>();
        var window = (GpoCopySettingsWindow)windows.CreateWindow(new GpoCopySettingsViewModel(gp.Ui, Items(), "Stacje robocze – Pulpit zdalny"));
        UiTestSupport.ShowOffScreen(window);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        Assert.Equal("Kopiuj ustawienia do Stacje robocze – Pulpit zdalny", window.Title);
        Assert.Equal(2, window.ItemsList.Items.Count);
        Assert.True(VirtualizingPanel.GetIsVirtualizing(window.ItemsList));
        Assert.True(window.RefusedTextBlock.IsVisible);
        Assert.False(window.TooManySourcesBar.IsVisible);
        Assert.False(window.OrderTextBlock.IsVisible);
        Assert.Equal(3, window.ConflictBox.Items.Count);
        Assert.Equal("Zastąp", ((GpoConflictOption)window.ConflictBox.SelectedItem).Text);
        Assert.False(string.IsNullOrEmpty(window.ConflictHintText.Text));
        Assert.Equal("Kopiuj", window.OkButton.Content);
        Assert.True(window.OkButton.IsEnabled);

        var gridView = (GridView)window.ItemsList.View;
        foreach (var column in gridView.Columns.Where(c => c.Header is string))
        {
            var header = new TextBlock { Text = (string)column.Header };
            header.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.True(column.Width > header.DesiredSize.Width + 16, (string)column.Header);
        }

        window.CheckNoneButton.Command.Execute(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.False(window.OkButton.IsEnabled);
        window.CheckAllButton.Command.Execute(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(window.OkButton.IsEnabled);
        Assert.Equal("Zaznaczono: 1 z 2", window.CountTextBlock.Text);
        window.OkButton.Command.Execute(null);
        await UiTestSupport.WaitForIdleAsync();
        Assert.True(window.Result);
        Assert.Single(window.ViewModel.SelectedItems);
        context.AssertHealthy();
    });

    [Fact]
    public Task MergeMode_ShowsTheOrder_TheBlockingSettings_AndTheWarnings() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language: "pl");
        var gp = context.Services.GetRequiredService<GroupPolicyServices>();
        var windows = context.Services.GetRequiredService<IWindowService>();
        var merge = new GpoMergeInfo("Stacje robocze – Zasady haseł", "Stacje robocze – Pulpit zdalny", null, true, true,
            ["Obiekty GPO mają różne filtry WMI.", "Tylko Stacje robocze – Pulpit zdalny jest połączony z: Księgowość – po scaleniu te miejsca dostaną też ustawienia Stacje robocze – Zasady haseł."]);
        var window = (GpoCopySettingsWindow)windows.CreateWindow(new GpoCopySettingsViewModel(gp.Ui, Items(), merge));
        UiTestSupport.ShowOffScreen(window);
        await UiTestSupport.WaitForIdleAsync(rounds: 3);

        Assert.Equal("Scal Stacje robocze – Zasady haseł z Stacje robocze – Pulpit zdalny", window.Title);
        Assert.True(window.OrderTextBlock.IsVisible);
        Assert.True(window.BlockingBar.IsVisible);
        Assert.Single(window.BlockingList.Items);
        Assert.True(window.WarningsBar.IsVisible);
        Assert.Equal(2, window.WarningsList.Items.Count);
        Assert.False(window.CheckAllButton.IsVisible);
        Assert.Null(window.ConflictBox.SelectedItem);
        Assert.Equal("Scal", window.OkButton.Content);
        Assert.False(window.OkButton.IsEnabled);
        context.AssertHealthy();
    });
}
