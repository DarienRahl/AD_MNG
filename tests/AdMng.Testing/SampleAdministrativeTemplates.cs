using System.Text;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;

namespace AdMng.Testing;

/// <summary>
/// A small synthetic policy definition store (K3 tests): a base file with the root categories every vendor file refers to
/// (<c>windows:WindowsComponents</c>) and a "Contoso" file whose policies use every element kind, in English and Polish.
/// Names and texts are made up for the tests.
/// </summary>
public static class SampleAdministrativeTemplates
{
    public const string BaseNamespace = "Microsoft.Policies.Windows";
    public const string ContosoNamespace = "Contoso.Policies.Aktualizacje";

    /// <summary>Policy ids of the Contoso file.</summary>
    public const string Updates = ContosoNamespace + ":KonfiguracjaAktualizacji";
    public const string UpdateServer = ContosoNamespace + ":SerwerAktualizacji";
    public const string TrustedSites = ContosoNamespace + ":ZaufaneWitryny";
    public const string DriveMappings = ContosoNamespace + ":MapowanieDyskow";
    public const string Message = ContosoNamespace + ":Komunikat";
    public const string HidePreview = ContosoNamespace + ":WylaczPodglad";
    public const string WorkMode = ContosoNamespace + ":TrybPracy";

    /// <summary>The key of the update policies (computer).</summary>
    public const string UpdatesKey = @"Software\Policies\Contoso\Aktualizacje\AU";

    public const string BaseAdmx = """
        <?xml version="1.0" encoding="utf-8"?>
        <policyDefinitions revision="1.0" schemaVersion="1.0" xmlns="http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions">
          <policyNamespaces>
            <target prefix="windows" namespace="Microsoft.Policies.Windows" />
          </policyNamespaces>
          <resources minRequiredRevision="1.0" />
          <supportedOn>
            <definitions>
              <definition name="SUPPORTED_Win10" displayName="$(string.SUPPORTED_Win10)" />
            </definitions>
          </supportedOn>
          <categories>
            <category name="WindowsComponents" displayName="$(string.WindowsComponents)" />
            <category name="System" displayName="$(string.System)" />
          </categories>
        </policyDefinitions>
        """;

    public const string BaseAdmlEn = """
        <?xml version="1.0" encoding="utf-8"?>
        <policyDefinitionResources revision="1.0" schemaVersion="1.0" xmlns="http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions">
          <displayName>Windows</displayName>
          <description>Base</description>
          <resources>
            <stringTable>
              <string id="SUPPORTED_Win10">At least Windows 10</string>
              <string id="WindowsComponents">Windows Components</string>
              <string id="System">System</string>
            </stringTable>
          </resources>
        </policyDefinitionResources>
        """;

    public const string BaseAdmlPl = """
        <?xml version="1.0" encoding="utf-8"?>
        <policyDefinitionResources revision="1.0" schemaVersion="1.0" xmlns="http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions">
          <displayName>Windows</displayName>
          <description>Podstawa</description>
          <resources>
            <stringTable>
              <string id="SUPPORTED_Win10">Co najmniej Windows 10</string>
              <string id="WindowsComponents">Składniki systemu Windows</string>
              <string id="System">System</string>
            </stringTable>
          </resources>
        </policyDefinitionResources>
        """;

