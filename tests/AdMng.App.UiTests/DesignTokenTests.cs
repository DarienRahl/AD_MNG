using System.Windows;
using System.Windows.Controls;
using AdMng.App.Behaviors;
using AdMng.App.Infrastructure;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// The design tokens and surfaces of track V (stage V1, docs/dev/v-interface-renewal.md 4.3): Themes/Tokens.xaml holds
/// exactly the documented keys with their values, the surface styles take their brushes from the theme, the main window
/// is a toolbar card and three panels with the splitters as gaps, and the object list takes its column header height from
/// the token. Property and resource checks only - what it looks like is checked on the CI screenshots.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class DesignTokenTests
{
    private const string PanelStyleKey = "Ad.Surface.Panel";
    private const string ToolbarStyleKey = "Ad.Surface.Toolbar";
    private const string TokensDictionary = "Themes/Tokens.xaml";
    private const string ControlsDictionary = "Themes/Controls.xaml";

    [Fact]
    public Task EveryToken_IsAnApplicationResourceWithItsValue() => WpfTestHost.RunAsync(() =>
    {
        var tokens = ValueTokens();
        foreach (var (key, expected) in tokens)
        {
            var value = Application.Current.FindResource(key);
            Assert.IsType(expected.GetType(), value);
            Assert.True(expected.Equals(value), $"{key} is {value}, not {expected}");
        }

        // Tokens.xaml holds exactly these keys and the two surface styles: a new token is added here on purpose.
        var merged = Application.Current.Resources.MergedDictionaries.ToList();
        var dictionary = Assert.Single(merged, d => IsDictionary(d, TokensDictionary));
        string[] expectedKeys = [.. tokens.Select(t => t.Key), PanelStyleKey, ToolbarStyleKey];
        Assert.Equal(
            expectedKeys.Order(StringComparer.Ordinal).ToArray(),
            dictionary.Keys.OfType<string>().Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(expectedKeys.Length, dictionary.Count);

        // Merged before Controls.xaml, whose styles read the tokens.
        var controls = merged.FindIndex(d => IsDictionary(d, ControlsDictionary));
        Assert.True(controls >= 0, "Themes/Controls.xaml is not merged");
        Assert.True(merged.IndexOf(dictionary) < controls, "Themes/Tokens.xaml must be merged before Themes/Controls.xaml");
        return Task.CompletedTask;
    });

    [Fact]
    public Task SurfaceStyles_TakeTheirBrushesFromTheTheme_AndTheirSizesFromTheTokens() => WpfTestHost.RunAsync(() =>
    {
        AssertSurface(PanelStyleKey, "LayerFillColorDefaultBrush", new Thickness(4));
        AssertSurface(ToolbarStyleKey, "CardBackgroundFillColorDefaultBrush", new Thickness(4, 2, 4, 2));
        return Task.CompletedTask;
    });

    [Fact]
    public Task ControlsDictionary_ReadsTheTokens_WithoutChangingItsValues() => WpfTestHost.RunAsync(() =>
    {
        // The styles of Controls.xaml that now read a token keep the value they had as a literal.
        var card = new Border { Style = (Style)Application.Current.FindResource("Ad.Card") };
        Assert.Equal(new CornerRadius(8), card.CornerRadius);
        Assert.Equal(new Thickness(12), card.Padding);

        var error = new Border { Style = (Style)Application.Current.FindResource("Ad.ErrorBar") };
        Assert.Equal(new CornerRadius(4), error.CornerRadius);
        Assert.Equal(new Thickness(12, 8, 12, 8), error.Padding);

        var overlay = new Border { Style = (Style)Application.Current.FindResource("Ad.Overlay") };
        Assert.Equal(new CornerRadius(8), overlay.CornerRadius);
        Assert.Equal(new Thickness(8), overlay.Padding);

        var footer = new Border { Style = (Style)Application.Current.FindResource("Ad.DialogFooter") };
        Assert.Equal(new Thickness(16, 12, 16, 12), footer.Padding);

        var title = new TextBlock { Style = (Style)Application.Current.FindResource("Ad.Title") };
        Assert.Equal(20d, title.FontSize);
        return Task.CompletedTask;
    });

    [Fact]
    public Task MainWindow_IsAToolbarCardAndThreePanels_WithTheSplittersAsGaps() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(window);
        await window.Shell.InitializeAsync();
        await UiTestSupport.WaitForIdleAsync();

        var panel = Application.Current.FindResource(PanelStyleKey);
        var gap = (double)Application.Current.FindResource("Ad.Size.PanelGap");

        // The toolbar card holds the toolbar, the address bar and the quick search.
        Assert.Same(Application.Current.FindResource(ToolbarStyleKey), window.ToolbarCard.Style);
        Assert.Equal(new Thickness(8, 0, 8, 8), window.ToolbarCard.Margin);
        Assert.True(window.Toolbar.IsDescendantOf(window.ToolbarCard));
        Assert.True(window.AddressBox.IsDescendantOf(window.ToolbarCard));
        Assert.True(window.SearchBox.IsDescendantOf(window.ToolbarCard));

        // Three panels of one style in the workspace columns 0, 2 and 4.
        Assert.Equal(new Thickness(8, 0, 8, 8), window.Workspace.Margin);
        Assert.Same(panel, window.TreePanel.Style);
        Assert.Same(panel, window.DocumentPanel.Style);
        Assert.Same(panel, window.DetailsPane.Style);
        Assert.Equal(0, Grid.GetColumn(window.TreePanel));
        Assert.Equal(2, Grid.GetColumn(window.DocumentPanel));
        Assert.Equal(4, Grid.GetColumn(window.DetailsPane));
        Assert.Same(window.Workspace, window.TreePanel.Parent);
        Assert.Same(window.Workspace, window.DocumentPanel.Parent);
        Assert.Same(window.Workspace, window.DetailsPane.Parent);

        // What each panel holds.
        Assert.True(window.NavigationTree.IsDescendantOf(window.TreePanel));
        Assert.True(window.DropRefusedText.IsDescendantOf(window.TreePanel));
        Assert.True(window.Tabs.IsDescendantOf(window.DocumentPanel));
        Assert.True(window.DocumentHost.IsDescendantOf(window.DocumentPanel));
        Assert.True(window.DetailsContent.IsDescendantOf(window.DetailsPane));

        // The splitters are the gaps: transparent, one gap wide, between the panels.
        Assert.Equal(8d, gap);
        Assert.Equal(1, Grid.GetColumn(window.TreeSplitter));
        Assert.Equal(3, Grid.GetColumn(window.DetailsSplitter));
        Assert.Equal(gap, window.TreeSplitter.Width);
        Assert.Equal(gap, window.DetailsSplitter.Width);
        Assert.Equal(GridResizeBehavior.PreviousAndNext, window.TreeSplitter.ResizeBehavior);
        Assert.Equal(GridResizeBehavior.PreviousAndNext, window.DetailsSplitter.ResizeBehavior);
        Assert.Equal(gap, window.TreeSplitter.ActualWidth, 0.5);
        var treeRight = window.TreePanel.TranslatePoint(new Point(window.TreePanel.ActualWidth, 0), window.Workspace).X;
        var documentLeft = window.DocumentPanel.TranslatePoint(new Point(0, 0), window.Workspace).X;
        Assert.True(window.TreePanel.ActualWidth > 100, $"the tree panel is {window.TreePanel.ActualWidth} wide");
        Assert.Equal(gap, documentLeft - treeRight, 0.5);

        // The panels keep the column minimums of the workspace (tree 160, documents 320).
        var columns = window.Workspace.ColumnDefinitions;
        Assert.Equal(5, columns.Count);
        Assert.Equal(160d, columns[0].MinWidth);
        Assert.Equal(320d, columns[2].MinWidth);
        Assert.Same(window.DetailsColumn, columns[4]);
        context.AssertHealthy();
    });

    [Fact]
    public Task MainWindow_ADocumentIsAsWideAsBeforeThePanels() => WpfTestHost.RunAsync(async () =>
    {
        // The document views are fitted to the width a document had with the old layout (a tree column of 280 and its
        // 8 px splitter, then the document without margin, border or padding). The panels take 26 px of a row; the tree
        // column gives them, so the content of the document panel keeps that width.
        await using var context = await UiTestContext.CreateAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        await ScreenshotWriter.ShowWithClientSizeAsync(window, 1440, 900);
        await window.Shell.InitializeAsync();
        await UiTestSupport.WaitForIdleAsync();

        var client = ((FrameworkElement)window.Content).ActualWidth;
        var details = window.DetailsPane.Visibility == Visibility.Visible
            ? window.DetailsColumn.ActualWidth + window.DetailsSplitter.ActualWidth
            : 0;
        var document = (FrameworkElement)window.DocumentPanel.Child;
        Assert.True(client > 1000, $"the client area is {client} wide");
        Assert.Equal(client - 280 - 8 - details, document.ActualWidth, 0.5);

        // The same with the details pane hidden (as on the Watchdog tab).
        await window.Shell.ToggleDetailsPaneCommand.ExecuteAsync(null);
        await UiTestSupport.WaitForIdleAsync();
        var hidden = window.DetailsPane.Visibility == Visibility.Visible
            ? window.DetailsColumn.ActualWidth + window.DetailsSplitter.ActualWidth
            : 0;
        Assert.NotEqual(details, hidden);
        Assert.Equal(client - 280 - 8 - hidden, document.ActualWidth, 0.5);
        context.AssertHealthy();
    });

    [Fact]
    public Task ObjectList_TakesItsColumnHeaderHeightFromTheToken() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        await context.ConnectAsync();
        var window = context.Services.GetRequiredService<MainWindow>();
        UiTestSupport.ShowOffScreen(window);
        await window.Shell.InitializeAsync();
        await UiTestSupport.WaitForIdleAsync();
        Assert.NotNull(window.SelectedListView);
        var list = window.SelectedListView!.ListControl;
        Assert.True(list.IsDescendantOf(window.DocumentPanel));

        // b:Density wrote the token under the key the Fluent column header reads (the theme's own value is 32).
        var token = (double)Application.Current.FindResource("Ad.Size.ColumnHeader");
        Assert.Equal(28d, token);
        Assert.Equal(token, Density.GetColumnHeaderHeight(list));
        Assert.Equal(token, list.Resources[Density.ColumnHeaderHeightKey]);

        // Every realized column header finds that value first when it looks the key up.
        var headers = UiWait.Descendants<GridViewColumnHeader>(list).ToList();
        Assert.NotEmpty(headers);
        Assert.All(headers, header => Assert.Equal(token, header.FindResource(Density.ColumnHeaderHeightKey)));
        context.AssertHealthy();
    });

    [Fact]
    public Task Density_WritesAndRemovesTheColumnHeaderHeight() => WpfTestHost.RunAsync(() =>
    {
        var list = new ListView();
        Assert.True(double.IsNaN(Density.GetColumnHeaderHeight(list)));
        Assert.False(list.Resources.Contains(Density.ColumnHeaderHeightKey));

        Density.SetColumnHeaderHeight(list, 28);
        Assert.Equal(28d, list.Resources[Density.ColumnHeaderHeightKey]);

        Density.SetColumnHeaderHeight(list, 32);
        Assert.Equal(32d, list.Resources[Density.ColumnHeaderHeightKey]);

        // Not a number = the theme's own height: the override is taken away again.
        Density.SetColumnHeaderHeight(list, double.NaN);
        Assert.False(list.Resources.Contains(Density.ColumnHeaderHeightKey));
        return Task.CompletedTask;
    });

    /// <summary>Every value token of Themes/Tokens.xaml (design note 4.2 and 4.3); the type is the value's type.</summary>
    private static (string Key, object Value)[] ValueTokens() =>
    [
        ("Ad.Space.Page", new Thickness(16, 12, 16, 12)),
        ("Ad.Space.Card", new Thickness(12)),
        ("Ad.Space.Toolbar", new Thickness(4, 2, 4, 2)),
        ("Ad.Space.Panel", new Thickness(4)),
        ("Ad.Size.Control", 28d),
        ("Ad.Size.Row", 26d),
        ("Ad.Size.ColumnHeader", 28d),
        ("Ad.Size.PointerTarget", 24d),
        ("Ad.Size.PanelGap", 8d),
        ("Ad.FontSize.Caption", 12d),
        ("Ad.FontSize.Compact", 12d),
        ("Ad.FontSize.Body", 14d),
        ("Ad.FontSize.Subtitle", 20d),
        ("Ad.FontSize.Title", 28d),
        ("Ad.Radius.Control", new CornerRadius(4)),
        ("Ad.Radius.Card", new CornerRadius(8)),
        ("Ad.Radius.Pill", new CornerRadius(12)),
    ];

    private static bool IsDictionary(ResourceDictionary dictionary, string path) =>
        dictionary.Source is not null && dictionary.Source.OriginalString.EndsWith(path, StringComparison.OrdinalIgnoreCase);

    private static void AssertSurface(string key, string backgroundKey, Thickness padding)
    {
        var style = Assert.IsType<Style>(Application.Current.FindResource(key));
        Assert.Equal(typeof(Border), style.TargetType);

        // The brushes are dynamic references to Fluent keys, so they follow a live switch of light, dark and High Contrast.
        Assert.Equal(backgroundKey, DynamicKey(style, Border.BackgroundProperty));
        Assert.Equal("CardStrokeColorDefaultBrush", DynamicKey(style, Border.BorderBrushProperty));

        // Applied to a border: the brushes resolve in the running theme, the sizes are the tokens' values.
        var border = new Border { Style = style };
        Assert.NotNull(border.Background);
        Assert.NotNull(border.BorderBrush);
        Assert.Equal(new CornerRadius(8), border.CornerRadius);
        Assert.Equal(padding, border.Padding);
        Assert.Equal(new Thickness(1), border.BorderThickness);

        // Nothing that would draw the content through an intermediate surface (design note 4.3).
        Assert.Null(border.Effect);
        Assert.Null(border.OpacityMask);
        Assert.Null(border.Clip);
        Assert.False(border.ClipToBounds);
    }

    private static object? DynamicKey(Style style, DependencyProperty property)
    {
        var setter = Assert.Single(style.Setters.OfType<Setter>(), s => s.Property == property);
        return Assert.IsType<DynamicResourceExtension>(setter.Value).ResourceKey;
    }
}
