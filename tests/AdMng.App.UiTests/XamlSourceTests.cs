using System.Globalization;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using AdMng.App.Services;
using AdMng.Presentation.Localization;

namespace AdMng.App.UiTests;

/// <summary>
/// Checks of the XAML sources that WPF only reports at runtime (as "[key]" texts, silently missing commands or a
/// resource that cannot be found): every <c>{l:Tr Key}</c> / <c>TrBinding Key</c> exists in both languages, every
/// <c>b:CommandMenu.Id</c> names a shell command descriptor, every <c>Ad.*</c> resource key that is used is defined, and
/// the Fluent brush keys in use are a closed, documented set read with <c>DynamicResource</c> (track V, stage V1).
/// </summary>
public sealed partial class XamlSourceTests
{
    private static readonly ResourceManager Resources = new(ResxLocalizer.ResourceBaseName, typeof(ResxLocalizer).Assembly);

    /// <summary>
    /// The brushes of the Fluent theme the application uses, sorted (the design note docs/dev/v-interface-renewal.md 4.1
    /// groups them: surfaces and strokes, text, accent, status). The views name the Fluent keys themselves - there are no
    /// colour aliases, because a brush must follow a live switch of light, dark and High Contrast. A new key is a
    /// deliberate edit of this list: first check that the three theme dictionaries (light, dark, High Contrast) have it.
    /// </summary>
    private static readonly string[] FluentBrushKeys =
    [
        "AccentFillColorDefaultBrush",
        "AccentTextFillColorPrimaryBrush",
        "CardBackgroundFillColorDefaultBrush",
        "CardStrokeColorDefaultBrush",
        "ControlFillColorSecondaryBrush",
        "DividerStrokeColorDefaultBrush",
        "FocusStrokeColorOuterBrush",
        "LayerFillColorDefaultBrush",
        "SmokeFillColorDefaultBrush",
        "SolidBackgroundFillColorQuarternaryBrush",
        "SolidBackgroundFillColorSecondaryBrush",
        "SubtleFillColorSecondaryBrush",
        "SubtleFillColorTertiaryBrush",
        "SurfaceStrokeColorFlyoutBrush",
        "SystemFillColorAttentionBackgroundBrush",
        "SystemFillColorAttentionBrush",
        "SystemFillColorCautionBackgroundBrush",
        "SystemFillColorCautionBrush",
        "SystemFillColorCriticalBackgroundBrush",
        "SystemFillColorCriticalBrush",
        "SystemFillColorSuccessBrush",
        "TextFillColorDisabledBrush",
        "TextFillColorPrimaryBrush",
        "TextFillColorSecondaryBrush",
        "TextFillColorTertiaryBrush",
        "TextOnAccentFillColorPrimaryBrush",
    ];

    [Fact]
    public void EveryLocalizationKeyUsedInXaml_ExistsInEnglishAndPolish()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Runs with the Windows test suite.");
        var english = Keys(CultureInfo.InvariantCulture);
        var polish = Keys(CultureInfo.GetCultureInfo("pl"));
        var used = XamlFiles()
            .SelectMany(file => TrKey().Matches(File.ReadAllText(file)).Select(m => (File: Path.GetFileName(file), Key: m.Groups["key"].Value)))
            .ToList();

