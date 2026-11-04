**English** · [Polski](../WATCHDOG.md)

# AdMng Watchdog – Active Directory change monitor

AdMng Watchdog is a separate service that watches the Active Directory domain around the clock, records the change
history (who, what, when, from where, was → is – 3 years by default) in its own data folder, and notifies administrators
(email, daily report, Microsoft Teams, webhook). From version **0.3.0** it runs on Linux only – as the systemd service
`admng-watchdog` on a **Debian 12 or 13** server outside the domain, with the computer account `ADMNG-WD$` and a
keytab, receiving domain controller events through Windows Event Forwarding and OpenWEC. In the AD Manager (AD_MNG) app
the service has its own **Watchdog** tab: event view, notification rules, SMTP and channel configuration, and service
status.

This document describes the features. Step-by-step deployment (AD, domain controllers, Debian server, packages, pilot,
rollback): [WATCHDOG-LINUX.md](WATCHDOG-LINUX.md). The Windows service (gMSA, PowerShell script) was available in version
0.2.0 – its guide is in [WATCHDOG.md from version 0.2.0](https://github.com/DarienRahl/AD_MNG/blob/v0.2.0/docs/WATCHDOG.md).

The decisions about how the service stores data and about the move to Linux are described in
[ADR-0008](adr/0008-watchdog-data-in-separate-folder.md) and [ADR-0009](adr/0009-watchdog-on-linux.md). Implementation
specifications (for developers, in English): [dev/watchdog-spec.md](../dev/watchdog-spec.md) and
[dev/watchdog-linux-design.md](../dev/watchdog-linux-design.md).

---

## 1. What Watchdog detects

| Category | Example events | Default priority | Default notification |
|---|---|---|---|
| New objects | new user account, new computer, new group, new OU, new gMSA account (dMSA – high) | low / informational | accounts: email during working hours (batched every 5 min) + daily report; computers: daily report |
| Unusual computers | computer joined to the domain by an ordinary user (MachineAccountQuota), created in `CN=Computers` despite the default container being redirected (`redircmp`), new server | medium | email during working hours |
| Deletions | deleted / restored account, group, computer, OU; mass deletion (≥ 10 accounts, groups or OUs in 10 min – critical; ≥ 50 computers in 10 min – high) | medium (OU – high) | email |
| Account state | disabling, enabling (enabling an account that was disabled or unused for > 30 days – high), flag changes (e.g. "password never expires", "password not required") | informational … high | risky flags: email; others: daily report |
| Lockouts | account lockout with the **source of the bad passwords** (IP address / computer with the number of attempts), unlock by an administrator, many lockouts in a short time (≥ 5 accounts or 0.5% of accounts in 10 min), optionally detection of a "password spraying" attack from DC logs (≥ 20 accounts from one address in 10 min; account names that do not exist are masked to the first character and the length, e.g. `L…(9)`, because they are sometimes a password typed into the user name field – the number of accounts stays exact) | low (mass lockouts – medium, spraying – high) | lockouts: email to the helpdesk during working hours, at most one per account per hour; spraying: email always |
| Passwords | password reset by an administrator (a reset with the option "user must change password at next logon" yields both events: the reset – counted as a reset in the report and rules – and "user must change password"), password change by the user, "user must change password", extending password validity without changing it (the `pwdLastSet` 0 → -1 trick) | informational (extension – medium; password reset of a privileged or service account – high) | daily report; password reset of a privileged account and validity extension: email |
| Account parameters | every observed attribute change: old → new value (department, title, manager, phone, UPN, logon script, profile, logon restrictions …) | informational (scripts/profiles/restrictions – medium) | daily report; scripts and profiles: email |
| Groups | adding / removing a member (including a member from another domain of the forest, e.g. from a child domain), group type change | informational | daily report; watched groups: email |
| Privileged access | gaining membership (also indirect, through nested groups and the primary group, also after restoring the account from the AD Recycle Bin) in Domain Admins, Enterprise Admins, Administrators, Schema Admins, account/backup/server operators, DnsAdmins, Watchdog's own role groups … | critical / high | email immediately, around the clock |
| Critical attributes | adminCount, SID History, Kerberos delegation, SPN on a user account, msDS-KeyCredentialLink (Shadow Credentials), primary group change, dMSA account migration links (on both sides – BadSuccessor), explicit certificate mappings (`altSecurityIdentities`), the list of principals that can read a gMSA password, permission (ACL) changes on critical objects (domain, AdminSDHolder, domain controllers OU) | high / critical | email immediately, around the clock |
| Domain and forest | password and lockout policies, fine-grained policies (PSO), GPO links to the domain and to the domain controllers OU, trusts, new or removed domain controller (promoting a server to a DC in one step yields only "new domain controller", without an additional critical delegation event), FSMO role transfer, certificate templates and PKI services (NTAuth), enabling the AD Recycle Bin, `dSHeuristics`, `tombstoneLifetime`, schema changes (one event per cycle, never repeated after a cycle error; writes made during a cycle are reported by the next one) | high / critical | email immediately |
| DC logs | security log cleared (1102), audit policy change (4719), DSRM password change (4794) | critical / high | email immediately |
| GPO content (from 0.3.0) | GPO setting changes as **"setting: was → is"** (security template – user rights, restricted groups, account policies; `Registry.pol`; GPP preferences – e.g. scheduled tasks and local accounts; scripts with their SHA-256 hash; audit policies; comments), redirecting the GPO folder (`gPCFileSysPath`) to another server, file changes in SYSVOL bypassing GPMC (daily reconciliation), presence of GPP passwords (`cpassword`, MS14-025 – the value is never stored) | sensitive settings – high (GPO linked to the domain or to the domain controllers OU – critical), others – low; redirection and `cpassword` – critical | email immediately (rule `tier0-gpo`) |
| New Tier 0 rules (from 0.3.0) | creating a dMSA account and every change of the migration link (**BadSuccessor**, Windows Server 2025), raising `ms-DS-MachineAccountQuota` from 0, every change of the service account `ADMNG-WD$` other than its scheduled password change (`watchdog-self`), a change of only the SACL (auditing) of the domain, AdminSDHolder, the domain controllers OU, `CN=Public Key Services` and `CN=Sites` | critical (another change of the computer account quota – high) | email immediately, around the clock (rules `tier0-badsuccessor`, `tier0-machine-quota`, `watchdog-self`, `tier0-security`) |
| Deadlines | account expires in 14 / 7 / 1 days (calendar days to the last day of validity; when the deadline falls on a weekend or holiday – on the preceding business day), account expired, password of a privileged or service account is expiring | informational (privileged ≤ 7 days – high) | helpdesk: email during working hours; daily report; optionally the account's manager |
| Service health | no connection to the DC, resynchronization, missing permissions, problems with DC logs, failed deliveries, low disk space, outdated .NET runtime, **a change of Watchdog configuration** | high / medium | email and all Teams/webhook channels always (the rule is locked – it cannot be disabled and its delivery cannot be weakened) |

Every event contains: **what** changed (old → new value), **when** and **on which DC**, and, when the domain
controllers' logs allow it – **who** did it and **from where** (IP address, computer name with an indication of where
it came from – a name supplied by the client or taken from DNS is not proof – and the confidence level). Values of
passwords, LAPS, BitLocker, LSA secrets and confidential attributes are never read or sent – the notification shows only
"changed (value hidden)".

Watchdog deliberately **does not make noise**:

- On first start it takes a **baseline snapshot** of the domain and sends no notifications about changes.
- For the first **7 days** after the snapshot **rollout mode** is in effect: it notifies immediately only about critical
  events ("tier0-*" rules, mass events, service health) and collects the rest in the daily report; the Rules tab shows
  for each rule how many messages it would send. The administrator ends the mode earlier with one button
  ("End rollout mode" saves only that flag – other unsaved changes to rules, channels and the daily report remain
  unsaved on the new configuration version and must be saved separately).
- An account created in several steps (creation → password → enabling, mass scripts) yields **one** "New account"
  event with the final state (2-minute window).
- An administrator's own password change, automatic gMSA password rotation, an SDProp entry (adminCount) after a
  privilege grant that has already been reported, department or phone changes from the nightly HR synchronization,
  device key registration (Windows Hello / hybrid join), deleting child objects (BitLocker keys, DNS records, devices)
  and resetting the lockout at the first logon after it expired do **not** generate urgent notifications.
  An "own" password change is only an event 4723 written by the same account (or by ANONYMOUS LOGON – an expired
  password changed at logon). The password of a privileged account changed by **another** account remains a high event
  and sends an alert.
- Changes that an object makes to itself are skipped by the "tier0-security" rule (`ExcludeSelfChanges`) only when they
  are routine: computer account key rotation and self-registration of an SPN by a gMSA/dMSA – and never for domain
  controllers or privileged objects. Tier 0 changes to the object's own attributes (RBCD, SID History, the own SPN of a
  user account, primary group) always alert – expect an alert e.g. in the RBCD scenario with a relayed computer
  account authentication.
- **Exclusions** (`ExcludedSubtrees`): adding a subtree to the exclusions silently removes its objects from the
  snapshot – without a "moved" event for each object; the configuration change itself is reported as "Watchdog
  configuration change". An OU moved into an excluded subtree yields **one** "moved out of scope" event (`outOfScope`)
  for the OU itself, not for every object inside. An OU moved back, or removing an exclusion, yields one "object
  appeared" event, and the objects inside are taken over silently. Objects created in the same poll are still reported
  as new (e.g. "New account"). The `outOfScope` marker on "object appeared" means solely an actual move into scope,
  never merely the existence of exclusions.
