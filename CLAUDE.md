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
   hosts) stores snapshots of watched attributes, events, normalized DC log records and its outbox in its own data
   folder, under the conditions of ADR-0008 (never secrets, retention, enforced ACL, encrypted channel secrets,
   diagnostic logs still value-free). `AdMng.Watchdog.Client`, `.Contracts`, `.Core` (outside the service host),
   Presentation and App stay under the strict rule – the WPF process never persists watchdog data.
2. **Never persist or log passwords.** `PlannedChange.Password`, `ResetPasswordOperation.FixedPassword` and
   credentials stay in memory; override `ToString()` on anything that carries them. **Only exception:** AdMng Watchdog
   channel credentials (SMTP password, OAuth client secrets, Teams/webhook URLs, webhook signing keys) are persisted
   solely through `ISecretStore` (ASP.NET Core Data Protection, keys protected with DPAPI-NG to the service gMSA),
   bound to their destination host; they are write-only through the API and never appear in the config JSON, logs,
   exceptions or `ToString()`.
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
| `tests/AdMng.Testing` | net10.0 | Shared test library: `FakeDirectorySession` (in-memory directory with real LDAP modify semantics), `FakeDirectoryBuilder.AddSampleCompany()` |
| `tests/AdMng.Core.Tests`, `tests/AdMng.Presentation.Tests` | net10.0 | Unit tests (view models run against the real Core engines + fake directory) |
| `tests/AdMng.Directory.IntegrationTests` | net10.0 | Unit tests + integration tests against the lab DC (skipped without `ADMNG_TEST_LDAP_*`) |
| `tests/AdMng.App.UiTests` | net10.0-windows | WPF UI tests (STA, off-screen windows, binding-error listener); run on Windows only |
| `src/AdMng.Watchdog.Contracts` | net10.0 | AdMng Watchdog event model, API DTOs, runtime configuration, routes, JSON (`WatchdogJson.Options`); no references |
| `src/AdMng.Watchdog.Core` | net10.0 | Watchdog engine (detection, attribution, rules, notification planning, formatters) against `Abstractions/` + `Model/` (frozen); `AddWatchdogCore()` |
| `src/AdMng.Watchdog.Service` | net10.0 (Sdk.Web, `AdMng.Watchdog.exe`) | Watchdog Windows service: HTTPS + Kerberos API, job hosts, SQLite store, transports – the only project that stores directory data (ADR-0008) |
| `src/AdMng.Watchdog.Client` | net10.0 | HTTPS/Kerberos client of the watchdog API (`IWatchdogClient`), used by the WPF app |
| `tests/AdMng.Watchdog.Testing` | net10.0 | Shared watchdog test library: store contract tests, `InMemoryWatchdogStore`, fixtures, SMTP/HTTP sinks, fake client |
| `tests/AdMng.Watchdog.Core.Tests`, `tests/AdMng.Watchdog.Service.Tests` | net10.0 | Watchdog unit and in-process service tests (Linux and Windows) |
| `tests/AdMng.Watchdog.IntegrationTests` | net10.0 | Watchdog against the Samba lab + Mailpit (skipped without `ADMNG_TEST_LDAP_*`) |

The watchdog is specified in `docs/dev/watchdog-spec.md` (binding for its implementation; product docs `docs/WATCHDOG.md`).

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
```

The WPF app cannot run on Linux; it is compiled here and tested on Windows CI
(`AdMng.App.UiTests` with `ADMNG_UI_TESTS=1`, then the published `AdMng.exe --smoke-test`).
New windows: register with `services.AddWindow<TViewModel, TWindow>()` in `AppHost`, open them from
`Services/DialogService.cs`, and add them to `SmokeTest` and a UI test.

## Conventions

- Warnings are errors (`Directory.Build.props`); keep builds at 0 warnings. Nullable enabled.
- Central package versions in `Directory.Packages.props` (no versions in csproj).
- File-scoped namespaces, `_camelCase` private fields, braces always, `var` where obvious.
- Public APIs get XML doc comments when the behaviour is not obvious.
- Tests: xUnit v3 `Assert`, no FluentAssertions. Fakes over mocking libraries.
- Polish test data with diacritics (ą ć ę ł ń ó ś ź ż) is intentional – keep it.
- User-visible strings only via localization resources (`AdMng.Presentation/Resources/Strings*.resx`; watchdog
  notifications: `AdMng.Watchdog.Core/Resources/NotificationStrings*.resx`), both `en` and `pl` entries required.
