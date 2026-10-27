using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AdMng.App.Infrastructure;

namespace AdMng.App.UiTests.Infrastructure;

/// <summary>One picture of the release screenshots: the PNG file name and its captions.</summary>
internal sealed record ScreenshotInfo(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("title_pl")] string TitlePl,
    [property: JsonPropertyName("title_en")] string TitleEn);

/// <summary>
/// Writes the release screenshots (CI step "Screenshots" in .github/workflows/ci.yml): the client area of a window
/// rendered over an opaque theme background into a 96 DPI PNG, plus <c>manifest.json</c> with the captions of every
/// picture written so far. Only active when <see cref="DirectoryVariable"/> names the output folder; the pictures show
/// the in-memory sample company of the UI tests, never a real directory. UI-thread members.
/// </summary>
internal static class ScreenshotWriter
{
    /// <summary>Environment variable with the output folder; the screenshot tests skip themselves without it.</summary>
    public const string DirectoryVariable = "ADMNG_SCREENSHOTS_DIR";

    public const string ManifestFileName = "manifest.json";

    private const int WmGetMinMaxInfo = 0x0024;

    /// <summary>
    /// Offset of ptMaxTrackSize in MINMAXINFO: five POINTs (ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize,
    /// ptMaxTrackSize) of two 32-bit integers each.
    /// </summary>
    private const int MaxTrackSizeOffset = 4 * 8;

    /// <summary>Largest window size (device pixels) allowed once the desktop size limit is lifted.</summary>
    private const int MaxWindowSize = 8192;

    /// <summary>A render counts as blank when fewer pixels than this share differ from its dominant color.</summary>
    private const double MinimumDetailShare = 0.003;

    /// <summary>Theme brushes of the window background (Fluent: WindowBackground), in order of preference.</summary>
    private static readonly string[] BackgroundKeys = ["WindowBackground", "ApplicationBackgroundBrush", "SolidBackgroundFillColorBaseBrush"];

