**English** · [Polski](../../adr/0009-watchdog-na-linuksie.md)

# ADR-0009: The AdMng Watchdog service on Linux (Debian + systemd)

- Status: **Accepted** (2026-10-20). Based on the arrangements of 2026-10-19; the changes relative to them were
  confirmed by the user on 2026-10-20 (section "Arrangements confirmed on 2026-10-20"), and on the same day the user
  settled four further matters (section "Arrangements confirmed on 2026-10-20 – second round"). The technical details
  will be confirmed by the pilot on Windows Server 2025 and 2022 domain controllers
  (`docs/dev/watchdog-linux-design.md`, chapter 22)
- Date: 2026-10-19 (corrections after review and user confirmations: 2026-10-20)
- Amends: [ADR-0008](0008-watchdog-data-in-separate-folder.md) (the location and protection of the service's data,
  secrets, logs – details in the section "Amendment to ADR-0008") and rules 1 and 2 in `CLAUDE.md` (OpenWEC output
  files, the service account keytab)
- Implementation specification: [`docs/dev/watchdog-linux-design.md`](../../dev/watchdog-linux-design.md) (binding for
  Stage B)
- Deployment guide: [`docs/WATCHDOG-LINUX.md`](../WATCHDOG-LINUX.md)

## Context

Release 0.2.0 delivers AdMng Watchdog as a Windows service: a gMSA account, Kestrel or HTTP.sys, secret keys protected
with DPAPI-NG, reading DC security logs over RPC (Event Log Readers), installation with a PowerShell script, the
Application log.

