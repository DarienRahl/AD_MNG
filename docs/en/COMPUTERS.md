**English** · [Polski](../KOMPUTERY.md)

# Computers in AD Manager (stage K1)

This document describes working with computer accounts: **Computer review** (inventory and reports), the **Status** tab in
computer properties, the **account lifecycle** (preparing an account before joining, reset, disabling, moving, deleting)
and **remote actions** over WinRM. Technical design: [docs/dev/k-computers-and-gpo.md](../dev/k-computers-and-gpo.md)
(chapter 4).

Rules that apply everywhere:

- **Nothing from the directory goes to disk.** Review results, IP addresses, sites, remote action results and the window
  log exist only in the window's memory. A file is created only when the operator chooses *Export CSV…*.
- **Every change in AD goes through a change plan** (preview, validation, session log, rollback wherever possible).
- **One domain controller per window.** All reads and changes go to the window's controller. The exception is *Exact last
  logon*, which by design queries every controller (short read-only sessions). The DNS names of computers are resolved by
  the operator workstation's resolver, not by the domain controller.
- **Passwords only in memory.** Remote actions and tools use the window's credentials. They are not stored anywhere, do not
  end up in the command line, environment variables or Credential Manager.

## 1. Computer review

*Tools → Computer review…* (command palette: `tools.computerReview`). Each connection window has one such window.

**Scope:** the whole domain, an organizational unit (with its subtree), a saved view or the members of a group (as in
*Account review*). *Check* (F5) reads computer accounts with a single paged query (`sAMAccountType=805306369`, without
managed service accounts). Results appear as they arrive, and *Stop* halts the read.

**Reports** (tiles with the number of computers):

| Tile | What it shows |
|---|---|
| *Operating systems past end of support* | an operating system past end of support or ending within N days (90 by default) according to the release catalog |
| *Windows 11 and Server releases* | a separate tile for each release (e.g. *Windows 11 24H2*) with the number of computers |
| *Inactive for N days* | `lastLogonTimestamp` older than N days (90 by default). The value is replicated with a delay of up to 14 days, so before deciding it is worth using *Exact last logon*. |
| *Never logged on* | accounts created more than N days ago that nobody has logged on to (usually prepared and not joined) |
| *Servers without owner* | a server without `managedBy` or with a disabled or deleted owner |
| *Computers in unexpected OUs* | a computer in the default container (e.g. `CN=Computers`), a domain controller outside `OU=Domain Controllers`, another computer in that OU, and the rules from the settings |
| *Disabled* | disabled computer accounts (informational) |
| *Without operating system* | accounts without `operatingSystem` (prepared ones or non-Windows systems) |

**Inactive computers are only a report.** AD Manager never disables or deletes anything by itself. The operator selects
rows and chooses *Disable…*, *Move…* or *Delete…*. Each of these actions shows a change plan and requires confirmation.

**The Windows release catalog.** The release is determined by the **build number**, not by the name of the system. Early
Windows 11 builds wrote "Windows 10 …" in `operatingSystem`, so build 22000 or higher means Windows 11. The name of the
system determines the family (Server) and the release class (Home/Pro, Enterprise/Education, LTSC, IoT LTSC, Server,
Azure Stack HCI / Azure Local), because end-of-support dates differ between editions. Hyper-V Server has the Windows Server
dates of the same build, and Azure Stack HCI / Azure Local has its own dates for each version (from 23H2 support depends
on the solution version, so the catalog gives no date). The window shows the catalog date (*Release catalog as of …*) and
warns when it is more than 9 months old. Your own dates, for example after buying ESU for Windows 10, are entered in
*Settings → Computers*. They take precedence over the catalog.

**Columns and details.** Each tile has its own columns: the name with a type icon (workstation, server, domain controller,
disabled), DNS name, type, system, release, support status with the date, last logon, exact last logon, site and subnet,
creator (`mS-DS-CreatorSID`), owner (`managedBy`), location, description and OU. The details panel shows all facts about
the selected computer, the report findings and the catalog row. Clicking a header sorts the list.

**Actions** (for the selection, and where it makes sense – for the whole list):

- *Properties*, *Batch edit…*, *Export CSV…* (a file chosen by the operator, the separator from regional settings,
  protection against formulas, a DN column).
- *Exact last logon* – `lastLogon` from every domain controller (up to 500 computers, at most 4 controllers at a time). A
  computer that turns out to be active disappears from the inactivity reports. When the selection or the list has more
  than 500 computers, the window first asks whether to check only the first 500 in the current order, and the result says
  how many computers were checked out of how many.
- *Determine site* – the computer's DNS name is resolved with the operator workstation's resolver (2 s limit, 16 queries
  at a time), and the address is matched to the forest's subnets (longest prefix, IPv6 too). The site of a domain
  controller is the site of its server object. With more than 500 computers the window first asks for consent. Messages:
  *no subnet*, *not resolved*.
- *Remote actions…*, *Remote Desktop*, *Ping* – chapter 4.
- *Set owner…* (`managedBy` from the picker), *Disable…*, *Move…*, *Reset Account…*, *Delete…* – chapter 3.

