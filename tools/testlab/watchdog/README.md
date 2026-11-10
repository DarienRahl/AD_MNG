**English** · [Polski](README.pl.md)

# AdMng Watchdog in the lab (Samba AD DC)

The scripts in this directory run the **AdMng Watchdog** service on Linux against the lab domain controller
from `tools/testlab` (`CONTOSO.TEST`), with a local **Mailpit** mail server and a webhook receiver.
Everything runs on the loopback interface only (`127.0.0.1`), and the scenario changes the directory only in disposable OUs
that it creates and deletes itself. The lab passwords are public test values – never use these scripts
in a real domain.

| File | Purpose |
|---|---|
| `start-mailpit.sh` / `stop-mailpit.sh` | downloads the pinned Mailpit version **v1.31.5** (SHA-256 checked before unpacking, into `~/.cache/admng/mailpit/`), starts SMTP `127.0.0.1:1025` (STARTTLS with a self-signed certificate, any login) and the UI/API `http://127.0.0.1:8025`; `--require-starttls` enforces STARTTLS |
| `webhook-sink.py` | a receiver at `http://127.0.0.1:8091` (Python standard library only): verifies Standard Webhooks signatures (`SINK_SECRET`) and the 5-minute timestamp tolerance, prints the type / id / object, a Teams mode for `/teams…` paths (a `message` envelope with an Adaptive Card – prints the card title); responses controlled by `SINK_STATUS` (`202`, `429`, `500`) and `SINK_RETRY_AFTER` or on the fly with `PUT /_sink/response`; `GET /_sink/requests` returns a summary of the calls received |
| `appsettings.Lab.json` | the service's startup file (the `Lab` profile): `Development` authentication (loopback only), `http://127.0.0.1:5089`, directory read as `odczyt.test` with a simple bind without TLS (`AllowInsecureLab`, only in the Development environment), data in `~/.local/share/admng-watchdog-lab` |
| `run-watchdog-lab.sh` | starts Mailpit, the receiver and the service (`dotnet run --launch-profile Lab`); the LDAP password reaches the service only through the `ADMNG_WATCHDOG_LDAP_PASSWORD` variable (from `ADMNG_TEST_LDAP_READONLY_PASSWORD`), never through a file; `--reset` deletes the data folder (a new baseline), `--background` runs the service in the background, `--stop` stops everything |
| `configure-lab.sh` | configures the service through its API: SMTP → Mailpit (certificate pinned by SHA-256), recipient groups `it-admins` = `admin@contoso.test`, `helpdesk` = `helpdesk@contoso.test`, channels `siem-lab` (webhook) and `teams-lab` (Teams) → the receiver, a daily report in 2 minutes; `--test` sends test messages |
| `lab-scenario.sh` | a demonstration scenario in `OU=WatchdogDemo` (Zofia Żak, Łukasz Wiśniewski, Paweł Lewandowski, Grażyna Dąbrowska); `--verify` checks the events in the API, the emails in Mailpit and the signatures in the receiver; `--cleanup` deletes the OU |
| `krb5-lab.sh` | manual Negotiate experiments (the tests do not use it): a service account with the SPN `HTTP/localhost` in `OU=WatchdogKerberos`, a keytab (`samba-tool domain exportkeytab`) and a private `krb5.conf`; `--cleanup` |
| `keytab-lab.sh` | the Kerberos environment for the `admng-watchdog-keytab` tool, LDAP over SASL GSSAPI and the API with Negotiate (`KeytabLabTests`, `KerberosNegotiateTests`): a private `krb5.conf` and `ldap.conf` (as in the package), the `KRB5_*` variables, `LDAPCONF`; `env [--admin]`, `cleanup` |
| `wec-units-lab.sh` | Stage B4: the `admng-openwec` and `admng-watchdog` packages in a privileged container with systemd as PID 1 – `openwecd.service` in its sandbox with the keytab of a fresh account (its own OU, deleted at the end; `admng-watchdog-keytab init` from the package) accepts SPNEGO from the host and writes no access log; the real `admng-wec-rotate.service` unit: renaming, reloading `openwecd`, deleting only files that were read, `MAX_UNCONSUMED_DAYS`, a run with the service stopped |
| `common.sh` | shared settings and functions (sourced by the other scripts) |

Requirements: the lab from `tools/testlab/setup-samba-dc.sh`, .NET SDK 10, `ldap-utils`, `curl`, `jq`, `openssl`,
`python3`.

## Quick start

