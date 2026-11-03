using AdMng.Core.GroupPolicy.Content;

namespace AdMng.Core.Tests.GroupPolicy.Content;

/// <summary><see cref="GpoSettingValue.TryParseRegistry(string?, out uint, out string)"/> against the values the real parsers write.</summary>
public sealed class GpoSettingValueTests
{
    private static readonly IReadOnlyList<GpoSetting> Sample = GpoContentSamples.Settings();

    [Theory]
    [InlineData("REG_DWORD:1", RegistryFormat.RegDword, "1")]
    [InlineData("REG_DWORD:4294967295", RegistryFormat.RegDword, "4294967295")]
    [InlineData("REG_SZ:Zażółć gęślą jaźń", RegistryFormat.RegSz, "Zażółć gęślą jaźń")]
    [InlineData("REG_SZ:", RegistryFormat.RegSz, "")]
    [InlineData("REG_SZ:a:b", RegistryFormat.RegSz, "a:b")]
    [InlineData("REG_EXPAND_SZ:%ProgramData%\\Contoso", RegistryFormat.RegExpandSz, "%ProgramData%\\Contoso")]
    [InlineData("REG_BINARY:0a1b", RegistryFormat.RegBinary, "0a1b")]
    [InlineData("REG_QWORD:10000000000", RegistryFormat.RegQword, "10000000000")]
    [InlineData("REG_DWORD_BIG_ENDIAN:7", RegistryFormat.RegDwordBigEndian, "7")]
    [InlineData("REG_LINK:\\Registry\\Machine", RegistryFormat.RegLink, "\\Registry\\Machine")]
    [InlineData("REG_NONE:", RegistryFormat.RegNone, "")]
    [InlineData("REG_TYPE_9:00", 9u, "00")]
    [InlineData("REG_MULTI_SZ:\"a\",\"b\"", RegistryFormat.RegMultiSz, "a\nb")]
    [InlineData("REG_MULTI_SZ:\"Łódź, Kraków\",\"cudzysłów \\\"x\\\"\",\"ukośnik \\\\\"", RegistryFormat.RegMultiSz, "Łódź, Kraków\ncudzysłów \"x\"\nukośnik \\")]
    [InlineData("REG_MULTI_SZ:\"\"", RegistryFormat.RegMultiSz, "")]
    [InlineData("REG_MULTI_SZ:", RegistryFormat.RegMultiSz, "")]
    [InlineData("SOFT:REG_SZ:30", RegistryFormat.RegSz, "30")]
    [InlineData("SOFT:REG_MULTI_SZ:\"x\",\"y\"", RegistryFormat.RegMultiSz, "x\ny")]
    public void TypedValues(string value, uint type, string data)
    {
        Assert.True(GpoSettingValue.TryParseRegistry(value, out var parsedType, out var parsedData));
        Assert.Equal((type, data), (parsedType, parsedData));
    }

    [Theory]
    [InlineData("REG_DWORD:1", GpoRegistryAction.Set)]
    [InlineData("SOFT:REG_SZ:30", GpoRegistryAction.SetIfMissing)]
    [InlineData("DELETE", GpoRegistryAction.DeleteValue)]
    [InlineData("DELETE_ALL_VALUES", GpoRegistryAction.DeleteAllValues)]
    [InlineData("DELETE_KEY", GpoRegistryAction.DeleteKey)]
    [InlineData("CREATE_KEY", GpoRegistryAction.CreateKey)]
    public void Actions(string value, GpoRegistryAction action)
    {
        Assert.True(GpoSettingValue.TryParseRegistry(value, out var parsedAction, out _, out _));
        Assert.Equal(action, parsedAction);
    }

    [Theory]
    [InlineData("DELETE")]
    [InlineData("DELETE_ALL_VALUES")]
    [InlineData("DELETE_KEY")]
    [InlineData("CREATE_KEY")]
    public void ActionsWithoutData_AreNoTypedValue(string value)
    {
        Assert.False(GpoSettingValue.TryParseRegistry(value, out var type, out var data));
        Assert.Equal((RegistryFormat.RegNone, string.Empty), (type, data));
        Assert.True(GpoSettingValue.TryParseRegistry(value, out _, out type, out data));
        Assert.Equal((RegistryFormat.RegNone, string.Empty), (type, data));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("Success and Failure")]
    [InlineData(":1")]
    [InlineData("REG_WORD:1")]
    [InlineData("reg_dword:1")]
    [InlineData("REG_TYPE_x:1")]
    [InlineData("SOFT:")]
    [InlineData("SOFT:DELETE")]
    [InlineData("sha256:0123456789abcdef")]
    [InlineData("REG_MULTI_SZ:a,b")]
    [InlineData("REG_MULTI_SZ:\"a")]
    [InlineData("REG_MULTI_SZ:\"a\",")]
    [InlineData("REG_MULTI_SZ:\"a\"\"b\"")]
    [InlineData("REG_MULTI_SZ:\"a\" ,\"b\"")]
    public void OtherValues_AreNotRegistryValues(string? value)
    {
        Assert.False(GpoSettingValue.TryParseRegistry(value, out var type, out var data));
        Assert.Equal((RegistryFormat.RegNone, string.Empty), (type, data));
        Assert.False(GpoSettingValue.TryParseRegistry(value, out var action, out _, out _));
        Assert.Equal(GpoRegistryAction.Set, action);
    }

