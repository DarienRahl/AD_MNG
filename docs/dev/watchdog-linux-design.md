# AdMng Watchdog on Linux (Debian + systemd) – stage B design

Status: **binding for stage B** (implementation of the Linux service), in the same way as `docs/dev/watchdog-spec.md`
is binding for v1. Where this document and `watchdog-spec.md` disagree, this document wins for everything the Linux
service does; sections of the spec that are replaced are listed in section 23 and get amended during B8.
Date: 2026-10-19 (user confirmations: 2026-10-20). Decision record: `docs/adr/0009-watchdog-na-linuksie.md` (Polish;
status "accepted" – the five changes to the decisions of 2026-10-19 were confirmed by the user on 2026-10-20, ADR
section „Ustalenia potwierdzone 2026-10-20”, and four further points were decided the same day, ADR section „Ustalenia
potwierdzone 2026-10-20 – druga tura”). Deployment instructions: `docs/WATCHDOG-LINUX.md` (Polish, written in
stage A); feature description: `docs/WATCHDOG.md` (Polish, its Windows chapters replaced in B8). Inputs: the user
decisions of 2026-10-19 and their confirmations of 2026-10-20, the Linux lab verification (Samba lab, 2026-10-19,
appendix C), the two reviews of 2026-10-20 and the web research on OpenWEC, Windows Event Forwarding, Kerberos on Linux,
AD CS and BadSuccessor.

The confirmations of 2026-10-20 are binding: (1) the account is created by a Tier 0 admin on the PAW and the password
and keytab are handled by `admng-watchdog-keytab` (no msktutil/adcli); (2) raw logon records are kept 48 h; (3) the
collected events include 4625 and RDP logons (4624 type 10), the rules `tier0-machine-quota` and `watchdog-self` exist,
and `ms-DS-MachineAccountQuota` = 0 (with delegated computer creation for whoever joins computers) plus the Deny ACE
for SELF are deployment prerequisites; (4) OpenWEC **shares** the account, principal `HTTP/admng-wd.contoso.com` and
keytab with the API (4.3); (5) the lab recipe has three VMs including ADM-WS01 (appendix B). The second batch of
2026-10-20 is binding as well: (6) **no** Deny ACEs for `ADMNG-WD$` on the AD CS certificate templates and no hourly
template check – the PKINIT risk of a stolen keytab is **accepted** (4.1, 22), while the per-CA certificate check after
`init` and the revocation step of the compromise recovery stay; (7) the SACL-change detection of 9.4 is confirmed;
(8) Watchdog admin operations are allowed from the whole admin subnet – no PAW-only admin role (4.3 item 1); (9) the
Internet egress of `admng-watchdog` stays direct on TCP 80/443 without a proxy (accepted residual risk, Q23 closed;
`root`/`_apt` for updates, no egress for `openwec`).

Language rules from `CLAUDE.md` apply: code, comments, commits and this document in English; product docs, ADRs and
instructions in Polish; every user-visible string through resx (en + pl). **MUST** = requirement, **SHOULD** = strong
default that an implementer may deviate from only with a reason in the final report.

---

## 0. Decisions (summary)

| # | Decision | Why |
|---|---|---|
| L1 | The Watchdog service runs **only on Linux**: Debian 13 (recommended) or Debian 12, amd64, as the systemd unit `admng-watchdog.service` (`Type=notify`, full hardening, section 12), shipped as the `.deb` package `admng-watchdog` (framework-dependent .NET 10, `Depends: aspnetcore-runtime-10.0 (>= 10.0.13)` from packages.microsoft.com). The Windows service stays available as release 0.2.0 and is **removed from the code after the first Linux release** (section 17). | User decision. One platform to secure and test; the server is a Tier 0 asset that the customer wants to run as a minimal Linux VM. |
| L2 | The server `admng-wd.contoso.com` (10.0.1.25) is **not joined to the domain**. Kerberos (`krb5.conf`), DNS and time (chrony against the DCs) are configured by hand; the service has a private `krb5.conf`, `ldap.conf` and `smb-client.conf`. | No SSSD/realmd, no domain GPOs on a Tier 0 box, no interactive domain logons, fewer moving parts. |
| L3 | Service identity = the **computer account `ADMNG-WD$`** with a **keytab**, created by a **Tier 0 administrator on Windows** with a one-time password (`New-ADComputer … -KerberosEncryptionType AES128,AES256 -AccountNotDelegated $true`) in the existing Tier 0 OU. The Debian host replaces the one-time password with a random 120-character password and writes the keytab; the password is **rotated every 30 days** by `admng-watchdog-keytab.timer`. No AD write grants (the inherent self-writes of a computer account are removed by a Deny ACE for SELF on its own object, computer creation by `ms-DS-MachineAccountQuota` = 0 – section 4.1), not privileged, no interactive logon. Certificate enrollment through templates open to Domain Computers is **not** blocked (no template Deny ACEs – accepted risk, user decision 2026-10-20, 4.1). (Replaces the msktutil/adcli creation agreed on 2026-10-19; confirmed by the user on 2026-10-20, ADR-0009.) | A gMSA cannot be used by a non-joined Linux host without a second identity anyway; a computer account + keytab is the standard Linux pattern and was verified in the lab (kinit from keytab, self password change, kvno handling, acceptor without restart). |
| L4 | Password rotation and keytab writing are done by our own small tool **`admng-watchdog-keytab`** (separate .NET console project, MIT libkrb5 through P/Invoke: change-password protocol on port 464, key derivation with the AD salt, keytab append, verification against every DC). **Not** msktutil or adcli. | Both tools need LDAP self-writes whose effect on Windows DCs was not verifiable in the lab (operatingSystem, SPN, enctype writes), write temporary krb5.conf files to `/tmp`, and the msktutil package enables its own timer. The MIT primitives were verified. |
| L5 | LDAP from Linux: **port 389, SASL GSSAPI (Kerberos) with sign + seal**, enforced through a service-private `ldap.conf` (`SASL_SECPROPS minssf=56`, `SASL_NOCANON on`) passed with `LDAPCONF`; the negotiated SSF is checked after every bind. **LDAPS (636) is an option**, not the default. One DC per session, PDC emulator preferred (unchanged rule 4). | Works with the enforced LDAP signing of Windows Server 2025 and with 2022, needs no PKI and no channel binding. |
| L6 | API: **Kestrel + Negotiate with the keytab** (`KRB5_KTNAME`), Kerberos only (no NTLM), HTTPS 8443, admin subnet only. On Linux the Negotiate handler yields only the principal name, so **roles are resolved by the service itself from AD**: `sAMAccountName` → `objectSid` + `tokenGroups` over its own LDAP session, cached 5 minutes, then the unchanged SID role handler (`DeniedSids` first). The handler's `EnableLdap` role lookup is **never** used. | Lab: the Negotiate identity carries no SIDs; `EnableLdap` roles are group CN names and a look-alike group spoofed a role in the lab. tokenGroups by SID is spoof-proof and refreshes faster than Kerberos tickets. |
| L7 | Attribution events come from the DCs by **Windows Event Forwarding (source-initiated)** to **OpenWEC** (cea-sec/openwec, GPL-3.0, separate process and package `admng-openwec`, unit `openwecd.service`) on the same server, port 5985, Kerberos with **the same account, principal `HTTP/admng-wd.contoso.com` and keytab as the API** (shared keytab – user decision 2026-10-20; risk and mitigation in 4.3). OpenWEC writes **RawJson** (one JSON line per event, raw event XML inside) into one file per DC; Watchdog **tails** the files with an at-least-once cursor committed in the same SQLite transaction as the normalized records. | No RPC access to DC logs from Linux; WEF is built into Windows (no agents on DCs); OpenWEC is the only maintained open-source WEF collector; RawJson lets the existing, fixture-tested XML parser do the work; the Files driver is the only output that stays durable across a Watchdog outage. |
| L8 | The Security events used for **source lookups** (4624 logon types 3 and 10, 4625, 4771 and failed 4776) are now **ingested** together with the change-related events (they used to be queried on demand over RPC) and kept briefly (`Retention.LogonRecordsHours`, default **48 h** – confirmed by the user on 2026-10-20); change-related DC log records keep the **30-day** retention; Watchdog events (who / what / when / from where / old → new) are kept **1095 days**. | Push delivery cannot be queried later; the attribution copies the resolved source into the event, so raw logon records do not need to live long (ADR-0008 data minimisation). |
| L9 | **GPO content tracking**: the service reads the GPO folders on SYSVOL of its pinned DC **read-only over SMB 3 with Kerberos and mandatory signing** (the `smbclient` process, output only through a pipe into memory), parses `GptTmpl.inf`, `Registry.pol`, Group Policy Preferences XML, `scripts.ini`/`psscripts.ini`, `audit.csv` and comments, and reports changes as **"setting: was → is"**. Trigger: `versionNumber` / `gPCFileSysPath` of the GPC; daily reconciliation also finds out-of-band SYSVOL edits; attribution is inherited from the 5136 on the GPC. | User decision ("full GPO content tracking"). A managed SMB library with Kerberos + SMB3 encryption was not verifiable; smbclient was verified in the lab. |
| L10 | New / changed rules: **BadSuccessor** (dMSA created → Critical; any change of `msDS-ManagedAccountPrecededByLink` / `msDS-DelegatedMSAState` / the superseded attributes → Critical; built-in rule `tier0-badsuccessor`), **ms-DS-MachineAccountQuota** (`tier0-machine-quota`, Critical when raised from 0), **the service's own account** (`watchdog-self`, any change of `ADMNG-WD$` except its own password rotation → Critical), GPO settings rules (`tier0-gpo`). | User decisions (BadSuccessor 2026-10-19; `tier0-machine-quota` and `watchdog-self` confirmed 2026-10-20) + lab finding: the computer account may raise its own `msDS-SupportedEncryptionTypes` (RC4 back on) and add SPNs for its own host name. |
| L11 | Channel secrets: `ISecretStore` unchanged (ASP.NET Core Data Protection); the **key ring is encrypted with AES-256-GCM using a 256-bit key held as a systemd encrypted credential** (`systemd-creds encrypt --with-key=host+tpm2` when a TPM2 is present, `--with-key=host` otherwise or when TPM2 sealing fails; chosen by the installer, which reports the variant actually used – section 10). Restore onto another host without the same host key/TPM → secrets are re-entered (existing `secret.unreadable` path). | Replaces DPAPI-NG; no plaintext key on disk; no user decision needed. |
| L12 | Data folder `/var/lib/admng-watchdog` (owner `admng-watchdog`, mode **0700**, enforced at every start) replaces the NTFS ACL of ADR-0008; configuration in `/etc/admng-watchdog` owned by root and **not writable by the service**; logs to **journald**, value-free as before. | Same guarantees as ADR-0008 with POSIX means (ADR-0009 amends ADR-0008). |
| L13 | TLS certificate of the API: **the company CA when one exists** (CSR on Debian, `certreq` by a Tier 0 admin), otherwise a **self-signed certificate generated by the package**; AD Manager asks the operator to **confirm the SHA-256 fingerprint on the first connection** and pins it; a changed certificate is never accepted silently. | The customer does not know whether AD CS exists; both variants must work. |
| L14 | Backups: the existing nightly local `VACUUM INTO` backups (7 kept) in the data folder; the instructions recommend a VM-level backup once (3-year history) and do not push it further. | User decision. |

---

## 1. Goals and non-goals

### 1.1 Goals (stage B)

1. Run the complete v1 Watchdog (detection, attribution, rules, notifications, API, digest, retention – `watchdog-spec.md`)
   on Debian 12/13 under systemd with the identity, transport and hardening described here, without any Windows
   component on the Watchdog server.
2. Keep every guarantee of ADR-0008 (one data folder, data minimisation, no secrets of the directory, retention,
   enforced access, value-free logs) and of `CLAUDE.md` rules 1–6, with the amendments of ADR-0009.
3. Attribute changes to the account **and** the source computer/IP (4624 logon types 3 and 10, 4625, 4771 and failed
   4776) through WEF; keep the history (who, what, when, from where, old → new) for 3 years (1095 days).
4. Track GPO **content** ("setting: was → is") in addition to the GPC attributes and links.
5. Add the BadSuccessor, MachineAccountQuota and own-account detections.
6. Install, upgrade and remove cleanly as `.deb` packages; build and test them in Linux CI; give the instruction writers
   (Polish `docs/WATCHDOG-LINUX.md`) exact AD, DC and Debian steps (appendix A).

### 1.2 Non-goals (stage B)

- Running the service on Windows after the Linux release; a Windows ↔ Linux cluster; more than one domain per instance.
- A built-in WEF receiver (OpenWEC is used; an own receiver may be reconsidered later).
- Domain join (SSSD/realmd/winbind) of the Watchdog server.
- File-share auditing (event 5145) – SYSVOL edits that bypass `versionNumber` are found by the daily reconciliation,
  their actor stays unknown (`SYSVOL_NOT_AUDITED`).
- Remote management of OpenWEC from the API or AD Manager (subscriptions are files managed by the Linux admin).
- External monitoring integration (the user has none): health is the Status tab, the daily digest, journald and
  systemd restarts (`Restart=on-failure` without a start limit, `WatchdogSec`, 12.1). A unit that stops for good
  (a configuration error, `RestartPreventExitStatus=`) triggers the `OnFailure=` alert of 12.1; its absence from the
  daily digest is the remaining signal.
- Off-host backups managed by the product.

---

## 2. Fixed names and layout (reference values)

These are the example values of every instruction, test and default; they are customer placeholders, but the
documents MUST use exactly these.

