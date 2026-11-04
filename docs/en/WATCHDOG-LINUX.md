**English** · [Polski](../WATCHDOG-LINUX.md)

# AdMng Watchdog on Linux – step-by-step deployment (Debian + systemd)

This guide walks through the whole deployment of the Linux version of AdMng Watchdog: from a test domain, through
preparing Active Directory and the Debian server, to the pilot and rollback. It is written for an AD administrator who
first runs everything in a **test** domain and only then in production.

- The decision and its rationale: [ADR-0009](adr/0009-watchdog-on-linux.md). The implementation specification (in
  English, binding for stage B): [`dev/watchdog-linux-design.md`](../dev/watchdog-linux-design.md).
- Description of Watchdog features (detected events, rules, channels, the tab in AD Manager): [`WATCHDOG.md`](WATCHDOG.md).
  The guide for the Windows service (gMSA, PowerShell script, HTTP.sys, DPAPI-NG) applies only to release 0.2.0 and is
  available in [`WATCHDOG.md` from version 0.2.0](https://github.com/DarienRahl/AD_MNG/blob/v0.2.0/docs/WATCHDOG.md);
  from release 0.3.0 the service runs on Linux only.
- **Version.** This guide describes release **0.3.0**: the packages `admng-watchdog` (one for Debian 12 and 13, with the
  `admng-watchdog-keytab` tool) and `admng-openwec` (separate builds for Debian 12 and 13) and AD Manager 0.3.0 (the
  certificate confirmation window, new sections of the Status tab, the "Sources and retention" subtab). You download the
  files from the [v0.3.0 release](https://github.com/DarienRahl/AD_MNG/releases/tag/v0.3.0) (3.1). This is a **preview
  version**: the packages passed installation tests on Debian 12 and 13 and tests in the Samba AD lab (password change
  and keytab, LDAP with GSSAPI encryption, API with Kerberos, OpenWEC, SYSVOL reading), but they have **not yet passed a
  pilot on Windows Server 2025 and 2022 domain controllers** – that is chapter 7. Start with a test domain.

## How to read this guide

Every step has the same parts:

- **Where** – on which machine and with which permissions you perform it:
  - **PAW** – a Tier 0 administrative workstation with RSAT (the ActiveDirectory PowerShell module, the GPMC console,
    DNS tools), logged on with an account from the Domain Admins group. In the test domain the PAW role is played by
    ADM-WS01 (1.6).
  - **DC01 / DC02** – the domain controller console, PowerShell run as administrator.
  - **Debian** – the `admng-wd` server, a root shell (`sudo -i`) after logging in over SSH with your own administrator
    account.
  - **ADM-WS01** – the administrator workstation with AD Manager in the 10.0.20.0/24 subnet.
- **What and why** – what the step changes and why it is needed.
- **Verification** – a command and the expected result. Do not move on until the verification gives that result.

The sample names and addresses are the same throughout the guide. Replace them with your own according to the table
below – consistently in all commands and files.

Terms that recur throughout the guide:

| Term | Meaning |
|---|---|
| **Tier 0** | domain controllers and everything that allows managing them: administrator accounts and groups, servers holding their identity, backups |
| **SPN** | a service name in Kerberos, e.g. `HTTP/admng-wd.contoso.com`; the domain controller issues a ticket to the service encrypted with the key of the account that owns the SPN |
| **keytab** | a file with the account's Kerberos keys derived from the password (without the password itself); it lets a service authenticate without a human – whoever has this file has the account's identity |
| **kvno** | the account key version number – increases by 1 with every password change; the keytab must contain the version the domain controller uses |
| **SACL** | an object's auditing list – tells the domain controller which operations on the object to write to the Security log |
| **WEF** | Windows Event Forwarding – a mechanism built into Windows for sending events from the log to a collector |
| **SSF** | the strength of LDAP connection encryption in bits (0 = no encryption) |
| **SASL GSSAPI** | logging on to LDAP with a Kerberos ticket, with connection signing and encryption |
| **PTR** | a reverse DNS record: IP address → computer name |
| **kerberoasting** | offline cracking of passwords of accounts that have an SPN – based on tickets that any user can request |
| **SDProp** | a process on the PDC emulator that every hour copies permissions from `AdminSDHolder` to protected accounts (`adminCount=1`) |
| **FSMO, PDC emulator** | roles performed by one selected controller; the PDC emulator, among other things, accepts password changes and is the domain's time source |
| **UBR** | the patch number in the Windows build number (e.g. 26100.**4946**) – shows the update level |
| **XPath** | the filter language with which a WEF subscription selects events from the log |
| **WEF bookmark** | the place in the log remembered by the controller, up to which it has already sent events |
| **tmpfs** (`/dev/shm`, `/run`) | a file system in RAM – its contents disappear on restart and never reach the disk |
| **umask** | the mask of default permissions of new files (`077` = owner only) |
| **systemd unit / timer** | a Linux system service / its schedule (the equivalent of a Task Scheduler task) |
| **journald** (`journalctl`) | the Linux system log and the command for browsing it |
| **nftables** (`nft`) | the Linux firewall |
| **chrony** (`chronyc`) | the time synchronization service on Debian and the command for checking its state |
| **conffile** | a configuration file from a `.deb` package; when you change it, `dpkg` asks during an upgrade which version to keep |

## Replace with your own

| What | Value in the examples | Where it appears |
|---|---|---|
| DNS domain / NetBIOS name / Kerberos realm | `contoso.com` / `CONTOSO` / `CONTOSO.COM` | everywhere; the Kerberos realm is always in capital letters |
| Domain DN | `DC=contoso,DC=com` | PowerShell, `ldapsearch` |
| Controller 1 (PDC emulator) | `DC01.contoso.com`, 10.0.1.10, Windows Server 2025 | `krb5.conf`, chrony, firewall, OpenWEC, `appsettings.json` |
| Controller 2 | `DC02.contoso.com`, 10.0.1.11, Windows Server 2022 | as above |
| Server network and gateway | 10.0.1.0/24, gateway 10.0.1.1 | Debian network, DNS reverse zone |
| DNS reverse zones / PTR name of the Watchdog server | `1.0.10.in-addr.arpa` (servers), `20.0.10.in-addr.arpa` (workstations) / `25` | 1.2, 2.4, 2.13, 8.2 |
| Watchdog server | `admng-wd` / `admng-wd.contoso.com` / 10.0.1.25 | DNS, SPN, certificate, firewall |
| Service account (computer object) | `ADMNG-WD` (`sAMAccountName` `ADMNG-WD$`) | 2.5, keytab, permissions |
| The "salt" of the keys in the keytab (derived from the two rows above) | `CONTOSO.COMhostadmng-wd.contoso.com` | 3.6, 3.7 |
| Existing Tier 0 OU | `OU=Tier 0,OU=Admin,DC=contoso,DC=com` | 2.2, 2.9 |
| Service account OU | `OU=Service Accounts,OU=Tier 0,OU=Admin,DC=contoso,DC=com` | 2.5 |
| Role group OU | `OU=Groups,OU=Tier 0,OU=Admin,DC=contoso,DC=com` | 2.3 |
| Watchdog role groups | `AdMng-Watchdog-Admins`, `AdMng-Watchdog-Viewers` | 2.3, `appsettings.json` |
| Administrator workstation subnet | 10.0.20.0/24 (in the lab, gateway 10.0.20.1) | firewall, `appsettings.json`, 1.6 |
| Administrator workstation (tests) | `ADM-WS01`, 10.0.20.15 | pilot – the expected "source" |
| PAW workstation addresses (the only SSH source to the server) | 10.0.20.15 – in the examples and in the lab the PAW role is played by ADM-WS01 | firewall (`PAWS`, 3.3) |
| Administrator account (a person) | `adm-jan` – a Tier 0 account in the domain and a local account on Debian | 1.5, 2.3, 3.2, pilot |
| Group allowed SSH on Debian | `ssh-admins` | 3.2 |
| SMTP relay | `smtp.contoso.com:587` (STARTTLS), address 10.0.1.30 (in the lab, Mailpit – 1.1) | firewall, the Channels and SMTP tab |
| Notification sender | `AD Watchdog <watchdog@contoso.com>` | 6.2 |
| Microsoft Teams | a Workflows flow (the URL is a secret) | 6.2 |
| Watchdog API address | `https://admng-wd.contoso.com:8443` | AD Manager |
| WEF address (OpenWEC) | `http://admng-wd.contoso.com:5985/wsman/SubscriptionManager/WEC` | GPO on the controllers |
| Certification authority (variant A) | `CA01.contoso.com\Contoso-Issuing-CA` (in the lab from 1.7: `DC01.contoso.com\Contoso-Issuing-CA`) | 1.7, 2.1, 2.5, 3.8 |
| Pilot test OU | `OU=Pilot,OU=Firma,DC=contoso,DC=com` | 1.5, 7 |
| Test accounts | `jan.kowalski`, `anna.nowak`, `józef.żak`, `svc-pilot`, `hd-ewa` | 1.5, 7 |
| Objects created in the pilot | `test.pilot1`, `dmsa-pilot`, `dmsa-pilot2`, GPO "Pilot - test" | 7 |
| gMSA account of version 0.2.0 (only when migrating from the Windows service) | `svc-admngwd$` | 0.4 (migration), 2.12 |

A different domain name in the lab (e.g. `lab.contoso.com`) is fine, but then change **everything** together: the
Kerberos realm (`LAB.CONTOSO.COM`), the DN (`DC=lab,DC=contoso,DC=com`), the full names (`DC01.lab.contoso.com`,
`admng-wd.lab.contoso.com`), the NetBIOS name (e.g. `LAB`) and the "salt" of the keys in the keytab
(`LAB.CONTOSO.COMhostadmng-wd.lab.contoso.com`, chapter 3.6).

---

## 0. Overview

The overview describes how version 0.3.0 works.

### 0.1 What runs where

```text
┌────────────────────────────────────────────────────────────────────────────────────────────────┐
│ DOMAIN CONTROLLERS (Tier 0)                                                                    │
│ DC01.contoso.com  10.0.1.10  Windows Server 2025, PDC emulator                                 │
│ DC02.contoso.com  10.0.1.11  Windows Server 2022                                               │
│ Security log ──► the WinRM service sends events (WEF, built into Windows, no agents)           │
└──────┬───────────────────────────────────────────▲─────────────────────────────────────────────┘
       │ WEF: HTTP 5985 + Kerberos                 │ LDAP 389 with signing and encryption (GSSAPI)
       │ (encrypted messages)                      │ SMB 445: reading GPOs from SYSVOL
       │                                           │ Kerberos 88, kpasswd 464, DNS 53, NTP 123
       ▼                                           │
┌──────┴───────────────────────────────────────────┴─────────────────────────────────────────────┐
│ WATCHDOG SERVER: admng-wd.contoso.com  10.0.1.25  Debian 13, outside the domain, Tier 0 server │
│ openwecd (OpenWEC) ──► /var/lib/openwec/out/<DC>/security.jsonl (transient files)              │
│ admng-watchdog: reads OpenWEC files (who, from where), detects changes via LDAP, reads GPOs    │
│   change history 3 years: /var/lib/admng-watchdog (SQLite, service account only)               │
│   notifications: e-mail via smtp.contoso.com:587 (STARTTLS), Teams Workflows (HTTPS 443)       │
│ /etc/admng-watchdog/admng-wd.keytab: keys of account ADMNG-WD$, password changed every 30 days │
└────────────────────────────────────────▲───────────────────────────────────────────────────────┘
                                         │ HTTPS 8443 + Kerberos, only from 10.0.20.0/24
┌────────────────────────────────────────┴───────────────────────────────────────────────────────┐
│ ADMINISTRATOR WORKSTATIONS 10.0.20.0/24                                                        │
│ AD Manager, Watchdog tab: events, rules, channels, status (roles from AD groups)               │
└────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- **On the domain controllers** you install nothing. The controllers send Security log events themselves, using the
  built-in Windows Event Forwarding (WEF) mechanism, to the OpenWEC receiver on the Watchdog server.
- **The Watchdog server** is Debian 13 (packages are also available for Debian 12) **not joined to the domain**.
  Kerberos, DNS and time are configured on it manually. Two systemd services run there: `openwecd` (receives events from
  the DCs) and `admng-watchdog` (detects changes, determines the actors, notifies, stores the history), plus two timers:
  the service account password change and the cleanup of OpenWEC files.
- **AD Manager** connects to the service over HTTPS on port 8443 with the operator's Windows account (Kerberos). Roles
  in Watchdog result from membership in AD groups.

### 0.2 What Watchdog sees and what it never does

Watchdog **sees**:

- **what** changed in AD: new, modified, moved and deleted accounts, groups, computers, OUs, memberships, account
  flags, critical attributes (delegation, SPN, Shadow Credentials, SID History, dMSA), domain policies, GPOs, trust
  relationships, PKI, schema – always with the old and new value (full list: [`WATCHDOG.md`](WATCHDOG.md), chapter 1);
- **who** did it and **from where** – the actor's account from the controller's Security log (change events) and the
  computer and IP address the change came from – determined from events 4624 (network logons – type 3 – and RDP – type
  10), 4625, 4771 and failed 4776;
- **when** and **on which DC**;
- **GPO content** – settings in the form "setting: was → is" (a new feature compared to version 0.2.0);
- **its own state**: the service account, keytab, event reception from each DC, time, certificate.

The change history (who, what, when, from where, was → is) is kept for **3 years** (1095 days). DC log records needed to
match changes – 30 days, records of logons and failed logons (4624/4625/4771/4776) – 48 hours (the determined source is
copied to the event, so it stays with it for 3 years).

Watchdog **never**:

- changes anything in AD except the password of its own account (every 30 days) – the service account is granted read
  permissions only; the service blocks and rolls back nothing, it only reports;
- reads passwords, LAPS, BitLocker keys, gMSA passwords or confidential attributes; it does not store passwords from
  GPO preferences (`cpassword`) in any form – it only notes that they exist;
- installs software on domain controllers;
- accepts NTLM logon (Kerberos only) and sends data outside the configured channels (e-mail, Teams);
- writes directory values or secrets to the system logs (journald).

### 0.3 The security model in plain words

1. **The Watchdog server is a Tier 0 asset – like a domain controller.** Whoever has root on it has the identity of the
   service account (read access to the whole directory and SYSVOL), the 3-year change history, and can silence alarms.
   That is why only 1–3 Tier 0 people administer the server, exclusively with SSH keys and only from the PAW
   workstation. Backups of this machine are also a Tier 0 asset. So is the platform it runs on: the virtual machine may
   sit **only** on the Tier 0 virtualization platform – on the same hosts (cluster) and under the same administrators as
   the domain controllers – or on a separate host whose administrators, console access, snapshots, backups and vTPM key
   protection are Tier 0. A hypervisor administrator can open the machine's console, copy its disk, take a snapshot or
   a memory dump, or revert the machine to an earlier state – that is, take over the keytab, the secrets key and the
   history, or "blind" Watchdog. Disk encryption by the hypervisor protects only against theft of the media, not against
   hypervisor administrators.
2. **The service account `ADMNG-WD$` is not an administrator.** It is an ordinary computer object with read permissions:
   it belongs to no privileged group (the service refuses to start if it does), cannot log on interactively or over RDP,
   cannot be delegated, and uses AES only. Its password has 120 random characters, nobody knows it, and it changes every
   30 days. Taking over this account gives an attacker read access to the directory and SYSVOL – the same as every
   logged-on domain user has, plus a few explicitly granted read permissions. Writing to its own object is blocked by a
   deny entry (2.5), creating computer accounts by the `ms-DS-MachineAccountQuota` = 0 limit (2.8), and every change to
   the account is a critical alarm anyway. **A consciously accepted risk (ADR-0009):** we do not block issuing a
   certificate to this account. When the forest has an Enterprise CA, whoever steals the keytab can request a
   certificate for `ADMNG-WD$` using a template in which domain computers may enroll (by default "Computer"), and log on
   with it as this account without a password for the entire validity period of the certificate (usually a year) – the
   30-day password change does not invalidate it. That is why, after a keytab leak, the certificates issued to the
   account must be revoked (2.5, "When the keytab may have leaked"). There is also an exception outside AD: whoever has
   this account's keytab can forge a Kerberos ticket to the Watchdog API as any domain user (a so-called *silver
   ticket*), including as a Watchdog administrator, and change rules or silence alarms (it will not shorten the history
   below the threshold from the startup settings – point 5). The keytab is shared by the service and OpenWEC – this is a
   conscious decision (4.3); a forged ticket will work only from the administrator subnet, because only from it does the
   API accept connections – but from **any** workstation in that subnet, including computers of people outside Tier 0
   (e.g. helpdesk with the Viewer role). This is because Watchdog administrative operations are allowed from the whole
   subnet, not only from the PAW workstation – this is also part of the accepted risk (ADR-0009). That is why the
   keytab, the system accounts that read it (the service and OpenWEC), machine backups and the server's root are Tier 0
   assets, and every change to the Watchdog configuration also goes to the existing recipients (point 3).
3. **Watchdog administrators are not the same as Domain Admins.** The `AdMng-Watchdog-Admins` group manages rules,
   channels and exceptions in Watchdog; its members need no rights in AD. Membership in Domain Admins gives no role in
   Watchdog. The reason: the people whose changes are monitored should not quietly switch off monitoring. A Domain Admin
   can technically add themselves to the Watchdog group – but that change is itself a critical event, and every change to
   the Watchdog configuration also goes to the **existing** recipients. The goal is visibility, not blocking.
4. **The role groups reside in the Tier 0 OU**, because control over them is control over alarms about Tier 0. Only Tier 0
   manages their members; Watchdog treats them as privileged groups.
5. **The startup settings** (who is a Watchdog administrator, which subnets connections may come from, whether critical
   events may be silenced, how long the history must be kept at minimum) reside in a file on the server that the
   service cannot change. Only the server's root changes them. A Watchdog administrator therefore cannot shorten the
   event retention in AD Manager below 3 years, and every retention shortening is a critical event.

### 0.4 Order of work

1. Test domain – chapter 1 (if you do not have one yet; 1.7 – an optional certification authority).
2. AD, part one – 2.1 (is there a CA), 2.2 (Tier 0 OU audit), 2.3 (groups), 2.4 (DNS).
3. Debian – 3.1 to 3.5 (the system, the .NET environment and AdMng Watchdog packages, accounts, firewall, DNS and time,
   Kerberos configuration).
4. The service account – 2.5 on the PAW (creating a **disabled** account, a deny entry for SELF, only then enabling the
   account and handing over the one-time password) and **immediately** 3.6 on Debian (`admng-watchdog-keytab init` – the
   one-time password is meant to live for a few minutes).
5. AD, part two – checking certificates issued to the account (when there is an Enterprise CA) and the GPO from 2.5,
   then 2.6 to 2.12 (after 2.6 and 2.7, perform the read tests described there on Debian), finally the checklist 2.13.
   Step 2.8 (`ms-DS-MachineAccountQuota` = 0) is a condition for a production deployment (the service will also start
   without it, but the Status tab warns – 2.8).
6. Debian – 3.7 (the password change timer), 3.8 (certificate: variant A from the company CA or B from the package),
   3.9 (backups, system log).
7. OpenWEC (4), the Watchdog service (5), AD Manager (6).
8. The pilot – chapter 7.

> **Migration from version 0.2.0 (the Windows service).** If Watchdog 0.2.0 is already running, the Windows service keeps
> working until the switchover (after chapter 5). You then perform some of the steps differently than described –
> otherwise you will break the working service:
>
> - **2.3** – do not create the groups (they already exist), move them to the Tier 0 OU; the SIDs do not change:
>   `Move-ADObject -Identity (Get-ADGroup AdMng-Watchdog-Admins).DistinguishedName -TargetPath 'OU=Groups,OU=Tier 0,OU=Admin,DC=contoso,DC=com'`
>   and the same for `AdMng-Watchdog-Viewers`.
> - **2.4 and 3.1** – Debian gets a temporary address (e.g. 10.0.1.26; in 3.1 also in `/etc/hosts`); the A record
>   `admng-wd` points to the old server until the switchover (2.4, "The record points to another server").
> - **3.2, 3.3, 3.8** – until the switchover, connect to Debian by the temporary address, not by name (the name leads to
>   the old server): `ssh adm-jan@10.0.1.26`, `scp adm-jan@10.0.1.26:admng-wd.csr .`,
>   `scp admng-wd.cer chain.p7b adm-jan@10.0.1.26:`, `Test-NetConnection 10.0.1.26 -Port 22` – also when testing the new
>   connection in the safety net from 3.3. In 3.4, `host admng-wd.contoso.com` shows the address of the old server until
>   the switchover – this is expected. After the switchover, the first connection by name will ask about the host key –
>   compare the fingerprint with the console (3.2); on a key change warning, remove the old entry:
>   `ssh-keygen -R admng-wd.contoso.com`.
> - **2.5** – create the account **without** the `HTTP/…` SPN: `-ServicePrincipalNames 'HOST/admng-wd.contoso.com', 'HOST/ADMNG-WD'`
>   (the controller would reject an SPN that the gMSA `svc-admngwd$` still has). The keytab from 3.6 contains the
>   `HTTP/…` name anyway – it is the same account key. Until the switchover, `setspn -Q HTTP/admng-wd.contoso.com` still
>   shows the gMSA, and in test 1 from 3.6 the `HTTP/…` row ends with an error – this is expected; the `host/…` row
>   must show `keytab entry valid`. `admng-watchdog-keytab init` and the password change timer check the keys with a
>   ticket to `host/…`, so they work normally, but `admng-watchdog-keytab verify` compares the kvno of the ticket to
>   `HTTP/…` and until the switchover ends with the result `verifyFailed` (also recorded in the Status tab) – run it only
>   after the switchover.
> - **2.12** – create the WEF GPO with the link disabled (`New-GPLink … -LinkEnabled No`); you enable it at the
>   switchover.
> - **History from version 0.2.0** (optional): at the switchover, after stopping the Windows service and **before** the
>   first start of the service on Debian (5.3), copy the database `%ProgramData%\AdMng\Watchdog\data\watchdog.db` (the
>   service is stopped – the file is consistent) to Debian: `scp` to your account, then
>   `install -o admng-watchdog -g admng-watchdog -m 0600 /home/adm-jan/watchdog.db /var/lib/admng-watchdog/watchdog.db`
>   and delete the copy from the home directory and from the PAW. Do **not** copy the `keys\` folder – you will enter the
>   SMTP password and the Teams address again in AD Manager (6.2). On startup the service makes a copy of the database
>   and runs migrations, the saved configuration moves to the OpenWEC event source, the 365-day event retention from
>   version 0.2.0 is raised to the 1095-day threshold, and change detection continues incrementally against the same
>   controllers. The database size limit from version 0.2.0 (10,240 MB) stays – the database and 7 copies then need up to
>   about 90 GB on `/var` (5.2); AD Manager 0.3.0 has no editor for this value.
> - **The switchover** (after chapter 5): stop the Windows service; `setspn -D HTTP/admng-wd.contoso.com svc-admngwd$`;
>   `setspn -S HTTP/admng-wd.contoso.com ADMNG-WD$`; change the A and PTR records of `admng-wd` to the Debian address
>   (2.4) – or, after shutting down the old server, move its address to Debian (`/etc/network/interfaces` and
>   `/etc/hosts`, from the machine console), in which case the DNS records stay unchanged; enable the WEF GPO link:
>   `Set-GPLink -Name 'AdMng - WEF' -Target 'OU=Domain Controllers,DC=contoso,DC=com' -LinkEnabled Yes`; then 5.3
>   and 6.1. Finally remove the leftovers of the old service – "Migration from version 0.2.0" at the end of 2.12.

---

## 1. Test domain – recipe

Goal: a domain that mirrors production (a forest with Windows Server 2025 and 2022 controllers, functional level 2016,
a Tier 0 OU), on which you can safely rehearse all the steps of this guide.

### 1.1 Virtual machines and network

| Machine | System | vCPU / RAM / disk | Address | Role |
|---|---|---|---|---|
| DC01 – **required** | Windows Server 2025 (evaluation version, Desktop Experience) | 2 / 4 GB / 60 GB | 10.0.1.10 | controller, DNS, PDC emulator |
| admng-wd – **required** | Debian 13 netinst | 2 / 4 GB / 60 GB, vTPM optional | 10.0.1.25 | Watchdog server |
| ADM-WS01 – **required** | Windows 11 Enterprise (evaluation) or Windows Server 2025 | 2 / 4 GB / 60 GB | 10.0.20.15 | administrator workstation: RSAT, AD Manager; in the lab also the PAW. AD Manager runs only on Windows, and a change made on the DC itself has the controller as its source – without this workstation you cannot verify "from where" |
| DC02 – optional | Windows Server 2022 (evaluation) | 2 / 4 GB / 60 GB | 10.0.1.11 | second controller – replication of the service account password, DC switchover, an older WinRM client |
| smtp – optional | Debian 13 netinst with Mailpit | 1 / 1 GB / 10 GB | 10.0.1.30 | the lab SMTP relay (you view messages in a browser) – needed for the e-mail tests in 6.2 and 7.2 |

- You download the evaluation versions from the Microsoft Evaluation Center. Windows Server 2025 in the evaluation
  version runs for 180 days, but requires **online activation within 10 days** – without Internet access it will start
  shutting down periodically. Give the machines Internet access through the hypervisor router (NAT) – an example for
  Hyper-V below.
- **Network – in the lab, a single flat network is simplest**, 10.0.1.0/24: ADM-WS01 at address 10.0.1.50 (gateway
  10.0.1.1). Then wherever the guide gives the subnet 10.0.20.0/24 or the address 10.0.20.15, enter `10.0.1.50/32` or
  `10.0.1.50`: the address and gateway in 1.6, the Debian firewall (`ADMIN_NET` and `PAWS`, 3.3), `AllowedRemoteSubnets`
  (5.2), the expected source in the pilot (7.2); the reverse zone 10.0.20.0/24 from 1.2 is then unnecessary. Two subnets
  as in the examples (10.0.1.0/24 for servers, 10.0.20.0/24 for workstations) require a router between them (e.g. a
  small Linux machine with two network cards) that forwards traffic between the subnets **without NAT** (without
  masquerading). Debian and the controllers must see the workstation's real address 10.0.20.15 – behind NAT they would
  see the router's address: the Debian firewall would reject SSH and the API, and the pilot would show the router as the
  source of changes.
- **Network in Hyper-V** (on the host, PowerShell as administrator) – an internal switch with NAT for the flat network
  10.0.1.0/24; the host gets the gateway address 10.0.1.1:

  ```powershell
  New-VMSwitch -Name LAB -SwitchType Internal
  New-NetIPAddress -InterfaceAlias 'vEthernet (LAB)' -IPAddress 10.0.1.1 -PrefixLength 24
  New-NetNat -Name LAB -InternalIPInterfaceAddressPrefix 10.0.1.0/24
  ```

  Hyper-V NAT does not answer DNS queries – in 1.2 set query forwarding from DC01 to a company or public DNS server
  (e.g. `9.9.9.9`), not to 10.0.1.1. The machine with Debian: **generation 2**, Secure Boot with the "Microsoft UEFI
  Certificate Authority" template (the default "Microsoft Windows" template will not boot Debian):

  ```powershell
  Set-VMFirmware -VMName admng-wd -SecureBootTemplate MicrosoftUEFICertificateAuthority
  Set-VMKeyProtector -VMName admng-wd -NewLocalKeyProtector; Enable-VMTPM -VMName admng-wd     # vTPM (optional)
  ```
- A vTPM for Debian is optional: with it you can verify the "TPM2 + host key" variant of secret encryption (chapter 3.9).
- **The SMTP relay in the lab.** On the `smtp` machine (Debian, as root) download Mailpit, check the SHA-256 sum and
  install the program (the version and sum as in `tools/testlab/watchdog/common.sh` – `MAILPIT_VERSION` – and
  `tools/testlab/watchdog/start-mailpit.sh`; you will find newer versions there too):

  ```bash
  apt install -y curl ca-certificates openssl
  cd /root && curl -fsSLO https://github.com/axllent/mailpit/releases/download/v1.31.5/mailpit-linux-amd64.tar.gz
  echo '5924ace84f38c83c29f941a644a2d7be6c11f339eb4a9a12ab526414897ceed1  mailpit-linux-amd64.tar.gz' | sha256sum -c
  tar -xzf mailpit-linux-amd64.tar.gz mailpit && install -m 0755 mailpit /usr/local/bin/
  ```

  Expected: `mailpit-linux-amd64.tar.gz: OK`. Then create a permanent self-signed certificate for `smtp.contoso.com`
  (permanent, because you will pin its thumbprint in 6.2) and start Mailpit with STARTTLS required:

  ```bash
  openssl req -x509 -newkey rsa:2048 -nodes -days 825 -subj '/CN=smtp.contoso.com' \
      -addext 'subjectAltName=DNS:smtp.contoso.com' -keyout /root/smtp.key -out /root/smtp.crt
  systemd-run --unit=mailpit /usr/local/bin/mailpit --smtp 0.0.0.0:587 --listen 0.0.0.0:8025 \
      --smtp-require-starttls --smtp-auth-accept-any --smtp-tls-cert /root/smtp.crt --smtp-tls-key /root/smtp.key
  ```

  After a machine restart, repeat the `systemd-run` command. After the domain is created (1.2), add the record on DC01
  `Add-DnsServerResourceRecordA -ZoneName contoso.com -Name smtp -IPv4Address 10.0.1.30 -CreatePtr`. You view messages
  at `http://10.0.1.30:8025`.
- **Microsoft Teams** is optional – it requires a Microsoft 365 tenant. Without one, skip the Teams channel in 6.2 and
  7.2.

### 1.2 DC01 – the new forest `contoso.com`

**Where:** DC01, PowerShell as local administrator. **What and why:** a static address, name, the AD DS role and a new
forest at the Windows Server 2016 functional level – the one that a Windows Server 2022 controller enforces in
production.

```powershell
# address and name (check the adapter name: Get-NetAdapter)
New-NetIPAddress -InterfaceAlias 'Ethernet' -IPAddress 10.0.1.10 -PrefixLength 24 -DefaultGateway 10.0.1.1
Set-DnsClientServerAddress -InterfaceAlias 'Ethernet' -ServerAddresses 127.0.0.1
Rename-Computer -NewName DC01 -Restart
```

After the restart:

```powershell
Install-WindowsFeature AD-Domain-Services -IncludeManagementTools
Install-ADDSForest -DomainName contoso.com -DomainNetbiosName CONTOSO `
    -ForestMode WinThreshold -DomainMode WinThreshold -InstallDns `
    -SafeModeAdministratorPassword (Read-Host -AsSecureString 'DSRM password')
```

The server will restart as DC01.contoso.com. Next, configure DNS query forwarding to the outside and reverse zones for
PTR records – of servers and administrator workstations (from the PTR record Watchdog takes the name of the computer
the change came from – 7.2). DC01 registers its PTR record only at the next DNS registration – hence the last command:

```powershell
Set-DnsServerForwarder -IPAddress 10.0.1.1          # the lab router's DNS; with Hyper-V NAT – e.g. 9.9.9.9 (1.1)
Add-DnsServerPrimaryZone -NetworkId '10.0.1.0/24'  -ReplicationScope Domain -DynamicUpdate Secure
Add-DnsServerPrimaryZone -NetworkId '10.0.20.0/24' -ReplicationScope Domain -DynamicUpdate Secure   # skip on a flat network (1.1)
ipconfig /registerdns
```

**Verification:**

```powershell
Get-ADDomain | Format-List DNSRoot, NetBIOSName, DomainMode, PDCEmulator
Get-ADForest | Format-List ForestMode, SchemaMaster
Resolve-DnsName deb.debian.org
Resolve-DnsName 10.0.1.10
```

Expected: `DNSRoot : contoso.com`, `NetBIOSName : CONTOSO`, `DomainMode : Windows2016Domain`,
`PDCEmulator : DC01.contoso.com`, `ForestMode : Windows2016Forest`; `Resolve-DnsName` returns addresses (query
forwarding works – Debian will download updates through this DNS); `Resolve-DnsName 10.0.1.10` returns
`DC01.contoso.com` (the PTR record – a few minutes after `ipconfig /registerdns`). A new domain on Windows Server 2025
**requires LDAP signing** by default – Watchdog satisfies this (Kerberos/GSSAPI encryption, chapter 3.6).

### 1.3 DC02 – the second controller (optional)

**Where:** DC02 (Windows Server 2022). **What and why:** a second controller lets you verify replication of the service
account passwords, switching to another DC and the behavior of an older WinRM client (WEF).

```powershell
New-NetIPAddress -InterfaceAlias 'Ethernet' -IPAddress 10.0.1.11 -PrefixLength 24 -DefaultGateway 10.0.1.1
Set-DnsClientServerAddress -InterfaceAlias 'Ethernet' -ServerAddresses 10.0.1.10
Add-Computer -DomainName contoso.com -NewName DC02 -Credential CONTOSO\Administrator -Restart
```

After the restart (log on as `CONTOSO\Administrator`):

```powershell
Install-WindowsFeature AD-Domain-Services -IncludeManagementTools
Install-ADDSDomainController -DomainName contoso.com -InstallDns -Credential (Get-Credential CONTOSO\Administrator) `
    -SafeModeAdministratorPassword (Read-Host -AsSecureString 'DSRM password')
```

Finally, set DNS on both controllers mutually – the partner first, then the controller's own loopback address:

```powershell
Set-DnsClientServerAddress -InterfaceAlias 'Ethernet' -ServerAddresses 10.0.1.11, 127.0.0.1    # on DC01
Set-DnsClientServerAddress -InterfaceAlias 'Ethernet' -ServerAddresses 10.0.1.10, 127.0.0.1    # on DC02
ipconfig /registerdns                                                                          # on DC02 (PTR record)
```

**Verification** (on DC01): `repadmin /replsummary` – no errors; `Get-ADDomainController -Filter * | Format-Table
HostName, OperatingSystem, IPv4Address` – two controllers.

> **Without DC02.** If you are not building a second controller, remove `DC02.contoso.com` from `krb5.conf` (3.5), from
> the time sources file `ad-dcs.sources` and from `resolv.conf` (3.4), the address 10.0.1.11 from `DCS` in the firewall
> (3.3), `--dc 'DC02$@CONTOSO.COM'` when generating the subscription (4.4) and `DC02.contoso.com` from
> `Directory.Servers` (5.2). Skip the steps and verifications concerning DC02 (among others in 2.4, 2.9, 2.10 – also from
> the audit policy copy loop – 2.12, 3.4, 3.6 and 4.6).

### 1.4 Time in the domain

**Where:** DC01. **What and why:** the PDC emulator is the time source for the whole domain and – in chapter 3.4 – for the
Debian server. Kerberos rejects authentication when the clocks differ by more than 5 minutes. If DC01 has no source at
all, Debian (chrony) may consider it unsynchronized and reject it.

```powershell
w32tm /config /manualpeerlist:"0.pl.pool.ntp.org,0x8 1.pl.pool.ntp.org,0x8" /syncfromflags:manual /reliable:yes /update
Restart-Service w32time
w32tm /resync /rediscover
```

**Verification:** `w32tm /query /status` – `Source:` points to an NTP server (not `Local CMOS Clock`),
`Leap Indicator: 0`. In production the PDC emulator usually already has a source set – change nothing, just check.

In a Hyper-V virtual machine, the result `Source: VM IC Time Synchronization Provider` means the host imposes the time,
and the configured NTP servers are skipped. On DC01 disable this provider and synchronize again:

```powershell
reg add HKLM\SYSTEM\CurrentControlSet\Services\W32Time\TimeProviders\VMICTimeProvider /v Enabled /t REG_DWORD /d 0 /f
Restart-Service w32time; w32tm /resync /rediscover
```

In VMware, turn off "Synchronize guest time with host" in the machine settings.

### 1.5 OU structure, test accounts and groups

**Where:** DC01, as `CONTOSO\Administrator` (ADM-WS01 is created only in 1.6). **What and why:** a minimal structure
with a Tier 0 OU (a place for the service account and role groups), an OU for ordinary accounts and a "Pilot" OU for
dMSA tests, and several accounts for the tests from chapter 7. In production the Tier 0 OU already exists – do not
create a new one, use yours.

```powershell
$d = 'DC=contoso,DC=com'
New-ADOrganizationalUnit -Name 'Admin' -Path $d
New-ADOrganizationalUnit -Name 'Tier 0' -Path "OU=Admin,$d"
'Accounts', 'Groups', 'Service Accounts' | ForEach-Object { New-ADOrganizationalUnit -Name $_ -Path "OU=Tier 0,OU=Admin,$d" }
New-ADOrganizationalUnit -Name 'Firma' -Path $d
'Użytkownicy', 'Grupy', 'Komputery', 'Pilot' | ForEach-Object { New-ADOrganizationalUnit -Name $_ -Path "OU=Firma,$d" }

$pw = Read-Host -AsSecureString 'Test accounts password'
$u  = "OU=Użytkownicy,OU=Firma,$d"
New-ADUser -Name 'Jan Kowalski' -SamAccountName 'jan.kowalski' -UserPrincipalName 'jan.kowalski@contoso.com' -Path $u -AccountPassword $pw -Enabled $true
New-ADUser -Name 'Anna Nowak'   -SamAccountName 'anna.nowak'   -UserPrincipalName 'anna.nowak@contoso.com'   -Path $u -AccountPassword $pw -Enabled $true
New-ADUser -Name 'Józef Żak'    -SamAccountName 'józef.żak'    -UserPrincipalName 'jozef.zak@contoso.com'    -Path $u -AccountPassword $pw -Enabled $true
New-ADUser -Name 'Ewa Helpdesk' -SamAccountName 'hd-ewa'       -UserPrincipalName 'hd-ewa@contoso.com'       -Path $u -AccountPassword $pw -Enabled $true
New-ADUser -Name 'svc-pilot'    -SamAccountName 'svc-pilot'    -Path "OU=Pilot,OU=Firma,$d" -AccountPassword $pw -Enabled $true

# a person's administrative account (Tier 0) – in production you use existing administrator accounts
New-ADUser -Name 'adm-jan' -SamAccountName 'adm-jan' -UserPrincipalName 'adm-jan@contoso.com' `
    -Path "OU=Accounts,OU=Tier 0,OU=Admin,$d" -AccountPassword $pw -Enabled $true
Add-ADGroupMember -Identity "$((Get-ADDomain).DomainSID)-512" -Members 'adm-jan'     # Domain Admins (by SID – independent of language)

New-ADGroup -Name 'Księgowość' -GroupScope Global -GroupCategory Security -Path "OU=Grupy,OU=Firma,$d"
New-ADGroup -Name 'Helpdesk'   -GroupScope Global -GroupCategory Security -Path "OU=Grupy,OU=Firma,$d"
Add-ADGroupMember -Identity 'Helpdesk' -Members 'hd-ewa'
# a typical Tier 1/2 delegation: Helpdesk resets passwords in the Użytkownicy OU (the audit from 2.2 will not report it – it is outside Tier 0)
dsacls "OU=Użytkownicy,OU=Firma,$d" /I:S /G "CONTOSO\Helpdesk:CA;Reset Password;user"
```

Set the account lockout policy needed for the lockout test in the pilot (threshold 5 failed attempts, 15 minutes):

```powershell
Set-ADDefaultDomainPasswordPolicy -Identity contoso.com -LockoutThreshold 5 `
    -LockoutDuration 00:15:00 -LockoutObservationWindow 00:15:00
```

**Verification:** `Get-ADUser -Filter * -SearchBase "OU=Firma,$d" | Format-Table SamAccountName, Enabled` – five
accounts, `Enabled = True`; `Get-ADGroupMember "$((Get-ADDomain).DomainSID)-512"` – contains `adm-jan`.

### 1.6 The administrator workstation ADM-WS01

**Where:** ADM-WS01. **What and why:** from this workstation you make the test changes in the pilot (Watchdog is to show
it as the source) and connect to the service with AD Manager.

```powershell
New-NetIPAddress -InterfaceAlias 'Ethernet' -IPAddress 10.0.20.15 -PrefixLength 24 -DefaultGateway 10.0.20.1
# on a flat network (1.1) instead of the row above:
# New-NetIPAddress -InterfaceAlias 'Ethernet' -IPAddress 10.0.1.50 -PrefixLength 24 -DefaultGateway 10.0.1.1
Set-DnsClientServerAddress -InterfaceAlias 'Ethernet' -ServerAddresses 10.0.1.10, 10.0.1.11
Add-Computer -DomainName contoso.com -NewName ADM-WS01 -Credential CONTOSO\Administrator -Restart
```

On Windows 11 install RSAT (on Windows Server: `Install-WindowsFeature RSAT-AD-PowerShell, GPMC, RSAT-DNS-Server`):

```powershell
'Rsat.ActiveDirectory.DS-LDS.Tools~~~~0.0.1.0', 'Rsat.GroupPolicy.Management.Tools~~~~0.0.1.0', 'Rsat.Dns.Tools~~~~0.0.1.0' |
    ForEach-Object { Add-WindowsCapability -Online -Name $_ }
```

Install AD Manager 0.3.0 (the archive `AdMng-0.3.0-win-x64.zip` from the v0.3.0 release – older versions do not have the
Watchdog certificate confirmation window). Log on to the workstation with the account `CONTOSO\adm-jan`.

**Verification:** `Get-ADDomain` works on the workstation; `nltest /sc_query:contoso.com` – `Trusted DC Connection Status
Status = 0 0x0 NERR_Success`; after `ipconfig /registerdns` the command `Resolve-DnsName 10.0.20.15` (on a flat network:
`Resolve-DnsName 10.0.1.50`) returns `ADM-WS01.contoso.com` (the PTR record – Watchdog takes the workstation name from
it).

### 1.7 A certification authority in the lab (optional)

**Where:** DC01, PowerShell as `CONTOSO\Administrator`. **What and why:** without a certification authority, step 2.1
always ends with variant B (a certificate generated by the package). An Enterprise-type authority in the lab lets you
rehearse variant A from 3.8 and checking and revoking certificates issued to the service account from 2.5. **In the lab
only** – in production the certification authority never runs on a domain controller.

```powershell
Install-WindowsFeature ADCS-Cert-Authority -IncludeManagementTools
Install-AdcsCertificationAuthority -CAType EnterpriseRootCA -CACommonName 'Contoso-Issuing-CA' -Force
```

**Verification:** the command from 2.1 shows the authority `Contoso-Issuing-CA` with `dNSHostName : DC01.contoso.com` and
the `WebServer` template on the `certificateTemplates` list. In the lab the authority configuration string is
`DC01.contoso.com\Contoso-Issuing-CA` – enter it everywhere the guide gives `CA01.contoso.com\Contoso-Issuing-CA` (2.1,
2.5, 3.8).

---

## 2. Preparing AD step by step

You perform all commands in this chapter on the **PAW** in PowerShell (Windows PowerShell 5.1 or PowerShell 7) with the
ActiveDirectory module, as a member of Domain Admins – unless a step says otherwise. No step requires restarting the
controllers.

- **Scripts saved to files** (2.2, 2.9) save in **UTF-8 with BOM** encoding (Notepad: File → Save as → Encoding
  "UTF-8 with BOM"; VS Code: "Save with Encoding" → "UTF-8 with BOM") or run them in PowerShell 7 (`pwsh`). Without a
  BOM, Windows PowerShell 5.1 will read the Polish letters incorrectly and the script will end with a syntax error.
  Commands pasted directly into the PowerShell window do not have this problem.
- **GPO setting names** are given in English – as in the English GPMC console and in Microsoft documentation. Paths are
  given in both languages; the Polish console shows the same settings in the same place in the tree under translated
  names. Where possible, we also give the PowerShell command that sets the value without searching the tree.

### 2.1 Is there a certification authority (AD CS) in the domain?

**Where:** PAW. **What and why:** the answer determines where the service gets the TLS certificate for the API (chapter 3.8).
The command only reads the forest configuration.

```powershell
$cfg = (Get-ADRootDSE).configurationNamingContext
Get-ADObject -SearchBase "CN=Enrollment Services,CN=Public Key Services,CN=Services,$cfg" `
    -LDAPFilter '(objectClass=pKIEnrollmentService)' -Properties dNSHostName, certificateTemplates |
    Format-List Name, dNSHostName, certificateTemplates
```

How to read the result:

| Result | Meaning | Next |
|---|---|---|
| Empty | There is no Enterprise CA in the forest (in the lab without 1.7 – always). This query does not show a Standalone CA – ask the PKI team. | **Variant B** in 3.8: certificate generated on the server, thumbprint confirmed in AD Manager. |
| A CA with `WebServer` in the `certificateTemplates` list | A web server certificate can be issued. | **Variant A** in 3.8. Note the configuration string `<dNSHostName>\<Name>`, e.g. `CA01.contoso.com\Contoso-Issuing-CA`. |
| A CA without the `WebServer` template | The CA exists but does not issue server certificates. | Ask the PKI team to publish the template (or a copy of it) with the Enroll permission for Tier 0 administrators – or use variant B. |

When the forest has an Enterprise CA, the service account (like any domain computer) can request certificates from it
using the templates that allow it. We do not block this – it is a consciously accepted risk (2.5, "Certificates and the
service account"); after creating the keytab you only check that no certificate has been issued to the account.

When a CA exists, check that it responds:

```powershell
certutil -config "CA01.contoso.com\Contoso-Issuing-CA" -ping
```

Expected: `Server "Contoso-Issuing-CA" ICertRequest2 interface is alive` and `CertUtil: -ping command completed
successfully.` (The command `certutil -config - -ping` does the same but opens a CA selection window.)

### 2.2 Delegation audit in the Tier 0 OU

> **In short.** What it changes: nothing – the script only reads ACLs; you make the fixes yourself, after saving a copy
> of the ACL. Where: PAW. How long: a few minutes, plus time for the fixes. Result: a list of entries to fix, or an
> empty list. Rollback of the fixes: from the saved SDDL file (below the table in 2.13).

**Where:** PAW. **What and why:** the service account and the role groups will land in the existing Tier 0 OU. If anyone
outside Tier 0 (helpdesk, an application account, an Exchange group) has write permissions there – even inherited from a
higher OU – they can take over the service account or change the members of the Watchdog groups. The script only
**reads** ACLs: the Tier 0 OU, all objects under it, `AdminSDHolder` (the permission template of protected accounts) and
the parent OUs up to the domain object, and lists the entries that grant write permissions to anyone outside the Tier 0
set, as well as owners outside Tier 0 (an owner can always change an object's permissions).

Save as `Test-AdMngTier0Delegation.ps1`:

```powershell
# Test-AdMngTier0Delegation.ps1 – delegation audit of the Tier 0 OU, its ancestors and AdminSDHolder (read-only)
param(
    [string]   $Tier0Ou = 'OU=Tier 0,OU=Admin,DC=contoso,DC=com',
    [string[]] $AdditionalTier0Sids = @()      # SIDs of your own Tier 0 administrator groups, if you have any
)
Import-Module ActiveDirectory
$domain  = Get-ADDomain
$dSid    = $domain.DomainSID.Value
$rootSid = (Get-ADDomain -Identity (Get-ADForest).RootDomain).DomainSID.Value
$rootDse = Get-ADRootDSE
$SidType = [System.Security.Principal.SecurityIdentifier]

# Tier 0 set: SYSTEM, Administrators, Enterprise Domain Controllers, Domain Admins, Domain Controllers, Key Admins,
# Enterprise Read-only DCs, Schema Admins, Enterprise Admins, Enterprise Key Admins
$tier0 = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-9', "$dSid-512", "$dSid-516", "$dSid-526",
           "$rootSid-498", "$rootSid-518", "$rootSid-519", "$rootSid-527") + $AdditionalTier0Sids

# Entries that are usually defaults – shown with a comment, for assessment
$notes = @{
    'S-1-5-10'     = 'SELF: obiekt sam o sobie – zwykle domyślne'
    'S-1-3-0'      = 'CREATOR OWNER: szablon dla nowych obiektów'
    'S-1-5-32-548' = 'Account Operators: domyślne na OU – grupa ma być pusta'
    'S-1-5-32-550' = 'Print Operators: domyślne na OU – grupa ma być pusta'
    'S-1-5-32-561' = 'Terminal Server License Servers: domyślne (atrybut terminalServer)'
    "$dSid-517"    = 'Cert Publishers: domyślne (userCertificate) – traktuj grupę jak Tier 0'
}
$changePassword = [guid]'ab721a53-1e2f-11d0-9819-00aa0040529b'    # User-Change-Password (requires the old password)
$sendTo         = [guid]'ab721a55-1e2f-11d0-9819-00aa0040529b'    # Send-To (default on groups)

# Names of classes, attributes and extended rights instead of GUIDs
$names = @{}
Get-ADObject -SearchBase $rootDse.schemaNamingContext -LDAPFilter '(schemaIDGUID=*)' -Properties lDAPDisplayName, schemaIDGUID |
    ForEach-Object { $names[([guid]$_.schemaIDGUID).Guid] = $_.lDAPDisplayName }
Get-ADObject -SearchBase "CN=Extended-Rights,$($rootDse.configurationNamingContext)" -LDAPFilter '(objectClass=controlAccessRight)' `
    -Properties displayName, rightsGuid | ForEach-Object { $names[([guid]$_.rightsGuid).Guid] = $_.displayName }
function Get-GuidName([guid]$g, [string]$empty) {
    if ($g -eq [guid]::Empty) { return $empty }
    if ($names.ContainsKey($g.Guid)) { return $names[$g.Guid] }
    return $g.Guid
}
function Get-SidName([string]$sid) {
    try { return ([System.Security.Principal.SecurityIdentifier]$sid).Translate([System.Security.Principal.NTAccount]).Value }
    catch { return $sid }
}
function Get-Scope($r) {
    if (([int]$r.PropagationFlags -band 2) -ne 0) { return 'tylko podrzędne: ' + (Get-GuidName $r.InheritedObjectType 'wszystkie') }
    if ($r.InheritanceType -ne 'None') { return 'ten obiekt i podrzędne' }
    return 'ten obiekt'
}
$adr       = [System.DirectoryServices.ActiveDirectoryRights]
$dangerous = [int][System.DirectoryServices.ActiveDirectoryRights]'CreateChild, DeleteChild, Self, WriteProperty, DeleteTree, ExtendedRight, Delete, WriteDacl, WriteOwner'

$top = (Get-ADOrganizationalUnit -Identity $Tier0Ou).DistinguishedName
$adminSdHolder = "CN=AdminSDHolder,CN=System,$($domain.DistinguishedName)"
$objects = @(Get-ADObject -SearchBase $top -SearchScope Subtree -Filter * -Properties nTSecurityDescriptor) +
           @(Get-ADObject -Identity $adminSdHolder -Properties nTSecurityDescriptor)

$findings = @(foreach ($o in $objects) {
    $sd = $o.nTSecurityDescriptor
    $isRoot = ($o.DistinguishedName -eq $top) -or ($o.DistinguishedName -eq $adminSdHolder)
    $owner = $sd.GetOwner($SidType).Value
    if ($tier0 -notcontains $owner) {
        [pscustomobject]@{ Obiekt = $o.DistinguishedName; Kto = Get-SidName $owner; SID = $owner
            Prawa = 'WŁAŚCICIEL (może zmienić uprawnienia)'; Dotyczy = ''; Zakres = ''; Odziedziczone = ''; Przodek = $false; Uwagi = $notes[$owner] }
    }
    # On the Tier 0 OU itself and on AdminSDHolder – all entries; below only explicit ones (inherited ones are shown by the parent object)
    foreach ($r in $sd.GetAccessRules($true, $isRoot, $SidType)) {
        $sid = $r.IdentityReference.Value
        if ($r.AccessControlType -ne 'Allow' -or $tier0 -contains $sid) { continue }
        if (([int]$r.ActiveDirectoryRights -band $dangerous) -eq 0) { continue }
        $note = $notes[$sid]
        if ($r.ObjectType -eq $changePassword) { $note = 'Zmiana własnego hasła (wymaga starego) – domyślne' }
        if ($r.ObjectType -eq $sendTo) { $note = 'Send To na grupie – domyślne, nie daje zmiany członków' }
        [pscustomobject]@{ Obiekt = $o.DistinguishedName; Kto = Get-SidName $sid; SID = $sid
            Prawa = $r.ActiveDirectoryRights.ToString(); Dotyczy = Get-GuidName $r.ObjectType '(wszystko)'
            Zakres = Get-Scope $r; Odziedziczone = $r.IsInherited; Przodek = $false; Uwagi = $note }
    }
})

# Ancestors of the Tier 0 OU up to the domain object: owner and explicit entries through which the whole Tier 0 OU can be taken over
$takeover = [int]($adr::WriteDacl -bor $adr::WriteOwner)                # also GenericAll
$gpLink   = [guid]'f30e3bbe-9ff0-11d1-b603-0000f80367c1'                # gPLink – GPO links
$gpOpts   = [guid]'f30e3bbf-9ff0-11d1-b603-0000f80367c1'                # gPOptions – blocking GPO inheritance
$ancestors = @()
$p = $top
while ($p -ne $domain.DistinguishedName) {
    $p = ($p -split '(?<!\\),', 2)[1]                                    # DN without the first component (skipping \,)
    if (-not $p) { break }
    $ancestors += $p
}
$findings += @(foreach ($dn in $ancestors) {
    $sd = (Get-ADObject -Identity $dn -Properties nTSecurityDescriptor).nTSecurityDescriptor
    $owner = $sd.GetOwner($SidType).Value
    if ($tier0 -notcontains $owner) {
        [pscustomobject]@{ Obiekt = $dn; Kto = Get-SidName $owner; SID = $owner
            Prawa = 'WŁAŚCICIEL (może zmienić uprawnienia)'; Dotyczy = ''; Zakres = ''; Odziedziczone = ''; Przodek = $true; Uwagi = '' }
    }
    foreach ($r in $sd.GetAccessRules($true, $false, $SidType)) {          # only explicit entries, of any scope
        $sid = $r.IdentityReference.Value
        if ($r.AccessControlType -ne 'Allow' -or $tier0 -contains $sid) { continue }
        $rights = [int]$r.ActiveDirectoryRights
        $write  = (($rights -band [int]$adr::WriteProperty) -ne 0) -and ($r.ObjectType -in [guid]::Empty, $gpLink, $gpOpts)   # also GenericWrite
        if ((($rights -band $takeover) -eq 0) -and -not $write) { continue }
        [pscustomobject]@{ Obiekt = $dn; Kto = Get-SidName $sid; SID = $sid
            Prawa = $r.ActiveDirectoryRights.ToString(); Dotyczy = Get-GuidName $r.ObjectType '(wszystko)'
            Zakres = Get-Scope $r; Odziedziczone = $false; Przodek = $true; Uwagi = '' }
    }
})
foreach ($g in 'S-1-5-32-548', 'S-1-5-32-549', 'S-1-5-32-550', 'S-1-5-32-551') {
    Write-Host ('{0}: członków {1}' -f (Get-SidName $g), @(Get-ADGroupMember -Identity $g).Count)
}
$findings | Sort-Object @{ Expression = { [bool]$_.Uwagi } }, Obiekt
```

Running it:

```powershell
.\Test-AdMngTier0Delegation.ps1 -Tier0Ou 'OU=Tier 0,OU=Admin,DC=contoso,DC=com' | Out-GridView -Title 'Delegacje Tier 0'
```

If you have your own Tier 0 administrator groups (e.g. `T0-Admins`), add their SIDs:
`-AdditionalTier0Sids (Get-ADGroup T0-Admins).SID.Value`. The script does not expand group membership and does not
evaluate "Deny" entries.

**How to read the result.** At the top of the list are the rows **without a comment** – these are the real findings:

| Column | Meaning |
|---|---|
| `Obiekt` | where the entry is (the Tier 0 OU, an object under it, or AdminSDHolder) |
| `Kto` / `SID` | who was granted the permission |
| `Prawa` | e.g. `GenericAll` (full control), `WriteProperty`, `ExtendedRight` (e.g. password reset), `WriteDacl` (changing permissions), `CreateChild` |
| `Dotyczy` | attribute, class or extended right (e.g. `User-Force-Change-Password` = password reset, `member` = group members, `msDS-DelegatedManagedServiceAccount` = creating dMSAs); `(wszystko)` = no restriction |
| `Zakres` | whether the entry applies to this object or only to child objects of a given class |
| `Odziedziczone` | `True` = the entry comes from a higher OU or from the domain object – you fix it **there** |
| `Przodek` | `True` = the owner or an explicit entry on a parent OU (e.g. `OU=Admin`) or on the domain object. Only entries through which the whole Tier 0 OU can be taken over are shown: `GenericAll`, `WriteDacl`, `WriteOwner`, `GenericWrite` and `WriteProperty` to all attributes or to `gPLink`/`gPOptions` |

Typical findings and what to do about them:

- **Helpdesk with password reset** on the Tier 0 OU (often inherited from `OU=Admin` or from the domain object) – remove
  the entry at the source or disable inheritance on the Tier 0 OU. Helpdesk must not reset the passwords of Tier 0
  accounts.
- **Exchange groups** (e.g. `Exchange Windows Permissions` with `WriteDacl` on the domain object – a row with `Przodek` =
  `True`) – a known risk; apply Microsoft's recommendations for Exchange (the "split permissions" model or removing
  `WriteDacl`).
- **Entries and owners on ancestors** (`Przodek` = `True`) – nothing is visible under the Tier 0 OU, and yet the owner or
  holder of `WriteDacl`/`WriteOwner`/`GenericAll` on `OU=Admin` or on the domain object can add an inherited entry there
  at any moment that will flow down to the Tier 0 OU (and take over the service account and the Watchdog groups). Write
  access to `gPLink` makes it possible to link a GPO to the ancestor that will cover every computer in the Tier 0 OU
  (e.g. PAW workstations) – an enforced link works despite blocked inheritance. Fix these entries on the ancestor.
  Disabling permission inheritance on the Tier 0 OU is only a stopgap: it stops inherited entries but not GPO links.
- **Application or backup accounts** with `GenericAll` – limit them to the attributes they need or move them to Tier 0.
- **An owner** that is a personal account or a group outside Tier 0 – change the owner to Domain Admins.
- **Account Operators / Print Operators** – the entries are default; the groups should be **empty** (the script prints
  the member count; this also applies to Server Operators and Backup Operators).

Before you change anything, save the object's current ACL (this is your rollback plan):

```powershell
$dn = 'OU=Tier 0,OU=Admin,DC=contoso,DC=com'        # or the DN of the ancestor or object you are fixing
(Get-Acl -Path "AD:\$dn").Sddl | Out-File "$env:USERPROFILE\Documents\acl-tier0-$(Get-Date -Format yyyyMMdd-HHmm).txt"
```

Fixes: remove entries in "Active Directory Users and Computers" (View → Advanced Features → Properties → Security →
Advanced) or with `dsacls "<DN>" /R "CONTOSO\<group>"` (removes **all** entries of that group on that object).
You change the owner like this:

```powershell
$acl = Get-Acl -Path "AD:\$dn"
$acl.SetOwner([System.Security.Principal.SecurityIdentifier]"$((Get-ADDomain).DomainSID)-512")
Set-Acl -Path "AD:\$dn" -AclObject $acl
```

Protected accounts (`adminCount=1`) get their permissions from `AdminSDHolder` every hour – a fix on the account itself
without a fix on `AdminSDHolder` will be overwritten.

**Verification:** running the script again shows no rows without a comment; the operator groups have 0 members.
Rows with a comment remain – these are the default entries of every object (e.g. SELF, CREATOR OWNER, Account Operators,
changing one's own password by Everyone, "Send To" for Authenticated Users on groups). After 2.3 and 2.5 run the script
once more: the new groups and the service account must not add any rows without a comment.

**Practice in the test domain:** deliberately grant a wrong delegation and check that the script finds it, then remove it:

```powershell
dsacls 'OU=Tier 0,OU=Admin,DC=contoso,DC=com' /I:S /G 'CONTOSO\Helpdesk:CA;Reset Password;user'
.\Test-AdMngTier0Delegation.ps1 | Where-Object Kto -like '*Helpdesk' | Format-List
dsacls 'OU=Tier 0,OU=Admin,DC=contoso,DC=com' /R 'CONTOSO\Helpdesk'
```

### 2.3 Role groups `AdMng-Watchdog-Admins` and `AdMng-Watchdog-Viewers`

**Where:** PAW. **What and why:** two global security groups decide who has access to Watchdog in AD Manager:

- **Admins** – change rules, channels, exceptions, SMTP and Teams, and retry deliveries (they also have the Viewers
  permissions);
- **Viewers** – browse events and status, acknowledge events; they do not see exceptions, recipient addresses or SMTP
  server data.

When migrating from version 0.2.0 the groups already exist – do not create them, move them instead (box in 0.4).

```powershell
$g = 'OU=Groups,OU=Tier 0,OU=Admin,DC=contoso,DC=com'
New-ADGroup -Name 'AdMng-Watchdog-Admins'  -GroupScope Global -GroupCategory Security -Path $g -Description 'AdMng Watchdog - administratorzy (reguły, kanały)'
New-ADGroup -Name 'AdMng-Watchdog-Viewers' -GroupScope Global -GroupCategory Security -Path $g -Description 'AdMng Watchdog - podgląd i potwierdzanie zdarzeń'
Add-ADGroupMember -Identity 'AdMng-Watchdog-Admins' -Members 'adm-jan'
(Get-ADGroup 'AdMng-Watchdog-Admins').SID.Value      # -> Authorization.AdminSids  (chapter 5.2)
(Get-ADGroup 'AdMng-Watchdog-Viewers').SID.Value     # -> Authorization.ViewerSids (chapter 5.2)
```

Rules:

- The members are people's **administrative accounts** (e.g. `adm-jan`), which they use on the 10.0.20.0/24
  workstations – not everyday accounts. Members do not have to be Domain Admins.
- Do not set the `ManagedBy` attribute (a group manager could change its members themselves). Only Tier 0 manages the
  members.
- The groups are in the Tier 0 OU because whoever controls these groups can silence alerts about Tier 0. Watchdog itself
  reports every change of their membership as a privileged access change.
- The service determines roles itself, from AD (it checks membership by SID, with a 5-minute cache) – adding or removing
  a member takes effect within 5 minutes, without logging out. To revoke access immediately, use the `DeniedSids` list in
  the startup settings (5.2).

**Verification:**

```powershell
Get-ADGroup AdMng-Watchdog-Admins -Properties ManagedBy, member | Format-List DistinguishedName, GroupScope, ManagedBy, member
```

Expected: the DN in `OU=Groups,OU=Tier 0,…`, `GroupScope : Global`, `ManagedBy` empty, the `adm-jan` account in `member`.
Note both SIDs.

### 2.4 DNS A (and PTR) record for the Watchdog server

**Where:** PAW (DnsServer module). **What and why:** the Debian server is not in the domain, so it will not register
itself in DNS. Kerberos builds the service name (SPN) from the **canonical name** of the host – so it must be an **A**
record, never a CNAME. A PTR record is recommended (diagnostics, DC logs). A record added with the command is static –
it will not disappear during scavenging of stale records. First check whether the `admng-wd` record already exists
(e.g. from version 0.2.0):

```powershell
Get-DnsServerResourceRecord -ComputerName DC01.contoso.com -ZoneName 'contoso.com' -Name 'admng-wd' -ErrorAction SilentlyContinue
```

- **No result** – add the records:

  ```powershell
  # reverse zone – only if it does not exist yet (in the test domain it was created in 1.2)
  if (-not (Get-DnsServerZone -ComputerName DC01.contoso.com -Name '1.0.10.in-addr.arpa' -ErrorAction SilentlyContinue)) {
      Add-DnsServerPrimaryZone -ComputerName DC01.contoso.com -NetworkId '10.0.1.0/24' -ReplicationScope Domain
  }
  Add-DnsServerResourceRecordA -ComputerName DC01.contoso.com -ZoneName 'contoso.com' -Name 'admng-wd' -IPv4Address '10.0.1.25' -CreatePtr
  ```

- **The A record already points to the Debian server's address** (e.g. when repeating this step) – add nothing; if the
  verification below shows the PTR record missing, add it:
  `Add-DnsServerResourceRecordPtr -ComputerName DC01.contoso.com -ZoneName '1.0.10.in-addr.arpa' -Name '25' -PtrDomainName 'admng-wd.contoso.com'`.
- **The record points to another server or is a CNAME alias** (usually the old Watchdog 0.2.0 server – in its
  instructions at the same address 10.0.1.25) – do **not** add a second A record: clients and controllers would get both
  addresses alternately and half of the connections would hit the old server. When migrating, you change the record only
  at cutover (box in 0.4) – then remove the old record and its PTR and add the new ones:

  ```powershell
  Remove-DnsServerResourceRecord -ComputerName DC01.contoso.com -ZoneName 'contoso.com' -Name 'admng-wd' -RRType A -RecordData '<stary adres>' -Force
  Remove-DnsServerResourceRecord -ComputerName DC01.contoso.com -ZoneName '<strefa odwrotna starego adresu>' -Name '<ostatni oktet>' -RRType Ptr -Force
  Add-DnsServerResourceRecordA -ComputerName DC01.contoso.com -ZoneName 'contoso.com' -Name 'admng-wd' -IPv4Address '<adres Debiana>' -CreatePtr
  ```

  You remove a CNAME record the same way, with `-RRType CName` instead of `-RRType A` (without `-RecordData`).

**Verification:**

```powershell
Resolve-DnsName admng-wd.contoso.com -Type A -Server DC01.contoso.com
Resolve-DnsName 10.0.1.25 -Server DC01.contoso.com
```

Expected: **exactly one** row `admng-wd.contoso.com  A  … 10.0.1.25` (type **A**, no CNAME row) and
`25.1.0.10.in-addr.arpa  PTR  admng-wd.contoso.com`. Repeat with `-Server DC02.contoso.com` after replication.

In production also check that the administrators' workstations have PTR records (`Resolve-DnsName <workstation address>`
returns its name). With Kerberos logons the controller does not record the workstation name – Watchdog takes it from the
PTR record; without it, it will show only the IP address as the source.

### 2.5 The `ADMNG-WD$` service account

> **In short.** What it changes: a new computer object in the Tier 0 OU – disabled at first – with a deny entry for SELF;
> only then is the account enabled, and after 3.6 a GPO with a logon denial is created. Where: PAW (Domain Admin), with
> 3.6 on Debian between these parts. How long: about 15 minutes without the GPO. Result: the `ADMNG-WD$` account with
> three SPNs, AES only, without write permission on its own object. Certificate enrollment for this account is not
> blocked (accepted risk – below, "Certificates and the service account"). Rollback: 2.13, rows 4 and 5.

#### What it is and why it is done this way

The service needs a Kerberos identity in the domain: for LDAP, for SYSVOL, to accept tickets from AD Manager
(`HTTP/admng-wd.contoso.com`) and from the controllers (WEF). We use a **computer object** with a keytab:

- a gMSA account is out – a server outside the domain cannot retrieve the gMSA password without a second, separate
  identity;
- a user account with an SPN is subject to password expiration rules and is a typical "kerberoasting" target; a computer
  account with a random 120-character password is resistant to it;
- a computer account and a keytab is the standard pattern for Kerberos services on Linux.

The account is created by a **Tier 0 administrator on the PAW** (not a tool on Debian – the server needs no permissions
to create objects in AD) with a **one-time password**. On Debian (3.6) this password is immediately replaced with a
random one that nobody knows, and the keytab is created. The `msktutil` and `adcli` tools are not used (they write to AD
over LDAP and create temporary files); do not install them on the server.

#### Creating it

**Where:** PAW, Domain Admin. **What and why:** a computer object in the Tier 0 OU with three SPNs, AES only (0x18), with
the "account is sensitive and cannot be delegated" flag and a one-time password. The account is created **disabled**: the
one-time password starts working only once the account is enabled, and that happens after the deny entry for SELF
(below). Whoever learns this password therefore cannot change the account's own object with it. Before running it, agree
with the Debian administrator that they will perform step 3.6 right after the account is enabled. Do not close the
PowerShell window until "Enabling the account" – the `$plain` variable contains the one-time password. When migrating
from version 0.2.0, omit the `HTTP/…` SPN from the command (box in 0.4) – otherwise the controller will reject creating
the account, because that SPN still belongs to the gMSA.

```powershell
$ou = 'OU=Service Accounts,OU=Tier 0,OU=Admin,DC=contoso,DC=com'
# one-time password: 30 random bytes written as Base64 (40 characters); works in PowerShell 5.1 and 7
$bytes = New-Object byte[] 30
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create(); $rng.GetBytes($bytes); $rng.Dispose()
$plain = [Convert]::ToBase64String($bytes)
$otp   = ConvertTo-SecureString $plain -AsPlainText -Force
New-ADComputer -Name 'ADMNG-WD' -SamAccountName 'ADMNG-WD$' -Path $ou `
    -DNSHostName 'admng-wd.contoso.com' `
    -ServicePrincipalNames 'HTTP/admng-wd.contoso.com', 'HOST/admng-wd.contoso.com', 'HOST/ADMNG-WD' `
    -KerberosEncryptionType AES128, AES256 `
    -AccountNotDelegated $true -PasswordNotRequired $false `
    -AccountPassword $otp -Enabled $false `
    -Description 'AdMng Watchdog (Debian admng-wd, 10.0.1.25) - konto usługi Tier 0, tylko odczyt'
(Get-ADComputer ADMNG-WD).Enabled      # False – the account is waiting for the deny entry for SELF
```

SPNs – what each is for:

| SPN | Who requests it |
|---|---|
| `HTTP/admng-wd.contoso.com` | AD Manager (API 8443) and the WinRM client on Windows Server 2022 (WEF) |
| `HOST/admng-wd.contoso.com` | the WinRM client on Windows Server 2025 – according to the OpenWEC documentation, from this version it requests `HOST/` by default; OpenWEC accepts tickets for both names (the same account key, 4.6) |
| `HOST/ADMNG-WD` | the short host name – the customary SPN of a computer account |

In the keytab (3.6) the name `HOST/admng-wd.contoso.com` takes the form `host/admng-wd.contoso.com` – lowercase. AD is
case-insensitive in SPNs, MIT Kerberos is not; it is the same name, so `setspn -L` and `klist -k` show it differently.

`New-ADComputer` writes only the SPNs you give it. If `setspn -L` additionally shows `RestrictedKrbHost/…`, they come
from another tool; they do no harm, but note it during the pilot.

#### Write block on its own object

**Where:** PAW, Domain Admin, right after creating the account (the account is still disabled). **What and why:** by
default every computer account can itself create and delete child objects under its own object, add SPNs and DNS names to
itself, and change several of its own attributes (e.g. re-enable RC4 in `msDS-SupportedEncryptionTypes`). The service
needs none of this, so we take it away with a "Deny" entry for SELF. Changing the password with the Kerberos protocol
uses a separate "Change Password" right, which this entry does not cover – step 3.6 checks this right away.

```powershell
$dn   = (Get-ADComputer ADMNG-WD).DistinguishedName
$acl  = Get-Acl -Path "AD:\$dn"
$self = [System.Security.Principal.SecurityIdentifier]'S-1-5-10'      # SELF
$acl.AddAccessRule((New-Object System.DirectoryServices.ActiveDirectoryAccessRule($self, 'CreateChild, DeleteChild, Self, WriteProperty', 'Deny')))
Set-Acl -Path "AD:\$dn" -AclObject $acl
```

**Verification:**

```powershell
(Get-Acl -Path "AD:\$dn").Access | Where-Object AccessControlType -eq 'Deny' | Format-Table IdentityReference, ActiveDirectoryRights
```

Expected: the row `NT AUTHORITY\SELF  CreateChild, DeleteChild, Self, WriteProperty`. This entry does not affect
administrators (SELF means only the `ADMNG-WD$` account itself). The service checks every hour that the entry still
exists (5.4).

#### Enabling the account and handing over the one-time password

**Where:** PAW, the same PowerShell window as "Creating it". **When:** after the deny entry for SELF; the Debian
administrator is ready for 3.6.

```powershell
Enable-ADAccount -Identity 'ADMNG-WD$'
$plain    # hand ONCE to the Debian administrator (3.6), then close the PowerShell window
```

Hand over the one-time password through a channel that you both trust (verbally, through a password manager with the entry
deleted afterward) – not by email or a messenger. It is valid only until 3.6 is performed.

**Verification:**

```powershell
Get-ADComputer ADMNG-WD -Properties Enabled, AccountNotDelegated, KerberosEncryptionType, msDS-SupportedEncryptionTypes,
    userAccountControl, servicePrincipalName, mS-DS-CreatorSID, memberOf, PrimaryGroupID, msDS-KeyVersionNumber |
    Format-List Name, DistinguishedName, Enabled, AccountNotDelegated, KerberosEncryptionType, msDS-SupportedEncryptionTypes,
        userAccountControl, servicePrincipalName, mS-DS-CreatorSID, memberOf, PrimaryGroupID, msDS-KeyVersionNumber
setspn -L ADMNG-WD
setspn -Q HTTP/admng-wd.contoso.com
setspn -X
```

Expected:

- `Enabled : True` (if `Enable-ADAccount` reported a password policy error, the one-time password did not meet the domain
  policy – delete the account and create it again),
- `AccountNotDelegated : True`, `KerberosEncryptionType : {AES128, AES256}`, `msDS-SupportedEncryptionTypes : 24`,
- `userAccountControl : 1052672` (0x101000 = workstation account + "cannot be delegated"); if you see
  `1052704` (additionally 0x20 – "password not required"), run
  `Set-ADAccountControl -Identity (Get-ADComputer ADMNG-WD) -PasswordNotRequired $false` and check again,
- the three SPNs from the table, `mS-DS-CreatorSID` empty (the account was created by an administrator, not by the
  `MachineAccountQuota` limit), `memberOf` empty, `PrimaryGroupID : 515` (Domain Computers),
- `setspn -Q` finds `HTTP/admng-wd.contoso.com` **only** on `CN=ADMNG-WD,…`; `setspn -X` – `found 0 group of
  duplicate SPNs`. When migrating from version 0.2.0, the `HTTP/…` SPN still belongs to the gMSA until cutover (box in
  0.4).

#### Certificates and the service account (accepted risk)

The `ADMNG-WD$` account belongs to Domain Computers, and that group by default has the Enroll right on the "Computer"
(`Machine`) template, published on every Enterprise CA – and may have it on other templates. Whoever steals the keytab
(or learns the one-time password in those few minutes before 3.6) can therefore request a computer certificate for
`admng-wd.contoso.com` and log on with it as `ADMNG-WD$` (PKINIT) for the entire validity period of the certificate –
usually a year. Changing the password every 30 days does not invalidate this; only disabling the account or revoking the
certificate stops it – and only once the controllers fetch a new revocation list (below, "When the keytab may have
leaked").

**We do not add deny entries for this account on certificate templates** – this risk was consciously accepted
(ADR-0009). Watchdog does not read the certification authority database, so it will not report the issuance of a
certificate by itself. Two steps remain below: checking right after 3.6 that no certificate has been issued to the
account, and – when the keytab may have leaked – revoking the certificates issued to the account. The service itself
needs no certificate from the CA (the API certificate from 3.8 is requested by a Tier 0 administrator).

#### Certificates issued to the account (when the forest has an Enterprise CA)

**When:** right after 3.6 – and always when the keytab may have leaked (below). **Where:** PAW with the CA tools
(`certutil`). The query goes to **every** Enterprise CA in the forest:

```powershell
$cfg = (Get-ADRootDSE).configurationNamingContext
$cas = Get-ADObject -SearchBase "CN=Enrollment Services,CN=Public Key Services,CN=Services,$cfg" `
    -LDAPFilter '(objectClass=pKIEnrollmentService)' -Properties dNSHostName | ForEach-Object { "$($_.dNSHostName)\$($_.Name)" }
foreach ($ca in $cas) {
    "== $ca"
    certutil -config $ca -view -restrict 'RequesterName=CONTOSO\ADMNG-WD$,Disposition=20' -out 'RequestID,SerialNumber,NotAfter'
}
```

`Disposition=20` – issued certificates. Expected after 3.6: for no CA is there a row with a serial number
(`Serial Number:`).

**When the keytab may have leaked** – the order matters:

1. Disable the account: `Disable-ADAccount 'ADMNG-WD$'`. A disabled account will not log on either with a password or
   with a certificate; Watchdog and OpenWEC stop working until step 4.
2. Revoke the certificates from the block above (for each CA and serial number) and immediately publish new revocation
   lists (CRLs) – otherwise the CA will publish them only on its own schedule:

   ```powershell
   certutil -config '<urząd z listy>' -revoke <SerialNumber> 1      # 1 = key compromise
   certutil -config '<urząd z listy>' -crl                          # new CRLs right away
   ```

3. Leave the account disabled for as long as the controllers may use the old CRL. The controller (KDC) keeps the
   downloaded list until its expiry (`Next Update`) and until then does not see the revocation – so revocation alone does
   not immediately block certificate logon. The validity period is shown by the CA console (Certification Authority →
   Revoked Certificates → Properties: "CRL publication interval" and "Publish Delta CRLs"): with default settings it is
   about a day (delta list) or – when the CA does not publish delta lists – about a week. If there was no certificate in
   step 2, you do not wait. Instead of waiting you can delete the account and create it anew (8.2, then 2.5–2.7 and the
   GPO with the logon denial): the new account has a new SID, and the old certificates contain the old one – the
   controllers will reject them (strong certificate mapping, enforced by default since the 2025 updates).
4. Set a new one-time password while the account is still disabled (3.7, point 1 – the old keytab will not work for even
   a moment longer), then enable the account (`Enable-ADAccount 'ADMNG-WD$'`) and immediately perform 3.7, point 2. A
   disabled account will not get a ticket to change the password (`Client's credentials have been revoked` – verified in
   the lab), so first enable, then Debian.

#### What this account can and cannot do

| Can | Cannot |
|---|---|
| authenticate in the domain with Kerberos (AES only) | log on interactively, via RDP, as a batch job or as a service (GPO) |
| read the directory (like any authenticated principal + the explicit read permissions from 2.6) | write anything in other AD objects beyond what any authenticated account can (row below); replicate the directory (DCSync) |
| read SYSVOL (GPO) | read passwords, LAPS, BitLocker keys, gMSA passwords, confidential attributes |
| change its own password (Kerberos protocol, port 464) | be delegated (services cannot act on its behalf) |
| accept tickets for `HTTP/` and `HOST/admng-wd.contoso.com` | change its own `userAccountControl`, and thanks to the deny entry – also its other own attributes, SPNs and child objects |
| as long as `ms-DS-MachineAccountQuota` > 0 – create up to 10 computer accounts, like any account in the domain (2.8 sets 0) | |
| like any authenticated account – add a new record in an AD-integrated DNS zone (default Authenticated Users right) | |
| like any domain computer – obtain a certificate from an Enterprise CA using a template that allows it (by default "Computer") – accepted risk, above | |

Adding DNS records gives the account nothing beyond what every logged-on domain user has, and Watchdog does not track the
DNS partition. If you want to take this right away from this account too, add a deny entry on every AD-integrated zone
(also on reverse zones and on `_msdcs.contoso.com` in `ForestDnsZones`), e.g.:
`dsacls "DC=contoso.com,CN=MicrosoftDNS,DC=DomainDnsZones,DC=contoso,DC=com" /D "CONTOSO\ADMNG-WD$:CC"`.

Whoever has the keytab has this identity – and can also forge a ticket to the Watchdog API (0.3, point 2).

Regardless of the deny entry for SELF, Watchdog reports **every** change of the `ADMNG-WD$` account as critical
(the `watchdog-self` rule) – except for its own scheduled password change.

#### How the password change works

- The password is 120 random characters, exists only in memory at the moment of the change and is never stored anywhere –
  only the keytab with Kerberos keys derived from the password lies on disk.
- The daily timer on Debian (`admng-watchdog-keytab.timer`, 3.7) checks the password age; when it is over 30 days old, it
  changes it with the Kerberos protocol (port 464 on the PDC emulator). No AD permissions or LDAP writes are needed for
  this.
- The keytab contains the keys of the **current and previous** version (kvno) – tickets issued before the change still
  work, and DC02 may for a while, until replication, use the old key. (The keytab from the emergency procedure in 3.6
  keeps all previous versions.)
- A failed change breaks nothing: the password and keytab stay, and the timer tries again the next day. The service warns
  when the password is over 32 days old and reports an error after 35 days.
- Domain controllers do not enforce password expiration for computer accounts – 30 days is our policy, not a domain
  limit.
- Changing the password invalidates the old keys, but not certificates issued to the account – we do not block this risk
  (above, "Certificates and the service account"); after a keytab leak you revoke certificates according to "When the
  keytab may have leaked".

#### Logon restriction for the account (GPO) – after 3.6

**When:** only after 3.6 – the one-time password should live as briefly as possible. **Where:** PAW, the GPMC console.
**What and why:** the service account never logs on interactively. A GPO with denial of local, RDP, batch and service
logon means that someone who obtained the keytab could not use this identity to log on to servers and workstations.
Network logon is **not** blocked – the service needs it (LDAP, SMB).

User rights from several GPOs **do not merge**: on a given computer the list from one GPO applies – the one with the
highest precedence among the GPOs that define that right (a GPO linked to an OU wins over a GPO linked to the domain).
The list from the GPO also replaces the computer's local list entirely. Therefore:

1. The new GPO linked to the domain defines **all four** rights – this covers computers on which no other GPO defines
   these rights.
2. To **each** existing GPO that defines any of these rights (e.g. a tiering-model GPO linked to the servers OU), add
   `CONTOSO\ADMNG-WD$` in the rights that GPO defines – on computers in its scope it wins. You find such GPOs like this:

```powershell
Get-GPO -All | ForEach-Object {
    $x = Get-GPOReport -Guid $_.Id -ReportType Xml
    if ($x -match 'SeDeny(Interactive|RemoteInteractive|Batch|Service)LogonRight') { $_.DisplayName }
}
```

The new GPO:

```powershell
$gpo = New-GPO -Name 'AdMng - ograniczenia konta Watchdog' -Comment 'Odmowa logowania lokalnego, RDP, wsadowego i jako usługa dla CONTOSO\ADMNG-WD$'
New-GPLink -Guid $gpo.Id -Target 'DC=contoso,DC=com' -LinkEnabled Yes
```

In GPMC edit the GPO: Computer Configuration → Policies → Windows Settings → Security Settings → Local Policies → User
Rights Assignment and in the four settings – "Deny log on locally", "Deny log on through Remote Desktop Services", "Deny
log on as a batch job", "Deny log on as a service" – check "Define these policy settings" and add `CONTOSO\ADMNG-WD$`. In
the object picker click "Object Types" and check "Computers" – otherwise the computer account will not be found. In
"Deny log on locally" also add the built-in group `Guests` (in the Polish Windows: Goście) – the default local list
contains the Guest account, and the GPO replaces it. Do **not** add the account to "Deny access to this computer from the
network".

**Verification** (after `gpupdate /force`) on ADM-WS01 and on one computer in each OU to which the GPO found above is
linked:

```powershell
$sid = (Get-ADComputer ADMNG-WD).SID.Value
secedit /export /areas USER_RIGHTS /cfg "$env:TEMP\ur.inf" | Out-Null
Select-String -Path "$env:TEMP\ur.inf" -Pattern 'SeDeny(Interactive|RemoteInteractive|Batch|Service)LogonRight' |
    ForEach-Object { '{0}: {1}' -f $_.Line.Split('=')[0].Trim(), ($_.Line -match [regex]::Escape($sid)) }
Remove-Item "$env:TEMP\ur.inf"
```

Expected: four rows ending in `True`.

### 2.6 Read permissions for the service account

**Where:** PAW, Domain Admin. **What and why:** in many domains every account reads objects thanks to the "Pre-Windows
2000 Compatible Access" group. In hardened domains this is not so, and objects and attributes that the account cannot
read (e.g. the `uSNChanged` and `lockoutTime` attributes) **silently disappear** from tracking. That is why we grant
read permissions explicitly – always, also in the test domain. These are the same commands as in the Windows version
([`WATCHDOG.md`](WATCHDOG.md) 4.3), only for the `ADMNG-WD$` account.

```powershell
$d = 'DC=contoso,DC=com'; $a = 'CONTOSO\ADMNG-WD$'
$classes = 'user', 'inetOrgPerson', 'computer', 'group', 'organizationalUnit', 'contact',
           'msDS-ManagedServiceAccount', 'msDS-GroupManagedServiceAccount', 'msDS-DelegatedManagedServiceAccount'
$schema = (Get-ADRootDSE).schemaNamingContext
foreach ($c in $classes) {
    # the dMSA class exists only in the Windows Server 2025 schema – missing classes are skipped
    if (Get-ADObject -SearchBase $schema -LDAPFilter "(lDAPDisplayName=$c)") { dsacls $d /I:S /G "${a}:GR;;$c" }
}
dsacls $d /G "${a}:GR"                                                    # domain object: password and lockout policies, FSMO, MachineAccountQuota
dsacls "CN=AdminSDHolder,CN=System,$d" /G "${a}:GR"                       # protected accounts (adminCount=1) inherit their ACL from here (SDProp, up to 60 min)
dsacls "CN=Password Settings Container,CN=System,$d" /I:T /G "${a}:GR"    # fine-grained password policies (PSO) – the correct lockout time
```

Why separately for each class: inherited entries "for objects of class X" apply to the object's structural class, so
`inetOrgPerson` and dMSA accounts are not covered by the entry for `user`.

**Deleted objects** (recommended – without this, deletions are detected only during the daily reconciliation as "object
disappeared"):

```powershell
dsacls "CN=Deleted Objects,$d" /takeownership      # changes the owner of a system container – agree with the AD team
dsacls "CN=Deleted Objects,$d" /G "${a}:LCRP"
```

**`tokenGroups` (roles in the API).** The service determines operators' roles by reading the `tokenGroups` attribute of
their accounts. The right to it is given by the `GR` entry for the classes `user`, `inetOrgPerson` and `computer` from
this step – also in a hardened domain. Do not add the account to the Windows Authorization Access Group: that group
gives read access to a different attribute (`tokenGroupsGlobalAndUniversal`), which the service does not use.
`ADMNG-WD$` belongs to no group other than Domain Computers.

Write permissions in AD are **not** needed. After start and once a day the service checks whether it sees everything it
needs (the Status tab → "Permission check").

**Verification:**

```powershell
dsacls 'DC=contoso,DC=com' | Select-String 'ADMNG-WD'
dsacls 'CN=AdminSDHolder,CN=System,DC=contoso,DC=com' | Select-String 'ADMNG-WD'
```

Expected: several `Allow CONTOSO\ADMNG-WD$` rows – on the domain object also with the annotation `<Inherited to user>`,
`<Inherited to computer>` etc.; on AdminSDHolder at least one.

**Verification from Debian** (root; the service account and keytab from 3.6 already exist – the order from 0.4). Check the
`tokenGroups` read on an ordinary account (without `adminCount=1`), e.g. a future member of `AdMng-Watchdog-Viewers`:
protected accounts (e.g. Domain Admins) get their permissions from AdminSDHolder, where every authenticated principal can
read all attributes, so the test on them always passes. The row with `Deleted Objects` applies only if you granted
permissions to deleted objects.

```bash
export KRB5_CONFIG=/etc/admng-watchdog/krb5.conf KRB5CCNAME=FILE:/dev/shm/admng-test-cc
export KRB5_CLIENT_KTNAME=FILE:/etc/admng-watchdog/admng-wd.keytab LDAPCONF=/etc/admng-watchdog/ldap.conf
kdestroy 2>/dev/null
ldapsearch -Q -LLL -Y GSSAPI -H ldap://DC01.contoso.com -s base \
    -b 'CN=Ewa Helpdesk,OU=Użytkownicy,OU=Firma,DC=contoso,DC=com' tokenGroups | head -3
ldapsearch -Q -LLL -Y GSSAPI -H ldap://DC01.contoso.com -s base -E '!1.2.840.113556.1.4.417' \
    -b 'CN=Deleted Objects,DC=contoso,DC=com' dn
ldapsearch -Q -LLL -Y GSSAPI -H ldap://DC01.contoso.com -s base \
    -b 'CN=Password Settings Container,CN=System,DC=contoso,DC=com' dn
kdestroy; unset KRB5_CLIENT_KTNAME LDAPCONF
```

Expected: for `Ewa Helpdesk` rows `tokenGroups:: …`, in the other two queries a row `dn: …`. A lack of these rows (an
empty result or `No such object (32)`) means that the permissions from this step are missing – check the `dsacls`
commands and repeat the test after a few minutes. In production you find the DN of an ordinary account with the command
`ldapsearch -Q -LLL -Y GSSAPI -H ldap://DC01.contoso.com -b 'DC=contoso,DC=com' '(sAMAccountName=<account>)' dn`
(with the same `export` lines).

### 2.7 Reading SYSVOL (GPO content)

**Where:** PAW and DC01. **What and why:** Watchdog reads GPO files from the `SYSVOL` share of its domain controller
(read only, SMB 3 with Kerberos and signing) to show "was → is" changes of settings. Usually nothing needs to be
granted: Authenticated Users and Domain Computers can read SYSVOL and the GPO folders, and `ADMNG-WD$` belongs to both.
The problem is GPOs from which someone removed read access for these groups (security filtering) – their folder is then
invisible to the service.

**Check** (PAW, GroupPolicy module) – GPOs without read access for Authenticated Users and Domain Computers:

```powershell
$dSid = (Get-ADDomain).DomainSID.Value
Get-GPO -All | Where-Object {
    -not (Get-GPPermission -Guid $_.Id -All | Where-Object {
        $_.Trustee.Sid.Value -in @('S-1-5-11', "$dSid-515") -and $_.Permission -in 'GpoRead', 'GpoApply' })
} | Select-Object DisplayName, Id
```

Expected: no results. Add read access for Domain Computers to the listed GPOs – we take the group name by SID, because in
a domain created on a Polish Windows Server the group is called "Komputery domeny". Adding read access alone does not
change who the GPO applies to:

```powershell
$dc = (Get-ADGroup "$((Get-ADDomain).DomainSID)-515").Name
Set-GPPermission -Guid <Id> -TargetName $dc -TargetType Group -PermissionLevel GpoRead
```

On DC01: `Get-SmbShareAccess -Name SYSVOL` – the share must grant read to the Everyone group or Authenticated Users (the
default state). On Debian, repeat Test 3 from 3.6 (SYSVOL read; the test block contains the necessary `export`
commands).

### 2.8 `ms-DS-MachineAccountQuota` = 0

**Where:** PAW. **What and why:** by default every domain account – a user, but also a computer, so `ADMNG-WD$` as well –
can create up to 10 computer accounts in the domain (join computers), and such accounts are later sometimes used in
attacks (e.g. resource-based delegation). The value 0 is a **deployment condition** of Watchdog in production: without
it, someone who obtains the keytab could create computer accounts. Computers are then joined only by people with an
explicitly delegated right to create computer objects, or by people for whom an administrator prepared the computer
account (below). If you cannot set 0 right away, record it as an accepted risk.

The Status tab warns as long as the value is greater than 0, and Watchdog reports an **increase** of this value from 0
as a critical event (rule `tier0-machine-quota`).

First check the current state and who uses the quota today:

```powershell
$dn = (Get-ADDomain).DistinguishedName
(Get-ADObject $dn -Properties 'ms-DS-MachineAccountQuota').'ms-DS-MachineAccountQuota'      # note it down – this is the value to roll back to
Get-ADComputer -LDAPFilter '(mS-DS-CreatorSID=*)' -Properties mS-DS-CreatorSID, whenCreated |
    Select-Object Name, whenCreated, @{ n = 'Utworzył'; e = {
        $s = $_.'mS-DS-CreatorSID'      # in the catch block $_ is already the error, not the computer – hence the variable
        try { ([System.Security.Principal.SecurityIdentifier]$s).Translate([System.Security.Principal.NTAccount]).Value }
        catch { "$s" } } } | Sort-Object whenCreated
```

The computers on the list were joined by ordinary users. If this has happened in recent months (e.g. a service desk
joins laptops with its own account), first give these people – preferably a group – the right to create computer
objects, and only then set 0. First check whether the group already has this right (if so, do not grant it – and do not
roll it back later). Only entries that apply to the object itself count (also those inherited from a parent OU) – the
"Full Control – Descendant Computer objects" entry from the delegation wizard applies only to existing computers and
does **not** allow creating them:

```powershell
(Get-ADDomain).ComputersContainer        # computers joined without specifying an OU end up here
$sid      = (Get-ADGroup '<grupa>').SID
$computer = [guid]'bf967a86-0de6-11d0-a285-00aa003049e2'               # the computer class
foreach ($dn in 'OU=Komputery,OU=Firma,DC=contoso,DC=com', 'CN=Computers,DC=contoso,DC=com') {
    $had = (Get-Acl -Path "AD:\$dn").GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]) | Where-Object {
        $_.IdentityReference -eq $sid -and $_.AccessControlType -eq 'Allow' -and
        ([int]$_.ActiveDirectoryRights -band 1) -and $_.ObjectType -in [guid]::Empty, $computer -and   # 1 = CreateChild
        $_.InheritanceType -in 'None', 'All', 'SelfAndChildren' -and $_.InheritedObjectType -eq [guid]::Empty }   # applies to this object
    '{0}: grupa już może tworzyć komputery: {1}' -f $dn, [bool]$had
}
dsacls 'OU=Komputery,OU=Firma,DC=contoso,DC=com' /I:T /G 'CONTOSO\<grupa>:CC;computer'
```

- Joining without specifying an OU (`Add-Computer` without `-OUPath`, the "Computer Name Changes" window) creates the
  object in the container from the first command – usually `CN=Computers,DC=contoso,DC=com`. Either redirect that
  location to the computers OU (on a DC: `redircmp "OU=Komputery,OU=Firma,DC=contoso,DC=com"`), or grant the same right
  there as well:
  `dsacls 'CN=Computers,DC=contoso,DC=com' /G 'CONTOSO\<grupa>:CC;computer'`.
- The same right is granted by the "Delegate Control" wizard in the "Active Directory Users and Computers" console
  (custom task → "Computer objects" → "Create selected objects in this folder").
- **Computer accounts prepared by an administrator.** When you create the computer object in advance (ADUC: New →
  Computer), specify in the "The following user or group can join this computer to a domain" field the person or group
  that will do the joining – they then get the rights needed for joining (password reset, writing the DNS name and SPN,
  account restrictions). Since update KB5020276, joining to an existing computer account is allowed only for its
  creator, when the account was created by a member of Domain Admins, or when the account owner is on the list of the
  policy "Domain controller: Allow computer account re-use during domain join" – an account prepared by a domain
  administrator meets this condition.

Finally, set 0:

```powershell
Set-ADDomain -Identity contoso.com -Replace @{ 'ms-DS-MachineAccountQuota' = 0 }
```

**Check:** the read command from the beginning of this step returns `0`; the group right check (the block above, without
the last `dsacls` line) shows `True` on every object where you granted the right. Result: joining a computer by an
ordinary user without delegation ends with access denied. **Rollback:** `Set-ADDomain` with the previous value (default
`10`). Remove the delegation **only where you granted it** (the check above showed `False`), and only that one entry –
**not** `dsacls … /R`, which removes all of the group's entries on the object, e.g. reading LAPS passwords, BitLocker
keys or resetting computer passwords:

```powershell
$sid      = (Get-ADGroup '<grupa>').SID
$computer = [guid]'bf967a86-0de6-11d0-a285-00aa003049e2'
# OU – the entry with /I:T (this object and child objects); CN=Computers – the entry without /I (this object only)
foreach ($x in @{ dn = 'OU=Komputery,OU=Firma,DC=contoso,DC=com'; inh = 'All' }, @{ dn = 'CN=Computers,DC=contoso,DC=com'; inh = 'None' }) {
    $acl = Get-Acl -Path "AD:\$($x.dn)"
    $acl.RemoveAccessRuleSpecific((New-Object System.DirectoryServices.ActiveDirectoryAccessRule($sid, 'CreateChild', 'Allow', $computer, $x.inh)))
    Set-Acl -Path "AD:\$($x.dn)" -AclObject $acl
}
```

Skip in the loop the object on which you did not grant the right. Then repeat the group right check (the block above,
without the `dsacls` line): on the objects from which you removed the entry it should be `False` again.
`RemoveAccessRuleSpecific` removes exactly the entry granted by `dsacls`; plain `RemoveAccessRule` refuses to remove it
without an error message when the group also has a "Full Control – Descendant Computer objects" entry on the OU. Remove
a right granted by the "Delegate Control" wizard in the console (Properties → Security → Advanced) – only this group's
"Create Computer objects" entry.

### 2.9 BadSuccessor (dMSA) exposure and patch level

**Where:** PAW and DC01. **What and why:** dMSA accounts (Windows Server 2025) can "supersede" another account through
the `msDS-ManagedAccountPrecededByLink` attribute. Akamai researchers showed (the "BadSuccessor" attack) that anyone who
can **create a dMSA** in any OU could in this way obtain the privileges of any account, including Domain Admins.
Microsoft changed the KDC behavior in the August 2025 update (CVE-2025-53779) – but the right to create dMSAs in the
hands of non-Tier 0 people remains a risk. A domain is exposed when it has at least one Windows Server 2025 controller
(in our case DC01).

Watchdog reports every dMSA creation and every change of the migration link as critical (rule
`tier0-badsuccessor`).

**Where a dMSA can be created and who can create it.** The script only reads. First it determines from the schema under
objects of which classes a dMSA can be created – the possible parents are inherited from the superclasses (dMSA →
`computer` → `user` → `organizationalPerson` → `person` → `top`), so the dMSA class definition alone does not show them.
Then it reads the ACLs of all objects of these classes in the domain and shows non-Tier 0 principals with the
`CreateChild` right (for all classes or for the dMSA class), `GenericAll`, `WriteDacl`, `WriteOwner`, and non-Tier 0
owners. Save it as `Test-AdMngDmsaCreators.ps1` and run `.\Test-AdMngDmsaCreators.ps1` (your own Tier 0 groups – as in
2.2, with the `-AdditionalTier0Sids` parameter):

```powershell
# Test-AdMngDmsaCreators.ps1 – who can create dMSAs (read only)
param([string[]] $AdditionalTier0Sids = @())
Import-Module ActiveDirectory
$dSid    = (Get-ADDomain).DomainSID.Value
$rootSid = (Get-ADDomain -Identity (Get-ADForest).RootDomain).DomainSID.Value
$tier0 = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-9', "$dSid-512", "$dSid-516", "$dSid-526",
           "$rootSid-498", "$rootSid-518", "$rootSid-519", "$rootSid-527") + $AdditionalTier0Sids
# DnsAdmins is in practice Tier 0 (members can run code on a DC); it has full control over CN=MicrosoftDNS,CN=System
$tier0 += @(Get-ADGroup -Filter "sAMAccountName -eq 'DnsAdmins'" | ForEach-Object { $_.SID.Value })
$SidType = [System.Security.Principal.SecurityIdentifier]
$dmsa = [guid]'0feb936f-47b3-49f2-9386-1dedc2c23765'      # the msDS-DelegatedManagedServiceAccount class
$mask = [int][System.DirectoryServices.ActiveDirectoryRights]'CreateChild, WriteDacl, WriteOwner'
function Get-SidName([string]$sid) {
    try { ([System.Security.Principal.SecurityIdentifier]$sid).Translate([System.Security.Principal.NTAccount]).Value } catch { $sid }
}
# classes of possible dMSA parents: possSuperiors and systemPossSuperiors of this class and all its superclasses
$schema = (Get-ADRootDSE).schemaNamingContext
$c = 'msDS-DelegatedManagedServiceAccount'
if (-not (Get-ADObject -SearchBase $schema -LDAPFilter "(lDAPDisplayName=$c)")) { $c = 'computer' }   # a schema without Windows Server 2025
$parents = @()
while ($c) {
    $o = Get-ADObject -SearchBase $schema -LDAPFilter "(lDAPDisplayName=$c)" -Properties subClassOf, systemPossSuperiors, possSuperiors
    $parents += @($o.systemPossSuperiors) + @($o.possSuperiors)
    $c = if ($o.subClassOf -ne $c) { $o.subClassOf } else { $null }      # the top class is its own superclass
}
$parents = @($parents | Where-Object { $_ } | Sort-Object -Unique)
Write-Host ('dMSA może powstać pod obiektami klas: ' + ($parents -join ', '))
$filter = '(|' + (($parents | ForEach-Object { "(objectClass=$_)" }) -join '') + ')'
Get-ADObject -LDAPFilter $filter -Properties nTSecurityDescriptor |
ForEach-Object {
    $o = $_; $sd = $o.nTSecurityDescriptor
    $owner = $sd.GetOwner($SidType).Value
    if ($tier0 -notcontains $owner) { [pscustomobject]@{ Kontener = $o.DistinguishedName; Kto = Get-SidName $owner; Jak = 'właściciel' } }
    foreach ($r in $sd.GetAccessRules($true, $true, $SidType)) {
        if ($r.AccessControlType -ne 'Allow' -or $tier0 -contains $r.IdentityReference.Value) { continue }
        if (([int]$r.PropagationFlags -band 2) -ne 0) { continue }                  # an entry only for child objects
        if (([int]$r.ActiveDirectoryRights -band $mask) -eq 0) { continue }
        if ($r.ObjectType -ne [guid]::Empty -and $r.ObjectType -ne $dmsa) { continue }
        [pscustomobject]@{ Kontener = $o.DistinguishedName; Kto = Get-SidName $r.IdentityReference.Value; Jak = $r.ActiveDirectoryRights.ToString() }
    }
} | Sort-Object Kto, Kontener | Format-Table -AutoSize -Wrap
```

How to read it: the first line shows the classes of possible dMSA parents – in the Windows Server 2025 schema
`builtinDomain, container, domainDNS, lostAndFound, organization, organizationalUnit` (without a 2025 controller the
script shows the same for the `computer` class). The script checks objects of all these classes – including the
easy-to-miss `CN=Builtin`. If another class appears in the line (a schema extension), the script takes it into account
on its own. Every following line is a place where the given principal can create a dMSA (or grant itself such a right).
A fresh test domain usually gives no lines. Typical findings in production: teams with full control over "their" OUs,
identity management tool accounts, OU owners that are personal accounts, and on the domain object itself – e.g.
`Exchange Windows Permissions` with `WriteDacl` (2.2). What to do: narrow `CreateChild` to the needed classes (user,
group, computer), remove `GenericAll`/`WriteDacl`/`WriteOwner`, change the owner to Domain Admins. Before you change
anything, save the object's SDDL to a file as in 2.2 – it is your rollback plan (the restore command is below the table
in 2.13). For comparison you can run the Akamai script `Get-BadSuccessorOUPermissions.ps1`
(<https://github.com/akamai/BadSuccessor>) – it checks OUs only.

**Domain controller patch level:**

```powershell
Invoke-Command -ComputerName DC01.contoso.com, DC02.contoso.com {
    Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' | Select-Object ProductName, CurrentBuild, UBR
}
Invoke-Command -ComputerName DC01.contoso.com { Get-HotFix | Sort-Object InstalledOn | Select-Object -Last 3 }
```

Expected: on DC01 (Windows Server 2025, build 26100) at least the August 2025 cumulative update is installed (KB5063878,
build 26100.4946) – in practice the current monthly update. Compare the numbers with the Windows Server 2025 update
history on the Microsoft website. DC02 (2022) has no dMSA, but it should also have current patches.

### 2.10 Audit policy on domain controllers and the Security log size

**Where:** PAW (GPMC), check on DC01 and DC02. **What and why:** without an audit policy the domain controllers do not
record the events from which Watchdog determines the actor and the source – the actor will be "undetermined"
(`AUDIT_POLICY_MISSING`).

Audit subcategory settings from several GPOs are merged separately for each subcategory; in a conflict the GPO with the
higher priority wins. That is why the new GPO gets the highest priority (`-Order 1`) and sets **every** subcategory from
the table to "Success and Failure". This is the highest possible value, so the GPO lowers nothing – neither the
controller's default settings (e.g. success for Credential Validation), nor the requirements of other tools (Defender
for Identity requires e.g. success and failure for Credential Validation). Success events that Watchdog does not need
(e.g. 4768, successful 4776) stay in the DC log – the WEF subscription (4.4) does not forward them. Some subcategories
have no failure events – there "failure" adds nothing. Instead of a new GPO you can set the same in your existing
domain controller audit GPO.

**First save the current state of each DC** – it is your rollback plan (from the PAW; the files go to the Documents
folder). Keep the **first** backup files (preferably also off the workstation) – a backup made after 2.10 would already
contain the new settings; the block below checks this itself:

```powershell
foreach ($dc in 'DC01.contoso.com', 'DC02.contoso.com') {
    $n = $dc.Split('.')[0]
    if (Test-Path "$env:USERPROFILE\Documents\admng-auditpol-before-$n.csv") { "Kopia dla $n już istnieje – nie tworzę nowej"; continue }
    $bytes = Invoke-Command -ComputerName $dc {
        auditpol /backup /file:C:\Windows\Temp\admng-auditpol-before.csv | Out-Null
        , [System.IO.File]::ReadAllBytes('C:\Windows\Temp\admng-auditpol-before.csv')     # the file unchanged (for auditpol /restore)
        Remove-Item C:\Windows\Temp\admng-auditpol-before.csv
    }
    [System.IO.File]::WriteAllBytes("$env:USERPROFILE\Documents\admng-auditpol-before-$n.csv", [byte[]]$bytes)
    Invoke-Command -ComputerName $dc { wevtutil gl security } | Select-String 'maxSize' |
        ForEach-Object { "$n $_" } | Tee-Object -FilePath "$env:USERPROFILE\Documents\admng-auditpol-before-maxsize.txt" -Append
}
```

The last command prints the current Security log size of each DC (`maxSize`, in bytes) – it will be needed below.

Create the GPO `AdMng - inspekcja DC` linked to the **Domain Controllers** OU (or supplement your existing audit GPO):

```powershell
$gpo = New-GPO -Name 'AdMng - inspekcja DC'
New-GPLink -Guid $gpo.Id -Target 'OU=Domain Controllers,DC=contoso,DC=com' -LinkEnabled Yes -Order 1
```

In GPMC edit the GPO: Computer Configuration → Policies → Windows Settings → Security Settings → Advanced Audit Policy
Configuration. In each subcategory from the table select "Configure the following audit events" and "Success" and
"Failure":

| Subcategory (English name) | GUID (for `auditpol`) | Setting | Why |
|---|---|---|---|
| User Account Management | `{0CCE9235-69AE-11D9-BED3-505054503030}` | S, F | 4720–4726, 4738, 4740, 4767, 4781, 4794 |
| Computer Account Management | `{0CCE9236-69AE-11D9-BED3-505054503030}` | S, F | 4741–4743 |
| Security Group Management | `{0CCE9237-69AE-11D9-BED3-505054503030}` | S, F | 4727–4737, 4754–4758, 4764 |
| Distribution Group Management | `{0CCE9238-69AE-11D9-BED3-505054503030}` | S, F | 4744–4763 |
| Directory Service Changes | `{0CCE923C-69AE-11D9-BED3-505054503030}` | S, F | 5136–5141 – who changed an attribute (requires the SACL from 2.11) |
| Logon | `{0CCE9215-69AE-11D9-BED3-505054503030}` | S, F | 4624 – computer and source address; 4625 – lockout source |
| Kerberos Authentication Service | `{0CCE9242-69AE-11D9-BED3-505054503030}` | S, F | 4771 – source of bad passwords (lockouts, "password spraying"); Watchdog uses failures only |
| Credential Validation | `{0CCE923F-69AE-11D9-BED3-505054503030}` | S, F | 4776 – source of bad NTLM passwords; Watchdog uses failures only |
| Audit Policy Change | `{0CCE922F-69AE-11D9-BED3-505054503030}` | S, F | 4719 – audit policy change |

In the same GPO: Security Settings → Local Policies → Security Options → "Audit: Force audit policy subcategory settings
(Windows Vista or later) to override audit policy category settings" = Enabled (usually already enabled).

**Security log size.** The controllers now also send logon events – 4624 (network logons – type 3 – and RDP – type 10),
4625, 4771 and failed 4776 – so the log fills up faster. The log must hold the events from the longest receiver
outage you accept – after an outage the controller sends the backlog of events from where it stopped. At the start the
log should be **at least 1 GB**, and in the pilot measure the actual growth (7.4). If any `maxSize` line saved above is
smaller than `1073741824`, set the size in this GPO – 1 GB or more, but not less than the largest saved value (the GPO
specifies the size in KB: `maxSize` divided by 1024). If all DCs have at least 1 GB, leave this setting not configured:
the GPO with the highest priority would reduce a size set elsewhere. The setting: Administrative Templates → Windows
Components → Event Log Service → Security → "Specify the maximum log file size (KB)" = `1048576` (or more). The
PowerShell equivalent:

```powershell
Set-GPRegistryValue -Name 'AdMng - inspekcja DC' -Key 'HKLM\Software\Policies\Microsoft\Windows\EventLog\Security' `
    -ValueName 'MaxSize' -Type DWord -Value 1048576
```

**Check** (DC01 and DC02, after `gpupdate /target:computer /force`):

```powershell
auditpol /get /subcategory:"{0CCE9235-69AE-11D9-BED3-505054503030},{0CCE9236-69AE-11D9-BED3-505054503030},{0CCE9237-69AE-11D9-BED3-505054503030},{0CCE9238-69AE-11D9-BED3-505054503030},{0CCE923C-69AE-11D9-BED3-505054503030},{0CCE9215-69AE-11D9-BED3-505054503030},{0CCE9242-69AE-11D9-BED3-505054503030},{0CCE923F-69AE-11D9-BED3-505054503030},{0CCE922F-69AE-11D9-BED3-505054503030}"
wevtutil gl security
```

Expected: `Success and Failure` for each subcategory (on a Polish system "Sukces i niepowodzenie"); in `wevtutil` the
`maxSize:` line equal to `1073741824` or more.

### 2.11 SACL – events 5136 and 5137 "who changed, who created"

> **In short.** What it changes: adds audit entries (SACL) on the domain object, on `AdminSDHolder` and on the PKI
> services container; it does not change permissions (DACL). Where: PAW, Domain Admin (in a multi-domain forest –
> Enterprise Admin for the PKI container). How long: a few minutes. Result: the controllers record events
> 5136/5137/5141 for the tracked objects. Rollback: the "Rollback" block at the end of the step – it removes only the
> entries added here.

**Where:** PAW, Domain Admin (the "Manage auditing and security log" right on the controllers is required – Domain
Admins have it through the Administrators group; who else has it – below, "Who can remove these entries"). **What and
why:** the default SACLs in AD do **not** generate events 5136/5137/5141 for users, groups and computers. Without them
Watchdog knows what changed but not who did it (`SACL_MISSING`). The script adds "Everyone – success" audit entries
modeled on Microsoft Defender for Identity: for the classes of tracked objects (also dMSA and GPO objects – needed for
the BadSuccessor rules and for the actor of GPO changes), on the domain object itself (password policy, `gPLink`,
`ms-DS-MachineAccountQuota`), on `AdminSDHolder` and – in the configuration partition, which does not inherit entries
from the domain – on the PKI services container `CN=Public Key Services` (certificate templates, authorities, NTAuth).
Without the last one a certificate template change will be detected, but with the actor "undetermined"
(`SACL_MISSING`). A change in the configuration partition requires, in a multi-domain forest, an account from the
Enterprise Admins group; in a single-domain forest Domain Admin is enough.

The controller records event 5137 ("object created") according to the SACL of the **parent** object – the "create
objects of class X" entry on the container in which the object is created counts. That is why, besides the entries on
the objects themselves, the script adds on the domain object "create and delete objects of class X – this object and all
child objects" entries. They work everywhere in the domain: in OUs, in containers (e.g. `CN=Managed Service Accounts` –
where a dMSA created without `-Path` ends up, `CN=Users`, `CN=Policies,CN=System`) and directly under the domain object.

First save the current SACLs. Keep the **first** backup file (preferably also off the workstation) – when you run 2.11
again, do not create a new backup, because it would already contain Watchdog's entries; the block below checks this
itself:

```powershell
Import-Module ActiveDirectory
$domain = (Get-ADDomain).DistinguishedName
$cfg    = (Get-ADRootDSE).configurationNamingContext
$pki    = "AD:\CN=Public Key Services,CN=Services,$cfg"
$old    = Get-ChildItem "$env:USERPROFILE\Documents\admng-sacl-backup-*.txt" -ErrorAction SilentlyContinue
if ($old) { "Kopia już istnieje – nie tworzę nowej: $($old.FullName)" } else {
    $backup = "$env:USERPROFILE\Documents\admng-sacl-backup-$(Get-Date -Format yyyyMMdd-HHmm).txt"
    'domain=' + (Get-Acl -Path "AD:\$domain" -Audit).GetSecurityDescriptorSddlForm('Audit') | Out-File $backup
    'adminsdholder=' + (Get-Acl -Path "AD:\CN=AdminSDHolder,CN=System,$domain" -Audit).GetSecurityDescriptorSddlForm('Audit') | Out-File $backup -Append
    'pki=' + (Get-Acl -Path $pki -Audit).GetSecurityDescriptorSddlForm('Audit') | Out-File $backup -Append
    $backup
}
```

**Entry definitions.** This block only prepares the list of entries (`$rules`) – it changes nothing. You run it before
adding the entries, before the check and before the rollback (also in a new PowerShell session):

```powershell
Import-Module ActiveDirectory
$domain   = (Get-ADDomain).DistinguishedName
$cfg      = (Get-ADRootDSE).configurationNamingContext
$schema   = (Get-ADRootDSE).schemaNamingContext
$pki      = "AD:\CN=Public Key Services,CN=Services,$cfg"
$everyone = [System.Security.Principal.SecurityIdentifier]'S-1-1-0'
$none     = [guid]::Empty
$classes = @{
  user  = 'bf967aba-0de6-11d0-a285-00aa003049e2'; group = 'bf967a9c-0de6-11d0-a285-00aa003049e2'
  computer = 'bf967a86-0de6-11d0-a285-00aa003049e2'; organizationalUnit = 'bf967aa5-0de6-11d0-a285-00aa003049e2'
  inetOrgPerson = '4828cc14-1437-45bc-9b07-ad6f015e5f28'
  msa = 'ce206244-5827-4a86-ba1c-1c0c386c1b64'; gmsa = '7b8b558a-93a5-4af7-adca-c017e67f1057'
  groupPolicyContainer = 'f30e3bc2-9ff0-11d1-b603-0000f80367c1'; passwordSettings = '3bcd9db8-f84b-451c-952f-6c52b81f9ec6'
  trustedDomain = 'bf967ab8-0de6-11d0-a285-00aa003049e2'
}
# dMSA (Windows Server 2025) – only when the class exists in the schema
$dmsa = Get-ADObject -SearchBase $schema -LDAPFilter '(lDAPDisplayName=msDS-DelegatedManagedServiceAccount)' -Properties schemaIDGUID
if ($dmsa) { $classes['dmsa'] = ([guid]$dmsa.schemaIDGUID).Guid }
function New-AdMngAuditRule([string]$Rights, [guid]$ObjectType, [string]$Inheritance, [guid]$InheritedType) {
    New-Object System.DirectoryServices.ActiveDirectoryAuditRule($everyone, $Rights, 'Success', $ObjectType, $Inheritance, $InheritedType)
}
$write = 'WriteProperty, Delete, DeleteTree, WriteDacl, WriteOwner, CreateChild, DeleteChild, ExtendedRight, Self'
$rules = @{
    "AD:\$domain" = @(
        foreach ($g in $classes.Values) {
            New-AdMngAuditRule $write $none 'Descendents' $g               # 1) changes of objects of this class (5136, 5139, 5141)
            New-AdMngAuditRule 'CreateChild, DeleteChild' $g 'All' $none   # 2) creating and deleting objects of this class across the whole domain (5137, 5141)
        }
        New-AdMngAuditRule 'WriteProperty, WriteDacl, WriteOwner' $none 'None' $none    # 3) the domain object itself
    )
    "AD:\CN=AdminSDHolder,CN=System,$domain" = @(                          # 4) the permission template of protected accounts
        New-AdMngAuditRule 'WriteProperty, WriteDacl, WriteOwner' $none 'None' $none)
    $pki = @(                                                              # 5) PKI services – this object and all child objects
        New-AdMngAuditRule 'WriteProperty, Delete, DeleteTree, WriteDacl, WriteOwner, CreateChild, DeleteChild' $none 'All' $none)
}
```

(Optionally you can add to `$rules` the same entry as in item 5 for `"AD:\CN=Sites,$cfg"` – GPO links to sites.)

**Adding the entries** (in the same session, after the definitions block):

```powershell
foreach ($p in $rules.Keys) {
    $acl = Get-Acl -Path $p -Audit
    foreach ($r in $rules[$p]) { $acl.AddAuditRule($r) }
    Set-Acl -Path $p -AclObject $acl
}
```

**Check** (in the same session, after the definitions block):

```powershell
foreach ($p in $rules.Keys) {
    $have = (Get-Acl -Path $p -Audit).GetAuditRules($true, $false, [System.Security.Principal.SecurityIdentifier])
    $missing = @($rules[$p] | Where-Object {
        $r = $_
        -not ($have | Where-Object { $_.IdentityReference -eq $r.IdentityReference -and $_.AuditFlags -eq $r.AuditFlags -and
            $_.ObjectType -eq $r.ObjectType -and $_.InheritedObjectType -eq $r.InheritedObjectType -and
            $_.InheritanceType -eq $r.InheritanceType -and ($_.ActiveDirectoryRights -band $r.ActiveDirectoryRights) -eq $r.ActiveDirectoryRights })
    })
    '{0}: brakujących wpisów {1} z {2}' -f $p, $missing.Count, @($rules[$p]).Count
}
```

Expected: three lines with `brakujących wpisów 0` – for the domain object `0 z 23` on the Windows Server 2025 schema
(`0 z 21` without the dMSA class), for `AdminSDHolder` and the PKI container `0 z 1`.

**Functional test** (after 2.10). Make the changes on DC01 so that you know where to look for the events:

```powershell
Set-ADUser jan.kowalski -Description 'test SACL' -Server DC01.contoso.com
New-ADUser -Name 'test-sacl' -Server DC01.contoso.com             # without -Path: ends up in the CN=Users container
Remove-ADUser test-sacl -Confirm:$false -Server DC01.contoso.com
```

On DC01 (console or `Invoke-Command -ComputerName DC01.contoso.com { … }`):

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'Security'; Id = 5136, 5137, 5141 } -MaxEvents 6 | Format-List TimeCreated, Id, Message
```

Expected: event 5136 "A directory service object was modified" with the account `CONTOSO\adm-jan`, the object
`CN=Jan Kowalski,…` and the attribute `description`; 5137 "A directory service object was created" for
`CN=test-sacl,CN=Users,…` (the create entry works in containers too) and 5141 "A directory service object was deleted".

**Who can remove these entries.** The SACL list can be changed by anyone who has the "Manage auditing and security log"
right (`SeSecurityPrivilege`) on the controllers – and they do it silently: Watchdog will not read the SACL (that
requires the same right the service account does not have), and the attribution coverage (Status → Attribution) will
not show for a long time that individual entries were removed, e.g. only those for creating dMSAs and GPOs – critical
alerts then lose the actor. Check who has this right (DC01; the other controllers get the same policies from the GPOs
linked to the Domain Controllers OU):

```powershell
Invoke-Command -ComputerName DC01.contoso.com {
    secedit /export /areas USER_RIGHTS /cfg C:\Windows\Temp\admng-ur.inf | Out-Null
    (Select-String -Path C:\Windows\Temp\admng-ur.inf -Pattern '^SeSecurityPrivilege').Line
    Remove-Item C:\Windows\Temp\admng-ur.inf
}
```

Expected: `SeSecurityPrivilege = *S-1-5-32-544` (Administrators), and in an organization with Exchange additionally the SID of
the `Exchange Servers` group (the right is granted by the Exchange installer in the Default Domain Controllers Policy).
The name for a SID (without `*`) is shown by
`([System.Security.Principal.SecurityIdentifier]'<SID>').Translate([System.Security.Principal.NTAccount])`.
Every holder of this right can silently disable Watchdog's attribution, so they are in practice Tier 0. Explain every
other entry and remove it from the GPO that grants it. Do not remove the Exchange Servers group (without this right
Exchange will not mount databases) – Exchange servers are therefore a Tier 0 asset.

Watchdog reports as a critical event (rule `tier0-security`) every security change of the domain object,
`AdminSDHolder`, the Domain Controllers OU, `CN=Public Key Services` and `CN=Sites` – also a change of the SACL list
itself, although it does not see its content (detection confirmed – ADR-0009). It also shows the coverage – the
percentage of changes attributed to an actor (Status → Attribution). You check the content of the entries yourself: repeat
the "Check" block (after the definitions block) **once a month** and after every deployment of a tool that changes
SACLs (e.g. Defender for Identity, Exchange).

**Rollback** – removes only the entries added in 2.11; works in a new PowerShell session, also after weeks. First run
the "Entry definitions" block, then (enter the name of the **first** backup file):

```powershell
$backup = "$env:USERPROFILE\Documents\admng-sacl-backup-<data>.txt"      # the first backup file from 2.11
$saved  = @{}
Get-Content $backup | ForEach-Object { $k, $v = $_ -split '=', 2; $saved[$k] = $v }
$names  = @{ "AD:\$domain" = 'domain'; "AD:\CN=AdminSDHolder,CN=System,$domain" = 'adminsdholder'; $pki = 'pki' }
function Test-AdMngAuditRule($list, $r) {        # whether the list contains exactly such an entry as $r (the same rights too)
    @($list | Where-Object { $_.IdentityReference -eq $r.IdentityReference -and $_.AuditFlags -eq $r.AuditFlags -and
        $_.ObjectType -eq $r.ObjectType -and $_.InheritedObjectType -eq $r.InheritedObjectType -and
        $_.InheritanceType -eq $r.InheritanceType -and $_.ActiveDirectoryRights -eq $r.ActiveDirectoryRights }).Count -gt 0
}
foreach ($p in $rules.Keys) {
    $before = New-Object System.DirectoryServices.ActiveDirectorySecurity
    if ($saved[$names[$p]]) { $before.SetSecurityDescriptorSddlForm($saved[$names[$p]], 'Audit') }
    $old = $before.GetAuditRules($true, $false, [System.Security.Principal.SecurityIdentifier])
    $acl = Get-Acl -Path $p -Audit
    foreach ($r in $rules[$p]) {
        $acl.RemoveAuditRuleSpecific($r)                                   # removes exactly the entry added in 2.11
        $old | Where-Object { $_.IdentityReference -eq $r.IdentityReference -and $_.AuditFlags -eq $r.AuditFlags -and
            $_.ObjectType -eq $r.ObjectType -and $_.InheritedObjectType -eq $r.InheritedObjectType -and
            $_.InheritanceType -eq $r.InheritanceType } | ForEach-Object { $acl.AddAuditRule($_) }   # the same entry from before 2.11 comes back
    }
    Set-Acl -Path $p -AclObject $acl
    $now  = (Get-Acl -Path $p -Audit).GetAuditRules($true, $false, [System.Security.Principal.SecurityIdentifier])
    $left = @($rules[$p] | Where-Object { (Test-AdMngAuditRule $now $_) -and -not (Test-AdMngAuditRule $old $_) })
    '{0}: pozostało wpisów z 2.11: {1}' -f $p, $left.Count
}
```

Expected: three lines with `pozostało wpisów z 2.11: 0`. An entry from 2.11 that was also in the backup (e.g. entry 3,
equal to the default entry of the domain object) comes back from the backup and is not counted. A result greater than 0
means the entry did not disappear: repeat the block, and if the result does not change, show the entries
(`$left | Format-List`) and remove them in "Active Directory Users and Computers" (Properties → Security → Advanced →
Auditing). The block uses `RemoveAuditRuleSpecific`, which removes exactly the entry with the same rights, class and
inheritance. Plain `RemoveAuditRule` is not suitable here: when the object has entries 1 and 2 for the same account
(Everyone), it refuses to remove either of them and does not report an error (verified against the .NET code).

Entries that someone added after 2.11 (e.g. Defender for Identity, Exchange, another team) stay – with one exception: an
entry with the same account, class and inheritance as an entry from 2.11 may have merged with it into one. Such an entry
will disappear together with the 2.11 entry if its rights were contained within the 2.11 rights; if they were broader, it
stays in full (including the 2.11 rights). Examples are the "Everyone – Descendant User/Group/Computer objects" entries
required by Defender for Identity: if Defender for Identity was deployed after 2.11, do not roll back the SACL. The first
backup file then serves only for comparison – fully restoring the SACL from it would remove all entries added after 2.11.

### 2.12 Windows Event Forwarding – GPO for domain controllers

**Where:** PAW (GPMC), check on DC01 and DC02. **What and why:** the controllers themselves send selected events from the
Security log to OpenWEC on the Watchdog server (a source-initiated subscription). No agents are needed and no RPC has
to be opened on the DCs, and the service account needs no rights to the controllers' logs. The controller authenticates
with its computer account (`DC01$`) through Kerberos, and the content is encrypted at the message level (HTTP 5985).

Create the GPO `AdMng - WEF` linked to the **Domain Controllers** OU:

1. **Subscription Manager.** Administrative Templates → Windows Components → Event Forwarding → "Configure target
   Subscription Manager" = Enabled → Show → add:
   `Server=http://admng-wd.contoso.com:5985/wsman/SubscriptionManager/WEC,Refresh=60`.
   `Refresh=60` (seconds) speeds up testing; after the pilot change it to `3600`.
2. **Security log access for NETWORK SERVICE.** Forwarding is handled by the WinRM service running as NETWORK SERVICE,
   which by default does not read the Security log. Administrative Templates → Windows Components → Event Log Service →
   Security → "Configure log access" = Enabled, value:
   `O:BAG:SYD:(A;;0xf0005;;;SY)(A;;0x5;;;BA)(A;;0x1;;;S-1-5-32-573)(A;;0x1;;;NS)`. Set the same in "Configure log
   access (legacy)" (a recommendation of the OpenWEC documentation). This value **replaces** the permissions to the
   Security log on every DC as a whole – it does not merge with the current ones. Before you enter it, compare it with
   the current one on **each** DC (from the PAW:
   `Invoke-Command -ComputerName DC01.contoso.com, DC02.contoso.com { "$env:COMPUTERNAME $((wevtutil gl security) -match '^channelAccess')" }`).
   By default it is `O:BAG:SYD:(A;;0xf0005;;;SY)(A;;0x5;;;BA)(A;;0x1;;;S-1-5-32-573)`. If there are additional entries
   (e.g. read access for a SIEM or EDR agent account), note them down, copy them into the GPO value and add only
   `(A;;0x1;;;NS)` – otherwise that agent will lose access to the Security log. This method works without a restart –
   unlike adding NETWORK SERVICE to the Event Log Readers group.
3. **WinRM service.** Security Settings → System Services → "Windows Remote Management (WS-Management)" = Automatic (on
   Windows Server it usually already runs).
4. **Outbound firewall.** By default Windows Firewall does not block outbound traffic. If your DC GPO blocks outbound
   traffic, add a rule: TCP 5985 to 10.0.1.25.

Instead of creating the GPO and items 1–2 in GPMC you can use PowerShell (GroupPolicy module) – the commands create the
GPO, link it to the OU and set both values; add the "(legacy)" setting and items 3–4 afterwards in GPMC. The comparison
from item 2 is mandatory here too: `$sddl` replaces the permissions to the Security log on every DC, so add any
additional entries found on any controller to `$sddl` before `(A;;0x1;;;NS)`:

```powershell
# default entries + NETWORK SERVICE; add the additional entries from the DCs here (item 2)
$sddl = 'O:BAG:SYD:(A;;0xf0005;;;SY)(A;;0x5;;;BA)(A;;0x1;;;S-1-5-32-573)(A;;0x1;;;NS)'
$gpo = New-GPO -Name 'AdMng - WEF'
New-GPLink -Guid $gpo.Id -Target 'OU=Domain Controllers,DC=contoso,DC=com' -LinkEnabled Yes   # migration from 0.2.0: -LinkEnabled No (0.4)
Set-GPRegistryValue -Guid $gpo.Id -Key 'HKLM\Software\Policies\Microsoft\Windows\EventLog\EventForwarding\SubscriptionManager' `
    -ValueName '1' -Type String -Value 'Server=http://admng-wd.contoso.com:5985/wsman/SubscriptionManager/WEC,Refresh=60'
Set-GPRegistryValue -Guid $gpo.Id -Key 'HKLM\Software\Policies\Microsoft\Windows\EventLog\Security' `
    -ValueName 'ChannelAccess' -Type String -Value $sddl
```

The audit policy and SACL (2.10, 2.11) are part of the same whole – without them there is nothing to forward. The
"Remote Event Log Management" firewall rules and the service account's membership in Event Log Readers from version 0.2.0
are **not needed** (remove them during migration – at the end of this step).

**Check** (DC01 and DC02, after `gpupdate /target:computer /force`):

```powershell
Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\EventLog\EventForwarding\SubscriptionManager'
wevtutil gl security | Select-String 'channelAccess'
Get-Service WinRM | Format-Table Name, Status, StartType
Get-WinEvent -LogName 'Microsoft-Windows-Forwarding/Operational' -MaxEvents 10 | Format-Table TimeCreated, Id, Message -Wrap
```

Expected:

- the value `1 : Server=http://admng-wd.contoso.com:5985/wsman/SubscriptionManager/WEC,Refresh=60`,
- `channelAccess:` contains `(A;;0x1;;;NS)` and all additional entries noted in item 2 (e.g. the SIEM agent),
- `WinRM  Running  Automatic`,
- in the Forwarding/Operational log: **as long as OpenWEC is not running** – events 105 ("The forwarder is having a
  problem communicating with subscription manager…") – this is expected. After OpenWEC starts (chapter 4) – event
  **104** ("The forwarder has successfully connected to the subscription manager…"), followed by **100** ("The
  subscription admng-dc-security is created successfully.").

After OpenWEC starts you will also check the connection: `Test-NetConnection admng-wd.contoso.com -Port 5985` –
`TcpTestSucceeded : True`.

**Migration from version 0.2.0** (only when the Windows service was running; do it after the switchover from the 0.4
box and after starting and checking the service on Debian – chapters 5 and 6). Remove the rights that the gMSA account of
the Windows version received – otherwise permissions near Tier 0 will be left behind:

```powershell
$o = 'CONTOSO\svc-admngwd$'; $d = 'DC=contoso,DC=com'
foreach ($p in $d, "CN=AdminSDHolder,CN=System,$d", "CN=Password Settings Container,CN=System,$d", "CN=Deleted Objects,$d") {
    dsacls $p /R $o
}
Remove-ADGroupMember -Identity 'S-1-5-32-573' -Members (Get-ADServiceAccount svc-admngwd) -Confirm:$false   # Event Log Readers
(Get-ADGroup -Identity 'S-1-5-32-573' -Properties member).member      # remaining members
```

- If the last command shows `CN=S-1-5-20,CN=ForeignSecurityPrincipals,…` (NETWORK SERVICE – variant B of version
  0.2.0), remove it: `Set-ADGroup -Identity 'S-1-5-32-573' -Remove @{ member = "CN=S-1-5-20,CN=ForeignSecurityPrincipals,$d" }`.
  Access to the Security log is now provided by the setting from item 2.
- Disable the "Remote Event Log Management" firewall rules on every DC (or in the GPO that enabled them):
  `Get-NetFirewallRule -Name 'RemoteEventLogSvc*' | Disable-NetFirewallRule`.
- If in variant B the controllers sent events to a Windows collector through another subscription manager GPO – unlink
  it from the Domain Controllers OU or delete it.
- Finally, uninstall the Windows service on the old server (`Uninstall-AdMngWatchdog.ps1 -RemoveData`), delete the gMSA
  account and the group of servers that could retrieve its password: `Remove-ADServiceAccount svc-admngwd`,
  `Remove-ADGroup AdMng-Watchdog-Hosts`. You already removed the SPN `HTTP/admng-wd.contoso.com` from the gMSA during the
  switchover (0.4).

**Check:** `dsacls 'DC=contoso,DC=com' | Select-String 'svc-admngwd'` returns nothing.

### 2.13 Summary of AD changes and their rollback

| # | Change | Where | Check | Rollback |
|---|---|---|---|---|
| 1 | Delegation fixes in the Tier 0 OU (2.2) | ACL of the Tier 0 OU, AdminSDHolder | script 2.2 with no findings | restore the SDDL from the saved file (command below the table; usually you do **not** roll back – it is a security improvement) |
| 2 | Groups `AdMng-Watchdog-Admins`, `-Viewers` (2.3) | Groups OU in Tier 0 | `Get-ADGroup` | `Remove-ADGroup AdMng-Watchdog-Admins`, `Remove-ADGroup AdMng-Watchdog-Viewers` |
| 3 | A + PTR record `admng-wd` (2.4) | DNS | `Resolve-DnsName` | `Remove-DnsServerResourceRecord -ComputerName DC01.contoso.com -ZoneName contoso.com -Name admng-wd -RRType A -Force` and the PTR `-ZoneName 1.0.10.in-addr.arpa -Name 25 -RRType Ptr` |
| 4 | Account `ADMNG-WD$` with a deny entry for SELF (2.5) | Service Accounts OU in Tier 0 | `Get-ADComputer`, `setspn -L`, `Get-Acl` | `Disable-ADAccount 'ADMNG-WD$'`; when an Enterprise CA exists – revoke the certificates issued to the account (8.2, step 1); after confirming, `Remove-ADComputer ADMNG-WD` (the keytab stops working immediately; the deny entry disappears together with the object) |
| 5 | GPO "AdMng - ograniczenia konta Watchdog" and account entries in existing GPOs with deny-logon rights (2.5) | domain, the OUs of those GPOs | `secedit` on workstations | `Remove-GPO -Name 'AdMng - ograniczenia konta Watchdog'` and remove the account from the rights in the other GPOs |
| 6 | `dsacls` read permissions (2.6) | domain, AdminSDHolder, Password Settings Container, Deleted Objects | `dsacls … \| Select-String ADMNG-WD`, test from Debian | **before** removing the account: `dsacls <DN> /R 'CONTOSO\ADMNG-WD$'` on each of the four objects (we do not roll back the owner change of Deleted Objects) |
| 7 | GPO read for Domain Computers (2.7, only when needed) | selected GPOs | script 2.7 | `Set-GPPermission -Guid <Id> -TargetName $dc -TargetType Group -PermissionLevel None -Replace` (`$dc` as in 2.7; without `-Replace` the command changes nothing) |
| 8 | `ms-DS-MachineAccountQuota` = 0 (2.8) | domain object | read the attribute | `Set-ADDomain -Identity contoso.com -Replace @{ 'ms-DS-MachineAccountQuota' = <previous> }` |
| 9 | Fixes of the dMSA creation right (2.9) | ACL of the domain object, OUs, containers, `CN=Builtin` | script 2.9 | as 1 – usually you do not roll back |
| 10 | GPO "AdMng - inspekcja DC" + log size (2.10) | Domain Controllers OU | `auditpol`, `wevtutil gl security` | keep the GPO (recommended – auditing is useful regardless of Watchdog). `Remove-GPO` alone does **not** restore the previous policy – domain controllers keep the last settings in their local configuration. If you must roll back: in this GPO set each subcategory to the value from the backup made in 2.10 (command below the table; "No Auditing" only where the backup says so), and the log size as in the backup or not configured; on the DC run `gpupdate /target:computer /force`, compare `auditpol /get /category:*` with the backup, and only then `Remove-GPO -Name 'AdMng - inspekcja DC'` |
| 11 | SACL (2.11) | domain object, AdminSDHolder, `CN=Public Key Services` | check block from 2.11 | the "Rollback" block from 2.11 – removes only the entries added in 2.11 and checks the result itself (`pozostało wpisów z 2.11: 0` for each object) |
| 12 | GPO "AdMng - WEF" (2.12) | Domain Controllers OU | registry, `wevtutil`, Forwarding/Operational | `Remove-GPO -Name 'AdMng - WEF'` and `gpupdate /force` on the DCs – the domain controllers stop sending events, NETWORK SERVICE access to Security returns to the default state |
| 13 | Delegation of the right to create computer objects (2.8, only when needed) | computer OU, `CN=Computers` | `Get-Acl` from 2.8 | rollback block from 2.8 – removes only the `CC;computer` entry added in 2.8 and only where you granted it (**not** `dsacls /R`, which would also remove the group's other delegations); then the check from 2.8 shows `False` |

Audit policy values from before 2.10 (for each DC, from its backup file). The column headers of the file are in the
language of the DC's system (e.g. Polish or German), so the block reads the columns by their order, not by name:

```powershell
$guids = '{0CCE9235-69AE-11D9-BED3-505054503030}', '{0CCE9236-69AE-11D9-BED3-505054503030}', '{0CCE9237-69AE-11D9-BED3-505054503030}',
         '{0CCE9238-69AE-11D9-BED3-505054503030}', '{0CCE923C-69AE-11D9-BED3-505054503030}', '{0CCE9215-69AE-11D9-BED3-505054503030}',
         '{0CCE9242-69AE-11D9-BED3-505054503030}', '{0CCE923F-69AE-11D9-BED3-505054503030}', '{0CCE922F-69AE-11D9-BED3-505054503030}'
Import-Csv "$env:USERPROFILE\Documents\admng-auditpol-before-DC01.csv" -Header Machine, Target, Subcategory, Guid, Inclusion, Exclusion, Value |
    Select-Object -Skip 1 | Where-Object { $guids -contains $_.Guid } | Format-Table Subcategory, Guid, Inclusion, Value
```

Expected: 9 rows. `Value`: `0` – no auditing (No Auditing), `1` – success, `2` – failure, `3` – success and failure.
An empty result means a file read error (e.g. a wrong name), **not** missing settings – in that case do not remove the
GPO, check the file instead.

Instead of setting values in the GPO you can, after `Remove-GPO`, restore the backup on each DC: copy its backup file
to it and run `auditpol /restore /file:<file>`. Note: `/restore` restores the **entire** audit policy as of the time of
the backup – all subcategories and options, not just the 9 from the table – so it also reverts changes made after 2.10
by other GPOs or tools (e.g. Defender for Identity). Therefore save the current state before it
(`auditpol /get /category:* > C:\Windows\Temp\admng-auditpol-teraz.txt`), right after it run
`gpupdate /target:computer /force` on that DC (the other audit GPOs will set their values again) and compare
`auditpol /get /category:*` with the saved output. Only the subcategories from the table should differ (now the values
from the backup, unless another GPO sets them); set every other difference back with `auditpol /set` and only then
delete the saved output.

**Restoring the permission list (DACL) from the file saved in 2.2 or 2.9.** The fixes from 2.2 and 2.9 are usually
removed entries. First compare the backup with the current state and restore only what is needed – a full restore from
the file also removes all changes made after the backup was saved (by you, other teams or installers):

```powershell
$dn   = 'OU=Tier 0,OU=Admin,DC=contoso,DC=com'                 # the object the file refers to
$sddl = (Get-Content '<plik acl-….txt>' -Raw).Trim()
$copy = New-Object System.DirectoryServices.ActiveDirectorySecurity
$copy.SetSecurityDescriptorSddlForm($sddl)
$fmt  = { '{0} {1} {2} {3} {4} {5}' -f $_.AccessControlType, $_.IdentityReference, $_.ActiveDirectoryRights, $_.ObjectType, $_.InheritanceType, $_.InheritedObjectType }
$then = $copy.GetAccessRules($true, $false, [System.Security.Principal.NTAccount]) | ForEach-Object $fmt
$now  = (Get-Acl -Path "AD:\$dn").GetAccessRules($true, $false, [System.Security.Principal.NTAccount]) | ForEach-Object $fmt
Compare-Object $then $now     # <= only in the backup (removed later), => added after the backup was saved
'Właściciel w kopii: ' + $copy.GetOwner([System.Security.Principal.NTAccount]) + ', teraz: ' + (Get-Acl -Path "AD:\$dn").Owner
```

You can restore a removed entry in "Active Directory Users and Computers" (Security → Advanced → Add) or with
`dsacls … /G`. Full restore (the owner and all explicit entries from the backup; changes made after the backup was
saved **will disappear**):

```powershell
$acl = Get-Acl -Path "AD:\$dn"
$acl.SetSecurityDescriptorSddlForm($sddl, 'Owner, Access')
Set-Acl -Path "AD:\$dn" -AclObject $acl
```

---

## 3. Debian server

You run all commands in this chapter on **Debian** as root: log in over SSH to your administrator account and type
`sudo -i`.

### 3.1 System installation and updates

**Where:** the virtual machine console (installer), then Debian. **What and why:** a minimal system without a graphical
environment – less software means fewer vulnerabilities on a Tier 0 server.

**Where to place the machine (a requirement, before installation).** The virtual machine runs only on the Tier 0
virtualization platform: on the same hosts (cluster) and under the same administrators as the domain controllers – or on
a separate host whose administrators, console access, snapshots, backups and vTPM key protection are in the hands of
Tier 0. Do not put it on a general-purpose cluster (Tier 1): its administrators could copy the machine's disk or memory,
and with them the keytab, the secrets key and the 3-year history, or roll the machine back to a snapshot and "blind"
Watchdog (0.3, point 1).

1. Image: Debian 13 "trixie" netinst amd64 (debian.org). Debian 12 "bookworm" is also supported (a separate
   `admng-openwec` package for Debian 12).
2. In the installer:
   - host name `admng-wd`, domain `contoso.com`;
   - static network: 10.0.1.25/24, gateway 10.0.1.1, DNS 10.0.1.10 and 10.0.1.11;
   - **leave the root password empty** – the root account will be locked, and the first user (e.g. `adm-jan`) will get
     `sudo`. The password of this account (and of the accounts added in 3.2) will always be needed: for `sudo` and for
     logging in on the virtual machine console, even after password login over SSH is disabled. Store it in the Tier 0
     password manager – without it you can regain root only through rescue mode (changing boot parameters in the
     console);
   - a disk of at least 60 GB (3 years of history, database copies); if you create a separate `/var` partition, it
     must be at least 40 GB; the installer will create the swap partition itself – you will disable it after
     installation (below);
   - software selection: **only** "SSH server" and "standard system utilities".
3. Disk encryption (recommended – the disk will hold the 3-year change history and the keytab): prefer encryption by
   the hypervisor – it protects only against theft of the media, not against hypervisor administrators (hence the
   requirement above). LUKS from the Debian installer requires typing the passphrase in the console at **every** start –
   after a restart (e.g. a power failure) the server will not come up by itself, and monitoring does not work until
   then.

After the first start:

```bash
# /etc/hosts: the server name must point to the real address, not 127.0.1.1
sed -i 's/^127\.0\.1\.1\s.*$/10.0.1.25\tadmng-wd.contoso.com\tadmng-wd/' /etc/hosts
grep -q '^10\.0\.1\.25' /etc/hosts || printf '10.0.1.25\tadmng-wd.contoso.com\tadmng-wd\n' >> /etc/hosts
# no swap – the contents of service memory never reach the disk
swapoff -a && sed -i '/\sswap\s/s/^/#/' /etc/fstab
echo 'RESUME=none' > /etc/initramfs-tools/conf.d/resume && update-initramfs -u
# systemd enables a swap partition by itself at every start (it recognizes it by the GPT partition type) when fstab has
# none – the kernel parameter systemd.gpt_auto=0 turns that off
grep -q 'systemd\.gpt_auto=0' /etc/default/grub ||
    sed -i 's/^GRUB_CMDLINE_LINUX="\(.*\)"/GRUB_CMDLINE_LINUX="\1 systemd.gpt_auto=0"/' /etc/default/grub
update-grub
apt update && apt full-upgrade -y
DEBIAN_FRONTEND=noninteractive apt install -y krb5-user libsasl2-modules-gssapi-mit ldap-utils smbclient \
    chrony nftables openssl ca-certificates curl bind9-dnsutils unattended-upgrades apt-listchanges
```

During an interactive installation the `krb5-user` package asks for a default realm – it does not matter: the service
and all commands in this guide use their own file `/etc/admng-watchdog/krb5.conf` (3.5), and `/etc/krb5.conf` stays
unchanged. Do not install `msktutil`, `adcli`, `realmd`, `sssd` or `gss-ntlmssp`.

**The .NET runtime (ASP.NET Core Runtime 10)** – a requirement of the `admng-watchdog` package (version 10.0.13 or
later), from the Microsoft repository. For Debian 12 put `12` instead of `13` in the address:

```bash
curl -fsSLo /root/packages-microsoft-prod.deb https://packages.microsoft.com/config/debian/13/packages-microsoft-prod.deb
apt install -y /root/packages-microsoft-prod.deb && rm /root/packages-microsoft-prod.deb
apt update && apt install -y aspnetcore-runtime-10.0
```

**Automatic security updates:**

```bash
dpkg-reconfigure -plow unattended-upgrades          # answer "Yes"
cat > /etc/apt/apt.conf.d/51admng-unattended <<'EOF'
// the .NET runtime from the Microsoft repository
Unattended-Upgrade::Origins-Pattern {
        "site=packages.microsoft.com";
};
EOF
```

Updating the .NET runtime removes the files of the previous version from disk – the `admng-watchdog` package (below)
then restarts the service itself (a dpkg trigger). The restart takes a few seconds; the Status tab shows the runtime
version.

Restart the server (`systemctl reboot`) and log in again (console or SSH, then `sudo -i`): only after the restart can
you see whether swap is really disabled (and the kernel from `full-upgrade` starts running).

**Check** (after the restart):

```bash
hostname --fqdn; hostname -I; cat /etc/debian_version
swapon --show; grep -o 'systemd.gpt_auto=0' /proc/cmdline
dotnet --list-runtimes
unattended-upgrade --dry-run --debug 2>&1 | grep -i 'allowed origins'
systemd-creds has-tpm2; echo "kod: $?"
```

Expected: `admng-wd.contoso.com`, `10.0.1.25`, `13.x`; `swapon --show` prints nothing, and `grep` prints
`systemd.gpt_auto=0`; in `dotnet --list-runtimes` the lines `Microsoft.AspNetCore.App 10.0.13` (or later) and
`Microsoft.NETCore.App 10.0.13`; the list of allowed origins contains `Debian-Security` and `site=packages.microsoft.com`.
This checks only the configuration – the real check comes with the next .NET update (usually on the second Tuesday of
the month): `grep -i aspnetcore /var/log/unattended-upgrades/unattended-upgrades.log` shows its installation.

The result of `systemd-creds has-tpm2` tells whether the machine has a (v)TPM: `+firmware` and `+driver` – yes. In that
case **always** install the tpm2-tss libraries – without them secrets encryption will not use the TPM (3.9). On Debian
13 the result also shows `±libraries`; on Debian 12 (systemd 252) there is no such line and the command answers `yes`
even without the libraries, so check them with `dpkg -s`:

```bash
apt install -y libtss2-esys-3.0.2-0t64 libtss2-mu-4.0.1-0t64 libtss2-rc0t64 libtss2-tcti-device0t64   # Debian 13
apt install -y libtss2-esys-3.0.2-0 libtss2-mu0 libtss2-rc0 libtss2-tcti-device0                     # Debian 12
dpkg -s libtss2-esys-3.0.2-0 libtss2-mu0 libtss2-rc0 libtss2-tcti-device0 | grep -c 'Status: install ok installed'   # Debian 12
```

Expected: on Debian 13 `systemd-creds has-tpm2` answers `yes` (code 0) with a `+libraries` line; on Debian 12 the last
command prints `4`. When installing (below), the `admng-watchdog` package will use the "TPM2 + host key" variant if the
machine has a TPM, and if that fails – the host key alone, and it will print which variant it used and why.

**AdMng Watchdog packages.** From the [v0.3.0 release](https://github.com/DarienRahl/AD_MNG/releases/tag/v0.3.0)
download the service package, the OpenWEC package for your Debian version and the checksum list – on the server (the
root account may connect to the Internet, also after the firewall from 3.3 is enabled) or on the PAW and then on with
`scp`. For Debian 12 put `deb12` instead of `deb13` in the OpenWEC package name:

```bash
install -d -m 0755 /var/tmp/admng-0.3.0 && cd /var/tmp/admng-0.3.0
u=https://github.com/DarienRahl/AD_MNG/releases/download/v0.3.0
curl -fsSLO "$u/SHA256SUMS.txt"
curl -fsSLO "$u/admng-watchdog_0.3.0-1_amd64.deb"
curl -fsSLO "$u/admng-openwec_0.3.0.git20260908.86de07f-1-deb13_amd64.deb"
sha256sum --ignore-missing -c SHA256SUMS.txt
apt install -y ./admng-openwec_0.3.0.git20260908.86de07f-1-deb13_amd64.deb ./admng-watchdog_0.3.0-1_amd64.deb
cd / && rm -r /var/tmp/admng-0.3.0
```

Expected: `sha256sum` prints `OK` for both packages (on `FAILED` install nothing – download the files again); `apt`
installs both packages with dependencies (including `smbclient`, the MIT Kerberos libraries and SASL GSSAPI) and prints:

- the channel secrets key variant – `Data Protection key credential created (variant: tpm2+host)`, and without a (v)TPM
  or the tpm2-tss libraries `(variant: host; …)` with the reason (3.9);
- `self-signed TLS certificate for admng-wd.contoso.com created` and its SHA-256 fingerprint – the variant B certificate
  from 3.8 for the name from `hostname --fqdn` (in variant A you will replace it with a certificate from the company
  CA);
- the next steps (`admng-watchdog-keytab init`, editing `appsettings.json`, starting the service) – you will do them in
  3.6 and chapter 5.

The packages create the system accounts `admng-watchdog` and `openwec` (without a shell), the group `admng-keytab`
(keytab readers: both services), directories with owners and modes (5.1) and configuration files with example names:
`/etc/admng-watchdog/appsettings.json`, `krb5.conf`, `ldap.conf`, `smb-client.conf` and `/etc/openwec.conf.toml` (3.5,
4.3, 5.2). The `admng-watchdog` and `openwecd` services are enabled but will not start until there is a keytab (3.6);
only two timers already run: the service account password change (does nothing until there is a keytab) and the
cleanup of OpenWEC files. **Check:** `dpkg -l admng-watchdog admng-openwec` – both in state `ii`, version `0.3.0-1` and
`0.3.0+git20260908.86de07f-1~deb13` (the files in the release have `.` and `-` in their names instead of `+` and `~`,
because GitHub changes file names with such characters; the package version does not change).

### 3.2 Administrator accounts and SSH

**Where:** Debian. **What and why:** the server is administered by 1–3 Tier 0 people, each with their own account, only
with SSH keys, with no root login and only from PAW workstations (firewall – 3.3). The accounts are local (the server is
not in the domain).

On the **PAW** workstation (Tier 0 – not on workstations with AD Manager that are also used by people outside Tier 0)
create a passphrase-protected key if you do not have one (PowerShell, the built-in OpenSSH client; with a FIDO2 hardware
key use `-t ed25519-sk`):

```powershell
ssh-keygen -t ed25519 -C 'adm-jan@PAW'      # enter the key passphrase
```

On Debian (you run `groupadd` only once; the first person already has the account from the installer):

```bash
groupadd --system ssh-admins
usermod -aG sudo,ssh-admins adm-jan                     # the first person – the account from the installer
adduser --gecos 'Anna Nowak (Tier 0)' adm-anna          # each next person: a new account (it asks for a password – 3.1)…
usermod -aG sudo,ssh-admins adm-anna                    # …and the same groups
```

On the **first** connection from the PAW, `ssh` shows the server key fingerprint and asks whether you trust it. Before
you answer `yes`, display the fingerprint on the Debian console (in the hypervisor) and compare the two:

```bash
ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
```

Copy the public key from the PAW to the server (password login is still enabled; the `.pub` file is not secret):

```powershell
Get-Content $env:USERPROFILE\.ssh\id_ed25519.pub | ssh adm-jan@admng-wd.contoso.com 'umask 077; mkdir -p ~/.ssh; cat >> ~/.ssh/authorized_keys'
```

A person added later (when password login is already disabled) hands over their `.pub` file, and root appends it to
`/home/<account>/.ssh/authorized_keys` (directory 0700, file 0600, owner – that person).

Check logging in with the **key alone** from a second window – it must succeed without asking for the account password
on the server (a prompt for the key passphrase is fine):

```powershell
ssh -o PreferredAuthentications=publickey adm-jan@admng-wd.contoso.com
```

Only then disable passwords and root. **Do not close this session** until the check below passes. Rollback (from the
open session or from the virtual machine console):
`sudo rm /etc/ssh/sshd_config.d/10-admng.conf && sudo systemctl reload ssh`.

```bash
cat > /etc/ssh/sshd_config.d/10-admng.conf <<'EOF'
PermitRootLogin no
PasswordAuthentication no
KbdInteractiveAuthentication no
AuthenticationMethods publickey
AllowGroups ssh-admins
X11Forwarding no
EOF
sshd -t && systemctl reload ssh
passwd -l root
```

**Check:** a new connection with the key works; `ssh -o PubkeyAuthentication=no adm-jan@admng-wd.contoso.com` ends with
`Permission denied (publickey)`; `sshd -T | grep -E 'permitrootlogin|passwordauthentication|allowgroups'` shows
`permitrootlogin no`, `passwordauthentication no`, `allowgroups ssh-admins`; `getent group sudo` – only Tier 0 people.
Close the old session only once a new connection with the key works.

### 3.3 Firewall (nftables)

**Where:** Debian. **What and why:** by default everything is blocked in both directions; we open only the ports the
service needs – incoming only from specific senders. The `admng-watchdog` package ships the same rule set as the example
`/usr/share/admng-watchdog/nftables/admng-watchdog.nft`.

| Direction | Port | Other side | Why |
|---|---|---|---|
| incoming | 22/tcp | only the addresses of PAW workstations (`PAWS`; 10.0.20.15 in the examples) | administrators' SSH |
| incoming | 8443/tcp | 10.0.20.0/24 | Watchdog API (HTTPS + Kerberos) for AD Manager |
| incoming | 5985/tcp | 10.0.1.10, 10.0.1.11 | OpenWEC – events from the domain controllers |
| outgoing | 53/udp+tcp | domain controllers | DNS |
| outgoing | 88/udp+tcp | domain controllers | Kerberos |
| outgoing | 123/udp | domain controllers | time (chrony) |
| outgoing | 389/tcp | domain controllers | LDAP with signing and encryption |
| outgoing | 445/tcp | domain controllers | SMB – reading SYSVOL |
| outgoing | 464/udp+tcp | domain controllers | kpasswd – service account password change |
| outgoing | 636/tcp | domain controllers | LDAPS (only if you enable this option) |
| outgoing | 587/tcp | 10.0.1.30 (`smtp.contoso.com`) | email (STARTTLS) |
| outgoing – only the `root` and `_apt` accounts | 80, 443/tcp | Internet | Debian and Microsoft updates (`apt` downloads packages as `_apt`) |
| outgoing – only the `admng-watchdog` account | 80, 443/tcp | Internet and the CRL servers of the company CA | Teams Workflows (443); checking that the SMTP server certificate is not revoked (CRL/OCSP – usually 80) |
| outgoing – the `openwec` account | **none** | – | OpenWEC only accepts connections from the domain controllers |

- **SSH only from PAW workstations.** In the 10.0.20.0/24 subnet people outside Tier 0 also work (e.g. the helpdesk with
  the Viewer role in Watchdog), so port 22 is open only for the addresses of PAW workstations – put them all in
  `define PAWS` (comma-separated). The API (8443) stays open to the whole subnet of workstations with AD Manager – also
  for Watchdog's administrative operations, which we do not restrict to PAW workstations (ADR-0009; consequence – 0.3,
  point 2). In the lab the PAW is ADM-WS01.
- **The `openwec` account without outgoing connections.** The OpenWEC receiver (chapter 4) has access to the keytab, and
  as a ticket recipient it never needs to open anything itself. The rule `meta skuid "openwec" drop` drops every packet
  of this account that does not belong to a connection established by a domain controller – a compromised OpenWEC will
  not send the keytab out of the server (also through DNS) nor use it by itself against the domain controllers.
- **Internet only for updates and the Watchdog service.** Connections to ports 80 and 443 to any address are made only
  by root, the `_apt` account (updates) and the service account `admng-watchdog` (Teams, CRL/OCSP lists of the SMTP
  server certificate). The other accounts on the server connect only to the domain controllers and the SMTP server. For
  the Watchdog service fixed addresses cannot be entered – Teams (Power Automate) runs under changing Microsoft cloud
  addresses. And the service, like OpenWEC, reads data from outside (GPO files from SYSVOL, which any GPO editor changes,
  event records from the DCs, values from LDAP) and has more at stake: the keytab, the channel secrets key and the 3-year
  history. A compromised service could therefore send this data to any server on the Internet over ports 80 and 443.
  This is a residual risk accepted knowingly (ADR-0009): connections go directly, without an intermediary server
  (proxy), only from the `admng-watchdog` account (and root and `_apt` for updates); the `openwec` account and the other
  accounts do not connect to the Internet at all.
- The firewall refers to accounts by name, and `nft` rejects a rule with the name of a nonexistent account (`User does
  not exist`) – that is why you enable it after installing the packages (3.1), which create the system accounts
  `openwec` and `admng-watchdog`.

```bash
getent passwd openwec admng-watchdog   # system accounts from the packages (3.1) – the firewall refers to them by name
cp -a /etc/nftables.conf /etc/nftables.conf.orig        # a copy in case of rollback (8.1)
cat > /etc/nftables.conf <<'EOF'
#!/usr/sbin/nft -f
flush ruleset

define DCS        = { 10.0.1.10, 10.0.1.11 }
define PAWS       = { 10.0.20.15 }
define ADMIN_NET  = 10.0.20.0/24
define SMTP_RELAY = 10.0.1.30

table inet admng {
    chain input {
        type filter hook input priority filter; policy drop;
        ct state established,related accept
        ct state invalid drop
        iif lo accept
        ip saddr $PAWS tcp dport 22 accept
        ip saddr $ADMIN_NET tcp dport 8443 accept
        ip saddr $DCS tcp dport 5985 accept
        icmp type { echo-request, destination-unreachable, time-exceeded } accept
    }
    chain forward {
        type filter hook forward priority filter; policy drop;
    }
    chain output {
        type filter hook output priority filter; policy drop;
        ct state established,related accept
        meta skuid "openwec" drop
        oif lo accept
        ip daddr $DCS udp dport { 53, 88, 123, 464 } accept
        ip daddr $DCS tcp dport { 53, 88, 389, 445, 464, 636 } accept
        ip daddr $SMTP_RELAY tcp dport 587 accept
        meta skuid { "root", "_apt", "admng-watchdog" } tcp dport { 80, 443 } accept
        icmp type echo-request accept
    }
}
EOF
nft -c -f /etc/nftables.conf && echo 'składnia OK'
```

Apply the rules with a "safety net": if you cut off your own SSH, systemd removes the rules by itself after 3 minutes.
The systemd timer runs independently of your session – also when the connection is dropped. Type it as **one** command
(`&&` makes the rules load only with the timer armed; the first part removes the timer from a previous attempt):

```bash
systemctl stop admng-nft-rescue.timer 2>/dev/null; systemd-run --on-active=180 --timer-property=RemainAfterElapse=no --unit=admng-nft-rescue /usr/sbin/nft flush ruleset && nft -f /etc/nftables.conf
```

**Do not close this session.** From the PAW open a **new** connection `ssh adm-jan@admng-wd.contoso.com`. If it does NOT
work – do nothing: after 3 minutes the rules disappear by themselves; fix `PAWS` and repeat the same command. Only when
the new connection works, run in the old session:

```bash
systemctl stop admng-nft-rescue.timer
systemctl enable --now nftables
```

When everything fails: log in on the virtual machine console in the hypervisor to your account (local password – 3.1),
type `sudo nft flush ruleset`, fix `PAWS` in `/etc/nftables.conf` and run `sudo systemctl restart nftables` (when the
`nftables` service is already enabled, `flush` alone works only until the server restarts).

The rules accept IPv4 only. If your server network uses IPv6, add `ip6` equivalents (and ICMPv6 for neighbor
discovery); otherwise IPv6 traffic is blocked.

**Check:**

- `nft list ruleset` – shows the `inet admng` table;
- `systemctl list-timers admng-nft-rescue.timer` – no timer (the safety net is off);
- from the PAW (in the lab ADM-WS01): `Test-NetConnection admng-wd.contoso.com -Port 22` – `TcpTestSucceeded : True`;
- from a computer that is not a PAW (in production e.g. a helpdesk workstation in 10.0.20.0/24), the same – `False`;
- on Debian: `getent hosts DC01.contoso.com` and `curl -sI https://packages.microsoft.com | head -1` – they respond;
- on Debian as the service account:
  `sudo -u admng-watchdog curl -sS -m 10 -o /dev/null https://packages.microsoft.com; echo "kod: $?"` – `kod: 0`;
- on Debian as another account (e.g. `nobody`): the same command with `sudo -u nobody` – an error and a code other than 0
  (usually `28` – timeout);
- on Debian as the `openwec` account:
  `sudo -u openwec curl -sS -m 10 -o /dev/null https://packages.microsoft.com; echo "kod: $?"` – an error and a code
  other than 0 (e.g. `6` – no DNS response, or `28` – timeout).

### 3.4 DNS and time

**Where:** Debian. **What and why:**

- **DNS.** The server queries only the DNS of the AD domain controllers – otherwise it will not find the DC names or its
  own record.
- **Time.** Kerberos rejects authentication when clocks differ by more than **5 minutes** – then everything stops
  working: LDAP, SYSVOL, the API, WEF. The server synchronizes time with the domain controllers (like domain computers).
  The same time is also needed to match events from the DC logs to changes. Watchdog already warns at a difference of
  60 s and reports an error from 240 s.
- **Windows time servers** usually report a large "dispersion" (root dispersion – several seconds), and chrony by
  default rejects a source whose total error exceeds 3 s. That is why we raise this limit (`maxdistance 16`, the
  recommendation from the chrony FAQ for Windows servers) – in a separate file in `/etc/chrony/conf.d`, because
  `sources.d` files may contain only sources.

```bash
ls -l /etc/resolv.conf       # should be a regular file; if it is a symlink (resolvconf, systemd-resolved) – set DNS there
cp -a /etc/resolv.conf /etc/resolv.conf.orig; cp -a /etc/chrony/chrony.conf /etc/chrony/chrony.conf.orig   # copies (8.1)
cat > /etc/resolv.conf <<'EOF'
search contoso.com
nameserver 10.0.1.10
nameserver 10.0.1.11
EOF

sed -i 's/^pool /#pool /' /etc/chrony/chrony.conf          # disable the public Debian sources
cat > /etc/chrony/sources.d/ad-dcs.sources <<'EOF'
server DC01.contoso.com iburst
server DC02.contoso.com iburst
EOF
printf 'maxdistance 16.0\n' > /etc/chrony/conf.d/admng-ad.conf   # Windows domain controllers have a large time dispersion
systemctl restart chrony
```

**Check:**

```bash
getent hosts DC01.contoso.com DC02.contoso.com
host -t SRV _kerberos._tcp.contoso.com
host admng-wd.contoso.com; host 10.0.1.25
chronyc -N sources; chronyc tracking | grep -E 'Leap status|System time'
chronyc ntpdata 10.0.1.10 | grep -E 'Root (delay|dispersion)'
```

Expected: addresses 10.0.1.10 and 10.0.1.11; SRV records of both domain controllers on port 88;
`admng-wd.contoso.com has address 10.0.1.25` (without "is an alias for") and
`25.1.0.10.in-addr.arpa domain name pointer admng-wd.contoso.com.`; in `chronyc -N sources` the line
`^* DC01.contoso.com` (asterisk = the selected source; `-N` shows names from the sources file, not from reverse DNS),
`Leap status : Normal`, an offset on the order of milliseconds; the controller's `Root dispersion` – usually from a
fraction of a second to a few seconds. When a `?`, `x` or `Leap status : Not synchronised` is shown next to a
controller:

- `Root dispersion` (plus half of `Root delay`) from the last command close to 16 s or greater, or the file
  `/etc/chrony/conf.d/admng-ad.conf` missing – chrony rejects the source as too inaccurate; check the file and
  `systemctl restart chrony`;
- the PDC emulator has no time source of its own (the large dispersion grows with time) – fix it (1.4).

### 3.5 Kerberos client and private configuration files

**Where:** Debian. **What and why:** the service, OpenWEC and the keytab tool use their own files in
`/etc/admng-watchdog` (the system `/etc/krb5.conf` and `/etc/ldap/ldap.conf` are left untouched). The package (3.1)
installed them with example names (`contoso.com`): overwrite `krb5.conf` and `smb-client.conf` with the block below –
after replacing the domain, realm and controller names with your own (the "Replace with your own" table). `ldap.conf`
contains no names and stays unchanged (its lines `SASL_NOCANON on`, `SASL_SECPROPS minssf=56,noanonymous,noplain`,
`REFERRALS off`, `TLS_REQCERT demand` are explained in the table below; the commented-out lines concern LDAPS options).
These are the package's configuration files (conffiles): when it is updated, `dpkg` asks about them only if the new
package version changes their content – answer `N` then, to keep your own.

```bash
cat > /etc/admng-watchdog/krb5.conf <<'EOF'
[libdefaults]
    default_realm = CONTOSO.COM
    dns_lookup_kdc = false
    dns_lookup_realm = false
    dns_canonicalize_hostname = false
    rdns = false
    forwardable = false
    udp_preference_limit = 0
    allow_weak_crypto = false
    permitted_enctypes = aes256-cts-hmac-sha1-96 aes128-cts-hmac-sha1-96
    default_tgs_enctypes = aes256-cts-hmac-sha1-96 aes128-cts-hmac-sha1-96
    default_tkt_enctypes = aes256-cts-hmac-sha1-96 aes128-cts-hmac-sha1-96

[realms]
    CONTOSO.COM = {
        kdc = DC01.contoso.com
        kdc = DC02.contoso.com
        primary_kdc = DC01.contoso.com
        kpasswd_server = DC01.contoso.com
        admin_server = DC01.contoso.com
    }

[domain_realm]
    .contoso.com = CONTOSO.COM
    contoso.com = CONTOSO.COM
EOF

cat > /etc/admng-watchdog/smb-client.conf <<'EOF'
[global]
    workgroup = CONTOSO
    realm = CONTOSO.COM
    security = ads
    client min protocol = SMB3_00
    client signing = required
    client ipc signing = required
    client smb encrypt = desired
    client use kerberos = required
    disable netbios = yes
    name resolve order = host
    lock directory = /run/admng-watchdog/samba
    state directory = /run/admng-watchdog/samba
    cache directory = /run/admng-watchdog/samba
    private dir = /run/admng-watchdog/samba
    log level = 0
EOF
chmod 0644 /etc/admng-watchdog/*.conf
```

The most important settings and why:

| Setting | Why |
|---|---|
| `permitted_enctypes` … AES only, `allow_weak_crypto = false` | RC4 and DES are disabled – consistent with the service account (0x18) and with the phase-out of RC4 on the domain controllers |
| `dns_lookup_kdc = false` + explicit `kdc` | the controllers are hard-coded – replacing SRV records in DNS will not redirect authentication. When you add or retire a DC, update the list |
| `rdns = false`, `dns_canonicalize_hostname = false` | service names (SPNs) are built from the names you give, not from reverse DNS – a wrong PTR record breaks nothing |
| `primary_kdc`, `kpasswd_server` = PDC emulator | the password change goes to the PDC, and with a freshly changed password the client asks the PDC before the change reaches DC02. After moving the PDC role, change these lines (the service will warn) |
| `udp_preference_limit = 0` | Kerberos always over TCP – AD tickets are sometimes too large for UDP |
| `forwardable = false` | service tickets are not forwardable (and the "cannot be delegated" flag blocks this anyway) |
| `SASL_SECPROPS minssf=56` | the LDAP connection **must** be encrypted – without it the bind will fail (instead of silently working without encryption) |
| `SASL_NOCANON on` | libldap does not replace the DC name with the name from reverse DNS |
| `client signing = required`, `client use kerberos = required` | SYSVOL is read only with SMB signing and only with Kerberos (no NTLM) |

**Check** – authenticate with a test account (this checks DNS, port 88, time and AES before the service account
exists); the ticket goes to memory and disappears when the command ends:

```bash
KRB5_CONFIG=/etc/admng-watchdog/krb5.conf KRB5CCNAME=MEMORY:test kinit -V jan.kowalski@CONTOSO.COM
```

Expected: after entering the password `Authenticated to Kerberos v5`. Errors – chapter 9.1.

### 3.6 Keytab of the `ADMNG-WD$` account – creation and tests

> **In short.** What it changes: the password of the `ADMNG-WD$` account in AD (one-time → random, 120 characters that
> nobody knows); it creates the file `/etc/admng-watchdog/admng-wd.keytab`. Where: Debian, root, right after 2.5. How
> long: a few minutes including the tests. Result: a keytab with six entries, tests 1–3 pass. Rollback: removing the
> keytab (8.1) and disabling the account (8.2).

**Where:** Debian, right after 2.5 (you have the one-time password). **What and why:** we replace the one-time password
with a random 120-character password that nobody knows, and write the keytab – a file with the account's Kerberos keys.
This is done by the `admng-watchdog-keytab` tool from the package (3.1): you type the one-time password without echo, the
new password exists only in the tool's memory and is zeroed right after the keys are derived; only the keys reach the
disk.

```bash
admng-watchdog-keytab init          # asks for the one-time password from 2.5
admng-watchdog-keytab status
```

`init` takes the `ADMNG-WD$` account and the realm from `default_realm` in `/etc/admng-watchdog/krb5.conf`, and the DNS
name – from the account name and the realm (`admng-wd.contoso.com`); with a different account name or a DNS domain
different from the realm, pass `--principal 'NAME$@REALM'` and `--fqdn name.domain`. The tool:

1. changes the password with the Kerberos protocol on the PDC emulator (`primary_kdc`/`kpasswd_server` from `krb5.conf`,
   port 464);
2. writes a keytab with three names – first `ADMNG-WD$@CONTOSO.COM`, then `HTTP/admng-wd.contoso.com` and
   `host/admng-wd.contoso.com` – in two AES types, with the proper "salt" and key version number (kvno); the new file is
   created next to the old one (`root:admng-keytab 640`) and replaces it in one move, and the keys of the previous
   version from the existing keytab are kept;
3. checks the new keys on every controller from `krb5.conf` (waits for replication up to 15 minutes) and records the
   state in `/var/lib/admng-watchdog-keytab/status.json` (no secrets: name, kvno, time of the change and of attempts,
   result, date of the next change).

Expected: `init` ends with code 0, and `status` shows `"result": "ok"`, `"kvno"` (e.g. `3`) and `"nextDue"` in 30 days.
Other results:

| Result (`result`, `errorCode`) | Meaning | What to do |
|---|---|---|
| `otpActive` (`KRB5_KPASSWD_SOFTERROR`; code 0 with a warning) | the controller rejected the change because of password policy – usually the minimum password age counted from setting the one-time password. The keytab was created from the one-time password and works | nothing – the timer (3.7) tries the change on every run as long as the state is `otpActive`; the next day you can force it: `systemctl start admng-watchdog-keytab.service`. Until then a human knows the password – remove it from the password manager only when `status` shows `ok` |
| `changeFailed` (code 2) | the change was rejected (e.g. `KRB5_KPASSWD_ACCESSDENIED`) or port 464 does not respond | the cause – 9.1; then `init` again with the **same** one-time password (without a new reset on the PAW) |
| `keytabWriteFailed` (code 2) | the password in AD was changed, but the new keytab could not be written | the tool's message; a new one-time password on the PAW (3.7, "Repair") and `init` |
| `replicationPending` (code 0) | the new keys have not yet reached every DC | after a few minutes `admng-watchdog-keytab verify` |
| `verifyFailed` (code 2) | the keytab was written, but the check on the PDC emulator failed | `admng-watchdog-keytab verify`, the tests below, 9.1 |

If the tool does not work, the same result is given by the emergency procedure with the MIT Kerberos tools – at the end
of this section.

#### Keytab permissions

Only root and members of the `admng-keytab` group (the `admng-watchdog` and `openwec` accounts – packages, 3.1) read
the file:

```bash
stat -c '%U:%G %a %n' /etc/admng-watchdog/admng-wd.keytab
```

Expected: `root:admng-keytab 640 /etc/admng-watchdog/admng-wd.keytab`.

#### Tests

You run the tests in one root shell, with the private Kerberos configuration and tickets only in memory (tmpfs). If you
open a new session (or the connection drops), start with these three commands – otherwise the Kerberos tools will use
the system `/etc/krb5.conf` and show misleading errors:

```bash
export KRB5_CONFIG=/etc/admng-watchdog/krb5.conf
export KRB5CCNAME=FILE:/dev/shm/admng-test-cc          # tickets in memory (tmpfs), not on disk
trap 'kdestroy 2>/dev/null' EXIT                       # tickets disappear when the shell exits
```

**Test 1 – contents and authentication with the key:**

```bash
klist -k -t -e /etc/admng-watchdog/admng-wd.keytab
kinit -f -k -t /etc/admng-watchdog/admng-wd.keytab 'ADMNG-WD$@CONTOSO.COM'
klist -e -f
kvno -k /etc/admng-watchdog/admng-wd.keytab HTTP/admng-wd.contoso.com host/admng-wd.contoso.com
```

Expected (kvno 3 as an example):

```text
KVNO Timestamp         Principal
---- ----------------- --------------------------------------------------------
   3 …                 ADMNG-WD$@CONTOSO.COM (aes256-cts-hmac-sha1-96)
   3 …                 ADMNG-WD$@CONTOSO.COM (aes128-cts-hmac-sha1-96)
   3 …                 HTTP/admng-wd.contoso.com@CONTOSO.COM (aes256-cts-hmac-sha1-96)
   3 …                 HTTP/admng-wd.contoso.com@CONTOSO.COM (aes128-cts-hmac-sha1-96)
   3 …                 host/admng-wd.contoso.com@CONTOSO.COM (aes256-cts-hmac-sha1-96)
   3 …                 host/admng-wd.contoso.com@CONTOSO.COM (aes128-cts-hmac-sha1-96)
Default principal: ADMNG-WD$@CONTOSO.COM
…  krbtgt/CONTOSO.COM@CONTOSO.COM
        renew until …, Flags: RIA
        Etype (skey, tkt): aes256-cts-hmac-sha1-96, aes256-cts-hmac-sha1-96
HTTP/admng-wd.contoso.com@CONTOSO.COM: kvno = 3, keytab entry valid
host/admng-wd.contoso.com@CONTOSO.COM: kvno = 3, keytab entry valid
```

- `keytab entry valid` – a ticket issued by the KDC can be decrypted with the key from the keytab: the salt, kvno and
  encryption types match.
- The ticket flags **do not include `F`** (forwardable), although `kinit -f` asked for it – this proves that the flag
  "account is sensitive and cannot be delegated" works.
- `aes256-cts-hmac-sha1-96` – the account uses AES.
- After a password change (3.7) the keytab has two key versions (current and previous; after the emergency procedure –
  all the previous ones) – the first line is still `ADMNG-WD$@CONTOSO.COM`.

**Test 2 – LDAP with signing and encryption.** The `KRB5_CLIENT_KTNAME` variable lets the Kerberos library fetch the
ticket from the keytab itself – this is how the service works:

```bash
kdestroy
export KRB5_CLIENT_KTNAME=FILE:/etc/admng-watchdog/admng-wd.keytab
export LDAPCONF=/etc/admng-watchdog/ldap.conf
ldapsearch -Y GSSAPI -H ldap://DC01.contoso.com -b 'DC=contoso,DC=com' -s base '(objectClass=*)' distinguishedName
ldapsearch -Q -LLL -Y GSSAPI -H ldap://DC01.contoso.com -b 'DC=contoso,DC=com' '(sAMAccountName=ADMNG-WD$)' msDS-KeyVersionNumber
```

Expected:

```text
SASL/GSSAPI authentication started
SASL username: ADMNG-WD$@CONTOSO.COM
SASL SSF: 256
SASL data security layer installed.
…
result: 0 Success

dn: CN=ADMNG-WD,OU=Service Accounts,OU=Tier 0,OU=Admin,DC=contoso,DC=com
msDS-KeyVersionNumber: 3
```

- `SASL SSF: 256` – the connection is encrypted (meets the LDAP signing requirement on Windows Server 2025).
- `SASL username: ADMNG-WD$@CONTOSO.COM` – the identity from the first keytab entry.
- `msDS-KeyVersionNumber` equal to the newest kvno in the keytab – the keytab is current.
- Reading `tokenGroups` (operator roles in the API) and deleted objects you will check after granting the rights in 2.6 –
  the test is there.

Repeat the first command with `-H ldap://DC02.contoso.com` (after the password has replicated).

**Test 3 – reading SYSVOL.** The block is self-contained (it has its own `export`), so you can repeat it in a new
session (2.7, 9.4), also when the service is already running: Samba's state goes to a temporary directory in `/dev/shm`,
not to the service directory `/run/admng-watchdog`.

```bash
export KRB5_CONFIG=/etc/admng-watchdog/krb5.conf KRB5CCNAME=FILE:/dev/shm/admng-test-cc
sed 's#/run/admng-watchdog/samba#/dev/shm/admng-test-samba#' /etc/admng-watchdog/smb-client.conf > /dev/shm/admng-test-smb.conf
install -d -m 0700 /dev/shm/admng-test-samba
kinit -k -t /etc/admng-watchdog/admng-wd.keytab 'ADMNG-WD$@CONTOSO.COM'   # smbclient will not fetch a ticket from the keytab itself
smbclient //DC01.contoso.com/SYSVOL -s /dev/shm/admng-test-smb.conf --use-kerberos=required -N -m SMB3 \
    -c 'ls contoso.com/Policies/*'
echo "kod: $?"
kdestroy; rm -rf /dev/shm/admng-test-samba /dev/shm/admng-test-smb.conf
```

Expected: a list of GPO folders, including `{31B2F340-016D-11D2-945F-00C04FB984F9}` (Default Domain Policy) and
`{6AC1786C-016F-11D2-945F-00C04FB984F9}` (Default Domain Controllers Policy), and `kod: 0`. `-N` is mandatory (without
it `smbclient` waits for a password). Without a TGT in the credential cache (the `kinit` command in the block),
`smbclient` ends with the error `NT_STATUS_INVALID_PARAMETER` – 9.4.

**Cleanup after the tests:**

```bash
kdestroy 2>/dev/null; unset KRB5_CLIENT_KTNAME LDAPCONF KRB5CCNAME KRB5_CONFIG; trap - EXIT
```

**Check on the AD side** (PAW):

```powershell
Get-ADComputer ADMNG-WD -Properties pwdLastSet, msDS-KeyVersionNumber |
    Format-List Name, @{ n = 'pwdLastSet'; e = { [datetime]::FromFileTime($_.pwdLastSet) } }, msDS-KeyVersionNumber
```

Expected: `pwdLastSet` from a few minutes ago (the one-time password no longer works) and `msDS-KeyVersionNumber` equal to
the kvno from the keytab. When the forest has an Enterprise CA, also run the "Certificates issued to the account" block
from 2.5 – no certificate may be issued to the account (we do not block enrollment, and the one-time password worked
for a few minutes).

#### Emergency procedure – keytab with the MIT Kerberos tools

Only when `admng-watchdog-keytab` does not work (report it to the team). The password change, the keytab and tests 1 and
3 were verified by us in the lab (Samba AD); test 2 (LDAP with GSSAPI encryption) – on a test OpenLDAP server and in the
Samba lab with an `ldap/` SPN. On Windows controllers the pilot will confirm them.

The keytab must contain three names (the account and two SPNs used by the API and OpenWEC), each in two AES encryption
types, with the same "salt". For computer accounts in AD the salt is: `REALM` + `host` + the account name in lowercase
without `$` + `.` + the domain in lowercase, that is `CONTOSO.COMhostadmng-wd.contoso.com`. A wrong salt produces a
keytab that looks correct but does not work – that is why the tests are mandatory. The order of entries also matters:
the first entry is the account name `ADMNG-WD$` – the Kerberos library takes its identity from it when the service logs
in with the key from the keytab (test 2). The block below preserves this order.

If the keytab already exists, the block first reads the current keys – they stay in the new file, so tickets issued
earlier keep working. The block is self-contained – it sets the same variables as the tests above.

```bash
umask 077                                              # new files only for root (until the end of this session)
export KRB5_CONFIG=/etc/admng-watchdog/krb5.conf
export KRB5CCNAME=FILE:/dev/shm/admng-test-cc          # tickets in memory (tmpfs), not on disk
trap 'kdestroy 2>/dev/null' EXIT                       # tickets disappear when the shell exits
P='ADMNG-WD$@CONTOSO.COM'
KT=/etc/admng-watchdog/admng-wd.keytab
SALT='CONTOSO.COMhostadmng-wd.contoso.com'

read -rs -p 'Hasło jednorazowe: ' OTP; echo
NEW=$(openssl rand -base64 90 | tr -d '\n')            # 120 random characters – only in shell memory

# 1) password change with the Kerberos protocol (port 464 on DC01) – old password, new, new;
#    on refusal the keytab is created from the one-time password (below: "When kpasswd refuses")
if ! printf '%s\n%s\n%s\n' "$OTP" "$NEW" "$NEW" | kpasswd "$P"; then
    echo 'UWAGA: kpasswd odmówił – keytab powstanie z hasła jednorazowego'; NEW="$OTP"
fi

# 2) key version number (kvno) – from a ticket to its own host/ SPN
KVNO=
if printf '%s\n' "$NEW" | kinit "$P" >/dev/null; then
    KVNO=$(kvno host/admng-wd.contoso.com | sed -n 's/.*kvno = \([0-9]*\).*/\1/p'); kdestroy
fi
echo "kvno: ${KVNO:-BRAK}"

# 3) keytab: first the current keys (if the file already exists), then 3 names × 2 AES types – to a temporary file
# 4) permissions, then replacing the file in one move
if [ -n "$KVNO" ]; then
  rm -f "$KT.new"
  { [ -f "$KT" ] && printf 'rkt %s\n' "$KT"
    for pr in "$P" 'HTTP/admng-wd.contoso.com@CONTOSO.COM' 'host/admng-wd.contoso.com@CONTOSO.COM'; do
      for e in aes256-cts-hmac-sha1-96 aes128-cts-hmac-sha1-96; do
        printf 'addent -password -p %s -k %s -e %s -s %s\n%s\n' "$pr" "$KVNO" "$e" "$SALT" "$NEW"
      done
    done
    printf 'wkt %s.new\nquit\n' "$KT"; } | ktutil >/dev/null
  chown root:admng-keytab "$KT.new"; chmod 0640 "$KT.new"; mv "$KT.new" "$KT"
else
  echo 'Keytab NIE został zapisany – zobacz komunikat kpasswd/kinit powyżej'
fi
unset NEW OTP
```

Expected: `kpasswd` ends with the line `Password changed.`, and `kvno:` shows a number (e.g. `3`). `kvno: BRAK` means the keytab
was not created – the cause is shown by the `kpasswd` or `kinit` message (chapter 9.1). The line `UWAGA: kpasswd
odmówił…` – see below.

**When `kpasswd` refuses**

The block then writes the keytab from the **one-time password** – the service and the tests will work, but a human knows
the password. What next depends on the `kpasswd` message above the `UWAGA` line:

- `Password change rejected` – the minimum password age in the domain counts from setting the one-time password. The
  Tier 0 administrator keeps the one-time password in the password manager until the next day. The next day (when the
  minimum password age has passed) run the whole block on Debian again with the **same** one-time password – **without**
  a new reset on the PAW, because the reset starts the minimum password age again. The block will keep the current keys.
  Then the administrator removes the password from the manager. (The `admng-watchdog-keytab` tool does the same
  automatically – the `otpActive` state above.)
- `Access denied` – a different situation, chapter 9.1.
- another error (e.g. no connection to port 464 on DC01) – remove the cause (9.1) and run the whole block again with
  the same one-time password.

### 3.7 Changing the service account password every 30 days

**Where:** Debian. **What and why:** changing the password regularly limits the value of a stolen keytab – after a
change the old keys stop working (at the latest after the next change). It does not, however, invalidate a certificate
that someone holding the keytab managed to request from the certification authority – we do not block that (accepted
risk, 2.5 "Certificates and the service account"); after a keytab leak, the only remedy is to disable the account,
revoke the certificates and wait until the domain controllers fetch the new revocation list (2.5, "If the keytab may
have leaked").

The `admng-watchdog` package installs the following for this:

- the `admng-watchdog-keytab` tool (`/usr/sbin/admng-watchdog-keytab`) with the commands `init`, `rotate
  [--if-older-than 30d]`, `verify`, `status` (exit codes: 0 – OK, 1 – invalid invocation, 2 – error);
- the unit `admng-watchdog-keytab.service` (runs as root, with systemd restrictions; it executes
  `admng-watchdog-keytab rotate --if-older-than 30d` and does nothing as long as there is no keytab) and the timer
  `admng-watchdog-keytab.timer` – daily at about 04:20 (with a random delay of up to 30 minutes; a missed run is made up
  after the server starts); the password is changed only when it is more than 30 days old – or on every run for as long
  as the keytab comes from a one-time password (the `otpActive` state, 3.6);
- the state file `/var/lib/admng-watchdog-keytab/status.json` (`root:admng-watchdog 640`, no secrets): account name,
  key version, date of the last change and attempt, result (`ok`, `otpActive`, `changeFailed`, `keytabWriteFailed`,
  `replicationPending`, `verifyFailed`) with the error code, and the date of the next change. The service reads it and
  shows it in the Status → Service account tab; after its own scheduled password change (kvno matching `status.json`)
  it does not raise the `watchdog-self` alert.

During a change the tool: changes the password on the PDC, writes the new keytab next to the old one and swaps it in
one move (readers never see half a file), keeps the keys of the current and the previous version, and checks the new
keys on every DC (it waits up to 15 minutes for replication). `init` works the same way during a repair (new one-time
password): the keys of the existing version from the current keytab are kept, so tickets issued earlier keep working.
The services read the keytab on every authentication – no restart is needed.

Manual run and verification:

```bash
systemctl list-timers admng-watchdog-keytab.timer
systemctl start admng-watchdog-keytab.service       # same as the timer does: change only if the password is > 30 days old
journalctl -u admng-watchdog-keytab -n 20 --no-pager
admng-watchdog-keytab status
admng-watchdog-keytab verify
klist -k /etc/admng-watchdog/admng-wd.keytab | awk 'NR>3 {print $1}' | sort -u    # key versions in the keytab
```

Expected: a timer with the date of the next run; `status` with the result `ok`; `verify` ends with exit code 0 (order
of entries, authentication on every DC, kvno of a fresh ticket); at most two key versions in the keytab, the newer of
which equals `msDS-KeyVersionNumber` (PAW: `Get-ADComputer ADMNG-WD -Properties msDS-KeyVersionNumber`, or `ldapsearch`
from 3.6). To force a change before 30 days have passed (e.g. in the pilot): `admng-watchdog-keytab rotate
--if-older-than 0` – the change works like an automatic one, without the `watchdog-self` alert.

**Repair with a new one-time password** – a keytab that no longer matches AD (e.g. after restoring the machine from a
backup, `keytabWriteFailed`), or a password changed in AD outside Debian:

1. PAW – a new one-time password (generate `$plain` and `$otp` as in 2.5):
   `Set-ADAccountPassword -Identity 'ADMNG-WD$' -Reset -NewPassword $otp`. If the account is disabled (2.5, "If the
   keytab may have leaked"), enable it right after the reset: `Enable-ADAccount 'ADMNG-WD$'` – a disabled account will
   not change its password (`Client's credentials have been revoked`).
2. Debian – `admng-watchdog-keytab init` with the new one-time password (3.6; with `otpActive` – as described there).
   The keys of the existing version from the current keytab are kept.
3. Verification: `admng-watchdog-keytab status` – `ok`; `klist -k /etc/admng-watchdog/admng-wd.keytab` shows the old
   and the new version. The new one is **2 higher** – a password reset by an administrator and the change to a random
   password each raise the kvno by one. Tests 1–3 from 3.6 pass. Watchdog reports a password reset by an administrator
   as a critical `watchdog-self` alert – this is expected.

### 3.8 TLS certificate for the API

**Where:** Debian and PAW. **What and why:** AD Manager connects to the service over HTTPS. The certificate must contain
the name `admng-wd.contoso.com` (SAN) and the purpose "Server Authentication". The service reads it from PEM files:
`/etc/admng-watchdog/tls/admng-wd.crt` (the certificate, then intermediate certificates) and
`/etc/admng-watchdog/tls/admng-wd.key` (the private key). The service accepts only TLS 1.2 and 1.3.

#### Variant A – certificate from the corporate CA (when 2.1 found an authority with a WebServer template)

The private key is created on the server and never leaves it; only the request (CSR) goes to the CA. The first block
overwrites the key of the self-signed certificate that the package generated during installation (3.1) – that is fine
as long as the service is not running yet (chapter 5). When the service already runs on a self-signed certificate and
you are switching to a CA certificate, perform the "Renewal" below instead (the new pair is created next to the
running one).

Debian:

```bash
cd /etc/admng-watchdog/tls
umask 077
openssl req -new -newkey rsa:3072 -nodes -keyout admng-wd.key -out admng-wd.csr \
    -subj '/CN=admng-wd.contoso.com' -addext 'subjectAltName=DNS:admng-wd.contoso.com' -addext 'extendedKeyUsage=serverAuth'
cp admng-wd.csr /home/adm-jan/ && chown adm-jan /home/adm-jan/admng-wd.csr
```

PAW (the request is not secret – you copy it with a plain `scp`). `certreq` and `certutil` without `-f` do not
overwrite an existing file – the first command removes old files from a previous issuance:

```powershell
Remove-Item admng-wd.cer, chain.p7b -ErrorAction SilentlyContinue
scp adm-jan@admng-wd.contoso.com:admng-wd.csr .
certreq -submit -f -attrib "CertificateTemplate:WebServer" -config "CA01.contoso.com\Contoso-Issuing-CA" admng-wd.csr admng-wd.cer
certutil -f -config "CA01.contoso.com\Contoso-Issuing-CA" -ca.chain chain.p7b
scp admng-wd.cer chain.p7b adm-jan@admng-wd.contoso.com:
Remove-Item admng-wd.cer, chain.p7b, admng-wd.csr
```

If `certreq` reports a "pending" request (the template requires approval; `scp` and `Remove-Item` will then report a
missing `admng-wd.cer` – this is expected), retrieve the certificate after approval (`<RequestId>` from the `certreq`
message), and then run the last three commands of the block above:
`certreq -retrieve -f -config "CA01.contoso.com\Contoso-Issuing-CA" <RequestId> admng-wd.cer`.

Debian:

```bash
cd /etc/admng-watchdog/tls
openssl x509 -in /home/adm-jan/admng-wd.cer -out admng-wd.pem 2>/dev/null || openssl x509 -inform DER -in /home/adm-jan/admng-wd.cer -out admng-wd.pem
openssl pkcs7 -inform DER -in /home/adm-jan/chain.p7b -print_certs -out chain.pem 2>/dev/null || \
    openssl pkcs7 -inform PEM -in /home/adm-jan/chain.p7b -print_certs -out chain.pem
cat admng-wd.pem chain.pem > admng-wd.crt            # server certificate first, then the authorities
chmod 0644 admng-wd.crt; chown root:admng-watchdog admng-wd.key; chmod 0640 admng-wd.key
rm admng-wd.csr admng-wd.pem /home/adm-jan/admng-wd.c* /home/adm-jan/chain.p7b
```

The service must be able to read the key (`root:admng-watchdog 640` – the block above sets this; a key readable by
everyone stops the service from starting with exit code 4, 5.3). During installation and upgrade the package sets only
the `tls/` directory (`root:admng-watchdog 750`), and when a certificate is already in place it does not generate its
own.

**Verification:**

```bash
cd /etc/admng-watchdog/tls
openssl verify -CAfile chain.pem admng-wd.crt
openssl x509 -in admng-wd.crt -noout -subject -enddate -ext subjectAltName,extendedKeyUsage
a=$(openssl x509 -in admng-wd.crt -noout -pubkey) && b=$(openssl pkey -in admng-wd.key -pubout) &&
    [ -n "$a" ] && [ "$a" = "$b" ] && echo 'klucz pasuje'
stat -c '%U:%G %a %n' /etc/admng-watchdog/tls /etc/admng-watchdog/tls/*     # after installing the package
```

Expected: `admng-wd.crt: OK`; `subject=CN=admng-wd.contoso.com` (OpenSSL 3.0 on Debian 12 prints
`subject=CN = admng-wd.contoso.com`), `DNS:admng-wd.contoso.com`, `TLS Web Server
Authentication`; `klucz pasuje` ("key matches"; if this line is missing, the key does not match or one of the files
cannot be read); after installing the package the directory is `root:admng-watchdog 750`, `admng-wd.key` is
`root:admng-watchdog 640`, `admng-wd.crt` is `… 644`. Domain workstations trust the corporate CA, so AD Manager will
connect without any additional prompts.

**Renewal** (before the `notAfter` date, with a margin for the request approval). Do **not** repeat the first block of
variant A: it would overwrite the working key before the authority issues a new certificate (the request may wait days
for approval), and if the request were rejected you could not go back to the still-valid certificate. The new key and
certificate are created next to the working ones and replace them only after verification.

Debian:

```bash
cd /etc/admng-watchdog/tls
umask 077
openssl req -new -newkey rsa:3072 -nodes -keyout admng-wd.new.key -out admng-wd.new.csr \
    -subj '/CN=admng-wd.contoso.com' -addext 'subjectAltName=DNS:admng-wd.contoso.com' -addext 'extendedKeyUsage=serverAuth'
cp admng-wd.new.csr /home/adm-jan/ && chown adm-jan /home/adm-jan/admng-wd.new.csr
```

PAW (as with the first issuance: old files disappear first, `-f` allows overwriting a file; for a "pending" request –
`certreq -retrieve -f …` as above):

```powershell
Remove-Item admng-wd.cer, chain.p7b -ErrorAction SilentlyContinue
scp adm-jan@admng-wd.contoso.com:admng-wd.new.csr .
certreq -submit -f -attrib "CertificateTemplate:WebServer" -config "CA01.contoso.com\Contoso-Issuing-CA" admng-wd.new.csr admng-wd.cer
certutil -f -config "CA01.contoso.com\Contoso-Issuing-CA" -ca.chain chain.p7b
scp admng-wd.cer chain.p7b adm-jan@admng-wd.contoso.com:
Remove-Item admng-wd.cer, chain.p7b, admng-wd.new.csr
```

Debian – the new certificate next to the working one and a check of the pair:

```bash
cd /etc/admng-watchdog/tls
umask 077
openssl x509 -in /home/adm-jan/admng-wd.cer -out admng-wd.new.pem 2>/dev/null || openssl x509 -inform DER -in /home/adm-jan/admng-wd.cer -out admng-wd.new.pem
openssl pkcs7 -inform DER -in /home/adm-jan/chain.p7b -print_certs -out chain.pem 2>/dev/null || \
    openssl pkcs7 -inform PEM -in /home/adm-jan/chain.p7b -print_certs -out chain.pem
cat admng-wd.new.pem chain.pem > admng-wd.new.crt
openssl verify -CAfile chain.pem admng-wd.new.crt
a=$(openssl x509 -in admng-wd.new.crt -noout -pubkey) && b=$(openssl pkey -in admng-wd.new.key -pubout) &&
    [ -n "$a" ] && [ "$a" = "$b" ] && echo 'klucz pasuje'
```

Only when you see `admng-wd.new.crt: OK` and `klucz pasuje`, swap the files (owner and mode of the key – same as the
existing one). A missing `klucz pasuje` together with `OK` usually means that the old certificate got to the server (e.g.
from an old file on the PAW) – repeat the PAW block:

```bash
chown --reference=admng-wd.key admng-wd.new.key && chmod --reference=admng-wd.key admng-wd.new.key &&
    chmod 0644 admng-wd.new.crt && mv -f admng-wd.new.key admng-wd.key && mv -f admng-wd.new.crt admng-wd.crt
rm -f admng-wd.new.csr admng-wd.new.pem /home/adm-jan/admng-wd.new.csr /home/adm-jan/admng-wd.cer /home/adm-jan/chain.p7b
```

Then perform the "Verification" above. If the authority rejects the request, nothing changes – delete the
`admng-wd.new.*` files. (We verified the procedure in the lab with an authority simulated by `openssl`.) The service
loads the replaced files by itself; should it encounter a key that does not match the certificate (e.g. between two
file renames), it stays with the current certificate, reports a `certificate.key` error and accepts the new pair at the
next read (it checks the files 2 s after the last change and every 10 minutes; no restart is needed).

#### Variant B – certificate generated on the server (when there is no CA)

You do not have to do anything: when there was no certificate in `/etc/admng-watchdog/tls/`, the package generated a
self-signed certificate during installation (3.1) (ECDSA P-256, valid for 825 days, name taken from `hostname --fqdn` –
which is why 3.1 sets `/etc/hosts`; files `admng-wd.crt` and `admng-wd.key`) and printed its SHA-256 fingerprint. The
fingerprint is also shown at any time by `admng-watchdog cert-fingerprint` (in the form `3F:2A:9C:…` – 32 pairs of
hexadecimal digits) and by entry 1008 in the log at every service start. Renewal (before the 825 days pass or when the
server name has changed): `admng-watchdog cert-renew-self-signed` – the service accepts the new certificate within a few
seconds, and AD Manager will ask for confirmation of the new fingerprint.

Workstations do not trust such a certificate, so on the **first** connection AD Manager shows a window with the
certificate fingerprint (groups of 4 hexadecimal digits). Compare it digit by digit with the output of
`admng-watchdog cert-fingerprint` on the server (over SSH; colons and the division into groups do not matter) and only
then click "I trust this certificate" – AD Manager will remember the fingerprint. If the certificate changes (renewal,
replacement), AD Manager does **not** accept it silently: it shows the old and the new fingerprint and asks for
confirmation again. A name mismatch is never accepted. (6.1)

You can also check the fingerprint of any certificate without the package:
`openssl x509 -in /etc/admng-watchdog/tls/admng-wd.crt -noout -fingerprint -sha256`.

### 3.9 Backups

**What the service does:** every day it makes a local copy of the database (a consistent copy of the SQLite database
file) to `/var/lib/admng-watchdog/backup/` and keeps the **7** latest (when `/var` lacks space for another copy, it
skips it and reports a `storage.backupSkipped` error). These copies protect against a logical error (e.g. a corrupted
migration), but **not** against the loss of the disk or the machine – they are on the same disk.

**Recommendation (just one, but an important one):** the change history is meant to span 3 years, so make a **backup of
the entire virtual machine** at the hypervisor level – together with the vTPM state, if you use it. Such a backup
contains the service directory, the keytab and the data – it is a Tier 0 asset: only Tier 0 administrators have access
to the backup system (just as to the virtualization platform – 3.1), and the backups should be encrypted. In the "host
key" variant (without TPM2) a disk backup also contains the key that decrypts the channel secrets.

What a restore must include:

| Item | Where | Notes |
|---|---|---|
| Database and copies | `/var/lib/admng-watchdog/` (`watchdog.db`, `backup/`) | 3-year history |
| Channel secret keys | `/var/lib/admng-watchdog/keys/` | encrypted with the key from the systemd credential |
| systemd credential with the key | `/etc/admng-watchdog/credentials/dp-key.cred` | you can decrypt it only with the same host key (the file `/var/lib/systemd/credential.secret` – it is in the disk backup) and – in the TPM2 variant – the same vTPM; it does not depend on the boot state (Secure Boot, UEFI updates) |
| Configuration, certificate, keytab | `/etc/admng-watchdog/` | the keytab may be out of date if a password change took place after the backup was made |

Scenarios:

- **Restoring the same virtual machine** (disk + vTPM): everything works. Just check the keytab – the Status →
  Service account tab, or on Debian:

  ```bash
  export KRB5_CONFIG=/etc/admng-watchdog/krb5.conf KRB5CCNAME=FILE:/dev/shm/admng-test-cc
  kinit -k -t /etc/admng-watchdog/admng-wd.keytab 'ADMNG-WD$@CONTOSO.COM' && kvno -k /etc/admng-watchdog/admng-wd.keytab HTTP/admng-wd.contoso.com
  kdestroy
  ```

  `Preauthentication failed` or no `keytab entry valid` means that the kvno does not match AD – a new one-time password
  and `init` (3.7).
- **Restoring on a new machine** (different host key or TPM): the credential cannot be decrypted and systemd will not
  start the service (`systemctl status admng-watchdog` shows `status=243/CREDENTIALS`).
  `sudo admng-watchdog reset-secrets-key` sets the old keys aside, creates a new credential and starts the service; the
  history stays, and you enter the SMTP password and the Teams address again in AD Manager. There is no key export – by
  design. (A backup of the whole disk in the "host key" variant contains the key – restored on a new VM it works like the
  same machine.)

System journal – limit its size (the service does not write its own log files):

```bash
install -d /etc/systemd/journald.conf.d
printf '[Journal]\nSystemMaxUse=1G\n' > /etc/systemd/journald.conf.d/size.conf
systemctl restart systemd-journald
journalctl --disk-usage
```

---

## 4. OpenWEC – receiving events from the domain controllers

### 4.1 What it is and why a separate service

OpenWEC (the open source project `cea-sec/openwec`, GPL-3.0 license) is a Windows Event Forwarding receiver for Linux.
It accepts events sent by the domain controllers according to subscriptions (chapter 2.12), verifies their identity with
Kerberos and writes each event as one JSON line with the original event XML – in a separate file for each DC. Watchdog
only **reads** these files; it changes nothing in them.

- It runs as a separate process and a separate system account (`openwec`), in the separate package `admng-openwec`,
  which we build from a pinned version of the sources (commit `86de07f` of September 8, 2026 – package version
  `0.3.0+git20260908.86de07f-1~deb13` or `~deb12`). The source code (GPL-3.0: the sources, the Rust libraries used and
  our build recipe) is attached to the release as `admng-openwec-0.3.0.git20260908.86de07f-source.tar.xz`. The GPL license
  therefore does not extend to the AdMng code.
- The output files are the only place where raw events from the DCs lie on disk – only until they are processed (details
  in 4.5).
- If Watchdog stops for a moment, the events wait in the files; if OpenWEC stops, the controllers keep the events on
  their side and send them later (hence the size of the Security log from 2.10).

### 4.2 Installation

You installed the `admng-openwec` package (separate builds for Debian 12 and 13) in 3.1 together with the
`admng-watchdog` package, which depends on it. The package:

- creates the system account `openwec` (without a shell) and adds it to the `admng-keytab` group;
- installs `openwecd` and the `openwec` command (`/usr/bin`), the file `/etc/openwec.conf.toml` (4.3) and the unit
  `openwecd.service` with systemd restrictions (no privileges, write only in `/var/lib/openwec`, no outgoing
  connections, `IPAddressDeny=any` except the addresses of the controllers – 4.3);
- creates the OpenWEC state database (`/var/lib/openwec/db/openwec.sqlite` – only subscriptions and client state, no
  events; directory 0700). The output directory `/var/lib/openwec/out/` (owner `openwec`, group `admng-watchdog`, mode
  2750) is created by the `admng-watchdog` package;
- leaves the subscription directory `/etc/openwec/subscriptions/` empty – you generate the subscription file in 4.4; an
  example is in `/usr/share/admng-openwec/admng-dc-security.toml.example`. During installation and upgrade the package
  loads the subscription by itself, but only if the file `admng-dc-security.toml` is the only file in that directory and
  does not contain the example name `contoso.com`;
- enables `openwecd.service`; the service will not start until there is a keytab (3.6).

### 4.3 Kerberos configuration with the keytab

OpenWEC uses the **same** account and keytab as the Watchdog service: the controllers connect to the name
`admng-wd.contoso.com`, and the tickets for it are issued by the KDC for the account `ADMNG-WD$`. The file
`/etc/openwec.conf.toml` (supplied by the package; owner root, group `openwec`, mode 0640) – you change only the names:

```toml
[server]
keytab = "/etc/admng-watchdog/admng-wd.keytab"
db_sync_interval = 5
flush_heartbeats_interval = 5

[logging]
verbosity = "warn"
server_logs = "stderr"          # to journald
# access_logs – do not set: no key = no access log (addresses and accounts for every batch);
# the value "None" would be a file name, not a way of disabling it

[database]
type = "SQLite"
path = "/var/lib/openwec/db/openwec.sqlite"

[cli]
read_only_subscriptions = true  # subscriptions only from files

[[collectors]]
hostname = "admng-wd.contoso.com"
listen_address = "0.0.0.0"
listen_port = 5985
max_content_length = 512000

[collectors.authentication]
type = "Kerberos"
service_principal_name = "HTTP/admng-wd.contoso.com@CONTOSO.COM"

[outputs]
garbage_collect_interval = 600

[outputs.files]
files_descriptor_close_timeout = 600
```

**The shared keytab is an accepted solution (ADR-0009), with a consciously accepted risk.** Whoever takes over the
system account `openwec` (e.g. through a bug in OpenWEC, which accepts data from the network) holds the key
`HTTP/admng-wd.contoso.com` – the same one the Watchdog API uses to verify tickets. With it they can forge a ticket to
the API as any domain user, including the Watchdog administrator, and change the rules or silence the alerts (every
configuration change does, however, reach the existing recipients, and the history retention cannot be shortened below
the threshold from the startup settings – 5.2); they also hold the `ADMNG-WD$` identity (read access to the directory
and SYSVOL). They do not, however, have access to the Watchdog database (a 0700 directory of another account), the
channel secrets or write permissions in AD.

There is one more consequence, independent of the shared keytab: OpenWEC writes **every** byte that Watchdog reads –
including the fields by which Watchdog verifies the sender (4.5). A taken-over `openwec` account can therefore silently
omit or forge any record from the controllers: the performer and the source of a change, and events visible only in the
log (log clearing 1102, audit policy change 4719, DSRM password 4794). As long as anything arrives from every DC, the
Status tab will report nothing. The credibility of attribution therefore depends on the `openwec` account being intact.
This risk follows from using OpenWEC as the receiver at all (ADR-0009) and is limited by the same isolation of the
`openwec` account (below).

The risk is limited by:

- **API only from the administrators' subnet** – the firewall (3.3) lets port 8443 through only from 10.0.20.0/24, and
  the service checks the same with the `AllowedRemoteSubnets` setting (5.2). API requests from the server itself – also
  through the loopback address – are rejected (except `/healthz`), so a forged ticket will work only from the
  administrators' subnet – but from any workstation in it, including computers of people outside Tier 0 (e.g. helpdesk
  with the Viewer role). We do not restrict Watchdog's administrative operations to PAW workstations (ADR-0009), so the
  same applies to an administrator's ticket – this is part of the accepted risk;
- **isolation of the `openwec` account** – a separate system account without a shell, the service in a systemd sandbox
  (no privileges, write only in `/var/lib/openwec`), port 5985 accepts connections only from the domain controllers
  (firewall, 3.3); the account does not read the database, tickets or secrets of the Watchdog service;
- **no outgoing connections of the `openwec` account** – the receiver only accepts connections, so it needs no outgoing
  ones: the firewall rejects them with the rule `meta skuid "openwec" drop` (3.3), and the `openwecd.service` unit has
  `IPAddressDeny=any` except the addresses of the controllers (file below). A taken-over OpenWEC therefore cannot send
  the keytab out of the server (neither over HTTPS nor over DNS) and cannot use it itself – neither against the
  controllers (TGT ticket, LDAP, SMB) nor against the API on the server itself.

You enter the addresses of the controllers from which OpenWEC accepts connections in a separate unit file (the same as
`DCS` in the firewall, 3.3, plus any `AdditionalSourceAddresses`, 5.2). Without this file no controller will connect:

```bash
install -d -m 0755 /etc/systemd/system/openwecd.service.d
install -m 0644 /dev/stdin /etc/systemd/system/openwecd.service.d/admng-dcs.conf <<'EOF'
[Service]
IPAddressAllow=10.0.1.10 10.0.1.11
EOF
systemctl daemon-reload
systemctl show openwecd -p IPAddressDeny -p IPAddressAllow
```

Expected: `IPAddressDeny=` covers all addresses (`0.0.0.0/0 ::/0`), `IPAddressAllow=` – the addresses of the controllers.
When you add, retire or change the address of a DC, correct this file and `DCS` in the firewall.

Treat the `openwec` account like the keytab – as a Tier 0 asset. An optional future hardening would be a separate
computer account with its own DNS name and a keytab for the collector; version 0.3.0 does not provide for it and you
need not prepare anything in that direction.

### 4.4 Subscription for the controllers

The subscription tells the controllers **which** events to send. It covers the existing set of change events needed to
determine performers (changes of accounts, groups, computers, directory objects, log clearing, audit policy change) and
the sources of changes: 4624 (network logons – type 3 – and RDP – type 10), 4625, 4771 and failed 4776. Changes to
`dSCorePropagationData` (SDProp noise) are skipped already on the controller. It covers only the accounts `DC01$` and
`DC02$` – other computers will not receive this subscription.

The file `/etc/openwec/subscriptions/admng-dc-security.toml` is generated by a command (the event list comes from the
same event catalog as the service's parser, so they cannot diverge). From `appsettings.json` it reads only the
subscription identifier (`Watchdog.Collector.SubscriptionUuid`), so it works already before 5.2 – before you enter the
group SIDs and the other settings. `install -m 0644` gives the file a mode readable by the `openwec` account even when
the `umask 077` from 3.6 is active in the shell:

```bash
admng-watchdog print-wec-subscription --dc 'DC01$@CONTOSO.COM' --dc 'DC02$@CONTOSO.COM' |
    install -m 0644 /dev/stdin /etc/openwec/subscriptions/admng-dc-security.toml
```

Keep **only** this file in the `/etc/openwec/subscriptions/` directory – no copies or editor backup files. OpenWEC
loads the file you point it to (4.6); a copy with the same `uuid` in this directory would easily be confused with the
right file.

The result looks like this (an XPath query – each `Select` section has at most 20 event identifiers):

```toml
uuid = "4f7c2e1a-9b3d-4c55-8e21-6a0d9c7b3e10"
name = "admng-dc-security"
query = """
<QueryList>
  <Query Id="0" Path="Security">
    <Select Path="Security">*[System[(EventID=4720 or EventID=4722 or EventID=4723 or EventID=4724 or EventID=4725 or EventID=4726 or EventID=4738 or EventID=4740 or EventID=4767 or EventID=4781 or EventID=4765 or EventID=4766 or EventID=4794 or EventID=4741 or EventID=4742 or EventID=4743 or EventID=4719)]]</Select>
    <Select Path="Security">*[System[(EventID=4727 or EventID=4728 or EventID=4729 or EventID=4730 or EventID=4731 or EventID=4732 or EventID=4733 or EventID=4734 or EventID=4735 or EventID=4737 or EventID=4754 or EventID=4755 or EventID=4756 or EventID=4757 or EventID=4758 or EventID=4764)]]</Select>
    <Select Path="Security">*[System[(EventID=4744 or EventID=4745 or EventID=4746 or EventID=4747 or EventID=4748 or EventID=4749 or EventID=4750 or EventID=4751 or EventID=4752 or EventID=4753 or EventID=4759 or EventID=4760 or EventID=4761 or EventID=4762 or EventID=4763)]]</Select>
    <Select Path="Security">*[System[(EventID=5136 or EventID=5137 or EventID=5138 or EventID=5139 or EventID=5141 or EventID=1102 or EventID=1104 or EventID=1100)]]</Select>
    <Select Path="Security">*[System[(EventID=4624)]] and *[EventData[Data[@Name='LogonType']='3' or Data[@Name='LogonType']='10']]</Select>
    <Select Path="Security">*[System[(EventID=4625 or EventID=4771)]]</Select>
    <Select Path="Security">*[System[(EventID=4776)]] and *[EventData[Data[@Name='Status']!='0x0']]</Select>
    <Suppress Path="Security">*[System[(EventID=5136)]] and *[EventData[Data[@Name='AttributeLDAPDisplayName']='dSCorePropagationData']]</Suppress>
  </Query>
</QueryList>
"""

[options]
enabled = true
heartbeat_interval = 900
connection_retry_count = 5
connection_retry_interval = 60
max_time = 30
max_envelope_size = 512000
read_existing_events = false
content_format = "Raw"
ignore_channel_error = true

[filter]
operation = "Only"
type = "Client"
flags = "CaseInsensitive"
targets = ["DC01$@CONTOSO.COM", "DC02$@CONTOSO.COM"]

[[outputs]]
driver = "Files"
format = "RawJson"
config = { path = "/var/lib/openwec/out/{client}/security.jsonl" }
```

`max_time = 30` – the controller sends a batch after 30 seconds at the latest (a short delay of alerts);
`heartbeat_interval = 900` – a heartbeat every 15 minutes when there are no events.

**Check the query on the controllers before you load it.** **Where:** DC01 and DC02. The query alone (without the rest
of the file) is also printed by `admng-watchdog print-wec-subscription --dc 'DC01$@CONTOSO.COM' --query-only`. Save the
part from `<QueryList>` to `</QueryList>` as `admng-query.xml` and run:

```powershell
Get-WinEvent -FilterXml ([xml](Get-Content .\admng-query.xml -Raw)) -MaxEvents 5 | Format-Table TimeCreated, Id -AutoSize
```

Expected: a list of events (identifiers from the query) or `No events were found that match the specified selection
criteria.` – both results mean the query is correct. The error `The specified query is invalid` means a typo.
Separately check the filter of failed 4776 (an open question of the project): from ADM-WS01 perform a failed NTLM logon
(`net use \\10.0.1.10\IPC$ /user:CONTOSO\jan.kowalski ZleHaslo1` – an IP address instead of a name forces NTLM), and on
DC01:

```powershell
$q = '<QueryList><Query Id="0" Path="Security"><Select Path="Security">*[System[(EventID=4776)]] and *[EventData[Data[@Name=''Status'']!=''0x0'']]</Select></Query></QueryList>'
Get-WinEvent -FilterXml ([xml]$q) -MaxEvents 3 | Format-List TimeCreated, Message
```

Expected: a 4776 event with the error code `0xC000006A` (wrong password); successful 4776 must not appear.

**A rule for later – changing the event list.** You add new event identifiers from the Security channel in the **same**
subscription – the same `uuid` and `name`. Generate the file again (the `print-wec-subscription` command above, with the
same `install -m 0644 /dev/stdin /etc/openwec/subscriptions/admng-dc-security.toml`) and load it with the same
`sudo -u openwec openwec subscriptions load -y …` as in 4.6. The WEF bookmarks (a remembered place in the log up to
which the controller has already sent events) stay, so events from the gap are not lost; the controllers fetch the new
version at the next refresh (`Refresh`). Do not add channels other than Security – a controller with a bookmark would
then send all the existing events of the new channel (OpenWEC documentation). Do not change the `uuid`: a file with a
new `uuid` and the same name will be rejected by OpenWEC (`UNIQUE constraint failed: subscriptions.name`); with a new
name `load` deletes the old subscription together with its bookmarks, the new one starts from the next event
(`read_existing_events = false`) and events from the gap are lost, and Watchdog rejects all records until you also
change `Watchdog.Collector.SubscriptionUuid` (5.2). (Verified against the sources of the pinned OpenWEC version.)

### 4.5 Output to Watchdog

- Each DC has its own directory: `/var/lib/openwec/out/DC01@CONTOSO.COM/security.jsonl` (the account name without `$`),
  with read permission for the `admng-watchdog` group. Watchdog reads the files and remembers how far it has read, in
  the same transaction as it writes the events – after a crash it loses nothing; it rejects any duplicates.
- Watchdog accepts a line only when all of these match: the subscription identifier, the sender account (a writable DC
  of this domain), the computer name in the event and the sender's IP address. A non-matching record is rejected, and
  the Status tab shows an error: the events were sent by a computer other than a writable DC of this domain (e.g. an
  RODC, a DC of another domain, an ordinary computer with a faulty GPO) or by another subscription. All these fields are
  written by OpenWEC – the check protects against a foreign sender authenticated by an intact OpenWEC, but not against a
  taken-over `openwec` account (4.3).
- Logons of computer accounts (`…$`) and anonymous ones are rejected on read – the Windows XPath filter cannot exclude
  them on the controller.
- The timer `admng-wec-rotate.timer` (hourly, as root) renames the active files to
  `security.jsonl.<timestamp>`, tells OpenWEC to open new files (a reload signal) and deletes a file only when Watchdog
  confirms that it has read it to the end. An unprocessed file is deleted after **7 days** (the `MAX_UNCONSUMED_DAYS`
  setting in `/etc/default/admng-wec-rotate`, at most 30) or – starting with the oldest – when the unprocessed files of
  all DCs together take more than **8 GB** (`MAX_UNCONSUMED_MB`, default 8192). A flood of events (e.g. failed logons
  with random account names) or a stopped Watchdog will therefore not fill `/var`, where the database, its copies and the
  send queue live. In both cases Watchdog reports a gap.

### 4.6 Starting and checking that events from DC01 and DC02 arrive

**Where:** Debian, then DC01 and DC02.

```bash
sudo -u openwec openwec subscriptions load -y /etc/openwec/subscriptions/admng-dc-security.toml
systemctl start openwecd
systemctl status openwecd --no-pager
ss -tlnp 'sport = :5985'
journalctl -u openwecd -n 30 --no-pager
```

Expected: `+ Load subscription admng-dc-security`, `active (running)`, a `LISTEN … 0.0.0.0:5985` socket of the `openwecd`
process, no errors in the log. The `load` command with a single file removes other subscriptions from the OpenWEC
database (`-y` confirms this without asking) – that is intended, the controllers should receive only this one. You do
not have to load the subscription again after restarting `openwecd` – they are in the OpenWEC database.

On the controllers (after the `Refresh` time from the GPO at the latest, here 60 s):

```powershell
Get-WinEvent -LogName 'Microsoft-Windows-Forwarding/Operational' -MaxEvents 5 | Format-Table TimeCreated, Id, Message -Wrap
Test-NetConnection admng-wd.contoso.com -Port 5985
klist -li 0x3e4
```

Expected: event **104** (connected to the subscription manager) and **100** ("The subscription admng-dc-security is
created successfully"); `TcpTestSucceeded : True`. `klist -li 0x3e4` shows the tickets of the NETWORK SERVICE account
(forwarding runs under it) – note whether there is a ticket to `HTTP/admng-wd.contoso.com` or to
`HOST/admng-wd.contoso.com`. According to the OpenWEC documentation the WinRM client on Windows Server 2025 asks for
`HOST/` by default, and older ones for `HTTP/`. OpenWEC (MIT Kerberos) accepts a ticket to any SPN of the `ADMNG-WD$`
account – `HTTP/` and `HOST/` are encrypted with the same account key (verified in the lab), so record the `klist`
result for information only. A Kerberos error in event 105 – 9.2.

On Debian (after a few minutes):

```bash
sudo -u openwec openwec subscriptions machines admng-dc-security
sudo -u openwec openwec stats --subscription admng-dc-security
ls -l /var/lib/openwec/out/
wc -l /var/lib/openwec/out/*/security.jsonl
```

Expected: the machine list shows the address and name of each controller (e.g. `10.0.1.10:DC01$@CONTOSO.COM`); in the
statistics the number of active machines = the number of controllers; the directories `DC01@CONTOSO.COM` and
`DC02@CONTOSO.COM`; the line count grows (the controllers handle network logons all the time). Do not copy the file
contents off the server – it is directory data.

Finally, check that the `openwec` account will not establish any connection, even to a controller:

```bash
sudo -u openwec env KRB5_CONFIG=/etc/admng-watchdog/krb5.conf KRB5CCNAME=MEMORY:t \
    kinit -k -t /etc/admng-watchdog/admng-wd.keytab 'ADMNG-WD$@CONTOSO.COM'
```

Expected: after several dozen seconds `kinit: Cannot contact any KDC for realm 'CONTOSO.COM'` (the firewall also rejects
DNS queries of this account), and events from the DCs keep arriving (the line count above grows).

---

## 5. Watchdog service – settings and startup

### 5.1 What the package installed

The package `admng-watchdog_0.3.0-1_amd64.deb` (3.1; a single `amd64` package for Debian 12 and 13; dependencies: ASP.NET
Core Runtime 10.0 ≥ 10.0.13, `admng-openwec`, the MIT Kerberos and SASL GSSAPI libraries, `libldap`, `smbclient`,
`openssl`, systemd ≥ 252; recommended: `chrony`, `krb5-user`, `nftables`, the tpm2-tss libraries; conflicts with
`gss-ntlmssp`):

- creates the system account `admng-watchdog` (no shell) and the group `admng-keytab` (members: `admng-watchdog`,
  `openwec`);
- creates directories with owners and modes: data `/var/lib/admng-watchdog` (0700, service account only: `watchdog.db`,
  `backup/`, `keys/`), the keytab tool state `/var/lib/admng-watchdog-keytab` (`root:admng-watchdog` 0750), configuration
  `/etc/admng-watchdog` (owner root; `appsettings.json` root:admng-watchdog 0640 – the service can read it but not change
  it; `krb5.conf`, `ldap.conf`, `smb-client.conf` 0644; `tls/` root:admng-watchdog 0750; `credentials/` root only), the
  OpenWEC output directory `/var/lib/openwec/out` (2750 `openwec:admng-watchdog`);
- installs the service and the keytab tool in `/usr/lib/admng-watchdog/`, the commands `/usr/sbin/admng-watchdog` (below)
  and `/usr/sbin/admng-watchdog-keytab` (3.6, 3.7), the units `admng-watchdog.service`, `admng-watchdog-failed.service`,
  `admng-watchdog-keytab.service`/`.timer` and `admng-wec-rotate.service`/`.timer` (in `/usr/lib/systemd/system`), the
  OpenWEC file cleanup settings `/etc/default/admng-wec-rotate` (4.5) and the firewall example
  `/usr/share/admng-watchdog/nftables/admng-watchdog.nft` (3.3);
- creates the channel secrets key as a systemd encrypted credential (`/etc/admng-watchdog/credentials/dp-key.cred`) – if
  the machine has a TPM, in the "TPM2 + host key" variant, and if it has none or that variant fails – "host key"; it
  prints which variant it used and why, and records it in `/etc/admng-watchdog/dp-key.variant` (no secret; Status tab →
  Host);
- if there is no certificate in `/etc/admng-watchdog/tls/` – generates its own and prints the fingerprint (3.8, variant
  B);
- after an update of the .NET environment (ASP.NET Core Runtime) restarts the service – the old version disappears from
  the disk (dpkg trigger, 3.1);
- **on first installation it does not start the service** (it waits for the keytab and settings) – it enables and starts
  only the two timers; on an update it restarts the running service (database migrations run at startup, with a
  pre-migration copy);
- on `apt remove` it stops the service and timers and leaves the configuration; on `apt purge` it removes
  `/etc/admng-watchdog` (including the keytab, the certificate and the credential with the secrets key) and
  `/var/lib/admng-watchdog-keytab`, but **leaves** `/var/lib/admng-watchdog` – you remove the 3-year history only
  deliberately (8.1). `apt purge admng-openwec` removes `/var/lib/openwec`.

The `admng-watchdog` command (as root; it runs the checks as the service account, with its environment):

| Command | What it does |
|---|---|
| `admng-watchdog --check-config` | checks `appsettings.json` and the saved configuration; code 0 – OK, 2 – errors (printed) |
| `admng-watchdog --health` | asks the running service for `/healthz` at the address from `Hosting.Urls`; code 0 – running, 1 – not |
| `admng-watchdog --smoke-test` | test of the program itself (temporary data folder, loopback, no directory); code 0 / 1 |
| `admng-watchdog print-wec-subscription --dc 'DC01$@CONTOSO.COM' …` | the OpenWEC subscription file (4.4); `--query-only` – only the XPath query |
| `admng-watchdog cert-fingerprint` | SHA-256 fingerprint of the service's TLS certificate |
| `admng-watchdog cert-renew-self-signed` | a new self-signed certificate (3.8, variant B) |
| `admng-watchdog reset-secrets-key` | a new credential with the secrets key after a restore on a different machine (3.9) |

The service `admng-watchdog.service` runs as `admng-watchdog` in a systemd sandbox (no privileges, read-only file system
outside its own directory, port 8443 only) and tells systemd that it is alive only when its jobs are actually making
progress – a hung service will be restarted. systemd restarts it after a crash or hang every 30 seconds, with no limit
on attempts; data that causes a crash every time (e.g. a corrupted GPO file) the service skips after three attempts and
reports. It does not restart it only after a configuration error (the codes from the table in 5.3) – the unit then stays
in the `failed` state, and the helper unit `admng-watchdog-failed.service` writes a critical error to the system journal,
"AdMng Watchdog zatrzymany – sprawdź systemctl status admng-watchdog" ("AdMng Watchdog stopped – check systemctl status
admng-watchdog"; version 0.3.0 sends no notifications through Watchdog channels in this situation – 5.4).

### 5.2 Startup settings (`/etc/admng-watchdog/appsettings.json`)

Only the server's root edits the file. The package ships it with example names; change them to yours – except the
subscription identifier, which stays unchanged:

| Setting | Value in the example | Source |
|---|---|---|
| `Watchdog.Hosting.Urls` | `https://admng-wd.contoso.com:8443` | the name from the A record (2.4) |
| `Watchdog.Hosting.AllowedRemoteSubnets` | `[ "10.0.20.0/24" ]` | the workstation subnet (second line of defense after the firewall) |
| `Watchdog.Hosting.Certificate.Path` / `KeyPath` | `/etc/admng-watchdog/tls/admng-wd.crt` / `.key` | 3.8 |
| `Watchdog.Authorization.AdminSids` | SID of `AdMng-Watchdog-Admins` | 2.3 |
| `Watchdog.Authorization.ViewerSids` | SID of `AdMng-Watchdog-Viewers` | 2.3 |
| `Watchdog.Authorization.DeniedSids` | `[]` | immediate revocation of access (SID of a person or group) |
| `Watchdog.Directory.Domain` | `contoso.com` | |
| `Watchdog.Directory.Servers` | `[ "DC01.contoso.com", "DC02.contoso.com" ]` | full names only, not IP addresses; the service picks the PDC emulator |
| `Watchdog.Kerberos.Realm` / `ServicePrincipal` | `CONTOSO.COM` / `ADMNG-WD$@CONTOSO.COM` | 2.5 |
| `Watchdog.Collector.SubscriptionUuid` | `4f7c2e1a-9b3d-4c55-8e21-6a0d9c7b3e10` | **do not change** – it must equal the `uuid` of the subscription generated in 4.4 and loaded in 4.6 (`grep ^uuid /etc/openwec/subscriptions/admng-dc-security.toml`); after a change Watchdog rejects all records (4.4, "Rule for later") |
| `Watchdog.Retention.MinEventsDays` | `1095` | the shortest event retention that can be set in AD Manager (3 years); lower – only after root changes this file, e.g. after a DPO decision |

In place of the group SIDs the package writes the placeholder `S-1-5-21-0-0-0-0` – until you change it, `--check-config`
and the service start end with a message that the group SIDs must be provided. Leave the remaining settings
(`AllowTier0Suppression`, `AllowPrivilegedServiceAccount`, `AllowInsecureDataDirectoryAcl`) at `false`. Changes to
`Authorization` and the subnets take effect within a few seconds, the others after a service restart.

After every edit:

```bash
admng-watchdog --check-config
```

SMTP, Teams, rules and the retention time of logon records you set in AD Manager (chapter 6) – they are saved in the
service's database. The other retention values have defaults (AD Manager 0.3.0 has no editor for them). Default
retention in 0.3.0: events **1095 days**, DC log records related to changes 30 days, logon records 48 hours, 7 database
copies, database size limit 4 GB (`MaxDatabaseMb` 4096 – the database and 7 copies then fit on the recommended 40 GB of
`/var`). The service does not accept an events retention shorter than `MinEventsDays` (`retentionBelowFloor`), and every
shortening of retention (events, DC records, logon records, number of copies) is a critical event that also goes to the
existing recipients. When there is not enough disk space for another database copy, the service does not make it and
reports an error (`storage.backupSkipped`).

### 5.3 First start

```bash
systemctl start openwecd admng-watchdog
systemctl status openwecd admng-watchdog --no-pager
journalctl -u admng-watchdog -b --no-pager | tail -50
admng-watchdog --health; echo "kod: $?"
systemctl list-timers 'admng-*'
```

Expected: both services `active (running)`; in the journal a start without errors and entry 1008 with the certificate
fingerprint (`Category[1008]`);
`--health` – code 0; two timers (`admng-watchdog-keytab`, `admng-wec-rotate`) with the date of the next run.

After the start the service creates the database and takes the **baseline snapshot** of the domain (without change
notifications; a few minutes for about 50,000 objects) and silently reads all GPOs. At the end the event "Baseline
ready" appears and the **7-day rollout mode** begins (only critical events immediately, the rest in the daily report).

If the service does not start, the exit code shows the cause (`systemctl status admng-watchdog` → `status=…`). After
these codes systemd does **not** retry the start – you have to remove the cause and run `systemctl start
admng-watchdog`:

| Code | Cause | What to do |
|---|---|---|
| 2 | an error in `appsettings.json` or an inconsistent `ldap.conf` (e.g. missing `minssf`) | `admng-watchdog --check-config`; 3.5 |
| 3 | a database from a newer version than the program | do not roll back the package version |
| 4 | the certificate key is accessible to everyone or unreadable | 3.8 – file modes |
| 5 | a data folder with too broad permissions, with ACL entries, on a network share, or a symbolic link | `stat /var/lib/admng-watchdog` – owner `admng-watchdog`, mode 700 |
| 6 | the service account can write the settings file or its directory | owner root, modes as in 5.1 |
| 7 | `Development` authentication (lab only) in the settings of a production service | `"Authentication": "Negotiate"` (5.2) |
| 8 | the `ADMNG-WD$` account belongs to a privileged group | remove it from the group (2.5) |
| 9 | the service was started without the credential with the secrets key (e.g. outside the systemd unit or after the unit was changed) | start only through `systemctl`; 3.9 |
| 10 | keytab: wrong owner, group-writable, unreadable for the service | 3.6 – `root:admng-keytab 640` |
| `243/CREDENTIALS` (systemd message – the program did not even start) | `dp-key.cred` cannot be decrypted: a different machine, a different TPM, the file is missing | 3.9 – `sudo admng-watchdog reset-secrets-key` |

### 5.4 Health checks

- **The Status tab in AD Manager** (new sections): "Service account" (DN, kvno, last and next password change,
  encryption types, membership in privileged groups, keytab consistency with AD), "WEF collector" (for each DC: last
  record, delay, backlog, rejected records), "GPO content" (read progress, errors, SYSVOL lag), "Host" (system, .NET
  environment, secrets key variant, free space, clock difference, LDAP encryption level). Every problem has a
  description and a recommendation.
- **The service checks the `ADMNG-WD$` account itself** at startup and every hour: no membership in privileged groups,
  the "cannot be delegated" flag, AES only, SPNs, the deny entry for SELF on its own object (2.5), password age (warning
  > 32 days, error > 35 days), the keytab kvno equal to `msDS-KeyVersionNumber`, the first keytab entry equal to the
  account name (3.6), clock difference relative to the DC. The service does not check certificate templates (accepted
  risk – 2.5, "Certificates and the service account").
- **System journal:** `journalctl -u admng-watchdog -p warning --since today` – today's warnings and errors (without
  directory values). The event numbers (in the journal as `Category[1003]`) are as in version 0.2.0
  ([`WATCHDOG.md`](WATCHDOG.md), chapter 10); new ones: 1008 certificate fingerprint, 1011 service account problem, 1012
  WEF collector problem, 1013 GPO content problem; 1010 – a failed startup check or a stopped job (systemd then restarts
  the service).
- **Daily report** (7:30, also on weekends) – its absence means a problem with the service or with sending: then check
  `systemctl status admng-watchdog`.

Restart after a crash is automatic and has no attempt limit. A unit in the `failed` state (a configuration error – 5.3)
does not come back on its own, however: the critical error "AdMng Watchdog zatrzymany" ("AdMng Watchdog stopped") then
appears in the system journal (5.1), and there are no notifications – the only signal outside the server is the missing
daily report. If you have Zabbix or a SIEM, hook `journalctl` into it (priority warning and higher) or at least check
`systemctl is-failed admng-watchdog` every day.

---

## 6. AD Manager

### 6.1 Connecting to the service

**Where:** ADM-WS01, logged on as `CONTOSO\adm-jan` (a member of `AdMng-Watchdog-Admins`).

1. Check that the workstation gets a Kerberos ticket for the service and reaches the port:

   ```powershell
   klist get HTTP/admng-wd.contoso.com
   Test-NetConnection admng-wd.contoso.com -Port 8443
   ```

   Expected: a ticket `Server: HTTP/admng-wd.contoso.com @ CONTOSO.COM` with `KerbTicket Encryption Type:
   AES-256-CTS-HMAC-SHA1-96`; `TcpTestSucceeded : True`.
2. In AD Manager: **Tools → Watchdog** (Ctrl+Shift+A) → address `https://admng-wd.contoso.com:8443` → **Connect**.
   Always the full name from the A record – not the IP address, not an alias (Windows then tries NTLM, which the service
   rejects).
3. **Variant A** (a certificate from the company CA) – the connection works right away.
   **Variant B** (the server's own certificate) – AD Manager shows a window with the SHA-256 fingerprint (16 groups of 4
   hexadecimal digits), the name and the validity dates. On the server run `sudo admng-watchdog cert-fingerprint` (it
   prints the same digits in pairs separated by colons: `3F:2A:9C:…`), compare the **whole** fingerprint digit by digit
   and only then click "I trust this certificate" (Enter and Esc close the window without trust). The fingerprint is
   remembered in the AD Manager settings (Settings → Watchdog, with the "Remove pin" button); you confirm it once on
   each workstation. AD Manager will show a changed certificate as a change (old and new fingerprint) – do not accept it
   without checking on the server.
4. Roles: members of `AdMng-Watchdog-Admins` see everything and can change the configuration; `AdMng-Watchdog-Viewers` –
   read-only and acknowledging. A membership change takes effect within 5 minutes, without logging off.

### 6.2 First steps in the Watchdog tab

Description of all options: [`WATCHDOG.md`](WATCHDOG.md), chapters 5 and 6. The minimum to start:

1. **Status** – connection to the DC (PDC emulator), permission check (no gaps), attribution (coverage grows after the
   first changes), the new sections from 5.4 without errors.
2. **Channels and SMTP → SMTP**: server `smtp.contoso.com`, port `587`, **STARTTLS required**, authentication according
   to the relay's settings (no login – when the relay accepts mail from the address 10.0.1.25; or user and password).
   The password is saved encrypted, write-only (it never returns to AD Manager) and bound to this server – after
   changing the server, port or user you have to enter it again. Sender e.g. `AD Watchdog <watchdog@contoso.com>`.
   **Send a test message** – the window shows every stage (connection, TLS, login, recipients).

   In the lab (Mailpit from 1.1) – no login, and because the Mailpit certificate is self-signed, in the SMTP server
   certificate verification choose the pinned fingerprint and paste the SHA-256 fingerprint read on Debian (64
   hexadecimal characters without colons – the form in which Watchdog accepts it):

   ```bash
   openssl s_client -starttls smtp -connect smtp.contoso.com:587 </dev/null 2>/dev/null |
       openssl x509 -noout -fingerprint -sha256 | cut -d= -f2 | tr -d :
   ```
3. **Recipient groups**: "Administratorzy IT" (`it-admins`) and "Helpdesk" (`helpdesk`) – e-mail addresses (preferably
   distribution lists).
4. **Channels and SMTP → Teams** (optional – requires a Microsoft 365 tenant): in a Teams channel → … → Workflows →
   the template "Send webhook alerts to a channel" / "Post to a channel when a webhook request is received" → copy the
   address. In Power Automate add co-owners of the flow (a one-person flow stops working when that person leaves) and
   turn on "Secure inputs/outputs". Paste the address (it is a secret – after saving only the host name is visible) and
   send a test card; check that the card appeared in the channel (a 202 response only means acceptance). To the
   critical rules (`tier0-*`) add the Teams channel if the alerts are to go there.
5. **Rules**: review the built-in rules, including the ones new in 0.3.0: `tier0-badsuccessor` (dMSA),
   `tier0-machine-quota`, `watchdog-self` (service account), `tier0-gpo` (GPO content) – they notify immediately and
   around the clock, also in rollout mode; disabling them through the API requires local consent on the server
   (`AllowTier0Suppression`) and is itself a critical event. For every rule you can see how many messages it would send
   (the Preview button).
6. **Sources and retention**: attribution enabled with the source "OpenWEC (WEF)", dropping logons of computer accounts
   and anonymous ones, the collector silence thresholds, GPO content (enabled; SYSVOL lag tolerance, size limits) and
   the retention time of logon records (48 hours). Events are kept for 1095 days – AD Manager 0.3.0 does not edit this
   value, and the service does not accept one shorter than `MinEventsDays` from the startup settings (5.2). Three years
   of history is personal data of employees – agree the purpose and period with the DPO and enter the processing in the
   register of processing activities.
7. **Rollout mode** (7 days): in production, review the Rules for a week and add exceptions from the suggestions (e.g.
   the Entra Connect synchronization account), then "End rollout mode". In a test domain end it right after the baseline
   snapshot – otherwise in the pilot (chapter 7) you will see only critical alerts.
8. **Daily report**: recipients, time (7:30 by default), "Send now" as a test.

---

## 7. Pilot – checklist

### 7.1 Preparation

- All steps 2.x, 3.x, 4, 5 and 6 completed with verification; Status without errors; rollout mode ended (test domain).
- In production: the virtual machine sits on the Tier 0 virtualization platform (3.1) – record on which cluster or host
  and who administers it.
- Perform the test changes from **ADM-WS01** as `CONTOSO\adm-jan`. AD Manager and the "Active Directory Users and
  Computers" and GPMC consoles connect to the controller directly (LDAP) – Watchdog then shows `ADM-WS01` / 10.0.20.15 as
  the source (the name from the PTR record, with the note "(DNS)"). ActiveDirectory module commands in PowerShell and
  the Active Directory Administrative Center work through the ADWS service on the controller: the actor (`adm-jan`) is
  determined the same way, but as the source Watchdog may show "locally on the DC" – test 18 in 7.2 shows the
  difference. A change made in the controller's console has the DC itself as the source.
- For each test note: the execution time, the time the event appeared in AD Manager, the time of the e-mail/Teams card.
  An event usually appears within a dozen to several dozen seconds; the actor may briefly have the status
  "determining…".

### 7.2 Change tests and expected result

The "Who / from where" column is the expected attribution of a change made in AD Manager or in a console (ADUC, GPMC):
`CONTOSO\adm-jan`, workstation `ADM-WS01 (DNS)` (10.0.20.15), the DC on which the change was made. The PowerShell command
in the "Action" column describes the change – you can run it instead of AD Manager, but then the change goes through
ADWS: the actor is the same, and the source may be "locally on the DC" (7.1). Test 18 checks this difference on purpose.
The dMSA tests (11–12) have `-Server DC01.contoso.com`, because DC02 (Windows Server 2022) does not support dMSA.
Notifications according to the default rules after rollout mode ends (business hours: Mon–Fri 7:00–17:00).

| # | Action (ADM-WS01, AD Manager or PowerShell) | Expected event in Watchdog | Who / from where (AD Manager, console) | Notification |
|---|---|---|---|---|
| 1 | `New-ADUser -Name 'Test Pilot1' -SamAccountName test.pilot1 -Path 'OU=Użytkownicy,OU=Firma,DC=contoso,DC=com' -AccountPassword (Read-Host -AsSecureString) -Enabled $true` | "New account" (one event with the final state, despite several creation steps) | adm-jan / ADM-WS01 | e-mail to "Administratorzy IT" during business hours (batched every 5 min) + daily report |
| 2 | `Disable-ADAccount test.pilot1` | "Account disabled" | adm-jan / ADM-WS01 | daily report |
| 3 | `Set-ADAccountPassword test.pilot1 -Reset -NewPassword (Read-Host -AsSecureString)` | "Password changed" marked as a reset by an administrator | adm-jan / ADM-WS01 | daily report (for a privileged account – e-mail) |
| 4 | `Set-ADUser jan.kowalski -Department 'Księgowość' -Title 'Specjalista'` | "Account parameters changed" – `department` and `title`: was → is | adm-jan / ADM-WS01 | daily report |
| 5 | `Add-ADGroupMember -Identity "$((Get-ADDomain).DomainSID)-512" -Members test.pilot1` | "Added to a privileged group" (critical) + "Group member added" | adm-jan / ADM-WS01 | e-mail immediately, around the clock (rule `tier0-privilege`); Teams, if the channel is in the rule |
| 6 | `Remove-ADGroupMember -Identity "$((Get-ADDomain).DomainSID)-512" -Members test.pilot1 -Confirm:$false` | "Removed from a privileged group" | adm-jan / ADM-WS01 | e-mail |
| 7 | `Remove-ADUser test.pilot1 -Confirm:$false` | "Object deleted" | adm-jan / ADM-WS01 | e-mail |
| 8 | Lockout: on ADM-WS01 six times `net use \\DC01.contoso.com\IPC$ /user:CONTOSO\anna.nowak ZleHaslo<n>` (the threshold from 1.5 = 5) | "Account locked out" with the **source of bad passwords**: address 10.0.20.15 / ADM-WS01 and the number of attempts (from 4771/4776/4625) | source of bad passwords: ADM-WS01 | e-mail to the helpdesk during business hours (at most one per account per hour) |
| 9 | `Unlock-ADAccount anna.nowak` | "Account unlocked" | adm-jan / ADM-WS01 | daily report |
| 10 | GPO: in GPMC create the GPO "Pilot - test" linked to `OU=Pilot,…`, change a setting in it (e.g. Security Settings → User Rights Assignment → "Allow log on locally" – add the group `Księgowość`) | "Group Policy Object changed" (GPO version) and a GPO setting change event with a row "setting: was → is" (e.g. `Computer/Security/Privilege Rights/SeInteractiveLogonRight`) | adm-jan / ADM-WS01 (the actor from 5136 on the GPO object) | user rights are a sensitive category – high priority, e-mail immediately (rule `tier0-gpo`); for a GPO linked to the domain or the domain controllers OU – critical |
| 11 | dMSA: on DC01 (Windows Server 2025), if there is no KDS key (`Get-KdsRootKey`): `Add-KdsRootKey -EffectiveTime ((Get-Date).AddHours(-10))` (lab only); then from ADM-WS01 (if the RSAT module on the workstation does not know the `-CreateDelegatedServiceAccount` parameter, run it on DC01 – the source will then be DC01): `New-ADServiceAccount -Name dmsa-pilot -DNSHostName dmsa-pilot.contoso.com -CreateDelegatedServiceAccount -KerberosEncryptionType AES256 -Path 'OU=Pilot,OU=Firma,DC=contoso,DC=com' -Server DC01.contoso.com`; then the same without `-Path` for `dmsa-pilot2` (`-DNSHostName dmsa-pilot2.contoso.com`) – it goes to the default container `CN=Managed Service Accounts` | twice "New service account" – **critical** (rule `tier0-badsuccessor`); the second one also with the actor (the object creation auditing entry from 2.11 works in containers) | adm-jan / ADM-WS01 | e-mail immediately, around the clock |
| 12 | `Start-ADServiceAccountMigration -Identity dmsa-pilot -SupersededAccount (Get-ADUser svc-pilot).DistinguishedName -Server DC01.contoso.com` | "dMSA migration link changed" – **critical** (attributes `msDS-ManagedAccountPrecededByLink` / `msDS-SupersededManagedAccountLink`) | adm-jan / ADM-WS01 | e-mail immediately |
| 13 | `Set-ADDomain -Identity contoso.com -Replace @{ 'ms-DS-MachineAccountQuota' = 10 }` (from 0) | "Domain policy changed" – `ms-DS-MachineAccountQuota: 0 → 10`, **critical** (rule `tier0-machine-quota`); returning to 0 – high | adm-jan / ADM-WS01 | e-mail immediately |
| 14 | `Set-ADComputer ADMNG-WD -KerberosEncryptionType RC4, AES128, AES256`, then back to `-KerberosEncryptionType AES128, AES256` | service account change – **critical** (rule `watchdog-self`) – both steps; Status → Service account temporarily shows an encryption type mismatch | adm-jan / ADM-WS01 | e-mail immediately |
| 15 | `Add-ADGroupMember AdMng-Watchdog-Viewers -Members hd-ewa` | "Added to a privileged group" (the Watchdog role groups are treated as privileged); after ≤ 5 min `hd-ewa` can connect as Viewer | adm-jan / ADM-WS01 | e-mail immediately |
| 16 | In AD Manager change any rule and save | "Watchdog configuration changed" (who, what) | adm-jan | e-mail also to the existing recipients |
| 17 | (optional) on DC02: `wevtutil cl Security` | "Security log cleared" (critical) | the account that cleared the log | e-mail immediately |
| 18 | The same change by two paths: in AD Manager change the description of the account `anna.nowak` to "test LDAP", then in PowerShell `Set-ADUser anna.nowak -Description 'test ADWS'` | two "Account parameters changed" events – `description`: was → is | first: adm-jan / ADM-WS01 (DNS); second: adm-jan / ADM-WS01 or "locally on the DC" – note the result (7.4) | daily report |

After tests 11–12 clean up – first roll back the migration (the dMSA must still exist), only then delete the dMSA:

```powershell
Undo-ADServiceAccountMigration -Identity dmsa-pilot -SupersededAccount (Get-ADUser svc-pilot).DistinguishedName -Server DC01.contoso.com
Get-ADUser svc-pilot -Server DC01.contoso.com -Properties msDS-SupersededManagedAccountLink, msDS-SupersededServiceAccountState |
    Format-List msDS-SupersededManagedAccountLink, msDS-SupersededServiceAccountState     # oba puste
Remove-ADServiceAccount dmsa-pilot -Confirm:$false -Server DC01.contoso.com
Remove-ADServiceAccount dmsa-pilot2 -Confirm:$false -Server DC01.contoso.com
```

The cleanup also produces events (rolling back the link – critical, deleting the object) – this is expected. After
test 13 restore `0`.

### 7.3 Operation tests

| # | Test | How | Expected result |
|---|---|---|---|
| 1 | Restarting the receiver under load | during the tests from 7.2: `systemctl restart openwecd` | Status → WEF collector without a gap; events from the time of the restart arrive (the controllers retry them) |
| 2 | Service downtime | `systemctl stop admng-watchdog`, a test change, after 10 min `systemctl start admng-watchdog` | the change is reported after the start, with the actor (the OpenWEC files waited) |
| 3 | DC01 failure | turn DC01 off for 30 min | the service switches to DC02 (Status, the event "Domain controller unavailable"), Kerberos works through DC02; after DC01 returns the service stays on DC02 as long as it works – it returns to the PDC after a service restart or a DC02 failure, without a full rescan |
| 4 | Service account password change | the first automatic change after 30 days – or sooner with `admng-watchdog-keytab rotate --if-older-than 0` (3.7); then the repair from 3.7 (a new one-time password + `admng-watchdog-keytab init`) | AD Manager, LDAP and WEF work without a restart; Status → Service account shows the new kvno equal to AD. A change by the tool does not trigger the `watchdog-self` alarm; a password reset by an administrator in the one-time password path – does (a critical alarm, expected) |
| 5 | Certificate (variant B) | a connection from a second workstation; then `cert-renew-self-signed` | the fingerprint window at the first connection; after renewal – a change warning with two fingerprints |
| 6 | Restore on a different machine | TPM2 variant: restore the disk on a new VM without the same vTPM; "host key" variant: a new VM with the Debian from 3.1, copy only `/etc/admng-watchdog` and `/var/lib/admng-watchdog` (without `/var/lib/systemd/credential.secret`) | `systemctl status admng-watchdog` shows `status=243/CREDENTIALS`; after `reset-secrets-key` the service starts, the history is there, SMTP and Teams have to be entered again. (A copy of the whole disk in the "host key" variant contains the key – the service starts normally, this is expected.) |
| 7 | Roles | `Add-ADGroupMember AdMng-Watchdog-Viewers -Members anna.nowak`, log in with AD Manager using her account; then her SID in `DeniedSids` | after ≤ 5 min read-only access; after the entry in `DeniedSids` – denial within seconds |
| 8 | Firewall | from a network outside 10.0.20.0/24: a connection to 8443 and 22; from a computer in 10.0.20.0/24 that is not a PAW: a connection to 22 | no connection; from a workstation in 10.0.20.0/24 port 8443 works |

### 7.4 What the pilot is to settle

Record the results – some of the stage B decisions depend on them:

- which ticket the 2025 and 2022 controllers ask for – `HTTP/` or `HOST/` (`klist -li 0x3e4`, 4.6; informational only –
  OpenWEC accepts both);
- whether the WinRM client on the controllers has `TrustedHosts` set (`winrm get winrm/config/client` on both DCs) – with
  the value `*` (e.g. from a company GPO) the controller may send Negotiate instead of Kerberos;
- whether `kpasswd` accepts a computer account password change right after the one-time password is set and with the deny
  entry for SELF from 2.5 (3.6; then also the first automatic change after 30 days);
- whether the filter for failed 4776 works (4.4);
- the number of 4624 events (type 3 and 10) and 4776 per hour on each DC and the growth of the Security log – use it to
  set the target log size (2.10);
- whether LDAP signing enforced on Windows Server 2025 works with GSSAPI encryption (`SASL SSF: 256`, 3.6);
- whether the `tokenGroups` of a regular account is readable for `ADMNG-WD$` after granting the rights from 2.6 (the test
  from Debian in 2.6);
- which source Watchdog shows for changes through ADWS (PowerShell, Active Directory Administrative Center) – test 18 in
  7.2;
- who is recorded as the actor of the automatic `ADMNG-WD$` password change (events 4742/4723 on the DC);
- attribution coverage (Status → Attribution) after a week and the actual GPO content (changes in every category from
  7.2 #10);
- whether a change of only the SACL on the domain object (adding and removing a test auditing entry) produces the
  critical "security change" event (2.11, "Who can remove these entries");
- whether the hypervisor copies and restores the vTPM state (3.9).

---

## 8. Rollback

### 8.1 Debian server

**Where:** Debian, root.

```bash
systemctl disable --now admng-watchdog admng-watchdog-keytab.timer admng-wec-rotate.timer openwecd
apt purge admng-watchdog admng-openwec        # removes /etc/admng-watchdog (keytab, certificate, key) and /var/lib/openwec
```

- The history (`/var/lib/admng-watchdog`) **stays** after `purge`. Remove it only after deciding what to do with the
  3-year history (archive, handover, GDPR) – removal is irreversible:
  `rm -rf --one-file-system /var/lib/admng-watchdog`.
- `purge` also removes `/var/lib/admng-watchdog-keytab`; the directory `/etc/openwec/subscriptions` with your
  subscription file stays (`rm -r /etc/openwec`). The keytab loses its value only when the account in AD is disabled or
  deleted (8.2) – always do this.
- Firewall, chrony, DNS: restore the copies from 3.3 and 3.4 or delete the whole machine:

  ```bash
  cp -a /etc/nftables.conf.orig /etc/nftables.conf && nft -f /etc/nftables.conf
  cp -a /etc/chrony/chrony.conf.orig /etc/chrony/chrony.conf
  rm -f /etc/chrony/sources.d/ad-dcs.sources /etc/chrony/conf.d/admng-ad.conf && systemctl restart chrony
  cp -a /etc/resolv.conf.orig /etc/resolv.conf
  ```
- Leftovers that the packages do not remove (after restoring the firewall – its rules refer to the accounts `openwec`
  and `admng-watchdog`): `rm -r /etc/systemd/system/openwecd.service.d && systemctl daemon-reload` (the file from 4.3),
  `userdel openwec` and – only after the history is removed – `userdel admng-watchdog` (accounts created by the packages,
  3.1; the history directory belongs to the latter), and finally `groupdel admng-keytab`.
- If the machine was dedicated only to Watchdog – delete the whole VM and its backups (it is Tier 0 data).

### 8.2 AD and the controllers

**Where:** PAW, Domain Admin. The order matters: first the rights granted to the account, then the account itself.

1. Disable the account (from this moment the keytab does not work, and the account cannot log on with a certificate
   either): `Disable-ADAccount 'ADMNG-WD$'`. If there is a certification authority, check on each authority whether
   certificates were issued to this account (2.5, "Certificates issued to the account") and revoke them (2.5, "When the
   keytab may have leaked", step 2). There is no need to wait for a new CRL – you delete the account in step 6, and an
   account created some day under the same name would have a different SID, which the old certificates do not contain.
2. Remove the read rights (before deleting the account – otherwise entries with an "orphaned" SID would remain):

   ```powershell
   $d = 'DC=contoso,DC=com'; $a = 'CONTOSO\ADMNG-WD$'
   dsacls $d /R $a
   dsacls "CN=AdminSDHolder,CN=System,$d" /R $a
   dsacls "CN=Password Settings Container,CN=System,$d" /R $a
   dsacls "CN=Deleted Objects,$d" /R $a
   ```

3. GPO: `Remove-GPO -Name 'AdMng - WEF'`; `Remove-GPO -Name 'AdMng - ograniczenia konta Watchdog'` and remove the account
   from the rights in the existing GPOs to which you added it (2.5). On the controllers `gpupdate /target:computer
   /force`; in Forwarding/Operational a policy change notice appears, forwarding stops, and NETWORK SERVICE's access to
   the Security log returns to the defaults. We recommend leaving the auditing GPO (`AdMng - inspekcja DC`). Merely
   removing it does **not** restore the previous audit policy – the controllers keep the last settings in the local
   configuration. If it has to be rolled back, proceed as in 2.13, row 10: in this GPO set every subcategory to the value
   from the copy from 2.10 (not to "No Auditing" – unless the copy says so), on the DC `gpupdate /target:computer
   /force`, compare `auditpol /get /category:*` with the copy and only then `Remove-GPO -Name 'AdMng - inspekcja DC'`.
4. SACL: the "Rollback" block from 2.11 (removes only the entries added in 2.11 – also on `CN=Public Key Services`). The
   step is complete only when the block prints `pozostało wpisów z 2.11: 0` ("entries from 2.11 remaining: 0") for each
   of the three objects.
5. `ms-DS-MachineAccountQuota`: restore the previous value only if there is a business need (2.8).
6. Delete the account and groups: `Remove-ADComputer ADMNG-WD -Confirm:$false`; `Remove-ADGroup AdMng-Watchdog-Admins
   -Confirm:$false`; `Remove-ADGroup AdMng-Watchdog-Viewers -Confirm:$false`.
7. DNS: `Remove-DnsServerResourceRecord -ComputerName DC01.contoso.com -ZoneName contoso.com -Name admng-wd -RRType A -Force`
   and `Remove-DnsServerResourceRecord -ComputerName DC01.contoso.com -ZoneName 1.0.10.in-addr.arpa -Name 25 -RRType Ptr -Force`.
8. Do **not** undo the delegation fixes in the Tier 0 OU and the dMSA creation rights (2.2, 2.9) – they are a security
   improvement independent of Watchdog. If it is nevertheless necessary, restore the ACLs from the saved SDDL files (the
   command under the table in 2.13).

**Verification:** after step 2 (the account still exists) `dsacls 'DC=contoso,DC=com' | Select-String 'ADMNG-WD'` returns
nothing; after step 6 `Get-ADComputer ADMNG-WD` ends with the error "Cannot find an object"; after step 7
`Resolve-DnsName admng-wd.contoso.com` – no record; on the controllers no new events in Forwarding/Operational.

---

## 9. Troubleshooting

A detailed Kerberos trace on Debian is shown by the `KRB5_TRACE` variable, e.g.:

```bash
KRB5_TRACE=/dev/stderr KRB5_CONFIG=/etc/admng-watchdog/krb5.conf KRB5CCNAME=MEMORY:t \
    kinit -k -t /etc/admng-watchdog/admng-wd.keytab 'ADMNG-WD$@CONTOSO.COM'
```

`KRB5_CONFIG` points to the service's configuration (the system one gives misleading errors), and `KRB5CCNAME=MEMORY:`
keeps the ticket only in the command's memory. The trace contains no keys or passwords, but it does contain account names
and addresses – do not publish it outside the team.

### 9.1 Kerberos

| Symptom | Cause | Solution |
|---|---|---|
| `Clock skew too great` | the clock of Debian or the DC differs by more than 5 min: chrony rejects the controllers (no `maxdistance` from 3.4, a large time spread of the DC) or the PDC emulator has no time source | `chronyc tracking`, `chronyc -N sources`, `chronyc ntpdata 10.0.1.10` (`Root dispersion`), the file `/etc/chrony/conf.d/admng-ad.conf` (3.4); on DC01 `w32tm /query /status` (1.4) |
| `Cannot contact any KDC for realm 'CONTOSO.COM'` | DNS, firewall (88/tcp), wrong KDC name in `krb5.conf` | `getent hosts DC01.contoso.com`, `nft list ruleset`, is `KRB5_CONFIG` set? |
| `Client not found in Kerberos database` | wrong account name (missing `$`, a typo, lowercase realm) | `ADMNG-WD$@CONTOSO.COM` – in single quotes (the `$` character) |
| `Client's credentials have been revoked` | the account is disabled: it was not enabled after the deny entry for SELF (2.5, "Enabling the account…") or it was disabled after a keytab leak | `Get-ADComputer ADMNG-WD -Properties Enabled`; `Enable-ADAccount 'ADMNG-WD$'` (after a leak – only according to 2.5, "When the keytab may have leaked") |
| `Preauthentication failed` on `kinit -k` | the keys in the keytab do not match the password in AD: wrong salt, a keytab from before a password change, a password change in AD outside Debian | 3.6 (salt), compare the kvno: `klist -k` vs `msDS-KeyVersionNumber`; repair – a new one-time password and 3.7 |
| `kvno -k` without `keytab entry valid` / AD Manager "The service rejected the Kerberos authentication" (401) with a valid ticket | the keytab does not contain the key version with which the KDC encrypts tickets (kvno), or the `HTTP/…` name is missing from the keytab | `klist -k -e` – three names, current kvno; the service journal, entry 2501 with a hint |
| `KDC has no support for encryption type` | the account has no AES keys (encryption types set, but the password has not been changed since) or the client forces AES while the account has only RC4 | `Get-ADComputer ADMNG-WD -Properties msDS-SupportedEncryptionTypes` = 24; a new one-time password and `init` (3.7, "Repair") |
| `Server not found in Kerberos database` | the SPN does not exist or the client uses an alias (CNAME), an IP address or a name from another domain | `setspn -Q HTTP/admng-wd.contoso.com`; connect with the name from the A record (2.4) |
| The same SPN on two accounts (e.g. the old gMSA from 0.2.0) | the KDC does not know whose key to encrypt with; authentication fails | `setspn -X` (duplicates), `setspn -Q HTTP/admng-wd.contoso.com`, `setspn -D …` on the old account |
| `admng-watchdog-keytab`: `otpActive` / `KRB5_KPASSWD_SOFTERROR`; `kpasswd`: `Password change rejected` | domain password policy – the minimum password age counts from the moment the one-time password is set | 3.6 – keytab from the one-time password; the timer will change the password itself when the minimum age passes (in the emergency procedure: the next day the same block with the **same** one-time password, without a new reset on the PAW) |
| `admng-watchdog-keytab`: `changeFailed` / `KRB5_KPASSWD_ACCESSDENIED`; `kpasswd`: `Access denied` | password change blocked – e.g. by the deny entry for SELF from 2.5 (a pilot question) | remove the entry (the block from 2.5 with `RemoveAccessRuleSpecific` in place of `AddAccessRule` – it removes exactly that entry), repeat 3.6 and report the result to the team |
| The service stopped authenticating after a VM restore | the keytab is older than the password in AD (the change happened after the copy was made) | Status → Service account (`keytabStale`); a new one-time password + `init` (3.7) |

### 9.2 WEF (event forwarding)

On the DC: `Get-WinEvent -LogName 'Microsoft-Windows-Forwarding/Operational' -MaxEvents 20`. Event **104** – connected to
the subscription manager, **100** – subscription created (this is how it should be), **105** – a connection problem; its
text contains the error code.

| Symptom | Cause | Solution |
|---|---|---|
| No entries at all in Forwarding/Operational | the GPO did not reach the DC or the WinRM service is not running | `gpresult /scope computer /r` (is "AdMng - WEF" there), the registry from 2.12, `Get-Service WinRM` |
| 105 with code `2150858770` (0x80338012, "The client cannot connect to the destination…") | the DC does not reach 10.0.1.25:5985: DNS, firewall (Debian – DC addresses only; DC – outbound traffic), the DC address missing from `IPAddressAllow` of the `openwecd` unit (4.3), `openwecd` is not running | `Test-NetConnection admng-wd.contoso.com -Port 5985`; `systemctl status openwecd`; `systemctl show openwecd -p IPAddressAllow`; `nft list ruleset` |
| 105 with code `2150859027` (0x80338113, "…HTTP URL was not available") | something that is not a WS-Management receiver answers at the address, or a wrong address in the GPO | check the value `Server=…` in the GPO (2.12) and `ss -tlnp 'sport = :5985'` on Debian |
| 105 with a Kerberos error (e.g. `0x80090322` – wrong service name) | SPN / keytab: no SPN in AD, a different kvno in the keytab than in AD | `setspn -L ADMNG-WD` (2.5), `kvno -k` on Debian (3.6), `klist -li 0x3e4` on the DC (4.6) |
| 105 with an authentication error despite a correct SPN and keytab | the GPO sets `TrustedHosts=*` of the WinRM client on the DC – the controller sends Negotiate instead of Kerberos | `winrm get winrm/config/client` on the DC; note the result and the GPO (7.4) |
| 104 is there, 100 is missing | the DC connected, but the subscription does not cover it | `[filter] targets` in the subscription (names `DC01$@CONTOSO.COM`), `openwec subscriptions` – is it loaded and enabled |
| 100 is there, but no events from the Security log / an error with code 5 (access denied) | NETWORK SERVICE does not read the Security log | `wevtutil gl security` – `channelAccess` must contain `(A;;0x1;;;NS)` (2.12) |
| Events arrive, but Status shows "rejected records" for a DC | identity mismatch: a different DC IP address (NAT, several adapters), computer name, account | the DC addresses in DNS; for NAT or several adapters – `Watchdog.Collector.AdditionalSourceAddresses` in `appsettings.json` (5.2) |
| Status: "DC silent" / "DC is not forwarding events" | forwarding interrupted (GPO, WinRM, firewall 5985) – controllers always have network logons, so silence means a failure | as above, starting from Forwarding/Operational on this DC |

### 9.3 LDAP

| Symptom | Cause | Solution |
|---|---|---|
| `ldap_sasl_interactive_bind: Local error (-2)` … `Server not found in Kerberos database` | the DC name given as an IP address or alias, no `SASL_NOCANON on` with a wrong PTR | always the full DC name (`ldap://DC01.contoso.com`), `LDAPCONF=/etc/admng-watchdog/ldap.conf` |
| `SASL(-4): no mechanism available` | the SASL GSSAPI module is missing | `apt install libsasl2-modules-gssapi-mit` |
| `Strong(er) authentication required` (`… requires binds to turn on integrity checking …`) | a bind without signing – the DC requires LDAP signing (the default on Windows Server 2025) | use `-Y GSSAPI` with `ldap.conf` from 3.5 (`minssf=56`); do not use a simple bind without TLS |
| `SASL(-15): mechanism too weak for this user` (the GSSAPI bind rejected because the server does not offer encryption ≥ 56 bits) | enforced encryption (`minssf=56`) – and that is good | check the account's AES keys and `ldap.conf`; do not lower `minssf` |
| `SASL SSF: 0` | `ldap.conf` with `maxssf=0` (this setting is only for the optional LDAPS on 636) | for port 389 restore `ldap.conf` from 3.5 |
| No `tokenGroups` in the test from 2.6 / AD Manager: "No permission" despite group membership | the account cannot read `tokenGroups` of operator accounts | 2.6 – the `GR` entry for the classes `user`, `inetOrgPerson`, `computer` (Windows Authorization Access Group does not help) |
| Status: permission gaps (`permission.*`) | no explicit read rights | 2.6 (also AdminSDHolder; SDProp up to 60 min) |

### 9.4 SYSVOL

| Symptom (`smbclient`, `sysvol.*` state) | Cause | Solution |
|---|---|---|
| `NT_STATUS_ACCESS_DENIED` for a GPO folder | the GPO without read for Authenticated Users and Domain Computers | the script from 2.7 |
| `NT_STATUS_LOGON_FAILURE` | Kerberos: ticket, keytab, time | tests 1 and 3 from 3.6 (test 3 can be repeated while the service is running), 9.1 |
| `Could not find a suitable mechtype` / `NT_STATUS_INVALID_PARAMETER` | no TGT in the cache – `smbclient` does not fetch it from the keytab on its own | `kinit -k -t /etc/admng-watchdog/admng-wd.keytab 'ADMNG-WD$@CONTOSO.COM'` (3.6, Test 3); the service does this itself before every read |
| `NT_STATUS_BAD_NETWORK_NAME` | wrong share or DC name | `//DC01.contoso.com/SYSVOL` |
| `NT_STATUS_IO_TIMEOUT` / `Connection … failed` | firewall 445 (Debian or the DC) | `nft list ruleset`, `Test-NetConnection DC01.contoso.com -Port 445` from another machine |
| `smbclient` "hangs" | no `-N` – it waits for a password | add `-N` (3.6) |
| Status: SYSVOL lag for a GPO (`gpo.<GUID>.sysvolLag`) | SYSVOL replication (DFSR) is not keeping up with the GPO version change | on the DC: `dfsrdiag backlog` or the DFSR state; the service retries the read for up to 2 hours |
| A change in a GPO with the actor "undetermined – SYSVOL_NOT_AUDITED" | someone changed files in SYSVOL without changing the GPO version (bypassing GPMC) | check who has write access to SYSVOL; this is a signal to investigate |
