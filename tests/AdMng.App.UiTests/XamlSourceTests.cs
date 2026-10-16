using System.Globalization;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using AdMng.Presentation.Localization;

namespace AdMng.App.UiTests;

/// <summary>
/// Checks of the XAML sources that WPF only reports at runtime (as "[key]" texts or silently missing commands):
/// every <c>{l:Tr Key}</c> / <c>TrBinding Key</c> exists in both languages and every <c>b:CommandMenu.Id</c> names a
/// shell command descriptor.
/// </summary>
public sealed partial class XamlSourceTests
{
    private static readonly ResourceManager Resources = new(ResxLocalizer.ResourceBaseName, typeof(ResxLocalizer).Assembly);

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
        var palette = File.ReadAllText(Path.Combine(SourceDirectory(), "..", "AdMng.Presentation", "Shell", "ShellViewModel.Palette.cs"));
        var ids = DescriptorId().Matches(palette).Select(m => m.Groups["id"].Value).ToHashSet(StringComparer.Ordinal);
        var used = XamlFiles()
            .SelectMany(file => CommandId().Matches(File.ReadAllText(file)).Select(m => (File: Path.GetFileName(file), Id: m.Groups["id"].Value)))
            .ToList();

        Assert.NotEmpty(used);
        Assert.Equal([], used.Where(u => !ids.Contains(u.Id)).Select(u => $"{u.File}: {u.Id}"));
    }

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
}
