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

    /// <summary>
    /// The lines of the red team of R1 (goal g) that the filter kept although each carries the secret as typed - a splatted or inline
    /// hashtable, secret names outside the first word list, abbreviations, a value written positionally to a command whose name says
    /// it takes a password - and the assignments of the audit (PS-13).
    /// </summary>
    [Theory]
    [InlineData("$h = @{ Password = 'SuperSecret1!' }; Set-Thing @h")]
    [InlineData("Set-Thing @{ ApiKey = \"k\" }")]
    [InlineData("$p = [pscustomobject]@{ 'Secret' = 'x'; Name = 'n' }")]
    [InlineData("$h = @{ Name = 'n'; Pin = 1234 }")]
    [InlineData("Set-Thing -Passphrase 'SuperSecret1!'")]
    [InlineData("Set-Thing -pwd 'SuperSecret1!'")]
    [InlineData("Set-Thing -NewPwd 'SuperSecret1!'")]
    [InlineData("Set-Thing -pass 'SuperSecret1!'")]
    [InlineData("Set-Thing -Passw 'SuperSecret1!'")]
    [InlineData("Set-Thing -Key 'SuperSecret1!'")]
    [InlineData("Set-Thing -PrivateKey 'SuperSecret1!'")]
    [InlineData("Set-Thing -PW 'SuperSecret1!'")]
    [InlineData("Set-Thing -Pin 1234")]
    [InlineData("Set-Thing -Password 'a', 'b'")]
    [InlineData("Reset-AdmPassword user 'SuperSecret1!'")]
    [InlineData("Set-ADAccountPassword jkowalski 'SuperSecret1!'")]
    [InlineData("Set-Secret -Name n 'SuperSecret1!'")]
    [InlineData("$pw = 'SuperSecret1!'")]
    [InlineData("$password = \"SuperSecret1!\"")]
    [InlineData("[string]$token = 'SuperSecret1!'")]
    [InlineData("$env:ADMIN_PASSWORD = 'SuperSecret1!'")]
    [InlineData("$cred.Password = 'SuperSecret1!'")]
    [InlineData("$settings['ApiKey'] = 'SuperSecret1!'")]
    [InlineData("if ($true) { $secret = 'SuperSecret1!' }")]
    public void A_line_that_hands_a_plain_text_secret_over_another_way_is_not_remembered(string line)
    {
        Assert.False(PowerShellHistoryPolicy.ShouldRemember(line), line);
    }

    /// <summary>What the wider rules must not catch: attribute names with "pwd" as values, -PassThru, a hashtable or an assignment without a secret.</summary>
    [Theory]
    [InlineData("Get-AdmUser jkowalski -Properties pwdLastSet, badPwdCount")]
    [InlineData("Get-AdmUser -Filter 'pwdLastSet -eq 0'")]
    [InlineData("Get-Thing | Add-Member -NotePropertyName x -NotePropertyValue 1 -PassThru")]
    [InlineData("$h = @{ Name = 'Kowalski'; Department = 'Księgowość' }; Get-AdmUser @h")]
    [InlineData("Select-Object Name, @{ Name = 'Password set'; Expression = { $_.pwdLastSet } }")]
    [InlineData("$h = @{ Password = $secure }")]
    [InlineData("$password = Read-Host -AsSecureString")]
    [InlineData("$token = $response.access_token")]
    [InlineData("$key = $entry.Key")]
    [InlineData("$name = 'Łukasz Żółć'")]
    [InlineData("Get-Help Reset-AdmPassword")]
    [InlineData("Get-Command *Password*")]
    [InlineData("Get-Secret -Name MySecret")]
    [InlineData("Get-Secret MySecret")]
    [InlineData("Reset-AdmPassword -Identity jkowalski")]
    [InlineData("Set-Thing -Credential jkowalski")]
    [InlineData("$pwd")]
    public void An_ordinary_line_near_the_secret_rules_is_remembered(string line)
    {
        Assert.True(PowerShellHistoryPolicy.ShouldRemember(line), line);
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