The user decided that the service is to run ultimately **on Linux with systemd only** ("the service finally on
Linux – systemd"), as an extended version with a change history "who, what, when, from where, was → is". Arrangements
of 2026-10-19:

- a Debian 13 server (the package also for Debian 12) `admng-wd.contoso.com` (10.0.1.25), **not joined to the
  domain**; the server administrators are 1–3 Tier 0 people (SSH keys, no root login);
- a **computer**-type service account with a keytab, in the existing Tier 0 OU, **without administrative
  permissions**, with no interactive logon, AES only, random password changed every 30 days; the role groups
  `AdMng-Watchdog-Admins` and `AdMng-Watchdog-Viewers`;
- event history for **3 years**, DC log records kept for matching for 30 days; attribution covers the account
  **and** the source computer/address (network logons 4624, failed ones 4771/4776);
- events from the DCs through **Windows Event Forwarding** to **OpenWEC** on the same server (our own receiver – not
  now);
- full tracking of **GPO content** (SYSVOL, "setting: was → is");
- a new critical rule **BadSuccessor** (dMSA);
- secrets through **systemd-creds** (TPM2 when present, otherwise the host key – with no decision by the user);
- an API certificate from the corporate CA or a self-generated one with the fingerprint confirmed in AD Manager;
- local backups (7 days), a recommendation of a VM-level backup given once.

On 2026-10-20 the user confirmed five changes relative to these arrangements (section "Arrangements confirmed on
2026-10-20"): the way the account is created, 48 hours for logon records, additional events, rules and requirements, a
shared keytab for the API and OpenWEC, and a third machine in the lab. After further reviews the user settled four more
matters on the same day (section "Arrangements confirmed on 2026-10-20 – second round"): no deny entries on
certificate templates (risk accepted), detection of changes to the SACLs of key objects, Watchdog administrative
operations from the entire administrators' subnet, and direct connections of the service to the Internet with no
intermediary server.

The customer's environment: Windows Server 2025 and 2022 domain controllers (functional level 2016 at most); on Server
2025 LDAP signing is required by default.

Technical limitations of Linux (checked in the Samba lab on 2026-10-19): no gMSA without an additional host identity,
no reading of Windows event logs over RPC, Negotiate authentication in ASP.NET Core returns only the account name
(without group SIDs), `LdapSessionOptions.Signing/Sealing` does not work – LDAP confidentiality must be enforced by
libldap/SASL configuration.

## Decision

1. **Platform.** The service runs on Debian 12/13 (amd64) only, as the systemd unit `admng-watchdog.service`
   (`Type=notify`, full systemd sandbox hardening), delivered as the `.deb` package `admng-watchdog`
   (depending on `aspnetcore-runtime-10.0` ≥ 10.0.13 from the Microsoft repository). The server is not joined to the
   domain: Kerberos, DNS and time (chrony from the DC) are configured manually; the service has its own `krb5.conf`,
   `ldap.conf` and `smb-client.conf` files in `/etc/admng-watchdog`.
2. **Identity: a computer account + keytab instead of gMSA.** A Tier 0 administrator creates, on a PAW workstation, the
   computer object `ADMNG-WD` (`ADMNG-WD$`) in the Tier 0 OU – disabled at first, enabled only after the deny entry for
   SELF described below – with a one-time password, the SPNs `HTTP/admng-wd.contoso.com`,
   `HOST/admng-wd.contoso.com`, `HOST/ADMNG-WD`, AES-only encryption (0x18) and the flag "account is sensitive and
   cannot be delegated". On Debian the tool `admng-watchdog-keytab` (our own, based on the MIT Kerberos library, no
   LDAP) replaces the one-time password with a random 120-character password, writes the keytab
   `/etc/admng-watchdog/admng-wd.keytab` (root:admng-keytab 0640) and changes the password every 30 days (the timer
   `admng-watchdog-keytab.timer`, checked daily). The password is not stored anywhere – only the keys (the current and
   the previous version) in the keytab. The account does not belong to any privileged group (the service refuses to
   start if it does), has the local logon, RDP, batch and service logon rights revoked, and is granted only read
   permissions in AD. The writes that every computer account has by default on its own object (child objects, SPN, DNS
   name, a few attributes) are blocked by a "Deny" entry for SELF (the service checks hourly that it still exists);
   creating computer accounts – `ms-DS-MachineAccountQuota` = 0 (a deployment condition; the right to create computer
   objects is given through delegation to those who join computers today). What remains is changing its own password,
   what any authenticated domain account can do (e.g. adding a new record in an AD-integrated DNS zone), and what any
   member of Domain Computers can do – including requesting a certificate from a template that allows it (by default
   "Computer" on every Enterprise CA). We do not add deny entries on certificate templates (the user's decision of
   2026-10-20; risk accepted – "Consequences"). We do not use msktutil or adcli (LDAP writes to its own object whose
   effects on a Windows DC could not be verified, temporary files in `/tmp`, their own package timers) – confirmed by
   the user on 2026-10-20.
3. **LDAP.** Port 389 by default, SASL GSSAPI (Kerberos) with signing and encryption enforced in `ldap.conf`
   (`SASL_SECPROPS minssf=56`, `SASL_NOCANON on`); the service checks the negotiated security level after every
   bind. LDAPS (636) is an option. Still one session = one DC, the PDC emulator preferred.
4. **API.** Kestrel + Negotiate with the keytab, Kerberos only, HTTPS 8443 only from the administrators' workstation
   subnet. The service determines the roles itself: the account name from the ticket → `objectSid` and `tokenGroups`
   from AD (cached for 5 minutes) → the existing role handling by SIDs with the `DeniedSids` list checked first. The
   `EnableLdap` feature of Negotiate authentication is not used (roles by group names can be forged – checked in the
   lab).
5. **Events from DCs: WEF through OpenWEC.** The controllers send the events themselves (a source-initiated
   subscription, a GPO on the Domain Controllers OU) to OpenWEC (cea-sec/openwec, GPL-3.0) running as a separate
   service `openwecd` from the `admng-openwec` package that we build from a pinned version of the sources. OpenWEC
   receives them over Kerberos with **the same account, the name `HTTP/admng-wd.contoso.com` and the keytab as the
   API** (a shared keytab – the user's decision of 2026-10-20; the risk and its mitigation in "Consequences"; tickets
   for `HTTP/` and `HOST/admng-wd.contoso.com` – both SPNs have the same account key, checked in the lab), writes in
   the RawJson format to one file per DC, and Watchdog reads these files with a cursor committed in the same
   transaction as the records (at-least-once delivery, duplicates skipped by (computer, record number, time)). The
   subscription covers the existing set of change events (attribution of the actor) and 4624 (network logons – type 3
   – and RDP – type 10), 4625, 4771 and failed 4776 – the source of a change (computer/IP address) is determined from
   these records. Watchdog checks that a record comes from an authenticated DC (the account, computer name and IP
   address must match) – all these fields are written by OpenWEC, so the check protects against a foreign sender, not
   against a compromised OpenWEC ("Consequences").
6. **GPO content.** The service reads the GPO folders in the SYSVOL of its DC (SMB 3, Kerberos, signing required,
   read-only, data in memory only), parses `GptTmpl.inf`, `Registry.pol`, GPP preferences, scripts, `audit.csv`
   and comments, and reports changes as "setting: was → is". The actor is inherited from the 5136 event on the GPC
   object. A `gPCFileSysPath` path pointing outside the domain's SYSVOL is a critical event and is not read.
7. **New rules.** BadSuccessor (`tier0-badsuccessor`: creation of a dMSA, a change of
   `msDS-ManagedAccountPrecededByLink` / `msDS-DelegatedMSAState` and the attributes of the superseded side) –
   critical; two additional rules confirmed on 2026-10-20: `tier0-machine-quota` (a change of
   `ms-DS-MachineAccountQuota` – critical when raised from 0) and `watchdog-self` (any change of the `ADMNG-WD$`
   account itself other than its password rotation – critical); significant changes of GPO content (`tier0-gpo`) –
   high or critical.
8. **Secrets.** `ISecretStore` unchanged (ASP.NET Core Data Protection); the key ring is encrypted with AES-256-GCM
   using a key stored as a systemd encrypted credential (`systemd-creds --tpm2-pcrs=`: TPM2 + host key when the machine
   has a TPM2, otherwise – or when TPM2 encryption fails – the host key alone; not bound to the boot state, so a UEFI
   update does not block the service). The installer chooses the variant itself and prints which one it actually used
   (systemd 252 `auto` mode silently skips TPM2 when libraries are missing).
9. **Certificate.** A certificate from the corporate CA (a request from Debian, `certreq` on the Tier 0 side), and when
   there is no CA – a self-signed certificate generated at installation; on first connection AD Manager shows the
   SHA-256 fingerprint to compare with `sudo admng-watchdog cert-fingerprint` and remembers it only after
   confirmation; a change of certificate is never accepted silently.
10. **Retention and backups.** Watchdog events 1095 days (3 years); DC log records related to changes 30 days;
    records of logons and failed logons (4624/4625/4771/4776) 48 hours – the determined source goes into the event and
    is kept with it for 1095 days (confirmed on 2026-10-20); local backups 7 days. The event retention cannot be
    shortened in AD Manager below the threshold in the startup settings (`MinEventsDays`, 1095 by default – changed
    only by the server's root), and every shortening of the retention is a critical event.
11. **Retiring the Windows version.** The Windows service remains available as release 0.2.0. The Windows hosting code
    (the SCM service, HTTP.sys, DPAPI-NG, reading logs over RPC, NTFS ACLs, the Windows certificate store, the
    Application log, PowerShell scripts) is removed after the first Linux release (0.3.0). The 0.2.0 database can be
    moved to Linux (without keys – channel secrets are entered again).

## Consequences

- There is no Windows component on the Watchdog server; the server and its administrators are Tier 0 resources (the
  server's root = the identity `ADMNG-WD$` and all the service's data). This also applies to the platform: the virtual
  machine runs only on a Tier 0 virtualization platform (the same hosts or cluster and the same administrators as the
  domain controllers) or on a separate host whose administrators, console, snapshots, backups and vTPM key protection
  are Tier 0. The hypervisor administrator otherwise has access to the disk, memory and vTPM state – that is, to the
  keytab, the secrets key and the history – and can roll the machine back to a snapshot; disk encryption by the
  hypervisor protects only against theft of the media.
- On the AD side the following are needed: a computer account with a deny entry for SELF and two groups in the Tier 0
  OU, an A record (and PTR), explicit read permissions (as before, for `ADMNG-WD$`; they also give read of
  `tokenGroups` – the Windows Authorization Access Group is not needed), `ms-DS-MachineAccountQuota` = 0 (with
  delegation of the right to create computer objects to those who join computers today), a GPO restricting the
  account's logon, GPOs on the controllers (the WEF subscription manager, the WinRM service, NETWORK SERVICE access to
  the Security log, audit policy), SACLs in the domain and on the PKI container in the configuration partition. No
  longer needed: gMSA, the Event Log Readers group for the service account and the "Remote Event Log Management"
  firewall rules on DCs (removed during migration).
- The controllers send more events (4624 of all network logons – type 3 – and RDP – type 10, also of computer accounts,
  because the Windows XPath filter cannot exclude them; plus 4625, 4771 and failed 4776); the size of the Security log
  on DCs and the WEF traffic must be chosen based on measurement in the pilot. Watchdog discards computer-account and
  anonymous logons when reading, and keeps the remaining logon records for only 48 hours.
- Attribution depends on a correct subscription GPO on every DC; the service reports in the DC Status tab the DCs from
  which no events arrive, delays and records rejected at identity verification. It also depends on SACL entries that
  any holder of the "Manage auditing and security log" right on a DC (Administrators, and with Exchange also Exchange
  Servers) can silently remove; the service does not read the SACL, but reports as critical every change of the security
  (also of the SACL itself) of the domain object, AdminSDHolder, the Domain Controllers OU, `CN=Public Key Services`
  and `CN=Sites` (detection confirmed by the user on 2026-10-20), and the guide requires checking the holders of this
  right and the content of the entries every month.
- **Shared keytab of the API and OpenWEC (a knowingly accepted risk).** Whoever takes over the system account `openwec`
  (e.g. through a bug in OpenWEC, which accepts data from the network) has the long-term key of
  `HTTP/admng-wd.contoso.com` – the same one with which the API verifies tickets. They can use it to forge a service
  ticket (a *silver ticket*) with any domain user name, also a member of `AdMng-Watchdog-Admins`; the API determines
  roles from this name, and the MIT acceptor does not verify the PAC with the KDC, so the attacker gets full Watchdog
  administrator rights (changing rules, silencing alerts – every configuration change is, however, reported to the
  existing recipients, and the history retention cannot be shortened below the `MinEventsDays` threshold from the
  startup settings). They also get the identity `ADMNG-WD$` (read of the directory and SYSVOL); they do not get the
  Watchdog database (a 0700 directory of another account), the channel secrets or write permissions in AD. Splitting the
  keytab by names gives nothing (one account = one key). Mitigations: (1) the API is reachable only from the
  administrators' subnet – the nftables firewall and `AllowedRemoteSubnets`, and the service rejects requests from the
  server itself (also from the loopback address) – so the forged ticket has to be used from that subnet; the subnet is,
  however, not a Tier 0 boundary: it also contains the workstations of people with the Viewer role and of helpdesk, so
  the ticket will work from any of them. Watchdog administrative operations are permitted from the entire subnet – we
  do not restrict the administrator role to the addresses of PAW workstations (the user's decision of 2026-10-20), so
  the use of a forged administrator ticket from any workstation of this subnet is part of the accepted risk of the
  shared keytab; (2) isolation of the `openwec` account (a separate system account without a shell, a systemd sandbox
  without privileges, write only in `/var/lib/openwec`, port 5985 accepts connections only from DC addresses); (3) no
  outbound connections of the `openwec` account – the receiver only accepts connections, so the firewall rejects every
  connection this account tries to initiate (`meta skuid`), and the `openwecd` unit has `IPAddressDeny=any` except for
  the DC addresses: a compromised OpenWEC will not send the keytab out of the server and will not use it itself against
  the controllers or the API. Residual risk: a bug in OpenWEC exploited with traffic from a DC address, or another way
  to the `openwec` account on the server, combined with access to any workstation in the administrators' subnet. The
  residual risk also includes the trustworthiness of attribution – independently of the shared keytab: OpenWEC writes
  every byte that Watchdog reads (also the fields by which it checks the sender), so a compromised `openwec` account
  can silently omit or forge records from DCs – the actor and the source of changes, and events visible only in the log
  (1102, 4719, 4794) – without a signal in the Status tab, as long as anything arrives from each DC. The `openwec`
  account is therefore a Tier 0 resource on a par with the keytab.
- **The Watchdog service's connections to the Internet (a knowingly accepted residual risk).** The service also reads
  data influenced by less trusted people – GPO files from SYSVOL (changed by any GPO editor), event records from DCs
  (account names in failed logons are entered by anyone who can reach a DC), values from LDAP – and has more than
  OpenWEC: the keytab, the channel secrets key and a 3-year history. The firewall allows ports 80 and 443 to any address
  only for the accounts root and `_apt` (updates) and `admng-watchdog`; the latter cannot be narrowed to fixed
  addresses, because Teams (Power Automate) runs under variable Microsoft cloud addresses, and checking the revocation
  of the SMTP server's certificate downloads CRL/OCSP lists from the addresses in the certificate. A bug allowing code
  execution as `admng-watchdog` would therefore provide a channel for sending this data to the Internet. The user
  decided on 2026-10-20: connections to ports 80 and 443 go directly, with no intermediary server (proxy), and only from
  the `admng-watchdog` account (and root and `_apt` for updates); the `openwec` account and the other server accounts
  have no connections to the Internet. The residual risk is accepted.
- A computer account can by default change some of its own attributes itself (e.g. re-enable RC4 in
  `msDS-SupportedEncryptionTypes`, add an SPN for its own name) – the deny entry for SELF blocks this, and every change
  of the account itself is anyway reported as critical, despite the "computer noise" filter.
- Restoring the server on another machine (a different host key or TPM state) will not decrypt the secrets key: after
  `admng-watchdog reset-secrets-key` the service starts with a new key, and the SMTP password and the Teams address have
  to be entered again. Restoring the same virtual machine together with the disk and the vTPM state preserves everything.
- A failed password rotation breaks nothing (the password and the keytab remain); the only dangerous case (the password
  changed, the keytab write failed) is detected immediately and repaired with a one-time password from the Tier 0
  administrator.
- **A certificate for the service account (a knowingly accepted risk).** On 2026-10-20 the user did not agree to deny
  entries for `ADMNG-WD$` on certificate templates. The account, as a member of Domain Computers, can therefore request
  a certificate from any template that allows Domain Computers to (by default "Computer" on every Enterprise CA).
  Whoever steals the keytab (or learns the one-time password within a few minutes before the first password change) can
  obtain a certificate for `ADMNG-WD$` and log on with it as that account (PKINIT) for the entire period of its validity
  (usually a year); changing the password every 30 days limits the value of a stolen keytab, but does not invalidate such
  a certificate. Watchdog does not read the certification authority's database, so it will not detect the issuance of a
  certificate itself; the guide requires checking on every CA after the keytab is created that no certificate has been
  issued to the account. After a keytab leak the account must be disabled, the certificates issued to it on every
  Enterprise CA found and revoked, and new CRLs published immediately, and the account enabled (and given a new
  password) only when the controllers can no longer use the old CRL – the KDC keeps it until the end of its validity, so
  revocation alone does not immediately block certificate logon. An alternative: deleting and recreating the account
  (a new SID – old certificates will not pass strong mapping).
- OpenWEC is a small external project pinned to an unreleased version: we build it ourselves, include the sources
  (GPL-3.0) and update only after review. OpenWEC runs as a separate process – the GPL license does not cover the AdMng
  code.
- The team maintains one platform of the service; unit tests, service tests, integration tests with the Samba lab and
  building of `.deb` packages run in CI on Linux. On Linux the roles can finally be tested too (until now only the
  authentication itself). The behavior on real Windows DCs (WinRM tickets, LDAP signing on Server 2025, event volume,
  dMSA) is confirmed by the pilot.
- Changes made through ADWS (the ActiveDirectory PowerShell module, the Active Directory Administrative Center) are
  performed by the controller on itself – the actor is determined, but the source may be "locally on the DC"; the pilot
  checks whether this can be improved.
- To be updated in Stage B: `docs/WATCHDOG-LINUX.md` (the deployment guide – according to the finished packages),
  `docs/WATCHDOG.md` (the Windows installation chapters replaced with a reference), `CLAUDE.md` (rules 1 and 2, project
  layout, commands), the indicated chapters of `docs/dev/watchdog-spec.md`, a note in the header of ADR-0008.

## Arrangements confirmed on 2026-10-20

The user confirmed on 2026-10-20 five solutions by which the project departed from the arrangements of 2026-10-19. They
apply in the design (`watchdog-linux-design.md`) and in the guide (`WATCHDOG-LINUX.md`) without reservations.

1. **Creating the account.** Instead of creating the account from Debian with msktutil/adcli: the `ADMNG-WD` account is
   created by a Tier 0 administrator on a PAW (`New-ADComputer` with a one-time password), and the password (random, 120
   characters, changed every 30 days) and the keytab are handled by our own tool `admng-watchdog-keytab` (MIT Kerberos,
   no LDAP). Reason: the server needs no rights to create objects in AD, there are no LDAP writes to its own object and
   no temporary files in `/tmp` (alternative 7).
2. **Retention of logon records: 48 hours.** DC log records related to changes – 30 days (as arranged), and mass records
   of logons and failed logons (4624/4625/4771/4776) – 48 hours; the determined source is copied to the event and stays
   with it for 1095 days (3 years). Reason: data minimization (GDPR) – hundreds of thousands of logons a day.
3. **Additional events, rules and requirements.** Events: besides the arranged 4624 (network logons – type 3), 4771 and
   failed 4776, also 4625 (failed logons on a DC – the source of lockouts) and RDP logons (4624 type 10). Rules: besides
   the arranged `tier0-badsuccessor`, also `tier0-machine-quota` (raising `ms-DS-MachineAccountQuota` from 0) and
   `watchdog-self` (any change of the service account other than its password rotation). Deployment requirements:
   `ms-DS-MachineAccountQuota` = 0 (with delegation of the right to create computer objects to those who join computers
   today) and a deny entry for SELF on the service account – so that the account cannot change anything in AD except its
   own password, beyond what any authenticated domain account can do (e.g. adding a new record in an AD-integrated DNS
   zone).
4. **Shared keytab of the API and OpenWEC.** OpenWEC uses the same account `ADMNG-WD$`, the name
   `HTTP/admng-wd.contoso.com` and the keytab as the API; we create neither a second computer account nor a second DNS
   name for the collector. The risk (a forged ticket to the API after the `openwec` account is compromised) is described
   in "Consequences"; it is limited by access to the API only from the administrators' subnet (nftables +
   `AllowedRemoteSubnets`, with no exception for the loopback) and by the isolation of the `openwec` account, also with
   no outbound connections (the firewall and the systemd sandbox).
5. **Lab: three machines.** In addition to the arranged VMs with a Windows Server 2025 DC and Debian 13, a required
   Windows workstation ADM-WS01 (RSAT + AD Manager) is added: AD Manager runs only on Windows, and a change made on a DC
   has the controller as its source. Optionally DC02 and a small machine with Mailpit for email tests.

## Arrangements confirmed on 2026-10-20 – second round

After further reviews of the design the user settled four more matters on 2026-10-20. They apply in the design and in
the guide in the same way as items 1–5.

6. **Certificate templates: no deny entries (risk accepted).** The `ADMNG-WD$` account does not get "Deny" entries for
   the Enroll and Autoenroll rights on certificate templates, and the service does not check templates hourly. The
   risk: whoever steals the keytab can request a certificate for `ADMNG-WD$` from a template that allows Domain
   Computers to enroll, and log on with it (PKINIT) also after the password change every 30 days – the user accepts it
   ("Consequences"). Unchanged: creating the account disabled, the deny entry for SELF and only then enabling it;
   checking after the keytab is created that no certificate has been issued to the account; in the procedure after a
   keytab leak – disabling the account, finding and revoking the certificates issued to the account on every CA and
   waiting out the validity of the old CRL.
7. **Detection of SACL changes: yes.** Every change of the security (also of the SACL itself) of the domain object,
   AdminSDHolder, the Domain Controllers OU, `CN=Public Key Services` and `CN=Sites` is a critical event
   (`watchdog-linux-design.md` 9.4).
8. **Watchdog administrative operations from the entire administrators' subnet.** We do not restrict the administrator
   role to the addresses of PAW workstations. A forged administrator ticket (the shared keytab) will therefore work from
   any workstation of this subnet, also from helpdesk workstations and those of people with the Viewer role – this is
   part of the accepted risk of the shared keytab ("Consequences"). SSH to the server is still only from PAW
   workstations.
9. **Internet connections: direct, with no intermediary server.** Ports 80 and 443 only for the `admng-watchdog` account
   (Teams, CRL/OCSP lists of the SMTP server's certificate) and root and `_apt` (updates); the `openwec` account and
   the other accounts – none. The residual risk (a compromised service can send data to any address) is accepted
   ("Consequences").

## Alternatives considered

1. **gMSA on Linux** (e.g. a daemon fetching the gMSA password). The host needs its own identity with a keytab anyway
   to fetch the gMSA password (two accounts instead of one), a dependency on KDS appears and additional software with
   rights to the gMSA secret. No security gain over a computer account with rotation. Rejected.
2. **Our own WEF receiver in Watchdog.** Requires implementing WS-Management/SOAP with Kerberos message encryption,
   bookmarks and heartbeat – a large, risky scope. OpenWEC already does it today. Postponed ("not now" – the user's
   decision); the intermediate format (RawJson files) allows swapping the receiver later.
3. **Joining the server to the domain (SSSD/realmd/winbind).** Gives an automatic keytab and DNS, but introduces to the
   Tier 0 server domain GPOs, the possibility of domain account logon, additional services and computer account writes
   performed by the joining tools. The service needs only Kerberos, LDAP and SMB with a single identity. Rejected.
4. **Agents on domain controllers** (e.g. NXLog, Winlogbeat, a custom agent). Third-party software on DCs (Tier 0)
   requires a change process, updates and widens the attack surface; WEF is built into Windows. Rejected.
5. **Reading DC logs over RPC from Linux** (MS-EVEN6). No mature library, requires membership in Event Log Readers
   (reading all logs of all DCs) and open RPC on DCs. Rejected.
6. **Keeping the Windows service alongside the Linux version.** Two platforms to secure and test; the user wants Linux
   only. Rejected (0.2.0 remains available for download).
7. **msktutil or adcli for creating the account and rotation.** They require administrator credentials on Debian
   (creation) or LDAP writes to its own object (rotation), create a temporary `krb5.conf` in `/tmp`, msktutil generates
   a 63-character password and enables its own timer. Rejected in favor of our own tool built on MIT primitives checked
   in the lab; the account is created by a Tier 0 administrator on Windows.
8. **LDAPS only.** Requires PKI on DCs and (on Server 2025) channel binding; GSSAPI with encryption works without PKI.
   LDAPS remains an option.
9. **Other OpenWEC outputs** (TCP, Unix socket, Redis, Kafka). TCP and the datagram socket lose events when the
   receiver is unavailable ("at most once" delivery), Redis has no authentication or TLS, Kafka is too heavy for a
   single server. Files were chosen.
10. **An SMB library in .NET instead of `smbclient`.** The available library supports Kerberos only through its own
    authentication module, and SMB 3 encryption with Kerberos keys could not be confirmed. The `smbclient` process was
    chosen (checked in the lab); P/Invoke to libsmbclient remains a possible change.
11. **A separate computer account and keytab for OpenWEC** (a second DNS name of the collector, a keytab readable only by
    `openwec`, a second password rotation, a different address in the subscription GPO). Removes the API key from the
    OpenWEC process, but adds a second Tier 0 account, a DNS record and a keytab to maintain. Rejected by the user's
    decision of 2026-10-20 in favor of the shared keytab with the mitigations described in "Consequences"; it remains
    only an optional hardening for the future (neither the default nor recommended in the guide).
12. **Deny entries for `ADMNG-WD$` on certificate templates** (Enroll and Autoenroll on every template, performed
    before the account is enabled, with the service checking the published templates hourly). They close the route to a
    PKINIT certificate after a keytab theft, but require changes in the configuration partition (in a multi-domain
    forest – Enterprise Admins) with every new template. Rejected by the user's decision of 2026-10-20; risk accepted.
13. **The Watchdog administrator role only from the addresses of PAW workstations.** Narrows the use of a forged
    administrator ticket to Tier 0 workstations, but changes the place of work of Watchdog administrators. Rejected by
    the user's decision of 2026-10-20.
14. **An intermediary server (proxy) with a list of allowed names, or giving up Teams** for the `admng-watchdog`
    connections to the Internet. It would allow `IPAddressDeny=any` in the service unit, but requires additional
    infrastructure (or giving up the Teams channel and publishing CRLs ourselves). Rejected by the user's decision of
    2026-10-20; residual risk accepted.

## Amendment to ADR-0008

ADR-0008 remains in force; on Linux we implement its conditions as follows:

1. **One place.** The data folder is `/var/lib/admng-watchdog` (owner `admng-watchdog`, mode **0700**, no POSIX ACL
   entries, a local file system): `watchdog.db` with WAL files, `backup/`, `keys/`. Instead of NTFS ACLs the service
   checks, at every start, the owner and mode of the folder and of the database files; with broader permissions it does
   not start (unless the local administrator sets `AllowInsecureDataDirectoryAcl`). The startup settings are in
   `/etc/admng-watchdog` (owner root), which the service account cannot modify – checked at start. Temporary files:
   `PrivateTmp=yes`, SQLite with `temp_store = MEMORY`; memory dumps disabled (`LimitCORE=0`,
   `DOTNET_DbgEnableMiniDump=0`), the service's memory does not go to swap (`MemorySwapMax=0`) – in place of disabling
   WER.
2. **A second, short-lived place: OpenWEC output files.** Raw events from DCs are in
   `/var/lib/openwec/out` (openwec:admng-watchdog, 2750, files 0640) only until they are processed: the files are
   rotated hourly and deleted after Watchdog's confirmed read, and unprocessed ones – after 7 days at the latest
   (30 at most) or earlier, oldest first, when together they exceed 8 GB (a flood of events must not fill `/var`). The
   OpenWEC database contains only subscriptions and client state. This exception covers only these two
   directories. A third place, in memory only (the tmpfs `/run/admng-watchdog`, 0700, removed when the service stops):
   the Samba cache (may contain name and SID mappings, cleared after every SYSVOL read) and the progress file of reading
   the OpenWEC files.
3. **Only what is needed.** Added: the records 4624/4625/4771/4776 limited to the fields needed to determine the source
   (without computer-account and anonymous logons, 48-hour retention) and the GPO settings snapshot (only the newest one
   for each GPO; files outside the list – only name, size and date; scripts – only the SHA-256 hash).
4. **Never secrets.** GPP preference passwords (`cpassword`) and registry values with names indicating passwords are
   not stored in any form (only information about their presence, marked as critical).
5. **Retention.** The default retention of Watchdog events is **1095 days** (3 years, the user's decision); DC log
   records related to changes – 30 days; logon records – 48 hours; backups – 7. The `MinEventsDays` threshold in the
   startup settings (1095 by default) protects the history from shortening through the API; the database size limit on
   Linux is 4096 MB by default, so that the database and its copies fit on the recommended `/var`.
6. **Channel secrets.** The Data Protection keys are encrypted with a key from a systemd credential (TPM2 + host key or
   the host key) instead of DPAPI-NG; the binding of secrets to the destination is unchanged. Restoring on another host
   requires entering the secrets again (previously: recreating the gMSA).
7. **Diagnostic logs** go to journald instead of the Application log – still without directory values, exception
   contents and secrets.
8. **Disk encryption.** The BitLocker recommendation is replaced by disk encryption of the virtual machine by the
   hypervisor (preferred – protects against theft of the media, not against hypervisor administrators, hence the Tier 0
   platform requirement in "Consequences") or LUKS (requires a password on the console at every start); swap disabled; a
   VM-level backup recommended (3-year history).
9. **The service account credential.** The keytab `/etc/admng-watchdog/admng-wd.keytab` (root:admng-keytab 0640)
   contains the Kerberos keys of the `ADMNG-WD$` account – never the password – and the TGT of this account in
   `/run/admng-watchdog/krb5cc` (tmpfs, 0600, only while the service is running). This is a new, narrow exception to
   rule 2 in `CLAUDE.md`, next to the channel secrets in `ISecretStore`. The manual procedures in the guide keep tickets
   in memory only (`/dev/shm`).
