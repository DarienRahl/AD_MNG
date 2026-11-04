using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Views;

namespace AdMng.Presentation.Tests.Localization;

public partial class LocalizationTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly ResourceManager Resources = new(ResxLocalizer.ResourceBaseName, typeof(ResxLocalizer).Assembly);

    private static Dictionary<string, string> Neutral => Load(CultureInfo.InvariantCulture);

    private static Dictionary<string, string> PolishEntries => Load(CultureInfo.GetCultureInfo("pl"));

    [Fact]
    public void ResourceFiles_HaveIdenticalKeySets()
    {
        var en = Neutral.Keys.ToHashSet(StringComparer.Ordinal);
        var pl = PolishEntries.Keys.ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(en);
        Assert.Empty(en.Except(pl));
        Assert.Empty(pl.Except(en));
    }

    [Fact]
    public void EveryKeyOfL_ExistsInBothLanguages()
    {
        var keys = typeof(L).GetNestedTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.True(keys.Count > 200);
        var en = Neutral;
        var pl = PolishEntries;
        Assert.Equal([], keys.Where(k => !en.ContainsKey(k)));
        Assert.Equal([], keys.Where(k => !pl.ContainsKey(k)));
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void DynamicKeyFamilies_AreComplete()
    {
        var keys = new List<string>();
        keys.AddRange(Enum.GetValues<ObjectKind>().Select(DisplayNames.ObjectKindKey));
        keys.AddRange(Enum.GetValues<DirectoryErrorKind>().Select(DisplayNames.ErrorKindKey));
        keys.AddRange(typeof(IssueCodes).GetFields().Where(f => f.IsLiteral).Select(f => DisplayNames.IssueKey((string)f.GetRawConstantValue()!)));
        keys.AddRange(Enum.GetValues<IssueSeverity>().Select(DisplayNames.IssueSeverityKey));
        keys.AddRange(DisplayNames.UacFlags.Select(DisplayNames.UacFlagKey));
        keys.AddRange(Enum.GetValues<GroupScope>().Select(DisplayNames.GroupScopeKey));
        keys.AddRange(DisplayNames.NamedGroupScopes.SelectMany(s => new[] { DisplayNames.GroupTypeKey(true, s), DisplayNames.GroupTypeKey(false, s) }));
        keys.AddRange(DisplayNames.CommonAttributes.Select(DisplayNames.AttributeKey));
        keys.AddRange(Enum.GetValues<ThemePreference>().Select(DisplayNames.ThemeKey));
        keys.AddRange(Enum.GetValues<AuthenticationMode>().Select(DisplayNames.AuthenticationKey));
        keys.AddRange(Enum.GetValues<TransportSecurity>().Select(DisplayNames.SecurityKey));
        keys.AddRange(Enum.GetValues<SearchScope>().Select(DisplayNames.ScopeKey));
        keys.AddRange(Enum.GetValues<QuickFilterKind>().Select(DisplayNames.ChipKey));
        keys.AddRange(Enum.GetValues<BuiltInViewKind>().Select(DisplayNames.BuiltInViewKey));

        var en = Neutral;
        var pl = PolishEntries;
        Assert.Equal([], keys.Where(k => !en.ContainsKey(k)));
        Assert.Equal([], keys.Where(k => !pl.ContainsKey(k)));
    }

    [Fact]
    public void FormatPlaceholders_MatchBetweenLanguages()
    {
        var en = Neutral;
        var pl = PolishEntries;
        var mismatches = en.Keys
            .Where(k => !Placeholders(en[k]).SetEquals(Placeholders(pl[k])))
            .ToList();

        Assert.Empty(mismatches);
        foreach (var value in en.Values.Concat(pl.Values))
        {
            // Every value must be a valid composite format string (no stray braces).
            _ = string.Format(CultureInfo.InvariantCulture, value, "a", "b", "c", "d", "e", "f", "g", "h");
        }
    }

    [Fact]
    public void Polish_UsesAducTerminologyWithDiacritics()
    {
        var l = new ResxLocalizer(Polish);

        Assert.Equal("Użytkownicy i komputery usługi Active Directory", l[L.App.Title]);
        Assert.Equal("Właściwości", l[L.Commands.Properties]);
        Assert.Equal("Resetuj hasło...", l[L.Commands.ResetPassword]);
        Assert.Equal("Przenieś...", l[L.Commands.Move]);
        Assert.Equal("Odśwież", l[L.Commands.Refresh]);
        Assert.Equal("Wyłącz konto", l[L.Commands.Disable]);
        Assert.Equal("Włącz konto", l[L.Commands.Enable]);
        Assert.Equal("Odblokuj konto", l[L.Commands.Unlock]);
        Assert.Equal("Dodaj do grupy...", l[L.Commands.AddToGroup]);
        Assert.Equal("Nowy", l[L.Shell.MenuNew]);
        Assert.Equal("Znajdź...", l[L.Commands.Find]);
        Assert.Equal("Jednostka organizacyjna", l.ObjectKindName(ObjectKind.OrganizationalUnit));
        Assert.Equal("Grupa zabezpieczeń - globalna", l.GroupTypeName(GroupTypes.Compose(GroupScope.Global, security: true)));
        Assert.Equal("Funkcje zaawansowane", l[L.Commands.ToggleAdvancedFeatures]);
    }

    [Fact]
    public void MissingKey_IsShownInBrackets()
    {
        var l = new ResxLocalizer(CultureInfo.GetCultureInfo("en-US"));

        Assert.Equal("[No.Such.Key]", l["No.Such.Key"]);
        Assert.False(l.Contains("No.Such.Key"));
        Assert.True(l.Contains(L.Commands.Refresh));
    }

    [Fact]
    public void SetCulture_SwitchesLanguage_AndRaisesIndexerChange()
    {
        var l = new ResxLocalizer(CultureInfo.GetCultureInfo("en-US"));
        var raised = new List<string?>();
        var cultureChanged = 0;
        ((INotifyPropertyChanged)l).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        l.CultureChanged += (_, _) => cultureChanged++;
        Assert.Equal("Refresh", l[L.Commands.Refresh]);

        l.SetCulture(Polish);
        l.SetCulture(Polish);

        Assert.Equal("Odśwież", l[L.Commands.Refresh]);
        Assert.Contains("Item[]", raised);
        Assert.Equal(1, cultureChanged);
        Assert.Equal("Obiektów: 3 · 0,17 s · stan na 14:35:02", l.Format(L.List.LoadSummary, 3, 0.17.ToString("0.00", Polish), "14:35:02"));
    }

    [Theory]
    [InlineData("", "pl-PL", "pl-PL")]
    [InlineData("", "de-DE", "en-US")]
    [InlineData("en", "pl-PL", "en-US")]
    [InlineData("pl", "en-US", "pl-PL")]
    public void ResolveCulture_MapsSettingToSupportedCulture(string language, string system, string expected) =>
        Assert.Equal(expected, ResxLocalizer.ResolveCulture(language, CultureInfo.GetCultureInfo(system)).Name);

    [Fact]
    public void AttributeName_PrefersDisplaySpecifiers_ThenLocalizedFallback_ThenLdapName()
    {
        var l = new ResxLocalizer(Polish);
        var schema = TestDoubles.TestSchema.Create();

        Assert.Equal("Dział", l.AttributeName("department", schema));
        Assert.Equal("Nazwa logowania użytkownika (sprzed systemu Windows 2000)", l.AttributeName("sAMAccountName", schema));
        Assert.Equal("extensionAttribute7", l.AttributeName("extensionAttribute7", schema));
    }

    [Fact]
    public void IssueText_UsesAttributeDisplayName_AndFallsBackForUnknownCodes()
    {
        var l = new ResxLocalizer(CultureInfo.GetCultureInfo("en-US"));
        var schema = TestDoubles.TestSchema.Create();

        Assert.Equal("You do not have permission to change 'Department'.",
            l.IssueText(new ValidationIssue(IssueSeverity.Error, IssueCodes.NotWritable, "detail", "department"), schema));
        Assert.Equal("planner detail", l.IssueText(new ValidationIssue(IssueSeverity.Error, "FutureCode", "planner detail")));
        Assert.Equal("planner detail", l.IssueText(new ValidationIssue(IssueSeverity.Error, IssueCodes.NotWritable, "planner detail")));
        Assert.Equal("Object not found.", l.IssueText(new ValidationIssue(IssueSeverity.Error, IssueCodes.NotFound, "detail")));
    }

    [Fact]
    public void IssueText_FillsThePlaceholdersFromTheIssuesArguments()
    {
        var l = new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL"));

        var password = l.IssueText(new ValidationIssue(IssueSeverity.Warning, IssueCodes.GpoGppPassword, "detail") { Arguments = ["Machine/Preferences/Groups/Groups.xml"] });
        var domain = l.IssueText(new ValidationIssue(IssueSeverity.Error, IssueCodes.GpoBackupOtherDomain, "detail") { Arguments = ["wrocław.example"] });
        var filter = l.IssueText(new ValidationIssue(IssueSeverity.Warning, IssueCodes.GpoWmiFilterMissing, "detail", "gPCWQLFilter"));

        Assert.Contains("Machine/Preferences/Groups/Groups.xml", password, StringComparison.Ordinal);
        Assert.StartsWith("Ustawienia zawierają hasła", password, StringComparison.Ordinal);
        Assert.Contains("z domeny wrocław.example", domain, StringComparison.Ordinal);
        Assert.Contains("bez filtru WMI", filter, StringComparison.Ordinal);
    }

    private static HashSet<string> Placeholders(string value) =>
        PlaceholderRegex().Matches(value).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, string> Load(CultureInfo culture)
    {
        var set = Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException($"No resources for '{culture.Name}'.");
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!, StringComparer.Ordinal);
    }

    [GeneratedRegex(@"\{\d+[^}]*\}")]
    private static partial Regex PlaceholderRegex();
}
