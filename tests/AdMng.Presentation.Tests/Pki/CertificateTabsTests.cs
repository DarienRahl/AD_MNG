using System.Formats.Asn1;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using AdMng.Core.Directory;
using AdMng.Core.Pki;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Pki;
using AdMng.Presentation.PropertySheets;
using AdMng.Presentation.Services;
using AdMng.Presentation.Tests.Dialogs;
using AdMng.Testing;
using AdMng.Testing.Pki;

namespace AdMng.Presentation.Tests.Pki;

/// <summary>
/// The read-only property-sheet tabs of P1 (docs/dev/p-certificate-services.md 2.6, 2.10) on the Polish PKI sample:
/// <i>Opublikowane certyfikaty</i>, <i>Mapowania nazw</i> with the KB5014754 strength and <i>Poświadczenia kluczy</i>, through
/// <c>Factory.CreatePropertySheet(dn)</c> + <c>ActivateAsync</c>; read lazily, nothing written, no key material exposed.
/// </summary>
public sealed class CertificateTabsTests
{
    // ---- Opublikowane certyfikaty ---------------------------------------------------------------------------------------

    [Fact]
    public async Task PublishedCertificates_AreReadWhenTheTabIsShown_WithTheirColumnsInPolish()
    {
        var harness = await PkiHarness.CreateAsync();
        var jan = harness.Company.JanKowalski;
        var (sheet, page) = await OpenAsync<PublishedCertificatesPageViewModel>(harness, jan, activate: false);

        // Lazy: the sheet's own read does not carry the certificates; the tab reads them when shown.
        Assert.Empty(page.Rows);
        Assert.DoesNotContain(harness.Session.Operations, o => o.Kind == FakeOperationKind.Get && o.Attributes.Contains(PkiAttributes.UserCertificate));
        Assert.Equal(string.Empty, page.CountText);
        await page.ActivateAsync();

        Assert.Equal("Opublikowane certyfikaty", page.Title);
        Assert.False(page.IsLoading);
        Assert.Null(page.Error);
        var row = Assert.Single(page.Rows);
        Assert.Same(row, page.SelectedRow);
        Assert.Equal("Jan Kowalski", row.IssuedTo);
        Assert.Equal(PkiSamples.IssuingCaName, row.IssuedBy);
        Assert.Equal("Encrypting File System, Secure Email, Client Authentication", row.Purposes);
        Assert.Equal("30.10.2026", row.ValidTo);
        Assert.Equal(CertificateValidity.Expiring, row.Validity);
        Assert.Equal(16, row.DaysLeft);
        Assert.Equal("Wygasa (pozostało dni: 16)", row.ValidityText);
        Assert.True(row.IsExpiring);

        // The v1 template name extension resolved to the template's display name by one search of CN=Certificate Templates.
        Assert.Equal("Użytkownik", row.Template);
        var der = harness.Session.Snapshot(jan)!.Attributes[PkiAttributes.UserCertificate].OfType<byte[]>().Single();
        Assert.Equal(Convert.ToHexString(SHA1.HashData(der)), row.Thumbprint);
        Assert.Equal(PkiAttributes.UserCertificate, row.Attribute);
        Assert.Equal("Opublikowane certyfikaty: 1", page.CountText);
        Assert.False(page.IsEmpty);

        var details = page.Details.ToDictionary(d => d.Label, d => d.Value);
        Assert.Equal("jan.kowalski@contoso.test", details["Główna nazwa użytkownika (SAN)"]);
        Assert.Equal(SidOf(harness, jan), details["SID (rozszerzenie zabezpieczeń)"]);
        Assert.Equal(SidOf(harness, jan), details["SID (adres URL w SAN, silne mapowanie)"]);
        Assert.Equal("6300", details["Numer seryjny"]);
        Assert.Equal("Użytkownik", details["Szablon"]);
        Assert.Contains("Client Authentication (1.3.6.1.5.5.7.3.2)", details["Przeznaczenie"], StringComparison.Ordinal);

        // Read only: nothing to apply, nothing written.
        Assert.False(sheet.HasChanges);
        Assert.DoesNotContain(harness.Session.Operations, o => o.IsWrite);
    }