    public const string ContosoAdmx = """
        <?xml version="1.0" encoding="utf-8"?>
        <policyDefinitions revision="1.2" schemaVersion="1.0" xmlns="http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions">
          <policyNamespaces>
            <target prefix="contoso" namespace="Contoso.Policies.Aktualizacje" />
            <using prefix="windows" namespace="Microsoft.Policies.Windows" />
          </policyNamespaces>
          <resources minRequiredRevision="1.2" />
          <categories>
            <category name="Aktualizacje" displayName="$(string.Aktualizacje)" explainText="$(string.Aktualizacje_Help)">
              <parentCategory ref="windows:WindowsComponents" />
            </category>
            <category name="Pulpit" displayName="$(string.Pulpit)">
              <parentCategory ref="Aktualizacje" />
            </category>
          </categories>
          <policies>
            <policy name="KonfiguracjaAktualizacji" class="Machine" displayName="$(string.Konfiguracja)" explainText="$(string.Konfiguracja_Help)"
                    presentation="$(presentation.Konfiguracja)" key="Software\Policies\Contoso\Aktualizacje\AU" valueName="NoAutoUpdate">
              <parentCategory ref="Aktualizacje" />
              <supportedOn ref="windows:SUPPORTED_Win10" />
              <enabledValue><decimal value="0" /></enabledValue>
              <disabledValue><decimal value="1" /></disabledValue>
              <elements>
                <enum id="Tryb" valueName="AUOptions" required="true">
                  <item displayName="$(string.Tryb2)"><value><decimal value="2" /></value></item>
                  <item displayName="$(string.Tryb3)"><value><decimal value="3" /></value></item>
                  <item displayName="$(string.Tryb4)"><value><decimal value="4" /></value></item>
                </enum>
                <decimal id="Dzien" valueName="ScheduledInstallDay" maxValue="7" required="true" />
                <boolean id="InneProdukty" valueName="AllowMUUpdateService">
                  <trueValue><decimal value="1" /></trueValue>
                  <falseValue><delete /></falseValue>
                </boolean>
              </elements>
            </policy>
            <policy name="SerwerAktualizacji" class="Machine" displayName="$(string.Serwer)" explainText="$(string.Serwer_Help)"
                    presentation="$(presentation.Serwer)" key="Software\Policies\Contoso\Aktualizacje">
              <parentCategory ref="Aktualizacje" />
              <supportedOn ref="windows:SUPPORTED_Win10" />
              <enabledList defaultKey="Software\Policies\Contoso\Aktualizacje\AU">
                <item valueName="UseWUServer"><value><decimal value="1" /></value></item>
              </enabledList>
              <elements>
                <text id="Adres" valueName="WUServer" required="true" maxLength="200" />
                <text id="Statystyki" valueName="WUStatusServer" />
              </elements>
            </policy>
            <policy name="ZaufaneWitryny" class="Both" displayName="$(string.Witryny)" explainText="$(string.Witryny_Help)"
                    presentation="$(presentation.Witryny)" key="Software\Policies\Contoso\Przegladarka">
              <parentCategory ref="Pulpit" />
              <elements>
                <list id="Lista" key="Software\Policies\Contoso\Przegladarka\ZaufaneWitryny" valuePrefix="" />
              </elements>
            </policy>
            <policy name="MapowanieDyskow" class="User" displayName="$(string.Dyski)" explainText="$(string.Dyski_Help)"
                    presentation="$(presentation.Dyski)" key="Software\Policies\Contoso\Dyski">
              <parentCategory ref="Pulpit" />
              <elements>
                <list id="Mapy" key="Software\Policies\Contoso\Dyski\Mapy" explicitValue="true" expandable="true" />
              </elements>
            </policy>
            <policy name="Komunikat" class="User" displayName="$(string.Komunikat)" explainText="$(string.Komunikat_Help)"
                    presentation="$(presentation.Komunikat)" key="Software\Policies\Contoso\Komunikat">
              <parentCategory ref="Pulpit" />
              <elements>
                <multiText id="Tresc" valueName="Lines" maxStrings="3" required="true" />
                <text id="Sciezka" valueName="Path" expandable="true" />
                <longDecimal id="Rozmiar" valueName="MaxSize" maxValue="10000000000" />
                <decimal id="Opoznienie" valueName="Delay" storeAsText="true" soft="true" />
              </elements>
            </policy>
            <policy name="WylaczPodglad" class="Machine" displayName="$(string.Podglad)" explainText="$(string.Podglad_Help)"
                    key="Software\Policies\Contoso\Eksplorator" valueName="NoPreview">
              <parentCategory ref="windows:System" />
            </policy>
            <policy name="TrybPracy" class="Machine" displayName="$(string.TrybPracy)" presentation="$(presentation.TrybPracy)"
                    key="Software\Policies\Contoso\Tryb" valueName="Enabled">
              <parentCategory ref="windows:System" />
              <elements>
                <boolean id="Oszczedny" valueName="Saver">
                  <trueList><item valueName="Dim"><value><decimal value="1" /></value></item></trueList>
                  <falseList><item valueName="Dim"><value><decimal value="0" /></value></item></falseList>
                </boolean>
                <enum id="Profil" valueName="Profile">
                  <item displayName="$(string.ProfilBiuro)">
                    <value><string>biuro</string></value>
                    <valueList><item valueName="Volume"><value><decimal value="20" /></value></item></valueList>
                  </item>
                  <item displayName="$(string.ProfilDom)">
                    <value><string>dom</string></value>
                    <valueList><item valueName="Volume"><value><decimal value="80" /></value></item></valueList>
                  </item>
                </enum>
              </elements>
            </policy>
          </policies>
        </policyDefinitions>
        """;