After each published change, the changed rows are read again.

## 2. Computer properties

- The *General* page also shows the computer **type**, and the *Operating System* page the catalog **release** and the
  **support status** with the date.
- The **Status** page: last logon (approximate) with the *Check on all DCs* button, the site with the *Determine*
  button, the account creator, the object owner (from the security descriptor), the date of the account password renewal
  and whether the computer has LAPS (only the presence of the expiration date – **the LAPS password is not read**). At the
  bottom of the page are the buttons *Remote actions…*, *Remote Desktop*, *Computer Management*, *Event Viewer* and *Ping*.

## 3. Computer account lifecycle

### Preparing an account (New → Computer)

The *New → Computer* wizard has three additional fields:

- **The user or group that can join this computer to a domain.** By default nobody, so only administrators can join.
  Choosing a person or group adds to the plan, right after the account is created, a **permissions change** step with the
  four ACEs that ADUC writes: *Reset Password*, read and write of the *Account Restrictions* property set, validated write
  to DNS host name and validated write to SPN (allow, no inheritance).
- **Managed by** (`managedBy`) and **Location** (`location`).

The ADUC option *Assign this computer account as a pre-Windows 2000 computer* is deliberately absent. It sets a password
derived from the name, and predictable computer account passwords are a known attack path.

**Hardening of domain join (KB5020276).** Since the updates of 2022–2023, reusing an existing computer account succeeds
only when:

- the joining party created the account,
- the joining party is a domain or enterprise administrator or the built-in Administrator,
- the account's owner belongs to these groups,
- the owner is designated by the policy *Domain controller: Allow computer account re-use during domain join*.

The owner of a prepared account is the account of the AD Manager window. If that account is not an administrator and
someone else is to do the joining, the wizard summary warns that without this policy the join will fail.

### Reset Account

*Computer → Reset Account…* sets a random 120-character password on the computer account. Nobody sees it: it does not go
to the report, the clipboard or the PowerShell export. The reset does not go through the bulk editor (which allows
exporting the plan), so there is no plan preview – instead, the confirmation lists the accounts being reset (the first 20
and the number of the rest) and the skipped objects, and warns that **the computer will lose its trust relationship with
the domain** – it has to be joined again or the channel repaired with `Test-ComputerSecureChannel -Repair`. The
confirmation requires typing the domain name. Domain controller accounts cannot be reset (demoting the controller is for
that).

### Disable, move, delete

The actions work as for other objects, with additional rules for computers:

- disabling, deleting or resetting the account of a **domain controller** is a plan error,
- moving a domain controller out of `OU=Domain Controllers` is a warning: *Default Domain Controllers Policy* no longer
  applies to it,
- deleting a computer with child objects (BitLocker recovery information, Hyper-V or cluster objects) deletes the whole
  subtree. The confirmation warns that **BitLocker recovery passwords will be deleted together with the account**.

## 4. Remote actions (WinRM, Kerberos)

*Computer → Remote actions…* opens the computer's window (one per computer in a connection window). The window header
shows the computer name, its DNS name and the connection method, e.g. *Connection: WinRM (HTTP 5985, Kerberos) as
CONTOSO\admin*.

| Action | What it does |
|---|---|
| *Check status* (F5) | ping (4 tries), WinRM availability, the system reported by the computer itself (with the UBR patch number) and the last boot time |
| *Show logged-on users* | the console account and the owners of `explorer.exe` processes with the session number. CIM does not know the session state, so disconnected RDP sessions are on the list too. |
| *Show services* | services with startup type and account, with the filter *automatic, stopped*. Starting and stopping services is not part of K1. |
| *Refresh Group Policy* | `gpupdate /target:computer /force` on the computer. The window waits for completion (up to 10 minutes) and gives the time from the policy registry. Refreshing the policy of logged-on users (a scheduled task) is switched on in the settings – to be confirmed in the pilot. |
| *Restart…* | a delay of 0–1440 minutes (10 by default), a message of up to 500 characters (shown verbatim to logged-on users), the option *Close applications without asking*. The computer name has to be typed for everything except a known workstation: a server, a domain controller and a computer of unknown type (without `operatingSystem` – for example a cluster name object, whose restart goes to the node that owns it). |
| *Cancel scheduled restart* | `shutdown /a` on the computer. The window does not know the result of the command, so it records only that the cancellation was ordered – if no restart was scheduled, nothing changes. |

Important rules:

- **Kerberos only.** No fallback to NTLM, so administrator credentials never reach the computer through NTLM. The computer
  must have a DNS name in AD (`dNSHostName`). IP addresses are not supported, because Kerberos does not accept them.
- **The window's credentials.** A window logged on with the current Windows account uses the process token. A window with
  other credentials passes them to WinRM as a copy of the password from memory, removed after each action. A window with a
  simple bind by DN has no Windows account and does not offer remote actions.
- **Required permissions:** the window's account must be a **local administrator** of the computer (on domain
  controllers: Domain Admins). The window reports a lack of permissions as *Access denied*.