    private static readonly HwndSourceHook LiftLimitHook = OnWindowMessage;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>The output folder, or null when no screenshots were requested.</summary>
    public static string? OutputDirectory =>
        Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } directory ? directory : null;

    /// <summary>Skips the calling test unless <see cref="DirectoryVariable"/> is set; returns the output folder.</summary>
    public static string RequireOutputDirectory()
    {
        var directory = OutputDirectory;
        Assert.SkipWhen(directory is null, $"{DirectoryVariable} is not set: screenshots are only taken on request (CI step \"Screenshots\").");
        return directory!;
    }

    /// <summary>
    /// Shows <paramref name="window"/> off-screen at its declared size (Width/Height of its XAML; a dimension sized to
    /// content stays so), even when that is larger than the desktop of the build agent.
    /// </summary>
    public static async Task ShowAsync(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var declared = new Size(window.Width, window.Height);
        LiftDesktopSizeLimit(window);
        UiTestSupport.ShowOffScreen(window);
        await ResizeAsync(window, declared.Width, declared.Height);
    }

    /// <summary>
    /// Shows <paramref name="window"/> off-screen with a client area (the rendered part) of exactly
    /// <paramref name="width"/> × <paramref name="height"/> DIPs.
    /// </summary>
    public static async Task ShowWithClientSizeAsync(Window window, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(window);
        LiftDesktopSizeLimit(window);
        UiTestSupport.ShowOffScreen(window);
        await ResizeAsync(window, width, height);

        // The frame (title bar, borders) depends on the system: correct the outer size until the client area fits.
        for (var attempt = 0; ; attempt++)
        {
            var client = Root(window).RenderSize;
            if (IsClose(client.Width, width) && IsClose(client.Height, height))
            {
                return;
            }

            if (attempt == 3)
            {
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"Warning: {window.GetType().Name} has a client area of {client.Width} x {client.Height} instead of {width} x {height}.");
                return;
            }

            await ResizeAsync(window, window.ActualWidth + width - client.Width, window.ActualHeight + height - client.Height);
        }
    }

    /// <summary>
    /// Renders the client area of a window once layout and the dispatcher are idle, fails the test when the picture is
    /// blank, and writes the PNG and its manifest entry to the output folder.
    /// </summary>
    /// <param name="window">The shown window.</param>
    /// <param name="shot">File name and captions.</param>
    /// <param name="dark">The theme of the window (for the fallback background when no theme brush is found).</param>
    public static async Task SaveAsync(Window window, ScreenshotInfo shot, bool dark)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(shot);
        var directory = RequireOutputDirectory();
        window.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync();

        var bitmap = Render(window, dark);
        AssertNotBlank(bitmap, shot.File);

        System.IO.Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(directory, shot.File)))
        {
            encoder.Save(stream);
        }

        UpdateManifest(directory, shot);
        TestContext.Current.TestOutputHelper?.WriteLine($"{shot.File}: {bitmap.PixelWidth} x {bitmap.PixelHeight} px - {shot.TitleEn}");
    }

    /// <summary>
    /// Like <see cref="SaveAsync"/>, for two windows side by side in one picture (two connection windows): both client
    /// areas, top aligned, with a gap of the theme background between them.
    /// </summary>
    public static async Task SaveComposedAsync(Window left, Window right, ScreenshotInfo shot, bool dark)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(shot);
        const int Gap = 16;
        var directory = RequireOutputDirectory();
        left.UpdateLayout();
        right.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync();

        var first = Render(left, dark);
        var second = Render(right, dark);
        var width = first.PixelWidth + Gap + second.PixelWidth;
        var height = Math.Max(first.PixelHeight, second.PixelHeight);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Background(left, dark), null, new Rect(0, 0, width, height));
            drawing.DrawImage(first, new Rect(0, 0, first.PixelWidth, first.PixelHeight));
            drawing.DrawImage(second, new Rect(first.PixelWidth + Gap, 0, second.PixelWidth, second.PixelHeight));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        AssertNotBlank(bitmap, shot.File);

        System.IO.Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(directory, shot.File)))
        {
            encoder.Save(stream);
        }

        UpdateManifest(directory, shot);
        TestContext.Current.TestOutputHelper?.WriteLine($"{shot.File}: {bitmap.PixelWidth} x {bitmap.PixelHeight} px - {shot.TitleEn}");
    }

    /// <summary>The window's root visual (its template), which covers exactly the client area.</summary>
    private static FrameworkElement Root(Window window) =>
        VisualTreeHelper.GetChildrenCount(window) > 0 && VisualTreeHelper.GetChild(window, 0) is FrameworkElement root ? root : window;

    private static bool IsClose(double actual, double expected) => Math.Abs(actual - expected) < 0.5;

    /// <summary>
    /// Sets the outer size (NaN leaves a dimension alone). A size change makes Windows ask the window for its limits
    /// again (WM_GETMINMAXINFO), which refreshes WPF's cached copy through <see cref="LiftLimitHook"/>: first shrink a
    /// little (always within the limits, so it really reaches the window), then set the requested size.
    /// </summary>
    private static async Task ResizeAsync(Window window, double width, double height)
    {
        SetSize(
            window,
            double.IsNaN(width) ? double.NaN : window.ActualWidth - 16,
            double.IsNaN(height) ? double.NaN : window.ActualHeight - 16);
        window.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 1);
        SetSize(window, width, height);
        window.UpdateLayout();
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
    }

    private static void SetSize(Window window, double width, double height)
    {
        if (!double.IsNaN(width))
        {
            window.Width = width;
        }

        if (!double.IsNaN(height))
        {
            window.Height = height;
        }
    }

    /// <summary>
    /// Build agents often run a 1024 × 768 desktop, and Windows (and WPF's own copy of the limits) clamp a window to the
    /// desktop size. A message hook raises the maximum tracking size before WPF reads it.
    /// </summary>
    private static void LiftDesktopSizeLimit(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (HwndSource.FromHwnd(handle) is { } source)
        {
            // Hooks run newest first, so this one runs before the window's own WM_GETMINMAXINFO handler.
            source.RemoveHook(LiftLimitHook);
            source.AddHook(LiftLimitHook);
        }
    }

    private static nint OnWindowMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmGetMinMaxInfo && lParam != 0)
        {
            RaiseTo(lParam, MaxTrackSizeOffset, MaxWindowSize);
            RaiseTo(lParam, MaxTrackSizeOffset + sizeof(int), MaxWindowSize);
        }

        // Not handled: the window's own handler runs next and caches the raised limits.
        return 0;
    }

    private static void RaiseTo(nint structure, int offset, int minimum) =>
        Marshal.WriteInt32(structure, offset, Math.Max(Marshal.ReadInt32(structure, offset), minimum));

    /// <summary>
    /// The Fluent window background is transparent where the system draws Mica, which a bitmap would show as black or
    /// transparent: draw an opaque rectangle with the theme's window background first, then the window's visual.
    /// </summary>
    private static RenderTargetBitmap Render(Window window, bool dark)
    {
        var root = Root(window);
        var width = (int)Math.Round(root.RenderSize.Width);
        var height = (int)Math.Round(root.RenderSize.Height);
        Assert.True(width > 0 && height > 0, $"{window.GetType().Name} has no size to render ({width} x {height}).");

        var bounds = new Rect(0, 0, width, height);
        var content = new VisualBrush(root)
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
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Background(window, dark), null, bounds);
            drawing.DrawRectangle(content, null, bounds);
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static SolidColorBrush Background(Window window, bool dark)
    {
        foreach (var key in BackgroundKeys)
        {
            if (window.TryFindResource(key) is SolidColorBrush { Color.A: 255 } brush)
            {
                return brush;
            }
        }

        return new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3));
    }

    /// <summary>Fails when the picture is (nearly) one color, black or not opaque: a broken render, not a screenshot.</summary>
    private static void AssertNotBlank(BitmapSource bitmap, string file)
    {
        var pixels = new int[bitmap.PixelWidth * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var counts = new Dictionary<int, int>();
        foreach (var pixel in pixels)
        {
            counts[pixel] = counts.GetValueOrDefault(pixel) + 1;
        }

        var dominant = counts.MaxBy(c => c.Value);
        var detail = pixels.Length - dominant.Value;
        var color = $"#{(uint)dominant.Key:X8} (ARGB)";
        Assert.True(
            detail >= pixels.Length * MinimumDetailShare,
            $"The screenshot {file} is blank: {detail} of {pixels.Length} pixels differ from the dominant color {color}, {counts.Count} colors in total.");
        Assert.True((uint)dominant.Key >> 24 == 0xFF, $"The screenshot {file} is not opaque: the dominant color is {color}.");
        Assert.True((uint)dominant.Key != 0xFF000000, $"The screenshot {file} is mostly black ({dominant.Value} of {pixels.Length} pixels): the render failed.");
    }

    /// <summary>Adds or replaces the entry of <paramref name="shot"/> in manifest.json (sorted by file name).</summary>
    private static void UpdateManifest(string directory, ScreenshotInfo shot)
    {
        var path = Path.Combine(directory, ManifestFileName);
        var entries = File.Exists(path)
            ? JsonSerializer.Deserialize<List<ScreenshotInfo>>(File.ReadAllText(path, Encoding.UTF8), JsonOptions) ?? []
            : [];
        entries.RemoveAll(e => string.Equals(e.File, shot.File, StringComparison.OrdinalIgnoreCase));
        entries.Add(shot);
        entries.Sort((a, b) => string.CompareOrdinal(a.File, b.File));
        File.WriteAllText(path, JsonSerializer.Serialize(entries, JsonOptions) + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