| Item | Value |
|---|---|
| Domain / NetBIOS / Kerberos realm | `contoso.com` / `CONTOSO` / `CONTOSO.COM` |
| DCs | `DC01.contoso.com` 10.0.1.10 (Windows Server 2025, PDC emulator in the examples), `DC02.contoso.com` 10.0.1.11 (Windows Server 2022); domain/forest functional level ≤ 2016 (mixed DCs) |
| Watchdog server | Debian 13 (package also for Debian 12), hostname `admng-wd`, FQDN `admng-wd.contoso.com`, IP 10.0.1.25, **not domain-joined**; AD DNS: A record `admng-wd.contoso.com → 10.0.1.25` (never a CNAME), PTR recommended |
| Service account | computer object `ADMNG-WD` (`sAMAccountName` `ADMNG-WD$`), `dNSHostName` `admng-wd.contoso.com`, SPNs `HTTP/admng-wd.contoso.com`, `HOST/admng-wd.contoso.com`, `HOST/ADMNG-WD`; `msDS-SupportedEncryptionTypes` = 0x18 (AES128 + AES256); "Account is sensitive and cannot be delegated"; in `OU=Service Accounts,OU=Tier 0,OU=Admin,DC=contoso,DC=com` (placeholder for the customer's existing Tier 0 OU) |
| Role groups | `AdMng-Watchdog-Admins`, `AdMng-Watchdog-Viewers` (global security groups) in `OU=Groups,OU=Tier 0,OU=Admin,DC=contoso,DC=com` |
| Admin workstations (AD Manager) | 10.0.20.0/24 |
| SMTP relay / Teams | `smtp.contoso.com:587` STARTTLS / Power Automate Workflows over HTTPS 443 |
| Watchdog API | `https://admng-wd.contoso.com:8443` |
| WEF endpoint (OpenWEC) | `http://admng-wd.contoso.com:5985/wsman/SubscriptionManager/WEC` (HTTP + Kerberos message encryption) |
| Package / unit / user | `admng-watchdog` (.deb, amd64), `admng-watchdog.service`, system user `admng-watchdog` (no shell, home `/var/lib/admng-watchdog`) |
| Binaries | `/usr/lib/admng-watchdog/` (service `AdMng.Watchdog`, tool `admng-watchdog-keytab`, helper scripts) |
| Configuration | `/etc/admng-watchdog/appsettings.json` (root:admng-watchdog 0640), `krb5.conf`, `ldap.conf`, `smb-client.conf` (root:root 0644), `tls/` (root:admng-watchdog 0750), `credentials/` (root:root 0700) |
| Keytab | `/etc/admng-watchdog/admng-wd.keytab` (root:admng-keytab 0640; group `admng-keytab` = `admng-watchdog` + `openwec`) |
| Data | `/var/lib/admng-watchdog` (admng-watchdog 0700): `watchdog.db` (+ `-wal`, `-shm`), `backup/`, `keys/` |
| Keytab tool state | `/var/lib/admng-watchdog-keytab/status.json` (root:admng-watchdog 0640, no secrets) |
| OpenWEC | package `admng-openwec`, unit `openwecd.service`, user `openwec`, config `/etc/openwec.conf.toml`, subscriptions `/etc/openwec/subscriptions/`, database `/var/lib/openwec/db/openwec.sqlite`, output `/var/lib/openwec/out/<client>/security.jsonl` |
| Timers | `admng-watchdog-keytab.timer` (daily; rotates when the password is older than 30 days), `admng-wec-rotate.timer` (hourly; rotates and deletes consumed OpenWEC output files) |
| Retention | events 1095 days; change-related DC log records 30 days; logon/failure records 48 h; local backups 7 |

Ports on `admng-wd`:

| Direction | Port | Peer | Purpose |
|---|---|---|---|
| in | 22/tcp | Tier 0 PAW addresses only (example and lab: ADM-WS01 10.0.20.15) – not the whole admin subnet, which also holds non-Tier-0 AD Manager workstations | SSH (keys only, no root login) |
| in | 8443/tcp | 10.0.20.0/24 | Watchdog API (HTTPS + Kerberos) |
| in | 5985/tcp | 10.0.1.10, 10.0.1.11 | OpenWEC WEF receiver (HTTP + Kerberos message encryption) |
| out | 53/udp+tcp | DCs | DNS |
| out | 88/udp+tcp | DCs | Kerberos (TCP preferred, `udp_preference_limit = 0`) |
| out | 123/udp | DCs | NTP (chrony) |
| out | 389/tcp | DCs | LDAP, SASL GSSAPI sign + seal |
| out | 445/tcp | DCs | SMB 3 (SYSVOL read, GPO content) |
| out | 464/udp+tcp | DCs | kpasswd (password rotation) |
| out | 636/tcp | DCs | LDAPS (only with the LDAPS option) |
| out | 587/tcp | smtp.contoso.com | SMTP STARTTLS |
| out (users `root`, `_apt`) | 80, 443/tcp | packages.microsoft.com / deb.debian.org (the default Debian sources use HTTP) | OS and runtime updates (apt downloads as `_apt`) |
| out (user `admng-watchdog`) | 80, 443/tcp | Internet (Microsoft) and the CRL/OCSP URLs of the SMTP relay certificate | Teams Workflows (443); SMTP certificate revocation check (`checkRevocation`, online on Linux – usually HTTP 80) |

No outbound connection at all for the `openwec` user (nftables `meta skuid "openwec" drop`, 16; `IPAddressDeny=any`
in `openwecd.service`, 12.2) – the WEF receiver only accepts connections (4.3). Every other local user reaches only
the DCs and the SMTP relay; the egress of `admng-watchdog` (direct, no proxy – user decision 2026-10-20) is discussed
in 4.3 ("The `admng-watchdog` process").

---

## 3. Architecture

```
 Windows Server 2025 / 2022 DCs (DC01, DC02)                       Debian 13 "admng-wd" (10.0.1.25, not joined)
 ┌─────────────────────────────────────────┐            ┌──────────────────────────────────────────────────────────────────┐
 │ Security log ──► WinRM forwarder        │  5985 HTTP │ openwecd.service (user openwec, group admng-keytab)               │
 │ (GPO: subscription manager, NETWORK     ├──Kerberos─►│  subscription admng-dc-security, filter Only DC01$/DC02$          │
 │  SERVICE may read Security)             │  msg enc.  │  Files driver, RawJson ─► /var/lib/openwec/out/<client>/security.jsonl
 │                                         │            │                                            │ (2750 openwec:admng-watchdog)
 │ LDAP 389 (sign+seal required on 2025) ◄─┼──GSSAPI────┤ admng-watchdog.service (user admng-watchdog, group admng-keytab)  │
 │ SYSVOL \\DC01\SYSVOL (SMB3, signing)   ◄─┼──Kerberos──┤  ChangeDetectionEngine · ScheduledScanJob · GpoContentJob         │
 │ KDC 88 / kpasswd 464                   ◄─┼──AES only──┤  WecFileIngestionJob ─► AttributionEngine (store lookups)         │
 └─────────────────────────────────────────┘            │  RuleEvaluation ─► Dispatch ─► Outbox ─► SMTP 587 / Teams 443     │
                                                        │  Kestrel API 8443 (Negotiate, keytab, roles by tokenGroups SIDs) │
                                                        │  SQLite /var/lib/admng-watchdog/watchdog.db (0700)                │
                                                        │ admng-watchdog-keytab.timer (root) – password + keytab rotation   │
                                                        │ admng-wec-rotate.timer (root) – OpenWEC file rotation/cleanup     │
                                                        │ /etc/admng-watchdog/admng-wd.keytab (root:admng-keytab 0640)      │
                                                        └──────────────────────────────▲───────────────────────────────────┘
                                                                                       │ HTTPS 8443, Kerberos HTTP/admng-wd.contoso.com
                                                        ┌──────────────────────────────┴──────────────────────────────┐
                                                        │ AD Manager (WPF) on 10.0.20.0/24 – Watchdog tab              │
                                                        │ certificate fingerprint confirmation on first connection     │
                                                        └─────────────────────────────────────────────────────────────┘
```

Trust boundaries and data flows:

- **DC → OpenWEC**: source-initiated WEF; the DCs authenticate as `DC01$` / `DC02$`, OpenWEC accepts with the keytab
  (`HTTP/admng-wd.contoso.com`). Payloads are Kerberos-encrypted at the message level (WinRM over HTTP).
- **OpenWEC → Watchdog**: local files only; Watchdog has read access, never write (section 7.4).
- **Watchdog → DC**: LDAP (detection, role resolution, own-account checks), SMB (SYSVOL), Kerberos, kpasswd (only the
  keytab tool). All to the pinned DC except Kerberos (any KDC, `primary_kdc` = PDC).
- **AD Manager → Watchdog**: HTTPS + Kerberos; roles from AD groups by SID.
- **Watchdog → SMTP/Teams**: unchanged (spec 13–15).

Processing pipeline: unchanged from spec 2, with `WecFileIngestionJob` replacing the RPC ingestion of
`AttributionEngine` and `GpoContentJob` as a new detection job.

---

## 4. Identity: the `ADMNG-WD$` computer account and its keytab

### 4.1 Account properties (MUST, checked by the service – 4.7)

| Property | Value | Set by |
|---|---|---|
| Object class / location | `computer`, `OU=Service Accounts,OU=Tier 0,OU=Admin,DC=contoso,DC=com` | Tier 0 admin (`New-ADComputer`) |
| Owner | Domain Admins (created by a Tier 0 admin, **not** through `ms-DS-MachineAccountQuota`: `mS-DS-CreatorSID` absent) | creation |
| `dNSHostName` | `admng-wd.contoso.com` | creation |
| SPNs | `HTTP/admng-wd.contoso.com`, `HOST/admng-wd.contoso.com`, `HOST/ADMNG-WD` – exactly these; `setspn -Q HTTP/admng-wd.contoso.com` must return only `ADMNG-WD` (remove the SPN from a 0.2.0 gMSA first). `New-ADComputer` writes only the SPNs given; additional `RestrictedKrbHost/…` entries (added by some join tools) are harmless but not expected | creation |
| `msDS-SupportedEncryptionTypes` | 0x18 (AES128_CTS_HMAC_SHA1_96 \| AES256_CTS_HMAC_SHA1_96) – `-KerberosEncryptionType AES128,AES256` | creation |
| `userAccountControl` | `WORKSTATION_TRUST_ACCOUNT` (0x1000) + `NOT_DELEGATED` (0x100000); never `TRUSTED_FOR_DELEGATION` / `TRUSTED_TO_AUTH_FOR_DELEGATION`. The account cannot change its own UAC (lab), so only the Tier 0 admin sets `-AccountNotDelegated $true` | creation |
| Groups | only `Domain Computers` (primary group 515). Not in Protected Users (Microsoft: never for computers/services), no authentication silo, no privileged group (4.7) | – |
| Password | random, 120 printable ASCII characters (0x21–0x7E) from the OS CSPRNG, set by `admng-watchdog-keytab`, never written anywhere, rotated when older than 30 days | Debian host |
| Logon rights | domain GPO "Deny log on locally", "Deny log on through Remote Desktop Services", "Deny log on as a batch job", "Deny log on as a service" for `CONTOSO\ADMNG-WD$`; **not** "Deny access to this computer from the network" (LDAP, SMB need it) | Tier 0 admin (appendix A.6) |
| Directory rights | read grants only: the explicit read grants of `docs/WATCHDOG.md` 4.3 (per class `GR`, domain head, AdminSDHolder, Password Settings Container, `LCRP` on Deleted Objects). Inherent computer-account writes: the own password (kpasswd, User-Change-Password right – kept), the computer class default SD's `(A;;CCDC;;;PS)`, validated SPN / dNSHostName writes and `RPWP` on Personal-Information for SELF – removed by a Deny ACE `SELF: CreateChild, DeleteChild, Self, WriteProperty` on the own object (verified hourly, 4.7); computer creation through `ms-DS-MachineAccountQuota` (Authenticated Users hold "Add workstations to domain" on DCs) – removed by MAQ = 0, a deployment prerequisite (health Warning `domain.machineAccountQuota` while > 0, 9.2); certificate enrollment as a member of Domain Computers (default `Machine` template, published on every enterprise CA, and any template that grants Enroll to Domain Computers or a broader group – a certificate gives PKINIT logons for its whole validity, typically one year, and survives every password rotation) – **not** removed: the user declined Deny ACEs on the certificate templates on 2026-10-20 (accepted risk, below and 22); the service does not check the templates | Tier 0 admin (appendix A.2, A.5) |

Self-writable attributes that matter (lab, without the Deny ACE): the account itself can change
`msDS-SupportedEncryptionTypes` (e.g. re-enable RC4) and add SPNs / `dNSHostName` values for its own host name
(validated writes). It cannot change `userAccountControl`, `description` or `operatingSystem*`. The Deny ACE removes
these self-writes; that it does not block the kpasswd change (a control access right, not WriteProperty) is pilot
question Q22. Every change of the account raises `watchdog-self` regardless (section 9.3), and the keytab tool never
writes LDAP.

What remains is what every authenticated principal may do – notably creating new records in AD-integrated DNS zones
(default `Create all child objects` for Authenticated Users on the zone objects) – and what every member of Domain
Computers may do – notably enrolling for a certificate from any template that grants Enroll to Domain Computers.
Stolen keys therefore give no directory write beyond an ordinary domain account; the Watchdog does not watch the DNS
application partitions (outside its detection scope; `dnsNode` tombstones are dropped as noise, spec 10.9). The
instructions (2.5) offer an optional Deny `CC` for `ADMNG-WD$` on each AD-integrated zone.

**Accepted risk – certificate for the service account** (user decision 2026-10-20, ADR-0009 „Konsekwencje” and
„Ustalenia potwierdzone 2026-10-20 – druga tura”, item 6): no Deny ACEs for `ADMNG-WD$` are placed on the
`pKICertificateTemplate` objects and the service does not check the templates. Whoever steals the keytab (or learns
the one-time password in the minutes between enabling the account and `init`, 4.2) can request a certificate for
`ADMNG-WD$` wherever a template allows Domain Computers to enroll (the default `Machine` template on every enterprise
CA) and log on with it by PKINIT for the certificate's whole validity – the 30-day password rotation does not end
that. The Watchdog does not read the CA databases and therefore does not detect the issuance itself. What stays: the
per-CA check after `init` that no certificate was issued to the account (4.2 step 3, instructions 2.5 „Certyfikaty
wystawione na konto”) and the revocation step of the compromise recovery below.

Compromise recovery (instructions 2.5, "Gdy keytab mógł wyciec"), in this order: (1) disable the account (blocks
password and PKINIT logons alike); (2) on **every** enterprise CA (the `pKIEnrollmentService` objects) find the
certificates issued to it (`certutil -config <CA> -view -restrict 'RequesterName=CONTOSO\ADMNG-WD$,Disposition=20'`),
revoke each (`certutil -config <CA> -revoke <serial> 1`) and publish new CRLs at once (`certutil -config <CA> -crl`) –
this step stays although the templates are not locked (accepted risk above); (3) keep the account disabled until no
DC can still use a CRL that was current before the revocation – the KDC trusts a cached base/delta CRL until its
NextUpdate, so revocation alone does not block PKINIT at once (no certificate found → no wait); alternatively delete
the account and create it anew (instructions 8.2, then A.2…A.6): the new SID makes old certificates fail strong
certificate mapping; (4) set a new one-time password while the account is still disabled (the old keytab stops
working at once), then enable the account and run `init` right away (a disabled account cannot obtain the
`kadmin/changepw` ticket – `KDC_ERR_CLIENT_REVOKED`, lab).

### 4.2 Creation (Tier 0 admin on a Tier 0 PAW) and handover

1. The Tier 0 admin creates the account **disabled** with a **one-time password** (appendix A.2: `New-ADComputer …
   -Enabled $false`, CSPRNG-generated one-time password of 32+ characters), adds the Deny ACE for SELF (A.5), and only
   then enables the account (`Enable-ADAccount`) and passes the one-time password to the Debian administrator over a
   channel both trust (it is valid only until step 2, which happens within minutes). The Deny ACE exists before the
   one-time password works, so nobody who learns it can use a self-write. There is no certificate-template step (user
   decision 2026-10-20): certificate enrollment with the one-time password in the minutes before step 2 is part of the
   accepted risk of 4.1 and is what the per-CA check of step 3 looks for.
2. On `admng-wd`: `sudo admng-watchdog-keytab init` (prompts for the one-time password without echo) – section 4.4.
3. The Tier 0 admin verifies: `Get-ADComputer ADMNG-WD -Properties pwdLastSet,msDS-KeyVersionNumber` shows a fresh
   `pwdLastSet` (the one-time password is gone), `setspn -L ADMNG-WD` the three SPNs and – with an enterprise CA – the
   per-CA `certutil -view -restrict 'RequesterName=CONTOSO\ADMNG-WD$,Disposition=20'` query returns no certificate
   (instructions 2.5, „Certyfikaty wystawione na konto”).

Linux needs **no** AD rights and **no** LDAP for steps 2 and later rotations: only Kerberos (88) and kpasswd (464).

### 4.3 Keytab contents, salt and sharing

- Principals (each with the same keys, aes256-cts-hmac-sha1-96 and aes128-cts-hmac-sha1-96 only), **in this order** –
  the `ADMNG-WD$` entries first (4.4, 4.8):
  `ADMNG-WD$@CONTOSO.COM` (client: LDAP, SMB, kpasswd), `HTTP/admng-wd.contoso.com@CONTOSO.COM` (acceptor: API and
  OpenWEC), `host/admng-wd.contoso.com@CONTOSO.COM` (kept for tools that look the name up). Windows Server 2025 WinRM
  clients may ask for `HOST/` (Q1): the MIT acceptor bound to `HTTP/…` accepts a ticket for any SPN of the account,
  because all SPNs share the account key and MIT does not compare the ticket's server name after a successful decrypt
  (lab, libgssapi_krb5 1.20, acquired exactly as OpenWEC does; tickets for `host/` accepted even with a keytab holding
  only `HTTP/` entries). The `HOST/` SPN must still exist in AD so that the KDC issues such tickets.
- Salt (AD computer account): `<REALM>host<lower-case name without $>.<lower-case domain>` =
  `CONTOSO.COMhostadmng-wd.contoso.com` for every principal of the account; a wrong salt yields a keytab that fails only
  at use time (lab), so `init` and `rotate` verify the keys against the KDC before they are committed (4.4).
- The keytab always holds the **current and the previous kvno**; older kvnos are pruned. Removing the previous kvno at
  rotation time breaks still-valid service tickets (lab: HTTP 500 for a live ticket), and DC02 may still issue tickets
  with the previous key until replication.
- File `/etc/admng-watchdog/admng-wd.keytab`, owner root, group `admng-keytab`, mode 0640. Readers: `admng-watchdog`
  and `openwec` (supplementary group `admng-keytab`). Only the root-run keytab tool writes it, always as a new file in the
  same directory followed by an atomic `rename` (group and mode set before the rename), so readers never see a partial
  file. The MIT acceptor re-reads the keytab on every accept (lab: no restart after rotation).
- **Sharing with OpenWEC** (user decision 2026-10-20, ADR-0009; Q8 closed): one account and one keytab serve both the
  API and the WEF receiver, because both run on this Tier 0 host and the DCs address the same name. No second computer
  account, DNS name or keytab is created for the collector. Accepted risk: a compromise of the `openwec` user (e.g.
  through a parser bug in the network-facing OpenWEC) yields the long-term key of `HTTP/admng-wd.contoso.com`, the
  API's acceptor key. With it anyone can forge a service ticket (silver ticket) for `HTTP/admng-wd.contoso.com` with any
  client name, e.g. `adm-jan@CONTOSO.COM`; the API resolves roles from that name (6.2) and the MIT acceptor does not
  validate the PAC against the KDC, so a forged ticket for a member of `AdMng-Watchdog-Admins` gives full Watchdog admin
  (rule and channel changes are still announced to the previous recipients; retention cannot be lowered below the
  root-owned floor `Watchdog:Retention:MinEventsDays` and every decrease is Critical – 14.2). It also yields the
  `ADMNG-WD$` identity (directory and SYSVOL read), not the Watchdog database (0700), the channel secrets (systemd
  credential, root 0700) or any AD write. Splitting the keytab by principal does not help (one key per account).
  Mitigations (MUST):
  1. **API reachable only from the admin subnet** – nftables accepts 8443 only from `10.0.20.0/24` (16) and
     `AllowedRemoteSubnets` repeats it in the service; on Linux loopback and the host's own addresses are **not**
     exempt (6.1), so a forged ticket cannot be used from the server itself. The subnet is not a Tier 0 boundary: it
     also holds the AD Manager workstations of Viewers and helpdesk staff (instructions 3.2 and 3.3, 7.2 test 15), so
     a forged admin ticket works from **any** host in `AllowedRemoteSubnets`. Watchdog admin operations are allowed
     from the whole admin subnet: the admin role is **not** restricted to PAW addresses (user decision 2026-10-20,
     ADR-0009 – the open item is closed). A forged admin ticket used from any workstation of the subnet, including
     helpdesk and Viewer workstations, is therefore part of the accepted shared-keytab risk. (SSH stays PAW-only – 2,
     16.)
  2. **Isolation of the `openwec` user** – separate system user without a shell, only supplementary group
     `admng-keytab`; `openwecd` runs in the sandbox of 12.2 (no capabilities, `ProtectSystem=strict`, writes only
     `/var/lib/openwec`, binds only 5985); nftables accepts 5985 only from the DC addresses (16), so the network-facing
     parser is reachable only from the DCs; the Watchdog data folder, `/run/admng-watchdog` and
     `/etc/admng-watchdog/credentials` are not readable by `openwec` (11).
  3. **No outbound connections for `openwec`** – as an acceptor with a keytab OpenWEC never opens a connection (no KDC,
     DNS or LDAP traffic; Files output only). `openwecd.service` sets `IPAddressDeny=any` with only the DC addresses in
     `IPAddressAllow=` (drop-in written by the admin, 12.2), and the nftables output chain drops every packet of the
     `openwec` user that is not part of an established connection (`meta skuid "openwec" drop`, before the loopback and
     DC accepts – 16). A compromised `openwecd` can then neither send the keytab off the host (HTTPS, or DNS through the
     DC resolvers) nor use it itself from the server (TGT, LDAP, SMB, S4U2Self against the DCs, or the local API). B4
     acceptance test (lab VM): as user `openwec`, `curl https://packages.microsoft.com` and `kinit -k` fail, while WEF
     from a DC address still arrives.

  Residual risk: an OpenWEC bug triggered by traffic from a DC address, or another local path to the `openwec` user,
  combined with access to any host in the admin subnet. The `openwec` user is therefore a Tier 0 asset like the keytab
  itself.

  **Integrity of the WEF feed** (inherent to the collector design, not to the shared keytab): `openwec` writes every
  byte Watchdog ingests, including the `meta` fields (`Client`, `IpAddress`, `Subscription.Uuid`) and the event XML on
  which the identity checks of 7.6 rest, and its own subscription database. A compromised `openwec` user can
  therefore suppress or forge any WEF-derived record – actor and source of a change, and the event-log-only detections
  (1102, 4719, 4794, 1100/1104) – without a health signal, as long as it keeps passing some records per DC (7.9 only
  sees total silence). The checks of 7.6 reject other forwarders authenticated by an **intact** OpenWEC; they are not
  a control against a compromised one. The control is the isolation above (items 2 and 3); integrity of attribution
  belongs to the residual risk of the OpenWEC compromise.
  Optional hardening (not a default, not part of stage B): a separate computer account with its own DNS name and
  keytab for the collector would remove the API key from OpenWEC.

  **The `admng-watchdog` process** is the second network-facing process and holds strictly more than `openwec`: the
  same keytab, the decrypted Data Protection key (channel secrets) and the 3-year history (0700). Its inputs are not
  all trusted either: the API (8443, admin subnet only, Kerberos-authenticated), GPO files read from SYSVOL
  (`Registry.pol`, `GptTmpl.inf`, GPP XML – writable by every GPO editor, often a Tier 1 delegation; 8.4), the WEF
  records written by `openwec` (forged ones if `openwec` is compromised; account names in 4625/4771/4776 are chosen by
  anyone who can reach a DC), LDAP attribute values (some writable by the objects' owners), and – in the same sandbox
  – `smbclient` (C) parsing the DCs' SMB responses and its tar stream (8.3). The parsers of 8.4 are managed code,
  bound their input, fail closed per file and are tested with malformed and oversized fixtures (20.1); `smbclient` is
  the main native parser and talks only to the DCs. Containment (MUST): the sandbox of 12.1 (no capabilities,
  read-only system, binds only 8443) and the nftables output chain of 16 – DCs, the SMTP relay and TCP 80/443 to any
  address; the 80/443 rule is limited to the users `root` and `_apt` (updates) and `admng-watchdog`. The service
  cannot be narrowed to fixed addresses: Teams (Power Automate Workflows) is served from changing Microsoft cloud
  addresses, and the SMTP revocation check (`checkRevocation`, default on) fetches CRL/OCSP from the URLs in the
  relay's certificate chain. For the same reason `admng-watchdog.service` sets no `IPAddressDeny=` (12.1). Residual
  risk, **accepted by the user on 2026-10-20** (ADR-0009, Q23 closed): code execution as `admng-watchdog` can send the
  keytab, the channel secrets and the history to any Internet host over 80/443. Decision: direct egress on TCP 80/443
  only for `admng-watchdog` (plus `root` and `_apt` for updates), no outbound proxy, Teams kept; `openwec` and every
  other local user have no Internet egress (16).

### 4.4 `admng-watchdog-keytab` (new project `src/AdMng.Watchdog.Keytab`)

net10.0 console application, assembly name `admng-watchdog-keytab`, installed in `/usr/lib/admng-watchdog/`, linked as
`/usr/sbin/admng-watchdog-keytab`. MIT Kerberos through `LibraryImport` of `libkrb5.so.3` (Debian `libkrb5-3`):
`krb5_init_context`, `krb5_parse_name`, `krb5_get_init_creds_password` / `krb5_get_init_creds_keytab` with
`in_tkt_service = "kadmin/changepw"`, `krb5_change_password`, `krb5_c_string_to_key` (explicit salt),
`krb5_kt_resolve` / `krb5_kt_add_entry` / `krb5_kt_start_seq_get` (prune), `krb5_get_credentials` (kvno of a fresh
service ticket), the matching `krb5_free_*`. Environment: `KRB5_CONFIG=/etc/admng-watchdog/krb5.conf`,
`KRB5CCNAME=MEMORY:admng-keytab` (no credential cache on disk). The tool never logs or prints the password; the
password lives in a pinned byte array that is zeroed (`CryptographicOperations.ZeroMemory`) right after the change and
the key derivation. One instance at a time (`flock` on `/var/lib/admng-watchdog-keytab/lock` – created by tmpfiles for
manual runs and writable as `StateDirectory=` inside the unit, so both share the lock; `/run` is read-only under
`ProtectSystem=strict`).

**Entry order (MUST):** every keytab written by `init` or `rotate` begins with the `ADMNG-WD$@REALM` entries (SHOULD:
newest kvno first), followed by the `HTTP/` and `host/` entries. gssapi initiators that use `KRB5_CLIENT_KTNAME` with an
empty ccache (libldap through Cyrus SASL, i.e. every LDAP bind of the service) take the client principal from the
**first** keytab entry – lab: a keytab starting with the `HTTP/` entries made `ldapsearch -Y GSSAPI` fail with `Client
'HTTP/admng-wd.contoso.test@CONTOSO.TEST' not found in Kerberos database`, the same keys in the order above gave `SASL
username: ADMNG-WD$@CONTOSO.TEST`. `verify` checks the order and fails (`verifyFailed`) otherwise; the service reports
it as health Error `serviceAccount.keytabOrder` (4.7). B2 integration test: an LDAP bind through `KRB5_CLIENT_KTNAME`
after `init` and after `rotate`.

Commands (exit code 0 = ok, 1 = usage, 2 = failure; all output value-free):

| Command | Behaviour |
|---|---|
| `init [--principal 'ADMNG-WD$@CONTOSO.COM'] [--keytab …] [--fqdn admng-wd.contoso.com]` | Prompts for the one-time password (no echo; refuses a non-terminal stdin unless `--password-stdin`, used only by the lab). Gets initial credentials for `kadmin/changepw` with it, generates the 120-character password, `krb5_change_password`, derives the keys with the salt, obtains a service ticket for `host/admng-wd.contoso.com` with the new password from the primary KDC to learn the new **kvno**, writes a new keytab = the entries of the existing keytab (if any) with its newest kvno, plus the new entries (3 principals × 2 enctypes); keys derived from the one-time password are never written except in the `otpActive` fallback; anything older is pruned as in `rotate` (an admin reset must not break live tickets of the previous kvno – 4.3). Then it verifies `kinit -k` semantics (`krb5_get_init_creds_keytab`) against every KDC of `krb5.conf` (waits for replication up to `--replication-timeout`, default 15 min, polling every 30 s), writes `status.json`. If the change is refused because of the domain's minimum password age (open question Q5), it writes a keytab derived from the one-time password itself (kvno from a service ticket), sets status `otpActive` and the daily timer changes the password as soon as the KDC allows it. |
| `rotate --if-older-than 30d` | Run by the timer. Reads `status.json` (`lastChange`); younger → only `verify`, exit 0 – except when `status.json.result` is `otpActive` (keytab keys derived from the one-time password): then the age threshold is ignored and the change is attempted on every run until it succeeds. Otherwise: pre-flight (keytab directory writable, ≥ 1 MiB free, current keytab authenticates), initial credentials for `kadmin/changepw` from the keytab (newest kvno), generate, change, derive keys with kvno + 1 (confirmed from a service ticket), write the new keytab = entries with kvno ≥ new − 1 plus the new entries, atomic rename, verify against every KDC (replication wait as above, status `replicationPending` when DC02 is still behind after the timeout – not an error, `primary_kdc` covers it), write `status.json`. |
| `verify` | Checks the entry order (first entry = `ServicePrincipal`), authenticates from the keytab against each KDC, gets a service ticket for `HTTP/admng-wd.contoso.com`, compares kvnos; prints principals, kvnos and enctypes. |
| `status` | Prints `status.json`. |

`status.json` (root:admng-watchdog 0640): `{ "principal", "kvno", "lastChange", "lastAttempt", "result":
"ok|otpActive|changeFailed|keytabWriteFailed|replicationPending|verifyFailed", "errorCode": "<krb5 error name>",
"nextDue" }` – no secrets, no directory values.

### 4.5 Rotation unit and timer

`admng-watchdog-keytab.service` (oneshot, root, hardened – full text in 12.3) runs `rotate --if-older-than 30d`;
`admng-watchdog-keytab.timer`: `OnCalendar=*-*-* 04:20:00`, `RandomizedDelaySec=30min`, `Persistent=true`. The Debian
`msktutil` package is **not** installed; if an admin installs it, its `msktutil-auto-update.timer` must stay disabled
(documented).

### 4.6 Failure handling

| Failure | Effect | Detection | Recovery |
|---|---|---|---|
| KDC/kpasswd unreachable, policy refusal | password unchanged, keytab unchanged – nothing breaks | `status.json` `changeFailed` → health Warning `serviceAccount.rotation`; Error when `pwdLastSet` (read over LDAP) is older than 35 days | automatic on the next day; check ports 88/464 and the DC |
| Change succeeded, keytab write failed (disk, permissions) | the new password exists nowhere; existing tickets work for up to 10 h, then LDAP, SMB, API and WEF fail | `keytabWriteFailed` → health Error immediately + journald error; pre-flight checks make it unlikely | Tier 0 admin sets a new one-time password (`Set-ADAccountPassword -Identity 'ADMNG-WD$' -Reset -NewPassword $otp`, `$otp` generated as in A.2 – the `$` is required: the cmdlet resolves a sAMAccountName and does not append it), then `sudo admng-watchdog-keytab init`. Every remedy text that stage B emits uses this exact form |
| DC02 not yet replicated | DC02 refuses the new key for the client side; MIT retries at `primary_kdc` (DC01) | `replicationPending` (Info) | none; next `verify` clears it |
| Keytab older than AD (kvno mismatch, e.g. restored VM) | acceptor fails (`HTTP 401` with SPN hint, WEF stops) | the service compares the keytab's newest kvno with `msDS-KeyVersionNumber` of its own account → health Error `serviceAccount.keytabStale` | one-time password + `init` |
| One-time password still active | the human-known password works | `otpActive` → health Error until rotated | automatic (timer) |

### 4.7 Self-checks of the account (service, at start and hourly)

The service resolves its own account by `sAMAccountName` from `Watchdog:Kerberos:ServicePrincipal` and checks:

1. **Not privileged** – `tokenGroups` of `ADMNG-WD$` contains none of: domain `-512` (Domain Admins), `-516` (Domain
   Controllers), `-517` (Cert Publishers), `-518` / `-519` (Schema / Enterprise Admins, forest root), `-520` (Group
   Policy Creator Owners), `-521` (Read-only DCs), `-526` / `-527` (Key Admins / Enterprise Key Admins), `-498`,
   `S-1-5-32-544` (Administrators), `-548` (Account Operators), `-549` (Server Operators), `-550` (Print Operators),
   `-551` (Backup Operators), `-552` (Replicators), DnsAdmins (by name lookup), and no group the PrivilegeEvaluator
   counts as a privileged root. At start: refuse to run (exit code 8, journald error) unless
   `Watchdog:AllowPrivilegedServiceAccount = true`; at runtime: health Error `serviceAccount.privileged` (and the
   PrivilegeGranted event of the change itself is Critical through `watchdog-self`).
2. `userAccountControl` has `NOT_DELEGATED`, no delegation bits; `msDS-SupportedEncryptionTypes` = 0x18; SPNs and
   `dNSHostName` equal 4.1 → health Warning `serviceAccount.drift` (with the attribute name).
3. `pwdLastSet` age: Warning > 32 days, Error > 35 days (`serviceAccount.passwordAge`).
4. Keytab newest kvno = `msDS-KeyVersionNumber` (or one less while `replicationPending`) → else Error
   `serviceAccount.keytabStale`.
5. `status.json` of the keytab tool → `serviceAccount.rotation`; the first keytab entry is not `ServicePrincipal`
   (4.4) → Error `serviceAccount.keytabOrder`.
6. Clock skew: `currentTime` of the RootDSE vs. local time each detection cycle → Warning > 60 s, Error > 240 s
   (`host.clockSkew`; Kerberos fails at 300 s).
7. **Deny ACE for SELF still in place** – read the account's own `nTSecurityDescriptor` with SD flags 4 (DACL; readable
   by the account, appendix C) and require an explicit Deny ACE for `S-1-5-10` whose mask contains at least
   CreateChild | DeleteChild | Self | WriteProperty (`0x2B`); otherwise health Warning `serviceAccount.drift` with the
   reason `denySelfAceMissing` (someone with WriteDacl on the Tier 0 OU, or a "restore defaults" on the object, would
   otherwise silently re-enable the self-writes of 4.1).

There is no certificate-template check: the template Deny ACEs were declined by the user on 2026-10-20 (accepted
risk, 4.1), so the service neither reads `pKICertificateTemplate` DACLs nor raises template-related health reasons.

The Status tab gets a "Konto usługi" section (account DN, kvno, last rotation, next due, enctypes, checks above).

### 4.8 Kerberos client configuration

`/etc/admng-watchdog/krb5.conf` (conffile, 0644; also used by openwecd and the keytab tool; `/etc/krb5.conf` is left
alone):

```ini
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
```

`primary_kdc` and `kpasswd_server` name the PDC emulator (update when the role moves – the service warns with
`host.primaryKdcNotPdc` when `fSMORoleOwner` differs). Environment of the service (systemd unit, section 12.1 – .NET
`Environment.SetEnvironmentVariable` does **not** reach libkrb5/libldap, lab): `KRB5_CONFIG`,
`KRB5_KTNAME=FILE:/etc/admng-watchdog/admng-wd.keytab` (acceptor), `KRB5_CLIENT_KTNAME=FILE:…same…` (gssapi acquires the
TGT automatically, as the principal of the **first** keytab entry – hence the entry order of 4.4),
`KRB5CCNAME=FILE:/run/admng-watchdog/krb5cc` (tmpfs, 0600, needed by the `smbclient` child process – smbclient does
**not** use the client keytab, it needs a valid TGT in this ccache, 8.3), `KRB5RCACHEDIR=/var/tmp` (private through
`PrivateTmp=yes`; MIT replay cache).

---

## 5. LDAP from Linux

### 5.1 Default: 389 + SASL GSSAPI sign and seal

- `Directory.Authentication = CurrentUser` maps on Linux to S.DS.P `AuthType.Negotiate` with no credential: libldap +
  Cyrus SASL GSSAPI (`libsasl2-modules-gssapi-mit`) authenticate as `ADMNG-WD$` from `KRB5_CLIENT_KTNAME`.
- `LdapSessionOptions.Signing`/`Sealing` throw on Linux (lab). Confidentiality is enforced by
  `/etc/admng-watchdog/ldap.conf` (conffile, 0644) passed with `LDAPCONF`:

  ```
  # AdMng Watchdog – private libldap configuration (LDAPCONF)
  SASL_NOCANON   on
  SASL_SECPROPS  minssf=56,noanonymous,noplain
  REFERRALS      off
  TLS_REQCERT    demand
  #TLS_CACERT    /etc/admng-watchdog/tls/dc-ca.pem     (LDAPS option only)
  ```

  `SASL_NOCANON on` stops libldap/Cyrus from reverse-resolving the DC address (lab: `127.0.0.1 → localhost → ldap/localhost`);
  `minssf=56` makes the bind fail rather than run unencrypted.
- After every bind the provider reads the negotiated SSF (`ldap_get_option(LDAP_OPT_X_SASL_SSF = 0x6104)` through
  `LibraryImport`; **not** 0x6109 – lab) and refuses a session below 56 (health Error `directory.notSealed`, reconnect).
  `LdapConnectionFactory.IsEncrypted` uses this value on Linux instead of assuming sealing. The SSF is shown in Status.
- The startup check parses `LDAPCONF` and refuses to start (exit code 2, journald error) when `Security = SignAndSeal`
  and `SASL_SECPROPS` lacks `minssf` ≥ 56, or when the service user's home contains `.ldaprc`/`ldaprc`.
- DCs are addressed by FQDN only (Kerberos needs `ldap/<fqdn>`); IP literals are refused.

### 5.2 Option: LDAPS (636)

`Directory.Security = Tls`, `Port = 636`, `ldap.conf`: `TLS_CACERT /etc/admng-watchdog/tls/dc-ca.pem`,
`SASL_SECPROPS minssf=0,maxssf=0,noanonymous,noplain` (Windows DCs reject a SASL security layer inside TLS; libldap
would otherwise negotiate one – lab). The startup check requires `maxssf=0` exactly when `Security = Tls`. On Linux
`VerifyServerCertificate` is unsupported; trust comes from `TLS_CACERT` (or `TrustedCertificatesDirectory` +
`StartNewTlsSessionContext()`, guarded by `OperatingSystem.IsLinux()` because the APIs carry CA1416 on Windows and
warnings are errors). Channel binding (`SASL_CBINDING tls-endpoint`) towards Server 2025 was not verified – open
question Q4; the option stays secondary.

### 5.3 DC selection

- New bootstrap setting `Directory.Servers` (ordered FQDN list, required on Linux; `WindowsDomainLocator` /
  `DsGetDcName` does not exist there). With `PreferPdc` the provider connects to the first reachable server, reads
  `fSMORoleOwner` of the domain head and reconnects to the PDC emulator when it is in the list. Failover after
  `FailoverAfter` to the next server (unchanged spec 10.3 semantics: known DC → incremental, never-seen DC → resync).
- One DC per session (rule 4): LDAP, role resolution, own-account checks and SYSVOL reads use the **current** session
  server.

---

## 6. API on Linux

### 6.1 Kestrel + Negotiate with the keytab

- Kestrel HTTPS on 8443 (HTTP/1.1, TLS 1.2/1.3) with `AddNegotiate` as on Windows; `KRB5_KTNAME` supplies the acceptor
  keys of `HTTP/admng-wd.contoso.com`. `PersistKerberosCredentials = false`. No `EnableLdap`.
- `KerberosOnlyEvents.OnAuthenticationFailed` MUST answer **401** and `HandleResponse()` – on Linux GSS failures (wrong
  key, SPN mismatch, CNAME use) surface as `AuthenticationFailureException` and would otherwise be HTTP 500 (lab); log
  2501 with the hint "check that clients use the A record admng-wd.contoso.com and that the keytab kvno matches AD".
- `RequireKerberos` (default true) keeps rejecting every identity whose `AuthenticationType` is not `Kerberos`. NTLM is
  unavailable anyway (no `gss-ntlmssp`; the package `Conflicts: gss-ntlmssp`).
- `AllowedRemoteSubnets = ["10.0.20.0/24"]` by default in the shipped `appsettings.json` (defence in depth to nftables).
  On Linux `RemoteAddressFilter` MUST NOT exempt loopback for non-health paths: requests from `127.0.0.0/8`, `::1` and
  the machine's own addresses get 403 unless they fall into `AllowedRemoteSubnets` (a local process holding the shared
  keytab could otherwise use a forged ticket – 4.3); `/healthz` keeps answering loopback and own addresses (`--health`);
  a request without a remote IP (in-process transport, `--smoke-test`) still counts as local. Service test: forged-name
  ticket over loopback → 403.

