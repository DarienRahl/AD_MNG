using System.Collections;
using System.Globalization;
using System.Resources;
using AdMng.Core.Batch;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Localization;
using AdMng.Presentation.PropertySheets;

namespace AdMng.Presentation.Tests.Localization;

public class DialogLocalizationTests
{
    private static readonly ResourceManager Resources = new(ResxLocalizer.ResourceBaseName, typeof(ResxLocalizer).Assembly);

    [Fact]
    public void B2KeyFamilies_AreCompleteInBothLanguages()
    {
        var keys = new List<string>();
        keys.AddRange(Countries.All.Select(c => DisplayNames.CountryKey(c.Code)));
        keys.AddRange(Enum.GetValues<AttributeSyntax>().Select(DisplayNames.SyntaxKey));
        keys.AddRange(Enum.GetValues<ChangeAction>().Select(DisplayNames.ChangeActionKey));
        keys.AddRange(Enum.GetValues<ObjectResultStatus>().Select(DisplayNames.ObjectResultStatusKey));
        keys.AddRange(Enum.GetValues<OperationType>().Select(DisplayNames.OperationTypeKey));
        keys.AddRange(Enum.GetValues<OperationType>().Select(DisplayNames.OperationDescriptionKey));
        keys.AddRange(Enum.GetValues<PreviewFilter>().Select(DisplayNames.PreviewFilterKey));
        keys.AddRange(Enum.GetValues<ResultFilter>().Select(DisplayNames.ResultFilterKey));
        keys.AddRange(Enum.GetValues<FindKind>().Select(DisplayNames.FindKindKey));
        keys.AddRange(Enum.GetValues<FindOperator>().Select(DisplayNames.FindOperatorKey));
        keys.AddRange(DisplayNames.PluralKinds.Select(DisplayNames.KindPluralKey));
        keys.AddRange(DisplayNames.PropertySheetAttributes.Select(DisplayNames.AttributeKey));

        var en = Load(CultureInfo.InvariantCulture);
        var pl = Load(CultureInfo.GetCultureInfo("pl"));
        Assert.Equal([], keys.Where(k => !en.ContainsKey(k)));
        Assert.Equal([], keys.Where(k => !pl.ContainsKey(k)));
        Assert.Equal(249, Countries.All.Count);
        Assert.Equal(Countries.All.Count, Countries.All.Select(c => c.Code).Distinct().Count());
    }

    [Fact]
    public void Polish_UsesAducTerminology()
    {
        var l = new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL"));

        Assert.Equal("Właściwości: Jan Kowalski", l.Format(L.PropertySheet.WindowTitle, "Jan Kowalski"));
        Assert.Equal("Użytkownik musi zmienić hasło przy następnym logowaniu", l[L.Labels.MustChangePassword]);
        Assert.Equal("Nazwa logowania użytkownika", l[L.Labels.UserLogonName]);
        Assert.Equal("Grupa podstawowa", l[L.Labels.PrimaryGroup]);
        Assert.Equal("Polska", l[DisplayNames.CountryKey("pl")]);
        Assert.Equal("Niemcy", l[DisplayNames.CountryKey("DE")]);
        Assert.Equal("Ciąg Unicode", l[DisplayNames.SyntaxKey(AttributeSyntax.UnicodeString)]);
        Assert.Equal("Znajdź teraz", l[L.Find.FindNow]);
        Assert.Equal("Sprawdź nazwy", l[L.Picker.CheckNames]);
        Assert.Equal("Wybieranie: Grupy", l.Format(L.Picker.WindowTitle, l[DisplayNames.KindPluralKey(ObjectKind.Group)]));
    }

    [Fact]
    public void TemplateExamplesInResources_RenderWithSingleBraces()
    {
        var l = new ResxLocalizer(CultureInfo.GetCultureInfo("en-US"));

        var hint = l.Format(DisplayNames.OperationDescriptionKey(OperationType.SetAttribute));

        Assert.Contains("{givenName}.{sn}@firma.pl", hint, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> Load(CultureInfo culture)
    {
        var set = Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException($"No resources for '{culture.Name}'.");
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!, StringComparer.Ordinal);
    }
}