        Assert.True(used.Count > 100);
        Assert.Equal([], used.Where(u => !english.Contains(u.Key) || !polish.Contains(u.Key)).Select(u => $"{u.File}: {u.Key}"));
    }

    [Fact]
    public void EveryCommandIdUsedInXaml_IsAShellCommand()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Runs with the Windows test suite.");
        // The palette and the descriptor lists of the feature partials (ShellViewModel.GroupPolicy.cs, ...) it spreads in.
        var ids = System.IO.Directory.EnumerateFiles(Path.Combine(SourceDirectory(), "..", "AdMng.Presentation", "Shell"), "ShellViewModel*.cs")
            .SelectMany(file => DescriptorId().Matches(File.ReadAllText(file)).Select(m => m.Groups["id"].Value))
            .ToHashSet(StringComparer.Ordinal);
        var used = XamlFiles()
            .SelectMany(file => CommandId().Matches(File.ReadAllText(file)).Select(m => (File: Path.GetFileName(file), Id: m.Groups["id"].Value)))
            .ToList();

        Assert.NotEmpty(used);
        Assert.Equal([], used.Where(u => !ids.Contains(u.Id)).Select(u => $"{u.File}: {u.Id}"));
    }

    [Fact]
    public void EveryAdResourceKeyUsedInXaml_IsDefined()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Runs with the Windows test suite.");
        var files = XamlFiles().Select(file => (Path: file, Text: File.ReadAllText(file))).ToList();

        // Application-wide keys: the dictionaries of Themes/ and App.xaml, and the resource the code sets (Ad.IconStyle).
        var shared = files.Where(f => IsApplicationDictionary(f.Path))
            .SelectMany(f => DefinedKeys(f.Text))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("Ad.Card", shared);
        Assert.Contains("Ad.Radius.Card", shared);
        shared.Add(IconStyleService.ResourceKey);

        var used = 0;
        var missing = new List<string>();
        foreach (var (path, text) in files)
        {
            // A view may also define a key for itself (window or control resources).
            var local = DefinedKeys(text).ToHashSet(StringComparer.Ordinal);
            foreach (var key in ResourceReference().Matches(text).Select(m => m.Groups["key"].Value).Where(k => k.StartsWith("Ad.", StringComparison.Ordinal)))
            {
                used++;
                if (!shared.Contains(key) && !local.Contains(key))
                {
                    missing.Add($"{Path.GetFileName(path)}: {key}");
                }
            }
        }

        Assert.True(used > 1000, $"only {used} references found");
        Assert.Equal([], missing.Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public void FluentBrushKeysInUse_AreTheDocumentedSet()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Runs with the Windows test suite.");
        var references = XamlFiles()
            .SelectMany(file => ResourceReference().Matches(File.ReadAllText(file))
                .Select(m => (File: Path.GetFileName(file), Kind: m.Groups["kind"].Value, Key: m.Groups["key"].Value)))
            .Where(r => r.Key.EndsWith("Brush", StringComparison.Ordinal)
                && !r.Key.StartsWith("Ad.", StringComparison.Ordinal)
                && !r.Key.StartsWith("Wd.", StringComparison.Ordinal))
            .ToList();

        Assert.True(references.Count > 100, $"only {references.Count} references found");
        Assert.Equal(FluentBrushKeys.Order(StringComparer.Ordinal).ToArray(), FluentBrushKeys);
        Assert.Equal(FluentBrushKeys, references.Select(r => r.Key).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

        // A theme brush is always a dynamic reference: a static one keeps the brush of the theme it was first read in.
        Assert.Equal([], references.Where(r => r.Kind != "DynamicResource").Select(r => $"{r.File}: {r.Key}").Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public void NoXamlFile_AliasesAResourceWithAnElement()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Runs with the Windows test suite.");

        // <StaticResource x:Key="A" ResourceKey="B" /> as a dictionary entry: the markup compiler of the SDK writes a wrong
        // key table for it (the entry loses its key or takes the place of the dictionary), and an alias of a brush would
        // not follow a theme switch. Resources are referenced with the attribute form {StaticResource B} only.
        var aliases = XamlFiles()
            .Where(file => ResourceElement().IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetFileName(file));

        Assert.Equal([], aliases);
    }

    /// <summary>Whether a XAML file is merged into the application resources: App.xaml and the dictionaries of Themes/.</summary>
    private static bool IsApplicationDictionary(string file)
    {
        var relative = Path.GetRelativePath(SourceDirectory(), file).Replace('\\', '/');
        return relative.Equals("App.xaml", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("Themes/", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> DefinedKeys(string xaml) => ResourceKeyDefinition().Matches(xaml).Select(m => m.Groups["key"].Value);

    private static string SourceDirectory() =>
        typeof(XamlSourceTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "AdMng.AppSourceDirectory").Value!;

    private static IEnumerable<string> XamlFiles() =>
        System.IO.Directory.EnumerateFiles(SourceDirectory(), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static HashSet<string> Keys(CultureInfo culture)
    {
        var set = Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException($"No resources for {culture.Name}.");
        return [.. set.Cast<System.Collections.DictionaryEntry>().Select(e => (string)e.Key)];
    }

    [GeneratedRegex(@"\{l:Tr (?<key>[^}\s]+)\}|<l:TrBinding Key=""(?<key>[^""]+)""")]
    private static partial Regex TrKey();

    [GeneratedRegex(@"b:CommandMenu\.Id=""(?<id>[^""]+)""")]
    private static partial Regex CommandId();

    [GeneratedRegex(@"new\(""(?<id>[a-zA-Z.]+)""")]
    private static partial Regex DescriptorId();

    /// <summary>{StaticResource Key} / {DynamicResource Key} with a plain key (not {x:Type ...} or {x:Static ...}).</summary>
    [GeneratedRegex(@"\{(?<kind>StaticResource|DynamicResource)\s+(?:ResourceKey=)?(?<key>[^\s{}=,]+)\s*\}")]
    private static partial Regex ResourceReference();

    /// <summary>x:Key="Key" with a plain string key.</summary>
    [GeneratedRegex(@"x:Key=""(?<key>[^""{]+)""")]
    private static partial Regex ResourceKeyDefinition();

    /// <summary>A resource extension written as an element.</summary>
    [GeneratedRegex(@"<(?:StaticResource|DynamicResource)(?:Extension)?[\s/>]")]
    private static partial Regex ResourceElement();
}
