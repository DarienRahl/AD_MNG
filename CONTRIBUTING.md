# Contributing to AD Manager

Thank you for helping. This page is the short version; the full developer notes are in [CLAUDE.md](CLAUDE.md) and the
design notes are indexed in [docs/dev/README.md](docs/dev/README.md).

## Before you start

- Read the [goals](docs/en/GOALS.md) and the six rules that never bend (no directory data on disk, no stored or
  logged passwords, everything escaped, one domain controller per session, every write through the change plan, a UI
  that never blocks). A change that breaks one of them will not be merged.
- For anything larger than a fix, open an issue first and describe what you want to change.
- Security problems: do not open a public issue – use GitHub's private vulnerability reporting for this repository.

## Build and test

```bash
dotnet build AdMng.slnx                     # the whole solution; also works on Linux (the WPF app only compiles there)
dotnet test tests/AdMng.Core.Tests
dotnet test tests/AdMng.Presentation.Tests
tools/release/release.sh check              # versions, release notes, Polish/English document pairs, links, resx keys
python3 tools/icon/build-icon-set.py --check
```

The WPF UI tests run on Windows only (CI does that on every push). Integration tests need the Samba lab – see
[tools/testlab/README.md](tools/testlab/README.md).

## What every change needs

- **0 warnings** – warnings are errors.
- **Tests** next to the code they cover; fakes instead of mocking libraries; Polish sample data with diacritics is
  intentional. Use only invented names and `contoso` domains – never real people, companies or hosts.
- **Both languages**: every user-visible string in `Strings.resx` and `Strings.pl.resx`; documentation in `docs/`
  (Polish) and `docs/en/` (English) with the same headings; `README.md` and `README.pl.md` in step.
- **Release notes**: a user-visible change adds its paragraph to `docs/releases/NEXT.md`.
- **Dependencies** must have an OSI-approved licence compatible with Apache-2.0.
- **Icons and pictures** must be original work – see [docs/assets/icons](docs/assets/icons/README.md).

## Releases

Maintainers release with `tools/release/release.sh` – see [docs/dev/releasing.md](docs/dev/releasing.md).

## Licence

By contributing you agree that your contribution is licensed under the [Apache License 2.0](LICENSE).
