**English** · [Polski](../PILOTAZ.md)

# AD Manager pilot – checklist

Almost everything AD Manager can do has been verified with tests on a fake directory, in the Samba lab and in UI tests on
Windows. **What has not been verified yet is whatever requires a real Windows Server AD**: Windows domain controllers,
GPMC and clients applying Group Policy, event logs read over RPC, remote actions, classic Outlook and Exchange. This
document guides you through the pilot: preparation, the readiness check, scenarios with expected results, and how to
report problems. The detailed checklists of the AdMng Watchdog service and of Correspondence are in their guides (links
below) – we do not repeat them here.

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
| Comparison tools | GPMC, `gpresult /h`, `secedit`, LockoutStatus and EventCombMT, Event Viewer, ADUC/ADAC, PowerShell `ActiveDirectory` and `GroupPolicy` |

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

### 3.5 AdMng Watchdog and Correspondence

- **Watchdog:** checklist
  [WATCHDOG-LINUX.md, chapter 7](WATCHDOG-LINUX.md#7-pilot--checklist) – forwarding events from the DCs to
  OpenWEC, "who and from where", GPO "was → is" content, notifications.
- **Correspondence:** checklist
  [CORRESPONDENCE.md, chapter 14](CORRESPONDENCE.md#14-pilot-checklist) – classic Outlook 32- and 64-bit,
  sending as and on behalf of, images, voting, on-premises Exchange and Exchange Online.

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

The pilot ends when all A, G and E scenarios (and the Watchdog and Correspondence checklists, if used) have the result
"match" or reported and fixed differences; the version can then be considered stable.
