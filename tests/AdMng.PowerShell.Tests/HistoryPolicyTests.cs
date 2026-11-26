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

    /// <summary>
    /// The lines of the second red team of R1 (goal g) that the filter kept: the literal in parentheses or computed from literals only
    /// ('Sum' + 'mer'), the abbreviations of two and three letters that PowerShell binds to -Password, and a chained assignment.
    /// </summary>
    [Theory]
    [InlineData("$password = ('Summer2026!')")]
    [InlineData("Set-Thing -Password ('Summer2026!')")]
    [InlineData("Set-Thing -Password (('Summer2026!'))")]
    [InlineData("New-Thing -ApiKey ('xyzzy-123')")]
    [InlineData("$password = 'Sum' + 'mer'")]
    [InlineData("Set-Thing -Password ('Sum' + 'mer' + '2026!')")]
    [InlineData("$h = @{ Password = ('Summer2026!') }")]
    [InlineData("$password = $('Summer2026!')")]
    [InlineData("$password = [string]'Summer2026!'")]
    [InlineData("$password = -join ('Sum', 'mer')")]
    [InlineData("Reset-AdmPassword -Identity user ('Summer2026!')")]
    [InlineData("Set-Thing -Pas 'Summer2026!'")]
    [InlineData("Set-Thing -Pa 'Summer2026!'")]
    [InlineData("Set-Thing -pa:'Summer2026!'")]
    [InlineData("$password = $x = 'Summer2026!'")]
    [InlineData("$x = $password = 'Summer2026!'")]
    [InlineData("Set-Thing -Password ($x = 'Summer2026!')")]
    public void A_line_that_hides_a_plain_text_secret_in_an_expression_or_a_short_name_is_not_remembered(string line)
    {
        Assert.False(PowerShellHistoryPolicy.ShouldRemember(line), line);
    }

    /// <summary>
    /// The lines of the third red team of R1 (goal g) that the filter kept: <c>-As</c> and <c>-A</c> bind to <c>-AsPlainText</c> of
    /// ConvertTo-SecureString (the secret piped in), an interpolated hashtable key or index that begins with a secret's name, and a
    /// literal cast to SecureString.
    /// </summary>
    [Theory]
    [InlineData("'P@ssw0rd!' | ConvertTo-SecureString -As -Force")]
    [InlineData("$v = 'P@ssw0rd!' | ConvertTo-SecureString -A -Force")]
    [InlineData("'P@ssw0rd!' | Microsoft.PowerShell.Security\\ConvertTo-SecureString -as -Force")]
    [InlineData("$h = @{ \"Pass$x\" = 'P@ssw0rd!' }")]
    [InlineData("$h = @{ \"Key$i\" = 'P@ssw0rd!' }")]
    [InlineData("$h[\"Pass$x\"] = 'P@ssw0rd!'")]
    [InlineData("$o.\"Pass$x\" = 'P@ssw0rd!'")]
    [InlineData("[securestring]$x = 'P@ssw0rd!'")]
    [InlineData("[System.Security.SecureString]$x = 'P@ssw0rd!'")]
    [InlineData("$x = [securestring]'P@ssw0rd!'")]
    [InlineData("Set-Thing -Value ([Security.SecureString]('P@ss' + 'w0rd!'))")]
    public void A_line_of_the_third_red_team_is_not_remembered(string line)
    {
        Assert.False(PowerShellHistoryPolicy.ShouldRemember(line), line);
    }

    /// <summary>What the rules of the third red team must not catch: <c>-A</c> of another command, an interpolated key without a secret's name, a SecureString that is no literal.</summary>
    [Theory]
    [InlineData("Get-Thing -A 1")]
    [InlineData("Get-ChildItem -As")]
    [InlineData("$encrypted | ConvertTo-SecureString -Key $key")]
    [InlineData("$h = @{ \"Name$x\" = 'Kowalski' }")]
    [InlineData("$h[\"Department$i\"] = 'Księgowość'")]
    [InlineData("[securestring]$x = Read-Host -AsSecureString")]
    [InlineData("$x = [securestring]$secure")]
    [InlineData("[string]$x = 'Łukasz'")]
    public void An_ordinary_line_near_the_rules_of_the_third_red_team_is_remembered(string line)
    {
        Assert.True(PowerShellHistoryPolicy.ShouldRemember(line), line);
    }

    /// <summary>
    /// The third red team of R1: 5 000 nested parentheses threw out of the filter where the parser gave up on them (no syntax tree); on the
    /// Release build of CI it made a tree and the line was remembered. It is not remembered, either way.
    /// </summary>
    [Fact]
    public void The_red_teams_deeply_nested_line_is_not_remembered()
    {
        var line = "$x = " + new string('(', 5000) + "1" + new string(')', 5000);

        Assert.False(PowerShellHistoryPolicy.ShouldRemember(line));
    }

    /// <summary>A line past the parser's limits (InputParser) is not remembered: the guard refuses it, and it could not be looked into.</summary>
    [Fact]
    public void A_line_past_the_parsers_limits_is_not_remembered()
    {
        Assert.True(PowerShellHistoryPolicy.ShouldRemember("$x = 1" + string.Concat(Enumerable.Repeat("+1", InputParser.MaxDepth - 10))));
        Assert.False(PowerShellHistoryPolicy.ShouldRemember("$x = 1" + string.Concat(Enumerable.Repeat("+1", InputParser.MaxDepth))));
        Assert.False(PowerShellHistoryPolicy.ShouldRemember("'" + new string('a', InputParser.MaxLength) + "'"));
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
    [InlineData("Get-AdmUser ('jkowalski')")]
    [InlineData("$name = ('Łukasz' + ' Żółć')")]
    [InlineData("$total = 1 + 2")]
    [InlineData("Get-Thing -Path ('C:\\dane')")]
    [InlineData("Get-Thing -P 'x'")]
    [InlineData("$password = ($secure)")]
    [InlineData("$password = 'prefix' + $secure")]
    [InlineData("$password = $x = Read-Host -AsSecureString")]
    public void An_ordinary_line_near_the_secret_rules_is_remembered(string line)
    {
        Assert.True(PowerShellHistoryPolicy.ShouldRemember(line), line);
    }

    /// <summary>
    /// What audit PS-13 found besides the red teams' lines: a literal that reaches a secret through a variable of the same line, a
    /// credential built from a literal, and console programs - a password after <c>net user</c>, a switch with a secret's name and a
    /// value (<c>/pass:x</c>, <c>/pd:x</c>, <c>/p:x</c>) or followed by one (<c>/rp x</c>, <c>psexec -p x</c>).
    /// </summary>
    [Theory]
    [InlineData("$p = 'P@ssw0rd!'; Set-Thing -Password $p")]
    [InlineData("$p = 'x'; $h = @{ Password = $p }; Set-Thing @h")]
    [InlineData("$p = 'x'; ConvertTo-SecureString $p")]
    [InlineData("$p = 'P@ssw0rd!'; $cred = New-Object System.Management.Automation.PSCredential('jan', $p)")]
    [InlineData("Set-Thing -Credential (New-Object PSCredential 'jan', 'P@ssw0rd!')")]
    [InlineData("New-Object -TypeName PSCredential -ArgumentList 'jan', 'P@ssw0rd!'")]
    [InlineData("$c = [pscredential]::new('jan', 'P@ssw0rd!')")]
    [InlineData("net user jan P@ssw0rd! /domain")]
    [InlineData("net user jan 'P@ssw0rd!' /add")]
    [InlineData("net.exe USER jan P@ssw0rd!")]
    [InlineData("net use Z: \\\\srv\\share P@ssw0rd! /user:contoso\\jan")]
    [InlineData("cmdkey /add:server /user:admin /pass:P@ssw0rd!")]
    [InlineData("netdom resetpwd /s:dc1 /ud:contoso\\admin /pd:P@ssw0rd!")]
    [InlineData("netdom join pc1 /domain:contoso /userd:admin /passwordd:P@ssw0rd!")]
    [InlineData("ldp.exe /p:P@ssw0rd!")]
    [InlineData("wmic /node:pc1 /user:admin /password:P@ssw0rd! os get caption")]
    [InlineData("schtasks /create /tn t /ru admin /rp P@ssw0rd!")]
    [InlineData("psexec \\\\pc -u admin -p P@ssw0rd! cmd")]
    [InlineData("sqlcmd.exe -S db -U sa -P P@ssw0rd!")]
    public void A_line_that_carries_a_plain_text_secret_through_a_variable_a_credential_or_a_console_program_is_not_remembered(string line)
    {
        Assert.False(PowerShellHistoryPolicy.ShouldRemember(line), line);
    }

    /// <summary>What the rules of audit PS-13 must not catch: a variable set from elsewhere, console programs without a secret, <c>-p</c> of a cmdlet.</summary>
    [Theory]
    [InlineData("$p = Read-Host -AsSecureString; Set-Thing -Password $p")]
    [InlineData("$name = 'jan'; Get-AdmUser $name")]
    [InlineData("$c = New-Object PSCredential 'jan', $secure")]
    [InlineData("net user jan /domain")]
    [InlineData("net user jan * /domain")]
    [InlineData("net use Z: \\\\srv\\share /user:contoso\\jan")]
    [InlineData("net group \"Domain Admins\" /domain")]
    [InlineData("runas /user:contoso\\jan cmd")]
    [InlineData("nltest /dsgetdc:contoso.test")]
    [InlineData("whoami /groups")]
    [InlineData("schtasks /query /tn t")]
    [InlineData("psexec \\\\pc -u admin cmd")]
    [InlineData("Get-ChildItem -p 'C:\\dane'")]
    [InlineData("robocopy C:\\a D:\\b /mt:8 /r:1")]
    public void An_ordinary_line_near_the_rules_of_audit_PS13_is_remembered(string line)
    {
        Assert.True(PowerShellHistoryPolicy.ShouldRemember(line), line);
    }

    /// <summary>
    /// The scrollback shows every command it ran (audit PS-13 asked what it shows of a line the history refuses): the secret's literal
    /// is masked there, the rest of the line is as typed.
    /// </summary>
    [Theory]
    [InlineData("Set-Foo -Password 'hunter2' -Name jan", "Set-Foo -Password *** -Name jan")]
    [InlineData("ConvertTo-SecureString 'hunter2' -AsPlainText -Force", "ConvertTo-SecureString *** -AsPlainText -Force")]
    [InlineData("'hunter2' | ConvertTo-SecureString -AsPlainText -Force", "*** | ConvertTo-SecureString -AsPlainText -Force")]
    [InlineData("$h = @{ Name = 'jan'; Password = 'hunter2' }; Set-Thing @h", "$h = @{ Name = 'jan'; Password = *** }; Set-Thing @h")]
    [InlineData("$password = 'hunter2'", "$password = ***")]
    [InlineData("$p = 'hunter2'; Set-Thing -Password $p", "$p = ***; Set-Thing -Password $p")]
    [InlineData("Set-Thing -Value ([securestring]'hunter2')", "Set-Thing -Value ([securestring]***)")]
    [InlineData("net user jan hunter2 /domain", "net user jan *** /domain")]
    [InlineData("cmdkey /add:server /user:admin /pass:hunter2", "cmdkey /add:server /user:admin /pass:***")]
    [InlineData("schtasks /create /ru admin /rp hunter2", "schtasks /create /ru admin /rp ***")]
    [InlineData("Get-AdmUser jan", "Get-AdmUser jan")]
    [InlineData("$p = ConvertTo-SecureString $env:X -AsPlainText -Force", "$p = ConvertTo-SecureString $env:X -AsPlainText -Force")]
    public void The_echo_of_a_line_masks_its_plain_text_secret(string line, string echo)
    {
        Assert.Equal(echo, PowerShellHistoryPolicy.HideSecrets(line));
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
        Assert.Equal("ConvertTo-SecureString *** -AsPlainText -Force", session.HideSecrets("ConvertTo-SecureString 'x' -AsPlainText -Force"));
    }
}
