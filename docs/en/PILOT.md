**English** · [Polski](../PILOTAZ.md)

# AD Manager pilot – checklist

Almost everything AD Manager can do has been verified with tests on a fake directory, in the Samba lab and in UI tests on
Windows. **What has not been verified yet is whatever requires a real Windows Server AD**: Windows domain controllers,
GPMC and clients applying Group Policy, event logs read over RPC, remote actions, certification authorities (AD CS),
classic Outlook and Exchange. This document guides you through the pilot: preparation, the readiness check, scenarios
with expected results, and how to report problems. The detailed checklists of the AdMng Watchdog service and of
Correspondence are in their guides (links below) – we do not repeat them here.

> Start with a **test domain** or an account with limited permissions. Every change in AD and SYSVOL goes through a
> change plan preview with rollback, but the pilot is meant precisely to confirm compatibility with Microsoft tools.

---

## 1. Environment

| Item | Recommendation |
|---|---|
| Domain controllers | at least two: Windows Server 2025 and Windows Server 2022 (different functional levels to check), one of them the PDC emulator |
| Administrator workstation | Windows 11 24H2/25H2, an account with permissions to the test OU (separately: a helpdesk account with limited permissions) |
| Test computers | a Windows 11 workstation and a member server in the test OU – for remote actions, Group Policy and event logs |
| AdMng Watchdog (optional) | Debian 13 according to [WATCHDOG-LINUX.md](WATCHDOG-LINUX.md) |
| Correspondence (optional) | classic Outlook with an Exchange profile (on-premises or Exchange Online) |
| Certificate Services (optional, P1) | AD CS with a root CA and an issuing enterprise CA, CRLs published in AD and at an HTTP URL; deliberately vulnerable template copies only in the test domain (lab recipe: [WATCHDOG-LINUX.md, 1.7](WATCHDOG-LINUX.md#17-a-certification-authority-in-the-lab-optional)) |
| Comparison tools | GPMC, `gpresult /h`, `secedit`, LockoutStatus and EventCombMT, Event Viewer, ADUC/ADAC, PowerShell `ActiveDirectory` and `GroupPolicy`; for the PKI: `pkiview.msc`, `certtmpl.msc`, `certlm.msc`, `certutil`, Certipy (`certipy find -vulnerable`) and Locksmith |

AD Manager files are not digitally signed yet: SmartScreen, AppLocker or WDAC may block `AdMng.exe` – for the duration of
the pilot you need an exception for the program folder or the file hash (`SHA256SUMS.txt` from the release).

## 2. Preparation and readiness

1. On the domain controllers and test computers, enable what the features need (details in the guides):
   - remote actions – WinRM ([COMPUTERS.md, chapter 5](COMPUTERS.md#5-enabling-winrm-on-computers-with-group-policy));
   - event logs – the "Remote Event Log Management" firewall rule group, the operator account in the Event Log Readers
     group, auditing on the DCs ([EVENT-LOGS.md, chapter 2](EVENT-LOGS.md#2-requirements)).
2. Run **Tools → Check environment readiness…** – first *Quick check*, then *Full check* with a test computer. Every
   error and warning has a hint on what to do. Save the report (HTML) as a baseline.
3. Note the AD Manager version (bottom right corner) and the operating system versions.

## 3. Scenarios

Enter the results in the table in chapter 5. *Compare with* – the Microsoft tool the result is supposed to match.

### 3.1 Everyday work (Phases 1–3)

| # | Scenario | Expected result | Compare with |
|---|---|---|---|
| A1 | Connection with the current account and with another account (Kerberos, signing and sealing; LDAPS) | connection without warnings, the DC and the security method shown in the status bar | – |
| A2 | Large OU (10,000+ objects), scrolling, search-as-you-type | first page < 300 ms on a LAN, the window does not freeze | ADUC |
| A3 | Property pages of a user, group, computer (country, logon hours, account expiration, group scope) | the same values as in ADUC | ADUC |
| A4 | Helpdesk account: fields without permissions | read-only fields, understandable messages (access denied, password policy) | ADUC |
| A5 | Bulk editing of 50 accounts with preview, execution and *Undo* | changes as in the preview, rollback restores the state | PowerShell |
| A6 | AD Recycle Bin: deleting and restoring an account and an OU with its subtree | objects and memberships come back | ADAC |
| A7 | "No directory data on disk": after the work, review `%APPDATA%\AdMng` and `%TEMP%` | only settings, profiles without passwords, view definitions | – |
| A8 | *Multiple accounts…* in a test OU: about 20 rows pasted from Excel (mobile phone, given name, surname, department, e-mail), the *Numbered accounts (kd201…)* profile, the UPN suffix, a group; *Check*, *Preview*, *Execute*, *Copy table with passwords…*; logon with one account on the test workstation; finally *Undo* | numbers from the first free one, UPNs and attributes as in the *Check* step, accounts enabled with a forced password change and the group membership; the first logon forces a password change; the undo deletes all created accounts (with the AD Recycle Bin enabled they go to the Recycle Bin) | ADUC, `Get-ADUser -Properties *` |
| A9 | The same sheet in the wizard opened with an account that has delegated account creation in the OU but no right to reset passwords | the execution stops after the first account (*Stopped after the first account…*), that account stays disabled and without groups, the other rows are not run | ADUC |
| A10 | Closing a window during an execution: the batch editor with 300 objects (e.g. the description), three times – *Stop and close*, *Close and continue in the background*, *Don't close*; then the main window closed while an execution runs in the background, and *Disconnect* while one runs in the background | *Stop and close*: the object in progress finished, the window closes after the end, then a yellow notification of the main window "Executing “…” in … was stopped: N of 300 objects done, failed: 0, …" (with failures a dialog with their reasons instead; no notification of the editor with its counts); *background*: the window goes at once, the row in the list of background operations runs to its end, a notification of the main window (failures – a dialog with their reasons); *Don't close*: nothing changes; the main window asks once, lists the background execution, offers no background and closes after the objects in progress ended; *Disconnect* waits for their end; every change in the session log | session log, `Get-ADUser -Properties description` |
| A11 | Recycle Bin: restoring two objects with an account the DC refuses one of them for; during it *Stop* in the status line, then closing the window; a second time closing the window with *Stop and close* while the restore is just ending | no question (the stop came first), the window closes after the end of the restore; only after it closed the main window shows the dialog with the failures (the domain, the object and the reason, the session log sentence) – it does not vanish with the closed window; the second time the window closes after the list was read again, and the main window says the outcome | session log, ADAC |

**Creating multiple accounts – to be confirmed on Windows domain controllers** (the fake directory of the tests and the
Samba lab with one domain in the forest cannot settle this; details:
[CREATING-ACCOUNTS.md, chapter 12](CREATING-ACCOUNTS.md#12-limitations-and-pilot)):

- a forest with several domains: the uniqueness of UPNs and addresses checked through the global catalog of the window's
  controller (*Uniqueness of UPNs and addresses: the whole forest (global catalog on …)*), *checked in the domain only*
  when the controller is not a global catalog, and the forest-wide UPN uniqueness check that Windows performs itself at
  write time (error 0x21C8 → *The UPN may already be used in another domain of the forest.*);
- uniqueness of the object name in a container for different types (e.g. a group *Księgowość* next to an organizational
  unit *Księgowość*);
- computer accounts: KB5020276 (joining to a prepared account by someone outside the administrators) and KB5008102 (the
  rules for the `$` character), the "disabled → random password → enabled" sequence and the *New Computer* window (K1),
  which creates the account enabled without a password;
- the RID pool running out with a large batch (the low-pool warning, errors of the following accounts, *Retry failed*);
- logging on with a new account through another controller before replication has finished;
- an OU administrator with delegated permissions but without the right to reset passwords – the stop after the first
  account (scenario A9).

### 3.2 Computers (K1)

[COMPUTERS.md, chapter 7](COMPUTERS.md#7-limitations-and-pilot): remote actions with an account other than the Windows
account, restart with a message, refreshing user policy, launching tools (`mmc`, Event Viewer), preparing a computer
account and joining the domain (KB5020276), inventory consistent with `Get-ADComputer`.

### 3.3 Group Policy (K2–K7)

| # | Scenario | Expected result | Compare with |
|---|---|---|---|
| G1 | New GPO, link to the test OU, order, enforcement, blocking inheritance | GPMC shows the same; `gpresult /h` on the test computer | GPMC, `gpresult` |
| G2 | Security filtering and delegation | GPMC reports no inconsistent folder permissions | GPMC |
| G3 | Backup in AD Manager → restore in GPMC and vice versa | settings and permissions restored | GPMC |
| G4 | Administrative Templates: several policies (Windows, Edge), save | `Registry.pol` gives the same settings in GPMC; the client applies them | GPMC, `gpresult` |
| G5 | Central store update (Windows 11 / Edge package) | differences before saving, a copy of the previous version | – |
| G6 | Security settings: restricted group, user right, password policy, advanced audit policy | GPMC shows them as its own; `secedit /analyze` and `gpresult` on the client | GPMC, `secedit` |
| G7 | "What the computer will get" model for a test computer | list of GPOs and winning settings as in *Group Policy Modeling* and `gpresult` | GPMC, `gpresult` |
| G8 | The *Settings* tab and *HTML report…* | names and values as in the GPMC report (`Get-GPOReport`) | `Get-GPOReport` |
| G9 | Group Policy diagnostics (full) | accurate findings; *Fix…* works through the plan preview | knowledge of the domain |
| G10 | *GPO settings search*: a setting (e.g. the minimum password length), a value, a technical key, an account (name and SID) | the same GPOs and values as *Find* in the GPMC editor; the winning value consistent with `gpresult /h` | GPMC (*Find*), `gpresult` |
| G11 | *Copy to another GPO…*: an ADMX policy, a user right, a security setting and a drive mapping into a test GPO; then *Undo* | GPMC shows the copied settings as its own; `gpresult /h` on the test computer applies them; after the undo they are gone; a GPP password is refused with a reason | GPMC, `gpresult` |
| G12 | *Merge with…* of two test GPOs linked to the same OU | the settings in the target GPO, the link of the merged GPO replaced, the merged GPO disabled; `gpresult /h` shows no change in effect; *Undo* restores the previous state | GPMC, `gpresult` |
| G13 | *GPO cleanup*: every proposal (empty, unlinked, disabled, with a single setting, duplicates, merge candidates) | proposals consistent with what GPMC shows; *Delete GPO…* offers a backup and refuses when the GPO has changed since the analysis | GPMC |

Limitations and details: [GROUP-POLICY.md, chapter 15](GROUP-POLICY.md#15-limitations-and-pilot).

### 3.4 Event logs (E)

| # | Scenario | Expected result | Compare with |
|---|---|---|---|
| E1 | Reader: the *Security* channel of all DCs from the last 24 hours, filter by account | the same events as in Event Viewer; the log coverage is given | Event Viewer |
| E2 | A computer without the firewall rule / an account outside *Event Log Readers* | a clear cause and a hint | – |
| E3 | Locking out a test account from a test computer (wrong password 5×), then *Find the lockout source…* | source: the test computer; the state on all DCs consistent with LockoutStatus | LockoutStatus, EventCombMT |
| E4 | A service or scheduled task with an old password of the test account | *Check the source computer* points to the service or task | – |
| E5 | Failed logons: several accounts with a wrong password from one computer | a *Password spraying* or *Password guessing* finding | Event Viewer |
| E6 | An `.evtx` file saved on a DC ("Save All Events As…") | events and descriptions as in Event Viewer | Event Viewer |

Limitations: [EVENT-LOGS.md, chapter 7](EVENT-LOGS.md#7-limitations-and-pilot).

### 3.5 Certificate Services (P1)

Everything is read-only; create the deliberately vulnerable templates and ACL entries only in the test domain and remove
them after the pilot.

| # | Scenario | Expected result | Compare with |
|---|---|---|---|
| P1 | *Public Key Services* in a domain with a root and an issuing CA: CAs, trusted CAs and NTAuth, CRLs, the *Health* tab; then *Check HTTP URLs* | the states of the CA certificates, the AIA and CDP locations and the base and delta CRLs as in *Enterprise PKI*; the HTTP URLs are contacted only after the button | `pkiview.msc` |
| P2 | *Certificate Templates*: the default templates and their copies (schema versions 1–4) – flags, periods, providers, *Issued by*, *Who can enroll* and *Who can change the template*, also with a deny entry and a nested group | the same properties and permissions as in the console; account counts consistent with the membership | `certtmpl.msc`, `Get-ADGroupMember -Recursive` |
| P3 | *AD CS security review* on an unchanged, default PKI – from a window of the root domain and of a child domain | no findings; the *Privileges* tab (*Account review*) without delegations on PKI objects | Certipy, Locksmith |
| P4 | Deliberately vulnerable template copies (ESC1, ESC2, ESC3, ESC4, ESC9, ESC13, ESC15, ESC17), a write right on a PKI container for a test group (ESC5), a weak mapping on a test Tier 0 account (ESC14) | every `certipy find -vulnerable` and Locksmith finding for the ESCs of the LDAP list has a counterpart; differences only those of [CERTIFICATES.md, chapter 5.4](CERTIFICATES.md#54-differences-from-certipy-find--vulnerable-and-locksmith); CSV and HTML export on request | `certipy find -vulnerable`, Locksmith |
| P5 | The *LDAPS certificates* tab on all DCs (an RODC and a global catalog too), one DC without an LDAPS certificate, a workstation without internet access | the certificate and chain as in the DC's computer store; a DC without a certificate – *DC without an LDAPS certificate (the TLS handshake failed)*; nothing is downloaded | `certlm.msc` on the DC |
| P6 | *Strong mapping and NTAuth readiness* with *Check LDAPS certificates*: an account with a weak and one with a strong mapping, a CA outside NTAuth publishing a logon template, a logon with a test certificate | rows consistent with reality; the logon result consistent with the assessment (under full enforcement a weak mapping is refused) | KDC events 39/40/41 in the *System* log of the DC |
| P7 | The *Published Certificates*, *Name Mappings* and *Key Credentials* tabs of a user with an S/MIME certificate from Outlook, a mapping created by the ADUC tab and a Windows Hello for Business key | the same certificates and mappings as in ADUC; the ADUC mapping marked as weak; the keys consistent with `msDS-KeyCredentialLink` | ADUC (*Advanced Features*), `Get-ADUser -Properties *` |
| P8 | The Attribute Editor and a saved view with an `msPKIAccountCredentials` column on an account with roaming credentials | the value only as the mask, *Edit* and *Copy* disabled, the list CSV with the mask | – |

Include the work with the PKI documents in scenario A7: apart from the exports you chose, no file is created.
Limitations and details: [CERTIFICATES.md, chapter 11](CERTIFICATES.md#11-limitations-and-pilot).

### 3.6 AdMng Watchdog and Correspondence

- **Watchdog:** checklist
  [WATCHDOG-LINUX.md, chapter 7](WATCHDOG-LINUX.md#7-pilot--checklist) – forwarding events from the DCs to
  OpenWEC, "who and from where", GPO "was → is" content, notifications.
- **Correspondence:** checklist
  [CORRESPONDENCE.md, chapter 14](CORRESPONDENCE.md#14-pilot-checklist) – classic Outlook 32- and 64-bit,
  sending as and on behalf of, images, voting, on-premises Exchange and Exchange Online.

### 3.7 PowerShell window (R1)

Reads only: the window changes nothing in AD. Nobody has seen it on a screen yet, so the first scenarios are about looking.
Run it once with a helpdesk account and once with the pilot administrator.

| # | Scenario | Expected result | Compare with |
|---|---|---|---|
| PW1 | *Tools → PowerShell…* and **Ctrl+Shift+P** from two connection windows; type `Get-AdmUser -Department 'IT' -Pro` and press **Tab**; look at the window in the light, dark and High Contrast themes at 125 %–200 % scaling | one window per connection, the domain, DC and account in the title; the colours read well, the caret stays on the text, the completion list opens at the caret and does not leave the screen | – |
| PW2 | `Get-AdmUser`, `Get-AdmGroup`, `Get-AdmComputer` with several filters (also `-Properties`, `-SearchBase`), 20 objects compared one by one | the same objects and values as in the program's lists and the Attribute Editor; dates in local time, flags as names | the program's lists, `Get-ADUser` |
| PW3 | `Get-AdmGroupMember <group> -Recursive` for a group with nested groups (and a cycle); with `-ExcludeGroups` | the same list as *All (nested)* on the *Members* page; `-ExcludeGroups` gives only accounts and computers | `Get-ADGroupMember -Recursive` |
| PW4 | `Search-AdmObject` with values containing `(`, `)`, `*`, `\` and Polish letters (create such objects in the test OU) | only the objects with exactly that value (or `-Match StartsWith`); no extra results | – |
| PW5 | `Get-AdmUser … \| Out-AdmGrid`: sort, filter, **Enter** on a row, **Ctrl+C** into Excel, *Export list…*; `Export-AdmList` with a path, without a path (*Save As*), onto an existing file | the grid and the CSV hold the same values; a formula in a value (`=1+1`) is not run in Excel; an existing file is replaced only with `-Force` | the object list |
| PW6 | Turn on transcription (then script block logging) by Group Policy for the pilot account, open the window, run a command | before the first command the window says what the policies record and where; the transcript holds the commands and the results; the window does not try to avoid it | the GPO, the transcript folder, the *PowerShellCore/Operational* log |
| PW7 | A machine with WDAC or AppLocker enforcing ConstrainedLanguage | the window stays constrained, the *Trusted mode* switch is disabled with the reason, AD Manager's commands and the window's `ForEach-Object` work | `$ExecutionContext.SessionState.LanguageMode`; `1, 2 \| ForEach-Object { $_ * 2 }` |
| PW8 | The trusted mode: turn it on, define a function with a .NET call (`function Get-Tmp { [System.IO.Path]::GetTempPath() }`) and call it, turn the mode off, call it again; then `cls` and `exit 3`; close the window, open a new one | a question that names the domain first and also says that turning the mode off starts a new session; the title and a note say *trusted mode*; while it is turned off the status line says *Turning the trusted mode off…* for a moment, the window says the session is new, and `Get-Tmp` is not recognized; `cls` clears the window, `exit 3` names the exit code and the window stays open; the new window is constrained again | – |
| PW9 | A command that returns 100,000 objects, then **Stop**; close the window while a command runs | the window stays responsive, Stop ends the read at the DC, closing asks the question; after closing no file with directory data is left in the profile and *Temp* (the engine's AppLocker check creates and deletes two `__PSScriptPolicyTest_*` files in *Temp* when the first window opens – a comment line, no directory data) | Process Monitor |
| PW10 | Narrator or NVDA on the window | the command line, the output and the status line are read; an error or a warning line is read with *Error:* / *Warning:* first; the completion list is not announced (known) | – |
| PW11 | `while ($true) { 'x' }`, then click **Stop** with the mouse; again, and stop it with **Ctrl+C**; once more, press **Ctrl+L** while the lines arrive, then **Stop** | the window keeps taking clicks and keys while the lines pour in, Stop and Ctrl+C end the command within a second, Ctrl+L empties the output without an error message (only an automated test has flooded the window so far) | – |
| PW12 | Without any script block logging policy and without a `ScriptBlockLogging` section in your own `powershell.config.json`, on a computer where the PowerShell 7 event provider is registered (PowerShell 7 installed from the MSI package with its default options, or `RegisterManifest.ps1` from PowerShell 7's folder run as an administrator): run `Write-Output 'Add-Type'`; then turn on *Turn on PowerShell Script Block Logging* (PowerShell Core) by Group Policy, restart AD Manager and run `Get-AdmUser <account>` | without the policy the first line is a warning 4104 in the log (`Add-Type` is a word of the engine's list); with the policy the window's note says that script block logging is on, and every line you run, the second one included, is in the log (event 4104) | the *PowerShellCore/Operational* log |

Limitations and details: [POWERSHELL.md, chapter 12](POWERSHELL.md#12-limitations-and-pilot).

## 4. Reporting problems

**Help → Report a problem…** creates a description without directory data: the program and system version, the connection
method (without server and account names), the readiness check results as codes and the latest entries of the diagnostic
log as message templates (without values). Attach it to the report together with the scenario number, the steps and what
the Microsoft tool showed. Pass on screenshots and exports with names from the directory only in accordance with the
organization's rules.

## 5. Results

| # | Date | Version | Result (match / differences / error) | Notes and report number |
|---|---|---|---|---|
| A1 | | | | |
| … | | | | |

The pilot ends when all A, G, E and P scenarios (and the Watchdog and Correspondence checklists, if used) have the
result "match" or reported and fixed differences; the version can then be considered stable.
