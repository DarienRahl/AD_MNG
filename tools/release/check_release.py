#!/usr/bin/env python3
"""Consistency checks of everything a release touches (python3 standard library only).

  versions  VersionPrefix in Directory.Build.props, the version badges and the preview-release sentences of README.md
            and README.pl.md agree; the Debian changelog of admng-watchdog starts with the entry "@VERSION@" and
            continues with a lower, concrete version
  notes     docs/releases holds only v<major>.<minor>.<patch>[-suffix].md and NEXT.md; a release-commit marker is on
            the first line and has 7-40 hex digits (in a full clone: the marker of the current version is a commit in
            the history of HEAD); no notes are newer than VersionPrefix; no placeholder or TODO comment is left
  draft     docs/releases/NEXT.md exists and still has the sections of the template

Usage: check_release.py [--root DIR]
Exit code 0 when everything is consistent, 1 otherwise. Run by `tools/release/release.sh check` and the CI job "checks".
"""
from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
from pathlib import Path

sys.dont_write_bytecode = True  # no __pycache__ next to the tools
import notes  # noqa: E402

SEMVER = r"\d+\.\d+\.\d+"
NOTES_NAME = re.compile(rf"^v({SEMVER})(-[0-9A-Za-z.-]+)?\.md$")
# The same pattern as the plan job of .github/workflows/release.yml (sed, first line only).
MARKER_LINE = re.compile(r"^<!-- *release-commit: *([0-9a-fA-F]{7,40}) *-->")
CHANGELOG = "packaging/admng-watchdog/debian/changelog"
CHANGELOG_HEADER = re.compile(r"^admng-watchdog \(([^)]+)\) unstable; urgency=\w+$")
CHANGELOG_TRAILER = re.compile(r"^ -- .+ <[^<>@ ]+@[^<>@ ]+>  [A-Z][a-z]{2}, \d{2} [A-Z][a-z]{2} \d{4} \d{2}:\d{2}:\d{2} [+-]\d{4}$")


class Report:
    def __init__(self) -> None:
        self.errors = 0
        self._annotate = os.environ.get("GITHUB_ACTIONS") == "true"

    def error(self, path: str, message: str, line: int = 0) -> None:
        self.errors += 1
        print(f"  {path}:{line}: {message}" if line else f"  {path}: {message}")
        if self._annotate:
            print(f"::error file={path}{f',line={line}' if line else ''}::{message}")


def version_tuple(version: str) -> tuple[int, ...]:
    return tuple(int(part) for part in version.split("."))


def check_versions(root: Path, report: Report) -> str | None:
    props = (root / "Directory.Build.props").read_text(encoding="utf-8-sig")
    found = re.findall(r"<VersionPrefix>([^<]*)</VersionPrefix>", props)
    if len(found) != 1 or not notes.VERSION.match(found[0]):
        report.error("Directory.Build.props", f"expected one <VersionPrefix>major.minor.patch</VersionPrefix>, found {found}")
        return None
    version = found[0]
    preview = version.startswith("0.")  # the preview sentence goes away with the stable release
    spots = [
        ("README.md", "version badge", rf"badge/version-({SEMVER})-", True),
        ("README.md", "sentence '(<version> is a preview release)'", rf"\(({SEMVER}) is a preview release\)", preview),
        ("README.pl.md", "version badge", rf"badge/wersja-({SEMVER})-", True),
        ("README.pl.md", "sentence '(<version> jest wersją zapoznawczą)'", rf"\(({SEMVER}) jest wersją zapoznawczą\)", preview),
    ]
    for path, what, pattern, required in spots:
        lines = (root / path).read_text(encoding="utf-8-sig").splitlines()
        hits = [(number, match.group(1)) for number, line in enumerate(lines, 1) for match in re.finditer(pattern, line)]
        if not hits and required:
            report.error(path, f"the {what} is missing")
        elif len(hits) > 1:
            report.error(path, f"the {what} appears {len(hits)} times", hits[1][0])
        for number, value in hits:
            if value != version:
                report.error(path, f"the {what} says {value}, VersionPrefix is {version}", number)

    lines = (root / CHANGELOG).read_text(encoding="utf-8").splitlines()
    headers = [(number, match.group(1)) for number, line in enumerate(lines, 1) if (match := CHANGELOG_HEADER.match(line))]
    if not lines or not headers or headers[0] != (1, "@VERSION@"):
        report.error(CHANGELOG, "the first line must be the entry 'admng-watchdog (@VERSION@) unstable; urgency=medium'", 1)
    for number, value in headers[1:]:
        if value == "@VERSION@":
            report.error(CHANGELOG, "a second '@VERSION@' entry: the previous release was not made concrete", number)
    if len(headers) > 1:
        number, value = headers[1]
        match = re.fullmatch(rf"({SEMVER})-\d+", value)
        if match and version_tuple(match.group(1)) >= version_tuple(version):
            report.error(CHANGELOG, f"the entry below '@VERSION@' is {value}, not lower than VersionPrefix {version}", number)
    trailers = [line for line in lines if line.startswith(" -- ")]
    if len(trailers) != len(headers):
        report.error(CHANGELOG, f"{len(headers)} entries but {len(trailers)} ' -- maintainer  date' lines")
    for number, line in enumerate(lines, 1):
        if line.startswith(" -- ") and not CHANGELOG_TRAILER.match(line):
            report.error(CHANGELOG, "not ' -- Name <address>  Day, DD Mon YYYY HH:MM:SS +ZZZZ' (two spaces before the date)", number)
            break
    print(f"versions: {version}")
    return version


