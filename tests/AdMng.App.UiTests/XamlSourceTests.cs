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
/// the Fluent brush keys in use are a closed, documented set read with <c>DynamicResource</c> (track V, stage V1). And
/// the one source of motion (design note 5.3, rule 6): no XAML file writes a storyboard, an animation or a visual
/// state, and durations and easings are entries of Themes/Motion.xaml only. And the one place of feedback (stage V2,
/// design note 6.1 and goal G2): a page that was converted says its status through one status line named PageStatus.
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
        "ControlStrokeColorDefaultBrush",
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
        "SystemFillColorSuccessBackgroundBrush",
        "SystemFillColorSuccessBrush",
        "TextFillColorDisabledBrush",
        "TextFillColorPrimaryBrush",
        "TextFillColorSecondaryBrush",
        "TextFillColorTertiaryBrush",
        "TextOnAccentFillColorPrimaryBrush",
    ];

    /// <summary>
    /// The views that say their status through the status line (track V, stage V2; design note 6.1), sorted. The list
    /// grows with every page that is converted (the sweep of stage V2b and later); a view that gets a status line
    /// must be put on it, so the rule of <see cref="APageWithAStatusLine_SaysItsStatusThere_AndNowhereElse"/> holds for it.
    /// </summary>
    private static readonly string[] PagesWithAStatusLine =
    [
        "GroupPolicy/GpoCleanupView.xaml",
        "GroupPolicy/GpoSettingsSearchView.xaml",
        "GroupPolicy/GroupPolicyObjectView.xaml",
        "GroupPolicy/GroupPolicyObjectsView.xaml",
        "GroupPolicy/GroupPolicyScopeView.xaml",
        "GroupPolicy/WmiFiltersView.xaml",
        "Views/AccountReviewWindow.xaml",
        "Views/BatchEditorWindow.xaml",
        "Views/ChangeDomainControllerWindow.xaml",
        "Views/ChangePlanPreviewWindow.xaml",
        "Views/CompareObjectsWindow.xaml",
        "Views/ConnectWindow.xaml",
        "Views/ContainerPickerWindow.xaml",
        "Views/FilterEditorWindow.xaml",
        "Views/FindWindow.xaml",
        "Views/NewObjectWindow.xaml",
        "Views/ObjectListView.xaml",
        "Views/ObjectPickerWindow.xaml",
        "Views/PropertiesWindow.xaml",
        "Views/RecycleBinWindow.xaml",
        "Views/ResetPasswordWindow.xaml",
        "Views/SavedViewEditorWindow.xaml",
        "Views/SessionLogWindow.xaml",
        "Views/SettingsWindow.xaml",
        "Views/UserMessagesWindow.xaml",
    ];

    /// <summary>
    /// The views that were converted in stage V2b and have no status line (design note "V2b-2 as built", Dialogs 11),
    /// sorted: a dialog that starts no operation - it collects input and hands it to its opener - has no status to say;
    /// what it says is true of its fields for as long as they are shown, and stands next to them as a note. The same
    /// holds for a control that a converted window hosts (the rendered preview of a message): its sentence is a note,
    /// and what the window does is said by the line of the window.
    /// <see cref="AConvertedViewWithoutAStatusLine_HasNoBannerAndNoProgressBar"/> keeps the banners of before out of it.
    /// </summary>
    private static readonly string[] ConvertedViewsWithoutAStatusLine =
    [
        "Controls/HtmlPreviewView.xaml",
        "GroupPolicy/GroupPolicyHealthPanel.xaml",
        "GroupPolicy/GroupPolicyNotes.xaml",
        "Views/LogonHoursWindow.xaml",
    ];

    /// <summary>
    /// The views that have their status line already and are not converted yet, sorted (design note "V2b-3 as built",
    /// <i>Group Policy documents</i>). The Group Policy documents got their status holder in their base class, and with
    /// it every one of their views got its status line in one step - the status bar of the main window says nothing
    /// about a tab that has one. The views on this list still have banners or progress bars of their own: the unit
    /// that converts them moves each to <see cref="PagesWithAStatusLine"/>. A list for the time of that sweep - it
    /// ends empty; <see cref="AViewWithAStatusLineThatIsNotConvertedYet_StillHasSomethingToConvert"/> keeps a view
    /// that has nothing left to convert from staying on it.
    /// </summary>
    private static readonly string[] ViewsWithAStatusLineNotConvertedYet =
    [
        "GroupPolicy/AdministrativeTemplatesEditorView.xaml",
        "GroupPolicy/GroupPolicyDiagnosticsView.xaml",
        "GroupPolicy/GroupPolicyModelingView.xaml",
        "GroupPolicy/SecuritySettingsEditorView.xaml",
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

    [Fact]
    public void NoViewWritesAMotion()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Runs with the Windows test suite.");

        // Rule 6 of docs/dev/v-interface-renewal.md 5.3: outside Themes/ no XAML file has a storyboard, an animation
        // element, a visual state, a duration, a key spline or an easing. A view asks for motion with the attached
        // properties of Behaviors/Motion.cs, which read the tokens of Themes/Motion.xaml behind the one switch.
        var views = XamlFiles().Where(file => !IsThemeDictionary(file)).ToList();
        Assert.True(views.Count > 50, $"only {views.Count} XAML files outside Themes/ found");

        var written = views.SelectMany(file => TimelineMarkup().Matches(WithoutComments(File.ReadAllText(file)))
            .Concat(TimingMarkup().Matches(WithoutComments(File.ReadAllText(file))))
            .Select(m => $"{Path.GetFileName(file)}: {m.Value.Trim()}"));

        Assert.Equal([], written.Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public void MotionDictionary_IsTheOnlyPlaceOfDurationsAndEasings()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Runs with the Windows test suite.");
        var themes = XamlFiles().Where(IsThemeDictionary)
            .Select(file => (Name: Path.GetFileName(file), Text: WithoutComments(File.ReadAllText(file))))
            .ToList();
        Assert.Contains(themes, theme => theme.Name == "Tokens.xaml");
        Assert.Contains(themes, theme => theme.Name == "Controls.xaml");

        // Durations and easings are entries of Themes/Motion.xaml and of no other dictionary.
        Assert.Equal(["Motion.xaml"], themes.Where(theme => TimingMarkup().IsMatch(theme.Text)).Select(theme => theme.Name));

        // No storyboard, animation element or visual state of ours in any XAML file, the dictionaries included: every
        // motion is started from code (Behaviors/Motion.cs), where "off" sets the final values directly.
        Assert.Equal([], XamlFiles().Where(file => TimelineMarkup().IsMatch(WithoutComments(File.ReadAllText(file)))).Select(Path.GetFileName));

        // What the dictionary holds are the motion tokens: every key is "Motion.<name>".
        var keys = DefinedKeys(themes.Single(theme => theme.Name == "Motion.xaml").Text).ToList();
        Assert.True(keys.Count >= 18, $"only {keys.Count} motion tokens found");
        Assert.All(keys, key => Assert.StartsWith("Motion.", key, StringComparison.Ordinal));
    }

    [Fact]
    public void APageWithAStatusLine_SaysItsStatusThere_AndNowhereElse()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Runs with the Windows test suite.");
        var views = XamlFiles().Where(file => !IsApplicationDictionary(file)).ToDictionary(
            file => Path.GetRelativePath(SourceDirectory(), file).Replace('\\', '/'),
            file => WithoutComments(File.ReadAllText(file)),
            StringComparer.Ordinal);
        Assert.True(views.Count > 50, $"only {views.Count} views found");
        Assert.Equal(PagesWithAStatusLine.Order(StringComparer.Ordinal).ToArray(), PagesWithAStatusLine);

        foreach (var page in PagesWithAStatusLine)
        {
            Assert.True(views.TryGetValue(page, out var xaml), $"{page} is not a view of the application");

            // Exactly one status line, with the fixed name of design note 6.1, over the status holder of the page.
            AssertOneStatusLine(page, xaml!);

            // The page says what happened in no other place: no banner of the old styles, no progress bar of its own.
            Assert.False(xaml!.Contains("Ad.ErrorBar", StringComparison.Ordinal), $"{page} still has an error bar");
            Assert.False(xaml.Contains("Ad.WarningBar", StringComparison.Ordinal), $"{page} still has a warning bar");
            Assert.False(xaml.Contains("<ProgressBar", StringComparison.Ordinal), $"{page} still has a progress bar");
        }

        // The other way round: a view with a status line is on the list - or, for the time of the sweep, on the list of
        // the views that have the line and are not converted yet -, and nothing else takes the name PageStatus.
        Assert.Equal(
            PagesWithAStatusLine.Concat(ViewsWithAStatusLineNotConvertedYet).Order(StringComparer.Ordinal).ToArray(),
            views.Where(view => StatusLineElement().IsMatch(view.Value)).Select(view => view.Key).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(
            [],
            views.SelectMany(view => PageStatusName().Matches(view.Value)
                    .Where(match => match.Groups["tag"].Value != "StatusLine")
                    .Select(match => $"{view.Key}: {match.Groups["tag"].Value}")));
    }

    [Fact]
    public void AViewWithAStatusLineThatIsNotConvertedYet_StillHasSomethingToConvert()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Runs with the Windows test suite.");
        var views = XamlFiles().Where(file => !IsApplicationDictionary(file)).ToDictionary(
            file => Path.GetRelativePath(SourceDirectory(), file).Replace('\\', '/'),
            file => WithoutComments(File.ReadAllText(file)),
            StringComparer.Ordinal);
        Assert.True(views.Count > 50, $"only {views.Count} views found");
        Assert.Equal(ViewsWithAStatusLineNotConvertedYet.Order(StringComparer.Ordinal).ToArray(), ViewsWithAStatusLineNotConvertedYet);

        // A view is on one of the three lists, never on two.
        Assert.Equal([], ViewsWithAStatusLineNotConvertedYet.Intersect(PagesWithAStatusLine, StringComparer.Ordinal));
        Assert.Equal([], ViewsWithAStatusLineNotConvertedYet.Intersect(ConvertedViewsWithoutAStatusLine, StringComparer.Ordinal));

        foreach (var view in ViewsWithAStatusLineNotConvertedYet)
        {
            Assert.True(views.TryGetValue(view, out var xaml), $"{view} is not a view of the application");

            // The line is the one every page has: one, named PageStatus, over the status holder of the page.
            AssertOneStatusLine(view, xaml!);

            // Something is left to convert; a view without any banner or progress bar belongs on PagesWithAStatusLine.
            Assert.True(
                xaml!.Contains("Ad.ErrorBar", StringComparison.Ordinal)
                    || xaml.Contains("Ad.WarningBar", StringComparison.Ordinal)
                    || xaml.Contains("<ProgressBar", StringComparison.Ordinal),
                $"{view} has no banner and no progress bar left: it belongs on PagesWithAStatusLine");
        }
    }

    [Fact]
    public void AConvertedViewWithoutAStatusLine_HasNoBannerAndNoProgressBar()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Runs with the Windows test suite.");
        var views = XamlFiles().Where(file => !IsApplicationDictionary(file)).ToDictionary(
            file => Path.GetRelativePath(SourceDirectory(), file).Replace('\\', '/'),
            file => WithoutComments(File.ReadAllText(file)),
            StringComparer.Ordinal);
        Assert.True(views.Count > 50, $"only {views.Count} views found");
        Assert.NotEmpty(ConvertedViewsWithoutAStatusLine);
        Assert.Equal(ConvertedViewsWithoutAStatusLine.Order(StringComparer.Ordinal).ToArray(), ConvertedViewsWithoutAStatusLine);

        // A view is on one of the two lists, never on both.
        Assert.Equal([], ConvertedViewsWithoutAStatusLine.Intersect(PagesWithAStatusLine, StringComparer.Ordinal));

        foreach (var view in ConvertedViewsWithoutAStatusLine)
        {
            Assert.True(views.TryGetValue(view, out var xaml), $"{view} is not a view of the application");

            // No status line (a view that gets one moves to PagesWithAStatusLine), no banner of the old styles and no
            // progress bar: what the view says, it says in notes.
            Assert.False(StatusLineElement().IsMatch(xaml!), $"{view} has a status line: it belongs on PagesWithAStatusLine");
            Assert.False(xaml!.Contains("Ad.ErrorBar", StringComparison.Ordinal), $"{view} still has an error bar");
            Assert.False(xaml.Contains("Ad.WarningBar", StringComparison.Ordinal), $"{view} still has a warning bar");
            Assert.False(xaml.Contains("<ProgressBar", StringComparison.Ordinal), $"{view} still has a progress bar");
        }
    }

    /// <summary>Exactly one status line, with the fixed name of design note 6.1, over the status holder of the page.</summary>
    private static void AssertOneStatusLine(string page, string xaml)
    {
        var lines = StatusLineElement().Matches(xaml);
        Assert.True(lines.Count == 1, $"{page} has {lines.Count} status lines");
        var line = lines[0].Value;
        Assert.True(line.Contains("x:Name=\"PageStatus\"", StringComparison.Ordinal), $"{page}: the status line is not named PageStatus");
        Assert.True(line.Contains("Status=\"{Binding Status.Current, Mode=OneWay}\"", StringComparison.Ordinal), $"{page}: the status line does not show Status.Current");
        Assert.True(line.Contains("CloseCommand=\"{Binding Status.CloseCommand, Mode=OneWay}\"", StringComparison.Ordinal), $"{page}: the status line has no close command");
    }

    /// <summary>Whether a XAML file is merged into the application resources: App.xaml and the dictionaries of Themes/.</summary>
    private static bool IsApplicationDictionary(string file)
    {
        var relative = Path.GetRelativePath(SourceDirectory(), file).Replace('\\', '/');
        return relative.Equals("App.xaml", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("Themes/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a XAML file is one of the dictionaries of Themes/.</summary>
    private static bool IsThemeDictionary(string file) =>
        Path.GetRelativePath(SourceDirectory(), file).Replace('\\', '/').StartsWith("Themes/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The markup without its XML comments (a comment may name what the markup must not contain).</summary>
    private static string WithoutComments(string xaml) => XmlComment().Replace(xaml, string.Empty);

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

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XmlComment();

    /// <summary>The start tag of a status line (c:StatusLine) with its attributes.</summary>
    [GeneratedRegex(@"<(?:\w+:)?StatusLine\b[^>]*>")]
    private static partial Regex StatusLineElement();

    /// <summary>An element named PageStatus: its tag without the prefix.</summary>
    [GeneratedRegex(@"<(?:\w+:)?(?<tag>[\w.]+)\b[^>]*\bx:Name=""PageStatus""")]
    private static partial Regex PageStatusName();

    /// <summary>
    /// A motion written in markup: a storyboard (also BeginStoryboard and the attached Storyboard.* properties), an
    /// element whose name ends with "Animation" or "AnimationUsingKeyFrames" / "AnimationUsingPath", a visual state.
    /// </summary>
    [GeneratedRegex(@"Storyboard|<(?:\w+:)?\w*Animation(?:UsingKeyFrames|UsingPath)?[\s/>]|VisualState")]
    private static partial Regex TimelineMarkup();

    /// <summary>
    /// A time or a curve of a motion: a Duration attribute or element (not ToolTipService.ShowDuration, which is how
    /// long a tool tip stays), a key time or key spline, a begin time, an easing function or an easing element.
    /// </summary>
    [GeneratedRegex(@"(?<![A-Za-z])Duration\s*=|<Duration[\s>]|KeySpline\s*=|KeyTime\s*=|BeginTime\s*=|EasingFunction|<(?:\w+:)?\w+Ease[\s/>]")]
    private static partial Regex TimingMarkup();
}
