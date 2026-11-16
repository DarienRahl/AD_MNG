using AdMng.PowerShell.Input;

namespace AdMng.PowerShell.Tests;

/// <summary>D5: a line with a plain-text password never enters the history.</summary>
public sealed class HistoryPolicyTests
{
    [Theory]
    [InlineData("Get-AdmVersion")]
    [InlineData("Get-ADUser -Filter * | Select-Object Name")]
    [InlineData("Set-Foo -Password $secure")]
    [InlineData("Set-Foo -Password (Read-Host -AsSecureString)")]
    [InlineData("$p = Read-Host -AsSecureString")]
    [InlineData("Connect-Foo -Token \"$t\"")]
    [InlineData("ConvertTo-SecureString $encrypted")]
    [InlineData("$encrypted | ConvertTo-SecureString")]
    [InlineData("Get-Help ConvertTo-SecureString")]
    [InlineData("Write-Host 'the password is in the vault'")]
    [InlineData("Get-Content passwords.txt")]
    [InlineData("Get-ChildItem -As")]
    public void An_ordinary_line_is_remembered(string line)
    {
        Assert.True(PowerShellHistoryPolicy.ShouldRemember(line), line);
    }

    [Theory]
    [InlineData("ConvertTo-SecureString 'hunter2' -AsPlainText -Force")]
    [InlineData("$p = ConvertTo-SecureString $env:X -AsPlainText -Force")]
    [InlineData("ConvertTo-SecureString -String 'hunter2' -AsPlainText")]
    [InlineData("ConvertTo-SecureString -AsPlain -Force 'x'")]
    [InlineData("ConvertTo-SecureString \"hunter2\"")]
    [InlineData("ConvertTo-SecureString hunter2")]
    [InlineData("New-Foo -Name a -AccountPassword 'x'")]
    [InlineData("Set-Foo -Password:'x'")]
    [InlineData("Set-Foo -NewPassword hunter2")]
    [InlineData("Set-Foo -Password 123")]
    [InlineData("Set-Foo -Password \"literal\"")]
    [InlineData("Connect-Foo -Token 'abc'")]
    [InlineData("Connect-Foo -ClientSecret \"s\"")]
    [InlineData("Connect-Foo -ApiKey k")]
    [InlineData("Get-Foo | ForEach-Object { Set-Foo -Password 'x' }")]
    [InlineData("Set-Foo -Password 'x' -AsPlainText -Force; Get-Date")]
    public void A_line_with_a_plain_text_password_is_not_remembered(string line)
    {
        Assert.False(PowerShellHistoryPolicy.ShouldRemember(line), line);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    [InlineData("'an open quote")]
    [InlineData("Get-Date |")]
    [InlineData("if ($true) {")]
    public void An_empty_or_incomplete_line_is_not_remembered(string line)
    {
        Assert.False(PowerShellHistoryPolicy.ShouldRemember(line), line);
    }

    [Fact]
    public void A_line_with_a_syntax_error_that_is_complete_is_still_checked_for_a_password()
    {
        Assert.False(PowerShellHistoryPolicy.ShouldRemember("Set-Foo -Password 'x' )"));
        Assert.True(PowerShellHistoryPolicy.ShouldRemember("Get-Date )"));
    }

    [Fact]
    public async Task The_session_gives_the_same_answer()
    {
        await using var session = await TestSessions.CreateAsync();

        Assert.True(session.ShouldRemember("Get-AdmVersion"));
        Assert.False(session.ShouldRemember("ConvertTo-SecureString 'x' -AsPlainText -Force"));
    }
}
