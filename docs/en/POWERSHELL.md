**English** · [Polski](../POWERSHELL.md)

# The PowerShell window in AD Manager (stage R1)

This document describes the **PowerShell** window: a command line next to the connection window, with AD Manager's own
**read commands** (`Get-AdmUser`, `Search-AdmObject` and the others), results that are ordinary PowerShell objects, a grid
for them, and the rules that keep it safe. Stage R1 is **reads only**: no command changes the directory (chapter 9).
Technical design: [docs/dev/r1-powershell-window.md](../dev/r1-powershell-window.md); the owner's decisions and their
reasons: [ADR-0010](adr/0010-powershell-window-embedded-engine.md).

Rules that apply everywhere:

- **Nothing from the directory goes to disk.** The text in the window, the results and the command history exist only in
  the memory of the window and disappear with it. A file is created only when the operator names it or chooses it in
  *Save As* (chapter 5). Group Policy can change this from outside the program – chapter 8.
- **No command writes to the directory.** Writes go through the change plan (preview, audit, rollback); the commands that
  will add operations to it are stage R2.
- **One domain controller per window.** The commands read through the window's own session: the same domain controller and
  the same account as the window they belong to. They have no `-Server` and no `-Credential` parameter, and a script in the
  default mode cannot reach the window's session or its password (chapter 7).
- **Escaped values.** Whatever you type in a parameter is a value, never a piece of a filter (chapter 3).

## 1. What the window is and how to open it

*Tools → PowerShell…* in a connection window, **Ctrl+Shift+P**, or the command palette (`tools.powershell`). Each connection
window has one PowerShell window; it belongs to that connection, shows its domain, domain controller and account in the
title (*contoso.test – dc01.contoso.test – CONTOSO\admin – PowerShell*), and closes with it. Windows of two connections do
not share anything: each has its own PowerShell session (a *runspace*) and its own variables.

The window runs **PowerShell 7.6 built into AD Manager**. You do not need `pwsh.exe`, Windows PowerShell or the RSAT modules,
and you do not sign in again. The first window of a program run takes under a second to start (the status line says
*Starting PowerShell…*); the next ones are quick.

From top to bottom the window has:

- the **status line**: what the window is doing (*Running the command…*, *The command finished: 12 lines of output in the
  window, 0.8 s*, *The command was stopped after 3 s*) with **Stop**; errors and warnings have an icon, not only a colour;
- **notes that last** – what Group Policy records (chapter 8), *Trusted mode is on* (chapter 7), and *The oldest N lines
  were removed from this window* (the window keeps the last 20,000 lines);
- the **output area**: the commands you ran and what they wrote, with errors and warnings in the window's colours;
- the **command line** with *Run* (while a command runs it is *Stop*), *Clear* and the *Trusted mode (FullLanguage)* switch.

When the window opens it writes two lines: which language mode it is in and how to find the commands
(`Get-Command *-Adm*`, `Get-Help <command>`), with the name of this guide. A place under the output is reserved for the
pending change plan of stage R2; in R1 it stays empty and hidden.

Closing the window while a command runs asks *Stop the running operation and close the window?* – whatever you typed can do
anything in the trusted mode, so the window treats a running command like any operation that may write.

## 2. Commands

The names are the same in the English and the Polish interface. `Get-Command *-Adm*` lists them, `Get-Help <command>
-Examples` shows the help with examples (in English). The commands have no `-Server` and no `-Credential` parameter: they
always use the window's session.

