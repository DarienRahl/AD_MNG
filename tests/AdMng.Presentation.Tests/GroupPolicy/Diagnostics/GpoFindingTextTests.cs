using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using AdMng.Core.GroupPolicy.Diagnostics;
using AdMng.Presentation.GroupPolicy.Diagnostics;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Tests.GroupPolicy.Diagnostics;

/// <summary>
/// The texts of the GPO diagnostics (K6, design note 20.2): every code of <see cref="GpoFindingCodes"/> has a title, a
/// message, an explanation and a recommendation in English and Polish, and the message takes exactly the code's arguments.
/// </summary>
public sealed partial class GpoFindingTextTests
{
    private static readonly ResourceManager Resources = new(ResxLocalizer.ResourceBaseName, typeof(ResxLocalizer).Assembly);

    [Fact]
    public void EveryCode_HasItsFourTexts_InBothLanguages()
    {
        var missing = new List<string>();
        foreach (var code in GpoFindingCodes.All.Select(d => d.Code))
        {
            foreach (var key in new[] { GpoFindingText.TitleKey(code), GpoFindingText.MessageKey(code), GpoFindingText.ExplanationKey(code), GpoFindingText.RecommendationKey(code) })
            {
                foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("pl") })
                {
                    var value = Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)?.GetString(key);
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        missing.Add($"{key} ({(culture.Name.Length == 0 ? "en" : culture.Name)})");
                    }
                }
            }
        }

        Assert.Equal([], missing);
        Assert.Equal(49, GpoFindingCodes.All.Count);
    }

    [Fact]
    public void EveryMessage_FormatsWithTheArgumentsOfItsCode()
    {
        Assert.Equal(GpoFindingCodes.All.Select(d => d.Code).Order(StringComparer.Ordinal), GpoFindingText.ArgumentCounts.Keys.Order(StringComparer.Ordinal));
        foreach (var culture in new[] { CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("pl-PL") })
        {
            var localizer = new ResxLocalizer(culture);
            foreach (var (code, count) in GpoFindingText.ArgumentCounts)
            {
                var message = localizer[GpoFindingText.MessageKey(code)];
                var indexes = Placeholder().Matches(message).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().Order().ToList();

                // Every argument of the code is shown, and nothing else is asked for.
                Assert.True(Enumerable.Range(0, count).SequenceEqual(indexes), $"{code} ({culture.Name}): {message}");
                var args = Enumerable.Range(0, count).Select(i => (object?)("arg" + i)).ToArray();
                var text = localizer.Format(GpoFindingText.MessageKey(code), args);
                Assert.All(args, a => Assert.Contains((string)a!, text, StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void EveryDynamicKey_ExistsInBothLanguages_AndEveryRepairKindIsNamed()
    {
        var en = Resources.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        var pl = Resources.GetResourceSet(CultureInfo.GetCultureInfo("pl"), true, false)!;
        var keys = GpoFindingText.AllKeys().ToList();

        Assert.Equal([], keys.Where(k => en.GetString(k) is null));
        Assert.Equal([], keys.Where(k => pl.GetString(k) is null));
        Assert.Equal(11, GpoFindingText.FixKinds.Count);
        Assert.Contains(nameof(GpoQuickFix.RepairExtensionNames), GpoFindingText.FixKinds);
        Assert.Contains(nameof(GpoQuickFix.RemoveDuplicateLinks), GpoFindingText.FixKinds);
    }

    [Fact]
    public void Polish_ReadsAsGpmc_ForTheTitleAreasAndSeverities()
    {
        var l = new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL"));

        Assert.Equal("Diagnostyka zasad grupy", l[L.GpoDiagnostics.Title]);
        Assert.Equal("Bezpieczeństwo", GpoFindingText.Area(l, GpoFindingArea.Security));
        Assert.Equal("Folder SYSVOL", GpoFindingText.Area(l, GpoFindingArea.Folder));
        Assert.Equal("Krytyczna", GpoFindingText.Severity(l, GpoFindingSeverity.Critical));
        Assert.Equal("Odczyt folderów SYSVOL: 3 z 8", GpoFindingText.Stage(l, new GpoDiagnosticsProgress(GpoDiagnosticsStage.Folders, 3, 8)));
        Assert.Equal("Hasło zapisane w preferencjach (MS14-025)", l[GpoFindingText.TitleKey(GpoFindingCodes.GppPassword)]);
        Assert.Equal("ustaw stan obiektu GPO na „Włączone”",
            GpoFindingText.Fix(l, new GpoQuickFix.SetStatus(Guid.NewGuid(), Core.GroupPolicy.Management.GpoStatus.AllSettingsEnabled), s => s));
        Assert.Equal("nadaj uprawnienie „Odczyt” dla: Użytkownicy uwierzytelnieni",
            GpoFindingText.Fix(l, new GpoQuickFix.GrantRead(Guid.NewGuid(), "S-1-5-11"), _ => "Użytkownicy uwierzytelnieni"));
    }

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex Placeholder();
}