    public const string ContosoAdmlEn = """
        <?xml version="1.0" encoding="utf-8"?>
        <policyDefinitionResources revision="1.2" schemaVersion="1.0" xmlns="http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions">
          <displayName>Contoso updates</displayName>
          <description>Contoso</description>
          <resources>
            <stringTable>
              <string id="Aktualizacje">Contoso Update</string>
              <string id="Aktualizacje_Help">Update settings.</string>
              <string id="Pulpit">Desktop</string>
              <string id="Konfiguracja">Configure automatic updates</string>
              <string id="Konfiguracja_Help">Specifies whether this computer receives updates.</string>
              <string id="Tryb2">2 - Notify for download</string>
              <string id="Tryb3">3 - Auto download and notify</string>
              <string id="Tryb4">4 - Auto download and schedule</string>
              <string id="Serwer">Specify intranet update service location</string>
              <string id="Serwer_Help">The server that hosts updates.</string>
              <string id="Witryny">Trusted sites</string>
              <string id="Witryny_Help">Sites the browser trusts.</string>
              <string id="Dyski">Drive mappings</string>
              <string id="Dyski_Help">Drive letters and their paths.</string>
              <string id="Komunikat">Logon message</string>
              <string id="Komunikat_Help">A message shown at logon.</string>
              <string id="Podglad">Turn off the preview pane</string>
              <string id="Podglad_Help">Hides the preview pane.</string>
              <string id="TrybPracy">Work mode</string>
              <string id="ProfilBiuro">Office</string>
              <string id="ProfilDom">Home</string>
            </stringTable>
            <presentationTable>
              <presentation id="Konfiguracja">
                <dropdownList refId="Tryb" defaultItem="1" noSort="true">Configure automatic updating:</dropdownList>
                <text>The following settings apply to option 4 only.</text>
                <decimalTextBox refId="Dzien" defaultValue="0" spinStep="1">Scheduled install day:</decimalTextBox>
                <checkBox refId="InneProdukty" defaultChecked="true">Install updates for other products</checkBox>
              </presentation>
              <presentation id="Serwer">
                <textBox refId="Adres"><label>Update server:</label><defaultValue>https://</defaultValue></textBox>
                <comboBox refId="Statystyki" noSort="true"><label>Statistics server:</label><default>https://</default><suggestion>https://wsus.contoso.test</suggestion></comboBox>
              </presentation>
              <presentation id="Witryny">
                <listBox refId="Lista">Sites:</listBox>
              </presentation>
              <presentation id="Dyski">
                <listBox refId="Mapy">Drive letter and path:</listBox>
              </presentation>
              <presentation id="Komunikat">
                <multiTextBox refId="Tresc" defaultHeight="4">Message lines:</multiTextBox>
                <textBox refId="Sciezka"><label>Log path:</label></textBox>
                <longDecimalTextBox refId="Rozmiar" defaultValue="1048576">Largest log (bytes):</longDecimalTextBox>
                <decimalTextBox refId="Opoznienie" defaultValue="30" spin="false">Delay (seconds):</decimalTextBox>
                <unknownControl refId="Nowy">Ignored</unknownControl>
              </presentation>
              <presentation id="TrybPracy">
                <checkBox refId="Oszczedny">Power saver</checkBox>
                <dropdownList refId="Profil">Profile:</dropdownList>
              </presentation>
            </presentationTable>
          </resources>
        </policyDefinitionResources>
        """;