### 6.2 Role resolution (`Auth/KerberosSidClaimsTransformation.cs`, `Auth/PrincipalSidResolver.cs`)

An `IClaimsTransformation` (idempotent – it may run more than once per request) for Kerberos identities:

1. `identity.Name` = `name@REALM` (lab: the only claim). Split at the last `@`; the realm MUST equal
   `Watchdog:Kerberos:Realm` (case-insensitive), the name must match `^[^@\\/"\[\]:;|=+*?<>,]{1,20}\$?$`
   (sAMAccountName syntax) – otherwise no SID claims (403) and log 2502 (realm only).
2. Cache lookup (key = canonical principal): positive entries `Authorization.RoleCacheSeconds` (default 300), negative
   entries 60 s.
3. Miss: over the service's LDAP session (`IDirectorySessionProvider`), search the domain NC for
   `(&(sAMAccountName=<LdapFilter-escaped>)(|(objectCategory=person)(objectCategory=computer)))` (attributes
   `objectSid`), exactly one result required; then a base-scope read of `tokenGroups` on that DN (constructed attribute,
   base scope only; includes the primary group and nested groups). Readable through the per-class `GR` grants of
   appendix A.5 (`user`, `inetOrgPerson`, `computer`) also in hardened domains where "Pre-Windows 2000 Compatible
   Access" no longer contains Authenticated Users. **Not** Windows Authorization Access Group: it only grants read of
   `tokenGroupsGlobalAndUniversal` (computer/user default SD `(OA;;RP;46a9b11d-…;;S-1-5-32-560)`), which this resolver
   does not read. Self-check `PermissionSelfCheck` gets the code `tokenGroupsUnreadable` (remedy: the A.5 grants); it
   tests a non-protected account (protected accounts inherit AdminSDHolder's Authenticated Users read and always
   pass).
4. Claims added: `ClaimTypes.PrimarySid` (objectSid), one `ClaimTypes.GroupSid` per tokenGroups SID, and
   `admng:sidSource = ldap`. The unchanged `SidRoleHandler` then denies on `DeniedSids` first (user SID or any group
   SID) and grants on `ViewerSids`/`AdminSids`.
5. LDAP unavailable: cached entries are used up to `Authorization.RoleCacheMaxStaleSeconds` (default 3600) after their
   expiry (so admins can still read Status during a DC outage); without a usable entry the request gets **503**
   (`roleLookupUnavailable`), never a role. `DeniedSids` always applies.

Effects: group changes apply within 5 minutes (better than the up to 10 h of ticket-embedded SIDs on Windows); the
real-Kerberos integration test on Linux can now prove roles (Viewer/Admin/403), not only authentication.
`Authorization.WatchdogHostGroupSids` (gMSA host group) is obsolete on Linux: ignored with a startup warning;
`AdminSids`/`ViewerSids` groups remain High-tier privileged roots.

### 6.3 TLS certificate (`Hosting/PemCertificateSource.cs`)

- `Hosting.Certificate.Path` (PEM chain, leaf first) and `KeyPath` (PEM private key), default
  `/etc/admng-watchdog/tls/admng-wd.crt` / `.key` (root:admng-watchdog, 0644 / 0640). Loaded with
  `X509Certificate2.CreateFromPemFile`; reloaded every 10 minutes and on file change; the existing signing self-check,
  serverAuth EKU check, expiry warnings and `certificate.key` health apply. A reload that finds a key that does not
  belong to the certificate (e.g. between the two renames of a renewal, or a key overwritten before the CA issued the
  new certificate) MUST keep serving the current certificate and raise health Error `certificate.key` instead of
  applying the pair; the next reload retries. Renewal of the company-CA variant writes `admng-wd.new.key` /
  `admng-wd.new.crt` next to the live files and swaps them only after the pair is verified (instructions 3.8,
  "Odnowienie"). `Thumbprint`/`Subject`/`Store` stay
  Windows-only until section 17.
- **Company CA variant** (appendix A.8): on Debian
  `openssl req -new -newkey rsa:3072 -nodes -keyout admng-wd.key -out admng-wd.csr -subj '/CN=admng-wd.contoso.com' -addext 'subjectAltName=DNS:admng-wd.contoso.com' -addext 'extendedKeyUsage=serverAuth'`;
  a Tier 0 admin: `certreq -submit -f -attrib "CertificateTemplate:WebServer" -config "<CAHost>\<CAName>" admng-wd.csr admng-wd.cer`
  and `certutil -f -config "<CAHost>\<CAName>" -ca.chain chain.p7b` (without `-f` both refuse to overwrite an existing
  output file, so a renewal on the same PAW would copy the old certificate; the instructions also delete the PAW copies
  before and after); the chain is installed on Debian. Check for an enterprise CA:
  `certutil -config - -ping` / `certutil -TCAInfo`, or `Get-ADObject -SearchBase "CN=Enrollment Services,CN=Public Key Services,CN=Services,$((Get-ADRootDSE).configurationNamingContext)" -LDAPFilter '(objectClass=pKIEnrollmentService)'`.
- **Self-signed variant** (default when no certificate is configured): `postinst` generates ECDSA P-256, 825 days,
  `CN`/SAN `DNS:<hostname --fqdn>`, EKU serverAuth, and prints its SHA-256 fingerprint; `sudo admng-watchdog
  cert-fingerprint` prints it at any time (also logged as Information 1008 at every start – a fingerprint is not
  directory data). Renewal: `sudo admng-watchdog cert-renew-self-signed` (operators re-confirm in AD Manager).
- `--health` on Linux accepts the configured certificate by comparing it with the file (local pin) instead of the
  system trust store.

---

## 7. WEF ingestion through OpenWEC

### 7.1 OpenWEC build and package (`admng-openwec`)

