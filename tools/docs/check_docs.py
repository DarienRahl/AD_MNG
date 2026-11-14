#!/usr/bin/env python3
"""Consistency checks of the documentation (python3 standard library only).

  structure  every Polish document and its English twin have the same headings: same levels and the same
             numbering, in the same order (text in code blocks is ignored); a document without its twin is an error
  links      every relative link and #anchor in README*.md and docs/**/*.md resolves (GitHub anchor rules)
  resx       every *.pl.resx under src/ has the same keys as its neutral (English) file

Usage: check_docs.py [--root DIR] [structure] [links] [resx]      (no check named = all of them)
Exit code 0 when everything is consistent, 1 otherwise. Run by `tools/release/release.sh check` and the CI job "checks".
"""
from __future__ import annotations

import argparse
import difflib
import os
import re
import sys
import unicodedata
import xml.etree.ElementTree as ET
from pathlib import Path
from urllib.parse import unquote

# Polish file in docs/ -> English file in docs/en/. Names that are the same in both languages need no entry.
# Keep in step with the list at the top of CLAUDE.md.
DOC_NAMES = {
    "KOMPUTERY.md": "COMPUTERS.md",
    "DZIENNIKI-ZDARZEN.md": "EVENT-LOGS.md",
    "ZASADY-GRUPY.md": "GROUP-POLICY.md",
    "KORESPONDENCJA.md": "CORRESPONDENCE.md",
    "PILOTAZ.md": "PILOT.md",
    "CERTYFIKATY.md": "CERTIFICATES.md",
    "ZAKLADANIE-KONT.md": "CREATING-ACCOUNTS.md",
    "CELE.md": "GOALS.md",
}
# Directories whose README.md must have a README.pl.md (a README.pl.md always needs its README.md).
BILINGUAL_README_DIRS = ["", "tools/testlab", "tools/testlab/watchdog"]
# Not checked for links: release notes (they link to tags that do not exist yet) and prototypes.
LINK_EXCLUDED = ("docs/releases/", "docs/dev/prototypes/")
PRUNED_DIRS = {".git", "bin", "obj", "node_modules", "artifacts", "TestResults", ".vs", ".idea"}

FENCE = re.compile(r"^\s*(```|~~~)")
ATX_HEADING = re.compile(r"^ {0,3}(#{1,6})[ \t]+(.*?)(?:[ \t]+#+)?[ \t]*$")
HTML_HEADING = re.compile(r"<h([1-6])\b[^>]*>(.*?)</h\1>", re.I)
# "7.", "2.14", "K7", "A8.", "U1c", "B)" – what a heading is numbered with; a lone letter counts only with "." or ")".
NUMBERING = re.compile(r"^(\d+(?:\.\d+)*|[A-Z]\d+[a-z]?(?:\.\d+)*|[A-Z](?=[.)]))[.):]?(?=\s|$)")
INLINE_CODE = re.compile(r"`[^`]*`")
MD_LINK = re.compile(r"\]\(\s*<?([^)\s>]+)>?(?:\s+[\"'][^)]*)?\)")
REF_DEFINITION = re.compile(r"^ {0,3}\[[^\]]+\]:\s*<?([^\s>]+)")
HTML_ATTRIBUTE = re.compile(r"\b(href|src|srcset)\s*=\s*\"([^\"]*)\"", re.I)
EXPLICIT_ANCHOR = re.compile(r"<[a-z][a-z0-9]*\b[^>]*?\b(?:id|name)\s*=\s*\"([^\"]+)\"", re.I)
URL_SCHEME = re.compile(r"^(?:[a-z][a-z0-9+.-]*:|//)", re.I)


class Report:
    def __init__(self, root: Path) -> None:
        self.root = root
        self.errors = 0
        self._annotate = os.environ.get("GITHUB_ACTIONS") == "true"

    def error(self, path: str, line: int, message: str) -> None:
        self.errors += 1
        where = f"{path}:{line}" if line else path
        print(f"  {where}: {message}")
        if self._annotate:
            position = f"file={path},line={line}" if line else f"file={path}"
            print(f"::error {position}::{message}")


def read_lines(path: Path) -> list[str]:
    return path.read_text(encoding="utf-8-sig").splitlines()


def prose_lines(path: Path):
    """Yields (line number, line) of everything outside fenced code blocks."""
    fence = None
    for number, line in enumerate(read_lines(path), 1):
        match = FENCE.match(line)
        if match:
            if fence is None:
                fence = match.group(1)
            elif fence == match.group(1):
                fence = None
            continue
        if fence is None:
            yield number, line