```bash
# 1. Service + Mailpit + webhook receiver (in the background; without --background it runs in the foreground, Ctrl+C ends it)
tools/testlab/watchdog/run-watchdog-lab.sh --reset --background

# 2. Notification channels and recipients (through the service API)
tools/testlab/watchdog/configure-lab.sh --test

# 3. Directory changes and a check that they were reported (about 2 minutes)
tools/testlab/watchdog/lab-scenario.sh --verify

# 4. Cleanup
tools/testlab/watchdog/run-watchdog-lab.sh --stop
```

After step 3 you can see:

- events: `curl -s -H 'X-AdMng-Watchdog-Api: 1' http://127.0.0.1:5089/api/v1/events | jq` or the **Watchdog**
  tab in AD_MNG (the service address `http://127.0.0.1:5089` is allowed only for the loopback interface in development
  mode) – the page of a single event: the "Open in the browser" link in the email;
- emails (Polish subjects, e.g. "[AD Watchdog][WYSOKI] Dodano do grupy uprzywilejowanej: Zofia Żak…", "Nowe konta: 4",
  "Zablokowano konto: Paweł Lewandowski" for the help desk) in Mailpit: <http://127.0.0.1:8025>;
- webhook calls and Teams cards in the receiver log `~/.cache/admng/watchdog-lab/webhook-sink.log`
  (`signatureValid: true`) or `curl -s http://127.0.0.1:8091/_sink/requests | jq`;
- the daily report at the time set by `configure-lab.sh` (by default 2 minutes after configuration).

The service log (`~/.cache/admng/watchdog-lab/watchdog.log` in `--background` mode) contains no directory values
or secrets – just as in production.

## What the scenario does

1. Creates `OU=WatchdogDemo` with the OUs `Pracownicy`, `Przeniesieni`, `Komputery`, `Grupy`, the groups `Demo Tier0` (the
   privileged root – `configure-lab.sh` adds it to `privilegedGroups.highSamAccountNames`), `Demo Operatorzy`
   (nested in `Demo Tier0`) and `Demo Projekt`.
2. Creates Zofia Żak's account in three steps (disabled → password over StartTLS → enabled) – within the
   `creationSettleWindow` (30 s in the lab) this is a single **UserCreated** event with the final state; in addition three
   more accounts and the computer `WD-KRK-PC01` with a child object.
3. Changes Łukasz Wiśniewski's department and job title (**AttributesChanged** old → new value), sets the account
   expiration in 5 days (**AccountExpirationChanged**, and at the next scan **AccountExpiring**), adds him to a group
   (**GroupMemberAdded**), adds Zofia to a nested group (**PrivilegeGranted** with indirect membership),
   sets `pwdLastSet` 0 → -1 for Grażyna Dąbrowska (**PasswordAgeReset**) and disables her account (**AccountDisabled**).
4. Five failed logons of Paweł Lewandowski (lockout threshold 5) → **AccountLockedOut**, then an unlock by an
   administrator → **AccountUnlocked**.
5. Moves Łukasz (**ObjectMoved**), renames the OU `Pracownicy` → `Zespół Demo` (a single **ObjectRenamed**, child objects
   are not reported), deletes Grażyna and the computer with its child object.

The service reads the directory as `odczyt.test`, which cannot see `CN=Deleted Objects`: deletions are reported only by the
nightly reconciliation as **ObjectDisappeared** ("disappeared"), and the health status shows the warning `permission.deletedObjects`
– just as in a domain without delegated read access to deleted objects (`docs/WATCHDOG.md`). The integration tests also
check the variant with the administrator account (**ObjectDeleted**).

The lab configuration deliberately differs from the default: the rollout mode is finished, working hours
cover the whole week without holidays, the aggregation windows are 1 minute, and the message limit per rule is 1000 per hour, so that
subsequent scenario runs do not end up only in the daily report.

## Kerberos (optional)

By default the lab uses `Development` authentication. The real API Kerberos (Stage B3,
`docs/dev/watchdog-linux-design.md` 6.1–6.2) is checked by the `KerberosNegotiateTests` test: it creates a fresh computer
account in its own OU, runs `admng-watchdog-keytab init` (the keytab from the `keytab-lab.sh env` environment), starts the service with
`Negotiate` on Kestrel (an acceptor from `KRB5_KTNAME`, directory read over SASL GSSAPI as that account) and logs in with the passwords of
the test users (a ticket for `HTTP/<account FQDN>`). Roles come solely from reading `objectSid` and `tokenGroups`
by the service itself: a Viewer (through a nested group) reads, an Admin gets the administrator endpoints, a person outside the
groups gets 403, a member of a group from `DeniedSids` gets 403 despite membership in the Admins group; a ticket encrypted with a key
that the keytab does not contain gives 401 (not 500), an NTLM-only client gets 401, and a call from the loopback interface outside
`AllowedRemoteSubnets` gets 403 even before authentication.