| Command | What it does | Example |
|---|---|---|
| `Get-AdmVersion` | The versions of AD Manager and of the engine, the language mode and the domain, controller and account of the window | `Get-AdmVersion` |
| `Get-AdmObject` | One object by identity, or the objects a ready LDAP filter finds | `Get-AdmObject 'OU=IT,OU=Pracownicy,OU=Firma,DC=contoso,DC=test' -Properties description` |
| `Get-AdmUser` | User accounts, by identity or by filters | `Get-AdmUser -Department 'Księgowość' -Enabled $true -Properties mail,title` |
| `Get-AdmGroup` | Groups, by identity or by filters | `Get-AdmGroup -Name 'GG_*' -GroupScope Global -GroupCategory Security` |
| `Get-AdmComputer` | Computer accounts, by identity or by filters | `Get-AdmComputer -OperatingSystem 'Windows 11*' -Enabled $true` |
| `Get-AdmGroupMember` | The members of a group; with `-Recursive` also those of nested groups | `Get-AdmGroupMember GG_IT -Recursive` |
| `Search-AdmObject` | Objects by the value of an attribute, or by a ready LDAP filter | `Search-AdmObject -Attribute description -Value 'Łukasz (Kraków)*' -Match StartsWith` |
| `Out-AdmGrid` | The objects of the pipeline in a grid window (chapter 4) | `Get-AdmUser -Department IT \| Out-AdmGrid -Title 'IT staff'` |
| `Open-AdmObject` | Opens the properties of an object in the program | `Open-AdmObject jan.kowalski` |
| `Export-AdmList` | The objects of the pipeline to a CSV file (chapter 5) | `Get-AdmUser -Department IT \| Export-AdmList C:\Temp\it.csv` |

**Identity** (`-Identity`, the first parameter of `Get-AdmObject`, `Get-AdmUser`, `Get-AdmGroup`, `Get-AdmComputer`,
`Get-AdmGroupMember` and `Open-AdmObject`) is recognised by its shape, not guessed: a distinguished name (read where it is),
an `objectGUID` (also `<GUID=…>`), a SID (`S-1-5-…`, also `<SID=…>`), otherwise a `sAMAccountName` (a user or object also
by user principal name; a computer with or without the `$`). The type of the command is part of the search:
`Get-AdmUser` returns only user accounts, so a computer with the same name is never returned by it. An identity that finds
nothing is an error that says so (*Cannot find a user with identity 'nobody' …: it does not exist, or this account cannot
see it*). A distinguished name with a comma in a value must be quoted: `'CN=Kowalski\, Jan,OU=IT,DC=contoso,DC=test'`.

**Filters** of `Get-AdmUser`, `Get-AdmGroup` and `Get-AdmComputer`: each parameter narrows the result (they are combined
with *and*) and takes `*` as a wildcard.

- Users: `-Name`, `-SamAccountName`, `-UserPrincipalName`, `-GivenName`, `-Surname`, `-DisplayName`, `-Mail`, `-Department`,
  `-Title`, `-Company`, `-Description`, `-Enabled`.
- Groups: `-Name`, `-SamAccountName`, `-Description`, `-GroupScope` (`DomainLocal`, `Global`, `Universal`), `-GroupCategory`
  (`Security`, `Distribution`).
- Computers: `-Name`, `-SamAccountName`, `-DnsHostName`, `-OperatingSystem`, `-Location`, `-Description`, `-Enabled`.
- Each of the three also takes `-LdapFilter`, a ready LDAP filter that is added to the others. Its syntax is checked first
  (the same check as in the *Find* window), so a mistake is an error before anything is read.

**Common parameters** of the read commands:

- `-Properties name1,name2` – attributes to return besides the usual ones (names from the schema, completed with Tab; an
  unknown name is an error before anything is read; `*` asks for everything the account may read);
- `-SearchBase 'OU=…'` and `-SearchScope Base|OneLevel|Subtree` (alias `-Scope`) – where to search; Tab completes the
  domain's naming contexts and the containers the window has already returned;
- `-ResultPageSize` (1–10000, 500 by default) and `-ResultSetSize` (0 = all) – the size of a page and the limit of the result.

**`Get-AdmGroupMember`.** Without `-Recursive`: the direct members (a group of any size, also beyond 1,500 members).
With `-Recursive`: the same list as *All (nested)* on the *Members* page of the program – the domain controller evaluates
the nesting, a cycle of groups ends it, and **the nested groups themselves are in the result**; `-ExcludeGroups` leaves them
out and gives only the accounts and computers. Members whose *primary group* is this group are not members here (as on the
*Members* tab).

**`Search-AdmObject`.** `Search-AdmObject <name>` looks for objects whose `name` is exactly that; `-Attribute a,b -Value x,y`
looks for objects where any of the attributes has any of the values; `-Match Exact|StartsWith|Contains|EndsWith` says how
the value is compared; `-ObjectClass` narrows the type; `-Filter '(…)'` is a ready LDAP filter. It refuses to compare
secret attributes (for example the LAPS password): a search could be used to guess them.

