using System.Globalization;
using System.Windows.Controls;
using AdMng.App.Localization;
using AdMng.App.UiTests.Infrastructure;
using AdMng.Presentation.Localization;

namespace AdMng.App.UiTests;

/// <summary>
/// Localized texts of bindings made once (styles and templates of the application resources) follow a language switch
/// and a host that replaces the localizer – each UI test builds its own host; a style first used under a Polish host kept
/// "Kopiuj: …" for every later English one.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class TranslationSourceTests
{
    [Fact]
    public Task ABindingMadeOnce_FollowsTheLanguage_AndAReplacedLocalizer() => WpfTestHost.RunAsync(() =>
    {
        var previous = TranslationSource.Localizer;
        try
        {
            var polish = new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL"));
            var english = new ResxLocalizer(CultureInfo.GetCultureInfo("en-US"));
            Assert.NotEqual(polish[L.Copy.CopyValue], english[L.Copy.CopyValue]);

            TranslationSource.Localizer = polish;
            var text = new TextBlock();
            text.SetBinding(TextBlock.TextProperty, new TrBinding(L.Copy.CopyValue));
            Assert.Equal(polish[L.Copy.CopyValue], text.Text);

            // Another host: the same binding reads from its localizer.
            TranslationSource.Localizer = english;
            Assert.Equal(english[L.Copy.CopyValue], text.Text);

            // A language switch of the current localizer.
            english.SetCulture(CultureInfo.GetCultureInfo("pl-PL"));
            Assert.Equal(polish[L.Copy.CopyValue], text.Text);
            english.SetCulture(CultureInfo.GetCultureInfo("en-US"));
            Assert.Equal(english[L.Copy.CopyValue], text.Text);
            Assert.Equal("en-US", TranslationSource.Texts.Culture.Name);
        }
        finally
        {
            TranslationSource.Localizer = previous;
        }

        return Task.CompletedTask;
    });
}
