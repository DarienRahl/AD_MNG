using System.Globalization;
using System.Reflection;
using AdMng.Core.Directory;
using AdMng.PowerShell.Reading;
using AdMng.Testing;

namespace AdMng.PowerShell.Tests.Reading;

/// <summary>
/// Audit ps-commands-8: the errors and warnings of AD Manager's commands were English in a Polish window. They follow the window's
/// language now (the culture of the session's host); the help of the commands stays English, like PowerShell's own.
/// </summary>
public sealed class CommandTextTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    [Fact]
    public async Task In_a_Polish_window_the_errors_and_warnings_of_the_commands_are_Polish()
    {
        var services = new RecordingWindowServices();
        await using var harness = await ReadHarness.CreateAsync(services: services, culture: Polish);

        var missing = await harness.RunAsync("Get-AdmUser -Identity nobody.here");
        var group = await harness.RunAsync("Get-AdmGroupMember -Identity nobody.here");
        var property = await harness.RunAsync("Get-AdmUser -Properties noSuchAttribute");
        var empty = await harness.RunAsync("@() | Export-AdmList");
        var grid = await harness.RunAsync("@() | Out-AdmGrid");

        Assert.Contains("Nie znaleziono użytkownika o tożsamości 'nobody.here' w 'DC=contoso,DC=test': nie istnieje albo to konto go nie widzi.", missing.Errors, StringComparison.Ordinal);
        Assert.Contains("Nie znaleziono grupy o tożsamości 'nobody.here'", group.Errors, StringComparison.Ordinal);
        Assert.Contains("Schemat tej domeny nie ma atrybutu 'noSuchAttribute'.", property.Errors, StringComparison.Ordinal);
        Assert.Contains("Nie ma obiektów do eksportu: nic nie zapisano.", empty.Warnings, StringComparison.Ordinal);
        Assert.Contains("Nie ma obiektów do pokazania: nie otwarto siatki.", grid.Warnings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_directory_failure_in_a_Polish_window_says_so_in_Polish_and_keeps_its_id()
    {
        await using var harness = await ReadHarness.CreateAsync(culture: Polish);
        harness.Directory.FailNext(FakeOperationKind.Search, null, DirectoryErrorKind.Timeout);

        var result = await harness.RunAsync("Get-AdmUser -ErrorVariable e -ErrorAction SilentlyContinue; $e[0].FullyQualifiedErrorId.Split(',')[0]; $e[0].Exception.Message");

        Assert.Equal("AdmTimeout", result.Objects[0].ToString());
        Assert.StartsWith("Kontroler domeny nie odpowiedział na odczyt 'DC=contoso,DC=test'.", result.Objects[1].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task In_an_English_window_the_messages_are_English()
    {
        await using var harness = await ReadHarness.CreateAsync(culture: English);

        var missing = await harness.RunAsync("Get-AdmUser -Identity nobody.here");

        Assert.Contains("Cannot find a user with identity 'nobody.here' under 'DC=contoso,DC=test': it does not exist, or this account cannot see it.", missing.Errors, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "Nie udało się odczytać 1 członka grupy 'G'")]
    [InlineData(3, "Nie udało się odczytać 3 członków grupy 'G'")]
    [InlineData(12, "Nie udało się odczytać 12 członków grupy 'G'")]
    public void A_count_before_a_noun_has_its_Polish_plural_form(int count, string expected)
    {
        Assert.StartsWith(expected, AdmText.Plural(Polish, AdmText.Keys.MembersMissing, count, "G", "x"), StringComparison.Ordinal);
        Assert.StartsWith(count == 1 ? "1 member of 'G'" : $"{count} members of 'G'", AdmText.Plural(English, AdmText.Keys.MembersMissing, count, "G", "x"), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_key_has_a_text_in_both_languages()
    {
        var keys = typeof(AdmText.Keys).GetFields(BindingFlags.Public | BindingFlags.Static).Select(field => (string)field.GetValue(null)!).ToList();
        var plurals = new[] { AdmText.Keys.MembersMissing, AdmText.Keys.CellBinary };

        Assert.True(keys.Count > 30, $"{keys.Count} keys");
        foreach (var key in keys.Except(plurals).Concat(plurals.SelectMany(p => new[] { p + ".One", p + ".Few", p + ".Many" })))
        {
            Assert.False(AdmText.Get(English, key).StartsWith('['), key);
            Assert.False(AdmText.Get(Polish, key).StartsWith('['), key);
            Assert.NotEqual(AdmText.Get(English, key), AdmText.Get(Polish, key));
        }
    }
}
