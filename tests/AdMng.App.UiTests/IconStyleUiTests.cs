using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AdMng.App.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests;

/// <summary>
/// The icon styles of track V (stage V1): <see cref="ObjectIconView"/> and <see cref="AreaIconView"/> draw the colour
/// pictures of the icon set by default and the glyphs of the icon font in the monochrome style; the application
/// resource <see cref="IconStyleService.ResourceKey"/> switches every icon of every open window without recreating it.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class IconStyleUiTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    public Task ColourStyle_DrawsTheSharedPictureOfTheSet_NotTheTextColour(int size) => WpfTestHost.RunAsync(async () =>
    {
        using var style = new StyleScope(IconStylePreference.Colour);
        var first = Icon(ObjectIcon.User, size);
        var second = Icon(ObjectIcon.User, size);
        var locked = Icon(ObjectIcon.UserLocked, size);
        var window = Show(first, second, locked);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);

            // A light element of exactly Size x Size: no template, no visual children.
            Assert.Equal(new Size(size, size), first.DesiredSize);
            Assert.Equal(new Size(size, size), first.RenderSize);
            Assert.Equal(0, VisualTreeHelper.GetChildrenCount(first));

            // The user picture is blue although the text colour of the element is red, and it has a gradient.
            var user = Pixels.Of(first, size);
            Assert.True(user.Covered >= size * size / 10, $"only {user.Covered} of {size * size} pixels are painted");
            Assert.True(user.Solid > 0, "no well covered pixel");
            Assert.Equal(user.Solid, user.Blue);
            Assert.Equal(0, user.Red);
            Assert.True(user.Colours >= 2, "the gradient of the picture gives more than one colour");

            // More than one hue: the locked account adds the amber padlock to the blue person; no glyph badge on top.
            var lockedPixels = Pixels.Of(locked, size);
            Assert.True(lockedPixels.Blue > 0, "the locked user has no blue pixel");
            Assert.True(lockedPixels.Amber > 0, "the locked user has no amber pixel");

            // One shared frozen drawing: both elements draw the very image of the application resources.
            var image = Assert.IsAssignableFrom<ImageSource>(AppIcons.Find(AppIcons.User));
            Assert.True(image.IsFrozen);
            Assert.Same(image, Assert.Single(Images(first)));
            Assert.Same(image, Assert.Single(Images(second)));
            Assert.Same(AppIcons.Find(AppIcons.UserLocked), Assert.Single(Images(locked)));
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    public Task MonochromeStyle_DrawsTheGlyphInTheTextColour(int size) => WpfTestHost.RunAsync(async () =>
    {
        using var style = new StyleScope(IconStylePreference.Monochrome);
        var icons = new[] { Icon(ObjectIcon.User, size), Icon(ObjectIcon.Computer, size), Icon(ObjectIcon.Group, size) };
        var window = Show(icons);
        try
        {
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            foreach (var icon in icons)
            {
                Assert.Equal(new Size(size, size), icon.RenderSize);
                var pixels = Pixels.Of(icon, size);
                Assert.True(pixels.Covered > 0, $"{icon.Icon} at {size} px paints nothing");
                // Only the foreground colour family (red here): no pixel of another colour, no picture.
                Assert.Equal(pixels.Covered, pixels.RedOnly);
                Assert.Empty(Images(icon));
            }
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task SwitchingTheStyleResource_RedrawsTheSameElements() => WpfTestHost.RunAsync(async () =>
    {
        using var style = new StyleScope(IconStylePreference.Colour);
        var icon = Icon(ObjectIcon.User, 32);
        var area = new AreaIconView { IconKey = AppIcons.Connection, Glyph = Glyphs.Network, Size = 48, Foreground = Brushes.Red };
        var window = Show(icon, area);
        try
        {
            var panel = Assert.IsType<StackPanel>(window.Content);
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.Equal(0, Pixels.Of(icon, 32).RedOnly);
            Assert.Same(AppIcons.Find(AppIcons.Connection), Assert.Single(Images(area)));
            Assert.Equal(new Size(48, 48), area.RenderSize);

            style.Set(IconStylePreference.Monochrome);
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var monochrome = Pixels.Of(icon, 32);
            Assert.True(monochrome.Covered > 0);
            Assert.Equal(monochrome.Covered, monochrome.RedOnly);
            Assert.Empty(Images(icon));
            var areaGlyph = Pixels.Of(area, 48);
            Assert.True(areaGlyph.Covered > 0);
            Assert.Equal(areaGlyph.Covered, areaGlyph.RedOnly);
            Assert.Empty(Images(area));

            style.Set(IconStylePreference.Colour);
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            var colour = Pixels.Of(icon, 32);
            Assert.Equal(colour.Solid, colour.Blue);
            Assert.Same(AppIcons.Find(AppIcons.User), Assert.Single(Images(icon)));

            // The elements were redrawn, not recreated.
            Assert.Same(icon, panel.Children[0]);
            Assert.Same(area, panel.Children[1]);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task EveryObjectIcon_PaintsSomething_InBothStyles() => WpfTestHost.RunAsync(async () =>
    {
        using var style = new StyleScope(IconStylePreference.Colour);
        var icons = Enum.GetValues<ObjectIcon>().Select(value => Icon(value, 16)).ToArray();
        var window = Show(icons);
        try
        {
            var problems = new List<string>();
            foreach (var preference in new[] { IconStylePreference.Colour, IconStylePreference.Monochrome })
            {
                style.Set(preference);
                await UiTestSupport.WaitForIdleAsync(rounds: 2);
                foreach (var icon in icons)
                {
                    var pictures = Images(icon).Count;
                    if (Pixels.Of(icon, 16).Covered == 0)
                    {
                        problems.Add($"{icon.Icon} paints nothing in the {preference} style");
                    }
                    else if (pictures != (preference == IconStylePreference.Colour ? 1 : 0))
                    {
                        problems.Add($"{icon.Icon} draws {pictures} pictures in the {preference} style");
                    }
                }
            }

            Assert.Empty(problems);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task IconStyleService_PublishesTheEffectiveStyle_AndHighContrastForcesMonochrome() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync();
        var service = context.Services.GetRequiredService<IconStyleService>();
        var appearance = context.Services.GetRequiredService<AppearanceService>();
        var icon = Icon(ObjectIcon.Group, 16);
        var window = Show(icon);
        try
        {
            // The settings of a new profile: colour icons (High Contrast is taken out of the picture first).
            service.HighContrast = static () => false;
            service.Refresh();
            Assert.Equal(IconStylePreference.Colour, appearance.IconStyle);
            Assert.Equal(IconStylePreference.Colour, service.Preference);
            Assert.Equal(IconStylePreference.Colour, Resource());
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.Single(Images(icon));

            // Windows High Contrast switches on: glyphs although the preference stays colour; and back.
            service.HighContrast = static () => true;
            service.Refresh();
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.Equal(IconStylePreference.Colour, service.Preference);
            Assert.Equal(IconStylePreference.Monochrome, service.Effective);
            Assert.Equal(IconStylePreference.Monochrome, Resource());
            Assert.Empty(Images(icon));
            service.HighContrast = static () => false;
            service.Refresh();
            await UiTestSupport.WaitForIdleAsync(rounds: 2);
            Assert.Equal(IconStylePreference.Colour, Resource());
            Assert.Single(Images(icon));

            // The setting: stored, applied at once to the open window, and back.
            await appearance.SetIconStyleCommand.ExecuteAsync(IconStylePreference.Monochrome);
            await UiTestSupport.WaitForIdleAsync();
            Assert.Equal(IconStylePreference.Monochrome, context.Services.GetRequiredService<ISettingsStore>().Current.IconStyle);
            Assert.Equal(IconStylePreference.Monochrome, service.Preference);
            Assert.Equal(IconStylePreference.Monochrome, Resource());
            Assert.Empty(Images(icon));
        }
        finally
        {
            // The resource is process wide: leave it as the other tests expect it.
            await appearance.SetIconStyleCommand.ExecuteAsync(IconStylePreference.Colour);
            await UiTestSupport.WaitForIdleAsync();
            window.Close();
        }

        Assert.Equal(IconStylePreference.Colour, Resource());
        context.AssertHealthy();
    });

    private static object? Resource() => Application.Current.Resources[IconStyleService.ResourceKey];

    private static ObjectIconView Icon(ObjectIcon icon, double size) =>
        new() { Icon = icon, Size = size, Foreground = Brushes.Red, HorizontalAlignment = HorizontalAlignment.Left };

    /// <summary>A window with the elements in a column, shown off-screen (application resources reach only shown windows).</summary>
    private static Window Show(params UIElement[] elements)
    {
        var panel = new StackPanel { Background = Brushes.White };
        foreach (var element in elements)
        {
            if (element is FrameworkElement framework)
            {
                framework.HorizontalAlignment = HorizontalAlignment.Left;
            }

            panel.Children.Add(element);
        }

        var window = new Window { Width = 240, Height = 200, Content = panel, WindowStyle = WindowStyle.ToolWindow };
        UiTestSupport.ShowOffScreen(window);
        return window;
    }

    /// <summary>The images an element draws in its own render data (the instances it passed to the drawing context).</summary>
    private static List<ImageSource> Images(Visual visual)
    {
        var images = new List<ImageSource>();
        Collect(VisualTreeHelper.GetDrawing(visual), images);
        return images;

        static void Collect(Drawing? drawing, List<ImageSource> images)
        {
            switch (drawing)
            {
                case ImageDrawing image when image.ImageSource is not null:
                    images.Add(image.ImageSource);
                    break;
                case DrawingGroup group:
                    foreach (var child in group.Children)
                    {
                        Collect(child, images);
                    }

                    break;
            }
        }
    }

    /// <summary>Sets the style resource for a test and restores what was there before.</summary>
    private sealed class StyleScope : IDisposable
    {
        private readonly bool _had;
        private readonly object? _before;

        public StyleScope(IconStylePreference style)
        {
            var resources = Application.Current.Resources;
            _had = resources.Contains(IconStyleService.ResourceKey);
            _before = _had ? resources[IconStyleService.ResourceKey] : null;
            Set(style);
        }

        public void Set(IconStylePreference style) => Application.Current.Resources[IconStyleService.ResourceKey] = style;

        public void Dispose()
        {
            if (_had)
            {
                Application.Current.Resources[IconStyleService.ResourceKey] = _before;
            }
            else
            {
                Application.Current.Resources.Remove(IconStyleService.ResourceKey);
            }
        }
    }

    /// <summary>Pixel statistics of an element rendered alone on a transparent bitmap of size x size at 96 DPI.</summary>
    private sealed record Pixels(int Covered, int Solid, int Blue, int Amber, int Red, int RedOnly, int Colours)
    {
        public static Pixels Of(FrameworkElement element, int size)
        {
            // Through a visual brush: the element's own offset in its parent does not move it out of the bitmap.
            var bounds = new Rect(0, 0, size, size);
            var brush = new VisualBrush(element)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = bounds,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = bounds,
            };
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(brush, null, bounds);
            }

            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var buffer = new byte[size * size * 4];
            bitmap.CopyPixels(buffer, size * 4, 0);

            int covered = 0, solid = 0, blue = 0, amber = 0, red = 0, redOnly = 0;
            var colours = new HashSet<int>();
            for (var i = 0; i < buffer.Length; i += 4)
            {
                // Premultiplied BGRA.
                int b = buffer[i], g = buffer[i + 1], r = buffer[i + 2], a = buffer[i + 3];
                if (a <= 32)
                {
                    continue;
                }

                covered++;
                if (g <= 16 && b <= 16)
                {
                    redOnly++;
                }

                if (a < 128)
                {
                    continue;
                }

                // Well covered pixels: the colour itself (premultiplication undone), without the faint antialiased edge.
                solid++;
                r = Math.Min(255, r * 255 / a);
                g = Math.Min(255, g * 255 / a);
                b = Math.Min(255, b * 255 / a);
                colours.Add((r << 16) | (g << 8) | b);
                if (b > r + 60 && b > g)
                {
                    blue++;
                }
                else if (r > b + 60 && g > b + 30)
                {
                    amber++;
                }
                else if (r > g + 60 && r > b + 60)
                {
                    red++;
                }
            }

            return new Pixels(covered, solid, blue, amber, red, redOnly, colours.Count);
        }
    }
}