def headings(path: Path) -> list[tuple[int, int, str]]:
    """(line number, level, text as written) of every heading outside code blocks."""
    found = []
    for number, line in prose_lines(path):
        match = ATX_HEADING.match(line)
        if match:
            found.append((number, len(match.group(1)), match.group(2)))
            continue
        for html in HTML_HEADING.finditer(line):
            found.append((number, int(html.group(1)), html.group(2)))
    return found


def numbering(text: str) -> str:
    match = NUMBERING.match(plain_text(text))
    return match.group(1) if match else ""


def plain_text(heading: str) -> str:
    """The heading as GitHub renders it: no images, link targets, code ticks, emphasis marks or HTML tags."""
    text = re.sub(r"!\[[^\]]*\]\([^)]*\)", "", heading)
    text = re.sub(r"\[([^\]]*)\]\([^)]*\)", r"\1", text)
    text = re.sub(r"<[^>]+>", "", text)
    text = text.replace("`", "")
    text = re.sub(r"(?<![\w*])(\*{1,3}|_{1,3})(?=\S)(.+?)(?<=\S)\1(?![\w*])", r"\2", text)
    return text.strip()


def slug(heading: str) -> str:
    """GitHub's anchor of a heading: lower case, punctuation and symbols (emoji) dropped, spaces to hyphens."""
    kept = []
    for char in plain_text(heading).lower():
        category = unicodedata.category(char)
        if char in "-_ " or category[0] in "LM" or category in ("Nd", "Nl", "Pc"):
            kept.append("-" if char == " " else char)
    return "".join(kept)


_anchor_cache: dict[Path, set[str]] = {}


def anchors(path: Path) -> set[str]:
    if path not in _anchor_cache:
        found: set[str] = set()
        for _, _, text in headings(path):
            base = candidate = slug(text)
            count = 0
            while candidate in found:  # duplicates are numbered: "name", "name-1", "name-2"
                count += 1
                candidate = f"{base}-{count}"
            found.add(candidate)
        for _, line in prose_lines(path):
            found.update(EXPLICIT_ANCHOR.findall(line))
        found.add("top")  # GitHub scrolls to the top of the page for #top
        _anchor_cache[path] = found
    return _anchor_cache[path]


def markdown_files(root: Path) -> list[Path]:
    found = []
    for directory, subdirectories, names in os.walk(root):
        subdirectories[:] = sorted(d for d in subdirectories if d not in PRUNED_DIRS)
        found.extend(Path(directory, name) for name in sorted(names) if name.endswith(".md"))
    return found


def relative(root: Path, path: Path) -> str:
    return path.relative_to(root).as_posix()


# ----------------------------------------------------------------------------------------------- structure


def document_pairs(root: Path, report: Report) -> list[tuple[Path, Path]]:
    """(Polish, English) pairs; reports every document whose twin is missing."""
    pairs = []

    def pair(polish: Path, english: Path) -> None:
        if polish.is_file() and english.is_file():
            pairs.append((polish, english))
        elif polish.is_file():
            report.error(relative(root, polish), 0, f"has no English twin {relative(root, english)}")
        elif english.is_file():
            report.error(relative(root, english), 0, f"has no Polish twin {relative(root, polish)}")

    readme_dirs = {root / d for d in BILINGUAL_README_DIRS}
    readme_dirs.update(p.parent for p in markdown_files(root) if p.name == "README.pl.md")
    for directory in sorted(readme_dirs):
        pair(directory / "README.pl.md", directory / "README.md")

    docs, english_docs = root / "docs", root / "docs" / "en"
    english_names = {v: k for k, v in DOC_NAMES.items()}
    for polish in sorted(docs.glob("*.md")):
        pair(polish, english_docs / DOC_NAMES.get(polish.name, polish.name))
    for english in sorted(english_docs.glob("*.md")):
        polish = docs / english_names.get(english.name, english.name)
        if not polish.is_file():
            pair(polish, english)

    def by_number(directory: Path) -> dict[str, Path]:
        return {p.name[:4]: p for p in sorted(directory.glob("[0-9][0-9][0-9][0-9]-*.md"))}

    polish_adrs, english_adrs = by_number(docs / "adr"), by_number(english_docs / "adr")
    for number in sorted(set(polish_adrs) | set(english_adrs)):
        pair(polish_adrs.get(number, docs / "adr" / f"{number}-*.md"),
             english_adrs.get(number, english_docs / "adr" / f"{number}-*.md"))
    return pairs