    [Fact]
    public async Task AnExpiredCertificate_AndAComputersKdcCertificate_ShowTheirStateAndTemplate()
    {
        var harness = await PkiHarness.CreateAsync(CultureInfo.GetCultureInfo("en-US"));

        var (_, grazyna) = await OpenAsync<PublishedCertificatesPageViewModel>(harness, harness.Company.GrazynaDabrowska);
        var expired = Assert.Single(grazyna.Rows);
        Assert.Equal("Grażyna Dąbrowska", expired.IssuedTo);
        Assert.Equal(CertificateValidity.Expired, expired.Validity);
        Assert.Equal("Expired", expired.ValidityText);
        Assert.True(expired.IsExpired);

        // The DC's KDC certificate names its template by OID (template information extension): resolved by msPKI-Cert-Template-OID.
        var (_, dc) = await OpenAsync<PublishedCertificatesPageViewModel>(harness, harness.Builder.DomainControllerDn);
        var kdc = Assert.Single(dc.Rows);
        Assert.Equal(PkiSamples.DcHost, kdc.IssuedTo);
        Assert.Equal("Uwierzytelnianie Kerberos", kdc.Template);
        Assert.Equal(CertificateValidity.Valid, kdc.Validity);
        Assert.Contains("KDC Authentication", kdc.Purposes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheExpiryThreshold_ComesFromTheSettings()
    {
        var harness = await PkiHarness.CreateAsync(CultureInfo.GetCultureInfo("en-US"));
        await harness.B2.Settings.UpdateAsync(s => s.Pki.CertificateExpiryWarningDays = 10, TestContext.Current.CancellationToken);

        var (_, page) = await OpenAsync<PublishedCertificatesPageViewModel>(harness, harness.Company.JanKowalski);

        Assert.Equal(CertificateValidity.Valid, Assert.Single(page.Rows).Validity);
        Assert.Equal("Valid", page.Rows[0].ValidityText);
    }

    [Fact]
    public async Task AContactsCertificate_WithoutATemplate_IsShown()
    {
        var harness = await PkiHarness.CreateAsync();

        var (sheet, page) = await OpenAsync<PublishedCertificatesPageViewModel>(harness, harness.Sample.Contact!);

        Assert.DoesNotContain(sheet.Pages, p => p is NameMappingsPageViewModel or KeyCredentialsPageViewModel);
        var row = Assert.Single(page.Rows);
        Assert.Equal("Zofia Żurawska", row.IssuedTo);
        Assert.Equal("Secure Email", row.Purposes);
        Assert.Equal(string.Empty, row.Template);
        Assert.Contains(page.Details, d => d.Value == "zofia.zurawska@partner.example");
    }

    [Fact]
    public async Task AnSmimeBundle_IsUnwrapped_ToItsEndEntityCertificates()
    {
        var harness = await PkiHarness.CreateAsync(CultureInfo.GetCultureInfo("en-US"));
        var jan = harness.Company.JanKowalski;
        var smime = PkiSamples.IssueDer(new PkiCertificateOptions
        {
            CommonName = "Jan Kowalski (S/MIME)",
            SerialNumber = 0x6310,
            NotBefore = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero),
            NotAfter = new(2027, 1, 1, 8, 0, 0, TimeSpan.Zero),
            Ekus = [PkiSamples.SecureEmail],
            Email = "jan.kowalski@contoso.test",
            Issuer = PkiSamples.IssuingCa,
        });
        harness.Session.SetValues(jan, PkiAttributes.UserSmimeCertificate, SignedData(smime, PkiSamples.IssuingCaDer));

        var (_, page) = await OpenAsync<PublishedCertificatesPageViewModel>(harness, jan);

        Assert.Equal([PkiAttributes.UserCertificate, PkiAttributes.UserSmimeCertificate], page.Rows.Select(r => r.Attribute));
        var bundled = page.Rows[1];
        Assert.Equal("Jan Kowalski (S/MIME)", bundled.IssuedTo);
        Assert.Equal(Convert.ToHexString(SHA1.HashData(smime)), bundled.Thumbprint);
        Assert.Equal("Published certificates: 2", page.CountText);
    }

    [Fact]
    public async Task UnreadableValues_AreRows_ThePlaceholderIsNot_AndNoCertificateMeansEmpty()
    {
        var harness = await PkiHarness.CreateAsync();
        var lukasz = harness.Company.LukaszWisniewski;
        harness.Session.SetValues(lukasz, PkiAttributes.UserCertificate, PkiSamples.MalformedDer, new byte[] { 0x00 });

        var (_, page) = await OpenAsync<PublishedCertificatesPageViewModel>(harness, lukasz);

        var row = Assert.Single(page.Rows);
        Assert.True(row.IsUnreadable);
        Assert.Equal("Nieczytelna wartość (to nie jest certyfikat)", row.IssuedTo);
        Assert.Null(row.Validity);
        Assert.False(page.CopyToFileCommand.CanExecute(null));
        Assert.False(page.CopyThumbprintCommand.CanExecute(null));

        var (_, empty) = await OpenAsync<PublishedCertificatesPageViewModel>(harness, harness.Company.PawelLewandowski);
        Assert.Empty(empty.Rows);
        Assert.True(empty.IsEmpty);
        Assert.Equal("Na tym obiekcie nie opublikowano żadnego certyfikatu.", empty.CountText);
        Assert.Null(empty.SelectedRow);
    }

    [Fact]
    public async Task CopyToFile_WritesTheDer_OnlyToTheFileTheOperatorPicked()
    {
        var harness = await PkiHarness.CreateAsync();
        var jan = harness.Company.JanKowalski;
        var (_, page) = await OpenAsync<PublishedCertificatesPageViewModel>(harness, jan);
        var der = harness.Session.Snapshot(jan)!.Attributes[PkiAttributes.UserCertificate].OfType<byte[]>().Single();

        // Cancelled dialog: nothing is written.
        harness.B2.Files.NextSavePath = null;
        await page.CopyToFileCommand.ExecuteAsync(null);
        Assert.Equal("Jan Kowalski.cer", harness.B2.Files.LastDefaultFileName);
        Assert.Empty(harness.B2.Notifications.Items);

        var path = Path.Combine(Path.GetTempPath(), "admng-w7-" + Guid.NewGuid().ToString("N") + ".cer");
        try
        {
            harness.B2.Files.NextSavePath = path;
            await page.CopyToFileCommand.ExecuteAsync(null);

            Assert.Equal(der, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            var toast = Assert.Single(harness.B2.Notifications.Items);
            Assert.Equal(NotificationSeverity.Success, toast.Severity);
            Assert.Contains(Path.GetFileName(path), toast.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }

        page.CopyThumbprintCommand.Execute(null);
        Assert.Equal(page.Rows[0].Thumbprint, harness.B2.Clipboard.Text);
    }

    [Fact]
    public async Task ARefreshOfTheSheet_RereadsAnActivatedTab_AndAReadFailureIsAnError()
    {
        var harness = await PkiHarness.CreateAsync(CultureInfo.GetCultureInfo("en-US"));
        var jan = harness.Company.JanKowalski;
        var (sheet, page) = await OpenAsync<PublishedCertificatesPageViewModel>(harness, jan);
        Assert.Single(page.Rows);

        harness.Session.SetValues(jan, PkiAttributes.UserCertificate);
        await sheet.RefreshCommand.ExecuteAsync(null);
        await B2Harness.WaitUntilAsync(() => !page.IsLoading);
        Assert.Empty(page.Rows);

        harness.Session.FailNext(FakeOperationKind.Get, jan, DirectoryErrorKind.AccessDenied);
        await page.RefreshCommand.ExecuteAsync(null);
        Assert.NotNull(page.Error);
        Assert.False(page.IsLoading);
    }

    [Fact]
    public async Task ATemplateSearchThatFails_LeavesTheNameTheCertificateCarries()
    {
        var harness = await PkiHarness.CreateAsync();
        var templates = "CN=Certificate Templates," + PkiSamples.PublicKeyServicesDn;
        harness.Session.FailNext(FakeOperationKind.Search, templates, DirectoryErrorKind.AccessDenied);

        var (_, page) = await OpenAsync<PublishedCertificatesPageViewModel>(harness, harness.Company.JanKowalski);

        Assert.Null(page.Error);
        Assert.Equal("User", Assert.Single(page.Rows).Template);
    }

    // ---- Mapowania nazw -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task NameMappings_ShowTheKb5014754Strength_AndKerberosNamesApart()
    {
        var harness = await PkiHarness.CreateAsync();

        var (_, jan) = await OpenAsync<NameMappingsPageViewModel>(harness, harness.Company.JanKowalski, activate: false);
        Assert.Equal("Mapowania nazw", jan.Title);
        var strong = Assert.Single(jan.X509Mappings);
        Assert.Equal(AltSecurityIdentityKind.X509IssuerSerial, strong.Kind);
        Assert.Equal("Wystawca i numer seryjny", strong.KindText);
        Assert.Equal("Silne", strong.StrengthText);
        Assert.True(strong.IsStrong);
        Assert.Equal("C=PL,O=Kontoso Sp. z o.o.,CN=Kontoso Wystawiający CA", strong.Issuer);
        Assert.Equal("Numer seryjny 6300", strong.SubjectOrIdentifier);
        Assert.Empty(jan.KerberosNames);
        Assert.Equal(0, jan.WeakCount);
        Assert.Equal(string.Empty, jan.WeakWarning);

        var (_, lukasz) = await OpenAsync<NameMappingsPageViewModel>(harness, harness.Company.LukaszWisniewski, activate: false);
        var weak = Assert.Single(lukasz.X509Mappings);
        Assert.Equal("Słabe", weak.StrengthText);
        Assert.Equal("lukasz.wisniewski@contoso.test", weak.SubjectOrIdentifier);
        Assert.StartsWith("Słabe mapowania: 1.", lukasz.WeakWarning, StringComparison.Ordinal);

        var (_, pawel) = await OpenAsync<NameMappingsPageViewModel>(harness, harness.Company.PawelLewandowski, activate: false);
        Assert.False(pawel.HasX509Mappings);
        var kerberos = Assert.Single(pawel.KerberosNames);
        Assert.Equal("pawel.lewandowski@CONTOSO.TEST", kerberos.SubjectOrIdentifier);
        Assert.Equal("Nie jest mapowaniem certyfikatu", kerberos.StrengthText);
        Assert.Equal(string.Empty, pawel.WeakWarning);

        var (_, administrator) = await OpenAsync<NameMappingsPageViewModel>(harness, harness.Builder.AdministratorDn, activate: false);
        var issuerSubject = Assert.Single(administrator.X509Mappings);
        Assert.Equal("Wystawca i podmiot", issuerSubject.KindText);
        Assert.Equal("DC=test,DC=contoso,CN=Users,CN=Administrator", issuerSubject.SubjectOrIdentifier);
    }

    [Fact]
    public async Task InvalidMappings_AreCounted_AndTheValueIsCopied()
    {
        var harness = await PkiHarness.CreateAsync(CultureInfo.GetCultureInfo("en-US"));
        var grazyna = harness.Company.GrazynaDabrowska;
        harness.Session.SetValues(grazyna, PkiAttributes.AltSecurityIdentities,
            "X509:<SR>0063", "X509:<SKI>0A1B2C3D", "Kerberos:grazyna@CONTOSO.TEST");

        var (sheet, page) = await OpenAsync<NameMappingsPageViewModel>(harness, grazyna, activate: false);

        Assert.Equal([MappingStrength.Invalid, MappingStrength.Strong], page.X509Mappings.Select(m => m.Strength));
        Assert.True(page.X509Mappings[0].IsInvalid);
        Assert.Equal("0A1B2C3D", page.X509Mappings[1].SubjectOrIdentifier);
        Assert.Equal("Invalid values: 1 (the KDC ignores them).", page.InvalidWarning);
        Assert.Single(page.KerberosNames);

        Assert.False(page.CopyValueCommand.CanExecute(null));
        page.SelectedMapping = page.X509Mappings[1];
        page.CopyValueCommand.Execute(null);
        Assert.Equal("X509:<SKI>0A1B2C3D", harness.B2.Clipboard.Text);
        page.CopyValueCommand.Execute(page.KerberosNames[0]);
        Assert.Equal("Kerberos:grazyna@CONTOSO.TEST", harness.B2.Clipboard.Text);

        // One selection across the two lists: "Kopiuj" copies the row selected last.
        page.SelectedKerberosName = page.KerberosNames[0];
        Assert.Null(page.SelectedMapping);
        page.CopyValueCommand.Execute(null);
        Assert.Equal("Kerberos:grazyna@CONTOSO.TEST", harness.B2.Clipboard.Text);
        page.SelectedMapping = page.X509Mappings[0];
        Assert.Null(page.SelectedKerberosName);
        page.CopyValueCommand.Execute(null);
        Assert.Equal("X509:<SR>0063", harness.B2.Clipboard.Text);

        // The mappings come with the sheet's read, and a refresh replaces them.
        harness.Session.SetValues(grazyna, PkiAttributes.AltSecurityIdentities, "X509:<RFC822>grazyna@contoso.test");
        await sheet.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(MappingStrength.Weak, Assert.Single(page.X509Mappings).Strength);
        Assert.Empty(page.KerberosNames);
        Assert.False(sheet.HasChanges);
    }

    // ---- Poświadczenia kluczy -------------------------------------------------------------------------------------------

    [Fact]
    public async Task KeyCredentials_ShowTheMetadata_AreReadLazily_AndNeverTheKey()
    {
        var harness = await PkiHarness.CreateAsync();
        var jan = harness.Company.JanKowalski;
        var (_, page) = await OpenAsync<KeyCredentialsPageViewModel>(harness, jan, activate: false);
        Assert.Empty(page.Rows);
        Assert.DoesNotContain(harness.Session.Operations, o => o.Kind == FakeOperationKind.Get && o.Attributes.Contains(PkiAttributes.KeyCredentialLink));

        await page.ActivateAsync();

        Assert.Equal("Poświadczenia kluczy", page.Title);
        var row = Assert.Single(page.Rows);
        Assert.Equal(Convert.ToBase64String("Jan-Klucz"u8.ToArray()), row.KeyId);
        Assert.Equal("6d0f7c2e-8a41-4b4b-9c3e-1f2a3b4c5d6e", row.DeviceId);
        Assert.Equal("Active Directory", row.SourceText);
        Assert.Equal("Windows Hello dla firm (NGC)", row.UsageText);
        Assert.Equal(Local(harness, new(2026, 5, 4, 7, 15, 0, TimeSpan.Zero)), row.Created);
        Assert.Equal(Local(harness, new(2026, 10, 12, 6, 50, 0, TimeSpan.Zero)), row.LastLogon);
        Assert.False(row.HasRemarks);
        Assert.Equal("Poświadczenia kluczy: 1", page.CountText);

        // Rule 2: no member of the row carries bytes, and its text is value-free.
        Assert.DoesNotContain(typeof(KeyCredentialRowViewModel).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            p => p.PropertyType == typeof(byte[]) || p.PropertyType == typeof(ReadOnlyMemory<byte>));
        Assert.Equal("KeyCredentialRowViewModel(ActiveDirectory Ngc)", row.ToString());

        page.SelectedRow = row;
        page.CopyKeyIdCommand.Execute(null);
        Assert.Equal(row.KeyId, harness.B2.Clipboard.Text);
        page.CopyDeviceIdCommand.Execute(null);
        Assert.Equal(row.DeviceId, harness.B2.Clipboard.Text);
    }

    [Fact]
    public async Task AComputersKeyCredential_AMalformedEntry_AndATamperedOne_AreShownAsSuch()
    {
        var harness = await PkiHarness.CreateAsync(CultureInfo.GetCultureInfo("en-US"));
        var laptop = harness.Company.Laptop;
        var good = PkiSamples.KeyCredentialLinkValue(laptop, [0x4B, 0x52, 0x4B, 0x2D, 0x4C, 0x54], new Guid("2b9e4f61-0c7d-4e8a-b1f2-3a4b5c6d7e8f"),
            new(2026, 4, 20, 9, 0, 0, TimeSpan.Zero));
        var fido = PkiSamples.KeyCredentialLinkValue(laptop, [0x46, 0x49, 0x44, 0x4F], new Guid("9c8b7a69-5847-4362-9150-a1b2c3d4e5f6"),
            new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero), usage: 0x07, source: 0x00);
        harness.Session.SetValues(laptop, PkiAttributes.KeyCredentialLink, good, Tampered(fido), "B:8:00010000:" + laptop);

        var (sheet, page) = await OpenAsync<KeyCredentialsPageViewModel>(harness, laptop);

        Assert.Contains(sheet.Pages, p => p is PublishedCertificatesPageViewModel);
        Assert.Equal(3, page.Rows.Count);
        Assert.Equal(string.Empty, page.Rows[0].LastLogon);
        Assert.False(page.Rows[0].HasRemarks);
        Assert.Equal("FIDO security key", page.Rows[1].UsageText);
        Assert.True(page.Rows[1].IsHashMismatch);
        Assert.Equal("The key hash does not match (the entry was changed)", page.Rows[1].Remarks);
        Assert.True(page.Rows[2].IsMalformed);
        Assert.Equal("Unreadable entry", page.Rows[2].Remarks);
        Assert.Equal(string.Empty, page.Rows[2].KeyId);
        Assert.False(page.CopyKeyIdCommand.CanExecute(null));

        var (_, empty) = await OpenAsync<KeyCredentialsPageViewModel>(harness, harness.Company.PawelLewandowski);
        Assert.True(empty.IsEmpty);
        Assert.Equal("No key credentials.", empty.CountText);
    }

    // ---- Every tab ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheTabs_AreReadOnly_AndTheirRowsPrintNoValue()
    {
        var harness = await PkiHarness.CreateAsync();
        var jan = harness.Company.JanKowalski;
        var sheet = harness.B2.Factory.CreatePropertySheet(jan);
        await sheet.InitializeAsync();
        var pages = sheet.Pages.Where(p => p is PublishedCertificatesPageViewModel or NameMappingsPageViewModel or KeyCredentialsPageViewModel).ToList();
        Assert.Equal(3, pages.Count);
        foreach (var page in pages)
        {
            await page.ActivateAsync();
            Assert.Empty(page.GetOperations());
            Assert.False(page.HasChanges);
            Assert.False(page.HasErrors);
            page.Revert();
        }

        Assert.False(sheet.HasChanges);
        Assert.DoesNotContain(harness.Session.Operations, o => o.IsWrite);

        var published = Assert.Single(((PublishedCertificatesPageViewModel)pages[0]).Rows);
        Assert.Equal("PublishedCertificateRowViewModel(Expiring)", published.ToString());
        Assert.DoesNotContain("Kowalski", published.ToString(), StringComparison.Ordinal);
        Assert.Equal("NameMappingRowViewModel(X509IssuerSerial Strong)", Assert.Single(((NameMappingsPageViewModel)pages[1]).X509Mappings).ToString());
        Assert.All(published.Details, d => Assert.Equal(d.Label, d.ToString()));
    }

    [Fact]
    public void TheDynamicKeyFamilies_ExistInBothLanguages()
    {
        var keys = L.Certificates.AllKeys().ToList();
        var en = new ResxLocalizer(CultureInfo.GetCultureInfo("en-US"));
        var pl = new ResxLocalizer(CultureInfo.GetCultureInfo("pl-PL"));

        Assert.Equal(Enum.GetValues<KeyCredentialSource>().Length + Enum.GetValues<KeyCredentialUsage>().Length + Enum.GetValues<CertificateValidity>().Length, keys.Count);
        Assert.All(keys, k =>
        {
            Assert.NotEqual($"[{k}]", en[k]);
            Assert.NotEqual($"[{k}]", pl[k]);
        });
        Assert.Equal("Wygasa (pozostało dni: 3)", pl.Format(L.Certificates.ValidityKey(CertificateValidity.Expiring), 3));
    }

    [Fact]
    public void ThePkcs7Reader_ToleratesWhatIsNotABundle()
    {
        Assert.Null(PublishedCertificates.Bundle(PkiSamples.MalformedDer));
        Assert.Null(PublishedCertificates.Bundle(PkiSamples.IssuingCaDer));
        Assert.Equal([], PublishedCertificates.Bundle(SignedData()));
        Assert.Empty(PublishedCertificates.Parse(PkiAttributes.UserCertificate, []));
        var unreadable = Assert.Single(PublishedCertificates.Parse(PkiAttributes.UserSmimeCertificate, SignedData()));
        Assert.Null(unreadable.Summary);

        // A bundle of CA certificates only shows them (nothing else to show).
        var cas = PublishedCertificates.Parse(PkiAttributes.UserSmimeCertificate, SignedData(PkiSamples.IssuingCaDer, PkiSamples.RootCaDer)).ToList();
        Assert.Equal(2, cas.Count);
        Assert.All(cas, c => Assert.True(c.Summary!.IsCa));
    }

    // ---- Helpers --------------------------------------------------------------------------------------------------------

    private static async Task<(PropertySheetViewModel Sheet, T Page)> OpenAsync<T>(PkiHarness harness, string dn, bool activate = true)
        where T : PropertyPageViewModel
    {
        var sheet = harness.B2.Factory.CreatePropertySheet(dn);
        await sheet.InitializeAsync();
        var page = Assert.Single(sheet.Pages.OfType<T>());
        if (activate)
        {
            await page.ActivateAsync();
            Assert.False(page.IsLoading);
        }

        return (sheet, page);
    }

    private static string SidOf(PkiHarness harness, string dn) => Assert.Single(harness.Session.GetValues(dn, "objectSid"));

    private static string Local(PkiHarness harness, DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, harness.B2.Time.LocalTimeZone).ToString("g", CultureInfo.GetCultureInfo("pl-PL"));

    /// <summary>A PKCS #7 ContentInfo with SignedData holding <paramref name="certificates"/> (as Outlook publishes userSMIMECertificate).</summary>
    private static byte[] SignedData(params byte[][] certificates)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();
        writer.WriteObjectIdentifier("1.2.840.113549.1.7.2");
        writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
        writer.PushSequence();
        writer.WriteInteger(1);
        writer.PushSetOf();
        writer.PopSetOf();
        writer.PushSequence();
        writer.WriteObjectIdentifier("1.2.840.113549.1.7.1");
        writer.PopSequence();
        if (certificates.Length > 0)
        {
            writer.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
            foreach (var certificate in certificates)
            {
                writer.WriteEncodedValue(certificate);
            }

            writer.PopSetOf(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
        }

        writer.PushSetOf();
        writer.PopSetOf();
        writer.PopSequence();
        writer.PopSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
        writer.PopSequence();
        return writer.Encode();
    }

    /// <summary>The value with the last byte of its blob flipped (the KeyCreationTime): the KeyHash no longer matches.</summary>
    private static string Tampered(string value)
    {
        var parts = value.Split(':', 4);
        var blob = Convert.FromHexString(parts[2]);
        blob[^1] ^= 0x01;
        return $"B:{parts[1]}:{Convert.ToHexString(blob)}:{parts[3]}";
    }
}