- Source: `https://github.com/cea-sec/openwec`, **pinned commit `86de07f` (2026-09-08)** – includes SPNEGO support
  (#307) and the filter rework (#186); release v0.3.0 (2024-09-07) has neither and no Debian 13 package. Built in CI
  in `rust:slim-bookworm` and `rust:slim-trixie` with `cargo build --release --locked` and `cargo deb`, producing
  `admng-openwec_0.3.0+git20260908.86de07f-1~deb12_amd64.deb` and `…~deb13_amd64.deb` (different `libssl` package
  names). `Provides`/`Conflicts`/`Replaces: openwec`. GPL-3.0: the exact source tarball and the build recipe are
  attached to the same GitHub release; `/usr/share/doc/admng-openwec/copyright` says so. OpenWEC runs as a separate
  process and is not linked into AdMng.
- `postinst`: `openwec db init` on first install, `openwec db upgrade` on upgrades (as user `openwec`), then – when
  `/etc/openwec/subscriptions/admng-dc-security.toml` exists and contains no placeholders –
  `openwec subscriptions load -y /etc/openwec/subscriptions/admng-dc-security.toml` (the **single file**, never the
  directory). It refuses (message, no load, exit 0) when the directory holds any other file: the pinned OpenWEC
  (`cli/src/config.rs` `visit_dirs()` / `load_from_path()`) parses every file below a directory regardless of its
  extension and `load` stores each one, so a `.dpkg-dist`, an editor backup or a second copy with the same uuid would
  silently replace the live subscription, and an unparsable file aborts the whole load. Loading one file without
  `--keep` also removes every other subscription from the OpenWEC database – intended (one subscription per
  collector).
- `admng-watchdog` `Depends: admng-openwec (>= 0.3.0+git20260908)`.

### 7.2 OpenWEC configuration (`/etc/openwec.conf.toml`, conffile of `admng-openwec`, root:openwec 0640)

```toml
[server]
keytab = "/etc/admng-watchdog/admng-wd.keytab"
db_sync_interval = 5
flush_heartbeats_interval = 5

[logging]
verbosity = "warn"
server_logs = "stderr"          # journald through the unit
# access_logs: leave unset – absent key = no access log ("None" would be parsed as a file name: LoggingType::from_str
# only knows stdout/stderr; under ProtectSystem=strict opening "/None" fails and openwecd panics in a restart loop)

[database]
type = "SQLite"
path = "/var/lib/openwec/db/openwec.sqlite"

[cli]
read_only_subscriptions = true  # subscriptions only from files (openwec subscriptions load)

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

### 7.3 Subscription (`/etc/openwec/subscriptions/admng-dc-security.toml`)

Generated by `sudo admng-watchdog print-wec-subscription --dc 'DC01$@CONTOSO.COM' --dc 'DC02$@CONTOSO.COM'` from
`SecurityEventCatalog` (so the catalog and the subscription cannot drift; `SecurityEventQueryBuilder` rules: ≤ 20 EventID
expressions per `Select`, XML-escaped) and written by the admin with mode 0644 (`… | install -m 0644 /dev/stdin
/etc/openwec/subscriptions/admng-dc-security.toml` – the root shell of the instructions runs with `umask 077`). Nothing
is shipped below `/etc/openwec/subscriptions/` (the directory is created empty and stays admin-owned); the template is
the non-conffile example `/usr/share/admng-openwec/admng-dc-security.toml.example` (13.2). Before
loading, the XML is validated on **both** DCs with
`Get-WinEvent -FilterXml ([xml](Get-Content .\admng-query.xml -Raw)) -MaxEvents 5` (`-LogName` and `-FilterXml` are in
different parameter sets; open question Q6 for
the 4776 `Status` filter). **Changing the event list later:** new Security event IDs are added to the **same**
subscription – same `uuid` and `name`: regenerate the file and load it again with the same `openwec subscriptions
load -y <file>`. `store_subscription` is an upsert on `uuid` and bookmarks are keyed by the subscription `uuid`, so the
DCs' WEF bookmarks survive and no events are lost; the DCs pick up the new version at their next refresh. Never add
another **channel** (OpenWEC `doc/query.md`: a client with a bookmark then sends every existing event of the new
channel). Never change the `uuid`: a new uuid with the same `name` fails (`UNIQUE constraint failed:
subscriptions.name`); with a new name, `load` deletes the old subscription together with its bookmarks, and the new
one starts at the next event (`read_existing_events = false`), so the gap is lost; and Watchdog rejects every record
until `Watchdog:Collector:SubscriptionUuid` is changed too (7.6). `print-wec-subscription` therefore MUST always emit
the configured `Watchdog:Collector:SubscriptionUuid` and the name `admng-dc-security` (never a fresh uuid). It MUST read
only the `Watchdog:Collector` section and MUST NOT run the full configuration validation: the instructions run it in
4.4, right after the package install and before `appsettings.json` is edited (5.2), so it has to succeed with the
shipped placeholder SIDs of 14.1 (packaging CI test, 20). The instructions mark `SubscriptionUuid` as "do not change"
in 5.2.

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

Notes: the event list is the change-related set plus 4624 logon types 3 and 10, 4625, 4771 and failed 4776. 4625
(failed logons at the DC – lockout sources, spec 11.4 #6b) and RDP logons (4624 type 10) were added to the list of
2026-10-19 and confirmed by the user on 2026-10-20. 4771 is a failure event by definition. The Windows XPath subset
cannot exclude machine accounts (`ends-with` is unsupported), so 4624 of computer accounts are forwarded and dropped by
Watchdog (7.7) – size the DC Security log and measure the volume in the pilot (Q7). `{client}` is the Kerberos
principal with characters outside `[a-zA-Z0-9.\-_@]` removed: `DC01$@CONTOSO.COM` → directory `DC01@CONTOSO.COM`.

### 7.4 Output files, rotation and deletion

- `/var/lib/openwec/out` is created by `admng-watchdog`'s tmpfiles entry as `2750 openwec:admng-watchdog` (setgid:
  OpenWEC's per-client subdirectories and files inherit the group `admng-watchdog`; `openwecd` runs with `UMask=0027`,
  so they are 2750/0640). Watchdog only **reads**; it never writes, renames or deletes there (`ReadOnlyPaths=` in its
  unit).
- **Rotation and cleanup** by `admng-wec-rotate.service` (root oneshot, hardened, run hourly by
  `admng-wec-rotate.timer`; script `/usr/lib/admng-watchdog/wec-rotate`):
  1. every non-empty `/var/lib/openwec/out/*/security.jsonl` is renamed to `security.jsonl.<UTC yyyyMMddTHHmmssZ>`
     (same directory, inode unchanged);
  2. the unit then reloads `openwecd` (`ExecStartPost=`, 12.4 – SIGHUP: OpenWEC drops its output contexts and
     reopens the path on the next write – verified in the source);
  3. a rotated file is deleted when Watchdog's progress file `/run/admng-watchdog/wec-progress.json` (written by
     Watchdog after every commit; root reads it) lists its `(device, inode, firstLineSha256)` as **complete**
     (consumed offset = size, committed, and the file unmodified for ≥ 5 minutes);
  4. a rotated file older than `MAX_UNCONSUMED_DAYS` (`/etc/default/admng-wec-rotate`, default 7, maximum 30 –
     ADR-0008/0009) is deleted anyway with a journald warning (file name only); Watchdog reports the loss (7.5); the
     same happens, oldest first, while the rotated files of all DCs together exceed `MAX_UNCONSUMED_MB` (default 8192)
     – a WEF flood or a stopped Watchdog must not fill `/var`, where the database, its backups and the outbox live
     (OpenWEC itself never checks free space);
  5. a missing or unreadable progress file (Watchdog stopped – `/run/admng-watchdog` is removed with its unit) means
     "nothing confirmed": steps 1, 2 and 4 still run, step 3 deletes nothing.
- Durability: the Files driver writes with `write_all` and no `fsync`; OpenWEC acknowledges the batch to the DC after the
  write, so a host power loss can lose up to a few seconds of events (open question Q12). A crash of `openwecd` itself
  loses nothing (unacknowledged batches are resent by the DC; duplicates are absorbed, 7.5).

### 7.5 Tailer (`Service/Attribution/WecFileIngestionJob.cs`, `WecFileCursor.cs`, `WecProgressFile.cs`)

- One logical source per client directory; cursor in `eventlog_cursors`: `source = "wec:<client dir name>"`,
  `last_record_id` = byte offset, `bookmark_xml` =
  `<WecFileCursor Device="…" Inode="…" Offset="…" FirstLineSha256="…" Name="security.jsonl.20261019T0300Z"/>`. The
  first-line hash protects against inode reuse after deletion.
- Order of files in a directory: rotated files by their timestamp suffix, then the active file. On start the job finds
  the cursor's file by `(device, inode, firstLineSha256)` and resumes at the offset; files after it start at 0; files
  before it are complete. Cursor file not found → health Error + `HealthEventLogGap` reason `collectorFileLost` (per
  DC) and continue with the oldest existing file.
- Reading: `FileStream` (`FileShare.ReadWrite | FileShare.Delete`), wake on `FileSystemWatcher` (inotify) or every 2 s;
  only **complete lines** (up to the last `\n`) are consumed; a line longer than 1 MiB is skipped and counted.
- Per batch (≤ 1000 lines or end of data): parse → identity checks (7.6) → reduce (7.7) → one
  `ISecurityEventStore.AppendAsync(records, cursor)` transaction (records and the new cursor together – **at-least-once**:
  a crash before the commit re-reads the batch; the store ignores duplicates). Event-log-only detections (1102, 4719,
  4794, 1100/1104) keep their `RecordEventIds` (Guid from computer, record id, time, event id) – re-reading never
  repeats them.
- **Poison input** (MUST): a line that crashes or hangs the process would otherwise be re-read after every restart
  (the cursor is committed only after the batch). Before a batch is processed the job records `(file, offset,
  attempts + 1)` in the cursor's bookmark (`Attempts` attribute) in its own transaction; after 3 attempts at the same
  offset the batch is processed line by line and the line that fails again is skipped and counted as malformed
  (`wec.<dc>.malformedLines`). `GpoContentJob` does the same with `gpo_content_queue.attempts`: incremented before a
  GPO is read, and after 3 attempts the GPO is marked `gpo.<guid>.parseError` and skipped until its `versionNumber`
  changes again.
- **Ingest budget for logon and failure records** (MUST): 4624/4625/4771/4776 beyond
  `Watchdog:Collector:MaxLogonRecordsPerDcPerMinute` (bootstrap, default 20 000) are counted, not stored, and raise
  `HealthEventLogGap` with reason `flooding` (spec 11.1) for that DC; change-related records are never dropped. An
  unauthenticated attacker can produce 4625/4776 at will (NTLM attempts with random names against a DC). SHOULD:
  failures for non-existent accounts (`Status`/`SubStatus` `0xC0000064`) are reduced to per (DC, source, minute)
  counters with a capped set of distinct names – enough for `PasswordSprayDetector` – instead of one row each.
- Malformed line (not JSON, missing fields): counted (`wec.<dc>.malformedLines`, Warning); when OpenWEC died
  mid-write, the next batch is appended to a partial line – the parser resynchronises at the last occurrence of the
  record start token (`{"meta":` – field order to be confirmed in B4) inside the line.
- After each commit the job rewrites `/run/admng-watchdog/wec-progress.json` (atomic rename; content: per file
  `device`, `inode`, `firstLineSha256`, `offset`, `size`, `complete`).
- Idempotency key of stored records: **(computer, record id, time created)** (migration 0004, 7.7) – OpenWEC may
  deliver duplicates, and a cleared log can restart record numbers.

### 7.6 Mapping RawJson → `SecurityEvent` and identity checks

A line is `{"meta": {"IpAddress", "TimeReceived", "Client", "Principal" (deprecated upstream, same value),
"Subscription": {"Name", "Version", "Uuid", "Uri"}},
"data": "<Event …>…</Event>"}` (`content_format = Raw`, no rendering info). `WecRecordReader` (Core, pure) reads it with
`Utf8JsonReader` (no DOM, no dynamic types) and hands `data` to the **unchanged** `SecurityEventParser` (DTD prohibited,
by-name EventData, masking, %% decoding – spec 11.3). Accepted only when all hold, otherwise dropped and counted
(`wec.<dc>.identityMismatch`, Error – the rogue-forwarder signal):

1. `meta.Subscription.Uuid` = `Watchdog:Collector:SubscriptionUuid`;
2. `meta.Client` (fallback `meta.Principal` only when `Client` is absent; case-insensitive) is
   `<sAMAccountName>@CONTOSO.COM` of a **writable DC** known to
   `DomainControllerDirectory` (RODCs and DCs of other domains are refused);
3. `System/Computer` equals that DC's `dNSHostName`;
4. `meta.IpAddress` is one of the DC's A/AAAA records (resolved at every DomainControllerDirectory refresh) or in
   `Watchdog:Collector:AdditionalSourceAddresses` (NAT / multi-homed DCs);
5. `System/Channel` = `Security` and the provider passes `EventSourceGuard` (spec 11.1).

These checks reject records of other forwarders that an **intact** OpenWEC authenticated (a non-DC machine, an RODC,
a DC of another domain, a wrong subscription). Every field they read is written by OpenWEC, so they do not protect
against a compromised `openwec` user (4.3, "Integrity of the WEF feed").

Resulting `SecurityEvent`: `Source` = DC FQDN, `Computer` = `System/Computer`, `RecordId` = `EventRecordID`,
`TimeCreated` = `System/TimeCreated/@SystemTime`. `EvidenceRef.Dc` = DC FQDN, `Log` = `Security` (the record's origin).
`meta.TimeReceived − TimeCreated` feeds the per-DC delivery lag in Status.

### 7.7 Reduction, storage and retention

- Change-related ingestion set (spec 11.2): unchanged reduction (spec 11.1 "Reduction at parse time").
- **Logon and failure records** (4624, 4625, 4771, 4776) – new, stored in `security_events` with only these `Data`
  fields: 4624 `TargetUserSid`, `TargetUserName`, `TargetDomainName`, `TargetLogonId`, `LogonType`, `IpAddress`,
  `WorkstationName`, `AuthenticationPackageName`; 4625 `TargetUserName`, `TargetDomainName`, `Status`, `SubStatus`,
  `LogonType`, `IpAddress`, `WorkstationName`; 4771 `TargetUserName`, `TargetSid`, `Status`, `IpAddress`; 4776
  `TargetUserName`, `Workstation`, `Status`. Dropped at ingestion by default (runtime settings, 7.8): 4624 of computer
  accounts (`TargetUserName` ending in `$`) and of ANONYMOUS LOGON (S-1-5-7); 4624 with LogonType other than 3/10.
- Migration **0004_security_events_v2** (forward-only, rebuilds the table – it holds at most 30 days of reduced records
  on an upgraded 0.2.0 database and nothing on a new install): new column `logon_id TEXT` (4624 `TargetLogonId`,
  normalized `0x…`), uniqueness `UNIQUE (computer, record_id, time_created)` instead of `(computer, record_id)`,
  indexes `ix_sec_logon ON security_events(computer, logon_id) WHERE logon_id IS NOT NULL` and
  `ix_sec_user_code ON security_events(target_user, event_code, time_created) WHERE target_user IS NOT NULL`.
- Retention: `Retention.SecurityEventsDays` (30) for the change-related set; new `Retention.LogonRecordsHours`
  (default 48, range 12–720; MUST be ≥ `LogonLookback` + the last `PendingRetries` step – validator code
  `logonRetentionTooShort`) for 4624/4625/4771/4776, purged **hourly** by `StorageGuardJob` (batches of 5 000,
  `secure_delete`). The resolved source is copied into the watch event's `Attribution` (kept `EventsDays`).
- Size guide: a reduced logon row ≈ 0.3 KB incl. indexes; 200 000 forwarded user logons/day × 48 h ≈ 120 MB.

### 7.8 Attribution in collector mode

- Contracts: `AttributionSourceKind.OpenWec = 3` (new, default on Linux). `DomainControllers` and `ForwardedEvents`
  remain as values for stored-configuration compatibility; on Linux the validator rejects them (`sourceUnsupportedOnHost`)
  and the configuration migration v1 → v2 (`SchemaVersion = 2`) rewrites them to `OpenWec` once, with a config audit
  entry "migrated for Linux".
- Runtime settings added to `AttributionSettings`: `DropComputerLogons` (true), `DropAnonymousLogons` (true),
  `CollectorSilenceWarning` (15 min), `CollectorSilenceError` (60 min). Host-level settings live in the bootstrap
  section `Watchdog:Collector` (8.4 below) because an API admin must not redirect file reading.
- New Core abstraction `Abstractions/ISecurityEventLookup.cs` used by `LogonSourceResolver`, `LockoutSourceResolver`
  and `PasswordSprayDetector` instead of `ISecurityEventSource`:
  `FindLogonAsync(dc, logonId, from, to)`, `FindFailuresForAccountAsync(accountName, codes, from, to, dcs)`,
  `FindFailuresAsync(codes, from, to, dc)`. Implementations: `StoredSecurityEventLookup` (Core, over
  `ISecurityEventStore.FindAsync`; `SecurityEventCriteria` gets `LogonId`), and – until section 17 – the Windows
  `LiveSecurityEventLookup` wrapping today's queries. Caching, the `SourceQueryGate` timeouts and the confidence caps
  stay as specified; `SOURCE_NOT_COLLECTED` = no 4624 from that DC in the last hour; `SOURCE_LOGON_NOT_FOUND` = no
  record within `LogonLookback`.
- Changes made through ADWS (ActiveDirectory PowerShell module, AD Administrative Center) reach the DS from the DC
  itself: the 5136/47xx `SubjectLogonId` resolves to a loopback 4624 (`IpAddress` `::1`/`127.0.0.1`) → SourceMethod
  "local" ("lokalnie na DC"), as in spec 11.4 #6. The instructions say so (WATCHDOG-LINUX 7.1/7.2, test 18). Candidate
  (SHOULD, only if pilot question Q20 shows a usable pattern): an `AdwsSourceCorrelator` that, for a loopback 4624 type
  3, takes the same `TargetUserSid`'s most recent non-loopback 4624 type 3 on that DC within 60 s before, with
  SourceMethod "adws", confidence capped at Low and the reason text (pl) „przez ADWS – adres z logowania tej samej
  osoby”.
- `SecurityLogIngestor` RPC paths are not used on Linux (`NullSecurityEventSource`); `AttributionEngine` correlates
  whatever the tailer stored and keeps all reason codes. `DC_UNREACHABLE` becomes "collector silent" in collector mode:
  remedy text (pl) „Sprawdź, czy DC przekazuje zdarzenia (GPO subskrypcji, WinRM, zapora 5985) – Stan → Kolektor WEF.”

### 7.9 Health signals per DC (Status → "Kolektor WEF")

| Check | Condition | Level |
|---|---|---|
| `wec.<dc>.silent` | no record from that DC for `CollectorSilenceWarning` / `CollectorSilenceError` (DCs always produce 4624 type 3, so silence means broken forwarding) | Warning / Error + `HealthAttributionDegraded` |
| `wec.<dc>.notForwarding` | a writable DC of the domain never delivered | Error ("DC nie przekazuje zdarzeń – sprawdź GPO subskrypcji") |
| `wec.<dc>.lag` | median `TimeReceived − TimeCreated` > 5 min over 1 h | Warning |
| `wec.<dc>.identityMismatch` | any rejected record (7.6) | Error |
| `wec.<dc>.malformedLines` | > 0 in 24 h | Warning |
| `wec.<dc>.backlog` | unconsumed bytes > 256 MiB or a rotated file unconsumed > 1 h | Warning |
| `wec.collectorFileLost` | 7.5 | Error + `HealthEventLogGap` |
| `wec.service` | `openwecd.service` not active (read through `systemctl is-active` is not possible from the sandbox – the check uses "no file written by any DC for 10 min" together with DC reachability over LDAP) | Error |

### 7.10 DC-side configuration (input for the instructions – appendix A.7)

GPO "AdMng - WEF" linked to the Domain Controllers OU: (1) Computer Configuration → Policies → Administrative Templates
→ Windows Components → Event Forwarding → **Configure target Subscription Manager** =
`Server=http://admng-wd.contoso.com:5985/wsman/SubscriptionManager/WEC,Refresh=60` (3600 after the pilot); (2) System
Services → **Windows Remote Management (WS-Management)** = Automatic; (3) Windows Components → Event Log Service →
Security → **Configure log access** =
`O:BAG:SYD:(A;;0xf0005;;;SY)(A;;0x5;;;BA)(A;;0x1;;;S-1-5-32-573)(A;;0x1;;;NS)` (NETWORK SERVICE may read Security, no
reboot). The value **replaces** the channel's whole access SDDL on every DC (no merge), in the GPMC and in the
`Set-GPRegistryValue` path alike: the instructions first read `channelAccess` on every DC (`wevtutil gl security`),
carry any non-default ACE (e.g. a SIEM/EDR agent's read grant) into the value and check after `gpupdate` that those
ACEs are still present; (4) audit policy and SACLs as in `docs/WATCHDOG.md` 4.5/4.6 and
instructions 2.10/2.11: the audit GPO is linked with `-Order 1` and sets **every** listed subcategory (User, Computer,
Security Group and Distribution Group Management, Directory Service Changes, Logon, Kerberos Authentication Service,
Credential Validation, Audit Policy Change) to **Success and Failure** – the highest value, so the highest-precedence
GPO never lowers what DCs have by default (Credential Validation and Kerberos Authentication Service default to
Success on servers) or what other tools require (MDI: Credential Validation S+F); the WEF query forwards only failed
4771/4776, so Success adds no WEF traffic; the pre-deployment state is saved per DC with `auditpol /backup` first;
(5) Security log size from the pilot measurement (≥ 1 GB; it must cover the longest tolerated collector outage – the
forwarder resends from its bookmark); the audit GPO sets `MaxSize` only when a DC is below 1 GB and never below the
largest current value (an Order-1 GPO would otherwise shrink a larger size set elsewhere).
Verification: `Microsoft-Windows-Forwarding/Operational` on the DCs (event 105 = problem),
`sudo -u openwec openwec subscriptions machines admng-dc-security` and `openwec stats` on Debian, and Status in AD
Manager.

---

## 8. GPO content tracking

### 8.1 Triggers (`Core/Gpo/GpoContentJob.cs`)

- A `GroupPolicyChanged` event whose changes include `versionNumber` or `gPCFileSysPath`, or a GPO creation/restore →
  enqueue the GPO (table `gpo_content_queue`, idempotent by GUID).
- Baseline: on the first run (or when `gpo_content` is empty) every GPO is read silently (no events), throttled
  (one GPO at a time, at most 20 per cycle).
- Daily reconciliation (with the existing reconciliation): a cheap recursive listing (names, sizes, SMB modification
  times) of every GPO folder is compared with the stored inventory; a difference without a `versionNumber` change
  triggers a full read and, if settings differ, `GroupPolicyContentOutOfBand`.

### 8.2 Path validation (`GpoPathValidator`)

`gPCFileSysPath` MUST equal (case-insensitive) `\\<domain DNS>\SysVol\<domain DNS>\Policies\{<GPO GUID>}` with the GUID
of the GPC's `cn`. Anything else (another host, share or folder – the "GPO path hijack") → **`GroupPolicyPathRedirected`
(Critical)**, and the content is **not** read from that path. The service always reads
`\\<pinned DC FQDN>\SYSVOL\<domain DNS>\Policies\{GUID}` – never a host taken from AD data.

### 8.3 SYSVOL reading (`Service/Gpo/SmbClientSysvolReader.cs` implementing new `Core/Abstractions/ISysvolReader.cs`)

- Process `/usr/bin/smbclient` (package `smbclient`, `Depends`) started with `ProcessStartInfo.ArgumentList` (no shell);
  arguments: `//DC01.contoso.com/SYSVOL -s /etc/admng-watchdog/smb-client.conf --use-kerberos=required -N -E -m SMB3`
  plus `-D 'contoso.com/Policies/{GUID}'` and the command. Paths are built only from a validated `Guid` ("B" format) and
  the domain DNS name (`[a-z0-9.-]`). `-N` is mandatory (without it smbclient prompts on the terminal and hangs – lab),
  the exit code is checked; `KRB5CCNAME` comes from the unit.
- **TGT before every invocation:** smbclient never obtains a TGT from the client keytab – it only uses a valid TGT in
  `KRB5CCNAME` (lab: empty ccache → `Could not find a suitable mechtype` / `NT_STATUS_INVALID_PARAMETER`; expired TGT →
  `NT_STATUS_LOGON_FAILURE`). Before every smbclient run `SmbClientSysvolReader` ensures a TGT with at least 15 minutes
  left: `krb5_get_init_creds_keytab` for `ADMNG-WD$` with the client keytab, then `krb5_cc_initialize` /
  `krb5_cc_store_cred` into a temporary ccache in `/run/admng-watchdog` and an atomic rename over `krb5cc`. B5 test
  with a 1-minute TGT.
- **Listing** (`-c 'recurse ON; ls'`) runs **without** `-E` and is parsed from stdout (with `-E` smbclient writes the
  `ls` listing to stderr – lab); a line starting with `NT_STATUS_` or a non-zero exit code is an error.
  **Content reads** (`-Tc - <file …>`: tar of the selected files to stdout, read with `System.Formats.Tar.TarReader`
  from the pipe – to be confirmed in B5; fallback one `get "<path>" -` per file) keep `-E`, so stdout carries only data
  (otherwise diagnostics land in the streamed data – lab). Parser fixtures are captured from Debian 12 (Samba 4.17)
  and 13 (4.22). Everything stays in memory; nothing is written to disk.
- `smb-client.conf` (conffile, 0644):

  ```ini
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
  ```

  The Samba state directory (gencache may hold name/SID mappings) is on tmpfs inside the runtime directory and is
  emptied after every smbclient run.
- stderr of smbclient (content reads) and `NT_STATUS_*` lines of the listing are parsed only for `NT_STATUS_*` codes
  (access denied → `HealthPermissionGap` code `sysvol.accessDenied`; logon failure / invalid parameter → Kerberos hint)
  and never logged verbatim.
- Limits (bootstrap `Watchdog:Sysvol` and runtime `GpoContent`): per GPO ≤ 2 000 files, depth ≤ 12, content files
  ≤ 5 MiB each and ≤ 50 MiB in total, ≤ 10 000 settings; one smbclient invocation ≤ 60 s; one GPO read at a time.
  Exceeding a limit stores the inventory and a health Warning `gpo.<guid>.tooLarge`; the event lists what was read.

### 8.4 Files and parsers (Core, pure, fixture-tested)

| File (under the GPO folder) | Parser | Encoding |
|---|---|---|
| `GPT.INI` | `GptIniParser` (version: machine = low 16 bits, user = high 16 bits) | ANSI/UTF-8 |
| `Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf` | `GptTmplParser` (sections `[System Access]`, `[Kerberos Policy]`, `[Event Audit]`, `[Privilege Rights]`, `[Group Membership]`, `[Registry Values]`, `[Service General Setting]`, `[Registry Keys]`, `[File Security]`) | UTF-16LE with BOM |
| `Machine/Microsoft/Windows NT/Audit/audit.csv` | `AuditCsvParser` (subcategory GUID → setting) | UTF-8 (BOM optional) |
| `Machine/Registry.pol`, `User/Registry.pol` | `RegistryPolParser` (`PReg` v1: `[key;value;type;size;data]` UTF-16LE; `**del.`, `**delvals.`, `**DeleteValues`, `**DeleteKeys` markers) | binary |
| `Machine/Preferences/**/*.xml`, `User/Preferences/**/*.xml` (Groups, Services, ScheduledTasks, Registry, Files, Folders, Drives, Shortcuts, DataSources, Printers, EnvironmentVariables, IniFiles, NetworkShares, PowerOptions, …) | `GppXmlParser` (items identified by `uid`, display by `name`; `Properties` attributes, child elements, `Filters` canonicalised; `disabled`, `removePolicy`) | UTF-8 (BOM) |
| `Machine/Scripts/scripts.ini`, `psscripts.ini`, `User/Scripts/…` | `ScriptsIniParser` | UTF-16LE |
| `Machine/Scripts/**`, `User/Scripts/**` other files ≤ 1 MiB | content **hash only** (SHA-256 + size) | – |
| `Machine/comment.cmtx`, `User/comment.cmtx`, `GPO.cmt` | `PolicyCommentParser` (≤ 200 characters per comment) | XML / UTF-16 |
| everything else | inventory only (path, size, SMB modification time) | – |

`XmlReader` with `DtdProcessing.Prohibit`, `XmlResolver = null`, character checking off (U+FFFD for invalid
characters); every parser bounds its input and fails closed per file (`gpo.<guid>.parseError`, the file is shown as
"nie udało się odczytać").

### 8.5 Settings model

`GpoContentSnapshot { GpoGuid, Dc, GpcVersion, GptVersion, ReadAt, Inventory[], Settings[] }`;
`GpoSetting { Key, Value, Kind (Text|Hash|Masked), Category }`. Keys are stable, English and scope-prefixed, e.g.
`Computer/Security/System Access/MinimumPasswordLength`,
`Computer/Security/Privilege Rights/SeDebugPrivilege` (value = sorted SID list; names resolved from the snapshot only
for display), `Computer/Registry/HKLM\Software\Policies\Microsoft\Windows\WindowsUpdate\AU!NoAutoUpdate`
(value `REG_DWORD:1`), `Computer/Preferences/Groups/{uid}/Members` (display "Administrators (built-in)"),
`Computer/Preferences/ScheduledTasks/{uid}/Command`, `Computer/Scripts/Startup/0`, `Computer/Audit/{subcategory GUID}`,
`User/Registry/…`, `Gpo/Comment`. Values are canonical strings capped at 200 characters (longer → `sha256:<hex>`,
`Kind = Hash`). **Masked** (never stored, not even hashed – only "present"): GPP `cpassword` (MS14-025; presence sets
tag `gppPassword`), registry values whose name contains `Password`, `Pwd` or `Secret` (case-insensitive), and the list
`GpoSecretSettings` (e.g. `Winlogon!DefaultPassword`). `GPT.INI` version fields are not settings.

### 8.6 Diff and events

`GpoSettingsDiffer` produces `AttributeChange` rows (`Attribute` = setting key, `OldValues`/`NewValues` = one canonical
value each, `ValueKind` Text or Hash, `Redacted` for masked) – the GUI, e-mail and Teams show them as
"ustawienie: było → jest"; added/removed settings have one empty side; at most 200 changes per event (`Truncated`,
counts). New `WatchEventKind`s (Contracts, stable numbers):

| Kind | Number | Severity | Notes |
|---|---|---|---|
| `GroupPolicySettingsChanged` | 510 | Critical: GPO linked to the domain head or the Domain Controllers OU **and** a sensitive category changed, or tag `gppPassword`; High: sensitive category elsewhere; Medium: linked GPO; Low: unlinked GPO | object = GPO; `ChangeSetId` = the GroupPolicyChanged event's |
| `GroupPolicyPathRedirected` | 511 | Critical | 8.2 |
| `GroupPolicyContentOutOfBand` | 512 | High (Critical when linked to the domain head / DC OU) | content changed without `versionNumber` (direct SYSVOL edit) |

Sensitive categories (`GpoSettingCategories`): Privilege Rights; Group Membership / Restricted Groups; GPP Groups and
Local Users; scripts and script files; GPP Scheduled Tasks (incl. immediate tasks); services (SecEdit and GPP); audit
policy (`[Event Audit]`, `audit.csv`); Kerberos and account policy; registry under `…\Control\Lsa`, `…\Winlogon`,
`…\CurrentVersion\Run*`, `…\Policies\System`, `…\SecurityProviders\WDigest`, `…\Services\Netlogon\Parameters`,
`…\Services\LanmanServer\Parameters`, `…\Services\LanmanWorkstation\Parameters`, `…\Windows Defender\Exclusions`,
`…\WindowsFirewall`; File and Registry security (SDDL); GPP Files/Folders/Shortcuts targeting `%SystemRoot%` or
startup folders. Linked scope comes from the gPLink snapshot (enforced or not).

Built-in rule `tier0-gpo` (High+, immediate to it-admins, Always) for the three kinds; `ou-gpo` digest gets them too.

### 8.7 Attribution

- `GroupPolicySettingsChanged` inherits the attribution of the `GroupPolicyChanged` event of the same `versionNumber`
  transition (`GpoAttributionInheritor`, same mechanics as `PrivilegeAttributionInheritor`): GPMC writes the files over
  SMB and then bumps `versionNumber` over LDAP as the editing user → 5136 on the GPC (needs the GPC SACL of
  `docs/WATCHDOG.md` 4.6). Follow-ups and re-evaluation as in spec 11.4/11.5.
- `GroupPolicyContentOutOfBand`: no LDAP change, SYSVOL access is not audited → `Unknown` with the new reason
  `SYSVOL_NOT_AUDITED` (remedy pl: „Zmiana plików SYSVOL bez zmiany wersji GPO – wykonawca nieznany; sprawdź, kto ma
  zapis do SYSVOL.”).
- SYSVOL lag: when `GPT.INI` version < GPC `versionNumber` on the pinned DC, the read is retried every 2 min up to
  `GpoContent.SysvolLagTolerance` (2 h), then done anyway with health Warning `gpo.<guid>.sysvolLag`.

### 8.8 Storage (ADR-0008 conditions as amended by ADR-0009)

Migration **0005_gpo_content**: `gpo_content(gpo_guid BLOB PRIMARY KEY, dc TEXT NOT NULL, gpc_version INTEGER, gpt_version
INTEGER, read_at INTEGER NOT NULL, state INTEGER NOT NULL, inventory BLOB NOT NULL, settings BLOB NOT NULL, deleted_at
INTEGER)` (deflated JSON through `WatchdogStorageJsonContext`) and `gpo_content_queue(gpo_guid BLOB PRIMARY KEY,
reason INTEGER, due_at INTEGER, attempts INTEGER)`. Only the latest snapshot per GPO is kept; a deleted GPO's snapshot
is kept `DeletedSnapshotDays` (restore diff), then purged. Events with their changes follow `EventsDays`.

---

## 9. New and changed rules

### 9.1 BadSuccessor (dMSA, Windows Server 2025 schema)

- Catalog changes (spec 7): `ServiceAccountCreated` for `msDS-DelegatedManagedServiceAccount` → **Critical** (was High),
  new tag `badSuccessor`; `ManagedAccountLinkChanged` (changes of `msDS-ManagedAccountPrecededByLink`,
  `msDS-DelegatedMSAState` on a dMSA, `msDS-SupersededManagedAccountLink`, `msDS-SupersededServiceAccountState` on a
  user/computer) → **Critical always** (was High / Critical for privileged), tag `badSuccessor`; a link pointing to a
  privileged principal adds `privileged` (unchanged).
- New built-in rule `tier0-badsuccessor`: kinds `ServiceAccountCreated` + `ManagedAccountLinkChanged`, `RequiredTags
  [badSuccessor]`, MinSeverity Critical, immediate to it-admins and security, `Always` (quiet hours and aggregation
  ignored), notifies during rollout mode, not suppressible without `AllowTier0Suppression`.
- Detection is LDAP-based (works without SACLs) and covers dMSAs anywhere in the domain NC. Possible superiors are
  inherited along the superclass chain (`msDS-DelegatedManagedServiceAccount` → `computer` → `user` →
  `organizationalPerson` → `person` → `top`), so the effective parents of a dMSA are `domainDNS`, `organizationalUnit`,
  `container`, `builtinDomain` (from `user`), `organization` (from `organizationalPerson`) and `lostAndFound` (from
  `top`) – not `computer` (Q10 closed). The instructions' audit script (2.9, appendix A.4) computes this set from the
  schema of each forest and audits every object of those classes. Attribution: the **creation** (5137) is audited on
  the parent's SACL, so it needs the domain-head entries "CreateChild/DeleteChild for objects of class X, this object
  and all descendants" for every watched class incl. dMSA (instructions 2.11 – they cover OUs, containers such as
  `CN=Managed Service Accounts`, the default target of `New-ADServiceAccount` without `-Path`, and the domain head);
  later writes to the dMSA (5136, e.g. the migration links) need the descendant dMSA-class entry (`docs/WATCHDOG.md`
  4.6). The instructions add the Tier 0 OU / BadSuccessor delegation audit (appendix A.4) and the requirement that
  DC01 has the August 2025 or later cumulative update (CVE-2025-53779).
- SHOULD (B6, if time allows): weekly digest section "Kto może tworzyć dMSA" – the objects of the effective dMSA parent
  classes above whose DACL grants `CreateChild` (all or the dMSA class), `GenericAll` or `WriteDacl`/`WriteOwner` to a
  principal outside Tier 0 (SDs read with SD flags 7, readable by the computer account – lab).

### 9.2 ms-DS-MachineAccountQuota

`DomainPolicyChanged` with a change of `ms-DS-MachineAccountQuota` gets tag `machineAccountQuota` and severity
**Critical** when the new value > 0 and the old value was 0 (a hardening reverted), **High** otherwise. New built-in
rule `tier0-machine-quota` (MinSeverity High, immediate, Always). The quota 0 is a deployment prerequisite (confirmed
by the user on 2026-10-20; appendix A.5: whoever joins computers today first gets a delegated "Create Computer objects"
right): any authenticated principal – also `ADMNG-WD$` with a stolen keytab – can otherwise create computer accounts.
Health Warning `domain.machineAccountQuota` while the value is > 0 (read with the domain head each detection cycle).

### 9.3 The service's own account (`watchdog-self`)

`EventTagger` adds tag `watchdogAccount` to every event whose object (or, for membership/privilege events, principal) is
`ADMNG-WD$`. New built-in rule `watchdog-self`: `RequiredTags [watchdogAccount]`, MinSeverity Info, severity override
**Critical**, immediate to it-admins and security, Always; excluded: a `pwdLastSet` / password change of `ADMNG-WD$`
whose new `msDS-KeyVersionNumber` equals `status.json.kvno` and whose `pwdLastSet` lies within ±10 minutes of
`status.json.lastChange` (result `ok` / `replicationPending`) – regardless of the attributed actor, because who the DC
records for a kpasswd change of a computer account is unverified (Q21); any other password change stays Critical
(an admin reset in the one-time password path is expected to alert). Covers
`msDS-SupportedEncryptionTypes`, `servicePrincipalName`, `dNSHostName`, `userAccountControl`,
`msDS-AllowedToActOnBehalfOfOtherIdentity` (RBCD), `msDS-KeyCredentialLink` (shadow credentials), `nTSecurityDescriptor`
(owner and DACL – e.g. removal of the Deny ACE for SELF, also checked hourly by 4.7 item 7), group membership /
PrivilegeGranted, move/rename/disable/delete. All of these are already watched on computers (spec 9.2), but
`dNSHostName`, `servicePrincipalName`, `msDS-SupportedEncryptionTypes`, `operatingSystem*`, `userCertificate` and
`pwdLastSet` are **computer noise** (spec 9.3) and would be dropped: the service's own account MUST be exempt from the
computer-noise filter and from `SuppressComputerSelfChanges` (`NoiseAttributes` gets the own objectGUID as an
exception), so a self-write of `ADMNG-WD$` (e.g. RC4 re-enabled, an extra SPN) is always reported.

### 9.4 Changes of the audit configuration (SACL) of key objects

All attribution of the Critical rules (BadSuccessor creations, GPO, PKI, AdminSDHolder) rests on the SACL entries of
instructions 2.11. Every holder of "Manage auditing and security log" (`SeSecurityPrivilege`) on the DCs –
Administrators and, in every Exchange organisation, the Exchange Servers group (granted by Exchange setup in the
Default Domain Controllers Policy) – can remove them silently; the service cannot read a SACL, and a targeted removal
(e.g. only the dMSA/GPC/trustedDomain create entries) trips neither the coverage health (spec 7, < 50 % over 24 h)
nor `SACL_MISSING` (7 days without any 5136 from a DC). Therefore (MUST, B6; **confirmed by the user on 2026-10-20**,
ADR-0009 „Ustalenia potwierdzone 2026-10-20 – druga tura”, item 7), every detection cycle the service reads
`replPropertyMetaData` of the domain head, `CN=AdminSDHolder,CN=System,<domain>`, `OU=Domain Controllers,<domain>`,
`CN=Public Key Services,CN=Services,<configuration NC>` and `CN=Sites,<configuration NC>` (readable by the computer
account – appendix C) and keeps the version of the `nTSecurityDescriptor` entry (attid `0x20119`). A version increase
while the owner and the ACE summary read with SD flags 7 (spec 9.4) are unchanged is reported as the existing kind
**`SecurityDescriptorChanged`** (Critical, rule `tier0-security` – no new rule, not suppressible without
`AllowTier0Suppression`) with the new tag `auditSettings` and one change row „lista inspekcji (SACL) albo inne ustawienie
zabezpieczeń zmienione – treści konto usługi nie odczyta” (originating DC and time from the metadata; actor through the
5136 of the object if one arrives). It also triggers an immediate attribution-coverage re-check. A rewrite of an
unchanged SD may bump the version as well and is reported the same way. Pilot item (20.6): add and remove a test
audit entry on the domain head → one Critical event each.

### 9.5 Contracts summary

New `WatchEventKind` 510–512 (section 8.6), `HealthServiceAccount = 909` (rotation failing, password age, keytab stale,
keytab entry order, privileged, drift incl. a missing Deny ACE for SELF – no certificate-template reasons, 4.7), new
tags `badSuccessor`, `machineAccountQuota`, `watchdogAccount`, `gppPassword`, `auditSettings` (9.4), new reason
`SYSVOL_NOT_AUDITED`, validator code `retentionBelowFloor` and health code `storage.backupSkipped` (14.2),
`AttributionSourceKind.OpenWec = 3`, `SecurityEventCriteria.LogonId`, `RetentionSettings.LogonRecordsHours`,
`RetentionSettings.EventsDays` default **1095**, `AttributionSettings` collector fields (7.8), `GpoContentSettings` (`Enabled` true, `SysvolLagTolerance` 2 h,
`MaxFileMb` 5, `MaxTotalMb` 50, `MaxFiles` 2000) in `DirectorySettings`, `StatusDto` sections (service account,
collector per DC, GPO content, host). All localization keys en + pl.

**Contract step (2026-10-20, before the tracks split).** The items above, the bootstrap records of 14.1 and the
localization keys were added in one commit; the code is the reference (`Contracts/Api/HealthCheckCodes.cs`,
`StatusDto` sections `ServiceAccount`/`Collector`/`GpoContent`/`Host`, `KeytabStatusResults`, `HostCredentialKeys`,
`Service/Hosting/*BootstrapOptions.cs`). Names this document did not fix, and the deviations, with their reasons:

- Health codes with a subject are `wec.<dc>.<check>` / `gpo.<guid>.<check>` (`HealthCheckCodes.Wec`/`Gpo`); the GUI
  resolves them by first and last segment, so `wec.collectorFileLost` (7.9) and the per-DC form of 7.5 share one text.
  New family codes `serviceAccount`, `host`, `wec`, `gpo`, `sysvol`, `domain` carry the fallback text of unknown checks.
  The self-check code of 6.2 is `permission.tokenGroupsUnreadable` (all `PermissionSelfCheck` codes carry the
  `permission.` prefix). The start-up warnings of 11 items 3 and 6 are one code, `host.hardening` (Args `umask`,
  `coreDumps`, `credentialsReadable`); `host.credentialKey` is an Ok check carrying the variant of 10 in its Args.
- The collector-mode remedy of `DC_UNREACHABLE` (7.8) is a localization **variant** (`Reason.DC_UNREACHABLE.Collector`
  and `.Remedy`, `AttributionReasons.CollectorVariant`) instead of a changed text: the stored reason stays, and
  AD Manager 0.3.0 still shows the right text for a 0.2.0 Windows service.
- Catalog defaults: `GroupPolicySettingsChanged` starts at **Low** (the unlinked case of 8.6 – the GPO classifier only
  raises), `GroupPolicyContentOutOfBand` High, `GroupPolicyPathRedirected` Critical, `HealthServiceAccount` **High**
  (not stated above; like the other failing-health kinds). The severity changes of 9.1 stay with B6. The three GPO kinds
  are Domain kinds, hence Tier-0 kinds (12.6), and are added to the `ou-gpo` digest rule now (8.6: "`ou-gpo` digest gets
  them too") so that every recorded kind reaches a digest; `tier0-gpo` and the other new rules are B5/B6 (only their
  ids exist in `DefaultRules`).
- Record defaults are the Linux values **unconditionally** (`Attribution.Source` OpenWec, `EventsDays` 1095,
  `MaxDatabaseMb` 4096 – 14.2 says "on Linux"): host-dependent record defaults would make Windows and Linux CI disagree,
  and no Windows service is shipped from this branch (17). On the Windows service OpenWec behaves like
  DomainControllers until its removal. `WatchdogConfiguration.CurrentSchemaVersion` is already 2; the v1 → v2 migration
  is B4's.
- The fixed limits of 8.3 that are not runtime settings (depth 12, 10 000 settings) are constants
  `GpoContentSettings.MaxDepth` / `MaxSettings`.
- `ConfigValidationContext` gets `MinEventsDays` (0 = no floor) and `SupportedAttributionSources` (empty = every
  source; the Linux service passes OpenWec and None), both filled by `ConfigValidationService`; the checks themselves
  are B4/B6.
- Bootstrap `Watchdog:Collector:SubscriptionUuid` defaults to the shipped uuid of 7.3, so a file without the key still
  matches the loaded subscription. `Kerberos`, `Collector`, `Retention`, `Sysvol` and `AllowPrivilegedServiceAccount`
  apply at restart (`ServiceOptionsFactory.Pin`); `Authorization.RoleCache*` apply live like the rest of
  `Authorization`.
- 6.2's 503 has the problem type `https://admng/problems/role-lookup-unavailable` (`ApiProblem.RoleLookupUnavailable`);
  exit codes 8–10 are `StartupExitCodes.PrivilegedServiceAccount`/`MissingCredential`/`Keytab`; the event ids of 15 are
  `WatchdogEventIds.CertificateFingerprint` (1008), `ServiceAccountProblem` (1011), `CollectorProblem` (1012),
  `GpoContentProblem` (1013); webhook types of 510–512 are `ad.gpo.settings_changed`, `ad.gpo.path_redirected`,
  `ad.gpo.content_out_of_band`.

---

## 10. Secrets on Linux

- `ISecretStore`, `SqliteSecretStore`, destination bindings and `DestinationGuard` are unchanged (spec 18).
- Data Protection: `PersistKeysToFileSystem(/var/lib/admng-watchdog/keys)` +
  `XmlKeyManagementOptions.XmlEncryptor = SystemdCredentialXmlEncryptor` (new, `Secrets/`): AES-256-GCM with a random
  96-bit nonce per key element; output `<encryptedKey xmlns="urn:admng:dp" alg="A256GCM" kid="<first 8 bytes of
  SHA-256(key), hex>">` with base64 nonce, ciphertext and tag; `SystemdCredentialXmlDecryptor` (`IXmlDecryptor`,
  DI-constructed) decrypts. The 32-byte key is read once from `$CREDENTIALS_DIRECTORY/dp-key` (systemd decrypts
  `LoadCredentialEncrypted=dp-key:/etc/admng-watchdog/credentials/dp-key.cred` into a non-swappable, service-private
  ramfs) into a pinned array.
- Creation (`postinst`, only when the file is missing): when the machine has a TPM (`/sys/class/tpmrm*` populated or
  the firmware reports one – `systemd-creds has-tpm2` shows `+firmware`/`+driver`)
  `head -c 32 /dev/urandom | systemd-creds encrypt --name=dp-key --with-key=host+tpm2 --tpm2-pcrs= - /etc/admng-watchdog/credentials/dp-key.cred`;
  if that fails, or without a TPM, the same with `--with-key=host` (host key `/var/lib/systemd/credential.secret`),
  and `postinst` prints the variant **actually used** and, for a fallback, the reason (e.g. "TPM2 sealing failed –
  tpm2-tss libraries missing?"). It does **not** use `--with-key=auto`: on systemd 252 (Debian 12) `has-tpm2` has no
  libraries indicator and answers `yes` on a vTPM even when libtss2 is missing, and `auto` then falls back to the host
  key with only a notice-level log line (`creds-util.c`: "TPM2 sealing didn't work, continuing without TPM2") – the
  reported variant would be wrong. (systemd 257 on Debian 13 shows `±libraries`.) The plaintext never touches the
  disk. `--tpm2-pcrs=` (empty) binds to no PCR: systemd 252 otherwise seals to PCR 7, and a UEFI db/dbx update, a
  Secure Boot toggle or a hypervisor NVRAM change would make the credential undecryptable; systemd 257 already defaults
  to no PCR. The credential is bound to the TPM and the host key, not to the boot state. A usable TPM2 needs the
  tpm2-tss runtime libraries (`Recommends`, 13.1; the instructions install them whenever a TPM is present, 3.1). The
  Status tab shows the variant (`host.credentialKey` = `tpm2+host` | `host`).
- Production without `CREDENTIALS_DIRECTORY/dp-key` → refuse to start (exit code 9) – this happens only when the process
  runs without the credential (started outside the unit, or the unit was changed). Development/tests:
  `UseEphemeralDataProtectionProvider()` or an explicit test key file.
- **Restore onto another host / new VM**: the credential cannot be decrypted → systemd refuses to start the unit before
  the program runs (`status=243/CREDENTIALS`, not exit 9 – the instructions' exit-code table lists both). The
  documented recovery `sudo admng-watchdog reset-secrets-key` (root) moves `keys/` to `keys.unreadable-<timestamp>/`
  (inside the data folder, deleted after 30 days by retention), creates a new credential and starts the service; every
  stored secret is `secret.unreadable` (health Error) and the admin re-enters the SMTP password / Teams URL in AD
  Manager (spec 18 DR path). A VM-level restore of the same VM (disk + vTPM state) keeps everything (Q9). No key export
  is offered.
- Keytab: a credential file outside `ISecretStore` (keys, never the password) – amendment of `CLAUDE.md` rule 2 by
  ADR-0009.

---

## 11. File system layout, ownership and startup permission checks

| Path | Owner:group | Mode | Written by |
|---|---|---|---|
| `/usr/lib/admng-watchdog/` | root:root | 0755 (files 0644, executables 0755) | package |
| `/etc/admng-watchdog/` | root:root | 0755 | package |
| `/etc/admng-watchdog/appsettings.json` | root:admng-watchdog | 0640 | admin (conffile) |
| `/etc/admng-watchdog/{krb5.conf,ldap.conf,smb-client.conf}` | root:root | 0644 | admin (conffiles) |
| `/etc/admng-watchdog/admng-wd.keytab` | root:admng-keytab | 0640 | `admng-watchdog-keytab` |
| `/etc/admng-watchdog/tls/` (+ `.crt` 0644, `.key` 0640) | root:admng-watchdog | 0750 | postinst / admin |
| `/etc/admng-watchdog/credentials/dp-key.cred` | root:root | 0700 dir / 0600 file | postinst |
| `/var/lib/admng-watchdog/` (+ `watchdog.db*`, `keys/`, `backup/`) | admng-watchdog:admng-watchdog | 0700 dirs / 0600 files | service |
| `/var/lib/admng-watchdog-keytab/` (`status.json`, `lock`) | root:admng-watchdog | 0750 dir / 0640 files | keytab tool |
| `/run/admng-watchdog/` (`krb5cc`, `samba/`, `wec-progress.json`) | admng-watchdog | 0700 | service (RuntimeDirectory) |
| `/var/lib/openwec/` (`db/` 0700) | openwec:openwec | 0751 | admng-openwec |
| `/var/lib/openwec/out/` | openwec:admng-watchdog | 2750 (subdirs 2750, files 0640) | openwecd |
| `/etc/systemd/system/openwecd.service.d/admng-dcs.conf` (`IPAddressAllow=` DC addresses) | root:root | 0644 | admin (12.2) |

Startup checks (`Hosting/PosixPermissionCheck.cs` replacing `DataDirectoryAcl` on Linux; exit codes as spec 19.1 plus
8, 9, 10; systemd itself reports `243/CREDENTIALS` when `LoadCredentialEncrypted=` fails – section 10):

1. Data folder and `watchdog.db`, `-wal`, `-shm`, `keys/`, `backup/`: owned by the service user, not symlinks, no
   group/other permission bits, no POSIX ACL entries (`system.posix_acl_access` xattr absent – `lgetxattr`
   `LibraryImport`), on a local file system (`DriveInfo.DriveFormat` not in nfs/nfs4/cifs/smb3/fuse.*/9p) → else
   journald error 1003 and exit 5 unless `AllowInsecureDataDirectoryAcl` (health Warning `storage.acl`).
