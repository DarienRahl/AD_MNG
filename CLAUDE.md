# AD_MNG – developer notes

Modern, fast replacement for ADUC (dsa.msc). WPF on .NET 10 with the Fluent theme, live LDAP via
System.DirectoryServices.Protocols. Product docs are in Polish (`README.md`, `docs/`); code, comments,
commit messages and these notes are in English.

## Non-negotiable rules

1. **No local directory data in the app.** AD_MNG (App, Presentation, Core, Directory) never writes directory content
   (objects, attribute values, search results, schema dumps, snapshots) to disk, registry, temp files or logs. Everything
   is read live from the DC and kept in memory only while displayed. Allowed on disk: settings (UI layout, language,
   connection profiles without passwords, saved views = names + DNs + LDAP filters, the Watchdog service URL),
   diagnostic logs WITHOUT directory values, and files the operator explicitly exports.
   **Only exception:** the AdMng Watchdog service host (`src/AdMng.Watchdog.Service` and the store implementations it
   hosts) stores snapshots of watched attributes, GPO settings snapshots (never `cpassword` or other secrets), events,
   normalized DC log records (logon records 48 h), its outbox (alerts and, when switched on, AD Manager's user messages –
   content deleted once sent) and the user-message audit (who, when, recipient count, address hash – no content) in
   its own data folder, under the conditions of
   ADR-0008 as amended by ADR-0009 (never secrets, retention, enforced permissions, encrypted channel secrets,
   diagnostic logs still value-free). On Linux (the only shipped platform from 0.3.0) that folder is
   `/var/lib/admng-watchdog` (owner `admng-watchdog`, 0700, checked at every start). Second, short-lived place: the
   OpenWEC output files `/var/lib/openwec/out` (raw DC Security events, rotated hourly, deleted once the service has
   consumed them, unconsumed ones after 7 days / 8 GB); RAM only: `/run/admng-watchdog` (Samba cache, WEC progress).
   `AdMng.Watchdog.Keytab` writes only the keytab (rule 2) and `/var/lib/admng-watchdog-keytab/status.json` (own
   principal, kvno, times – no other directory data). `AdMng.Watchdog.Client`, `.Contracts`, `.Core` (outside the
   service host), Presentation and App stay under the strict rule – the WPF process never persists watchdog data.
2. **Never persist or log passwords.** `PlannedChange.Password`, `ResetPasswordOperation.FixedPassword` and
   credentials stay in memory; override `ToString()` on anything that carries them. **Only exceptions:** (a) AdMng
   Watchdog channel credentials (SMTP password, OAuth client secrets, Teams/webhook URLs, webhook signing keys) are
   persisted solely through `ISecretStore` (ASP.NET Core Data Protection; on Linux the key ring is encrypted with the
   32-byte key of the systemd encrypted credential `/etc/admng-watchdog/credentials/dp-key.cred` – TPM2 + host key or
   host key – the 0.2.0 Windows service used DPAPI-NG to the gMSA), bound to their destination host; they are
   write-only through the API and never appear in the config JSON, logs, exceptions or `ToString()`; (b) the Kerberos
   keys (never the password) of the service account `ADMNG-WD$` in the keytab `/etc/admng-watchdog/admng-wd.keytab`
   (root:admng-keytab 0640), written only by `admng-watchdog-keytab` (ADR-0009); the random account password exists only
   in a zeroed in-memory buffer of that tool, and the TGT cache of the service is the tmpfs `/run/admng-watchdog`.
3. **Escape everything.** Build filters with `AdMng.Core.Ldap.LdapFilter`, DNs with `DistinguishedName`
   (`EscapeValue`, `Child`, `WithParent`). Never concatenate user input into filters/DNs.
4. **One DC per session.** All reads and writes of an `IDirectorySession` go to the same DC.
5. **Every write goes through the change-plan pipeline** (`IChangePlanner`/`ChangePlan` → `IBatchExecutor`),
   also single-object edits from property pages, so preview, validation, audit and rollback work everywhere.
6. **UI never blocks.** All directory calls are async with `CancellationToken`; long lists stream/virtualize.

## Layout

