using System.Windows;
using System.Windows.Controls;
using AdMng.App.Infrastructure;
using AdMng.App.Pki;
using AdMng.App.Services;
using AdMng.App.UiTests.Infrastructure;
using AdMng.App.Views;
using AdMng.Core.Pki.Tls;
using AdMng.Core.Schema;
using AdMng.Directory.Pki;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Pki;
using AdMng.Presentation.PropertySheets;
using Microsoft.Extensions.DependencyInjection;

namespace AdMng.App.UiTests.Pki;

/// <summary>
/// The three read-only property-sheet tabs of P1 (docs/dev/p-certificate-services.md 2.6, 2.10) in the real
/// <see cref="PropertiesWindow"/> templates on the PKI sample (seeded by the test – <c>UiTestContext</c> seeds none): every
/// list, the details, the warnings and the buttons render without binding errors in both languages; the attribute
/// editor shows the mask for <c>userPKCS12</c>; the App registers the <c>SslStreamTlsProbe</c>.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class CertificateTabsUiTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("pl")]
    public Task PropertySheet_OfAUser_RendersTheThreeCertificateTabs(string language) => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync(language);
        context.Directory.AddPublicKeySample(context.Company);
        await context.ConnectAsync();
        var window = await OpenSheetAsync(context, context.Company.JanKowalski);
        var sheet = window.ViewModel;

        var published = await ShowAsync<PublishedCertificatesPageViewModel>(window);
        await UiWait.UntilAsync(() => !published.IsLoading && published.Rows.Count == 1, "the published certificate");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Single(List(window, "PublishedCertificateList").Items);
        Assert.NotEmpty(Assert.Single(UiWait.Descendants<ItemsControl>(window), c => c.Name == "CertificateDetails" && c.IsVisible).Items);
        Assert.True(Assert.Single(UiWait.Descendants<Button>(window), b => b.Name == "CopyCertificateToFileButton").IsEnabled);
        Assert.Contains(UiWait.Descendants<TextBox>(window), t => t.IsVisible && t.Text == "jan.kowalski@contoso.test");

        var mappings = await ShowAsync<NameMappingsPageViewModel>(window);
        Assert.Single(mappings.X509Mappings);
        Assert.Single(List(window, "X509MappingList").Items);
        Assert.Empty(List(window, "KerberosNameList").Items);

        var keys = await ShowAsync<KeyCredentialsPageViewModel>(window);
        await UiWait.UntilAsync(() => !keys.IsLoading && keys.Rows.Count == 1, "the key credential");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Single(List(window, "KeyCredentialList").Items);

        Assert.False(sheet.HasChanges);
        context.AssertHealthy();
    });

    [Fact]
    public Task PropertySheets_OfAContactAndAWeaklyMappedUser_ShowTheirTabs() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync("pl");
        var sample = context.Directory.AddPublicKeySample(context.Company);
        await context.ConnectAsync();

        var contact = await OpenSheetAsync(context, sample.Contact!);
        Assert.DoesNotContain(contact.ViewModel.Pages, p => p is NameMappingsPageViewModel or KeyCredentialsPageViewModel);
        var published = await ShowAsync<PublishedCertificatesPageViewModel>(contact);
        await UiWait.UntilAsync(() => !published.IsLoading && published.Rows.Count == 1, "the contact's certificate");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Single(List(contact, "PublishedCertificateList").Items);
        contact.Close();

        // Łukasz: one weak (RFC 822) mapping – the KB5014754 warning is shown.
        var lukasz = await OpenSheetAsync(context, sample.WeakAccount!);
        var mappings = await ShowAsync<NameMappingsPageViewModel>(lukasz);
        Assert.NotEmpty(mappings.WeakWarning);
        var warning = Assert.Single(UiWait.Descendants<TextBlock>(lukasz), t => t.Name == "WeakMappingsWarning");
        Assert.True(warning.IsVisible);
        Assert.Equal(mappings.WeakWarning, warning.Text);

        // The empty tabs say so.
        var keys = await ShowAsync<KeyCredentialsPageViewModel>(lukasz);
        await UiWait.UntilAsync(() => !keys.IsLoading && keys.IsEmpty, "no key credentials");
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Contains(UiWait.Descendants<TextBlock>(lukasz), t => t.IsVisible && t.Text == keys.CountText);
        context.AssertHealthy();
    });

    [Fact]
    public Task AttributeEditor_ShowsTheMask_ForKeyMaterial_AndOffersNoEditOrCopy() => WpfTestHost.RunAsync(async () =>
    {
        await using var context = await UiTestContext.CreateAsync("pl");
        context.Directory.AddPublicKeySample(context.Company);
        await context.ConnectAsync();
        var window = await OpenSheetAsync(context, context.Company.JanKowalski);

        var editor = await ShowAsync<AttributeEditorPageViewModel>(window);
        await UiWait.UntilAsync(() => !editor.IsLoading && editor.Rows.Count > 0, "the attributes");
        editor.FilterText = "userPKCS12";
        await UiWait.UntilAsync(() => editor.Rows.Count == 1, "the filtered row");
        var row = editor.Rows[0];
        editor.SelectedRow = row;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);

        Assert.True(row.IsMasked);
        Assert.Contains(UiWait.Descendants<TextBlock>(window), t => t.IsVisible && t.Text == SecretAttributes.Mask);
        var localizer = context.Services.GetRequiredService<ILocalizer>();
        var buttons = UiWait.Descendants<Button>(window).Where(b => b.IsVisible).ToList();
        Assert.False(Assert.Single(buttons, b => Equals(b.Content, localizer[L.Common.Edit])).IsEnabled);
        Assert.False(Assert.Single(buttons, b => Equals(b.Content, localizer[L.Common.Copy])).IsEnabled);
        Assert.Null(editor.Editor);
        context.AssertHealthy();
    });

    [Fact]
    public void PkiAppRegistration_RegistersTheSslStreamProbe_Once()
    {
        var services = new ServiceCollection();
        services.AddAdMngPkiApp();
        using var provider = services.BuildServiceProvider();

        var probe = Assert.IsType<SslStreamTlsProbe>(provider.GetRequiredService<IDomainControllerTlsProbe>());
        Assert.Same(probe, provider.GetRequiredService<IDomainControllerTlsProbe>());
        Assert.Single(services, d => d.ServiceType == typeof(IDomainControllerTlsProbe) && d.Lifetime == ServiceLifetime.Singleton);
    }

    private static async Task<PropertiesWindow> OpenSheetAsync(UiTestContext context, string distinguishedName)
    {
        var factory = context.Services.GetRequiredService<IDialogViewModelFactory>();
        var window = (PropertiesWindow)context.Services.GetRequiredService<IWindowService>().CreateWindow(factory.CreatePropertySheet(distinguishedName));
        UiTestSupport.ShowOffScreen(window);
        await UiWait.InitializedAsync(window.Initialization);
        await UiTestSupport.WaitForIdleAsync();
        Assert.Null(window.ViewModel.LoadError);
        return window;
    }

    private static async Task<T> ShowAsync<T>(PropertiesWindow window)
        where T : PropertyPageViewModel
    {
        var page = Assert.Single(window.ViewModel.Pages.OfType<T>());
        window.ViewModel.SelectedPage = page;
        await UiTestSupport.WaitForIdleAsync(rounds: 2);
        Assert.Same(page, window.PageTabs.SelectedItem);
        return page;
    }

    private static ListView List(DependencyObject window, string name) =>
        Assert.Single(UiWait.Descendants<ListView>(window), l => l.Name == name && l.IsVisible);
}
