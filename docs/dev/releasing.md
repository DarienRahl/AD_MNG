# Releasing AD Manager

A release is one command and one push. `tools/release/release.sh` writes the commit, the workflow
`.github/workflows/release.yml` builds, tests and packages it, and only then creates the tag and the GitHub release.
The script needs git, sed, awk and python3, runs from any directory and refuses a working tree that is not clean.

## The short path

```bash
tools/release/release.sh status            # version, state of the draft, what the next push would release
tools/release/release.sh 0.16.0 --dry-run  # the diff and the result of the checks; changes nothing
tools/release/release.sh 0.16.0            # one commit "Release 0.16.0"
git push
```

The commit contains everything the manual release had in two commits:

- the version in its four places – `VersionPrefix` in `Directory.Build.props`, the version badge and the sentence
  "(X.Y.Z is a preview release)" / "(X.Y.Z jest wersją zapoznawczą)" in `README.md` and `README.pl.md`, and
  `packaging/admng-watchdog/debian/changelog` (the entry `@VERSION@` becomes `<previous>-1`, a new `@VERSION@` entry
  dated now goes on top). The Watchdog entry says "No change in how the service works…" unless you pass
  `--watchdog-note "<text>"` (repeat it for more lines);
- `docs/releases/vX.Y.Z.md` made from the draft `docs/releases/NEXT.md`, and a fresh `NEXT.md`.

The notes have no marker: the workflow releases the pushed commit itself. Commit trailers come from the environment
variable `ADMNG_COMMIT_TRAILERS` (one per line); without it there are none.

## The two-step path

Use it when a green CI run (integration tests against the Samba lab, the Debian packages) is wanted before anything is
released – the release workflow itself runs the Windows build with unit and UI tests and builds the packages, but not
the lab jobs.

```bash
tools/release/release.sh prepare 0.16.0    # commit "Prepare 0.16.0"; the notes stay in NEXT.md
git push                                   # wait for CI
tools/release/release.sh publish           # commit "Release notes 0.16.0" with the marker of the Prepare commit
git push
```

`publish` finds the commit "Prepare X.Y.Z" in the history and writes it into the first line of the notes
(`<!-- release-commit: <sha> -->`). If CI needed a fix after the Prepare commit, release the fixed commit with
`release.sh publish --commit <rev>`.

## The draft: docs/releases/NEXT.md

`NEXT.md` is always there and is the draft of the next release notes (Polish, with an English summary; its name does
not match the workflow trigger `v*.md`). **Every user-visible change adds its paragraph to it in the same commit** –
under `## Nowe: <feature>`, `## Inne zmiany`, `## AdMng Watchdog`, `## Znane ograniczenia` – and removes the
`<!-- TODO: ... -->` comment it replaces. At release time `release.sh` fills `{{VERSION}}`, `{{PREVIOUS}}` and
`{{DOWNLOADS}}` (the download table and the checksum paragraph, with the OpenWEC version from
`tools/packaging/build-openwec-deb.sh`) and refuses to release while a TODO comment or an empty section is left.
The template is `tools/release/NEXT.template.md`; `python3 tools/release/notes.py reset` restores the draft from it.

## What the workflow does

`plan` decides what to release – from pushed notes `docs/releases/v*.md` (the commit named by the marker, or the pushed
commit), from a pushed tag, or from a manual start – and fails early without the secret `RELEASE_TOKEN`. `build`
(Windows: build, unit and UI tests, single-file publish, smoke test), `openwec-package` and `linux-package` (the three
`.deb` files, install tests on Debian 12 and 13, lintian) all work on that one commit. `release` runs last: it uploads
the files with `SHA256SUMS.txt` to a draft and publishes it, which creates the tag. A failed build therefore leaves
no tag and no release behind.

## Retrying and starting by hand

*Actions → Release → Run workflow*, choose the branch and type the version, or:

```bash
gh workflow run release.yml --ref <branch> -f version=0.16.0
```

This releases `docs/releases/v<version>.md` as it is on that branch – the commit of its marker or, without a marker,
the head of the branch – without touching the notes. Use it after a failed run (push the fix first when one was
needed; notes without a marker then release the fixed head). For a version whose release is already published it only
updates the description. The *Run workflow* button appears once this version of the workflow is on the default
branch; `gh workflow run` works before that.

## Afterwards

- the release page: the tag is on the intended commit, six files (`AdMng-X.Y.Z-win-x64.zip`, three `.deb`, the OpenWEC
  source tarball, `SHA256SUMS.txt`), marked as pre-release for `0.x`;
- `sha256sum --ignore-missing -c SHA256SUMS.txt` on the downloaded files;
- `git fetch --tags`, then `tools/release/release.sh status` shows the new tag.

## Checks

`tools/release/release.sh check` runs `tools/release/check_release.py` and `tools/docs/check_docs.py` (python3 standard
library only); `release.sh` runs them before every commit it makes and the CI job `checks` on every push:

- the version agrees in all its places and the changelog starts with `@VERSION@`;
- release notes have valid names, a well-formed marker on the first line, no `{{PLACEHOLDER}}` or TODO left, and none
  is newer than `VersionPrefix`; `NEXT.md` exists with its sections;
- every Polish document and its English twin (both READMEs, `docs/` and `docs/en/`, the ADRs, the test lab READMEs)
  have the same headings – levels and numbering, in order; a document without its twin is an error. The file name map
  is `DOC_NAMES` in `check_docs.py` (keep it in step with the list at the top of `CLAUDE.md`);
- every relative link and `#anchor` in `README*.md` and `docs/**/*.md` resolves (GitHub anchor rules;
  `docs/releases` and `docs/dev/prototypes` are skipped);
- every `*.pl.resx` has the keys of its neutral file (the unit tests check this too; here it fails in seconds).

## Troubleshooting

- **"The repository secret RELEASE_TOKEN is not available"** – the plan job stops before the Windows build. Add a
  fine-grained token for this repository (Contents and Workflows: read and write) under *Settings → Secrets and
  variables → Actions → Repository secrets*, then start the release by hand.
- **"Tag vX.Y.Z already exists on …, not on …"** – the version is released. Notes of a one-step release have no marker,
  so a later change of them points at a new commit: add `<!-- release-commit: <sha of the tag> -->` as their first line
  and push; the workflow then only updates the description. A release that is really wrong gets a new patch version.
- **The draft release exists but the tag was not created** – the summary of the run says so: publish the draft on
  GitHub (*Releases → vX.Y.Z → Edit → Publish release*) or start the workflow by hand.
- **The `checks` job is red** – run `tools/release/release.sh check` locally; every line names the file and line. A
  pair of documents drifted apart, a link or anchor broke, or a version was changed in one place only.
- **`release.sh` stopped half-way** – it restores the files it touched; fix what it reported and run it again.

## What stays manual on purpose

- writing the notes: what is new, the known limitations, the English summary;
- `git push` – the script never pushes;
- the wording of the Watchdog changelog entry when the service changed (`--watchdog-note`), and the sections
  "AdMng Watchdog" and "Aktualizacja z" of the notes in that case;
- pre-releases with a suffix (`1.0.0-rc.1`) and the first stable release (the preview-release sentence leaves the
  READMEs): edit the files by hand, the workflow accepts such tags as before;
- README pictures (the CI job `readme-screenshots`) and publishing a draft the workflow could not publish.