def check_structure(root: Path, report: Report) -> None:
    pairs = document_pairs(root, report)
    for polish, english in pairs:
        left, right = headings(polish), headings(english)
        left_keys = [(level, numbering(text)) for _, level, text in left]
        right_keys = [(level, numbering(text)) for _, level, text in right]
        if left_keys == right_keys:
            continue
        polish_name, english_name = relative(root, polish), relative(root, english)

        def show(items: list[tuple[int, int, str]]) -> str:
            return "; ".join(f"line {n} '{'#' * level} {text[:60]}'" for n, level, text in items[:3]) \
                + (f" (+{len(items) - 3} more)" if len(items) > 3 else "")

        matcher = difflib.SequenceMatcher(a=left_keys, b=right_keys, autojunk=False)
        for tag, i1, i2, j1, j2 in matcher.get_opcodes():
            if tag == "equal":
                continue
            line = left[i1][0] if i1 < len(left) else (left[-1][0] if left else 0)
            if tag == "delete":
                report.error(polish_name, line, f"headings without a counterpart in {english_name}: {show(left[i1:i2])}")
            elif tag == "insert":
                report.error(polish_name, line, f"headings only in {english_name}: {show(right[j1:j2])}")
            else:
                report.error(polish_name, line, f"headings differ in level or numbering: {show(left[i1:i2])} "
                                                f"<> {english_name}: {show(right[j1:j2])}")
    print(f"structure: {len(pairs)} document pairs compared")


# --------------------------------------------------------------------------------------------------- links


def link_targets(line: str):
    line = INLINE_CODE.sub("", line)
    yield from MD_LINK.findall(line)
    definition = REF_DEFINITION.match(line)
    if definition:
        yield definition.group(1)
    for attribute, value in HTML_ATTRIBUTE.findall(line):
        if attribute.lower() == "srcset":
            yield from (part.split()[0] for part in value.split(",") if part.split())
        else:
            yield value


def check_links(root: Path, report: Report) -> None:
    files = []
    for path in markdown_files(root):
        name = relative(root, path)
        in_scope = path.name.startswith("README") or name.startswith("docs/")
        if in_scope and not name.startswith(LINK_EXCLUDED):
            files.append(path)
    checked = 0
    for path in files:
        name = relative(root, path)
        for number, line in prose_lines(path):
            for target in link_targets(line):
                if not target or URL_SCHEME.match(target) or "{{" in target:
                    continue
                checked += 1
                file_part, _, fragment = target.partition("#")
                file_part = unquote(file_part.split("?", 1)[0])
                if not file_part:
                    destination = path
                elif file_part.startswith("/"):
                    destination = root / file_part.lstrip("/")
                else:
                    destination = Path(os.path.normpath(path.parent / file_part))
                if root != destination and root not in destination.parents:
                    report.error(name, number, f"link '{target}' points outside the repository")
                elif not destination.exists():
                    report.error(name, number, f"link '{target}': {relative(root, destination)} does not exist")
                elif fragment and destination.suffix == ".md" and unquote(fragment) not in anchors(destination):
                    report.error(name, number, f"link '{target}': no heading with the anchor "
                                               f"#{unquote(fragment)} in {relative(root, destination)}")
    print(f"links: {checked} relative links and anchors in {len(files)} files")


# ---------------------------------------------------------------------------------------------------- resx


def resx_keys(path: Path) -> set[str]:
    return {data.get("name", "") for data in ET.parse(path).getroot().iter("data")}


def check_resx(root: Path, report: Report) -> None:
    pairs = 0
    for directory, subdirectories, names in os.walk(root / "src"):
        subdirectories[:] = sorted(d for d in subdirectories if d not in PRUNED_DIRS)
        for name in sorted(names):
            if not name.endswith(".pl.resx"):
                continue
            polish = Path(directory, name)
            neutral = polish.with_name(name[: -len(".pl.resx")] + ".resx")
            if not neutral.is_file():
                report.error(relative(root, polish), 0, f"has no neutral file {neutral.name}")
                continue
            pairs += 1
            polish_keys, neutral_keys = resx_keys(polish), resx_keys(neutral)
            for missing, source in ((neutral_keys - polish_keys, polish), (polish_keys - neutral_keys, neutral)):
                if missing:
                    sample = ", ".join(sorted(missing)[:8]) + (" ..." if len(missing) > 8 else "")
                    report.error(relative(root, source), 0, f"{len(missing)} keys of the other language are missing: {sample}")
    print(f"resx: {pairs} resource pairs compared")


CHECKS = {"structure": check_structure, "links": check_links, "resx": check_resx}


def main() -> int:
    parser = argparse.ArgumentParser(description="Documentation consistency checks of AD_MNG.")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2], help="repository root")
    parser.add_argument("checks", nargs="*", choices=[*CHECKS, []], help="checks to run (default: all)")
    arguments = parser.parse_args()
    root = arguments.root.resolve()
    report = Report(root)
    for name in arguments.checks or CHECKS:
        CHECKS[name](root, report)
    if report.errors:
        print(f"check_docs: {report.errors} problem(s)")
        return 1
    print("check_docs: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
