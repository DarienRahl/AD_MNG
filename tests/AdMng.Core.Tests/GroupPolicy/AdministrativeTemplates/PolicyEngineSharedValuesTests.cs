using System.Text;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;
using static AdMng.Core.Tests.GroupPolicy.AdministrativeTemplates.RegistryPolicyDocumentTests;

namespace AdMng.Core.Tests.GroupPolicy.AdministrativeTemplates;

/// <summary>
/// Policies that share values with their own elements or with other policies (shapes of real templates: Citrix Workspace's
/// lockdowns, the IE zone templates, Windows Error Reporting's consent key, Edge's redirection channels, BITS "Custom"),
/// written as a synthetic Polish template.
/// </summary>
public class PolicyEngineSharedValuesTests
{
    private const string Audio = @"Software\Policies\Contoso\Kanaly\Audio";
    private const string Zone = @"Software\Policies\Contoso\Strefy\3";
    private const string Consent = @"Software\Policies\Contoso\Raporty\Zgoda";
    private const string Channel = @"Software\Policies\Contoso\Przegladarka\Kanal";
    private const string Bits = @"Software\Policies\Contoso\Bits";

    private const string Admx = """
        <?xml version="1.0" encoding="utf-8"?>
        <policyDefinitions revision="1.0" schemaVersion="1.0" xmlns="http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions">
          <policyNamespaces>
            <target prefix="wspolne" namespace="Contoso.Policies.Wspolne" />
          </policyNamespaces>
          <resources minRequiredRevision="1.0" />
          <categories>
            <category name="Testy" displayName="$(string.Testy)" />
          </categories>
          <policies>
            <policy name="WejscieAudio" class="Machine" displayName="$(string.WejscieAudio)" key="Software\Policies\Contoso\Kanaly\Audio" valueName="WlaczWejscie">
              <parentCategory ref="Testy" />
              <enabledValue><string>true</string></enabledValue>
              <disabledValue><string>false</string></disabledValue>
              <elements>
                <boolean id="Wlacz" valueName="WlaczWejscie">
                  <trueValue><string>true</string></trueValue>
                  <falseValue><string>false</string></falseValue>
                </boolean>
              </elements>
            </policy>
            <policy name="SzablonStrefy" class="Machine" displayName="$(string.SzablonStrefy)" key="Software\Policies\Contoso\Strefy" valueName="SzablonInternet">
              <parentCategory ref="Testy" />
              <enabledValue><decimal value="1" /></enabledValue>
              <disabledValue><decimal value="0" /></disabledValue>
              <disabledList defaultKey="Software\Policies\Contoso\Strefy\3">
                <item valueName="1201"><value><delete /></value></item>
                <item valueName="1400"><value><delete /></value></item>
              </disabledList>
              <elements>
                <enum id="Poziom" key="Software\Policies\Contoso\Strefy" valueName="Poziom">
                  <item displayName="$(string.Wysoki)">
                    <value><decimal value="1" /></value>
                    <valueList>
                      <item key="Software\Policies\Contoso\Strefy\3" valueName="1201"><value><decimal value="3" /></value></item>
                      <item key="Software\Policies\Contoso\Strefy\3" valueName="1400"><value><decimal value="3" /></value></item>
                    </valueList>
                  </item>
                  <item displayName="$(string.Niski)">
                    <value><decimal value="2" /></value>
                    <valueList>
                      <item key="Software\Policies\Contoso\Strefy\3" valueName="1201"><value><decimal value="0" /></value></item>
                      <item key="Software\Policies\Contoso\Strefy\3" valueName="1400"><value><decimal value="0" /></value></item>
                    </valueList>
                  </item>
                </enum>
              </elements>
            </policy>
            <policy name="AkcjaUrl" class="Machine" displayName="$(string.AkcjaUrl)" key="Software\Policies\Contoso\Strefy\3" valueName="1201">
              <parentCategory ref="Testy" />
              <enabledValue><decimal value="0" /></enabledValue>
              <disabledValue><decimal value="3" /></disabledValue>
            </policy>
            <policy name="ZgodyWlasne" class="Machine" displayName="$(string.ZgodyWlasne)" key="Software\Policies\Contoso\Raporty\Zgoda">
              <parentCategory ref="Testy" />
              <elements>
                <list id="Zgody" key="Software\Policies\Contoso\Raporty\Zgoda" additive="true" explicitValue="true" />
              </elements>
            </policy>
            <policy name="ZgodaDomyslna" class="Machine" displayName="$(string.ZgodaDomyslna)" key="Software\Policies\Contoso\Raporty\Zgoda">
              <parentCategory ref="Testy" />
              <elements>
                <enum id="Poziom" valueName="DomyslnaZgoda">
                  <item displayName="$(string.Pytaj)"><value><decimal value="1" /></value></item>
                  <item displayName="$(string.Wysylaj)"><value><decimal value="4" /></value></item>
                </enum>
              </elements>
            </policy>
            <policy name="KanalPrzegladarki" class="Machine" displayName="$(string.KanalPrzegladarki)" key="Software\Policies\Contoso\Przegladarka\Kanal">
              <parentCategory ref="Testy" />
              <elements>
                <enum id="Pierwszy" valueName="Kanal1">
                  <item displayName="$(string.Brak)"><value><delete /></value></item>
                  <item displayName="$(string.Stabilny)"><value><decimal value="1" /></value></item>
                </enum>
                <enum id="Drugi" valueName="Kanal2">
                  <item displayName="$(string.Brak)"><value><delete /></value></item>
                  <item displayName="$(string.Stabilny)"><value><decimal value="1" /></value></item>
                </enum>
                <enum id="Trzeci" valueName="Kanal3">
                  <item displayName="$(string.Brak)"><value><delete /></value></item>
                  <item displayName="$(string.Stabilny)"><value><decimal value="1" /></value></item>
                </enum>
              </elements>
            </policy>
            <policy name="TransferBits" class="Machine" displayName="$(string.TransferBits)" key="Software\Policies\Contoso\Bits">
              <parentCategory ref="Testy" />
              <elements>
                <enum id="Tryb" valueName="TrybTransferu">
                  <item displayName="$(string.Zawsze)"><value><decimal value="2147483903" /></value></item>
                  <item displayName="$(string.Wlasny)">
                    <value><decimal value="255" /></value>
                    <valueList>
                      <item valueName="TrybTransferu"><value><delete /></value></item>
                    </valueList>
                  </item>
                </enum>
                <decimal id="Limit" valueName="TrybWlasny" />
              </elements>
            </policy>
          </policies>
        </policyDefinitions>
        """;