**The usual cmdlets for objects work as in any PowerShell.** `Where-Object`, `Select-Object`, `Sort-Object`, `Group-Object`,
`Measure-Object`, `Format-Table`, `Format-List`, `ForEach-Object`, variables, `if`, `foreach` and the like – the window has
a fixed list of commands, given in chapter 7. A script block is typed directly, e.g.
`Get-AdmUser -Enabled $true | Where-Object { $_.LockedOut } | Select-Object Name, sAMAccountName`.

A real session (the sample company of the test directory):

```text
PS> Get-AdmVersion

ApplicationVersion : 1.2.3-test
EngineVersion      : 7.6.10
LanguageMode       : Constrained
Domain             : contoso.test
DomainController   : dc01.contoso.test
Account            : CONTOSO\Administrator

PS> Get-AdmUser -Department 'Księgowość' -Enabled $true

Name                   sAMAccountName       Enabled DistinguishedName
----                   --------------       ------- -----------------
Małgorzata Wójcik      malgorzata.wojcik    True    CN=Małgorzata Wójcik,OU=Księgowość,OU=Pracownicy,OU=Firma,DC=conto…
Grażyna Dąbrowska      grazyna.dabrowska    True    CN=Grażyna Dąbrowska,OU=Księgowość,OU=Pracownicy,OU=Firma,DC=conto…

PS> Get-AdmUser -Identity nobody
Get-AdmUser: Cannot find a user with identity 'nobody' under 'DC=contoso,DC=test': it does not exist, or this account cannot see it.
```

## 3. Escaping and wildcards

The commands never paste your text into a filter. Every value is escaped by the same code the program's search box uses
(RFC 4515), so a parenthesis, an asterisk, a backslash, a quote or a Polish letter in a value is just a character.

- In `Get-AdmUser`, `Get-AdmGroup` and `Get-AdmComputer` the **asterisk is a wildcard** (`-Name 'Jan*'` is what an
  administrator means); everything else in the value is literal.
