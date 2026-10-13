# AD_MNG – developer notes

Modern, fast replacement for ADUC (dsa.msc). WPF on .NET 10 with the Fluent theme, live LDAP via
System.DirectoryServices.Protocols. Product docs are in Polish (`README.md`, `docs/`); code, comments,
commit messages and these notes are in English.

## Non-negotiable rules

1. **No local directory data.** Never write directory content (objects, attribute values, search results,
   schema dumps, snapshots) to disk, registry, temp files or logs. Everything is read live from the DC and
   kept in memory only while displayed. Allowed on disk: settings (UI layout, language, connection profiles
   without passwords, saved views = names + DNs + LDAP filters), diagnostic logs WITHOUT directory values,
   and files the operator explicitly exports.
2. **Never persist or log passwords.** `PlannedChange.Password`, `ResetPasswordOperation.FixedPassword`
   and credentials stay in memory; override `ToString()` on anything that carries them.
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
| `tests/*` | | xUnit v3 tests; `AdMng.Directory.IntegrationTests` need the lab DC |

## Commands

```bash
dotnet build AdMng.slnx                      # whole solution, works on Linux too (EnableWindowsTargeting)
dotnet test tests/AdMng.Core.Tests
dotnet test tests/AdMng.Presentation.Tests
# integration tests against the Samba lab (see tools/testlab/README.md)
export ADMNG_TEST_LDAP_SERVER=127.0.0.1 ADMNG_TEST_LDAP_USER='Administrator@contoso.test' ADMNG_TEST_LDAP_PASSWORD='P@ssw0rd!2026'
dotnet test tests/AdMng.Directory.IntegrationTests
```

The WPF app cannot run on Linux; it is compiled here and smoke-tested on Windows CI
(`AdMng.exe --smoke-test`).

## Conventions

- Warnings are errors (`Directory.Build.props`); keep builds at 0 warnings. Nullable enabled.
- Central package versions in `Directory.Packages.props` (no versions in csproj).
- File-scoped namespaces, `_camelCase` private fields, braces always, `var` where obvious.
- Public APIs get XML doc comments when the behaviour is not obvious.
- Tests: xUnit v3 `Assert`, no FluentAssertions. Fakes over mocking libraries.
- Polish test data with diacritics (ą ć ę ł ń ó ś ź ż) is intentional – keep it.
- User-visible strings only via localization resources (`AdMng.Presentation/Resources/Strings*.resx`), both
  `en` and `pl` entries required.