| Project | Target | Purpose |
|---|---|---|
| `src/AdMng.Core` | net10.0 | DN/filter primitives, AD flags & converters, object/schema model, contracts (`Directory/`, `Batch/`, `Templates/`, `Csv/`, `Formatting/`, `Audit/`) and their pure implementations |
| `src/AdMng.Directory` | net10.0 | S.DS.P implementation of `IDirectoryConnector`/`IDirectorySession` |
| `src/AdMng.Presentation` | net10.0 | View models (CommunityToolkit.Mvvm), UI service interfaces, localization (resx en + pl), settings. **No WPF types.** |
| `src/AdMng.App` | net10.0-windows | WPF views (XAML), Generic Host/DI, Windows-only services |
| `src/AdMng.Mail` | net10.0 | Direct SMTP transport of user messages (U1, MailKit): TLS modes, certificate policy, authentication, failure classification; the SMTP password lives only in memory |
| `tests/AdMng.Mail.Tests` | net10.0 | SMTP transport tests (in-process test servers; Mailpit with `ADMNG_TEST_SMTP_*`) |
| `tests/AdMng.Testing` | net10.0 | Shared test library: `FakeDirectorySession` (in-memory directory with real LDAP modify semantics), `FakeDirectoryBuilder.AddSampleCompany()` |
| `tests/AdMng.Core.Tests`, `tests/AdMng.Presentation.Tests` | net10.0 | Unit tests (view models run against the real Core engines + fake directory) |
| `tests/AdMng.Directory.IntegrationTests` | net10.0 | Unit tests + integration tests against the lab DC (skipped without `ADMNG_TEST_LDAP_*`) |
| `tests/AdMng.App.UiTests` | net10.0-windows | WPF UI tests (STA, off-screen windows, binding-error listener); run on Windows only |
| `src/AdMng.Watchdog.Contracts` | net10.0 | AdMng Watchdog event model, API DTOs, runtime configuration, routes, JSON (`WatchdogJson.Options`); no references |
| `src/AdMng.Watchdog.Core` | net10.0 | Watchdog engine (detection, attribution, rules, notification planning, formatters) against `Abstractions/` + `Model/` (frozen); `AddWatchdogCore()` |
| `src/AdMng.Watchdog.Service` | net10.0 (Sdk.Web, `AdMng.Watchdog`) | Watchdog service – from 0.3.0 the systemd unit `admng-watchdog.service` on Debian 12/13 (the Windows hosting code of 0.2.0 is still compiled until its removal, `docs/dev/watchdog-linux-design.md` 17): HTTPS + Kerberos API, job hosts, SQLite store, transports, OpenWEC file tailer, SYSVOL reader (smbclient) – the only project that stores directory data (ADR-0008, ADR-0009) |
| `src/AdMng.Watchdog.Keytab` | net10.0 (`admng-watchdog-keytab`) | Password rotation and keytab of the service account `ADMNG-WD$` (`init`, `rotate`, `verify`, `status`; MIT libkrb5 through P/Invoke) |
| `src/AdMng.Watchdog.Client` | net10.0 | HTTPS/Kerberos client of the watchdog API (`IWatchdogClient`), used by the WPF app |
| `tests/AdMng.Watchdog.Testing` | net10.0 | Shared watchdog test library: store contract tests, `InMemoryWatchdogStore`, fixtures, SMTP/HTTP sinks, fake client |
| `tests/AdMng.Watchdog.Core.Tests`, `tests/AdMng.Watchdog.Service.Tests` | net10.0 | Watchdog unit and in-process service tests (Linux and Windows) |
| `tests/AdMng.Watchdog.IntegrationTests` | net10.0 | Watchdog against the Samba lab + Mailpit (skipped without `ADMNG_TEST_LDAP_*`); keytab, GSSAPI, Negotiate, OpenWEC and SYSVOL lab tests |
| `packaging/admng-watchdog`, `packaging/admng-openwec` | – | Debian packaging: systemd units, sysusers/tmpfiles, maintainer scripts, conffiles (`appsettings.json`, `krb5.conf`, `ldap.conf`, `smb-client.conf`, `openwec.conf.toml`), the `admng-watchdog` wrapper, `wec-rotate`, nftables example |
| `tools/packaging` | – | `build-deb.sh` (admng-watchdog), `build-openwec-deb.sh` (admng-openwec from the pinned upstream commit + GPL source tarball), `test-deb-install.sh` (install test in plain Debian containers) |