    private static readonly AdmxFile File = AdmxParser.Parse("wspolne.admx", Encoding.UTF8.GetBytes(Admx));

    /// <summary>What every policy of the file owns (the editor's map of known values).</summary>
    private static readonly Dictionary<string, HashSet<string>> OwnedByAny = Owned();

    private static AdmxPolicy Policy(string name) => File.Policies.Single(p => p.Name == name);

    private static Dictionary<string, PolicyElementValue> Values(params (string Id, PolicyElementValue Value)[] values) =>
        values.ToDictionary(v => v.Id, v => v.Value, StringComparer.OrdinalIgnoreCase);

    private static RegistryPolEntry Text(string key, string name, string value) => new(key, name, RegistryFormat.RegSz, RegistryPolFile.StringData(value));

    private static string? TextOf(RegistryValueState state) => state.Entry is { Type: RegistryFormat.RegSz } entry ? RegistryPolFile.ReadString(entry.Data) : null;

    private static Dictionary<string, HashSet<string>> Owned()
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var policy in File.Policies)
        {
            foreach (var (key, names) in PolicyEngine.OwnValueNames(policy))
            {
                if (!result.TryGetValue(key, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    result[key] = set;
                }

                set.UnionWith(names);
            }
        }

        return result;
    }

    [Fact]
    public void Disabled_KeepsTheDisabledValue_ThatAnElementShares()
    {
        var policy = Policy("WejscieAudio");
        var document = new RegistryPolicyDocument();
        PolicyEngine.Write(policy, PolicySetting.Enabled(Values(("Wlacz", new BooleanElementValue(true)))), document);

        PolicyEngine.Write(policy, PolicySetting.Disabled, document);

        // The lockdown "false" reaches the clients (the element's own deletion does not override it).
        Assert.Equal("false", TextOf(document.View.Get(Audio, "WlaczWejscie")));
        Assert.Single(document.Entries);
        Assert.Equal(PolicySetting.Disabled, PolicyEngine.Read(policy, document.View));
    }

    [Fact]
    public void Disabled_KeepsTheDisabledList_ThatTheEnumItemsShare()
    {
        var policy = Policy("SzablonStrefy");
        var document = new RegistryPolicyDocument();
        PolicyEngine.Write(policy, PolicySetting.Enabled(Values(("Poziom", new EnumElementValue(0)))), document);

        PolicyEngine.Write(policy, PolicySetting.Disabled, document);

        Assert.Equal(0u, Number(document.View.Get(@"Software\Policies\Contoso\Strefy", "SzablonInternet")));
        Assert.True(document.View.Get(Zone, "1201").IsDeleted);
        Assert.True(document.View.Get(Zone, "1400").IsDeleted);
        Assert.Equal(PolicySetting.Disabled, PolicyEngine.Read(policy, document.View));
    }

    [Fact]
    public void APolicyWhoseOwnValueIsAbsent_IsNotConfigured_WhateverOtherPoliciesWroteIntoSharedValues()
    {
        var template = Policy("SzablonStrefy");
        var single = Policy("AkcjaUrl");
        var document = new RegistryPolicyDocument();
        PolicyEngine.Write(single, PolicySetting.Enabled(Values()), document);
        Assert.Equal(PolicyState.NotConfigured, PolicyEngine.Read(template, document.View).State);

        PolicyEngine.Write(single, PolicySetting.Disabled, document);
        Assert.Equal(PolicyState.NotConfigured, PolicyEngine.Read(template, document.View).State);
        Assert.Equal(PolicySetting.Disabled, PolicyEngine.Read(single, document.View));
    }

    [Fact]
    public void AnAdditiveList_IgnoresTheValuesOfOtherPoliciesInItsKey()
    {
        var list = Policy("ZgodyWlasne");
        var other = Policy("ZgodaDomyslna");
        var document = new RegistryPolicyDocument([Dword(Consent, "ZewnetrznyLicznik", 7)]);
        PolicyEngine.Write(other, PolicySetting.Enabled(Values(("Poziom", new EnumElementValue(1)))), document);

        // Not configured: the other policy's DWORD and a value of another type are not list entries.
        Assert.Equal(PolicyState.NotConfigured, PolicyEngine.Read(list, document.View, OwnedByAny).State);

        PolicyEngine.Write(list, PolicySetting.Enabled(Values(("Zgody", new ListElementValue([new("Łódź.exe", "1")])))), document, OwnedByAny);
        var read = PolicyEngine.Read(list, document.View, OwnedByAny);
        Assert.Equal(PolicyState.Enabled, read.State);
        Assert.Equal(["Łódź.exe"], ((ListElementValue)read.ValueOf("Zgody")!).Items.Select(i => i.Name));
        Assert.Equal(4u, Number(document.View.Get(Consent, "DomyslnaZgoda")));
        Assert.Equal(7u, Number(document.View.Get(Consent, "ZewnetrznyLicznik")));
        Assert.Equal(new EnumElementValue(1), PolicyEngine.Read(other, document.View, OwnedByAny).ValueOf("Poziom"));

        // Not configured again: only the list's own entry goes.
        PolicyEngine.Write(list, PolicySetting.NotConfigured, document, OwnedByAny);
        Assert.Equal(["ZewnetrznyLicznik=7", "DomyslnaZgoda=4"], Describe(document));
    }

    [Fact]
    public void EnumDeletions_OfAnEnabledPolicyWithoutAValueName_ReadBackAsEnabled()
    {
        var policy = Policy("KanalPrzegladarki");
        var document = new RegistryPolicyDocument();
        var setting = PolicySetting.Enabled(Values(("Pierwszy", new EnumElementValue(1)), ("Drugi", new EnumElementValue(0)), ("Trzeci", new EnumElementValue(0))));

        PolicyEngine.Write(policy, setting, document);

        Assert.Equal(["Kanal1=1", "**del.Kanal2", "**del.Kanal3"], Describe(document));
        Assert.Equal(setting, PolicyEngine.Read(policy, document.View));
        Assert.Equal(PolicySetting.Disabled, Write(policy, PolicySetting.Disabled));
    }

    [Fact]
    public void AnEnumItemWhoseValueListDeletesTheEnumsValue_ReadsBack()
    {
        var policy = Policy("TransferBits");
        var document = new RegistryPolicyDocument();
        var setting = PolicySetting.Enabled(Values(("Tryb", new EnumElementValue(1)), ("Limit", new NumberElementValue(1000))));

        PolicyEngine.Write(policy, setting, document);

        Assert.True(document.View.Get(Bits, "TrybTransferu").IsDeleted);
        Assert.Equal(setting, PolicyEngine.Read(policy, document.View));
    }

    [Fact]
    public void AValueSetAgain_MovesAfterALaterDirectiveThatDeletesIt()
    {
        var document = new RegistryPolicyDocument([Dword(Channel, "A", 1), Text(Channel, RegistryPolicyDocument.DeleteAllValues, " ")]);

        document.Set(Channel, "A", RegistryFormat.RegDword, RegistryPolFile.DwordData(5));

        Assert.Equal(["**delvals.", "A=5"], Describe(document));
        Assert.Equal(5u, Number(document.View.Get(Channel, "A")));

        var listed = new RegistryPolicyDocument([Dword(Channel, "A", 1), Dword(Channel, "B", 2), Text(Channel, RegistryPolicyDocument.DeleteValuesList, "A;B")]);
        listed.Set(Channel, "B", RegistryFormat.RegDword, RegistryPolFile.DwordData(9));
        Assert.Equal(["A=1", "**DeleteValues", "B=9"], Describe(listed));
        Assert.Equal(9u, Number(listed.View.Get(Channel, "B")));
        Assert.True(listed.View.Get(Channel, "A").IsDeleted);

        var ancestor = new RegistryPolicyDocument([Dword(Channel, "A", 1), Text(@"Software\Policies\Contoso", RegistryPolicyDocument.DeleteKeysList, @"Przegladarka")]);
        ancestor.Set(Channel, "A", RegistryFormat.RegDword, RegistryPolFile.DwordData(3));
        Assert.Equal(3u, Number(ancestor.View.Get(Channel, "A")));
    }

    private static PolicySetting Write(AdmxPolicy policy, PolicySetting setting)
    {
        var document = new RegistryPolicyDocument();
        PolicyEngine.Write(policy, setting, document);
        return PolicyEngine.Read(policy, document.View);
    }
}
