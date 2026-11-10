#!/usr/bin/env python3
"""Release notes of AD Manager: the draft docs/releases/NEXT.md and what `release.sh` makes of it.

  notes.py render --version V --previous P [--marker SHA] [--openwec-changed] [--output FILE]
      docs/releases/NEXT.md -> docs/releases/v<V>.md; refuses while a TODO comment, a placeholder or an empty
      section is left
  notes.py state      one line about the draft: "template", "draft, N TODO comment(s) left" or "ready"
  notes.py reset      a fresh docs/releases/NEXT.md from tools/release/NEXT.template.md

Python 3 standard library only. Used by tools/release/release.sh and tools/release/check_release.py.
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DRAFT = "docs/releases/NEXT.md"
TEMPLATE = "tools/release/NEXT.template.md"
OPENWEC_SCRIPT = "tools/packaging/build-openwec-deb.sh"

VERSION = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")
MARKER = re.compile(r"^[0-9a-fA-F]{7,40}$")
DRAFT_HEADER = re.compile(r"\A\s*<!--\s*DRAFT\b.*?-->[ \t]*\n?", re.S)
TODO = re.compile(r"<!--\s*TODO\b.*?-->", re.S)
COMMENT = re.compile(r"<!--.*?-->", re.S)
# Placeholders of the release tools are upper case; "{{Termin}}" and other message template fields are product text.
PLACEHOLDER = re.compile(r"\{\{[A-Z][A-Z0-9_]*\}\}")
FENCE = re.compile(r"^\s*(```|~~~)")
# Sections every release has (prefix of the "## " heading) and the ones of which at least one must be there.
REQUIRED_SECTIONS = ["Pobieranie", "AdMng Watchdog", "Aktualizacja z", "Znane ograniczenia", "English summary"]
CHANGE_SECTIONS = ["Nowe:", "Inne zmiany"]
WATCHDOG_UNCHANGED = "- Bez zmian w działaniu"


def sections(text: str) -> list[tuple[int, str, str]]:
    """(line number, heading, body) of every "## " section; the text before the first one has the heading ""."""
    found: list[tuple[int, str, list[str]]] = [(1, "", [])]
    fenced = False
    for number, line in enumerate(text.splitlines(), 1):
        if FENCE.match(line):
            fenced = not fenced
        if not fenced and line.startswith("## "):
            found.append((number, line[3:].strip(), []))
        else:
            found[-1][2].append(line)
    return [(number, heading, "\n".join(body)) for number, heading, body in found]


def without_comments(text: str) -> str:
    return COMMENT.sub("", text).strip()


def line_of(text: str, position: int) -> int:
    return text.count("\n", 0, position) + 1


def problems(text: str, final: bool) -> list[str]:
    """What is wrong with the notes. A draft only needs its sections; final notes must be complete."""
    found = []
    parts = sections(text)
    headings = [without_comments(heading) for _, heading, _ in parts[1:]]
    for required in REQUIRED_SECTIONS:
        if not any(heading.startswith(required) for heading in headings):
            found.append(f"section '## {required}' is missing")
    if not any(heading.startswith(tuple(CHANGE_SECTIONS)) for heading in headings):
        found.append("neither a '## Nowe: ...' section nor '## Inne zmiany' is there")
    if not final:
        return found
    for match in TODO.finditer(text):
        found.append(f"line {line_of(text, match.start())}: {' '.join(match.group(0).split())[:90]}")
    for match in PLACEHOLDER.finditer(TODO.sub("", text)):
        found.append(f"unfilled placeholder {match.group(0)}")
    for number, heading, body in parts:
        title = without_comments(heading)
        if heading and (not title or title.endswith(":")):
            found.append(f"line {number}: the heading '## {title}' has no title")
        if not without_comments(body):
            found.append(f"line {number}: " + (f"section '## {title}' is empty" if heading else "the introduction is empty"))
    return found


def openwec_version() -> str:
    match = re.search(r"^UPSTREAM_VERSION=(\S+)$", (ROOT / OPENWEC_SCRIPT).read_text(encoding="utf-8"), re.M)
    if not match:
        raise SystemExit(f"notes.py: UPSTREAM_VERSION not found in {OPENWEC_SCRIPT}")
    return match.group(1).strip("\"'")


def downloads(version: str, watchdog_changed: bool, openwec_changed: bool) -> str:
    """The download table and the checksum paragraph. GitHub publishes '+' and '~' in asset names as '.' and '-'
    (release.yml, "Collect the release files")."""
    openwec = openwec_version().replace("+", ".")
    watchdog_note = "zmiany opisuje rozdział niżej" if watchdog_changed else "bez zmian w działaniu (pakiet zbudowany ponownie)"
    openwec_note = "" if openwec_changed else " – bez zmian"
    return "\n".join([
        "| Plik | Co to jest |",
        "|---|---|",
        f"| `AdMng-{version}-win-x64.zip` | AD Manager – `AdMng.exe` (wersja samodzielna: bez instalacji, .NET i RSAT) "
        "z bibliotekami natywnymi oraz `LICENSE` i `NOTICE`; rozpakuj całe archiwum do jednego folderu |",
        f"| `admng-watchdog_{version}-1_amd64.deb` | usługa AdMng Watchdog – {watchdog_note} |",
        f"| `admng-openwec_{openwec}-1-deb13_amd64.deb` | odbiornik zdarzeń OpenWEC dla Debiana 13{openwec_note} |",
        f"| `admng-openwec_{openwec}-1-deb12_amd64.deb` | to samo dla Debiana 12 |",
        f"| `admng-openwec-{openwec}-source.tar.xz` | kod źródłowy OpenWEC (GPL-3.0) i nasz przepis budowania pakietu |",
        "| `SHA256SUMS.txt` | sumy kontrolne wszystkich plików |",
        "",
        "Sumy sprawdzisz poleceniem `sha256sum --ignore-missing -c SHA256SUMS.txt` (Debian) albo",
        f"`(Get-FileHash .\\AdMng-{version}-win-x64.zip).Hash.ToLower()` (PowerShell). Pliki nie są jeszcze podpisane cyfrowo",
        "(SmartScreen, AppLocker lub WDAC mogą zablokować `AdMng.exe`). Zrzuty ekranu są dołączane tylko do wydań głównych",
        "(X.0.0) i na żądanie; aktualne obrazy interfejsu są w README.",
    ])


def render(draft: str, version: str, previous: str, marker: str | None, openwec_changed: bool) -> str:
    found = problems(draft, final=False) + [p for p in problems(draft, final=True) if "placeholder" not in p]
    if found:
        raise SystemExit(f"{DRAFT} is not ready to be released:\n  " + "\n  ".join(dict.fromkeys(found)))
    text = DRAFT_HEADER.sub("", draft, count=1).lstrip("\n")
    watchdog = next((body for _, heading, body in sections(text) if heading.startswith("AdMng Watchdog")), "")
    watchdog_changed = not without_comments(watchdog).startswith(WATCHDOG_UNCHANGED)
    text = text.replace("{{DOWNLOADS}}", downloads(version, watchdog_changed, openwec_changed))
    text = text.replace("{{VERSION}}", version).replace("{{PREVIOUS}}", previous)
    left = PLACEHOLDER.findall(text)
    if left:
        raise SystemExit(f"{DRAFT}: unknown placeholder(s) {', '.join(sorted(set(left)))}")
    head = f"<!-- release-commit: {marker} -->\n" if marker else ""
    return head + text.rstrip("\n") + "\n"


def state() -> str:
    draft_path, template_path = ROOT / DRAFT, ROOT / TEMPLATE
    if not draft_path.is_file():
        return "missing"
    draft = draft_path.read_text(encoding="utf-8")
    if template_path.is_file() and draft == template_path.read_text(encoding="utf-8"):
        return "template (nothing written yet)"
    todos = len(TODO.findall(draft))
    return f"draft, {todos} TODO comment(s) left" if todos else "ready (no TODO comment left)"


def main() -> int:
    parser = argparse.ArgumentParser(description="Release notes draft of AD Manager.")
    commands = parser.add_subparsers(dest="command", required=True)
    render_command = commands.add_parser("render")
    render_command.add_argument("--version", required=True)
    render_command.add_argument("--previous", required=True)
    render_command.add_argument("--marker")
    render_command.add_argument("--openwec-changed", action="store_true")
    render_command.add_argument("--output", type=Path)
    commands.add_parser("state")
    commands.add_parser("reset")
    arguments = parser.parse_args()

    if arguments.command == "state":
        print(state())
    elif arguments.command == "reset":
        (ROOT / DRAFT).write_text((ROOT / TEMPLATE).read_text(encoding="utf-8"), encoding="utf-8")
    else:
        for name, value in (("--version", arguments.version), ("--previous", arguments.previous)):
            if not VERSION.match(value):
                raise SystemExit(f"notes.py: {name} '{value}' is not <major>.<minor>.<patch>")
        if arguments.marker and not MARKER.match(arguments.marker):
            raise SystemExit(f"notes.py: --marker '{arguments.marker}' is not a commit id of 7-40 hex digits")
        if not (ROOT / DRAFT).is_file():
            raise SystemExit(f"notes.py: {DRAFT} is missing (restore it with: notes.py reset)")
        text = render((ROOT / DRAFT).read_text(encoding="utf-8"), arguments.version, arguments.previous,
                      arguments.marker, arguments.openwec_changed)
        output = arguments.output or ROOT / "docs" / "releases" / f"v{arguments.version}.md"
        output.write_text(text, encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
