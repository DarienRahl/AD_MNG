using System.Globalization;
using System.Management.Automation;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Formatting;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Testing;

namespace AdMng.PowerShell.Tests.Reading;

/// <summary>
/// The values of the objects the read commands return are the values the program's attribute view shows (R1 part 2): 1000
/// generated objects with every kind of value, compared attribute by attribute with <see cref="AttributeFormatter"/> (what the
/// Attribute Editor and the list use) and <see cref="SecretAttributes"/> (what masks).
/// </summary>
public sealed class ValueComparisonTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    [Fact]
    public async Task One_thousand_generated_objects_read_by_the_commands_show_what_the_programs_attribute_view_shows()
    {
        string ou = string.Empty;
        await using var harness = await ReadHarness.CreateAsync((builder, company) => ou = Generate(builder, company.CompanyOu));

        var result = await harness.RunAsync($"Get-AdmObject -LdapFilter '(objectClass=*)' -SearchBase '{ou}' -SearchScope OneLevel -Properties *");

        var schema = harness.Directory.Schema;
        var formatter = new AttributeFormatter(schema);
        var expected = new List<DirectoryObject>();
        await foreach (var obj in harness.Directory.SearchAsync(
            new DirectorySearch { BaseDn = ou, Scope = SearchScope.OneLevel, Filter = "(objectClass=*)", Attributes = ["*"] },
            TestContext.Current.CancellationToken))
        {
            expected.Add(obj);
        }

        Assert.Equal(1000, expected.Count);
        Assert.Equal(1000, result.Objects.Count);
        Assert.Equal(string.Empty, result.Errors);
        var byDn = result.Objects.ToDictionary(o => ((string)o.Properties["DistinguishedName"].Value!).ToUpperInvariant());
        var compared = 0;
        var kinds = new HashSet<string>();
        foreach (var obj in expected)
        {
            var actual = byDn[obj.DistinguishedName.ToUpperInvariant()];
            kinds.Add(actual.TypeNames.Count > 1 ? actual.TypeNames[1] : actual.TypeNames[0]);
            foreach (var (name, values) in obj.Attributes)
            {
                var property = actual.Properties[name];
                Assert.True(property is not null, $"{obj.DistinguishedName}: no property {name}");
                var shown = SecretAttributes.IsNeverShown(name, schema) ? SecretAttributes.Mask : formatter.Format(name, values, Culture);
                Assert.Equal(shown, TextLikeTheProgram(name, property.Value, actual, schema), StringComparer.Ordinal);
                compared++;
            }

            // The facts beside the attributes agree with the formatter's flag tables.
            if (obj.GetInt32(Attr.UserAccountControl) is { } uac && obj.Kind.IsAccount())
            {
                Assert.Equal(AttributeFormatter.GetUserAccountControlFlagNames(uac), (string[])actual.Properties["UserAccountControlFlags"].Value!);
                Assert.Equal((uac & 2) == 0, actual.Properties["Enabled"].Value);
            }

            if (obj.Kind == ObjectKind.Group && obj.GetInt32(Attr.GroupType) is { } groupType)
            {
                Assert.Equal(AttributeFormatter.GetGroupTypeFlagNames(groupType), (string[])actual.Properties["GroupTypeFlags"].Value!);
                Assert.Equal(GroupTypes.IsSecurity(groupType) ? "Security" : "Distribution", actual.Properties["GroupCategory"].Value);
            }
        }

        Assert.True(compared > 15000, $"only {compared} values were compared");
        Assert.Equal(["AdMng.Computer", "AdMng.Group", "AdMng.User"], kinds.Order().ToArray());
    }

    [Fact]
    public async Task The_values_have_the_type_the_schema_says()
    {
        await using var harness = await ReadHarness.CreateAsync((builder, company) =>
        {
            builder.AddUser("Typ", "Testowy", company.ItOu, new Dictionary<string, object?>
            {
                [Attr.AccountExpires] = new DateTimeOffset(2030, 5, 17, 10, 30, 0, TimeSpan.Zero),
                [Attr.LockoutTime] = 0,
                [Attr.BadPwdCount] = 3,
                [Attr.OtherTelephone] = new[] { "111", "222" },
                [Attr.Manager] = company.JanKowalski,
                ["uSNChanged"] = 123456789012L,
                [Attr.ShowInAdvancedViewOnly] = true,
                ["dSCorePropagationData"] = new[] { "20260102030405.0Z", "16010101000000.0Z" },
            });
        });

        var user = (await harness.RunAsync("Get-AdmUser -Identity typ.testowy -Properties *")).Single();

        Assert.Equal(new DateTime(2030, 5, 17, 10, 30, 0, DateTimeKind.Utc).ToLocalTime(), (DateTime)user.Properties["accountExpires"].Value!);
        Assert.Null(user.Properties["lockoutTime"].Value);
        Assert.Equal(3, user.Properties["badPwdCount"].Value);
        Assert.Equal(["111", "222"], (string[])user.Properties["otherTelephone"].Value!);
        Assert.Equal(harness.Company.JanKowalski, (string)user.Properties["manager"].Value!, ignoreCase: true);
        Assert.Equal(123456789012L, user.Properties["uSNChanged"].Value);
        Assert.Equal(true, user.Properties["showInAdvancedViewOnly"].Value);
        var propagation = (object?[])user.Properties["dSCorePropagationData"].Value!;
        Assert.IsType<DateTime>(propagation[0]);
        Assert.Null(propagation[1]);
        Assert.IsType<DateTime>(user.Properties["pwdLastSet"].Value);
        Assert.IsType<DateTime>(user.Properties["whenCreated"].Value);
        Assert.Null(user.Properties["logonHours"]);
    }

    [Fact]
    public async Task A_never_expiring_account_has_no_expiry_date_and_the_program_shows_never()
    {
        await using var harness = await ReadHarness.CreateAsync();

        var user = (await harness.RunAsync("Get-AdmUser -Identity jan.kowalski -Properties accountExpires")).Single();

        Assert.Null(user.Properties["accountExpires"].Value);
        Assert.Equal(
            AttributeFormatter.NeverText,
            new AttributeFormatter(harness.Directory.Schema).Format(
                Attr.AccountExpires, harness.Directory.Snapshot(harness.Company.JanKowalski)!.Attributes[Attr.AccountExpires], Culture));
    }

    [Fact]
    public async Task Binary_values_are_guids_sids_text_or_bytes_and_base64_only_when_nothing_says_what_they_are()
    {
        await using var harness = await ReadHarness.CreateAsync((builder, company) =>
        {
            builder.AddUser("Bin", "Arny", company.ItOu, new Dictionary<string, object?>
            {
                [Attr.LogonHours] = Enumerable.Range(0, 21).Select(i => (byte)(i * 11)).ToArray(),
                ["x-unknownText"] = "tekst"u8.ToArray(),
                ["x-unknownBinary"] = new byte[] { 0xFF, 0xFE, 0x00, 0x80 },
                [Attr.SidHistory] = new[] { "S-1-5-21-1-2-3-1500", "S-1-5-21-1-2-3-1501" },
            });
        });

        var user = (await harness.RunAsync("Get-AdmUser -Identity bin.arny -Properties *")).Single();

        Assert.Equal(21, ((byte[])user.Properties["logonHours"].Value!).Length);
        Assert.Equal(["S-1-5-21-1-2-3-1500", "S-1-5-21-1-2-3-1501"], (string[])user.Properties["sIDHistory"].Value!);
        Assert.Equal("tekst", user.Properties["x-unknownText"].Value);
        Assert.Equal("//4AgA==", user.Properties["x-unknownBinary"].Value);
        Assert.IsType<Guid>(user.Properties["objectGUID"].Value);
    }

    [Fact]
    public async Task Secrets_that_are_never_shown_are_masked_as_the_attribute_editor_masks_them_and_readable_ones_are_shown()
    {
        var schema = WithSecretAttributes(FakeSchema.Create());
        var builder = new FakeDirectoryBuilder(schema: schema);
        var company = builder.AddSampleCompany();
        builder.Session.Seed($"CN=Secretive,{company.ItOu}", new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "person", "organizationalPerson", "user" },
            [Attr.SamAccountName] = "secretive",
            [Attr.SamAccountType] = SamAccountType.UserObject,
            [Attr.UserAccountControl] = 512,
            ["userPKCS12"] = new[] { new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6 } },
            ["msLAPS-EncryptedPassword"] = new byte[] { 9, 9, 9 },
            ["ms-Mcs-AdmPwd"] = "Cleartext!Laps1",
            ["confidentialNote"] = "needs the control access right",
            ["unicodePwd"] = new byte[] { 7, 7 },
        });
        await using var harness = await ReadHarness.CreateAsync(builder, company);

        var user = (await harness.RunAsync("Get-AdmUser -Identity secretive -Properties userPKCS12,msLAPS-EncryptedPassword,ms-Mcs-AdmPwd,confidentialNote,unicodePwd,mail")).Single();

        // Key material and binary secrets: the mask, one string, whatever the number of values (the editor shows one mask).
        Assert.Equal(SecretAttributes.Mask, user.Properties["userPKCS12"].Value);
        Assert.Equal(SecretAttributes.Mask, user.Properties["msLAPS-EncryptedPassword"].Value);
        Assert.Equal(SecretAttributes.Mask, user.Properties["unicodePwd"].Value);

        // Operator-readable secrets (a clear-text LAPS password, a confidential text attribute) are shown by the editor, so here.
        Assert.False(SecretAttributes.IsNeverShown("ms-Mcs-AdmPwd", schema));
        Assert.Equal("Cleartext!Laps1", user.Properties["ms-Mcs-AdmPwd"].Value);
        Assert.Equal("needs the control access right", user.Properties["confidentialNote"].Value);

        // An unset secret is not the mask: the editor shows "not set", a script gets nothing.
        Assert.Null(user.Properties["mail"].Value);
    }

    [Fact]
    public async Task A_secret_never_shown_stays_masked_with_the_star_and_in_every_property_of_the_object()
    {
        var builder = new FakeDirectoryBuilder();
        var company = builder.AddSampleCompany();
        builder.Session.SetValues(company.JanKowalski, "userPKCS12", new byte[] { 1, 2, 3 });
        await using var harness = await ReadHarness.CreateAsync(builder, company);

        var all = (await harness.RunAsync("Get-AdmUser -Identity jan.kowalski -Properties *")).Single();
        var text = await harness.TextAsync("Get-AdmUser -Identity jan.kowalski -Properties userPKCS12 | Format-List * | Out-String");

        Assert.Equal(SecretAttributes.Mask, all.Properties["userPKCS12"].Value);
        Assert.Contains(SecretAttributes.Mask, text, StringComparison.Ordinal);
        Assert.DoesNotContain("01 02 03", text, StringComparison.Ordinal);
    }

    private static SchemaCatalog WithSecretAttributes(SchemaCatalog schema)
    {
        AttributeDefinition[] extra =
        [
            new() { LdapName = "msLAPS-EncryptedPassword", Syntax = AttributeSyntax.OctetString, IsSingleValued = true },
            new() { LdapName = "ms-Mcs-AdmPwd", Syntax = AttributeSyntax.UnicodeString, IsSingleValued = true },
            new() { LdapName = "confidentialNote", Syntax = AttributeSyntax.UnicodeString, IsSingleValued = true, IsConfidential = true },
        ];
        return new SchemaCatalog([.. schema.Attributes, .. extra], schema.Classes);
    }

    /// <summary>The text the program shows for a typed value (the inverse of the conversion, written independently of it).</summary>
    private static string TextLikeTheProgram(string attribute, object? value, PSObject owner, ISchemaCatalog schema)
    {
        if (value is null)
        {
            // Only the "never" sentinels are $null while the server returned a value.
            return AttributeFormatter.NeverText;
        }

        if (value is string and SecretAttributes.Mask)
        {
            return SecretAttributes.Mask;
        }

        if (value is Array array && value is not byte[])
        {
            return string.Join(AttributeFormatter.ValueSeparator, array.Cast<object?>().Select(v => TextLikeTheProgram(attribute, v, owner, schema)));
        }

        return value switch
        {
            bool flag => flag ? "TRUE" : "FALSE",
            DateTime when2 => when2.ToString("G", Culture),
            Guid guid => guid.ToString("D"),
            int number when string.Equals(attribute, Attr.UserAccountControl, StringComparison.OrdinalIgnoreCase)
                || string.Equals(attribute, Attr.UserAccountControlComputed, StringComparison.OrdinalIgnoreCase) =>
                WithFlags(number, AttributeFormatter.GetUserAccountControlFlagNames(number)),
            int number when string.Equals(attribute, Attr.GroupType, StringComparison.OrdinalIgnoreCase) =>
                WithFlags(number, AttributeFormatter.GetGroupTypeFlagNames(number)),
            int or long => Convert.ToString(value, Culture)!,
            byte[] bytes => Hex(bytes),
            string text when attribute == "x-unknownBinary" => Hex(System.Convert.FromBase64String(text)),
            string text => text,
            _ => Convert.ToString(value, Culture)!,
        };
    }

    private static string WithFlags(int number, IReadOnlyList<string> names) =>
        names.Count == 0 ? number.ToString(Culture) : $"{number.ToString(Culture)} ({string.Join(", ", names)})";

    private static string Hex(byte[] bytes)
    {
        var shown = bytes.Take(AttributeFormatter.MaxDisplayedBytes);
        return string.Join(' ', shown.Select(b => b.ToString("X2", Culture))) + (bytes.Length > AttributeFormatter.MaxDisplayedBytes ? " ..." : string.Empty);
    }

    /// <summary>
    /// 1000 objects below one OU: users, computers and groups with every sort of value the converter distinguishes (flags,
    /// FILETIME sentinels, dates, multi-valued lists, binary, SIDs, DNs, Polish text); the seed is fixed.
    /// </summary>
    private static string Generate(FakeDirectoryBuilder builder, string parent)
    {
        var random = new Random(20260101);
        var ou = builder.AddOu("Wygenerowane", parent);
        int[] uacValues = [512, 514, 544, 66048, 66050, 590336, 0x1000200, 0x1000, 0x2000, 0x82000, 0x10000200, 0x200 | 0x20000];
        int[] groupTypes = [-2147483646, -2147483644, -2147483640, 2, 4, 8, -2147483634, 5, -2147483643];
        long[] fileTimes = [0, long.MaxValue, 133000000000000000L, 132000000000000000L, 129000000000000000L, -1];
        string[] words = ["Zażółć", "gęślą", "jaźń", "Łódź", "Kraków", "Gdańsk", "(nawias)", "gwiazdka*", @"ukośnik\", "a;b", "tab"];

        string Word() => words[random.Next(words.Length)];
        string[] Words(int max) => [.. Enumerable.Range(0, random.Next(0, max + 1)).Select(_ => Word() + random.Next(100))];
        object? Maybe(object? value) => random.Next(4) == 0 ? null : value;

        for (var i = 0; i < 1000; i++)
        {
            switch (i % 10)
            {
                case < 4:
                    builder.AddUser($"Imię{i}", $"Nazwisko{i}", ou, new Dictionary<string, object?>
                    {
                        [Attr.UserAccountControl] = uacValues[random.Next(uacValues.Length)],
                        [Attr.Department] = Maybe(Word()),
                        [Attr.Title] = Maybe(Word()),
                        [Attr.Description] = Words(3),
                        [Attr.OtherTelephone] = Words(4),
                        [Attr.PwdLastSet] = random.Next(5) == 0 ? "0" : new DateTimeOffset(2020 + random.Next(6), 1 + random.Next(12), 1 + random.Next(28), random.Next(24), random.Next(60), 0, TimeSpan.Zero),
                        [Attr.AccountExpires] = random.Next(3) == 0 ? new DateTimeOffset(2027, 1 + random.Next(12), 1 + random.Next(28), 12, 0, 0, TimeSpan.Zero) : AdConvert.FileTimeNever,
                        [Attr.LockoutTime] = Maybe(fileTimes[random.Next(fileTimes.Length)].ToString(Culture)),
                        [Attr.LastLogonTimestamp] = Maybe(fileTimes[random.Next(fileTimes.Length)].ToString(Culture)),
                        [Attr.BadPwdCount] = random.Next(6),
                        [Attr.LogonHours] = Maybe(Enumerable.Range(0, 21).Select(_ => (byte)random.Next(256)).ToArray()),
                        [Attr.SidHistory] = random.Next(5) == 0 ? new[] { $"S-1-5-21-9-8-7-{1000 + i}" } : null,
                        [Attr.ProxyAddresses] = Words(3).Select(w => "smtp:" + w + "@contoso.test").ToArray(),
                        ["dSCorePropagationData"] = Maybe(new[] { "20260102030405.0Z", "16010101000000.0Z" }),
                        ["x-unknownBinary"] = random.Next(7) == 0 ? new byte[] { 0xFF, 0xFE, (byte)random.Next(256), 0x80 } : null,
                        ["userPKCS12"] = random.Next(9) == 0 ? new[] { new byte[] { 1, 2, 3 } } : null,
                    });
                    break;
                case < 7:
                    builder.AddComputer($"KOMPUTER{i}", ou, new Dictionary<string, object?>
                    {
                        [Attr.OperatingSystem] = Maybe(Word()),
                        [Attr.ServicePrincipalName] = Words(6).Select(w => "HOST/" + w).ToArray(),
                        [Attr.LastLogonTimestamp] = Maybe(fileTimes[random.Next(fileTimes.Length)].ToString(Culture)),
                        [Attr.UserAccountControl] = uacValues[random.Next(uacValues.Length)],
                    });
                    break;
                default:
                    builder.AddGroup($"Grupa{i}", ou, attributes: new Dictionary<string, object?>
                    {
                        [Attr.GroupType] = groupTypes[random.Next(groupTypes.Length)],
                        [Attr.Description] = Words(2),
                        [Attr.Member] = Words(4).Select((w, k) => $"CN=Członek {k} {w.Replace(";", string.Empty, StringComparison.Ordinal).Replace("\\", string.Empty, StringComparison.Ordinal)},DC=contoso,DC=test").ToArray(),
                    });
                    break;
            }
        }

        return ou;
    }
}