2. Bootstrap file and its directory: owned by root, **not writable by the service user** (`access(W_OK)` fails for the
   file and the directory – no rename attack) → else exit 6 (`BootstrapFileGuard` on Linux).
3. Keytab: owner root, no write for group/other, readable by the service; `/etc/admng-watchdog/credentials` not readable
   by the service (it uses the systemd copy) → else exit 10 (keytab) / warning.
4. TLS key file: not world-readable → exit 4.
5. `ldap.conf` consistency (5.1/5.2) → exit 2.
6. Process umask is 0077 (unit), core dumps disabled (`RLIMIT_CORE` = 0) → Warning otherwise.

---

## 12. systemd units

### 12.1 `admng-watchdog.service`

```ini
[Unit]
Description=AdMng Watchdog - Active Directory change monitor
Documentation=file:/usr/share/doc/admng-watchdog/README.Debian
After=network-online.target nss-lookup.target time-sync.target openwecd.service
Wants=network-online.target time-sync.target
ConditionPathExists=/etc/admng-watchdog/admng-wd.keytab
StartLimitIntervalSec=0
OnFailure=admng-watchdog-failed.service

[Service]
Type=notify
NotifyAccess=main
User=admng-watchdog
Group=admng-watchdog
SupplementaryGroups=admng-keytab
ExecStart=/usr/lib/admng-watchdog/AdMng.Watchdog --contentRoot /usr/lib/admng-watchdog
WorkingDirectory=/var/lib/admng-watchdog
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_EnableDiagnostics=0
Environment=DOTNET_DbgEnableMiniDump=0
Environment=DOTNET_gcServer=0
Environment=HOME=/var/lib/admng-watchdog
Environment=KRB5_CONFIG=/etc/admng-watchdog/krb5.conf
Environment=KRB5_KTNAME=FILE:/etc/admng-watchdog/admng-wd.keytab
Environment=KRB5_CLIENT_KTNAME=FILE:/etc/admng-watchdog/admng-wd.keytab
Environment=KRB5CCNAME=FILE:/run/admng-watchdog/krb5cc
Environment=KRB5RCACHEDIR=/var/tmp
Environment=LDAPCONF=/etc/admng-watchdog/ldap.conf
LoadCredentialEncrypted=dp-key:/etc/admng-watchdog/credentials/dp-key.cred
StateDirectory=admng-watchdog
StateDirectoryMode=0700
RuntimeDirectory=admng-watchdog
RuntimeDirectoryMode=0700
UMask=0077
Restart=on-failure
RestartSec=30s
RestartPreventExitStatus=2 3 4 5 6 7 8 9 10 243
TimeoutStartSec=15min
TimeoutStopSec=90s
WatchdogSec=120s
LimitCORE=0
LimitNOFILE=65536
MemorySwapMax=0

# Sandboxing
NoNewPrivileges=yes
CapabilityBoundingSet=
AmbientCapabilities=
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes
PrivateDevices=yes
PrivateIPC=yes
DevicePolicy=closed
ProtectHostname=yes
ProtectClock=yes
ProtectKernelTunables=yes
ProtectKernelModules=yes
ProtectKernelLogs=yes
ProtectControlGroups=yes
ProtectProc=invisible
ProcSubset=pid
RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6 AF_NETLINK
RestrictNamespaces=yes
RestrictRealtime=yes
RestrictSUIDSGID=yes
LockPersonality=yes
MemoryDenyWriteExecute=no
SystemCallArchitectures=native
SystemCallFilter=@system-service
SystemCallFilter=~@privileged @resources @mount @swap @reboot @raw-io @module @obsolete @cpu-emulation @debug
SystemCallFilter=sched_setaffinity
SystemCallErrorNumber=EPERM
KeyringMode=private
RemoveIPC=yes
SocketBindAllow=tcp:8443
SocketBindDeny=any
ReadOnlyPaths=/etc/admng-watchdog /var/lib/openwec/out /var/lib/admng-watchdog-keytab
InaccessiblePaths=-/var/lib/openwec/db

[Install]
WantedBy=multi-user.target
```

Notes (MUST be honoured by B1/B2 tests):
- `MemoryDenyWriteExecute=no` – the .NET JIT needs W+X pages. `PrivateUsers=` is not used: the owner checks of
  section 11 need real ids. `ProcSubset=pid` hides `/proc/meminfo`/`cpuinfo`; B1 MUST confirm the runtime and GC work
  (cgroup limits are read from `/sys/fs/cgroup`); drop it with a reason otherwise. `@resources` is denied – B1 MUST run
  the service tests under `systemd-run --property=…` with the same filter.
- `WatchdogSec=120s`: a `SystemdWatchdogNotifier` (`BackgroundService`) sends `WATCHDOG=1` through `ISystemdNotifier`
  every 30 s **only** while the job hosts report progress within their expected interval (a hung detection loop gets
  the service restarted).
- **Restarts never stop on their own** (MUST): with a start limit (the earlier `StartLimitIntervalSec=900`,
  `StartLimitBurst=5`) five crashes or watchdog timeouts within 15 minutes would leave the unit in `failed`
  (`start-limit-hit`) until a human runs `systemctl start` – and the only alerting component would be the dead
  process. `StartLimitIntervalSec=0` disables the limit, `RestartSec=30s` keeps a crash loop cheap, and input that
  re-triggers a crash at every start is skipped after 3 attempts (7.5 "Poison input"). Only the start-up exit codes
  that need a human (2 configuration, 3 newer database, 4 TLS key, 5 data folder, 6 bootstrap file writable,
  7 development auth, 8 privileged account, 9 missing credential, 10 keytab – 11, spec 19.1 – and systemd's
  `243/CREDENTIALS` when the credential cannot be decrypted – section 10) are excluded from restarts
  (`RestartPreventExitStatus=`); the unit then enters `failed`. (The `notify-failure` helper cannot read the SMTP
  password after a 243 either; it still logs at `crit`.)
- `OnFailure=admng-watchdog-failed.service` (SHOULD, B1): a oneshot that runs only when the unit enters `failed` (with
  `Restart=` and no start limit: a `RestartPreventExitStatus=` exit). It logs at priority `crit` (`systemd-cat -p
  crit`) and, best effort, runs `AdMng.Watchdog notify-failure` as `admng-watchdog` in the same sandbox and with the
  same `LoadCredentialEncrypted=`: it reads the channel configuration read-only and sends one fixed plain-text message
  without directory data („AdMng Watchdog zatrzymany – sprawdź `systemctl status admng-watchdog`”) through the health
  channels (e-mail to the health recipients, Teams if configured). Failure of this unit is only logged. `Type=notify`: `UseSystemd()` sends `READY=1` after the startup checks (spec 19.1) –
  migrations run before, hence `TimeoutStartSec=15min`.
- `AF_NETLINK` is needed by `NetworkInterface.GetAllNetworkInterfaces` (own addresses for `/healthz`).
- No `IPAddressDeny=`/`IPAddressAllow=` (unlike 12.2): Teams and the CRL/OCSP URLs of the SMTP relay certificate have
  no fixed addresses, so an allow-list would have to contain `any` (4.3, "The `admng-watchdog` process"). Egress is
  limited by the nftables output chain of 16 (DCs, SMTP relay, 80/443 only for `root`, `_apt` and `admng-watchdog`).
  No outbound proxy is used (user decision 2026-10-20, Q23 closed): the direct egress of `admng-watchdog` is the
  accepted residual risk of 4.3, so this unit keeps no `IPAddressDeny=`.
- `SocketBindAllow=`/`SocketBindDeny=` (here and in 12.2) are effective only when `systemctl --version` shows
  `+BPF_FRAMEWORK`; otherwise systemd skips them with a debug-level message only. B1 records this for Debian 12 and 13.
  nftables (16) and the Kestrel / openwec listen settings remain the binding controls, and the exposure-score target
  (20.4) must not rely on them.
- smbclient runs inside the same sandbox; its state goes to `/run/admng-watchdog/samba` (8.3).
- `ConditionPathExists=` keeps the unit inactive (not failed) until `admng-watchdog-keytab init` has run.
- `/etc/admng-watchdog/credentials` is protected by its mode (root 0700), not by `InaccessiblePaths=`: systemd reads
  `LoadCredentialEncrypted=` while setting up the service, and B1 must not depend on the ordering of credential and
  mount-namespace setup.

### 12.2 `admng-openwec`'s `openwecd.service` (hardened replacement of the upstream unit)

```ini
[Unit]
Description=OpenWEC Windows Event Collector (AdMng)
After=network-online.target
Wants=network-online.target
ConditionPathExists=/etc/admng-watchdog/admng-wd.keytab

[Service]
Type=simple
User=openwec
Group=openwec
SupplementaryGroups=admng-keytab
ExecStart=/usr/bin/openwecd -c /etc/openwec.conf.toml
ExecReload=/bin/kill -HUP $MAINPID
Environment=KRB5_CONFIG=/etc/admng-watchdog/krb5.conf
Environment=KRB5RCACHEDIR=/var/tmp
UMask=0027
Restart=always
RestartSec=5s
LimitNOFILE=65536
LimitCORE=0
NoNewPrivileges=yes
CapabilityBoundingSet=
ProtectSystem=strict
ReadWritePaths=/var/lib/openwec
ProtectHome=yes
PrivateTmp=yes
PrivateDevices=yes
ProtectHostname=yes
ProtectClock=yes
ProtectKernelTunables=yes
ProtectKernelModules=yes
ProtectKernelLogs=yes
ProtectControlGroups=yes
ProtectProc=invisible
RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6
RestrictNamespaces=yes
RestrictRealtime=yes
RestrictSUIDSGID=yes
LockPersonality=yes
MemoryDenyWriteExecute=yes
SystemCallArchitectures=native
SystemCallFilter=@system-service
SystemCallFilter=~@privileged @resources
SocketBindAllow=tcp:5985
SocketBindDeny=any
IPAddressDeny=any
MemorySwapMax=0

[Install]
WantedBy=multi-user.target
```

`IPAddressDeny=any` (MUST, 4.3 item 3) filters ingress (source address) and egress (destination address) of every
socket of the unit's processes (cgroup BPF; unlike `SocketBind*` it does not depend on `+BPF_FRAMEWORK` – B1 still
records that it is effective on Debian 12 and 13, because systemd ignores it silently where cgroup BPF is missing).
The DC addresses are site data, so the package cannot ship them: the admin writes the drop-in
`/etc/systemd/system/openwecd.service.d/admng-dcs.conf` with `[Service]` / `IPAddressAllow=10.0.1.10 10.0.1.11` – the
same addresses as the nftables `DCS` define plus `Watchdog:Collector:AdditionalSourceAddresses` (instructions 4.3).
Without it the unit is fail-closed: no DC can connect (Forwarding/Operational 105, `wec.<dc>.notForwarding`). The
DC addresses stay reachable for `openwecd` as far as systemd is concerned (KDC, LDAP, DNS on the DCs); the nftables
rule `meta skuid "openwec" drop` (16) blocks those new outbound connections too. `MemorySwapMax=0`: `openwecd` holds
the long-term `HTTP/` key in memory, so it gets the same second layer against swap as 12.1 and 12.3 (16).

`StateDirectory=` is deliberately **not** used: systemd would re-own `/var/lib/openwec` recursively when the top-level
owner differs and could reset the `admng-watchdog` group of `out/`. Directories come from tmpfiles (13.3).

### 12.3 `admng-watchdog-keytab.service` / `.timer`

```ini
[Unit]
Description=AdMng Watchdog - rotate the ADMNG-WD$ password and keytab (when older than 30 days)
After=network-online.target time-sync.target
Wants=network-online.target
ConditionPathExists=/etc/admng-watchdog/admng-wd.keytab

[Service]
Type=oneshot
User=root
Group=admng-watchdog
ExecStart=/usr/lib/admng-watchdog/admng-watchdog-keytab rotate --if-older-than 30d
Environment=KRB5_CONFIG=/etc/admng-watchdog/krb5.conf
Environment=KRB5CCNAME=MEMORY:admng-keytab
Environment=DOTNET_EnableDiagnostics=0
StateDirectory=admng-watchdog-keytab
StateDirectoryMode=0750
UMask=0027
LimitCORE=0
NoNewPrivileges=yes
CapabilityBoundingSet=CAP_CHOWN CAP_FOWNER
ProtectSystem=strict
ReadWritePaths=/etc/admng-watchdog
InaccessiblePaths=/var/lib/admng-watchdog /var/lib/openwec
ProtectHome=yes
PrivateTmp=yes
PrivateDevices=yes
ProtectKernelTunables=yes
ProtectKernelModules=yes
ProtectKernelLogs=yes
ProtectControlGroups=yes
RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6
RestrictNamespaces=yes
RestrictSUIDSGID=yes
LockPersonality=yes
SystemCallArchitectures=native
SystemCallFilter=@system-service
MemorySwapMax=0
```

```ini
[Unit]
Description=Daily check of the ADMNG-WD$ password age

[Timer]
OnCalendar=*-*-* 04:20:00
RandomizedDelaySec=30min
Persistent=true

[Install]
WantedBy=timers.target
```

### 12.4 `admng-wec-rotate.service` / `.timer`

Written out in full – **not** "as 12.3": root without `CAP_DAC_OVERRIDE`/`CAP_DAC_READ_SEARCH` cannot rename or
unlink in the `openwec`-owned 2750 directories nor read `/run/admng-watchdog` (0700, lab with
`setpriv --bounding-set`), a missing `/run/admng-watchdog` (Watchdog stopped) must not fail the namespace setup (`-`
prefix), and 12.3's `InaccessiblePaths=/var/lib/openwec` would hide `out/`.

```ini
[Unit]
Description=AdMng Watchdog - rotate and clean up OpenWEC output files
After=openwecd.service

[Service]
Type=oneshot
User=root
Group=root
ExecStart=/usr/lib/admng-watchdog/wec-rotate
ExecStartPost=+-/usr/bin/systemctl reload openwecd.service
EnvironmentFile=-/etc/default/admng-wec-rotate
UMask=0027
LimitCORE=0
NoNewPrivileges=yes
CapabilityBoundingSet=CAP_DAC_OVERRIDE CAP_DAC_READ_SEARCH
ProtectSystem=strict
ReadWritePaths=/var/lib/openwec/out
ReadOnlyPaths=-/run/admng-watchdog
InaccessiblePaths=-/etc/admng-watchdog -/var/lib/admng-watchdog -/var/lib/openwec/db
ProtectHome=yes
PrivateTmp=yes
PrivateDevices=yes
PrivateNetwork=yes
ProtectKernelTunables=yes
ProtectKernelModules=yes
ProtectKernelLogs=yes
ProtectControlGroups=yes
RestrictAddressFamilies=AF_UNIX
RestrictNamespaces=yes
RestrictSUIDSGID=yes
LockPersonality=yes
SystemCallArchitectures=native
SystemCallFilter=@system-service
```

`wec-rotate` itself (bash, `set -euo pipefail`) only renames and deletes (7.4); the reload of `openwecd` (SIGHUP:
reopen output files, reload subscriptions) runs in `ExecStartPost=` with the `+` prefix (outside the sandbox – a
capability-restricted root may be refused by systemd's D-Bus privilege check) and `-` (no failure when `openwecd` is
stopped). The script treats a missing progress file as "nothing confirmed" (7.4 step 5). Timer: `OnCalendar=hourly`,
`RandomizedDelaySec=5min`, `Persistent=true`. B4 acceptance test: run the unit under systemd in the Debian container
(or `systemd-run --wait -p …` with exactly these properties) and check rename, reload, deletion after consumption,
deletion by `MAX_UNCONSUMED_DAYS` and a start while `admng-watchdog` is stopped.

---

## 13. Debian packages

### 13.1 `admng-watchdog` (single amd64 package for Debian 12 and 13)

```
/usr/lib/admng-watchdog/                     framework-dependent publish (linux-x64) of AdMng.Watchdog + admng-watchdog-keytab, wec-rotate
/usr/sbin/admng-watchdog                     wrapper: runs the service binary as admng-watchdog for CLI verbs (--check-config, --health,
                                             cert-fingerprint, print-wec-subscription) and root-only verbs (reset-secrets-key, cert-renew-self-signed)
/usr/sbin/admng-watchdog-keytab              -> /usr/lib/admng-watchdog/admng-watchdog-keytab
/lib/systemd/system/admng-watchdog.service, admng-watchdog-failed.service, admng-watchdog-keytab.{service,timer}, admng-wec-rotate.{service,timer}
/usr/lib/sysusers.d/admng-watchdog.conf      u admng-watchdog - "AdMng Watchdog" /var/lib/admng-watchdog /usr/sbin/nologin
                                             g admng-keytab -
                                             m admng-watchdog admng-keytab
                                             m openwec admng-keytab
/usr/lib/tmpfiles.d/admng-watchdog.conf      d /var/lib/admng-watchdog 0700 admng-watchdog admng-watchdog -
                                             d /var/lib/admng-watchdog/backup 0700 admng-watchdog admng-watchdog -
                                             d /var/lib/admng-watchdog/keys 0700 admng-watchdog admng-watchdog -
                                             d /var/lib/admng-watchdog-keytab 0750 root admng-watchdog -
                                             d /var/lib/openwec/out 2750 openwec admng-watchdog -
                                             z /etc/admng-watchdog/admng-wd.keytab 0640 root admng-keytab -
/etc/admng-watchdog/appsettings.json, krb5.conf, ldap.conf, smb-client.conf      conffiles (placeholders = fixed names of section 2)
/etc/default/admng-wec-rotate                conffile (MAX_UNCONSUMED_DAYS=7, MAX_UNCONSUMED_MB=8192)
/usr/share/admng-watchdog/nftables/admng-watchdog.nft   example firewall (16)
/usr/share/doc/admng-watchdog/               copyright, changelog.Debian.gz, README.Debian (pointer to docs/WATCHDOG-LINUX.md)
```

Control: `Architecture: amd64`; `Depends: aspnetcore-runtime-10.0 (>= 10.0.13), admng-openwec (>= 0.3.0+git20260908),
libkrb5-3, libgssapi-krb5-2, libsasl2-modules-gssapi-mit, libldap-2.5-0 | libldap2, smbclient, openssl, systemd
(>= 252)`; `Recommends: chrony, krb5-user, nftables` and the tpm2-tss runtime libraries that systemd loads for TPM2
credentials (Debian 12: `libtss2-esys-3.0.2-0, libtss2-mu0, libtss2-rc0, libtss2-tcti-device0`; Debian 13: the `…t64`
variants `libtss2-esys-3.0.2-0t64, libtss2-mu-4.0.1-0t64, libtss2-rc0t64, libtss2-tcti-device0t64` – names confirmed
in B1; one package for both releases, so as alternatives `a | b`); `Conflicts: gss-ntlmssp`. The runtime comes from
`packages-microsoft-prod.deb` (`https://packages.microsoft.com/config/debian/<12|13>/`), documented as a prerequisite.

**Runtime updates.** A .NET patch keeps the package names but installs into version-specific directories
(`/usr/share/dotnet/shared/Microsoft.AspNetCore.App/10.0.13/` …) and dpkg removes the old directory, while the running
host has fixed its assembly paths at start – assemblies not loaded yet (e.g. `System.Formats.Tar` for GPO reads) would
then fail with `FileNotFoundException`. `debian/admng-watchdog.triggers`:
`interest-noawait /usr/share/dotnet/shared/Microsoft.NETCore.App`,
`interest-noawait /usr/share/dotnet/shared/Microsoft.AspNetCore.App`, `interest-noawait /usr/share/dotnet/host/fxr`;
`postinst triggered)` runs `deb-systemd-invoke try-restart admng-watchdog.service || true`.