- In `Search-AdmObject` **no character is a wildcard**: the value is taken literally and `-Match` says how it is compared.
  `Search-AdmObject 'Łukasz (Kraków)*\'` finds exactly an object whose name is `Łukasz (Kraków)*\`, nothing else.
  For a "starts with" search, use `-Match StartsWith`.
- A ready filter (`-LdapFilter`, `-Filter`) is **yours**: it is checked for syntax and sent as written. Escape its values
  yourself (`\28` for `(`, `\29` for `)`, `\2a` for `*`, `\5c` for `\`).

PowerShell has its own rules for what you type:

- Prefer **single quotes**. In double quotes `$` starts a variable: `"CN=$name"` inserts the variable `$name`, while `'CN=$name'`
  is the text as written. A single quote inside single quotes is doubled: `'O''Brien'`.
- A value with a space, a comma or a parenthesis needs quotes: `-Name 'Jan Kowalski'`.
- A path with brackets, such as `C:\Temp\[2026]\it.csv`, is taken literally by `Export-AdmList -Path`; other cmdlets read
  brackets in `-Path` as a wildcard (use `-LiteralPath`).

## 4. Results and the grid

**The results are objects.** What the window shows is the engine's own formatting of them (a table or a list), but
underneath they are real objects: `Where-Object`, `Select-Object`, `Sort-Object` and the rest work on them. Every object has
`DistinguishedName`, `Name` and `ObjectKind`; accounts have `Enabled`, users `LockedOut` and `UserAccountControlFlags`
(the flag names, e.g. `NORMAL_ACCOUNT`); groups have `GroupScope`, `GroupCategory` and `GroupTypeFlags`. Then come the
attributes under their LDAP names (`sAMAccountName`, `mail`, `memberOf`). `Get-Member` lists them.

**Values are decoded as in the program's windows**, and a test compares them for 1,000 objects:

- a *yes/no* attribute is `$true` / `$false`; numbers are numbers (`userAccountControl` as a number: you can compare its bits);
- dates are `DateTime` in local time; "never" (zero, the largest value) is `$null`;
- `objectGUID` is a `Guid`, a SID is text (`S-1-5-21-…`); binary values are bytes;
- an attribute with many values is an array (an empty array when it has none); an attribute without a value is `$null`;
- **secrets are masked** as in the *Attribute Editor*: key material, managed and encrypted passwords and the like show the
  program's mask text. The secrets the Attribute Editor shows to the operator (the clear-text LAPS password, a BitLocker
  recovery password) are shown here too – **do not read them in a window while Group Policy records commands and results**
  (chapter 8).

**The table and the distinguished name.** The default tables end with the `DistinguishedName` column, which gets the rest
of the line. A name longer than the line is cut by the engine with `…` (*Format-List*, a wider window or `Select-Object
-ExpandProperty DistinguishedName` show it whole). A name that is whole in the output is a **link**: click it to open the
object in the program (*Open in the program*), right-click for *Copy the distinguished name*; **Enter** on a selected line opens
the first name of that line. A cut name is not a link.

**Many results.** The results arrive as they are read: 100,000 objects do not block the window, and **Stop** (or **Ctrl+C**)
ends the read at the domain controller. `Select-Object -First 50` stops the read after 50 objects. The output area keeps the
last 20,000 lines.

**`Out-AdmGrid`** opens the objects of the pipeline in a **window of its own** (each call is a new window, like
`Out-GridView`; it belongs to the connection window and closes with it):

- the columns are the properties of the objects, the distinguished name first; dates are shown as `2026-11-16 08:30:00`, lists
  joined with `; `, binary values as `(binary, 12 bytes)`;
- a click on a column header sorts (numbers as numbers, empty cells last); the filter box above the list finds text in any
  column (case and Polish letters do not matter);
- **Enter** or a double click opens the object in the program (at most 10 windows at once); the right-click menu has *Open in
  the program*, *Copy rows*, *Copy rows with the column names*, *Copy distinguished name* and *Export list…*;
  **Ctrl+C** copies the selected rows (tab-separated, formulas protected as in the program's lists);
- *Export list…* writes CSV to a file you choose – chapter 5.

**`Open-AdmObject`** takes an identity or a distinguished name from the pipeline:
`Get-AdmGroup GG_IT | Open-AdmObject`. One command opens at most 10 property windows; a name that does not exist is an error
and no window opens for it.

## 5. Exporting

A file is written in exactly three cases, and only when you ask:

- **`Export-AdmList`** writes the objects of the pipeline as CSV the way the program exports a list: **formulas are
  protected** (a value that starts with `=`, `+`, `-` or `@` cannot run in Excel), the separator is the one of the regional
  settings (`-Delimiter ';'` overrides it), UTF-8 with a byte order mark. It writes **only to the file you name** with
  `-Path` (the first parameter) **or choose in *Save As***, when you give no path. It never writes to a default place; an
  existing file is replaced only with `-Force`; without a path and with the dialog cancelled it writes nothing and says so.
  It prints the path and the number of rows and columns, never the values.
- ***Export list…*** in the grid window of `Out-AdmGrid`: the same CSV, to the file you choose.
- **`Start-Transcript -Path <file>`** (or `-LiteralPath`) records the session – the commands and everything they show, so
  directory data too – to the file you name, until `Stop-Transcript` or until the window closes. Without a path it is refused:
  the engine would write to a file it names itself in your *Documents* folder. There is no formula protection; treat the file like an
  export.

No other command writes a file. `Export-Csv`, `Export-Clixml`, `Out-File`, `Set-Content` and `Add-Content` answer with a message
that says so, and redirection to a file (`>`, `>>`, `2>`) is refused before the command runs (`> $null` still discards output,
`2>&1` still merges the streams). For a CSV use `Export-AdmList`.

## 6. History and what stays in memory

- **The history of commands is only in the memory of the window** – the last 500 lines, no duplicates in a row. **Up** and
  **Down** walk through it (first the lines of a multi-line command), **Ctrl+R** searches it (*History search: …*; Enter takes
  the match, Esc cancels). It is not saved and is gone when the window closes. *Clear* (Ctrl+L) clears the output, not the
  history.
- **A line with a plain-text password is not remembered:** one with `-AsPlainText`, a `ConvertTo-SecureString 'literal'`, or a
  literal value for a secret's name – of a parameter (`-Password 'literal'`), a key of a hashtable (`@{ Password = 'literal' }`,
  also one passed as `@h`) or a variable or property (`$password = 'literal'`, `$cred.Password = 'literal'`). A secret's name
  is one that contains `Password`, `Passwd`, `Passphrase`, `Pwd`, `Secret`, `Token`, `ApiKey` or `PrivateKey`, the whole name
  `Pw`, `Pin` or `Key`, and the abbreviation `-Pass`. A line with a value written without a parameter name to a command whose
  name speaks of a password (`Reset-AdmPassword jan 'literal'`) is not remembered either. A variable or
  `Read-Host -AsSecureString` as the value is fine. The filter does not recognise every password: a value without a parameter
  name for another command, a password inside a longer text, an argument of a console program (`net user jan literal`) or a
  name outside the list (a Polish one, for example: `$haslo = 'literal'` is remembered). The line you typed also stays visible
  in the output area until you clear it or close the window – and Group Policy may record it (chapter 8) – so do not type a
  password into a command at all. A
  password asked for by `Read-Host -AsSecureString` or a mandatory parameter is typed into a password box and never shown.
- **The program writes nothing else.** PSReadLine is not loaded (no history file), no profile is run (`$PROFILE` is never
  loaded), the engine's telemetry and diagnostics channel are off, module auto-loading and the module analysis cache are
  off, and a test checks that the engine writes no file of its own (on Windows only in the folders a variable can redirect, so
  the settings above are what carries the proof). The program's own diagnostic log never holds a command or a value.

## 7. Language modes and the trusted mode

**The window starts in *ConstrainedLanguage*.** AD Manager's commands, the usual cmdlets, variables, loops, conditions and
script blocks work; what is restricted is arbitrary .NET:

- `[pscustomobject]@{ a = 1 }` is refused (*Only core types are supported in this language mode*); use `New-Object PSObject
  -Property @{ a = 1 }` or `Select-Object` with calculated properties (`@{ Name = 'x'; Expression = { … } }`);
- a method call on a type that is not a core type, such as `[System.IO.File]::ReadAllText(…)` or `$PSCmdlet.ShouldProcess()`,
  is refused (*Method invocation is supported only on core types in this language mode*); defining a new type
  (`Add-Type -TypeDefinition`) is refused;
- `Format-*`, `Out-String`, `Select-Object`, `Sort-Object`, `Group-Object` and `Measure-Object` work.

**The window has a fixed list of commands** – the same in both modes, on every workstation:

- AD Manager's commands (chapter 2);
- objects and the pipeline: `Where-Object`, `Select-Object`, `Sort-Object`, `Group-Object`, `Measure-Object`, `Compare-Object`,
  `ForEach-Object` (without `-Parallel`, `-AsJob` and the other parallel parameters), `Tee-Object` (into a variable,
  `-Variable`), `Get-Unique`, `Join-String`, `Get-Member`, `Add-Member`, `New-Object` (without `-ComObject`);
- showing: `Format-Table`, `Format-List`, `Format-Wide`, `Format-Custom`, `Format-Hex`, `Out-String`, `Out-Host`, `Out-Null`,
  `Out-Default`;
- data in memory: `ConvertTo-Csv`, `ConvertFrom-Csv`, `ConvertTo-Json`, `ConvertFrom-Json`, `ConvertTo-SecureString`;
- files and locations, reading only: `Get-Content`, `Import-Csv`, `Select-String`, `Test-Path`, `Get-ChildItem`, `Get-Item`,
  `Resolve-Path`, `Join-Path`, `Split-Path`, `Get-Location`, `Set-Location`, `Push-Location`, `Pop-Location`, `Get-PSDrive`;
- variables and aliases: `Get-Variable`, `Set-Variable`, `New-Variable`, `Remove-Variable`, `Clear-Variable`, `Get-Alias`,
  `Set-Alias`, `New-Alias`;
- help and values: `Get-Command`, `Get-Help` (also `help`, without a pager), `Get-Date`, `New-TimeSpan`, `Get-Random`, `New-Guid`,
  `Get-Culture`, `Get-UICulture`, `Get-ExecutionPolicy`;
- messages and questions: `Write-Host`, `Write-Output`, `Write-Error`, `Write-Warning`, `Write-Verbose`, `Write-Debug`,
  `Write-Information`, `Write-Progress`, `Read-Host`;
- scripts typed in the window: `Start-Sleep`, `Measure-Command`, `Set-StrictMode`, `Clear-Host` (`cls`); the record of the
  session: `Start-Transcript -Path` and `Stop-Transcript` (chapter 5).

Nothing else is there. There are no background jobs, threads or runspaces (`Start-Job`, `ForEach-Object -Parallel`, the `&` at
the end of a pipeline), no remote sessions (`Invoke-Command`, `Enter-PSSession`), no `Invoke-Expression`, no `Start-Process`,
no loading of modules or assemblies (`Import-Module`, `using module`, `using assembly`, `Add-Type`), no events, no debugger, no
`Update-Help`, no `Set-ExecutionPolicy` and no command that writes a file (chapter 5). A few names that are easy to type out of
habit answer with a message that says why and what to use instead: `Start-Job`, `Import-Module`, `Invoke-Command`,
`Invoke-Expression`, `Get-Credential` (the window works with the credentials of its connection), `Start-Process`, `Out-GridView`
(use `Out-AdmGrid`), `Export-Csv`, `Export-Clixml`, `Out-File`, `Set-Content` and `Add-Content`; the others are *not recognized*.
`using module`, `using assembly`, the `&` at the end of a pipeline, redirection to a file and a typed `ForEach-Object -Parallel`
are refused before anything of the command runs. Script files (`.ps1`) do not run – on Windows the window refuses them whatever
the execution policy says. There is no profile and no PSReadLine. No job outlives the command that started it: a job left
behind is stopped when the command ends, on *Stop* and when the window closes. Console programs (`whoami`, `nltest`,
`repadmin`) run as in any console, as a separate process with your own Windows identity – never with the window's
credentials.

The commands that ask for something – `Read-Host`, a mandatory parameter, `-Confirm` – ask in the window, and *Stop* cancels the
question.

**The trusted mode (FullLanguage)** is a switch under the command line:

- turning it **on** asks first. The question names the domain and says what the mode allows: any .NET code, and changes to the
  directory made directly, **around the change plan** – without AD Manager's preview, audit and rollback. Nothing in R1 stops a
  script from writing in this mode (the commands of R1 only read, but .NET can do more): .NET code reaches anything in the
  AD Manager process, the window's directory session and its credentials included;
- it adds no command: the list above is the same. A job that .NET code puts into the session (the engine's own
  `ForEach-Object -Parallel -AsJob`, reached through .NET) is stopped with its command like any other; a thread or a runspace
  that .NET code makes for itself is beyond the window's reach, like the rest of the process;
- while it is on, the title says *– trusted mode* and a note that lasts says the same; turning it off needs no question;
- it applies to this window only and **is never remembered**: a new window starts in ConstrainedLanguage again;
- it cannot be changed while a command runs, nor before PowerShell has started;
- when the system enforces ConstrainedLanguage (WDAC or AppLocker), the window stays in it and the switch is **disabled** –
  its tool tip gives the reason.

In ConstrainedLanguage no command of the window runs code in another language mode, and nothing in the session – no variable, no
parameter, nothing of `$Host` – hands a script the window's directory session or its credentials: AD Manager's commands find
their window in a table of the engine that a script cannot read.

The language mode and the list of commands are **a protection against mistakes, not a security boundary.** The boundary is the
rights of the window's account in AD: the window never gives more than the account has, and a change made around the plan has no
audit or rollback in the program (the AdMng Watchdog sees it like any other change in AD).

## 8. What Group Policy logging does to the window

Group Policy, a computer or user policy, can turn on PowerShell **transcription**, **script block logging** and **module
logging**. They apply to every program that hosts the PowerShell engine, **AD Manager included**: transcription writes the
commands you run here **and their results** – that is, directory data – to the directory the policy names, and script block
logging writes the text of the commands to the Windows event log (*PowerShellCore/Operational*). AD Manager does not change
these policies and does not get around them.

**What the window does about it:**

- when it opens, it reads the policies – PowerShell Core (`HKLM` and `HKCU`, `Software\Policies\Microsoft\PowerShellCore`), the
  Windows PowerShell ones when *Use Windows PowerShell Policy setting* is on, and the two `powershell.config.json` files (the
  engine's, beside `AdMng.exe`, and yours, in *Documents\PowerShell*) – and the system lockdown state;
- before you run the first command it says plainly what is recorded where, for example *Transcription is on (computer
  policy): the commands you run here and their results, which can include directory data, are written to D:\PSLogs.*
  The note is in the status line (while no command's result covers it), in the layout under it, and in the first lines of the
  output; it stays until the window closes;
- when the policies cannot be read, it says so: *Whether commands and results are recorded is unknown.* Not knowing is a note too.

**What to do about it:** if transcription or script block logging is on, do not look at sensitive attributes in the window
(LAPS passwords, BitLocker recovery passwords) and do not type a password into a command. Ask the security team where the
transcripts go and who can read them – they hold the same data the window shows. A `powershell.config.json` that AD Manager
ships beside `AdMng.exe` turns off the transcription and logging that **your own** `powershell.config.json` turned on; it cannot
turn off a computer policy, and the note then says that the policy is on. Security systems (EDR) may report *a PowerShell engine
in a process other than `powershell.exe`* – the process is `AdMng.exe`; tell the security team (SOC) in advance.

## 9. What the window does not do yet

- **No writes (stage R2).** There is no `Set-`, `New-`, `Remove-`, `Move-`, `Rename-`, `Add-`, `Enable-`, `Disable-`,
  `Unlock-` or `Reset-` command for AD Manager yet, and no `AD:` drive. A test checks that the list of commands holds
  nothing but reads. To change an object use the program's windows (properties, the bulk editor, the wizards): preview,
  validation, audit and rollback work there. R2 will add commands that put operations into a **pending change plan** with
  its own panel (the empty place under the output), shown in the same preview and executed only after you confirm.
- **No RSAT modules (stage R3).** `Import-Module ActiveDirectory` and `GroupPolicy` are refused, `Get-ADUser` and the other
  `…-AD…` commands do not exist in the window. Use `Get-AdmUser` and its relatives; R3 will add the modules, pointed at the
  window's domain controller, and translate their write commands into the change plan.
- **No Microsoft 365 modules** (track C), no remote PowerShell sessions to servers, no background jobs, no scripts from files
  (`.ps1`) and no scheduled scripts.
- **No file system completion.** Tab completes commands, parameters, variables, attribute names and the window's containers,
  not file names; the window never reads the directory per keystroke.
- **The status line counts lines, not objects.** The window shows the engine's formatted text, so it says *12 lines of
  output*, not *12 objects*.
- `LockedOut` is the domain controller's own flag when it returns one, and otherwise "has a lockout time"; the list of
  accounts in the program also applies the domain's lockout duration.

## 10. Keyboard

| Where | Key | What it does |
|---|---|---|
| Command line | **Enter** | Runs the command when it is complete; adds a line when it is not (open quote, bracket, script block, trailing `\|`). With the completion list open it takes the selected match; while a command waits for an answer it sends the answer. |
| | **Shift+Enter** | Always adds a line |
| | **Tab**, **Shift+Tab**, **Ctrl+Space** | Completes (one match is put in, several open a list), goes back in the list, opens the list |
| | **Esc** | Closes the list or the history search |
| | **Up**, **Down** | The lines of a multi-line command, then the history, then back to what you were typing |
| | **Ctrl+R** | Searches the history; type to narrow, **Backspace** removes a character |
| | **Ctrl+L** | Clears the output |
| | **Ctrl+C** | Copies when text is selected; otherwise **stops the running command** |
| Output area | **Ctrl+C** | Copies the selected lines (nothing selected: stops the command) |
| | **Enter** | Opens the first distinguished name of the selected line |
| | **Esc** | Back to the command line |
| Grid window | **Enter**, double click | Opens the object |
| | **Ctrl+C** | Copies the selected rows |

The editor colours the command as you type it: commands, parameters, variables, strings, numbers, keywords and comments
(the colours follow the light, dark and High Contrast themes; in High Contrast they merge into the text colour).

## 11. When something does not work

| What you see | What to check |
|---|---|
| *This installation has no PowerShell engine.* | This build of AD Manager was made without the engine. Use the standard package. |
| *PowerShell could not be started* | Close the window and open it again. If it repeats, report it ([PILOT.md, chapter 4](PILOT.md#4-reporting-problems)) with the program version. |
| A command does not end | **Stop** (or Ctrl+C). The window waits 5 seconds for the command to react, then shows it as stopped. One match of a regular expression (`-match`, `-replace`, `-split`, `switch -regex`, `Select-String`, `[regex]`) takes at most 2 seconds, then ends with the error *The Regex engine has timed out…*. A command stuck in .NET code (the trusted mode, or a `[regex]` with a longer time limit of its own) may keep running, and a new command is refused until it ends – close the window and open a new one. The stuck command keeps the processor busy until it ends or you close the program. |
| *Cannot find a user with identity …* | The identity does not exist, is of another type (`Get-AdmUser` finds no computer) or the account cannot see it. A distinguished name with a comma in a value needs quotes. |
| *Access denied reading …* / *The attribute '…' of '…' was not returned …* | The window's account has no right to read the object or the attribute; a domain controller answers an unreadable attribute as if it were empty. The message names the attribute once. |
| *The LDAP filter is not valid* | The syntax of `-LdapFilter` or `-Filter`: balanced parentheses, `(attribute=value)`. |
| Nothing is returned | Check the filter and `-SearchBase`; with several filter parameters all of them must match. A missing search base is an error that names it. |
| *Only core types are supported in this language mode* | See chapter 7: use `New-Object PSObject -Property`, or turn the trusted mode on. |
| *… is not available in the AD Manager PowerShell window* / *The term '…' is not recognized* | The window has a fixed list of commands (chapter 7). The message says what to use instead – for a file, `Export-AdmList` or `Start-Transcript -Path` (chapter 5). |
| The distinguished names end with `…` | Use `Format-List`, a wider window, or `Select-Object -ExpandProperty DistinguishedName`. |
| The trusted mode switch is disabled | Its tool tip says why: a command is running, PowerShell is starting, or the system enforces ConstrainedLanguage (WDAC, AppLocker). |
| A note says transcription or logging is on | Chapter 8. It is a Group Policy setting of this computer or account, not of AD Manager. |
| *There are no objects to show: no grid was opened* | The pipeline of `Out-AdmGrid` was empty. |
| *The file … exists. Nothing was written; use -Force* / *No file was chosen: nothing was written.* | `Export-AdmList` replaces a file only with `-Force`, and writes nothing without a path or a file chosen in *Save As*. |
| `Get-Help` shows no examples | The file `AdMng.PowerShell.dll-Help.xml` beside `AdMng.exe` was removed. Restore it from the package. |

## 12. Limitations and pilot

**Nobody has seen the window on a screen yet.** The engine, the commands and the window's model are covered by automated tests
on Linux and Windows, and the real window runs over the real engine and a fake directory in the Windows tests, but the
pilot on Windows has to confirm:

- the look of the coloured text (the box edits transparent text, the colours are drawn in a layer under it), the caret and the
  selection in light, dark and High Contrast themes, at 125 %–200 % scaling, and the completion list near the edge of the screen;
- a screen reader (Narrator, NVDA) with the window: the list of completions is not announced;
- the policies of the window under a **real Group Policy** (transcription, script block logging, module logging; the keys
  come from the engine's own code and the documented templates) and the engine under **WDAC / AppLocker**;
- the commands against a **real Windows Server AD** (the tests ran over a fake directory and a Samba domain controller).

The scenarios for the pilot are in [PILOT.md, chapter 3.7](PILOT.md#37-powershell-window-r1).

Other limits: PowerShell 7.6 makes `AdMng.exe` about 38 MB larger, and every PowerShell security update means a new AD
Manager release. A script in the trusted mode that uses `System.Data.SqlClient` does not find `sni.dll` (the package is not
open source, so it is left out). The engine is not in the Watchdog package.