- **The time limit** is 30 s (setting: 5–300 s). Closing the window or the connection window interrupts a call in
  progress.
- **The window log** exists only in memory. Every state-changing action goes to the session log (without the content of the
  restart message). Its PowerShell equivalent (`New-CimSession … -Authentication Kerberos`, `Invoke-CimMethod …`) goes to
  the PowerShell history – without the password.
- Error messages say what to check: *the computer does not respond*, *WinRM unavailable*, *Kerberos authentication failed*
  (missing SPN, time difference, wrong account), *access denied*, *time limit exceeded*.

### Tools

- **Ping** – inside the program itself, without credentials.
- **Remote Desktop** – `mstsc /v:<DNS name>`. **It gets no credentials:** mstsc uses the operator's saved credentials or
  asks for them. AD Manager stores nothing in Credential Manager.
- **Computer Management** and **Event Viewer** – `compmgmt.msc /computer:` and `eventvwr`. A window logged on with a
  different account launches them like `runas /netonly`: locally the operator's Windows account runs, and on the network –
  the window's credentials. The window asks about this before launching. The credentials then live in the memory of the
  tool's logon session until it is closed, never on disk.

## 5. Enabling WinRM on computers with Group Policy

Remote actions need the WinRM service and its HTTP listener (5985) with Kerberos encryption. A sample GPO recipe for an OU
with workstations and servers (names from the Polish version of Windows, the English ones in parentheses):

1. *Computer Configuration → Policies → Windows Settings → Security Settings → System Services*:
   **Zdalne zarządzanie systemem Windows (WS-Management)** (*Windows Remote Management (WS-Management)*) – startup mode
   **Automatic**.
2. *Computer Configuration → Policies → Administrative Templates → Windows Components → Windows Remote Management (WinRM)
   → WinRM Service*: **Zezwalaj na zdalne zarządzanie serwerem przy użyciu usługi WinRM** (*Allow remote server
   management through WinRM*) – **Enabled**, IPv4 filter `*` and IPv6 filter `*` (or the own subnets of the managed
   computers). These filters select **the addresses of the computer itself** that WinRM listens on – they do not filter
   clients. If you enter the administrators' subnet here, computers outside it will not create a listener and every
   remote action will end with a *WinRM unavailable* error. An empty filter means no listening on that protocol.
3. *Computer Configuration → Policies → Windows Settings → Security Settings → Windows Defender Firewall with Advanced
   Security → Inbound Rules*: a new predefined rule **Zdalne zarządzanie systemem Windows** (*Windows Remote
   Management*). Leave only **Zdalne zarządzanie systemem Windows (HTTP — ruch przychodzący)** for the *Domain* profile,
   and in the rule's properties, on the *Scope* tab, limit the remote addresses to the administrators' subnet (e.g.
   `10.10.5.0/24`). This is the only place where you restrict where WinRM can be reached from.
4. Permissions: the account (group) of the workstation administrators must be in the local *Administrators* group, e.g.
   through *Restricted Groups* or the GPO preferences *Local Users and Groups*.

Check from the administrator's computer:

```powershell
Test-WSMan -ComputerName pc01.contoso.test -Authentication Kerberos
```

HTTPS (5986) requires a certificate on each computer and an HTTPS listener. You switch it on in *Settings → Computers*
(*Use HTTPS*). Kerberos encrypts the traffic over HTTP as well.

## 6. Settings (the "Computers" tab)

*Settings → Computers*:

- **WinRM:** time limit (5–300 s), HTTPS instead of HTTP, own port (empty: 5985 or 5986) and refreshing the policy of
  logged-on users (off by default until the pilot).
- **Restart:** the default delay and the default message (empty: the text built into the interface language).
- **Own end-of-support dates:** build, release class and date. They take precedence over the catalog. This is also where
  you set the warning horizon (days ahead).
- **Expected containers** of workstations and servers: one container per line (with its subtree), `{domain}` stands for the
  connection's domain, e.g. `OU=Stacje robocze,{domain}`. An empty field switches off the check for that type.

The settings contain no computer names or results. The report parameters, the selected reports and the scope of the last
review are remembered by the review window itself.

## 7. Limitations and pilot

To be confirmed on Windows Server 2025 and Windows 11 24H2/25H2 (the Samba lab cannot verify this):

- `Win32ShutdownTracker` with a message and delay invoked by a remote administrator without elevation,
- refreshing user policy with a scheduled task (`RefreshUserPolicy`),
- CIM with Kerberos only and explicit credentials of an account other than the Windows account,
- `CreateProcessWithLogonW` with `RunAsInvoker` for `mmc`, and launching `mmc` and `eventvwr` directly with
  `RunAsInvoker` in a window with the current Windows account (the operator being a local administrator, UAC on),
- the KB5020276 rules for prepared accounts,
- an inventory consistent with `Get-ADComputer` on a real domain.

The end-of-support dates in the catalog have to be compared periodically with the Microsoft Lifecycle pages (the window
warns after 9 months). Starting and stopping services, software inventory, Wake-on-LAN and reading LAPS passwords are not
part of K1.