def marker_in_history(root: Path, marker: str) -> bool | None:
    """None when it cannot be told here (no git, or a shallow clone as in CI)."""
    def git(*arguments: str) -> subprocess.CompletedProcess:
        return subprocess.run(["git", "-C", str(root), *arguments], capture_output=True, text=True, check=False)

    try:
        if git("rev-parse", "--is-shallow-repository").stdout.strip() != "false":
            return None
        commit = git("rev-parse", "--verify", "--quiet", f"{marker}^{{commit}}")
        return commit.returncode == 0 and git("merge-base", "--is-ancestor", commit.stdout.strip(), "HEAD").returncode == 0
    except OSError:
        return None


def check_notes(root: Path, report: Report, version: str | None) -> None:
    directory = root / "docs" / "releases"
    count = 0
    for path in sorted(directory.iterdir()):
        name = f"docs/releases/{path.name}"
        if path.name == "NEXT.md":
            continue
        match = NOTES_NAME.match(path.name) if path.is_file() else None
        if not match:
            report.error(name, "unexpected name: release notes are v<major>.<minor>.<patch>[-suffix].md")
            continue
        count += 1
        if version and version_tuple(match.group(1)) > version_tuple(version):
            report.error(name, f"notes of a version newer than VersionPrefix {version} (was the version not raised?)")
        text = path.read_text(encoding="utf-8-sig")
        lines = text.splitlines()
        for number, line in enumerate(lines, 1):
            if "release-commit" not in line or not line.lstrip().startswith("<!--"):
                continue
            marker = MARKER_LINE.match(line)
            if number != 1:
                report.error(name, "the release-commit marker must be the first line (it is ignored anywhere else)", number)
            elif not marker:
                report.error(name, "the marker must be '<!-- release-commit: <7-40 hex digits> -->'", number)
            elif version and match.group(1) == version and marker_in_history(root, marker.group(1)) is False:
                report.error(name, f"release commit {marker.group(1)} is not in the history of HEAD", number)
        for placeholder in sorted(set(notes.PLACEHOLDER.findall(text))):
            report.error(name, f"unfilled placeholder {placeholder}")
        for todo in notes.TODO.finditer(text):
            report.error(name, "a TODO comment is left", notes.line_of(text, todo.start()))
        if not notes.without_comments(text):
            report.error(name, "the notes are empty")
    print(f"notes: {count} release notes")


def check_draft(root: Path, report: Report) -> None:
    path = root / notes.DRAFT
    if not path.is_file():
        report.error(notes.DRAFT, "the draft of the next release notes is missing (python3 tools/release/notes.py reset)")
        return
    for problem in notes.problems(path.read_text(encoding="utf-8"), final=False):
        report.error(notes.DRAFT, problem)
    if not (root / notes.TEMPLATE).is_file():
        report.error(notes.TEMPLATE, "the template of the draft is missing")
    print(f"draft: {notes.DRAFT}")


def main() -> int:
    parser = argparse.ArgumentParser(description="Release consistency checks of AD_MNG.")
    parser.add_argument("--root", type=Path, default=notes.ROOT, help="repository root")
    root = parser.parse_args().root.resolve()
    report = Report()
    version = check_versions(root, report)
    check_notes(root, report, version)
    check_draft(root, report)
    if report.errors:
        print(f"check_release: {report.errors} problem(s)")
        return 1
    print("check_release: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