- **Mass deletions and mass lockouts** are counted by the time of the change itself, not the time it was detected.
  Deletions or lockouts spread over many hours but detected together (catching up after a service outage, switching to
  another DC and resynchronizing, the nightly reconciliation) are not a mass event. A real series detected with a delay
  is reported once.
- After switching to another DC (resynchronization), objects created up to 3 hours before the last poll are reported as
  new rather than as "object appeared" (replication delay, clock differences).
- Swapping or shifting OU names in one poll (e.g. `Sprzedaż` → `Sprzedaż_stara` and `Sprzedaż_nowa` → `Sprzedaż`)
  preserves the correct DNs of the objects and yields no false moves; the previous DN of an object that left an OU
  renamed in the same poll is its actual old DN.
- Daily reconciliation: an object found under an old DN that the scan does not see is "object disappeared" (low), not
  "moved" with the same previous and new DN.

Deletions and the AD Recycle Bin: without the AD Recycle Bin (the Windows default, Samba) an object created and
deleted between two polls is reported as deleted. With the AD Recycle Bin enabled, the final removal (recycling) of a
long-deleted object – after about 180 days – is not reported as a new deletion. Restoring such a short-lived object is
an "object restored" event.

Membership in the `MembershipTracking: PrivilegedAndWatchListOnly` mode (only privileged and watched groups): the member
lists of groups nested in privileged or watched groups are stored as well, so their subsequent changes are reported as
a member being added / removed – also right after the baseline snapshot. The first read after a mode change is silent
(no events for existing members), and a group that stops being nested loses its stored list.

Members from another domain of the forest (also from a child domain): removing them from a group is reported as a
member removal, and for privileged groups also as a revocation of privileged access – the service does not see objects
of another domain, so it does not treat them as deleted.

---

## 2. How it works

```text
Debian 12/13 server outside the domain (Tier 0), computer account CONTOSO\ADMNG-WD$ with a keytab (password changed every 30 days)
├─ admng-watchdog.service (systemd, account admng-watchdog)
│   ├─► DC (PDC): LDAP 389, Kerberos + signing + encryption (SASL GSSAPI) – highestCommittedUSN every 15 s, only changed
│   │   objects (uSNChanged), comparison with the snapshot
│   ├─► DC (PDC): SMB 3 with signing and Kerberos – reading GPOs from SYSVOL (read only)
│   ├── reads OpenWEC files: 47xx, 5136–5141, 4624/4625/4771/4776 → who and from where
│   ├── rules → notification queue → SMTP / Teams / webhook (retries, limits)
│   ├── /var/lib/admng-watchdog (SQLite, secret keys, backups) · /etc/admng-watchdog (settings – root only)
│   └── HTTPS API 8443 (Kerberos, roles from AD groups), /healthz for monitoring
├─ openwecd.service (OpenWEC, account openwec) ◄── WEF from every DC: HTTP 5985 + Kerberos (encrypted messages)
│   └── /var/lib/openwec/out/<DC>/security.jsonl (transient files, deleted after reading)
└─ timers: service account password change (daily, when the password is > 30 days old), OpenWEC file cleanup (hourly)
                                   ▲ HTTPS + Kerberos
              AD Manager → Tools → Watchdog (Ctrl+Shift+A)
```

1. **Detection (LDAP).** The service is "pinned" to one domain controller (by default the PDC emulator – account
   lockouts land there). Every 15 seconds it reads the `highestCommittedUSN` counter from the RootDSE. When it has
   changed, it fetches only objects with `uSNChanged` greater than the last one processed and compares the observed
   attributes with the snapshot stored in the data folder. Group members and managers are compared by object GUID, so
   moving or renaming an account does not look like a membership change. It sees deleted objects in the
   `Deleted Objects` container, and once a day (by default at 02:30) it performs a full reconciliation. Progress is
   remembered separately for each DC, so returning to the PDC after a failure or a role transfer does not require
   another full scan.
2. **Attribution (DC logs).** The controllers themselves send selected Security log events with the Windows Event
   Forwarding mechanism (no agents) to the OpenWEC collector on the Watchdog server; the service reads its files
   continuously, checks the sender (a writable DC of this domain, subscription, address) and stores normalized records
   (change-related ones – 30 days, logons – 48 hours). From the replication metadata the service knows on which DC and
   when a change originated, and it matches the event of that controller (e.g. 4738 for an account, 4728 for a group,
   5136 for an attribute). It determines the source computer from the 4624 logon of the same session (network logons –
   type 3 – and RDP – type 10; first the IP address, then the workstation name), and for lockouts – from the "Caller
   Computer Name" field of event 4740 and from events 4771/4776/4625 (addresses the bad passwords came from, with the
   number of attempts). The determined source is copied to the event and stays with it for the entire history retention
   period. The log may be delayed – the event then has the status "determining…" and is completed once matched (for
   critical events an additional "Follow-up" arrives in the same thread – only once the original alert has been
   delivered; it waits as long as the alert is being retried, and is not sent at all if the alert ultimately failed; a
   follow-up is also produced when the actor was determined while the alert was being prepared). When the determined
   actor **raises the priority** of the event to high or critical (e.g. Shadow Credentials written by another account:
   medium → high), recipients who already received an alert with a lower priority get, in the same thread, one
   "Follow-up: priority raised" (together with the actor – never two messages). If the actor cannot be determined,
   Watchdog does not guess: it shows "undetermined" with the reason (e.g. no audit policy; after the Security log is
   cleared – events 1102/1104 or record numbering restarted – "LOG_OVERWRITTEN" instead of "NO_MATCH").
   - **Lockouts:** until event 4740 has arrived (WEF batches, unavailable PDC), the source of the bad passwords has the
     status "determining…" and is searched for again for up to 30 minutes. Accounts with Polish characters or an
     apostrophe (`józef.żak`, `o'neill`) also get the 4771/4776/4625 lookup.
   - **Privileged access** (gaining / revoking): the actor is the author of the change to any link of the nesting path,
     or of the account's primary group change, made at most 15 minutes earlier. Otherwise the actor remains
     "undetermined" – the service does not attribute an older, unrelated group addition.
   - The service connects to DCs **through Kerberos only** (no NTLM), which is why the controllers are given as DNS
     names (`Directory.Servers`); OpenWEC accepts only Kerberos authentication from the controllers.