    [Fact]
    public void TheParsersValues_ReadBack()
    {
        const string Rv = "Computer/Security/Registry Values/HKLM\\";
        const string Pol = "Computer/Registry/HKLM\\Software\\Policies\\Contoso\\";

        Assert.Equal((RegistryFormat.RegDword, "1"), Registry(Rv + @"System\CurrentControlSet\Control\Lsa!NoLMHash"));
        Assert.Equal((RegistryFormat.RegSz, "Ostrzeżenie"), Registry(Rv + @"Software\Microsoft\Windows\CurrentVersion\Policies\System!LegalNoticeCaption"));
        Assert.Equal((RegistryFormat.RegMultiSz, "Dostęp tylko dla upoważnionych.\nZażółć gęślą jaźń"),
            Registry(Rv + @"Software\Microsoft\Windows\CurrentVersion\Policies\System!LegalNoticeText"));

        Assert.Equal((RegistryFormat.RegMultiSz, "srv-łódź-01\nsrv \"główny\""), Registry(Pol + "Pulpit!Serwery"));
        Assert.Equal((RegistryFormat.RegBinary, "0a1b"), Registry(Pol + "Pulpit!Klucz"));
        Assert.Equal((RegistryFormat.RegQword, "10000000000"), Registry(Pol + "Pulpit!Rozmiar"));
        Assert.Equal((RegistryFormat.RegExpandSz, @"%ProgramData%\Contoso"), Registry(Pol + "Pulpit!Folder"));
        Assert.Equal((RegistryFormat.RegSz, "domyślna"), Registry(Pol + "Pulpit!(Default)"));
        Assert.Equal((RegistryFormat.RegSz, "30"), Registry(Pol + "Miękkie!Opóźnienie"));
        Assert.Equal(GpoRegistryAction.SetIfMissing, Action(Pol + "Miękkie!Opóźnienie"));
        Assert.Equal(GpoRegistryAction.DeleteValue, Action(Pol + "Stare!Usunięta"));
        Assert.Equal(GpoRegistryAction.DeleteAllValues, Action(Pol + "Lista!**delvals."));
        Assert.Equal(GpoRegistryAction.DeleteKey, Action(Pol + "Przestarzałe"));
        Assert.Equal(GpoRegistryAction.CreateKey, Action(Pol + "Pusty"));

        // Masked values have no value to read.
        var masked = Assert.Single(Sample, s => s.Key == @"Computer/Registry/HKLM\Software\Microsoft\Windows NT\CurrentVersion\Winlogon!DefaultPassword");
        Assert.Equal(GpoSettingKind.Masked, masked.Kind);
        Assert.False(GpoSettingValue.TryParseRegistry(masked.Value, out _, out _));
    }

    [Fact]
    public void TypeNames_RoundTrip()
    {
        foreach (var type in new uint[] { 0, 1, 2, 3, 4, 5, 6, 7, 11, 8, 9, 10, 12, 4711 })
        {
            Assert.True(GpoSettingValue.TryParseTypeName(RegistryFormat.TypeName(type), out var parsed));
            Assert.Equal(type, parsed);
        }
    }

    [Fact]
    public void QuotedLists_RoundTrip()
    {
        string[] items = ["zwykły", "z \"cudzysłowem\"", "z \\ ukośnikiem\\", "z,przecinkiem", string.Empty];

        Assert.True(GpoSettingValue.TryUnquoteList(string.Join(",", items.Select(GpoValues.Quote)), out var parsed));
        Assert.Equal(items, parsed);
        Assert.True(GpoSettingValue.TryUnquoteList(string.Empty, out var none));
        Assert.Empty(none);
        Assert.False(GpoSettingValue.TryUnquoteList(null, out _));
    }

    private static (uint Type, string Data) Registry(string key)
    {
        var setting = Assert.Single(Sample, s => s.Key == key);
        Assert.True(GpoSettingValue.TryParseRegistry(setting.Value, out var type, out var data));
        return (type, data);
    }

    private static GpoRegistryAction Action(string key)
    {
        var setting = Assert.Single(Sample, s => s.Key == key);
        Assert.True(GpoSettingValue.TryParseRegistry(setting.Value, out var action, out _, out _));
        return action;
    }
}