Maintainer scripts (`set -e`, idempotent, no network):
- `postinst configure`: `systemd-sysusers admng-watchdog.conf` (an `admng-watchdog` system user that already exists –
  the instructions create it in 3.3 so that the firewall rule of 16 can name it – is kept unchanged; only the
  membership is added); `systemd-tmpfiles --create admng-watchdog.conf`;
  ownership/modes of `/etc/admng-watchdog` per section 11; `credentials/dp-key.cred` if missing (10); self-signed
  certificate if `Hosting.Certificate.Path` does not exist (6.3) – prints the fingerprint; `deb-systemd-helper enable`
  for the service and both timers; on **first install** nothing is started (no keytab yet; message "next: sudo
  admng-watchdog-keytab init, edit appsettings.json, then systemctl start admng-watchdog"); on **upgrade**
  `systemctl try-restart admng-watchdog.service` (migrations run at start with the pre-migration backup, spec 16.1).
- `prerm remove`: stop the service and the timers. `prerm upgrade`: nothing (restart in postinst).
- `postrm remove`: `systemctl daemon-reload`. Configuration, keytab and data stay.
- `postrm purge`: removes `/etc/admng-watchdog` (incl. keytab, certificate, credential – the AD account stays: the
  message tells the Tier 0 admin to disable or delete `ADMNG-WD`), `/var/lib/admng-watchdog-keytab`, `/run/admng-watchdog`.
  **`/var/lib/admng-watchdog` (the 3-year history) is kept** and the message names it; it is deleted only by an explicit
  `sudo rm -rf --one-file-system /var/lib/admng-watchdog` (decommissioning procedure in the instructions, ADR-0008
  consequences). The system users and groups are not removed (Debian convention).
- Downgrades are refused by the service itself (database newer than the build → exit 3).

### 13.2 `admng-openwec` (two builds: `~deb12`, `~deb13`)

`/usr/bin/openwec`, `/usr/bin/openwecd`, `/lib/systemd/system/openwecd.service` (12.2), `/etc/openwec.conf.toml`
(7.2), the empty directory `/etc/openwec/subscriptions/` (0755 root; **no file in it** – anything shipped there would
be a conffile and leave `.dpkg-dist`/`.dpkg-old` copies that OpenWEC would load, 7.1),
`/usr/share/admng-openwec/admng-dc-security.toml.example` (7.3 with placeholders, not a conffile), sysusers
`u openwec - "OpenWEC" /var/lib/openwec /usr/sbin/nologin` + `g admng-keytab -` + `m openwec admng-keytab` (an
`openwec` system user that already exists – the instructions create it in 3.3 so that the firewall rule of 16 can name
it before the package is installed – is kept unchanged by `systemd-sysusers`; only the membership is added), tmpfiles
`d /var/lib/openwec 0751 openwec openwec -` (others may traverse to `out/` but not list; `admng-watchdog` needs the
search bit), `d /var/lib/openwec/db 0700 openwec openwec -`. `postinst`: db init /
upgrade, enable `openwecd.service` (started by the instructions after the keytab exists – `ConditionPathExists`).
Purge removes `/var/lib/openwec` (raw WEF data has no long-term value; ADR-0009).

### 13.3 Build

`tools/packaging/build-deb.sh` stages the trees and runs `dpkg-deb --build --root-owner-group`; `lintian` must pass
without errors (warnings reviewed). Versions: `admng-watchdog_<AdMng version>-1_amd64.deb`. Release assets:
both packages, `SHA256SUMS` and the OpenWEC source tarball.

---

## 14. Default configuration

### 14.1 `/etc/admng-watchdog/appsettings.json` (shipped)

```json
{
  "Watchdog": {
    "DataDirectory": "/var/lib/admng-watchdog",
    "AllowInsecureDataDirectoryAcl": false,
    "AllowPrivilegedServiceAccount": false,
    "Authentication": "Negotiate",
    "Hosting": {
      "Server": "Kestrel",
      "Urls": "https://admng-wd.contoso.com:8443",
      "Certificate": { "Path": "/etc/admng-watchdog/tls/admng-wd.crt", "KeyPath": "/etc/admng-watchdog/tls/admng-wd.key" },
      "RequireKerberos": true,
      "AllowedRemoteSubnets": [ "10.0.20.0/24" ],
      "MonitoringSubnets": []
    },
    "Authorization": {
      "AdminSids": [ "S-1-5-21-0-0-0-0" ],
      "ViewerSids": [ "S-1-5-21-0-0-0-0" ],
      "DeniedSids": [],
      "ViewerDetailLevel": "Standard",
      "AllowTier0Suppression": false,
      "RoleCacheSeconds": 300,
      "RoleCacheMaxStaleSeconds": 3600
    },
    "Notifications": { "OAuthSmtpHosts": [ "smtp.office365.com" ] },
    "Directory": {
      "Domain": "contoso.com",
      "Servers": [ "DC01.contoso.com", "DC02.contoso.com" ],
      "PreferPdc": true,
      "Authentication": "CurrentUser",
      "Security": "SignAndSeal"
    },
    "Kerberos": {
      "Realm": "CONTOSO.COM",
      "ServicePrincipal": "ADMNG-WD$@CONTOSO.COM",
      "KeytabStatusFile": "/var/lib/admng-watchdog-keytab/status.json"
    },
    "Collector": {
      "Kind": "OpenWec",
      "OutputDirectory": "/var/lib/openwec/out",
      "FileName": "security.jsonl",
      "SubscriptionUuid": "4f7c2e1a-9b3d-4c55-8e21-6a0d9c7b3e10",
      "ProgressFile": "/run/admng-watchdog/wec-progress.json",
      "AdditionalSourceAddresses": [],
      "MaxLogonRecordsPerDcPerMinute": 20000
    },
    "Retention": {
      "MinEventsDays": 1095
    },
    "Sysvol": {
      "Enabled": true,
      "SmbClientPath": "/usr/bin/smbclient",
      "ConfigFile": "/etc/admng-watchdog/smb-client.conf",
      "InvocationTimeoutSeconds": 60
    }
  },
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } }
}
```

The placeholder SID `S-1-5-21-0-0-0-0` makes `--check-config` and the start fail with "replace the role group SIDs"
(appendix A.3 prints the real ones). The service watches this file (`reloadOnChange`) exactly like spec 8.4
(Authorization, Notifications and subnets apply within seconds; the rest at restart). `ADMNG_WATCHDOG_CONFIG` is
refused when running under systemd (`INVOCATION_ID` / `NOTIFY_SOCKET` present).

### 14.2 Runtime configuration defaults that change

`Retention.EventsDays` **1095** (validator range 7–3650, plus the floor below); `Retention.LogonRecordsHours` 48;
`Retention.SecurityEventsDays` 30; `Retention.BackupsToKeep` 7; `Retention.MaxDatabaseMb` **4096** on Linux (below);
`Attribution.Source` `OpenWec`; `Attribution.DropComputerLogons` / `DropAnonymousLogons` true;
`Directory.GpoContent.Enabled` true; built-in rules `tier0-badsuccessor`, `tier0-machine-quota`, `watchdog-self`,
`tier0-gpo` (section 9).

**Retention floor** (MUST): the 3-year history is a user decision with evidence value, while any Watchdog admin – or
anyone holding a forged admin ticket (4.3) – could otherwise set `EventsDays` to 7, and the next daily maintenance
deletes the older events with `secure_delete` (the local backups that could restore them rotate out after 7 nights).
New bootstrap setting `Watchdog:Retention:MinEventsDays` (root-owned `appsettings.json`, default 1095): the validator
rejects a lower `EventsDays` with the code `retentionBelowFloor`; only root can lower the floor. Every **decrease** of
`EventsDays`, `SecurityEventsDays`, `LogonRecordsHours` or `BackupsToKeep` is a Critical `WatchdogConfigurationChanged`
(added to the "weakening" cases of spec 7 / ConfigChangeAuditor). SHOULD: a decreased `EventsDays` takes effect only
7 days after the change (pending value shown in Status and in the digest), so the previous recipients can react.

**Size cap and disk** (MUST): `MaxDatabaseMb` bounds `watchdog.db`, and the nightly `VACUUM INTO` keeps
`BackupsToKeep` copies of it next to the database, so `(BackupsToKeep + 2) × MaxDatabaseMb` must fit the free space of
`/var` (the recommended 40 GB `/var` → 4096 MB with 7 backups; the 10 240 MB default of spec 5.4 would need 90 GB).
`RetentionJob` skips the backup when the free space is below 1.5 × the database size and raises health Error
`storage.backupSkipped`. The cap purges only `security_events`; Watchdog events are never purged by size.

Size guide for 3 years: events ≈ 0.6 KB → 1 000/day ≈ 0.7 GB; logon records ≈ 120 MB (7.7); backups 7 × database; a
60 GB disk with at least 40 GB for `/var` is recommended (health `storage.free` below 1 GiB). Raw OpenWEC files are
bounded separately by `MAX_UNCONSUMED_MB` (7.4).

---

## 15. Logging

- `UseSystemd()` (package `Microsoft.Extensions.Hosting.Systemd` 10.0.13 – not part of the shared framework, so no
  NU1510) configures the console logger with the systemd formatter (`<priority>` prefixes) → journald. The
  `RedactingLoggerProvider` and `ThrottlingLoggerProvider` wrap it as before; `SafeExceptionLog` rules and
  `LogHygieneTests` are unchanged (no directory values, no exception messages, no secrets, no file contents, no
  smbclient output).
- The event ids of spec 19.8 stay (they appear as `Category[1003]` in the journal line); Information entries 1000, 1006,
  2600/2601 (the RODO access log), 1008 (certificate fingerprint) are kept. New: 1011 service account check failed
  (code), 1012 collector problem (DC, code), 1013 GPO content problem (GPO GUID, code), 2501/2502 authentication hints.
- Monitoring recipe (instructions): `journalctl -u admng-watchdog -p warning --since today`,
  `systemctl status admng-watchdog openwecd`, `systemctl list-timers 'admng-*'`, `sudo admng-watchdog --health`.
  journald size: `SystemMaxUse=1G` recommended. No file logs.
- `openwecd` logs to journald at `warn` (no access log – it would contain client IPs and principals for every batch;
  the `access_logs` key stays absent, 7.2).

---

## 16. Host hardening (input for the instructions)

- **Hosting (MUST):** the VM runs only on the Tier 0 virtualization platform – the same hosts or cluster and the same
  administrators as the DCs – or on a dedicated host whose administrators, console access, snapshots/backups and vTPM
  key protector are Tier 0. Hypervisor administrators can open the console, mount or copy the disk, snapshot or dump
  memory, read the vTPM state and roll the VM back: that is the keytab, `/var/lib/systemd/credential.secret`, the Data
  Protection keys and the 3-year history, a forged Watchdog-admin ticket, or a blinded Watchdog – a Tier 1 → Tier 0
  escalation on a general (Tier 1) cluster. Hypervisor disk encryption only protects against theft of the storage,
  not against the hypervisor's administrators. The pilot checklist (20.6) records it.
- Debian 13 netinst with only "SSH server" and "standard system utilities"; static IP 10.0.1.25; `/etc/hosts`:
  `10.0.1.25 admng-wd.contoso.com admng-wd` (`hostname --fqdn` = `admng-wd.contoso.com`); `/etc/resolv.conf` →
  10.0.1.10, 10.0.1.11, `search contoso.com`; chrony with `server DC01.contoso.com iburst` and `server DC02.contoso.com
  iburst` only, plus `maxdistance 16.0` (below).
- Local admin accounts for the 1–3 Tier 0 people, SSH keys only (`PasswordAuthentication no`, `PermitRootLogin no`,
  `AllowGroups ssh-admins`), `sudo` only for them; unattended-upgrades for security updates (incl.
  packages.microsoft.com for the runtime, pattern `site=packages.microsoft.com`); swap disabled for good after
  installation – `swapoff -a`, the fstab entry commented out, `RESUME=none` **and** `systemd.gpt_auto=0` in
  `GRUB_CMDLINE_LINUX` (once fstab lists no swap, `systemd-gpt-auto-generator` activates a GPT swap partition of the
  root disk at every boot – `add_partition_swap()` only backs off when fstab has a swap entry; systemd v252 and v257
  source, both honour `systemd.gpt_auto=`), checked after a reboot (`swapon --show` empty, the parameter in
  `/proc/cmdline`); `MemorySwapMax=0` on the units that hold key material (12.1–12.3) stays as a second layer;
  disk encryption by the hypervisor preferred (against stolen storage only – see "Hosting") – LUKS needs a console
  passphrase at every boot (replaces the BitLocker advice of ADR-0008).
- chrony: Windows time servers report a large root dispersion (seconds), above chrony's default `maxdistance` of 3 s,
  so chrony would refuse to select the DCs (chrony FAQ 3.6 "Using a Windows NTP server?"). The instructions add
  `maxdistance 16.0` in `/etc/chrony/conf.d/admng-ad.conf` (Debian's `confdir`; `sources.d` files may contain only
  `server`/`pool`/`peer`) and check `chronyc ntpdata <DC address>` (root dispersion) and `chronyc -N sources`.
- nftables – the shipped example `/usr/share/admng-watchdog/nftables/admng-watchdog.nft` MUST equal the ruleset of the
  instructions (3.3) byte for byte, including the defines, the forward chain, the SMTP rule limited to the relay and
  outbound echo-request (validated with `nft -c -f` in CI, in a container where the users `openwec` and
  `admng-watchdog` exist). SSH is accepted only from the Tier 0 PAW addresses (`PAWS`), the API from the whole admin
  subnet. `meta skuid "openwec" drop` sits before the loopback and DC accepts and drops every packet of the `openwec`
  user that does not belong to an established connection (4.3 item 3; replies to the DCs' inbound WEF connections are
  `established`). TCP 80/443 to any address is accepted only for `root` and `_apt` (apt runs its download methods as
  `_apt`; the instructions' `curl` checks run as root) and for `admng-watchdog` (Teams, SMTP revocation check – 4.3,
  "The `admng-watchdog` process"; direct egress without a proxy – user decision 2026-10-20, Q23 closed); every other
  local user reaches only the DCs and the SMTP relay. nft resolves user names when the rules are loaded and refuses an
  unknown user ("User does not exist", nftables 1.0.9; checked in a namespace: `meta skuid { "root", "_apt",
  "admng-watchdog" }` loads as a set of three uids), so the instructions create the system users `openwec` and
  `admng-watchdog` in 3.3, before the first load; the packages keep them (13.1 sysusers, 13.2):

  ```
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
  ```

---

## 17. What is removed, and when

| Item | Until the first Linux release (0.3.0) | After 0.3.0 (milestone B10) |
|---|---|---|
| Windows service hosting (`AddWindowsService`, `Microsoft.Extensions.Hosting.WindowsServices`, SCM recovery, delayed-auto) | compiled, untested on Linux, not shipped | removed |
| HTTP.sys (`HttpSysSetup`, `WindowsLogonPackageResolver`, `EnableCBTHardening`, `Hosting.Server = HttpSys`) | compiled, not shipped | removed; `Server` must be `Kestrel` |
| DPAPI-NG key protection | Windows branch kept | removed (systemd credential only) |
| EventLog RPC reader (`WindowsSecurityEventSource`, `SecurityEventXPath`, `LiveSecurityEventLookup`, RPC paths of `SecurityLogIngestor`, `SourceQueryGate` RPC use) | Windows only | removed; `SecurityEventQueryBuilder` stays (subscription generator) |
| NTFS ACL code (`DataDirectoryAcl`, Windows `BootstrapFileGuard` branch), `LocalMachine\My` certificate selection (`SystemCertificateStore`) | Windows only | removed |
| Application-log provider, event source creation | Windows only | removed |
| `Deploy/*.ps1`, `AdMngWatchdog.Install.psm1`, `DeployScriptTests` | not shipped | removed |
| `AttributionSourceKind.DomainControllers/ForwardedEvents`, `Collector` host, `WatchdogHostGroupSids` | rejected on Linux | values kept for stored configs (migrated), UI options removed |
| Windows CI jobs for the service (publish win-x64, `--smoke-test` on Windows) | kept | removed; AD Manager Windows CI stays |
| `docs/WATCHDOG.md` Windows chapters | replaced in B8 | – |

The 0.2.0 release assets stay downloadable. A 0.2.0 database can be moved to Linux (appendix A.10): copy `watchdog.db`
only (not `keys\`), the migrations run, channel secrets are re-entered, detection continues incrementally against the
same DCs; RPC cursors are ignored.

---

## 18. AD Manager changes

1. **Certificate confirmation on first connection** (Client, Presentation, App):
   - `WatchdogHttpHandlerFactory.IsTrusted`: accept when system validation passes; otherwise **only** when the only
     error is `RemoteCertificateChainErrors` **and** the leaf SHA-256 equals the pin; a name mismatch is never accepted
     (today any error passes with a matching pin – tightened).
   - On a chain failure without a pin (or with a different pin) the handler captures the presented leaf (SHA-256,
     subject, SAN DNS names, validity) and the client throws `WatchdogClientException` with the new error
     `CertificateUntrusted` / `CertificateChanged` carrying that information.
   - New dialog `WatchdogCertificateTrustViewModel` / `WatchdogCertificateTrustWindow` (registered with
     `services.AddWindow<…>()`, opened from `DialogService`, added to `SmokeTest` and a UI test): shows the fingerprint
     in groups of 4 hex digits, subject, SAN, validity, and the instruction „Porównaj z wynikiem `sudo admng-watchdog
     cert-fingerprint` na serwerze”; buttons "Ufam temu certyfikatowi" (saves `PinnedCertificateSha256` – a setting,
     not directory data) and "Anuluj". `CertificateChanged` shows old and new fingerprints in red and requires the same
     explicit confirmation; nothing is re-pinned silently.
   - Settings → Watchdog shows the pinned fingerprint with "Usuń przypięcie".
2. **Status tab**: new sections "Konto usługi", "Kolektor WEF" (per DC: last record, lag, backlog, rejected records),
   "Zawartość GPO" (baseline progress, last read, failures, SYSVOL lag), "Host" (OS, runtime, credential key variant,
   free space, clock skew, LDAP SSF); remedies in Polish.
3. **Events**: display names and icons for kinds 510–512 and `HealthServiceAccount`; GPO settings changes rendered as
   "ustawienie: było → jest" with the setting key split into a readable path; new tags and reason codes localized.
4. **Configuration**: Attribution source shows "OpenWEC (WEF)" only; collector settings (7.8); `GpoContent`; retention
   field for logon records; the four new built-in rules.
5. All strings en + pl (`Strings*.resx`, `NotificationStrings*.resx`); Presentation tests for every new VM; UI tests on
   Windows CI.

---

## 19. Code changes by project (overview)

| Project | Changes |
|---|---|
| `AdMng.Watchdog.Contracts` | kinds 510–512, 909; tags; reason; `AttributionSourceKind.OpenWec`; settings (`LogonRecordsHours`, `EventsDays` 1095, collector fields, `GpoContentSettings`); `SchemaVersion` 2 + migration; Status DTO sections |
| `AdMng.Watchdog.Core` | `Abstractions/ISecurityEventLookup.cs`, `Abstractions/ISysvolReader.cs`, `Abstractions/IGpoContentStore.cs`; `Model/SecurityEventCriteria.LogonId`, `Model/GpoContentSnapshot.cs`, `GpoSetting.cs`; `Attribution/WecRecordReader.cs`, `CollectedRecordIngestor.cs`, `LogonRecordReducer.cs`, `StoredSecurityEventLookup.cs`, `GpoAttributionInheritor.cs`; `Gpo/*` (job, validator, parsers, differ, classifier, categories); `Rules` (new defaults, severities, tags); `Configuration` (validator codes `sourceUnsupportedOnHost`, `logonRetentionTooShort`, config migration v2) |
| `AdMng.Watchdog.Service` | `Hosting/SystemdSetup.cs` (UseSystemd, watchdog notifier), `PosixPermissionCheck.cs`, `PemCertificateSource.cs`, `LdapConfCheck.cs`; `Auth/KerberosSidClaimsTransformation.cs`, `PrincipalSidResolver.cs`, `RoleCache.cs`; `Attribution/WecFileIngestionJob.cs`, `WecFileCursor.cs`, `WecProgressFile.cs`; `Gpo/SmbClientSysvolReader.cs`, `SmbClientListingParser.cs`; `Secrets/SystemdCredentialXmlEncryptor.cs`, `…Decryptor.cs`, `DataProtectionMasterKey.cs`; `ServiceAccount/ServiceAccountMonitor.cs`, `KeytabInspector.cs` (reads kvnos via libkrb5); `Storage/Migrations/Migration0004SecurityEventsV2.cs`, `Migration0005GpoContent.cs`, `SqliteGpoContentStore.cs`; CLI verbs (`cert-fingerprint`, `print-wec-subscription`, `reset-secrets-key`, `cert-renew-self-signed`) |
| `AdMng.Watchdog.Keytab` (new) | section 4.4 |
| `AdMng.Directory` | SSF read after bind (`OpenLdapSasl.cs`, `LibraryImport` of `ldap_get_option`), `IsEncrypted` on Linux, `Servers` list support in the connector options |
| `AdMng.Watchdog.Client`, `AdMng.Presentation`, `AdMng.App` | section 18 |
| `packaging/` (new, repo root `packaging/admng-watchdog/`, `packaging/admng-openwec/`), `tools/packaging/build-deb.sh` | units, sysusers, tmpfiles, maintainer scripts, conffiles, nftables example |
| `tools/testlab` | Samba DC: register `ldap/<dc>` SPN (`samba_spnupdate`) so GSSAPI LDAP works; GPO fixture upload; OpenWEC replay helper; keytab lab script |

---

## 20. Testing strategy

1. **Unit (Core.Tests, Linux)**: `WecRecordReaderTests` (fixtures of RawJson lines built from the Appendix-A Microsoft
   samples of the spec until pilot recordings exist; malformed, partial, concatenated, oversized lines; wrong
   subscription; principal/Computer/IP mismatch; RODC principal), `CollectedRecordIngestorTests` (reduction of
   4624/4625/4771/4776, computer/anonymous drops, duplicates by (computer, record id, time), event-log-only ids stable
   across re-reads), `StoredSecurityEventLookupTests` (same results as the existing resolver tests – the resolver
   test suites run against both lookups), GPO parsers (fixtures: UTF-16 `GptTmpl.inf` with every section,
   `Registry.pol` with every type and delete markers, GPP XML of each kind incl. `cpassword` masking, `scripts.ini`,
   `audit.csv`, `comment.cmtx`, malformed and oversized files), `GpoSettingsDifferTests`, `GpoPathValidatorTests`,
   `GpoSeverityTests`, rule tests for `tier0-badsuccessor`, `tier0-machine-quota`, `watchdog-self` (rotation
   exclusion), config migration v1 → v2, validator codes.
2. **Service tests (Linux)**: tailer (rotation by rename, SIGHUP reopen simulated, inode reuse with a different first
   line, crash between read and commit → no loss/no duplicate, file lost → gap, progress file), POSIX permission
   checks on temp directories (modes, owner, ACL xattr, symlink, NFS type mocked), AES-GCM XML encryptor/decryptor
   round trip and wrong key, credential missing → exit 9, `SystemdWatchdogNotifier` with a fake `NOTIFY_SOCKET`
   listener (READY=1, WATCHDOG=1 only while jobs progress, STOPPING=1), `KerberosSidClaimsTransformation` against
   `FakeDirectorySession` (tokenGroups, realm mismatch, cache, stale cache when LDAP is down, DeniedSids),
   `LdapConfCheck`, PEM certificate reload, smbclient listing parser (fixtures from Samba 4.17 and 4.22) and process
   wrapper with a fake executable, TGT refresh before smbclient (1-minute TGT), `RemoteAddressFilter` on Linux
   (loopback → 403 except `/healthz`), `WecRecordReader` with `Client`-only, `Principal`-only and both,
   `LogHygieneTests` extended to the new components.
3. **Integration (Samba lab, `ADMNG_TEST_LDAP_*`)**: keytab tool `init` with a one-time password set by
   `samba-tool user setpassword` (after an admin reset `init` keeps a live ticket of the previous kvno working),
   `rotate` (kvno + 1, previous kvno kept, acceptor keeps working, client re-auth; in `otpActive` the next run attempts
   kpasswd regardless of age),
   LDAP GSSAPI with SSF ≥ 56 (lab DC gets its `ldap/` SPN), Kestrel Negotiate with the keytab and **role resolution**
   (Viewer / Admin / 403 / DeniedSids – now provable on Linux), SYSVOL listing and content read with Kerberos
   (signing required, encryption on), GPO edit → `GroupPolicySettingsChanged` with "was → is", out-of-band edit →
   `GroupPolicyContentOutOfBand`, dMSA rule only where the schema allows (Samba has no dMSA classes – Windows pilot).
   OpenWEC: `openwecd` runs in the lab with the keytab; a curl `--negotiate` probe proves Kerberos acceptance; event
   delivery is tested by replaying recorded RawJson files into `out/` (no WEF client on Linux).
4. **Packaging CI (Linux job)**: build both packages; `lintian`; install into `debian:12` and `debian:13` containers
   with the Microsoft repository; `systemd-analyze verify` of every unit; `systemd-analyze security --offline=true`
   with an exposure score **≤ 2.0** for `admng-watchdog.service` and ≤ 3.0 for `openwecd.service` (recorded per
   release; without relying on `SocketBind*`); `admng-watchdog --check-config` with the shipped placeholders must fail
   with the documented message, while `admng-watchdog print-wec-subscription --dc …` with the same placeholders must
   succeed and emit the shipped `SubscriptionUuid` (7.3); `--smoke-test` as user `admng-watchdog` inside the container; purge keeps
   `/var/lib/admng-watchdog`; `rotate` through the real unit (`systemctl start admng-watchdog-keytab.service`, lock
   in the StateDirectory); `openwecd` started under its unit's sandbox creates nothing outside `db/` and `out/`;
   the wec-rotate unit test of 12.4 (incl. the `MAX_UNCONSUMED_MB` limit); a simulated runtime upgrade fires the dpkg
   trigger and restarts the service; the unit restarts after repeated crashes and watchdog timeouts without reaching
   `failed`, a `RestartPreventExitStatus=` exit runs `admng-watchdog-failed.service`; on the lab VM with the ruleset of
   16 and the drop-in of 12.2: as user `openwec`, `curl https://packages.microsoft.com` and `kinit -k` fail, `openwecd`
   cannot open any outbound connection, and WEF from a DC address still arrives; as `nobody` the same `curl` fails,
   as `admng-watchdog` and as root it succeeds, and `apt-get update` works.
5. **Windows CI**: AD Manager UI tests (certificate dialog, new Status sections) unchanged in approach.
6. **Pilot checklist (lab: WS2025 evaluation DC + Debian 13 + ADM-WS01 workstation, then the customer test domain
   with WS2025 + WS2022)** –
   everything section 22 marks "pilot only", plus: LDAP signing enforcement on WS2025 with GSSAPI sealing; `HOST/` vs
   `HTTP/` tickets from WinRM on both DC versions (`klist` on the DCs, openwecd debug); subscription XML validated with
   `Get-WinEvent -FilterXml` on both DCs; Forwarding/Operational without event 105; measured 4624/4776 volume per hour
   and Security log sizing; restart of `openwecd` under load (no loss); rotation across DC01/DC02 replication;
   `tokenGroups` readable in the customer's hardening; SACL coverage (`Stan → Atrybucja`); one GPO edit of each
   category; dMSA creation and link change raise Critical within one cycle; a test audit entry added to and removed
   from the domain head's SACL raises `SecurityDescriptorChanged` with tag `auditSettings` (9.4); certificate
   confirmation in AD Manager; VM restore drill (secrets re-entry); production VM placed on the Tier 0 virtualization
   platform (16, "Hosting").

---

## 21. Milestones (stage B)

| # | Scope | Acceptance criteria |
|---|---|---|
| B1 | Linux host basics: `UseSystemd`, notify + watchdog notifier, journald logging, POSIX permission checks, PEM certificate, systemd-creds Data Protection, Linux paths/defaults, `packaging/` skeleton with units, sysusers, tmpfiles, maintainer scripts, `build-deb.sh`, Linux packaging CI job | 0 warnings; Service.Tests green on Linux; `.deb` installs on Debian 12 and 13 containers; `systemd-analyze verify` clean; security exposure ≤ 2.0; `--smoke-test` passes as `admng-watchdog`; purge keeps data |
| B2 | Identity: `AdMng.Watchdog.Keytab` (init/rotate/verify/status), keytab timer, `krb5.conf`, LDAP GSSAPI via `LDAPCONF`, SSF check, `Directory.Servers` + PDC selection, own-account monitor (4.7), clock-skew check | Samba lab: init with one-time password, rotate (kvno + 1, previous kept), service and a Negotiate client keep working without restart, SSF ≥ 56 reported, `minssf` violation refuses the session, health codes of 4.6/4.7 produced by fault injection |
| B3 | API auth on Linux: claims transformation, role cache, 401 on GSS failure, `RequireKerberos`, `AllowedRemoteSubnets` default | real-Kerberos integration test proves Viewer/Admin/403/DeniedSids on Linux; no HTTP 500 for a wrong-key ticket; NTLM-only client gets 401 |
| B4 | WEF ingestion: `admng-openwec` build (both Debian versions), subscription generator, tailer, identity checks, reduction, migration 0004, store lookups, rotation unit, collector health, Status section | replay fixtures produce the same attribution results as the RPC path tests; crash/restart at every step loses and duplicates nothing; rotation + deletion only after consumption, also by the real unit of 12.4; openwecd accepts Kerberos with the keytab in the lab (tickets for `HTTP/` and `host/`) and runs under its sandbox without access-log files |
| B5 | GPO content: SYSVOL reader, parsers, differ, kinds 510–512, attribution inheritance, reconciliation, migration 0005, limits | lab GPO edits of each category produce correct "was → is"; path redirection → Critical without any SMB connection to the foreign host; out-of-band edit detected next day (test: forced reconciliation); `cpassword` never stored (DB scan test) |
| B6 | Rules: BadSuccessor, MachineAccountQuota, `watchdog-self`, `tier0-gpo`, catalog severities, localization | rule tests green; rollout mode still notifies them; config change disabling them is Critical and needs `AllowTier0Suppression` |
| B7 | AD Manager: certificate confirmation flow, handler tightening, Status sections, new kinds/tags/reasons, configuration UI | Presentation tests green on Linux; UI tests + smoke test green on Windows CI; screenshots reviewed |
| B8 | Documentation: `docs/WATCHDOG-LINUX.md` brought in line with the built packages (removing the "[od 0.3.0]" marks and the "Stan prac" table), `docs/WATCHDOG.md` Windows installation chapters replaced by a pointer to it, `CLAUDE.md` (rules 1–2 exceptions, layout table, commands), ADR-0008 amendment note, spec sections of 23 amended, release notes | docs review by the lead; every command of appendix A executed once in the lab |
| B9 | Lab pilot (WS2025 eval DC + Debian 13 + ADM-WS01 workstation, appendix B) then customer test domain (WS2025 + WS2022) – checklist 20.6 | all checklist items green or recorded as accepted risks; open questions Q1–Q22 answered or carried with an owner |
| B10 | Release 0.3.0 (Linux packages); afterwards removal of the Windows service code (section 17) | release artifacts published with `SHA256SUMS`; after removal: build 0 warnings, all Linux tests green, AD Manager CI green |

Parallelism: B2 and B4 can run in parallel after B1; B5 after B2 (needs Kerberos SMB); B6 after B4/B5 contracts; B7
after the Contracts changes of B4–B6 are merged.

### 21.1 As built (2026-10-20): deviations and lab findings (B1, B2)

**B1.**

Code: `Service/Hosting/SystemdSetup.cs`, `SystemdNotifySocket.cs`, `SystemdWatchdogNotifier.cs`, `JobProgress.cs`,
`PosixPermissionCheck.cs`, `PemCertificateSource.cs` (+ `CertificateSelector`), `StartupChecks.cs`, `LoggingSetup.cs`,
`Secrets/DataProtectionMasterKey.cs`, `DataProtectionKeySource.cs`, `SystemdCredentialXmlEncryptor.cs` /
`…Decryptor.cs`; packaging in `packaging/admng-watchdog/`, `tools/packaging/build-deb.sh` and
`tools/packaging/test-deb-install.sh` (the install test of the CI job `linux-package`).

- **No `UseSystemd()` call.** `SystemdHelpers.IsSystemdService()` reads `/proc/<parent>/comm`, which
  `ProtectProc=invisible` hides (PID 1 belongs to root): the unit would never send `READY=1`. `SystemdSetup` registers
  the same parts itself – `SystemdLifetime` (`READY=1` after the start-up checks and the listeners, `STOPPING=1`), an
  `ISystemdNotifier` over `NOTIFY_SOCKET` and the console logger with the systemd formatter – when `INVOCATION_ID` or
  `NOTIFY_SOCKET` is set **and** the parent process is PID 1 (`/proc/self/stat`): a CI step or test run below a runner
  that itself runs as a unit inherits `INVOCATION_ID` but has another parent. "Managed service" (ADMNG_WATCHDOG_CONFIG
  refused, exit 6 checks, Development scheme refused) now means Windows SCM **or** this unit.
- **Watchdog notifier:** `WATCHDOG=1` every `min(WATCHDOG_USEC / 4, 30 s)` (`WATCHDOG_PID` honoured), only after
  `ApplicationStarted` and only while no job loop is stalled: a running iteration may last 6 h (`detection`, `scans`,
  `retention` – a baseline of a large domain) or 1 h (others), a wait or backoff its interval + 2 min. A stalled job is
  logged once (1010, Critical) so the journal says why systemd restarted the service.
- **`SystemCallFilter=sched_setaffinity` added to 12.1** after the `~@resources` line: CoreCLR refuses to start
  ("Failed to create CoreCLR, HRESULT: 0x8007054F") when `sched_setaffinity` returns `EPERM` (bisected in the lab,
  systemd 255, .NET 10.0.13). `SystemCallErrorNumber=EPERM` is required as well: without it `@system-service` alone kills
  the runtime (`mincore`, x86_64 syscall 27, is outside the set; with `EPERM` the runtime copes). `ProcSubset=pid` is
  kept: runtime, GC, Kestrel and `/healthz` work, and the whole `AdMng.Watchdog.Service.Tests` suite ran under
  `systemd-run` with the unit's sandbox properties (12.1 note) with no sandbox-specific failure (the only differences to
  an unsandboxed run in the same container were 24 tests that need DNS, failing in both, and one test that relied on the
  test runner's umask, fixed).
- **Lab run of the unit under systemd 255 (PID 1 in a privileged container):** `Type=notify` reaches `active`,
  watchdog timestamps advance every 30 s, journald gets `<4>`/`<6>` priorities, `Umask: 0077`, seccomp on; SIGKILL →
  `auto-restart`; an exit-6 start (appsettings.json owned by `admng-watchdog`) → `failed` without restart and
  `admng-watchdog-failed.service` logs the crit line. `LoadCredential*=` directories did not appear for *any* unit in
  that container (also `LoadCredential=x:/etc/hostname`), so the run used a drop-in `CREDENTIALS_DIRECTORY` with the
  `systemd-creds decrypt` output; the real `LoadCredentialEncrypted=` path is part of the VM pilot (B9). Exposure
  (`systemd-analyze security --offline=true`, systemd 255): **1.3**; `SocketBind*`: Ubuntu 24.04's systemd 255 shows
  `-BPF_FRAMEWORK`; the Debian 12/13 values are printed by the CI job.
- **POSIX checks (11):** item 1 exits 5 only in Production; elsewhere (lab, development, tests – no `UMask=0077`) and
  with `AllowInsecureDataDirectoryAcl` it is health Warning `storage.acl`. Items 2, 3 and 6 run only as the systemd
  unit (like the Windows check under the SCM). Under `ProtectSystem=strict` `access(W_OK)` always fails (`EROFS`), so
  under the unit item 2 is decided by the owners (root) – an `appsettings.json` owned by the service user → exit 6.
  Item 3's credentials-folder test and item 6 are one Warning `host.hardening` (contract step). Item 5 (`ldap.conf`,
  exit 2) belongs to B2 (LDAP over GSSAPI). Owners come from `lstat(2)` (`LibraryImport`, x86_64 and aarch64 layouts;
  .NET has no owner API), ACLs from `lgetxattr`, network file systems from `DriveInfo.DriveFormat`.
- **Shipped placeholders (14.1):** `--check-config` reports `placeholderSid` ("Replace the role group SIDs …") and the
  start exits 2. `--smoke-test` no longer reads the bootstrap file or `ADMNG_WATCHDOG_CONFIG` (it must not depend on
  the machine's configuration nor need read access to the 0640 file).
- **Credential variant:** the service cannot read `credentials/`, so `postinst` / `reset-secrets-key` record the variant
  actually used in `/etc/admng-watchdog/dp-key.variant` (root:root 0644, no secret; new file of table 11); the service
  reports it as `host.credentialKey` and in Status „Host”. The key-element ciphertext is bound to purpose, algorithm and
  `kid` as AES-GCM associated data.
- **PEM certificate:** `KeyPath` is optional (key in the `Path` file). Kestrel serves the leaf; intermediates come from
  the system store (company-CA chain installed on Debian, A.8). Reload on file change (2 s after the last change) and
  every 10 minutes; a key of another certificate, an unreadable or invalid file → Error `certificate.key` (Args: the
  problem) while the current certificate keeps serving; an expired certificate or one without serverAuth is not
  applied. 1008 is logged at every Kestrel start (Windows too). `--health` pins the file's leaf.
- **Package (13.1):** units in `/usr/lib/systemd/system` instead of `/lib/systemd/system` (Debian 13 `/usr` merge,
  lintian `aliased-location`; both releases search it). `Depends` has no `admng-openwec` until B4 builds it (the
  sysusers line `m openwec admng-keytab` creates `openwec` when it is missing) and gains `libc6 (>= 2.34)`,
  `libgcc-s1`, `libstdc++6` (lintian); `util-linux` (setpriv) is essential. Not in the B1 package: the keytab tool and
  `/usr/sbin/admng-watchdog-keytab` (B2), `wec-rotate` and `/etc/default/admng-wec-rotate` (B4), and the units of 12.3 /
  12.4 – they are in `packaging/` and verified by the CI job, but shipping them would enable timers whose binaries do
  not exist yet. `print-wec-subscription` (B4) and `notify-failure` (12.1 SHOULD) are not implemented:
  `admng-watchdog-failed.service` only logs at crit. The wrapper `/usr/sbin/admng-watchdog` runs `--check-config`,
  `--health`, `--smoke-test` as `admng-watchdog` (setpriv, the unit's environment, `KRB5CCNAME=MEMORY:`) and implements
  `cert-fingerprint`, `cert-renew-self-signed` and `reset-secrets-key` in shell (`/usr/lib/admng-watchdog/
  admng-watchdog-functions`, shared with `postinst`). The version is the AdMng `VersionPrefix` (`0.2.0-1` today).
  lintian (Ubuntu 24.04's, Debian mirrors are not reachable from the agent lab) reports no errors; reviewed warnings:
  `non-standard-file-perm` of `appsettings.json` (0640 by design), `no-manual-page`, `initial-upload-closes-no-bugs`,
  the nft example's `#!/usr/sbin/nft -f` line (kept: byte-for-byte equal to the instructions).
- **Verification in this milestone:** the install test (users, modes, credential, certificate, idempotent `postinst`,
  `systemd-analyze verify` of every unit incl. 12.3/12.4 with stub binaries, exposure ≤ 2.0, `nft -c`,
  `--check-config` failing on the placeholders and passing with real SIDs, `--smoke-test` as `admng-watchdog`, a
  Production start with the credential serving `/healthz` over TLS with an encrypted key ring and a 0600 database,
  remove/purge) passed in `ubuntu:24.04` locally; the CI job runs the same script in `debian:12` and `debian:13`.
  The service tests read the machine's `/etc/admng-watchdog/appsettings.json` when the package is installed on the test
  machine (bootstrap overrides the test settings) – run them on a machine without the package.

**B2.** Code: `src/AdMng.Watchdog.Keytab` (`KeytabTool`, `Native/Krb5Native.cs`, `Krb5Context.cs`, `KerberosConfig.cs`,
`SecretBuffer.cs`), `Service/Identity/` (`ServiceAccountMonitor`, `ServiceAccountChecks`, `KeytabFile`,
`KeytabToolStatus`, `Krb5ConfigFile`), `Service/Hosting/LdapConfigCheck.cs`, `Service/Directory/
ServiceDirectorySessionProvider.cs` (Servers, failover, SSF health), `Core/Detection/ClockSkewCheck.cs`; the SSF check of
`AdMng.Directory` (`OpenLdapSasl`, `LdapConnectionFactory.EnsureSealed`) came with the B2 start. Lab: `tools/testlab/
watchdog/keytab-lab.sh`, `KeytabLabTests` (Watchdog.IntegrationTests), `GssapiTests` (Directory.IntegrationTests).

- **One KDC at a time (4.4):** MIT has no API to address a single KDC. The tool builds every krb5 context from an explicit
  profile (`profile_init_path` + `krb5_init_context_profile`) – the main file is `--krb5-config`, else `KRB5_CONFIG`
  as .NET sees it, else `/etc/admng-watchdog/krb5.conf` (so a plain `sudo` that drops the environment still uses the
  service's file) – and, per KDC, prepends a temporary overlay (`[realms] REALM = { kdc = X  primary_kdc = X
  kpasswd_server = … }*`) whose final marker hides the main file's KDC list (lab: an overlay with an unreachable KDC
  gives `KRB5_KDC_UNREACH`, no fallback). Overlays hold no secret and live in a private temp folder (`PrivateTmp`). The
  tool uses its own `MEMORY` ccaches (`krb5_cc_new_unique`); the unit's `KRB5CCNAME` is not used.
- **Change, kvno, commit (4.4, 4.6):** `kadmin/changepw` credentials and the change go to `primary_kdc`; `init` first
  reads the one-time password's kvno from a service ticket for `host/<fqdn>` and expects + 1, `rotate` expects the
  keytab's newest + 1; the kvno is then confirmed from a fresh ticket obtained with the new password (a failed
  confirmation falls back to the expected kvno with a warning instead of losing the password). Keys:
  `krb5_c_string_to_key` with the AD salt; the password buffer is zeroed right after the derivation. The new keytab is
  written with `krb5_kt_add_entry` into `.<name>.new-<random>` (created 0600 with the 0x502 header, then 0640, group
  `admng-keytab` when the tool runs as root and the group exists, fsync, `rename`, directory fsync). Kept entries: the
  old keytab's newest kvno (for `rotate` that is new − 1; it also keeps live tickets when the account was changed
  elsewhere in between). The pre-commit check at `primary_kdc` does not block the commit: if it fails the keytab is
  still committed (otherwise the new password would be lost for certain) and the status is `verifyFailed`; there is no
  salt fallback through `krb5_get_etype_info`. Only a failure between the change and the rename yields
  `keytabWriteFailed` with the exact remedy text of 4.6 (lab: injected write failure → AD kvno + 1, keytab unchanged,
  health Errors `serviceAccount.rotation` and `serviceAccount.keytabStale`; new one-time password + `init` recovers).
- **Exit codes and status:** `init` that ends in `otpActive` exits 0 with a warning (the keytab works); `rotate` that is
  still refused in `otpActive` keeps `otpActive` (errorCode = the kpasswd result) and exits 2; `verify` updates
  `status.json` but keeps `otpActive`, `changeFailed` and `keytabWriteFailed` unless its own check fails;
  `replicationPending` exits 0. Output lines carry journald priorities (`<3>`, `<4>`, `<6>`) when `JOURNAL_STREAM` is
  set. Lab-only seams: `--password-stdin` and an internal fault hook (`KeytabToolHost.FaultPoint`) used by the tests.
- **Unit 12.3 under systemd:** verified as shipped (no change) in a privileged Ubuntu 24.04 container with systemd 255 as
  PID 1 and the B2 package installed against the lab DC: `init` as root, then the hardened
  `admng-watchdog-keytab.service` (drop-in `--if-older-than 0`) rotated kvno 3 → 4 with result `success`, keytab
  root:admng-keytab 0640, `status.json` root:admng-watchdog 0640, journald priority 6. Unlike 12.1 it needs no
  `SystemCallErrorNumber=EPERM` (it does not deny `@resources`; the run also succeeded with the errno reset). In the
  same container `admng-watchdog.service` (B1's `CREDENTIALS_DIRECTORY` drop-in for the container, 21.1 B1) started
  with the shipped `ldap.conf` (LDAPCONF check passed), bound over SASL GSSAPI as the account from
  `KRB5_CLIENT_KTNAME`, ran the baseline and the own-account monitor (`serviceAccount.drift` Warning: no Deny ACE in
  that probe), and kept working through a rotation by the timer's unit (kvno + 1, `NRestarts=0`, LDAP activity and
  `/healthz` ok afterwards).
- **Own-account monitor (4.7):** job `service-account` (hourly, 5 min after a directory error), registered on Linux when
  `Kerberos.ServicePrincipal` is set and the directory is enabled; it runs over the provider's current session (rule 4).
  The expected SPNs and `dNSHostName` come from the keytab's `HTTP/`/`host/` principal (`RestrictedKrbHost/` ignored).
  Privileged = the fixed SIDs/RIDs of 4.7 item 1 (root-domain RIDs in any domain SID), DnsAdmins by name, and the
  SID-configured roots of `PrivilegedGroups` (DN-configured roots cannot match `tokenGroups`, which yields SIDs only);
  the check's Args are the count, never SIDs. Exit code 8 comes from the first run of the job (it needs LDAP), i.e. after
  `READY=1`, through `IProcessTerminator`. A check that turns Warning/Error appends one `HealthServiceAccount` event. An
  account that is not found is `serviceAccount.drift` Error (`accountMissing`); unreadable `tokenGroups` is
  `serviceAccount.privileged` Warning. `host.primaryKdcNotPdc` is checked by the same job (first file of `KRB5_CONFIG`).
- **Clock skew (4.7 item 6):** evaluated in Core's detection cycle from the RootDSE `currentTime` that `DcStateReader`
  already reads – also on Windows; `HostStatusDto.ClockSkew` is updated there.
- **LDAP (5.1–5.3):** the `LDAPCONF` start-up check (exit 2) runs for the systemd unit and Production starts on Linux
  with Kerberos; besides `minssf ≥ 56` (`SignAndSeal`) / `maxssf=0` (`Tls`) it refuses `LDAPNOINIT`, `LDAPRC`,
  `ldaprc`/`.ldaprc` in `HOME` and the working directory, takes `LDAPSASL_SECPROPS` as the effective value when set
  (libldap applies `LDAP<option>` variables after the files) and refuses `Security = None/StartTls` with Kerberos.
  LDAPS needs no code beyond that: trust comes from `TLS_CACERT` with `TLS_REQCERT demand` (the shipped `ldap.conf`
  documents the LDAPS lines); `StartNewTlsSessionContext` is not used. `--check-config` requires `Directory.Servers`
  (or `Server`) on Linux and refuses IP literals with Kerberos. The provider tries the list in order, switches to the
  PDC emulator only when it is listed (and ≥ 2 servers), retries the pinned DC alone until `Directory.FailoverAfter`
  and then the following ones; a refused unsealed bind is health Error `directory.notSealed` (Ok with the SSF after a
  sealed bind) and every connect reports `LdapSsf`/`LdapSecurity` in „Host” (lab: SSF 256).
- **Package (13.1):** the keytab tool is a second framework-dependent publish into `/usr/lib/admng-watchdog`,
  `/usr/sbin/admng-watchdog-keytab` a relative symlink; `admng-watchdog-keytab.service/.timer` ship in
  `/usr/lib/systemd/system`, `postinst` enables and starts the timer, `prerm` stops it, `postrm` masks/purges it.
  B1 fix: `packaging/admng-watchdog/bin/` (the wrapper and `admng-watchdog-functions`) was matched by `.gitignore`'s
  `bin/` and never committed – `build-deb.sh` and the CI job could not have worked from a clean clone; now committed
  with a `.gitignore` exception.
- **Lab findings (Samba):** the `ldap/` SPN fix (9cee61d) is applied to the running lab (`ldap/vm.contoso.test`
  present); the computer account reads its own `msDS-KeyVersionNumber`, `tokenGroups` and DACL (SD flags 7) over
  GSSAPI; `init` changes the password right after the admin reset (minPwdAge exemption – `otpActive` is not
  reproducible on Samba and is covered by unit tests); with one DC, `replicationPending` was produced with an
  unreachable second KDC; a Negotiate exchange (`NegotiateAuthentication`, the API's acceptor path) succeeded with the
  cached ticket of the previous kvno and with a fresh ticket of the new kvno after `rotate`, and the open LDAP session
  kept working, all without restart.
- **Verification in this milestone:** `dotnet build AdMng.slnx` 0 warnings; Watchdog.Core.Tests and
  Watchdog.Service.Tests green on Linux; on the Samba lab `KeytabLabTests` (3, with `keytab-lab.sh env`) and
  `GssapiTests` (4, with `env --admin`) pass, and so do the full suites (Watchdog integration 28 passed, 2 skipped –
  `KerberosNegotiateTests` need `ADMNG_TEST_KERBEROS`; Directory integration 249 passed); the package install test
  (`test-deb-install.sh` with the new keytab checks) passed in an `ubuntu:24.04` container (exposure 1.4) – Debian 12
  and 13 are the CI job's.
- **Left for later milestones:** the Status tab section „Konto usługi” and the Host values in AD Manager (B7); the
  `CLAUDE.md` layout row for `src/AdMng.Watchdog.Keytab` (B8); `otpActive` against a real Windows DC (Q5) and a real
  second DC for `replicationPending` (B9 pilot); the struct offsets of the P/Invoke layer are LP64 (x86_64 and aarch64)
  but only x86_64 was run.

---

## 22. Risks and open questions

What only the pilot on real Windows DCs can prove is marked **(pilot)**.

| # | Question / risk | Plan |
|---|---|---|
| Q1 | **(pilot)** Do WS2025 (DC01) and WS2022 (DC02) WinRM forwarders request `HTTP/admng-wd.contoso.com` or `HOST/…`? | acceptance answered in the lab: the MIT acceptor bound to `HTTP/…` accepts tickets for any SPN of the account (4.3); the pilot only records which SPN each DC version requests (`klist -li 0x3e4`) |
| Q2 | **(pilot)** Does a customer GPO set WinRM client `TrustedHosts=*` on DCs (forwarders then send Negotiate)? | the pinned OpenWEC commit has SPNEGO; check `winrm get winrm/config/client` on both DCs |
| Q3 | **(pilot)** Events lost across an `openwecd` restart with Kerberos and a WS2025 source (upstream issue #291, reproduced only with TLS)? | restart-under-load test; record result |
| Q4 | **(pilot)** LDAPS + channel binding on WS2025 (`SASL_CBINDING`) | keep LDAPS optional; default 389 GSSAPI |
| Q5 | **(pilot)** Can the computer account change its own password right after the admin set the one-time password (minPwdAge 1 day; Samba allowed it)? | `init` fallback `otpActive` already designed |
| Q6 | **(pilot)** Does `Data[@Name='Status']!='0x0'` filter 4776 correctly (field name, value format)? | validate with `Get-WinEvent -FilterXml` on both DCs before loading |
| Q7 | **(pilot)** 4624 type 3/10 and 4776 volume per hour on DC01/DC02 → Security log size, WEF bandwidth, ingest rate | measure for one week; size logs to cover the longest tolerated collector outage |
| Q8 | Shared keytab OpenWEC + API: accept the silver-ticket risk (4.3)? | **closed** (user decision 2026-10-20, ADR-0009): the keytab is shared; mitigations = API only from the admin subnet (nftables + `AllowedRemoteSubnets`, loopback not exempt – 6.1), isolation of the `openwec` user and no outbound connections for it (4.3, 12.2, 16). Admin role only from PAW addresses: **closed – no** (user decision 2026-10-20): Watchdog admin operations are allowed from the whole admin subnet, and a forged admin ticket working from any host in it (helpdesk and Viewer workstations included) is part of the accepted shared-keytab risk (4.3 item 1) |
| Q9 | Does the customer's hypervisor back up/restore the vTPM state? Without it a restore loses the Data Protection key ring (secrets re-entered). | document; `reset-secrets-key` recovery |
| Q10 | Can a dMSA be created below a computer object (computer default SD gives SELF CreateChild)? | **closed**: no – the effective possible superiors of a dMSA, inherited along `computer` → `user` → `organizationalPerson` → `person` → `top`, are `domainDNS`, `organizationalUnit`, `container`, `builtinDomain`, `organization` and `lostAndFound` (9.1; verified on the Samba schema for the inherited part, MS-ADSC for the dMSA class itself); the audit script (A.4) computes the set from each forest's schema and audits every object of those classes |
| Q11 | **(pilot)** Default SPNs and `msDS-KeyVersionNumber` after `New-ADComputer -ServicePrincipalNames …`; whether `New-ADComputer -AccountPassword` honours the domain password policy for computer accounts | `setspn -L`, `Get-ADComputer -Properties *` in the lab |
| Q12 | Files driver without `fsync`: a host power loss after the ACK can lose seconds of events | accepted (VM on UPS-backed storage); optional upstream contribution of an fsync option |
| Q13 | **(pilot)** `tokenGroups` of a non-protected account readable by `ADMNG-WD$` after the A.5 grants in the customer's hardening | per-class `GR` grants (6.2); Debian test in the instructions (2.6); self-check code |
| Q14 | **(pilot)** Self-writes allowed to the computer account on Windows (enctypes, SPN, dNSHostName) match Samba | `watchdog-self` alerts regardless |
| Q15 | `ProcSubset=pid` / `@resources` filter compatibility with the .NET 10 runtime | B1 test; relax with a recorded reason |
| Q16 | smbclient tar mode (`-Tc -` with a file list) on Debian 12 (Samba 4.17) and 13 (4.22) | B5; per-file `get` fallback |
| Q17 | **(pilot)** Real GPO content of the customer (Default Domain Policy has `GptTmpl.inf`; Samba GPOs are empty) | fixtures from the lab WS2025 DC; pilot run in rollout mode |
| Q18 | Enterprise CA present? Web Server template published and who may enrol? | `certutil -config - -ping` in the pilot; self-signed + fingerprint otherwise |
| Q19 | WS2025 evaluation needs online activation within 10 days (lab without internet shuts down) | lab recipe notes it |
| Q20 | **(pilot)** Which 4624 pattern do ADWS changes (PowerShell AD module, ADAC) leave on WS2025/WS2022 – loopback only, or also a network logon with the client address? | instructions test 18; decides whether the `AdwsSourceCorrelator` (7.8) is built |
| Q21 | **(pilot)** Which actor do 4742/4723 show for the kpasswd change of `ADMNG-WD$` on WS2025/WS2022? | the `watchdog-self` exclusion no longer depends on it (9.3); record for the docs |
| Q22 | **(pilot)** Does the Deny ACE for SELF (4.1) leave the kpasswd change of the own password working on Windows DCs? | the instructions apply the ACE before the first change (3.6 tests it at once); fallback: remove the ACE and keep `watchdog-self` as the only control |
| Q23 | Contain the Internet egress of `admng-watchdog` (TCP 80/443 to any address, for Teams and the SMTP revocation check – 4.3, 16)? | **closed** (user decision 2026-10-20, ADR-0009): direct egress on TCP 80/443 only for `admng-watchdog` (and `root`/`_apt` for updates), no outbound proxy, Teams kept, no `IPAddressDeny=` on `admng-watchdog.service` (12.1); `openwec` has no egress at all (12.2, 16). Accepted residual risk: code execution as `admng-watchdog` can send the keytab, channel secrets and history to any Internet host |
| Q24 | Certificate for `ADMNG-WD$` through templates that allow Domain Computers to enroll (PKINIT outlives the 30-day rotation)? | **closed – accepted risk** (user decision 2026-10-20, ADR-0009): no Deny ACEs on the certificate templates and no template health check (4.1, 4.7); remaining controls: per-CA check after `init` (4.2 step 3) and certificate revocation in the compromise recovery (4.1) |

Further risks: OpenWEC is a small upstream project pinned to an unreleased commit (mitigation: own build, source
attached, upgrade only through reviewed commits); the WEF path makes attribution depend on DC GPO configuration that
the Watchdog cannot verify directly (mitigation: per-DC silence and coverage health); `smbclient` output format changes
between Samba versions (mitigation: parser tests on both Debian versions).

---

## 23. Documentation deliverables and amendments (B8)

- `docs/dev/watchdog-spec.md`: D1, D5, D8 (decision table), section 2 (diagram), 3.3 (file list), 5.4 (Linux default
  `MaxDatabaseMb` 4096), 8.3 (`retentionBelowFloor`), 8.4 (bootstrap incl. `Retention.MinEventsDays`,
  `Collector.MaxLogonRecordsPerDcPerMinute`), 11.1 / 11.4 / 11.7 (collector mode, store lookups, reasons, ingest
  budget), 12.4 (built-in rules), 7 (catalog rows; retention decreases as Critical `WatchdogConfigurationChanged`;
  SACL changes in `SecurityDescriptorChanged`, 9.4), 16 (migrations 0004/0005, retention of logon records, backup
  skipped on low space), 17.3 (Linux role resolution), 18 (secrets), 19 (hosting – replaced by sections 11–15 here), 21
  (packages: `Microsoft.Extensions.Hosting.Systemd`), 22–24 (tests, lab, CI). Each amended section points here.
- `docs/adr/0008-…`: a short "Zmieniona przez ADR-0009" note in the header (B8).
- `CLAUDE.md`: rule 1 exception text (Linux data folder, OpenWEC output, GPO snapshots), rule 2 exception (keytab of the
  service account, systemd credential), layout table (`AdMng.Watchdog.Keytab`, `packaging/`), commands (`build-deb.sh`).
- `docs/WATCHDOG-LINUX.md` (Polish deployment guide, appendix A and B written out): updated with the real packages;
  `docs/WATCHDOG.md` (Polish feature description): Windows installation chapters replaced by a pointer; the 0.2.0
  Windows guide stays reachable through the 0.2.0 tag.
- `README.md`, `docs/ARCHITECTURE.md`, `docs/ROADMAP.md`, release notes 0.3.0.

---

## Appendix A – AD and DC prerequisites (binding input for the Polish instructions)

All commands on a Tier 0 PAW with the ActiveDirectory module, as a Tier 0 admin. Replace the OU names by the customer's
existing Tier 0 OU.

### A.1 DNS

```powershell
Get-DnsServerResourceRecord -ComputerName DC01.contoso.com -ZoneName 'contoso.com' -Name 'admng-wd' -ErrorAction SilentlyContinue   # must be empty
Add-DnsServerResourceRecordA -ComputerName DC01.contoso.com -ZoneName 'contoso.com' -Name 'admng-wd' -IPv4Address '10.0.1.25' -CreatePtr
Resolve-DnsName admng-wd.contoso.com; Resolve-DnsName 10.0.1.25     # exactly one A record, PTR (never a CNAME)
```

The step is idempotent in the instructions (2.4): an existing record pointing to another host (the 0.2.0 server) is
never complemented by a second A record – DNS round-robin would send half of the AD Manager and WEF connections to the
old host, whose HTTP SPN has moved; the record is switched at cut-over (A.10).

### A.2 Service account

```powershell
$ou  = 'OU=Service Accounts,OU=Tier 0,OU=Admin,DC=contoso,DC=com'
# one-time password: 30 random bytes as Base64 (40 characters) – PowerShell 7; on 5.1 use RNGCryptoServiceProvider
$plain = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(30))
$otp   = ConvertTo-SecureString $plain -AsPlainText -Force
New-ADComputer -Name 'ADMNG-WD' -SamAccountName 'ADMNG-WD$' -Path $ou `
    -DNSHostName 'admng-wd.contoso.com' `
    -ServicePrincipalNames 'HTTP/admng-wd.contoso.com','HOST/admng-wd.contoso.com','HOST/ADMNG-WD' `
    -KerberosEncryptionType AES128,AES256 `
    -AccountNotDelegated $true -PasswordNotRequired $false `
    -AccountPassword $otp -Enabled $false `
    -Description 'AdMng Watchdog (Debian admng-wd, 10.0.1.25) - konto usługi Tier 0, tylko odczyt'
# still disabled: Deny ACE for SELF (A.5); no certificate-template Deny ACEs (accepted risk, 4.1)
Enable-ADAccount -Identity 'ADMNG-WD$'
$plain    # hand over once to the Debian administrator, then close the session
Get-ADComputer ADMNG-WD -Properties msDS-SupportedEncryptionTypes,userAccountControl,servicePrincipalName,mS-DS-CreatorSID,memberOf |
    Format-List Name,DistinguishedName,msDS-SupportedEncryptionTypes,userAccountControl,servicePrincipalName,mS-DS-CreatorSID,memberOf
setspn -L ADMNG-WD
setspn -Q HTTP/admng-wd.contoso.com       # only CN=ADMNG-WD (0.2.0 migration: create without HTTP/, move it at cut-over – A.10)
```

Expected: `msDS-SupportedEncryptionTypes` 24, `userAccountControl` 1052672 (0x101000 = WORKSTATION_TRUST_ACCOUNT +
NOT_DELEGATED; 1052704 means PASSWD_NOTREQD is still set → `Set-ADAccountControl -PasswordNotRequired $false`),
`mS-DS-CreatorSID` empty, `memberOf` empty (primary group Domain Computers). After
`admng-watchdog-keytab init` on Debian: `pwdLastSet` fresh, `msDS-KeyVersionNumber` increased.

### A.3 Role groups

```powershell
$g = 'OU=Groups,OU=Tier 0,OU=Admin,DC=contoso,DC=com'
New-ADGroup -Name 'AdMng-Watchdog-Admins'  -GroupScope Global -GroupCategory Security -Path $g -Description 'AdMng Watchdog - administratorzy (reguły, kanały)'
New-ADGroup -Name 'AdMng-Watchdog-Viewers' -GroupScope Global -GroupCategory Security -Path $g -Description 'AdMng Watchdog - podgląd i potwierdzanie zdarzeń'
(Get-ADGroup 'AdMng-Watchdog-Admins').SID.Value      # -> Authorization.AdminSids
(Get-ADGroup 'AdMng-Watchdog-Viewers').SID.Value     # -> Authorization.ViewerSids
```

Members: the people's administrative accounts used on the 10.0.20.0/24 workstations (they need not be Domain Admins).
`ManagedBy` stays empty; only Tier 0 admins can change membership; the Watchdog treats both groups as privileged roots
(membership changes alert). `ADMNG-WD$` is never a member of anything but Domain Computers.

### A.4 Tier 0 OU and BadSuccessor audit (scripts `Test-AdMngTier0Delegation.ps1` and `Test-AdMngDmsaCreators.ps1`, listed in full in the instructions 2.2 and 2.9)

Builds the Tier 0 SID set (`S-1-5-18`, `S-1-5-32-544`, `S-1-5-9`, domain `-512`, `-516`, `-526`, forest root `-498`,
`-518`, `-519`, `-527`; DnsAdmins in the dMSA script); maps `schemaIDGUID` and `rightsGuid` to names; for the Tier 0 OU,
every object below it (including computers and containers) and `CN=AdminSDHolder,CN=System,DC=contoso,DC=com` reads
`nTSecurityDescriptor` (`Get-Acl "AD:\<DN>"`) and reports: owners outside the Tier 0 set; every Allow ACE (explicit and
inherited) with GenericAll, GenericWrite, WriteProperty, WriteDacl, WriteOwner, CreateChild, DeleteChild, Delete,
DeleteTree, ExtendedRight or Self held by a SID outside the Tier 0 set, with its object type name (e.g.
User-Force-Change-Password `00299570-246d-11d0-a768-00aa006e0529`, msDS-DelegatedManagedServiceAccount
`0feb936f-47b3-49f2-9386-1dedc2c23765`); flags Account Operators (`S-1-5-32-548`) and Print Operators (`S-1-5-32-550`)
as "keep empty" and annotates default entries (SELF, CREATOR OWNER, Everyone change-password, Authenticated Users
Send-To on groups). It also walks the **ancestors** of the Tier 0 OU up to and including the domain head (column
`Przodek`): an owner outside the Tier 0 set, and every explicit Allow ACE (any inheritance) held by a SID outside the
set with GenericAll, WriteDacl, WriteOwner, GenericWrite, or WriteProperty on all properties or on `gPLink`
(`f30e3bbe-9ff0-11d1-b603-0000f80367c1`) / `gPOptions` (`f30e3bbf-9ff0-11d1-b603-0000f80367c1`; neither is in a
property set – lab). Such an owner or holder can add an inheritable ACE that flows into the Tier 0 OU, and a gPLink
write links a GPO that applies to every computer below (e.g. Tier 0 PAWs), although nothing below the OU shows it; a
fresh domain yields no ancestor rows (lab: the domain head and a new OU carry only Tier 0 SIDs with such rights). The dMSA audit (`Test-AdMngDmsaCreators.ps1`) first computes the effective possible superiors of the
dMSA class by walking `subClassOf` up to `top` and collecting `systemPossSuperiors` + `possSuperiors` of every class
(falls back to `computer` when the schema has no dMSA class) – on the Windows Server 2025 schema `domainDNS`,
`organizationalUnit`, `container`, `builtinDomain`, `organization`, `lostAndFound` – and then reads the SD of every
object of those classes; ACEs that apply to "this object only" on the domain head (e.g. Exchange Windows Permissions
with WriteDacl) never show on an OU, and `CN=Builtin` is easy to miss otherwise. Akamai's
`Get-BadSuccessorOUPermissions.ps1` checks OUs only. Also: DC01 must have the August 2025 or later cumulative update
(CVE-2025-53779).

### A.5 Read rights, MachineAccountQuota

As `docs/WATCHDOG.md` 4.3 with `$a = 'CONTOSO\ADMNG-WD$'` (per-class `GR`, domain head, AdminSDHolder, Password Settings
Container, `LCRP` on Deleted Objects after `/takeownership`). The per-class `GR` on `user`/`inetOrgPerson`/`computer`
also makes `tokenGroups` readable in hardened domains – no Windows Authorization Access Group (6.2). Verified from
Debian with a non-protected account (instructions 2.6). Deny ACE on the own object (instructions 2.5):
`SELF: CreateChild, DeleteChild, Self, WriteProperty` (Deny), added while the account is still disabled (A.2, 4.2).
No Deny ACEs on the certificate templates (user decision 2026-10-20 – accepted risk, 4.1). With an enterprise CA
(instructions 2.1/2.5), after `init` the per-CA
`certutil -view -restrict 'RequesterName=CONTOSO\ADMNG-WD$,Disposition=20'` query must return no certificate; the
same query starts the revocation step of the compromise recovery (4.1).
MachineAccountQuota = 0 is a deployment prerequisite: list who joins computers today (`mS-DS-CreatorSID` of existing
computers) and delegate computer creation to them (`dsacls "<computers OU>" /I:T /G "CONTOSO\<group>:CC;computer"`),
also on the default computers container `(Get-ADDomain).ComputersContainer` (usually `CN=Computers`, where joins
without an OU land) unless `redircmp` points it to the OU – or pre-stage the objects with the joining group as "the
user or group that can join this computer" (Reset Password, validated writes to DNS host name and SPN, Account
Restrictions; since KB5020276 re-use of an existing computer account is allowed for its creator, for accounts created
by Domain Admins and for owners in the "Domain controller: Allow computer account re-use during domain join" list);
then `Set-ADDomain -Identity contoso.com -Replace @{'ms-DS-MachineAccountQuota'='0'}` (instructions 2.8). No grant
where the group already has the right: the check counts only Allow ACEs with CreateChild, object type empty or
`computer`, no inherited object type and an inheritance type that covers the object itself (`None`, `All`,
`SelfAndChildren`), explicit **or inherited** – an explicit "Full Control – Descendant Computer objects" ACE (the usual
Delegate Control result) is inherit-only with inherited object type `computer` and does not allow creating computers
in the OU. The rollback removes exactly the granted ACE with `RemoveAccessRuleSpecific` and re-runs the check
(expected `False` where the grant was made). `RemoveAccessRule` MUST NOT be used: when the group also holds such an
inherit-only ACE without object type, .NET refuses the removal (same `RemoveQualifiedAces` conflict as in A.7),
returns `false` and keeps the grant. Never `dsacls /R` either – it deletes every ACE of the group on the object, e.g.
its LAPS or BitLocker read delegations.

### A.6 Logon restrictions (domain GPO "AdMng - ograniczenia konta Watchdog")

Computer Configuration → Policies → Windows Settings → Security Settings → Local Policies → User Rights Assignment:
"Deny log on locally", "Deny log on through Remote Desktop Services", "Deny log on as a batch job", "Deny log on as a
service" = `CONTOSO\ADMNG-WD$`. User rights assignments do not merge: per right, the highest-precedence GPO that
defines it wins, and it replaces the local list. Therefore the new domain-linked GPO always defines **all four** rights
(plus `BUILTIN\Guests` in "Deny log on locally" to keep the local default that denies the Guest account), and the
account is **also** added to every existing GPO that defines any of the four rights (e.g. an OU-linked tiering GPO,
which wins on the computers it covers). The check (`secedit /export`) runs on one machine per such OU. Applied after
the keytab exists (instructions 2.5 – the one-time password must live as briefly as possible).

### A.7 DC GPO for WEF, audit policy, SACLs

Section 7.10 plus `docs/WATCHDOG.md` 4.5 (audit subcategories) and 4.6 (SACLs incl. dMSA and GPC), plus an audit
entry "Everyone, success, write/delete/create/delete-child/DACL/owner, this object and all descendants" on
`CN=Public Key Services,CN=Services,<configuration NC>` (Enterprise Admins in a multi-domain forest; optionally
`CN=Sites`) – the configuration NC does not inherit the domain SACLs, so PKI template changes would otherwise stay
`SACL_MISSING`. Creation auditing (5137) is evaluated on the parent's SACL, so the domain head also gets, for every
watched class, an object-specific audit entry "Everyone, success, CreateChild + DeleteChild, objectType = the class,
this object and all descendants" – it covers OUs, containers (`CN=Managed Service Accounts`, `CN=Users`,
`CN=Policies,CN=System`, the Password Settings Container, `CN=System` for trusts) and the domain head itself. The
instructions save the pre-deployment state first (`auditpol /backup` per DC, the SACL SDDL of every touched object)
and roll back **only what they added**: the SACL rollback rebuilds the same `ActiveDirectoryAuditRule` objects,
removes each with `RemoveAuditRuleSpecific` (exact mask, flags, object type, inherited object type), re-adds whatever
an identical-key entry held before (the snapshot is only a reference – a full restore would delete entries added
later, e.g. MDI's) and then verifies: per object, no explicit ACE equal to a rule of 2.11 may remain unless the first
backup had it (`pozostało wpisów z 2.11: 0`). `RemoveAuditRule` MUST NOT be used: for DS ACLs .NET refuses to remove
an entry when another entry of the same SID shares object-typed access bits (CreateChild/DeleteChild) with a
conflicting object type or inherited object type (`CommonAcl.RemoveQualifiedAces` – `GetAccessMaskForRemoval` /
`GetInheritanceFlagsForRemoval`), returns `false` and changes nothing – with the two per-class families of 2.11 on
the domain head, 22 of the 23 removals fail (replayed with the dotnet/runtime `ACL.cs`). Removing the audit GPO does
**not** revert advanced audit policy (it stays in the local audit configuration), so the audit rollback first sets
every subcategory in the GPO back to its value from the backup (not "No Auditing"), runs gpupdate, compares
`auditpol /get` with the backup and only then removes the GPO. The backup CSV is read by column position, not by
header name: `auditpol /backup` writes the header row in the DC's UI language (e.g. German
`Unterkategorie-GUID`), so a header-based filter silently returns nothing on a non-English DC. The alternative
`auditpol /restore` on each DC after the removal restores the **whole** policy as of the backup (every subcategory and
option, also changes made later by other GPOs or tools), so the instructions save the current state first, run
gpupdate right after the restore and compare `auditpol /get /category:*` with the saved state. The Remote
Event Log Management firewall rules and the Event Log Readers membership of the 0.2.0 guide are **not** needed any
more (A.10).

### A.8 Certificate (company CA variant)

Check: `certutil -config - -ping` (or `certutil -TCAInfo`). With a CA: CSR on Debian (6.3), `certreq -submit` with the
Web Server template by a Tier 0 admin, copy `admng-wd.cer` + chain to `/etc/admng-watchdog/tls/admng-wd.crt` (leaf
first), key to `admng-wd.key`, `systemctl restart admng-watchdog`. Without a CA: keep the generated self-signed
certificate and confirm its fingerprint in AD Manager on each workstation's first connection.

### A.9 Debian side (order)

Same order as the instructions (`docs/WATCHDOG-LINUX.md` 0.4 and chapters 3–6):

1. Base system, `/etc/hosts`, swap off for good (`systemd.gpt_auto=0`, checked after a reboot – 16),
   unattended-upgrades, `packages-microsoft-prod.deb` + runtime, tpm2-tss libraries whenever a TPM is present
   (instructions 3.1); admin accounts and SSH (3.2); the system users `openwec` and `admng-watchdog` and nftables with
   the timed rescue (3.3 – early, before any service listens; SSH only from `PAWS`; 80/443 only for `root`, `_apt` and
   `admng-watchdog`; transient timer with `RemainAfterElapse=no`, rules loaded only with `&&` after an armed timer);
   DNS and chrony with `maxdistance 16.0` (3.4).
2. Private `/etc/admng-watchdog/krb5.conf`, `ldap.conf`, `smb-client.conf` created by hand (3.5); `dpkg` later keeps
   them (answer `N` at the conffile prompt).
3. Keytab right after A.2 – the account is enabled only after its Deny ACE for SELF is in place (3.6: manual MIT
   procedure until the package exists – the same block, which reads an existing keytab first, also serves the manual
   change and the minimum-password-age fallback; afterwards `sudo admng-watchdog-keytab init`
   + `verify`).
4. AD part two (right after the keytab: no certificate issued to the account (A.5), the logon-restriction GPO of A.6;
   then A.4–A.7), then certificate (3.8) and backups (3.9).
5. `apt install ./admng-openwec_…~deb13_amd64.deb ./admng-watchdog_…_amd64.deb`; the `openwecd` drop-in with
   `IPAddressAllow=` = the DC addresses (12.2) (instructions 4.2–4.3).
6. `sudo admng-watchdog print-wec-subscription --dc 'DC01$@CONTOSO.COM' --dc 'DC02$@CONTOSO.COM' |
   sudo install -m 0644 /dev/stdin /etc/openwec/subscriptions/admng-dc-security.toml` – on the shipped
   `appsettings.json` (reads only `Watchdog:Collector`, 7.3); validate the query on the DCs (7.3);
   `sudo -u openwec openwec subscriptions load -y /etc/openwec/subscriptions/admng-dc-security.toml` (the single
   file, 7.1); `sudo systemctl start openwecd` and check WEF arrival (instructions 4.4–4.6).
7. Edit `appsettings.json` (SIDs from A.3, servers, URL; **not** `Collector:SubscriptionUuid`, which must stay equal
   to the loaded subscription's uuid); `sudo admng-watchdog --check-config` (instructions 5.2).
8. `sudo systemctl start openwecd admng-watchdog`; `sudo admng-watchdog --health`; `journalctl -u admng-watchdog`;
   AD Manager: address `https://admng-wd.contoso.com:8443`, confirm the fingerprint (instructions 5.3, 6).

### A.10 Migration from the 0.2.0 Windows service (optional)

Preparation while the 0.2.0 Windows service keeps running (instructions 0.4, "Migracja z wersji 0.2.0"): the 0.2.0 role
groups already exist – move them to the Tier 0 OU (`Move-ADObject`, SIDs unchanged) instead of A.3; the Debian host gets
a temporary address (e.g. 10.0.1.26) while the A record `admng-wd` still points to the old server, so until cut-over
every `ssh`/`scp`/`Test-NetConnection` of instructions 3.2, 3.3 and 3.8 targets that address, not the name (after
cut-over the first connection by name shows a new host key: compare it with the console, `ssh-keygen -R` the old one); A.2 creates
`ADMNG-WD` **without** `HTTP/admng-wd.contoso.com` (DCs reject the duplicate SPN held by the gMSA; the keytab contains
`HTTP/` anyway – same account key), and the manual keytab procedure reads the kvno from `host/admng-wd.contoso.com` (a
ticket for `HTTP/` would still come from the gMSA); the WEF GPO is created with its link disabled. Cut-over: stop the
Windows service; copy `%ProgramData%\AdMng\Watchdog\data\watchdog.db` (after `sqlite3 … "PRAGMA
wal_checkpoint(TRUNCATE)"` or with the service stopped) to `/var/lib/admng-watchdog/watchdog.db`
(`admng-watchdog:admng-watchdog 0600`); do **not** copy `keys\`. Move `HTTP/admng-wd.contoso.com` from the gMSA to
`ADMNG-WD` (`setspn -D … svc-admngwd$`, `setspn -S … ADMNG-WD$`); replace the A and PTR records with the Debian address
– never add a second A record (A.1) – or move the old address to the Debian host after the old server is off; enable the
WEF GPO link; remove the gMSA's Event Log Readers membership and the DC firewall rules for remote event log access,
start the Linux service, re-enter the SMTP password and Teams URL in AD Manager. Then remove every right of the old gMSA
(instructions 2.12, "Migracja z wersji 0.2.0"): `dsacls <DN> /R 'CONTOSO\svc-admngwd$'` on the domain head,
AdminSDHolder (propagated to every protected account), the Password Settings Container and Deleted Objects; its Event
Log Readers (`S-1-5-32-573`) membership; NETWORK SERVICE (`CN=S-1-5-20,CN=ForeignSecurityPrincipals,…`) in Event Log
Readers when 0.2.0 variant B was used (`ChannelAccess` replaces it); disable the `RemoteEventLogSvc*` firewall rules on
the DCs; detach a 0.2.0 subscription-manager GPO. Uninstall the Windows service (`Uninstall-AdMngWatchdog.ps1
-RemoveData` after verifying the Linux service) and delete the gMSA (`Remove-ADServiceAccount svc-admngwd`) – otherwise
it stays a live credential on the old host – and the 0.2.0 host group (`Remove-ADGroup AdMng-Watchdog-Hosts`).

---

## Appendix B – Lab recipe (written out in `docs/WATCHDOG-LINUX.md` chapter 1)

- VM 1: Windows Server 2025 evaluation (180 days; online activation within 10 days, otherwise it shuts down
  periodically). `Install-WindowsFeature AD-Domain-Services -IncludeManagementTools`;
  `Install-ADDSForest -DomainName contoso.com -DomainNetbiosName CONTOSO -ForestMode WinThreshold -DomainMode WinThreshold -InstallDns`
  (functional level 2016, as the mixed 2025/2022 production forces). Static IP 10.0.1.10, name DC01; reverse zones
  for 10.0.1.0/24 and 10.0.20.0/24 (PTR of the admin workstation = source name).
- VM 2: Debian 13 netinst ("SSH server" + "standard system utilities"), 2 vCPU, 4 GB RAM, 60 GB disk, vTPM optional
  (tests the `tpm2+host` credential variant), static IP 10.0.1.25, DNS → 10.0.1.10, chrony → DC01.
- VM 3 (**required** – added to the agreed "1 DC + 1 Debian", confirmed by the user on 2026-10-20): ADM-WS01,
  Windows 11 Enterprise evaluation (or WS2025) with RSAT, 10.0.20.15 – AD Manager (Windows only) and the source of
  pilot changes (changes made on the DC show "lokalnie na DC"). A flat lab network with ADM-WS01 at 10.0.1.50 and
  `AllowedRemoteSubnets`/nftables `ADMIN_NET` `10.0.1.50/32` and `PAWS` `10.0.1.50` is the simplest variant.
- Optional: DC02 – Windows Server 2022 evaluation (10.0.1.11) for the replication, failover and WinRM-version tests;
  `smtp` – a small Debian VM (10.0.1.30) with Mailpit (pinned version and SHA-256 of `tools/testlab/watchdog`,
  STARTTLS, self-signed certificate pinned in the SMTP settings) for the e-mail tests; an enterprise CA on DC01 (lab
  only, instructions 1.7) to exercise certificate variant A and the per-CA certificate check and revocation of
  instructions 2.5; Teams only with a Microsoft 365
  tenant.
- Hypervisor notes (instructions 1.1): Hyper-V internal switch + `New-NetNat` for the flat 10.0.1.0/24 network (the
  NAT does not answer DNS – DC01 forwards to a public or company resolver); a two-subnet lab needs a router without
  NAT, otherwise the source address seen by Debian and the DCs is the router's; Debian on Hyper-V needs Generation 2
  with the "Microsoft UEFI Certificate Authority" Secure Boot template.
- Then appendix A in order; lab scenario: create/modify/delete users, a GPO edit per category, a dMSA creation and
  `msDS-ManagedAccountPrecededByLink` change, MachineAccountQuota 0 → 10, a change of `ADMNG-WD$`'s
  `msDS-SupportedEncryptionTypes` – each must arrive in AD Manager with actor and source.

---

## Appendix C – Linux lab facts (Samba lab, 2026-10-19)

Verified: MIT `kinit -k` from a keytab gives an AES256 TGT; with `KRB5_CLIENT_KTNAME` and an empty ccache gssapi
(curl, S.DS.P through Cyrus) acquires the TGT itself; self password change of the computer account with kvno + 1 and
appended keys works, a running acceptor and clients continue without restart, removing the previous kvno breaks live
tickets; `NOT_DELEGATED` is effective (TGT flags `RIA` instead of `FRIA` with `kinit -f`) but can be set only by an
admin; AES-only enctypes effective, and the account can raise its own `msDS-SupportedEncryptionTypes`; LDAP SASL GSSAPI
default SSF 256 (needs `SASL_NOCANON` or correct PTRs; `LdapSessionOptions.Signing/Sealing` and
`VerifyServerCertificate` throw on Linux; in-process environment changes do not reach libldap); over LDAPS libldap
still negotiates a SASL layer (`maxssf=0` needed for AD); a computer account can read uSNChanged, users/groups/GPCs,
configuration, wellKnownObjects, SDs (owner + group + DACL with SD flags 7), tokenGroups (lab-specific Pre-Windows 2000
membership) and replPropertyMetaData, but not deleted objects, the Password Settings Container or `CN=Keys` without
delegation; smbclient SMB 3.1.1 with signing required, encryption active and Kerberos as `ADMNG-WD$` lists and streams
SYSVOL (`-N`, `-E`, `KRB5CCNAME`, private `-s` file, exit code); Kestrel Negotiate with `KRB5_KTNAME` authenticates
users and the computer account, rejects NTLM, yields only a name claim, turns GSS failures into HTTP 500 unless
`OnAuthenticationFailed` is handled, breaks when the client reaches the service through an alias (CNAME / hosts alias);
`EnableLdap` roles are spoofable by a look-alike group; MIT replay cache in `/var/tmp`;
`Microsoft.Extensions.Hosting.Systemd` 10.0.x sends `READY=1`/`STOPPING=1`. Samba gaps (to be covered by the pilot):
no `ldap/` SPN on the lab DC, no constructed `msDS-ReplAttributeMetaData`, no dMSA classes, empty default GPOs, the
minPwdAge exemption for the computer account, single DC (no replication).