3. **GPO content (SYSVOL).** After the GPO version (`versionNumber`) or its path changes, the service reads the GPO
   folder from the SYSVOL of its controller (SMB 3, read only), parses the settings files and compares them with the
   previous snapshot – the event shows "setting: was → is", and it inherits the actor from the GPO object change (5136).
   The daily reconciliation finds file changes made bypassing GPMC (no version change; actor "undetermined –
   SYSVOL_NOT_AUDITED"). A GPO folder pointing to another server is not read at all – that is a critical event.
4. **Notifications.** Rules decide which events are sent and to whom: immediately (batched in a short window, with a
   per-hour message limit and taking working hours and Polish holidays into account), in the daily report, or to
   Teams / a webhook. Deliveries wait in a durable queue and are retried after errors.

---

## 3. Requirements

- **Server:** a virtual machine with **Debian 13** (recommended) or 12, amd64, 2 vCPU, 4 GB RAM, 60 GB disk (at least
  40 GB on `/var` – 3 years of history and database backups), optionally vTPM; **not joined to the domain**, on a Tier 0
  virtualization platform. **ASP.NET Core Runtime 10 at version 10.0.13 or later** from the packages.microsoft.com
  repository (automatic updates). The `admng-watchdog` and `admng-openwec` packages from the release.
- Treat the server, its backups, the keytab and the service account as **Tier 0** assets (like domain controllers):
  whoever has root on the server has the service account's identity, the change history, and can silence the alerts.
  Administer it only with SSH keys from a PAW workstation.
- **Service account:** the computer object `ADMNG-WD$` created by a Tier 0 administrator (AES only, "cannot be
  delegated", in a Tier 0 OU), with read-only rights; its password is changed by the `admng-watchdog-keytab` tool on the
  server. Conditions for production deployment: `ms-DS-MachineAccountQuota` = 0 and a deny-write entry for SELF on the
  account object. The "Replicating Directory Changes" right is not needed.
- A DNS record of type **A** for the service (e.g. `admng-wd.contoso.com`) – not a CNAME – and a PTR; server time from
  the controllers (chrony).
- **TLS certificate:** from the company CA or generated by the package (self-signed – AD Manager 0.3.0 then asks to
  confirm the fingerprint on the first connection).
- **Domain controllers:** Windows Server 2016 or later (pilot – 2025 and 2022), Kerberos AES (RC4 is being disabled on
  DCs in 2026); for attribution – audit policies, SACLs and the Windows Event Forwarding GPO (no agents on the DCs).
- For emails: an SMTP server (Exchange Online, on-premises Exchange or a relay) with TLS; Teams – optional.
- Administrator workstations with **AD Manager 0.3.0** in a subnet from which the server's firewall allows port
  8443/TCP; there must be no proxy server or TLS inspection device in front of the service.

Details (ports, permissions, auditing, order of steps): [WATCHDOG-LINUX.md](WATCHDOG-LINUX.md), chapters 0–3.

---

## 4. Step-by-step deployment

Deployment of version 0.3.0 is described in [WATCHDOG-LINUX.md](WATCHDOG-LINUX.md):

| Stage | Chapter |
|---|---|
| Test domain (lab with Windows Server 2025 and 2022 controllers) | 1 |
| Preparing AD: role groups, DNS, the `ADMNG-WD$` account, read permissions, SYSVOL, `ms-DS-MachineAccountQuota`, BadSuccessor, auditing, SACL, WEF GPO | 2 |
| Debian server: system, .NET, packages, SSH, firewall, DNS and time, Kerberos, keytab, password change every 30 days, certificate, backups | 3 |
| OpenWEC – receiving events from the controllers | 4 |
| Service: startup settings (`/etc/admng-watchdog/appsettings.json`), first start, exit codes, health checks | 5 |
| AD Manager: connection, certificate confirmation, first steps | 6 |
| Pilot – checklist | 7 |
| Rollback, troubleshooting | 8, 9 |

The Windows service (gMSA, `Install-AdMngWatchdog.ps1`, HTTP.sys, DPAPI-NG) is available only as release 0.2.0 – its
guide: [WATCHDOG.md, chapter 4 in version 0.2.0](https://github.com/DarienRahl/AD_MNG/blob/v0.2.0/docs/WATCHDOG.md#4-wdrożenie-krok-po-kroku).
After release 0.3.0 its code will be removed. Moving from 0.2.0 to Linux while keeping the history (a copy of the
`watchdog.db` database, without the `keys\` folder – the SMTP password and Teams address are entered again):
WATCHDOG-LINUX.md, the "Migration from version 0.2.0" box in chapter 0.4.

---

## 5. Configuration in the Watchdog tab

### 5.1 Rules

A rule defines: event kinds, object kinds, scope (OU – include/exclude), optionally attributes, tags
(e.g. "privileged account", "service account"), groups (privileged / watched / specified), minimum priority, actor
exclusions (by SID), and delivery: immediate (email recipient groups, Teams/webhook channels), daily report, hours
(always / working hours only / default: always for high and critical), collection window (default 5 min), message limit
per hour (default 10; critical events have no limit) and suppression of repeats for the same object (lockouts: 1 h).
The per-hour message limit (per rule and channel) counts messages **added to the queue** in the last hour, not only
those already sent – so a channel failure does not allow a series to pile up that would go out all at once after the
repair; after a service restart only messages already sent are counted.

Built-in rules can be edited, disabled and restored to their defaults; the "tier0-*" rules cover critical security
changes. The "Watchdog service health" rule can only be edited (not disabled); it also sends to all enabled
Teams/webhook channels. The **Preview** button shows how many events from the last days a rule would match and how many
messages it would send.

- Shadow Credentials (`msDS-KeyCredentialLink`) on an ordinary user account written by **another** account trigger an
  alert of the "tier0-security" rule right after the actor is determined – even when a custom rule for the same
  recipients matched the event earlier but excludes that actor.
- **Several rules, the same recipients**: a recipient (email group, channel) gets one alert per event, with the
  strongest setting of all the rules that currently cover it – the highest priority, the shortest collection window,
  delivery also outside working hours if at least one of them delivers that way; the alert shows the rule highest on the
  list. A rule that cannot send at that moment (working hours, rollout mode, repeat suppression) does not affect the
  alert. This also applies to changes after the actor is determined: the built-in "tier0-security" rule, which matches
  the event only after attribution, takes over the held notification of the user's own lower-priority rule (the alert
  has the priority and the name "tier0-security" and does not wait for working hours), and when the rule of the held
  notification stops matching (e.g. it excludes the determined actor), the notification is taken over by the next rule
  that still matches.
- Custom rules with required tags "password reset" (`passwordReset`), "own change" (`selfChange`) or "sync account"
  (`syncAccount`) work – these tags are assigned after attribution, and the rules are then evaluated again.
- After re-evaluation, recipients who already received an alert about the event with at least the same priority and with
  a determined actor (or will receive a "Follow-up") do not get a second alert from the new rule. In a batched alert,
  the priority with which **this** event was shown counts, not the highest priority of the whole message. The service
  checks this once more right before sending, so an alert sent at the same moment as the re-evaluation will not be
  repeated, and the takeover of a notification by the "tier0-security" rule will not be lost when the held notification
  is being sent just then. A priority assigned by a rule (e.g. "Critical") stays after the actor is determined.
- **Priority raised after the actor is determined (escalation)**: when a recipient has already received an alert about
  an event, and after the actor is determined a rule that covers it – the same one that sent the alert, or a new one
  (e.g. "tier0-security") – gives it a high or critical priority, higher than it saw, it gets **one** follow-up in the
  thread of that alert, not a second alert: an email with the subject "Re: …" (`In-Reply-To`/`References` headers), the
  banner "Follow-up: priority raised" or "Follow-up: actor determined, priority raised" and the line "Priority:
  Medium → High"; in Teams a follow-up card with the same title and the "Priority" line for each event; a webhook of
  type `ad.severity.escalated` (5.4). This also applies to a "medium" alert sent with the actor "determining…" after the
  wait time elapsed (for which the regular follow-up does not arrive) and to an event shown as medium in a batched alert
  – the follow-up covers only events whose priority was raised. If a follow-up with the actor would come anyway (a high
  alert that becomes critical), the recipient gets one message with the actor and the new priority. Escalation is sent
  only once (also after a service restart), only after the alert was delivered and with a determined actor. The rule's
  delivery hours work as for an alert (by default high and critical – always; a "working hours only" rule does not
  escalate outside them, the daily report shows the event), as do rollout mode and repeat suppression: a rule that
  stayed silent for this event as a repeat does not send an escalation with its priority.
  Escalation goes through the channel's limits and retries, does not depend on the `notifications.followUpWhenAttributed`
  setting and – like any follow-up – is not held back by the rule's per-hour message limit (there is at most one per
  alert and recipient). A priority decrease never sends a new message – the regular follow-up with the actor then shows
  the new priority (e.g. "High → Info" for a user's own password change).
- **Follow-ups show the priority of each event**: in a follow-up to a batched alert every event has its final priority
  ("Priority" column), not the highest priority of the whole alert.
- **Own priority vs. displayed priority**: a priority set in a rule raises the event's priority visible in the list, in
  the daily report and in the counters, but does not affect the matching of other rules – the "minimum priority" of each
  rule is compared with the event's own priority (detection, tags, attribution), also on re-evaluation after the actor
  is determined and when releasing a held notification. A review rule that raises privileged accounts' password changes
  to "High" therefore will not make a user's own password change (own priority: informational) send an alert of the
  "Privileged and service accounts" rule. Events stored before a service update take their previous priority as their
  own.
- `SuppressComputerSelfChanges` (on by default) also works when `TrackComputerPasswordChanges` or
  `TrackComputerOsChanges` are on: routine changes that a computer makes to itself (password rotation, OS version) do
  not match any rule but remain in the event list.
- The **"Configured DNs that no longer exist"** state (`rules.configuredDns`, checked at most every 10 minutes) lists the
  OUs and groups named in the configuration (rule scopes, privileged and watched groups, subtrees, critical objects,
  exclusions) that were renamed, moved or deleted – a rule with an old DN covers nothing. The service never corrects the
  configuration itself; `CN=Builtin`, `CN=System` and excluded subtrees are not checked.

**Exclusions** (e.g. the Entra Connect sync account that constantly changes `msDS-KeyCredentialLink`, or an account
that cleans up old computers) require a reason, identify the actor by SID (selected from AD in AD Manager), may have an
expiry date, are recorded in the configuration change history and are listed in every daily report. "Created by / when"
is determined by the service (the account of the administrator who saved the exclusion and the server time) – editing
an exclusion (reason, expiry date, scope) sets them anew, including the exclusion's age shown in the daily report.
Actor exclusions saved with the `{domainSid}` or `{rootDomainSid}` placeholder exclude nothing until the service knows
the domain SID (safe denial). Watchdog itself **suggests** such exclusions (Rules tab → "Add exclusion"), but never
adds them automatically.

**Protection against silencing**: every saved configuration change triggers a "Watchdog configuration change" event
(who, what), also sent to the **previous** recipients – removing yourself from the recipient list does not go unnoticed.
Disabling, through the API, the observation of attributes and critical events (e.g. `msDS-KeyCredentialLink`,
privileged access), or excluding the domain or the domain controllers OU, as well as a subtree with privileged groups
and accounts, and also disabling or narrowing the rules `tier0-badsuccessor`, `tier0-machine-quota`, `watchdog-self` and
`tier0-gpo`, is impossible without local consent (`AllowTier0Suppression` in the startup settings
`/etc/admng-watchdog/appsettings.json`, which only the server's root changes – [WATCHDOG-LINUX.md](WATCHDOG-LINUX.md)
5.2). Likewise, the event retention cannot be shortened below the `MinEventsDays` threshold (1095 days by default), and
every shortening of the retention is a critical event.

Working hours: by default Mon–Fri 7:00–17:00 (Europe/Warsaw time zone) with Polish holidays (including Christmas Eve
from 2025). Outside working hours, notifications with a priority lower than high go only to the daily report – this also
applies to a batched notification or one held by the limit whose wait ends after working hours (it is not sent, it
goes only to the report).

### 5.2 SMTP

| Mode | When | Settings |
|---|---|---|
| Relay without logon | on-premises Exchange (receive connector restricted to the Watchdog server's IP) or Exchange Online through an inbound connector (certificate/IP) at `<tenant>.mail.protection.outlook.com:25` | port 25, **STARTTLS (required)** – the "when available" variant is only a warning (an attacker on the network could disable encryption and read the alerts); authentication: none |
| Logon (Basic) | external relay, on-premises Exchange | port 587, STARTTLS (required), user + password (stored encrypted, write-only) |
| Exchange Online OAuth2 | Exchange Online – **Basic SMTP AUTH is disabled by default from the end of December 2026** | `smtp.office365.com:587`, STARTTLS, OAuth2 authentication, Entra cloud (global / US Government / China), user = sender mailbox |

Preparing Exchange Online OAuth2 (one-time, Entra/Exchange administrator):

1. Entra ID → App registrations → a new application "AdMng Watchdog SMTP"; application permission **Office 365
   Exchange Online → SMTP.SendAsApp**, admin consent; add a **client secret** (the Linux service in version 0.3.0
   supports only a secret – a certificate from the `LocalMachine\My` store worked only in the Windows 0.2.0 service; a
   secret has a limited validity, note its renewal date in your calendar).
2. Let the sender be a **shared mailbox with sign-in blocked** (no password that could be used to sign in). Exchange
   Online PowerShell:
   ```powershell
   New-ServicePrincipal -AppId <Application (client) ID> -ObjectId <Object ID from "Enterprise applications">
   Add-MailboxPermission -Identity watchdog@contoso.com -User <ServicePrincipal Id> -AccessRights FullAccess
   Set-CASMailbox -Identity watchdog@contoso.com -SmtpClientAuthenticationDisabled $false
   ```
   (The Object ID must come from "Enterprise applications", not from "App registrations".)
3. In the tab: cloud, tenant ID, application ID, client secret, sender
   `AD Watchdog <watchdog@contoso.com>`. The OAuth token is sent only to servers on the `OAuthSmtpHosts` list.

Passwords and secrets are **bound to the destination**: after changing the server, port, user, tenant or channel
address you must enter the password/secret again – a stored secret will never be sent to a new address (also with a
test message). The SMTP password and the XOAUTH2 client secret are also bound to the verification of the SMTP server's
certificate: every change of the pinned fingerprint, of the verification mode or of revocation checking requires
entering the password/secret again, also before a test message (otherwise a Watchdog administrator could pin the
certificate of their own server and intercept the password). Secrets stored by older versions with custom verification
must be entered again after the update.

The **Send test message** button shows every stage (connection, TLS with version and cipher, logon, sender, recipients,
content) with response codes and hints, e.g. `5.7.139`/`5.7.30` – SMTP AUTH disabled, `5.7.68` – "Direct Send"
rejected. The SMTP server certificate is always verified (optionally pinning the SHA-256 fingerprint; "accept any"
exists only in developer mode); only TLS 1.2 and 1.3 are used.

**Many recipients and limits.** The recipient list is sorted and split into messages of `MaxRecipientsPerMessage`
addresses each (50 by default). The `MaxMessagesPerMinute` limit (20 by default, below the Exchange Online limit of
30/min) counts **every SMTP message**, that is every such part, and not every queue entry – so large groups use up more
of the limit, and the delivery may be postponed in the middle without resending the parts already sent. A follow-up
(e.g. "actor determined", "priority raised") goes to the same recipients in the same thread – each part replies to the
corresponding part of the original alert.

**Partial delivery.** When the server rejects only some of the recipients:

- temporary rejection (4xx, e.g. greylisting) – the others get the message, and the retry goes only to those rejected,
  with no duplicates for the others (warning 2202 in the service log); after a service restart, however, the retry may
  go to everyone again,
- permanent rejection (5xx) – the rejected address is skipped (warning 2203), the others get the message; the delivery
  ends with a permanent error only when all were rejected,
- if one recipient answers 4xx for about 22 hours, the message ends in the "Undelivered" state with the code
  `partial.smtp.4xx…`, and the email channel has a warning state – without a "channel cannot deliver notifications"
  alert. A retry from the delivery log (Admin role, within 14 days) then goes only to the missing recipients.

### 5.3 Microsoft Teams (Workflows)

The old "Office 365 Connectors" webhooks (`*.webhook.office.com`) were disabled on May 18–22, 2026 – Watchdog rejects
them. Use **Workflows** (Power Automate):

1. In a Teams channel: … → Workflows → the template "Post to a channel when a webhook request is received" / "Send
   webhook alerts to a channel" → copy the URL.
2. In Power Automate add **co-owners** to the flow (a flow belongs to the person who created it – when they leave, it
   stops working) and turn on "Secure inputs/outputs" for the trigger and the card-posting action (the run history
   stores the content for 30 days).
3. In the Watchdog tab → Channels → Teams: paste the URL (it is a secret – it contains the `sig` signature; after
   saving only `scheme://host/…` is visible, without the path and parameters – hints saved by older versions are
   shortened automatically), choose the level of detail (default "Standard") and send a test card.

A 202 response only means acceptance by Workflows, not publication in the channel – check whether the test card
appeared. Flows of owners without a premium license are suspended after 90 days without invocations; the daily report
(by default also sent when there are no changes) prevents this. The "Specific users in my tenant" mode (an Entra ID
token instead of a signature in the URL) is supported: an Entra application with a certificate, scope
`https://service.flow.microsoft.com//.default`; the token is sent only to `*.environment.api.powerplatform.com`
addresses.

### 5.4 Webhook (SIEM, ticketing systems)

JSON `POST` requests compliant with **Standard Webhooks v1**: the headers `webhook-id` (constant across retries – the
idempotency key), `webhook-timestamp`, `webhook-signature: v1,<base64 HMAC-SHA256>`; the `whsec_…` secret is generated
by the tab (shown once). Optionally the `X-Hub-Signature-256` header (GitHub style). Event types: `ad.user.created`,
`ad.user.locked_out`, `ad.group.member_added`, `ad.privilege.granted`, `ad.burst.mass_lockout`,
`watchdog.configuration_changed`, `watchdog.digest`, `watchdog.heartbeat` … Follow-ups have the same `data.eventId` as
the alert: `ad.attribution.updated` (actor determined) or `ad.severity.escalated` when the event's priority rose
(5.1; with the final actor); when the priority changes, the `data.previousSeverity` field contains the previous priority
(e.g. `"severity":"high","previousSeverity":"medium"`). The default level of detail is "Minimal" (no attribute values).
The **heartbeat** option (e.g. every 15 min) lets the SIEM raise an alarm when Watchdog stops reporting. Heartbeat
signals are never retried or sent after their interval has elapsed: during a SIEM outage they appear in the delivery
log as "Undelivered" with the code `heartbeat.superseded`, and the SIEM receives only fresh signals. Verification on the
receiver's side (Python):

```python
import base64, hmac, hashlib, time
def verify(secret: str, msg_id: str, ts: str, body: bytes, signature_header: str) -> bool:
    if abs(time.time() - int(ts)) > 300: return False
    key = base64.b64decode(secret.removeprefix("whsec_"))
    expected = base64.b64encode(hmac.new(key, f"{msg_id}.{ts}.".encode() + body, hashlib.sha256).digest()).decode()
    return any(hmac.compare_digest(expected, s.split(",", 1)[1]) for s in signature_header.split())
```

When rotating the secret, enter the old one in the "Previous secret" field – until it is removed, both signatures are
sent.

### 5.5 Daily report

Every day at 7:30 (also on weekends – as a signal that the service is running). **The report of the first business day
after a weekend or holiday covers the whole period since the last business-day report** (e.g. from Friday 7:30 to Monday
7:30), so nothing from the weekend slips through (the option "After days off the digest covers the whole period since
the previous one", on by default). The period of such a report starts where the last business-day report actually ended
(e.g. at 10:00 when Friday's report was sent late after a service outage). "Send now" on Monday after the morning report
does not cover the weekend again.

The report period may end a few seconds or minutes before the sending time when some event has not yet been through rule
evaluation or is still waiting for the actor to be determined – it goes to the next report, already with its final
priority and actor (nothing is lost and nothing is counted twice). A report interrupted by stopping the service is
finished after it starts and is not sent a second time; restarting the service shortly after a report was sent does not
cause it to be sent again.

Contents: service health, counters, **"Needs attention"** (high and critical events with acknowledgment information; an
event whose priority was changed in the alert's thread after the alert was sent – raised, or lowered in a follow-up –
has the note "(priority: Medium → High)"), sections: new accounts, new computers (by OU), deletions, enabling and
disabling, moves, privileged access, group membership, account parameter changes (old → new value, who, when),
lockouts (one row per account and day: "locked out 5× today, last at 10:42" – "today" counted from actual local
midnight, also on daylight-saving change days – and the sources of bad passwords), passwords (separately resets by
administrators, changes by users and validity extensions), domain, GPO and PKI, security changes for review, upcoming
deadlines (≤ 14 days), and on Mondays also inactive accounts (≥ 90 days) and hygiene counters (password not required,
password never expires, unconstrained delegation, orphaned `adminCount`, krbtgt password age). Active exclusions and
Watchdog configuration changes are listed at the end. The report is sent also when there are no changes ("No changes in
the period") – its absence indicates a problem with the service.

The **preview** of the report in the tab (Daily digest → Preview) in the "Active exclusions" section shows, for each
role, only their count ("Number of active exclusions (details only in the sent report)"); the sent report still lists
them.

### 5.6 Levels of detail

| Level | What it contains |
|---|---|
| Minimal | event kind, object, priority, actor (account), link – without attribute values and IP addresses (text search in the tab then does not take the workstation or IP into account either) |
| Standard | as above + attribute values other than personal data (phones, employee number, notes, office, home directory, logon hours) |
| Full | all values except secret ones (passwords, LAPS, BitLocker, confidential attributes – never) |

Defaults: email to administrators – full, helpdesk – standard, Teams – standard, webhook – minimal, managers
(notifications about account expiry) – minimal, the Viewer role in the tab and on the event page – standard
(`ViewerDetailLevel`).

Texts coming from AD (descriptions, names of objects and groups) are "defused" in notifications: invisible control
characters (e.g. text direction reversal) are shown explicitly, and addresses in the content are turned into
`hxxps://przyklad[.]pl` so that Outlook and Teams do not make them clickable links. Control characters in values stored in
DC logs are shown as "�" in the attribution evidence – the record is not rejected because of it.

### 5.7 Delivery queue, retries and channel state

- **Unhealthy channel** (Teams flow disabled, webhook answers 404/410, authentication error): the service retries the
  delivery every hour, but those retries do not increase the "Attempts" counter in the delivery log. After 24 hours the
  message is dropped ("Undelivered"), and the first temporary error after the channel is repaired is retried within a
  few seconds.
- **Disabling or deleting a Teams/webhook channel**, or disabling SMTP, immediately drops the messages waiting in that
  channel's queue ("Undelivered", codes `channel.disabled` / `channel.unknown` / `smtp.disabled`), without a "channel
  cannot deliver notifications" alert. Re-enabling the channel does not deliver them; an Admin can retry them manually
  from the delivery log until `OutboxDeadDays` elapses (90 days by default).
- **Channel state** (Status tab): an error persists for 2 hours from the last failure at the error level, then changes
  to a warning until the next successful delivery – even when nothing is being sent. The next failure after that time
  triggers a new "channel cannot deliver notifications" alert.
- Partial delivery of emails and SMTP message limits are described in chapter 5.2, heartbeat signals – in 5.4.
- **Messages to users** (chapter 5.8) go through the same queue, but **after** the waiting alerts of their channel, and
  with an unhealthy channel or an authentication error they are dropped immediately (AD Manager has already received
  the error information).

### 5.8 Messages to users (from AD Manager)

AD Manager can send messages to users from templates ("password expires on November 5", "account will be disabled" …,
[README, U1](../../README.md#u1--e-mail-messages-to-users-smtp-)) directly through SMTP or **through this
service** – it then uses its SMTP channel (also Exchange Online with OAuth2), secrets, queue with retries and the
delivery log. The feature is **off by default**.

**Enabling:** the Watchdog tab → *Channels and SMTP* → the *Messages to users (from AD Manager)* card (Admin). The card
changes the first four settings of the table; `userMessages.allowInlineImages`, `userMessages.maxStoredImageMb` and
`retention.userMessageAuditDays` are changed by the Watchdog administrator through the API (below):

| Setting | Default | Meaning |
|---|---|---|
| `userMessages.enabled` | off | allows AD Manager to send messages to users (requires SMTP to be enabled with a sender) |
| `userMessages.maxMessagesPerHour` | 300 | how many messages the service accepts in the last 60 minutes – from all operators together |
| `userMessages.maxMessagesPerDay` | 2000 | the same for the last 24 hours |
| `userMessages.allowedSenders` | empty | additional allowed senders (*From*) and reply addresses: a whole address or `@domain`; the SMTP channel's sender and reply address are always allowed |
| `userMessages.allowInlineImages` | on | messages may contain images embedded in the body (a logo, a screenshot from instructions – below); off: a message with images is rejected (`imagesDisabled`), and AD Manager offers sending without images or directly through SMTP |
| `userMessages.maxStoredImageMb` | 256 (16–4096) | how many megabytes of images may wait in the queue at once (messages not yet sent and dropped ones); above that – 429 `imageStorageFull` for messages with images, messages without images are not held back |
| `retention.userMessageAuditDays` | 365 | how long the audit of sent messages is kept |

**Changing through the API** (with the Kerberos ticket of a Watchdog administrator account): read
`curl --negotiate -u : -D naglowki.txt -o config.json https://admng-wd.contoso.com:8443/api/v1/config`, change the values
in `configuration.userMessages` (or `configuration.retention`) of the `config.json` file, then save
`curl --negotiate -u : -X PUT -H 'X-AdMng-Client: 1' -H 'Content-Type: application/json' -H 'If-Match: <ETag from naglowki.txt>' --data-binary @config.json https://admng-wd.contoso.com:8443/api/v1/config`.
If someone changed the configuration in the meantime, the service answers 412 – read it again. Delete both files after
the change.

Changing the switch and the sender list has the severity "high" in the configuration audit, changing the image settings
– "medium". In AD Manager the *How messages are sent* setting = *Automatic* (the default) chooses Watchdog when the
connection to the service is configured and the feature is on – otherwise it sends directly through SMTP; *Through AdMng
Watchdog* shows the reason when the service cannot accept the message (off, no permissions, no connection, older version
of the service).

**Who can send:** Watchdog administrators **and viewers** (the helpdesk are usually viewers); `DeniedSids` wins as
everywhere. Only an administrator changes the configuration.

**What the service checks** (independently of AD Manager, for every message – `POST /api/v1/user-messages`):

- one message per account: 1–2 *To* addresses, at most 10 together with *CC*; each address is exactly one mailbox,
- no CR/LF or other control characters in the subject, addresses and names (the message is rejected, not "repaired"),
  subject up to 250 characters, text body up to 50,000 characters, HTML up to 400,000 (a service from before U1c:
  100,000),
- HTML without scripts, frames, forms, `on…=`, `javascript:`/`data:`/`vbscript:`/`file:` – checked with **the same
  sanitizer as in AD Manager** (HTML5 parser, allow-lists of tags, attributes and CSS properties); the only image source
  may be the `cid:` of an image sent in this message; the service sends the document after this check; language `pl` or
  `en`; a message **without** embedded images (as AD Manager from before U1c also sends it) is rejected only for what the
  U1b version rejected (scripts, frames, forms, `on…=`, disallowed schemes, too-deep nesting) – an image from a web
  server, hidden text or an overlay are removed from it before sending and do not cause a rejection,
- **no attachments** – a request with an unknown field (e.g. `attachments`, `bcc`) is rejected; images embedded in the
  body (below) are not attachments,
- *From* and *Reply-To* only from the SMTP channel configuration or the list of allowed senders; the **envelope sender**
  (MAIL FROM) is always `smtp.from`. The service does not know AD Manager's list of allowed link domains – AD Manager
  checks that one.

**Images in the body (U1c).** A message pasted in AD Manager from Outlook may have images embedded in the body (the
`inlineImages` field, [README, U1c](../../README.md#u1--e-mail-messages-to-users-smtp-)). The service does not
trust the client and for every message checks:

- only **PNG, JPEG and GIF** – recognized by content (signature, file structure up to the end marker, dimensions at most
  4096 × 4096 px), never by name; SVG, WebP and other formats are rejected (`invalidImage`),
- at most **10 images**, **1 MiB** each and **2 MiB** in total (`tooLarge`),
- the image identifier (`Content-ID`) computed from the SHA-256 hash of its bytes (`imageIdMismatch`), every image used
  in the HTML (`unreferencedImage`) and every `cid:` in the HTML pointing to a sent image (`missingImage`),
- metadata (EXIF with GPS, XMP, IPTC, comments) is removed before sending; the images go as *inline* parts of
  `multipart/related` with a `Content-ID` that the HTML refers to – **never as downloadable attachments**.

Only the `POST /api/v1/user-messages` route accepts a request body of up to **6 MiB** (images in base64); all other API
routes stay at 1 MB. `GET /api/v1/user-messages/status` reports the `inlineImages` capability and the limits (number and
size of images, HTML length, the occupancy and limit of image space in the queue). An older service does not report it
(and rejects the unknown field as `invalidJson`) – AD Manager then does not silently send without images but stops the
delivery and offers *Send directly through SMTP* (this delivery only) or *Send without images* (the preview then shows
the image descriptions). The content hash for the idempotency check also covers the images.

**Result for AD Manager.** The message goes to the delivery queue (kind "Message to a user"), and the request waits up
to 20 seconds for the first SMTP attempt:

| Response | When | Report in AD Manager |
|---|---|---|
| `Delivered` (200) | the SMTP server accepted the message for the *To* addressee | accepted (rejected *CC* copies as a warning) |
| `Rejected` (200) | permanent rejection 5xx, e.g. `smtp.550.5.1.1` | failed, code and hint; "Undelivered" in the delivery log |
| `Queued` (202) | channel rate limit, a temporary 4xx error/timeout, or no attempt within 20 s | **accepted** with the note "in the AdMng Watchdog queue" – the service retries like with alerts, the result is in the delivery log |
| `Failed` (200) | authentication, TLS or channel configuration error | failed; the run is stopped (the rest "not sent"), the message is dropped immediately |
| 409 / 429 | feature off / hourly or daily limit (with `Retry-After`) | the run is stopped, the rest "not sent" – *Retry failed* later |
| 429 `imageStorageFull` | images waiting in the queue reached `userMessages.maxStoredImageMb` (`Retry-After` 1 h) | the run is stopped; messages without images still go through |

Every request is **idempotent** by message identifier: a retry after a timeout or a broken connection returns the state
of the first submission and does not send the message a second time; the same identifier with different content or from
another person is a 409. `GET /api/v1/user-messages/status` (administrators and viewers) shows whether the feature
works, the channel (`host:port`), the sender, the allowed senders, the limits and the number of messages in the last
hour and day.

**Data and audit.** The subject, body, **embedded images** and addresses exist only in the content of the message in the
queue – deleted right after sending, failed ones after `OutboxDeadDays` (90 days, for manual retry). The size of the
images of queued messages is recorded in the `outbox.image_bytes` column (0 after sending) – the
`userMessages.maxStoredImageMb` limit is computed from it; images are not in the audit, in the delivery log or in the
diagnostic log. For messages to users the delivery log shows only the kind, state, number of recipients and codes –
**without the subject and addresses**. The audit (`user_messages` in `watchdog.db`): who, when, number of recipients,
the SHA-256 hash of the sorted addresses (it allows checking "did this address receive a message" without storing it)
and the content hash (idempotency check) – for `retention.userMessageAuditDays`. The diagnostic log contains one line per
message (identifier, operator account, number of recipients) – without addresses, hashes and content.

---

## 6. The Watchdog tab in AD Manager

Opening: menu **Tools → Watchdog**, the command palette (Ctrl+K → "Watchdog") or **Ctrl+Shift+A**; for a selected
object: context menu / details pane → **"Change history (Watchdog)"** (the events of that object by GUID).
The tab works independently of the AD connection (it stays open after a DC change); it connects to the service over
HTTPS (only `https://`) with the current Windows account (`runas /netonly` works too); the connection bar always shows
the service address.
The tab header shows the number of unacknowledged high and critical events (refreshed every minute, also in the
background). AD Manager does not write Watchdog data to disk – in the settings it remembers only the service address, the
layout and the named filters.

- **The status bar** shows "status + host" once; a second row appears only when a problem needs explaining.
  While the Watchdog tab is selected, the details pane of the main window's object is hidden (the Ctrl+D choice is
  remembered for the object lists). Critical events have an outline around the red dot to tell them apart from high
  ones. The event details pane is 400 px wide; with the splitter it can be widened or narrowed down to 300 px.
  The "Saved filters" list appears only when at least one saved set exists. "Saved filters", "More filters" and the ✕
  "Clear filters" button are always together at the end of the first filter row; in a narrow window the other filters
  wrap to the next row.
- **List columns** of events, rules and the delivery log (the Status subtab) fit within the list width – the list does
  not scroll sideways, also at the default AD Manager window size (1360 × 840, the size the program opens at) with the
  event details pane 400 px wide. The event list always shows priority, time, event and object; the event title takes the
  space left after the other columns. The ✓ (acknowledged), Actor, Source, Location and Lockouts columns
  appear in that order when the list has room for them (a wider window, a narrower details pane) – their values are
  always in the event details pane. In the rule list the name, event kinds and notifications share the width in the
  proportion 4 : 3 : 3; a tooltip shows a shortened rule name. In the delivery log the subject and the error share the
  space left after the other columns (3 : 2). A column width changed by the operator (dragging the header edge or
  double-clicking it, which fits the column to its content) is kept, and the other columns make room for it at once.
- **Connection problems**: a single request that timed out or did not reach the service (e.g. a slow save or a text
  search) shows an error in its own subtab, and the tab stays open. The tab hides behind a "Retry" banner only when the
  live updates fail too, and comes back by itself after the next successful status read (within 30–60 s), without
  clicking "Retry". After a failed connection to a **different** service address you have to click "Retry".
- "Retry" or F5 after a connection problem **keep** unsaved configuration changes and the passwords/secrets the
  administrator has typed, if the service address has not changed; changing the service address loads the configuration
  of that service.
- Closing the Watchdog tab (Ctrl+W, the close button, the middle mouse button) or the main window with an unsaved
  configuration asks: "The Watchdog configuration has unsaved changes (typed passwords and secrets included). Discard
  them?". Answering "No" leaves the tab and the window open, and the tab stays selected.
- **The event list**: "resolving…" is replaced automatically (in the row and in the details pane) about 30 s after the
  actor is determined, as long as the tab is visible. With a filter on what the service fills in after storing the event
  (priority, tags, attribution status, actor, rule, text), an event that starts to match only after attribution
  appears in the list in its place within about 30 s – also many at once (e.g. a bulk operation) and also when the
  service takes longer than the default 30 minutes to determine the actor: the list re-checks events detected during the
  period in which the service may still be determining the actor (the last attempt from `attribution.pendingRetries` in
  the service configuration plus a margin, at least 2 hours back, counted from the last full check of the list – so
  changes from the time when the tab was hidden or the service was unreachable appear on return; for the same period
  after the service restarts, the list also checks events stored before it stopped, because only then does the service
  finish evaluating and attributing them). A row that has stopped matching the
  filter (e.g. a lowered priority or a different actor) disappears from the list; a selected row is dimmed with the
  tooltip "No longer matches the filter" and disappears once it is deselected. An event acknowledged in another console
  stays in the list as acknowledged (like one acknowledged by the operator). The live list keeps the 2000 newest events
  in memory (more if the operator loaded older pages); older ones are available again with the "Load older" button. New
  and late-matching events and "Load older" do not scroll the list: when the operator is reading older events (the list
  scrolled down), the rows being read and the selection stay in place, and the "N new" button announces the new ones.
  After a refresh (F5) or a filter change, a scrolled list shows the same event if it still matches (otherwise the
  selected one, and without a selection – the newest).

| Subtab | Contents |
|---|---|
| Events | a live list (new events appear on their own), filters (priority, category, kind, object, OU, tags – e.g. "privileged", attribution status – e.g. "show undetermined", actor, text also by workstation/IP, dates, unacknowledged) with saved sets, acknowledging many events at once; details: changes old → new, the actor and source with the confidence level and evidence (DC, event number), for lockouts the source of bad passwords, related events, notifications sent; "Acknowledge", "Show in AD Manager" (the current location of the object by GUID; a deleted object – the AD Recycle Bin window), "Unlock account" (through a change plan with preview), "Copy", CSV export (only on the operator's command) |
| Rules | the rule list, the editor, exclusions and proposed exclusions, preview of the effects, the deployment mode banner |
| Channels and SMTP | SMTP, recipient groups, Teams and webhook channels, test sends with diagnostics |
| Daily digest | the schedule, recipients, a preview of the content, "Send now" |
| Status | the connection to the DC, the USN marker, the baseline snapshot, the permissions check, the logs of each DC (attribution coverage, the number and time of source queries), channels, the database and backups, the delivery log (retries), the configuration change history; from 0.3.0 also the sections **Service account** (kvno, last and next password change, encryption types, keytab consistency with AD, membership in privileged groups), **WEF collector** (for each DC: the last record, delay, backlog, records stored and rejected), **GPO content** (read progress, errors, SYSVOL delay) and **Host** (system, .NET environment, secrets key variant, free space, clock difference, LDAP encryption); every reported status code has a description and a recommendation – among others an expiring TLS certificate and its private key, database size and maintenance, notification scheduling, "Configured DNs that no longer exist", domain controller discovery, the attribution source, denied search for changes in the directory (`permission.search`) |
| Sources and retention (from 0.3.0) | attribution (enabled, the "OpenWEC (WEF)" source, dropping computer account and anonymous logons, collector silence thresholds), GPO content (enabled, SYSVOL delay tolerance, file limits) and the retention time of logon records |

**The service certificate (from 0.3.0).** When the service certificate does not come from a trusted authority (the
self-signed variant), AD Manager on the first connection shows a window with the SHA-256 fingerprint, the name and the
validity dates; the operator compares the fingerprint with the output of `admng-watchdog cert-fingerprint` on the server
and clicks "I trust this certificate" – the fingerprint stays pinned in the settings (Settings → Watchdog, "Remove
pin"). A changed certificate is never accepted silently (the window shows the old and the new fingerprint), and a name
mismatch never. Changing the service address removes the pin.

The Viewer role sees everything read-only (without exceptions, recipient addresses and SMTP server data); editing
requires the Admin role.

---

## 7. Data, retention and backups

The service data folder is `/var/lib/admng-watchdog` (owner `admng-watchdog`, mode 0700, checked at every start – with
broader permissions the service does not start).

| Data | Where | Default retention |
|---|---|---|
| Snapshot of watched attributes (without secrets) and memberships | `watchdog.db` | current state; deleted objects 180 days |
| Events (changes, attribution – who and from where, acknowledgments) | `watchdog.db` | **1095 days** (3 years; the `MinEventsDays` threshold in the startup settings – shorter only after a change by root) |
| Normalized events from DC logs related to changes (only the fields needed for matching; values of watched, non-secret attributes only) | `watchdog.db` | 30 days; the oldest are deleted earlier when the database exceeds the size limit (4096 MB by default) |
| Logon and failed logon records (4624 type 3 and 10, 4625, 4771, failed 4776 – without computer account and anonymous logons) | `watchdog.db` | 48 hours (the determined source is copied to the event) |
| GPO settings snapshot (only the newest for each GPO; GPP passwords and values that look like passwords – only the information that they are present; scripts – only the SHA-256 hash) | `watchdog.db` | current state; deleted GPOs – until restored |
| The outbox (message content – also user messages from AD Manager and their embedded images, chapter 5.8) | `watchdog.db` | content, also embedded images, deleted right after sending; failed ones 90 days (images together at most `userMessages.maxStoredImageMb`, 256 MB by default); metadata of sent ones 30 days (for user messages without subject and addresses) |
| User message audit (who, when, recipient count, SHA-256 hashes of addresses and content) | `watchdog.db` | 365 days (`retention.userMessageAuditDays`) |
| Configuration and its change history | `watchdog.db` | current configuration; history 3 years |
| Channel secrets (SMTP password, OAuth secret, Teams/webhook addresses, signing keys) | `watchdog.db`, encrypted with Data Protection and bound to the destination; the keys in `keys/` encrypted with the key from the systemd credential (`/etc/admng-watchdog/credentials/dp-key.cred`, TPM2 + host key or host key) | until deleted |
| Database backups | `backup/` (daily at 3:15, the last 7; pre-migration backups – the newest + 30 days); when `/var` is out of space, the backup is skipped (`storage.backupSkipped`) | 7 backups |
| Raw events from the controllers (OpenWEC files) | `/var/lib/openwec/out/<DC>/` | until read by the service (files rotated hourly); unread ones – at most 7 days or 8 GB in total |

The retention and size limit are stored by the service configuration (AD Manager 0.3.0 edits only the logon record
retention time of them). Deleted records are overwritten in the database file (`secure_delete`). Restoring requires the
**database, the `keys/` folder and the systemd credential from the same host** (with the TPM2 variant – also the same
vTPM); on another machine `admng-watchdog reset-secrets-key` creates a new key, and the channel secrets are entered again
([WATCHDOG-LINUX.md](WATCHDOG-LINUX.md) 3.9). A backup of the whole virtual machine is recommended (a Tier 0 asset).
Size: about 0.6 KB per event (1,000 events a day ≈ 0.7 GB over 3 years), about 0.6 KB per snapshot object, logon
records about 120 MB.

The daily backup (3:15, `VACUUM INTO`) and database compaction **do not pause** change detection or sending
alerts. The database state is shown by two checks with a single definition (the state no longer changes every hour):

- "Database size" (`storage.size`) – the database file together with the WAL file relative to
  `Retention.MaxDatabaseMb`: a warning from 80% of the limit, an error above the limit,
- "Free disk space" (`storage.free`) – an error when less than 1 GiB is left on the volume of the data folder.

A failed retention or database maintenance gives the status "Database maintenance" (`storage.maintenance`).

---

## 8. Security

- **Personal data (GDPR).** The data folder and notifications contain employee data (names, departments, account
  changes, addresses and workstation names). Agree with the DPO on the purposes, the retention period (3 years by
  default), the circle of recipients and the channels (Teams and Power Automate keep copies outside the folder – a flow
  history of 30 days). Enter the processing in the register of processing activities. The obligations under the Polish
  National Cybersecurity System Act (NIS2) concerning monitoring and incident evidence argue for longer event
  retention. Reading single events in the API is logged (who, event number) in the service log (journald) – events 2600
  (details in the tab) and 2601 (the event page from a link in a notification). Every opening of details and every opened
  event page is one entry – size the system log accordingly or forward it to a SIEM.
- **Zero secrets.** Values of passwords, LAPS, BitLocker, TPM, gMSA, LSA secrets (`currentValue`, `priorValue`),
  attributes marked in the schema as confidential and passwords from GPO preferences (`cpassword`) are neither stored
  nor sent.
- **The Watchdog server is a Tier 0 asset** (chapter 3): outside the domain, administered only with SSH keys from a PAW
  workstation, an nftables firewall (inbound only from the controllers and the administrator subnets, outbound
  connections only to the controllers, the SMTP server and – for the service – Teams), services in a systemd sandbox,
  without swap and memory dumps.
- **The service account** `ADMNG-WD$` has only read access to AD and SYSVOL; it has no write or replication
  permissions, does not belong to privileged groups (the service does not start if it does), cannot be delegated, and
  uses AES only. Its random password (120 characters) is known to nobody; it changes every 30 days, and only the keys
  in the keytab (`root:admng-keytab 0640`) are on disk. Every change of the account other than the scheduled password
  change is a critical alert. Risks accepted knowingly (ADR-0009): a certificate issued to this account after a keytab
  theft survives the password change, and the keytab shared with OpenWEC allows forging an API ticket from the
  administrator subnets – details and limits in [WATCHDOG-LINUX.md](WATCHDOG-LINUX.md) 0.3 and 4.3.
- **API access**: HTTPS (TLS 1.2/1.3), Kerberos only (NTLM rejected), roles by the SIDs of AD groups from the local
  `/etc/admng-watchdog/appsettings.json` (the service account cannot change it) – the service determines membership
  itself from `tokenGroups` (a 5-minute cache), a list of immediate denials (`DeniedSids`), an additional header
  required for changes (protection against CSRF from a browser), access only from the administrator and monitoring
  subnets (also for connections from the server itself), no proxy servers.
- **Channel secrets** are write-only and bound to the destination: the API and the tab never return them, the Watchdog
  administrator cannot redirect a stored password or OAuth token to another server (also by changing the SMTP
  certificate validation – pinning a different certificate requires re-entering the password); in the tab and in the
  service logs only `scheme://host/…` (without the path and parameters) is visible from channel addresses.
- **Connections to the controllers** only through Kerberos: LDAP with signing and encryption (the required encryption
  strength is ≥ 56 bits – without it the connection is rejected), SYSVOL over SMB 3 with signing; WEF from the
  controllers only from their addresses and only with Kerberos.
- **Configuration changes** are audited and reported (also to the existing recipients); critical detections cannot be
  disabled through the API without local consent, and event retention cannot be shortened below the threshold from the
  startup settings.
- **The diagnostic logs** of the service (journald) contain no directory values, exception contents or GPO file
  contents – automated tests check this.
- **Content from AD is untrusted**: in e-mails it is HTML-encoded and "defused" (5.6), in Teams cards it is displayed as
  plain text (without Markdown links), in webhooks as JSON. Computer names supplied by the client or from DNS are
  marked and never decide the rules. GPO files are read with size limits and without expanding XML entities.
- **Event sources** are verified: only records of the provider "Microsoft-Windows-Security-Auditing" from the writable
  controllers of this domain, with a matching subscription, computer name and sender address, are accepted. Whoever takes
  over the `openwec` account on the server can, however, forge or omit records – so this account is isolated (without
  outbound connections) and treated as a Tier 0 asset.
- **Rule exclusions** are audited and visible in every daily report; exclusions for critical rules are marked with a
  warning.
- **Service monitoring**: `/healthz` for the monitoring system, the service log (`journalctl -u admng-watchdog -p
  warning`, chapter 10), the state of units (`systemctl is-failed admng-watchdog openwecd`), an optional webhook
  heartbeat, a missing daily report as a failure signal.

---

## 9. Limitations

- **Preview version 0.3.0:** the Linux service has not yet passed a pilot on Windows Server 2025 and 2022 controllers –
  attribution through WEF and OpenWEC was verified on recorded Windows events (the same results as in version 0.2.0) and
  on the Samba lab, not on live controllers. The list of matters to settle in the pilot is given in
  [WATCHDOG-LINUX.md](WATCHDOG-LINUX.md) 7.4.
- One service instance monitors one domain (in a forest with many domains – one instance per domain); controllers of
  other domains appear only as "new / removed controller" (high priority).
- Changes made on DCs in other sites are visible after inter-site replication (up to 180 min by default).
- The expiry of a lockout after the lockout duration writes nothing to AD, and on the first successful logon the DC
  resets `lockoutTime` – Watchdog does not treat this as an unlock (the optional "lockout expired" event is computed by
  the service itself). The lockout duration takes a PSO into account if the service account can read it
  (WATCHDOG-LINUX.md 2.6).
- Attribution requires audit policy, SACLs and working event forwarding (WEF); without them the actor is "undetermined"
  (with the reason given). Changes made through ADWS (Active Directory Administrative Center, PowerShell) or a remote
  session on a DC may indicate the controller itself as the source ("locally on the DC"). Logon records are kept for 48
  hours – the source of older sessions will not be determined ("SOURCE_LOGON_NOT_FOUND").
- Events written by OpenWEC in the last seconds before a sudden loss of power on the server may be lost (OpenWEC does not
  force a write to disk); OpenWEC files created and deleted in full while the service was stopped (the limit of 7 days or
  8 GB) are not reported as a gap – only the log of the cleanup unit names them.
- `lastLogonTimestamp` is updated with a delay of 9–14 days – the inactivity threshold should not be shorter than
  30 days.
- Without the right to read `CN=Deleted Objects`, deletions are detected only at the daily reconciliation (as "object
  disappeared").
- Time-limited memberships (PAM, TTL) expire without a trace in AD – their end is detected at the daily reconciliation.
- GPO content: a change of files in SYSVOL without a change of the GPO version is detected only at the daily
  reconciliation, without an actor (`SYSVOL_NOT_AUDITED` – file share auditing is not collected); a change of the value
  of an already existing secret (e.g. a password in registry settings) is visible only by the change date of the GPP
  item, not by the value; a file that cannot be read or parsed is reported as "could not be read".
- The service blocks and rolls back nothing – it only reports.
- AD Manager 0.3.0 has no editor for event retention or the database size limit (a change only through the service API);
  a configuration carried over from version 0.2.0 keeps the limit of 10,240 MB.
- Exchange Online OAuth2 on Linux – only with a client secret (without a certificate, 5.2).
- A change of role groups affects the watching of privileged groups only after the service restarts; a change of
  subnets in `appsettings.json` does not change the nftables firewall – correct it separately (WATCHDOG-LINUX.md 3.3).

---

## 10. Troubleshooting

| Symptom | Cause and solution |
|---|---|
| The tab: "The service rejected Kerberos authentication" (401) | The SPN `HTTP/<name>` is not on the `ADMNG-WD$` account (or it is on two accounts, e.g. still on the gMSA from 0.2.0), the keytab lacks the key version the KDC uses, or the client uses a CNAME alias/IP address (Windows then tries NTLM, which is rejected). Check `setspn -Q HTTP/admng-wd.contoso.com`, `admng-watchdog-keytab verify` on the server (service log: entry 2501 with a hint), use the name from the A record; after changes `klist purge` on the workstation. |
| "No permission" (403) | The account does not belong to the groups from `Authorization` in `/etc/admng-watchdog/appsettings.json` (membership is checked every 5 minutes), is on the `DeniedSids` list, or the workstation is outside `AllowedRemoteSubnets`. |
| Response 503 `role-lookup-unavailable` (requests rejected despite a correct logon) | The service cannot read the operator's membership from AD (no connection to a DC) – try again after a moment; on the server `journalctl -u admng-watchdog` (entry 2506) and the state of the DC connection. |
| "Service unreachable" | The service is stopped, the firewall (port 8443 in nftables and `AllowedRemoteSubnets`), DNS. On the server: `systemctl status admng-watchdog` and `sudo admng-watchdog --health` (code 0 = running). |
| A certificate error / the "Confirm certificate" window | The certificate does not come from a trusted CA – compare the fingerprint with `admng-watchdog cert-fingerprint` on the server (chapter 6); a name that does not match the address is never accepted. |
| The service does not start (`systemctl status admng-watchdog` – `failed`, exit code) | Codes 2–10 and `243/CREDENTIALS` with their causes: [WATCHDOG-LINUX.md](WATCHDOG-LINUX.md) 5.3; settings errors are shown by `sudo admng-watchdog --check-config`. |
| Warning 2002 in the service log after editing `appsettings.json` | The file has a JSON syntax error or an invalid value (e.g. `"Minimalny"` instead of `"Minimal"`, `"tak"` instead of `true`). The service keeps running on the previous settings – the change is **not** in effect, and at the next start the service will stop (code 2). Fix the file and check it with `sudo admng-watchdog --check-config`. |
| Status: "Configured DNs that no longer exist" | An OU or a group pointed to in the configuration was renamed, moved or deleted – correct the listed DNs (rules, privileged groups, subtrees, exclusions). |
| Status: "permission.*" | The account does not see some objects or attributes – grant the permissions from [WATCHDOG-LINUX.md](WATCHDOG-LINUX.md) 2.6 (also on AdminSDHolder; SDProp up to 60 min). |
| Status: "Service account" – `serviceAccount.*` | The keytab does not match AD, a password older than 32/35 days, no deny entry for SELF, the account in a privileged group – a description and recommendation for each code; WATCHDOG-LINUX.md 3.6, 3.7, 9.1. |
| Status: "WEF collector" – `wec.<DC>.*` | A DC is silent, records rejected (sender identity mismatch) or an OpenWEC file lost – WATCHDOG-LINUX.md 9.2. |
| Status: "GPO content" – `gpo.<GUID>.*`, `sysvol.*` | No SYSVOL read, SYSVOL replication delay, a file too large or unreadable – WATCHDOG-LINUX.md 2.7 and 9.4. |
| Actor "undetermined – AUDIT_POLICY_MISSING / SACL_MISSING / LOG_OVERWRITTEN / DC_UNREACHABLE" | Respectively: the audit policy (WATCHDOG-LINUX.md 2.10), the SACL (2.11), the log size (2.10) or a cleared Security log (1102/1104 or record numbering started anew – events from before the clearing are no longer available), a DC that stopped forwarding events (2.12, 9.2). |
| Source "SOURCE_LOGON_NOT_FOUND / SOURCE_NOT_COLLECTED" | A logon older than the logon record retention time (48 h) or the controller sent no 4624 event in the search window – the subscription and the WEF GPO (WATCHDOG-LINUX.md 2.12, 4.4). |
| Saving the configuration: "enter the password/secret again" (`secretRebindRequired`) | The server, port, user, tenant, channel address or SMTP server certificate verification was changed (the pinned fingerprint, the mode, revocation checking) – the stored secret is bound to the old destination. After a service update also an SMTP password / XOAUTH2 secret stored with non-standard certificate verification (4.9): until it is entered again, no e-mail is sent. |
| Delivery log: "Failed" with the code `partial.smtp.4xx…` | One of the recipients rejected the message temporarily (4xx) for about 22 h; the others received it. A retry (the Admin role, up to 14 days) sends it only to the missing recipients (5.2). |
| Delivery log: "Failed" with the code `channel.disabled` / `channel.unknown` / `smtp.disabled` | The channel was disabled or deleted, or SMTP was disabled, while the message was waiting in the queue. Re-enabling the channel will not deliver it – retry manually (5.7). |
| E-mail: `535 5.7.139` / `550 5.7.30` | Exchange Online: SMTP AUTH is disabled for the tenant/mailbox – switch to OAuth2 or `Set-CASMailbox -SmtpClientAuthenticationDisabled $false`. |
| E-mail: `550 5.7.68` | The tenant rejects "Direct Send" – use an inbound connector or sign-in. |
| Teams: "outdated address" | An address `*.webhook.office.com` – create a Workflow (5.3). |
| Teams: 401/403 with text about the "Shared Access scheme" or "OAuth" | A mismatched trigger authentication mode (SAS vs Entra) – change the channel mode or the flow setting. |
| No daily report | The service is not running, SMTP is faulty (Status → Channels) or the report is disabled. |

Event numbers in the service log (journald – `journalctl -u admng-watchdog`, in the row as `Category[1003]`; in version
0.2.0 – the Application log, source "AdMng Watchdog") for monitoring (Zabbix, SIEM): 1000 service start,
1001 no connection to a DC / switch to another DC, 1002 failed sends, 1003 a storage problem (space, limit,
folder permissions), 1004 missing permissions, 1005 attribution problems / a gap in the DC log, 1006 configuration
change, 1007 an outdated .NET environment / an expiring certificate / an unavailable key of a renewed certificate
(a warning while the previous certificate works; an error when it expires), 1008 the TLS certificate fingerprint (at
every start), 1010 a failed startup check or a stopped job, 1011 a service account problem, 1012 a WEF collector
problem, 1013 a GPO content problem, **2002 a rejected edit of `appsettings.json`** (the service runs on the previous
settings), 2501 rejected Kerberos authentication (with a hint), 2202/2203 e-mail recipients rejected temporarily /
permanently (partial delivery), 2300 warning: a service job is failing (an entry at the first error or a change of its
code, then at most hourly), 2301 the job works again; for accountability (GDPR) 2600/2601 reading a single event (who,
event number).

Identical warnings and errors are written at most once per 15 minutes; the next entry ends with the note "(repeated N
times since the previous entry)". SIEM rules that count occurrences must take this into account. Warning 2002 is never
throttled this way.

---

## 11. Test environment

The service can be run on Linux against the Samba AD DC lab from `tools/testlab` – scripts and description:
[`tools/testlab/watchdog/README.md`](../../tools/testlab/watchdog/README.md).

```bash
tools/testlab/watchdog/run-watchdog-lab.sh --reset --background   # the service (Lab profile), Mailpit, the webhook receiver
tools/testlab/watchdog/configure-lab.sh --test                    # SMTP → Mailpit, channels → the receiver (through the API)
tools/testlab/watchdog/lab-scenario.sh --verify                   # changes in OU=WatchdogDemo and a check of the notifications
tools/testlab/watchdog/run-watchdog-lab.sh --stop
```

- The `Lab` profile (`appsettings.Lab.json`): `Development` authentication (loopback only, never as a systemd
  service), the API `http://127.0.0.1:5089`, reading the directory as `odczyt.test` with a simple bind without TLS
  (`AllowInsecureLab` – rejected outside the Development environment); the password only in the variable
  `ADMNG_WATCHDOG_LDAP_PASSWORD`, data in `~/.local/share/admng-watchdog-lab`. In this profile the Data Protection keys
  are not encrypted (a warning at start), and attribution is disabled (`SOURCE_DISABLED`) – the Samba lab has no Windows
  security logs.
- Mailpit (http://127.0.0.1:8025) collects e-mails (STARTTLS, the certificate pinned by `configure-lab.sh`), the webhook
  receiver verifies Standard Webhooks signatures and Teams Workflows envelopes.
- The lab configuration deliberately shortens the aggregation windows and ends the deployment mode; production settings
  are described in chapter 5 and [WATCHDOG-LINUX.md](WATCHDOG-LINUX.md).
- The `odczyt.test` account does not see `CN=Deleted Objects`, so deletions in the lab appear only at the daily
  reconciliation (the status `permission.deletedObjects`).

The integration tests (`tests/AdMng.Watchdog.IntegrationTests`) run the real engines and the whole service against the
lab:

```bash
tools/testlab/watchdog/start-mailpit.sh
export ADMNG_TEST_LDAP_SERVER=127.0.0.1 ADMNG_TEST_LDAP_USER='Administrator@contoso.test' ADMNG_TEST_LDAP_PASSWORD='P@ssw0rd!2026'
export ADMNG_TEST_LDAP_READONLY_USER='odczyt.test@contoso.test' ADMNG_TEST_LDAP_READONLY_PASSWORD='Passw0rd!Lab2026'
export ADMNG_TEST_SMTP_HOST=127.0.0.1 ADMNG_TEST_SMTP_PORT=1025 ADMNG_TEST_MAILPIT_API=http://127.0.0.1:8025
dotnet test tests/AdMng.Watchdog.IntegrationTests
# a computer account with a keytab (the admng-watchdog-keytab tool), LDAP over SASL GSSAPI, the API with Negotiate and roles
# by SIDs (KeytabLabTests, KerberosNegotiateTests), OpenWEC and SYSVOL: eval "$(tools/testlab/watchdog/keytab-lab.sh env)"
```

The Debian packages are built by `tools/packaging/build-deb.sh` (`admng-watchdog`) and
`tools/packaging/build-openwec-deb.sh` (`admng-openwec`, in the Rust image of the given Debian version);
`tools/packaging/test-deb-install.sh` verifies the installation in a clean Debian 12 or 13 container.

CI (`.github/workflows/ci.yml`): on Windows all test projects (also the Windows-only tests), the AD Manager application
and – on demand (*Run workflow*) – screenshots, also of the Watchdog tabs; on Ubuntu with a Samba DC – engine and service
tests, integration tests with Mailpit and the lab scripts (`lab-scenario.sh --verify`); the jobs `openwec-package`
(Debian 12 and 13, GPL-3.0 sources) and `linux-package` (the `admng-watchdog` package, an install test on Debian 12 and
13, lintian). The release (`.github/workflows/release.yml`) publishes these packages together with AD Manager.