```bash
export ADMNG_TEST_LDAP_SERVER=127.0.0.1 ADMNG_TEST_LDAP_USER='Administrator@contoso.test' ADMNG_TEST_LDAP_PASSWORD='P@ssw0rd!2026'
eval "$(tools/testlab/watchdog/keytab-lab.sh env)"          # empty MEMORY: cache – the service identity from the test keytab
dotnet test tests/AdMng.Watchdog.IntegrationTests --filter "FullyQualifiedName~KerberosNegotiateTests"
```

The test account name is not in DNS: clients connect to `127.0.0.1` and give the account FQDN in the `Host` header (.NET builds
the SPN from it without a DNS query). `KerberosNegotiateTests` and `KeytabLabTests` write the same keytab, so they run in
one xUnit collection (one after another).

`krb5-lab.sh` remains for manual Negotiate experiments (the account `wd-krb-http` with the SPN `HTTP/localhost`, a keytab and a private
`krb5.conf` in `~/.cache/admng/watchdog-lab/kerberos`, permissions 0700; `--cleanup` deletes the account and files).

## Service account and keytab

Stage B2 of the Linux version (`docs/dev/watchdog-linux-design.md` 4.4–4.7, 5.1): the `admng-watchdog-keytab` tool sets the
computer account password from a one-time password (`init`) and rotates it (`rotate`), and the service connects to the DC over LDAP 389 with
Kerberos and SASL encryption (SSF ≥ 56) as that account. The `KeytabLabTests` tests create a fresh computer account in their own OU
(disabled, with a one-time password, the SPNs `HTTP/` and `HOST/`, AES, `NOT_DELEGATED`), run `init` and `rotate`
in the test process and check that the open LDAP session and the Negotiate client (an acceptor from `KRB5_KTNAME`) keep working
without a restart, and that the service account monitor reports the health codes 4.6/4.7 for injected faults.

```bash
export ADMNG_TEST_LDAP_SERVER=127.0.0.1
eval "$(tools/testlab/watchdog/keytab-lab.sh env)"          # empty MEMORY: cache – the identity from the test keytab
dotnet test tests/AdMng.Watchdog.IntegrationTests --filter "FullyQualifiedName~KeytabLabTests"

eval "$(tools/testlab/watchdog/keytab-lab.sh env --admin)"  # the Administrator's ticket – the GssapiTests tests
dotnet test tests/AdMng.Directory.IntegrationTests --filter "FullyQualifiedName~GssapiTests"
tools/testlab/watchdog/keytab-lab.sh cleanup
```

The variables must be in the environment of the test process (libkrb5, libldap and Cyrus SASL read them natively; a change inside the
.NET process does not reach them). The files are in `~/.cache/admng/watchdog-lab/keytab` (0700). It requires the SPNs `ldap/<DC name>`
and a resolvable DC name – see [`../README.md`](../README.md) ("Kerberos (SASL GSSAPI) over LDAP"). Samba exempts the
computer account from the minimum password age, so the `otpActive` path cannot be triggered here (the unit tests cover it); with one DC the
replication delay is simulated by the test with a second, unreachable KDC in `krb5.conf`.

## WEF collector (OpenWEC)

Stage B4 (`docs/dev/watchdog-linux-design.md` 7, 12.2, 12.4): `OpenWecLabTests` starts `openwecd` from the pinned
OpenWEC commit with the package configuration (`packaging/admng-openwec/etc/openwec.conf.toml`) and the keytab of a fresh account
(`admng-watchdog-keytab init`), loads the subscription from `print-wec-subscription` with the real `openwec` CLI and checks that
tickets for `HTTP/` and `host/` of the account are accepted (the `Kerberos` and `Negotiate` schemes, the mutual authentication token),
and that a request without a ticket and a ticket for another service are not. `openwecd` is built by `tools/packaging/build-openwec-deb.sh` (in CI for
Debian 12 and 13) or by `cargo build --release --locked` in a clone at commit `86de07f`.