The watchdog is specified in `docs/dev/watchdog-spec.md` (v1) and, for everything the Linux service does,
`docs/dev/watchdog-linux-design.md` (stage B; it wins where they disagree). Product docs: `docs/WATCHDOG.md` (features)
and `docs/WATCHDOG-LINUX.md` (deployment), both Polish.

## Commands

```bash
dotnet build AdMng.slnx                      # whole solution, works on Linux too (EnableWindowsTargeting)
dotnet test tests/AdMng.Core.Tests
dotnet test tests/AdMng.Presentation.Tests
# integration tests against the Samba lab (see tools/testlab/README.md)
export ADMNG_TEST_LDAP_SERVER=127.0.0.1 ADMNG_TEST_LDAP_USER='Administrator@contoso.test' ADMNG_TEST_LDAP_PASSWORD='P@ssw0rd!2026'
dotnet test tests/AdMng.Directory.IntegrationTests
# AdMng Watchdog (one project per `dotnet test` call)
dotnet test tests/AdMng.Watchdog.Core.Tests
dotnet test tests/AdMng.Watchdog.Service.Tests
# lab: Samba + Mailpit (tools/testlab/watchdog/README.md)
dotnet test tests/AdMng.Watchdog.IntegrationTests
dotnet run --project src/AdMng.Watchdog.Service --launch-profile Lab
tools/testlab/watchdog/run-watchdog-lab.sh --reset --background && tools/testlab/watchdog/configure-lab.sh --test \
    && tools/testlab/watchdog/lab-scenario.sh --verify      # lab scenario as in CI; --stop / --cleanup afterwards
eval "$(tools/testlab/watchdog/keytab-lab.sh env)"           # Kerberos env for KeytabLabTests/KerberosNegotiateTests
# Debian packages (Linux; version = VersionPrefix): admng-watchdog_<version>-1_amd64.deb in artifacts/deb
tools/packaging/build-deb.sh --output artifacts/deb
# admng-openwec_<upstream>-1~deb13_amd64.deb (+ GPL source tarball) – run inside rust:slim-trixie (12: slim-bookworm)
tools/packaging/build-openwec-deb.sh --release 13 --output artifacts/deb --source-tarball
# install test as in CI: docker run --rm --cap-add=NET_ADMIN -v "$PWD:/src:ro" debian:13 bash \
#   /src/tools/packaging/test-deb-install.sh /src/<admng-watchdog .deb> /src/packaging/admng-watchdog /src/<admng-openwec .deb>
```

The WPF app cannot run on Linux; it is compiled here and tested on Windows CI
(`AdMng.App.UiTests` with `ADMNG_UI_TESTS=1`, then the published `AdMng.exe --smoke-test`).
New windows: register with `services.AddWindow<TViewModel, TWindow>()` in `AppHost`, open them from
`Services/DialogService.cs`, and add them to `SmokeTest` and a UI test.

Every connection window is one DI scope (`docs/dev/multi-window.md`): services that touch a session, the change bus,
the audit log, activities, dialogs, toasts, the clipboard or window ownership are **Scoped**, and new windows are
created from the connection's scope (`IWindowService`). Never resolve a per-connection service from the root provider
and never register one as a singleton before `AddAdMngPresentation` (its `TryAddScoped` would lose). A scoped service
that subscribes to an application-wide event must unsubscribe in `Dispose`. Add new per-connection services to
`tests/AdMng.Presentation.Tests/Connections/ConnectionLifetimes.cs` (or the App list in `HostTests`).

## Conventions

- Warnings are errors (`Directory.Build.props`); keep builds at 0 warnings. Nullable enabled.
- Central package versions in `Directory.Packages.props` (no versions in csproj).
- File-scoped namespaces, `_camelCase` private fields, braces always, `var` where obvious.
- Public APIs get XML doc comments when the behaviour is not obvious.
- Tests: xUnit v3 `Assert`, no FluentAssertions. Fakes over mocking libraries.
- Polish test data with diacritics (ą ć ę ł ń ó ś ź ż) is intentional – keep it.
- User-visible strings only via localization resources (`AdMng.Presentation/Resources/Strings*.resx`; watchdog
  notifications: `AdMng.Watchdog.Core/Resources/NotificationStrings*.resx`), both `en` and `pl` entries required.