    public const string ContosoAdmlPl = """
        <?xml version="1.0" encoding="utf-8"?>
        <policyDefinitionResources revision="1.2" schemaVersion="1.0" xmlns="http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions">
          <displayName>Aktualizacje Contoso</displayName>
          <description>Contoso</description>
          <resources>
            <stringTable>
              <string id="Aktualizacje">Aktualizacje Contoso</string>
              <string id="Aktualizacje_Help">Ustawienia aktualizacji.</string>
              <string id="Pulpit">Pulpit</string>
              <string id="Konfiguracja">Konfiguruj aktualizacje automatyczne</string>
              <string id="Konfiguracja_Help">Określa, czy ten komputer otrzymuje aktualizacje.
        Dotyczy stacji roboczych i serwerów.</string>
              <string id="Tryb2">2 – Powiadom o pobieraniu</string>
              <string id="Tryb3">3 – Pobierz automatycznie i powiadom</string>
              <string id="Tryb4">4 – Pobierz automatycznie i zaplanuj instalację</string>
              <string id="Serwer">Określ lokalizację usługi aktualizacji w intranecie</string>
              <string id="Serwer_Help">Serwer, z którego pochodzą aktualizacje.</string>
              <string id="Witryny">Zaufane witryny</string>
              <string id="Witryny_Help">Witryny, którym ufa przeglądarka.</string>
              <string id="Dyski">Mapowanie dysków</string>
              <string id="Dyski_Help">Litery dysków i ich ścieżki.</string>
              <string id="Komunikat">Komunikat przy logowaniu</string>
              <string id="Komunikat_Help">Komunikat pokazywany przy logowaniu.</string>
              <string id="Podglad">Wyłącz okienko podglądu</string>
              <string id="Podglad_Help">Ukrywa okienko podglądu.</string>
              <string id="TrybPracy">Tryb pracy</string>
              <string id="ProfilBiuro">Biuro</string>
              <string id="ProfilDom">Dom</string>
            </stringTable>
            <presentationTable>
              <presentation id="Konfiguracja">
                <dropdownList refId="Tryb" defaultItem="1" noSort="true">Konfigurowanie aktualizacji automatycznych:</dropdownList>
                <text>Poniższe ustawienia dotyczą tylko opcji 4.</text>
                <decimalTextBox refId="Dzien" defaultValue="0" spinStep="1">Zaplanowany dzień instalacji:</decimalTextBox>
                <checkBox refId="InneProdukty" defaultChecked="true">Instaluj aktualizacje innych produktów</checkBox>
              </presentation>
              <presentation id="Serwer">
                <textBox refId="Adres"><label>Serwer aktualizacji:</label><defaultValue>https://</defaultValue></textBox>
                <comboBox refId="Statystyki" noSort="true"><label>Serwer statystyk:</label><default>https://</default><suggestion>https://wsus.contoso.test</suggestion></comboBox>
              </presentation>
              <presentation id="Witryny">
                <listBox refId="Lista">Witryny:</listBox>
              </presentation>
              <presentation id="Dyski">
                <listBox refId="Mapy">Litera dysku i ścieżka:</listBox>
              </presentation>
              <presentation id="Komunikat">
                <multiTextBox refId="Tresc" defaultHeight="4">Wiersze komunikatu:</multiTextBox>
                <textBox refId="Sciezka"><label>Ścieżka dziennika:</label></textBox>
                <longDecimalTextBox refId="Rozmiar" defaultValue="1048576">Największy dziennik (bajty):</longDecimalTextBox>
                <decimalTextBox refId="Opoznienie" defaultValue="30" spin="false">Opóźnienie (sekundy):</decimalTextBox>
              </presentation>
              <presentation id="TrybPracy">
                <checkBox refId="Oszczedny">Oszczędzanie energii</checkBox>
                <dropdownList refId="Profil">Profil:</dropdownList>
              </presentation>
            </presentationTable>
          </resources>
        </policyDefinitionResources>
        """;

    /// <summary>The store: <c>windows.admx</c>, <c>contoso.admx</c> and both languages (pass false to leave Polish out).</summary>
    public static Dictionary<string, byte[]> Files(bool polish = true)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["windows.admx"] = Utf8(BaseAdmx),
            ["contoso.admx"] = Utf8(ContosoAdmx),
            ["en-US/windows.adml"] = Utf8(BaseAdmlEn),
            ["en-US/contoso.adml"] = Utf8(ContosoAdmlEn),
        };
        if (polish)
        {
            files["pl-PL/windows.adml"] = Utf8(BaseAdmlPl);
            files["pl-PL/contoso.adml"] = Utf8(ContosoAdmlPl);
        }

        return files;
    }

    public static InMemoryPolicyDefinitionSource Source(bool polish = true) => new("Magazyn testowy", Files(polish));

    /// <summary>The store loaded in Polish (en-US fallback).</summary>
    public static PolicyDefinitionSet LoadPolish()
    {
        var (set, _) = PolicyDefinitionLoader.LoadAsync(Source(), ["pl-PL", "en-US"], CancellationToken.None).GetAwaiter().GetResult();
        return set;
    }

    /// <summary>UTF-8 with BOM, as the Microsoft files are saved.</summary>
    public static byte[] Utf8(string text) => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];
}