```bash
export ADMNG_TEST_LDAP_SERVER=127.0.0.1 ADMNG_TEST_LDAP_USER='Administrator@contoso.test' ADMNG_TEST_LDAP_PASSWORD='P@ssw0rd!2026'
eval "$(tools/testlab/watchdog/keytab-lab.sh env)"
export ADMNG_TEST_OPENWECD=/ścieżka/do/openwec/target/release/openwecd   # the openwec binary next to it
dotnet test tests/AdMng.Watchdog.IntegrationTests --filter "FullyQualifiedName~OpenWecLabTests"

# units under systemd (an image with systemd >= 252 and aspnetcore-runtime-10.0, e.g. the lab image of B1)
eval "$(tools/testlab/watchdog/keytab-lab.sh env --admin)"
tools/testlab/watchdog/wec-units-lab.sh <obraz> admng-openwec_<wersja>_amd64.deb admng-watchdog_<wersja>_amd64.deb
```

## GPO content (SYSVOL)

Stage B5 (`docs/dev/watchdog-linux-design.md` 8): `GpoContentLabTests` runs `samba-tool gpo create` for its own GPO, writes
its files through `smbclient` as the administrator (a security template, Registry.pol, a startup script, a GPP task,
a GPP user with `cpassword`), bumps `GPT.INI` and `versionNumber` like GPMC and checks the "was → is" events of the
`gpo-content` job, which reads SYSVOL through `smbclient` with a ticket of a fresh computer account (a keytab from
`admng-watchdog-keytab init`, its own `krb5.conf` and `smb-client.conf` in a temporary directory). In addition: a file change without a
version change detected by a forced reconciliation, a `gPCFileSysPath` redirect (Critical, with no SMB connection at all),
a scan of the SQLite database for `cpassword` and a TGT ticket exchange with a 1-minute lifetime. At the end the GPO is deleted.
It requires `smbclient`, `kinit` and `samba-tool` (Python with the `samba` module). If the computer account gets
`NT_STATUS_ACCESS_DENIED` on `Policies`, the lab's SYSVOL permissions have diverged from the ACLs of the GPO objects
(`samba-tool ntacl sysvolcheck` reports an error) – `samba-tool ntacl sysvolreset` fixes it.

```bash
export ADMNG_TEST_LDAP_SERVER=127.0.0.1 ADMNG_TEST_LDAP_USER='Administrator@contoso.test' ADMNG_TEST_LDAP_PASSWORD='P@ssw0rd!2026'
dotnet test tests/AdMng.Watchdog.IntegrationTests --filter "FullyQualifiedName~GpoContentLabTests"
```

## Integration tests

```bash
tools/testlab/watchdog/start-mailpit.sh
export ADMNG_TEST_LDAP_SERVER=127.0.0.1 ADMNG_TEST_LDAP_USER='Administrator@contoso.test' ADMNG_TEST_LDAP_PASSWORD='P@ssw0rd!2026'
export ADMNG_TEST_LDAP_READONLY_USER='odczyt.test@contoso.test' ADMNG_TEST_LDAP_READONLY_PASSWORD='Passw0rd!Lab2026'
export ADMNG_TEST_SMTP_HOST=127.0.0.1 ADMNG_TEST_SMTP_PORT=1025 ADMNG_TEST_MAILPIT_API=http://127.0.0.1:8025
dotnet test tests/AdMng.Watchdog.IntegrationTests
tools/testlab/watchdog/stop-mailpit.sh
```

The tests create their own OU `OU=WatchdogIT-<run id>` and look only at events of objects from that OU –
the lab is sometimes shared. The OU description identifies the test process; at start, the OUs of runs that have
finished are deleted (the process on this host is no longer running or the OU is older than 2 hours), and the OUs of runs in progress
are left alone. Deletion is done on the client side, object by object (see "Cleaning up after tests" in
[`tools/testlab/README.md`](../README.md)). Without
`ADMNG_TEST_LDAP_SERVER` they are skipped; the end-to-end test without `ADMNG_TEST_SMTP_HOST` starts its own in-process SMTP server
instead of Mailpit.

## Cleanup

```bash
tools/testlab/watchdog/lab-scenario.sh --cleanup        # OU=WatchdogDemo
tools/testlab/watchdog/run-watchdog-lab.sh --stop       # background service, receiver, Mailpit (messages disappear)
tools/testlab/watchdog/run-watchdog-lab.sh --reset ...  # at the next start: a new database and a new baseline
rm -rf ~/.local/share/admng-watchdog-lab ~/.cache/admng # everything, including the downloaded Mailpit
```
