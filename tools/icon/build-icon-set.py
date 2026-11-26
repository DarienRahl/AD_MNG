#!/usr/bin/env python3
"""Builds the AD Manager icon set for WPF from the SVG sources in docs/assets/icons.

    tools/icon/build-icon-set.py              validate, then write Icons.xaml, AppIcons.cs, the state variants
                                              and the gallery in docs/assets/icons/README.md
    tools/icon/build-icon-set.py --check      validate and fail when a generated file is out of date (CI, no Chromium)
    tools/icon/build-icon-set.py --validate   validate only
    tools/icon/build-icon-set.py --preview    render preview-light.png / preview-dark.png (contact sheets)
    tools/icon/build-icon-set.py --roundtrip  prove the SVG -> XAML conversion by pixels (see below)

Inputs
    docs/assets/icons/README.md    the palette table (between the "palette" markers) - the only place colours are defined
    docs/assets/icons/icons.json   groups, icon names, meanings, state variants (base + overlay)
    docs/assets/icons/<group>/<name>.svg
    src/AdMng.Presentation/Icons/ObjectIcons.cs   the ObjectIcon enum (every value needs an icon)

Outputs
    src/AdMng.App/Themes/Icons.xaml      one DrawingImage "Icon.<PascalCaseName>" per icon
    src/AdMng.App/Controls/AppIcons.cs   the resource keys as constants, KeyFor(ObjectIcon), Find(key)
    docs/assets/icons/<group>/<variant>.svg   state variants = base drawing + overlay badge
    docs/assets/icons/README.md          the gallery (between the "gallery" markers)

The sources use a small SVG subset that maps one to one onto WPF drawings (validated here, documented in the README):
<svg>, <defs>, <linearGradient gradientUnits="userSpaceOnUse"> with <stop>, <path> with absolute commands and an
explicit fill-rule, <rect rx>, <circle>, <ellipse>; fill by palette colour or gradient; optional opacity; strokes with
explicit width, line cap and line join.

Round trip (the only check of the XAML that runs on Linux): the generated Icons.xaml is read back by an independent
reader that follows WPF's rules and defaults (EvenOdd unless "F1", flat caps and miter joins unless the Pen says
otherwise, bounding-box gradients unless MappingMode="Absolute", ...), turned into an SVG again and rendered next to
the source with Chromium at 96 px. The pixels must match. Canary mutations of the XAML (dropping MappingMode, the
line caps, the line joins, swapping the fill rules) must each be detected, which shows the comparison can fail.

Python 3 standard library only. --preview and --roundtrip need Node with Playwright (Chromium), like build-icon.sh.
"""
from __future__ import annotations

import argparse
import fnmatch
import json
import math
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ICONS_DIR = ROOT / "docs" / "assets" / "icons"
README = ICONS_DIR / "README.md"
MANIFEST = ICONS_DIR / "icons.json"
ICONS_XAML = ROOT / "src" / "AdMng.App" / "Themes" / "Icons.xaml"
APP_ICONS_CS = ROOT / "src" / "AdMng.App" / "Controls" / "AppIcons.cs"
OBJECT_ICONS_CS = ROOT / "src" / "AdMng.Presentation" / "Icons" / "ObjectIcons.cs"
SCRIPT_NAME = "tools/icon/build-icon-set.py"

SIZE = 48                    # the viewBox is 0 0 48 48
MARGIN = 2                   # safe margin: nothing is drawn outside 2..46
MIN_DETAIL = 2.5             # thinnest stroke / bar at 48 px (0.83 px at 16 px)
MIN_RADIUS = 2               # smallest corner radius of a rectangle
MAX_HUES = 3                 # colour families per icon, white not counted (state variants: one more)
LIGHT_SURFACE = "#F3F3F3"
DARK_SURFACE = "#202020"
MIN_SURFACE_CONTRAST_LIGHT = 1.4   # a colour below this contrast against a surface cannot stand alone on it
MIN_SURFACE_CONTRAST_DARK = 1.8
PREVIEW_SIZES = (16, 20, 24, 32, 48)
ROUNDTRIP_SIZE = 96
ROUNDTRIP_TOLERANCE = 6      # largest allowed difference of a premultiplied channel (0..255)

SVG_NS = "http://www.w3.org/2000/svg"
XAML_NS = "http://schemas.microsoft.com/winfx/2006/xaml/presentation"
XAML_X_NS = "http://schemas.microsoft.com/winfx/2006/xaml"
XAML_OPTIONS_NS = "http://schemas.microsoft.com/winfx/2006/xaml/presentation/options"
XAML_MC_NS = "http://schemas.openxmlformats.org/markup-compatibility/2006"

NUMBER = re.compile(r"-?\d+(?:\.\d{1,3})?\Z")
COLOR = re.compile(r"#[0-9A-F]{6}\Z")
NAME = re.compile(r"[a-z][a-z0-9]*(?:-[a-z0-9]+)*\Z")
PATH_ARGS = {"M": 2, "L": 2, "H": 1, "V": 1, "C": 6, "S": 4, "Q": 4, "T": 2, "A": 7, "Z": 0}


class IconError(Exception):
    """A source file breaks a rule of the icon set."""


# ----------------------------------------------------------------------------------------------------------------------
# Model
# ----------------------------------------------------------------------------------------------------------------------

@dataclass(frozen=True)
class Gradient:
    x1: float
    y1: float
    x2: float
    y2: float
    stops: tuple  # ((offset, "#RRGGBB"), ...)


@dataclass(frozen=True)
class Stroke:
    paint: object  # "#RRGGBB" or Gradient
    width: float
    cap: str       # butt | round | square
    join: str      # round | bevel


@dataclass(frozen=True)
class Shape:
    kind: str      # path | rect | circle | ellipse
    data: tuple    # path: (fill_rule, ((command, (numbers...)), ...)); rect: (x, y, w, h, rx); circle: (cx, cy, r);
                   # ellipse: (cx, cy, rx, ry)
    fill: object   # "#RRGGBB", Gradient or None
    stroke: Stroke | None
    opacity: float


@dataclass(frozen=True)
class Icon:
    name: str
    group: str
    meaning: str
    shapes: tuple
    base: str | None = None
    overlay: str | None = None

    @property
    def pascal(self) -> str:
        return "".join(part.capitalize() for part in self.name.split("-"))

    @property
    def key(self) -> str:
        return "Icon." + self.pascal

    @property
    def path(self) -> Path:
        return ICONS_DIR / self.group / (self.name + ".svg")


def fmt(value: float) -> str:
    """Fixed number formatting: at most three decimals, no exponent, no trailing zeros, no negative zero."""
    text = f"{value:.3f}".rstrip("0").rstrip(".")
    return "0" if text in ("-0", "") else text


# ----------------------------------------------------------------------------------------------------------------------
# Palette (README table) and manifest
# ----------------------------------------------------------------------------------------------------------------------

def section(text: str, marker: str, source: Path) -> tuple[int, int]:
    start, end = f"<!-- {marker}:start -->", f"<!-- {marker}:end -->"
    if text.count(start) != 1 or text.count(end) != 1 or text.index(start) > text.index(end):
        raise IconError(f"{rel(source)}: the markers '{start}' and '{end}' must appear once, in this order")
    return text.index(start) + len(start), text.index(end)


def read_palette() -> dict[str, tuple[str, str]]:
    """The palette of the README: colour -> (token, family)."""
    text = README.read_text(encoding="utf-8")
    start, end = section(text, "palette", README)
    palette: dict[str, tuple[str, str]] = {}
    for line in text[start:end].splitlines():
        cells = [cell.strip().strip("`") for cell in line.strip().strip("|").split("|")]
        if len(cells) < 3 or not cells[1].startswith("#"):
            continue
        token, color, family = cells[0], cells[1], cells[2]
        if not COLOR.match(color):
            raise IconError(f"{rel(README)}: palette colour '{color}' of '{token}' must be written as #RRGGBB in upper case")
        if color in palette:
            raise IconError(f"{rel(README)}: palette colour {color} is listed twice")
        palette[color] = (token, family)
    if not palette:
        raise IconError(f"{rel(README)}: the palette table is empty")
    return palette


def read_manifest() -> list[dict]:
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    groups = manifest.get("groups")
    if not isinstance(groups, list) or not groups:
        raise IconError(f"{rel(MANIFEST)}: 'groups' must be a non-empty list")
    seen: set[str] = set()
    for group in groups:
        if not NAME.match(str(group.get("id", ""))) or not group.get("title") or not isinstance(group.get("icons"), list):
            raise IconError(f"{rel(MANIFEST)}: every group needs an id (kebab-case), a title and a list of icons")
        for entry in group["icons"]:
            name = str(entry.get("name", ""))
            if not NAME.match(name):
                raise IconError(f"{rel(MANIFEST)}: icon name '{name}' is not kebab-case")
            if name in seen:
                raise IconError(f"{rel(MANIFEST)}: icon '{name}' is listed twice")
            if not str(entry.get("meaning", "")).strip() or "|" in entry["meaning"]:
                raise IconError(f"{rel(MANIFEST)}: icon '{name}' needs a meaning (without '|')")
            if ("base" in entry) != ("overlay" in entry):
                raise IconError(f"{rel(MANIFEST)}: icon '{name}': a state variant names both 'base' and 'overlay'")
            unknown = set(entry) - {"name", "meaning", "base", "overlay"}
            if unknown:
                raise IconError(f"{rel(MANIFEST)}: icon '{name}' has unknown properties {sorted(unknown)}")
            seen.add(name)
    return groups


def rel(path: Path) -> str:
    try:
        return path.resolve().relative_to(ROOT).as_posix()
    except ValueError:
        return str(path)


# ----------------------------------------------------------------------------------------------------------------------
# SVG subset: parsing and validation
# ----------------------------------------------------------------------------------------------------------------------

def number(element: ET.Element, attribute: str, what: str) -> float:
    text = element.get(attribute)
    if text is None:
        raise IconError(f"<{what}> needs the attribute {attribute}")
    if not NUMBER.match(text):
        raise IconError(f"<{what} {attribute}=\"{text}\">: expected a plain number with at most three decimals "
                        "(no units, percentages or exponents)")
    return float(text)


def parse_path(d: str) -> tuple:
    """Path data of the subset: absolute commands only, every argument a plain number."""
    tokens = re.findall(r"[A-Za-z]|[^A-Za-z\s,]+", d)
    segments: list[tuple[str, tuple]] = []
    index = 0
    while index < len(tokens):
        command = tokens[index]
        index += 1
        if command not in PATH_ARGS:
            if command.upper() in PATH_ARGS:
                raise IconError(f"path command '{command}' is relative; only absolute commands are allowed")
            raise IconError(f"path data: unexpected '{command}'")
        if not segments and command != "M":
            raise IconError("path data must start with M")
        if segments and segments[-1][0] == "Z" and command != "M":
            raise IconError("path data: after Z a new subpath must start with M")
        count = PATH_ARGS[command]
        if count == 0:
            segments.append((command, ()))
            continue
        first = True
        while first or (index < len(tokens) and not tokens[index].isalpha()):
            arguments = tokens[index:index + count]
            if len(arguments) < count or any(a.isalpha() for a in arguments):
                raise IconError(f"path command {command} needs {count} numbers")
            for argument in arguments:
                if not NUMBER.match(argument):
                    raise IconError(f"path data: '{argument}' is not a plain number with at most three decimals")
            values = tuple(float(a) for a in arguments)
            if command == "A":
                if values[3] not in (0, 1) or values[4] not in (0, 1):
                    raise IconError("path command A: the large-arc and sweep flags must be 0 or 1")
                if values[0] <= 0 or values[1] <= 0:
                    raise IconError("path command A: the radii must be positive")
            # An M with more than one pair continues with implicit L commands; write them explicitly.
            segments.append(("L" if command == "M" and not first else command, values))
            index += count
            first = False
    if not segments:
        raise IconError("path data is empty")
    return tuple(segments)


def arc_center(x1, y1, rx, ry, rotation, large, sweep, x2, y2):
    """Endpoint to centre parameterization (SVG 1.1 F.6.5). Returns (cx, cy, theta1, delta, rx, ry, scale)."""
    phi = math.radians(rotation)
    cos, sin = math.cos(phi), math.sin(phi)
    dx, dy = (x1 - x2) / 2, (y1 - y2) / 2
    xp, yp = cos * dx + sin * dy, -sin * dx + cos * dy
    scale = (xp * xp) / (rx * rx) + (yp * yp) / (ry * ry)
    if scale > 1:
        rx, ry = rx * math.sqrt(scale), ry * math.sqrt(scale)
    numerator = max(0.0, rx * rx * ry * ry - rx * rx * yp * yp - ry * ry * xp * xp)
    denominator = rx * rx * yp * yp + ry * ry * xp * xp
    factor = 0.0 if denominator == 0 else math.sqrt(numerator / denominator) * (-1 if large == sweep else 1)
    cxp, cyp = factor * rx * yp / ry, -factor * ry * xp / rx
    cx, cy = cos * cxp - sin * cyp + (x1 + x2) / 2, sin * cxp + cos * cyp + (y1 + y2) / 2
    theta1 = math.atan2((yp - cyp) / ry, (xp - cxp) / rx)
    theta2 = math.atan2((-yp - cyp) / ry, (-xp - cxp) / rx)
    delta = theta2 - theta1
    if sweep and delta < 0:
        delta += 2 * math.pi
    elif not sweep and delta > 0:
        delta -= 2 * math.pi
    return cx, cy, theta1, delta, rx, ry, scale


def path_points(segments: tuple) -> list[tuple[float, float]]:
    """Points on the outline (curves sampled), for the bounds check. Rejects arcs whose radii would be scaled."""
    points: list[tuple[float, float]] = []
    x = y = start_x = start_y = 0.0
    control: tuple[float, float] | None = None  # last control point, for S and T
    previous = ""
    for command, a in segments:
        if command == "M":
            x, y = a
            start_x, start_y = x, y
            points.append((x, y))
        elif command == "L":
            x, y = a
            points.append((x, y))
        elif command == "H":
            x = a[0]
            points.append((x, y))
        elif command == "V":
            y = a[0]
            points.append((x, y))
        elif command in ("C", "S"):
            if command == "C":
                x1, y1, x2, y2, ex, ey = a
            else:
                x2, y2, ex, ey = a
                x1, y1 = (2 * x - control[0], 2 * y - control[1]) if previous in ("C", "S") and control else (x, y)
            for step in range(1, 25):
                t = step / 24
                u = 1 - t
                points.append((u ** 3 * x + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t ** 3 * ex,
                               u ** 3 * y + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t ** 3 * ey))
            control = (x2, y2)
            x, y = ex, ey
        elif command in ("Q", "T"):
            if command == "Q":
                x1, y1, ex, ey = a
            else:
                ex, ey = a
                x1, y1 = (2 * x - control[0], 2 * y - control[1]) if previous in ("Q", "T") and control else (x, y)
            for step in range(1, 25):
                t = step / 24
                u = 1 - t
                points.append((u * u * x + 2 * u * t * x1 + t * t * ex, u * u * y + 2 * u * t * y1 + t * t * ey))
            control = (x1, y1)
            x, y = ex, ey
        elif command == "A":
            rx, ry, rotation, large, sweep, ex, ey = a
            if (ex, ey) != (x, y):
                cx, cy, theta, delta, rx, ry, scale = arc_center(x, y, rx, ry, rotation, large, sweep, ex, ey)
                if scale > 1.02:
                    raise IconError(f"path command A to {fmt(ex)},{fmt(ey)}: the radii are too small for the distance "
                                    "(renderers would scale them; write the real radii)")
                phi = math.radians(rotation)
                for step in range(1, 49):
                    angle = theta + delta * step / 48
                    px, py = rx * math.cos(angle), ry * math.sin(angle)
                    points.append((cx + math.cos(phi) * px - math.sin(phi) * py, cy + math.sin(phi) * px + math.cos(phi) * py))
            x, y = ex, ey
        elif command == "Z":
            x, y = start_x, start_y
        previous = command
    return points


def shape_bounds(shape: Shape) -> tuple[float, float, float, float]:
    """Bounding box including the stroke."""
    if shape.kind == "path":
        points = path_points(shape.data[1])
        left, top = min(p[0] for p in points), min(p[1] for p in points)
        right, bottom = max(p[0] for p in points), max(p[1] for p in points)
    elif shape.kind == "rect":
        x, y, w, h, _ = shape.data
        left, top, right, bottom = x, y, x + w, y + h
    elif shape.kind == "circle":
        cx, cy, r = shape.data
        left, top, right, bottom = cx - r, cy - r, cx + r, cy + r
    else:
        cx, cy, rx, ry = shape.data
        left, top, right, bottom = cx - rx, cy - ry, cx + rx, cy + ry
    if shape.stroke is not None:
        grow = shape.stroke.width / 2 * (math.sqrt(2) if shape.stroke.cap == "square" else 1)
        left, top, right, bottom = left - grow, top - grow, right + grow, bottom + grow
    return left, top, right, bottom


def parse_svg(path: Path, palette: dict) -> tuple:
    """Reads one source file and returns its shapes; raises IconError with the reason when it leaves the subset."""
    try:
        root = ET.parse(path).getroot()
    except ET.ParseError as error:
        raise IconError(f"not well-formed XML ({error})") from error

    def local(element: ET.Element) -> str:
        if not element.tag.startswith("{" + SVG_NS + "}"):
            raise IconError(f"element <{element.tag}> is not in the SVG namespace")
        return element.tag[len(SVG_NS) + 2:]

    def only(element: ET.Element, allowed: set[str], what: str) -> None:
        extra = sorted(set(element.attrib) - allowed)
        if extra:
            raise IconError(f"<{what}>: attribute '{extra[0]}' is not part of the subset")
        if (element.text or "").strip() or (element.tail or "").strip():
            raise IconError(f"<{what}>: text is not allowed")

    def color(value: str, what: str) -> str:
        if not COLOR.match(value):
            raise IconError(f"{what}: '{value}' must be a palette colour written as #RRGGBB in upper case")
        if value not in palette:
            raise IconError(f"{what}: {value} is not a palette colour (see the palette table in the README)")
        return value

    if local(root) != "svg":
        raise IconError("the root element must be <svg>")
    only(root, {"viewBox", "width", "height"}, "svg")
    if root.get("viewBox") != f"0 0 {SIZE} {SIZE}":
        raise IconError(f"<svg> needs viewBox=\"0 0 {SIZE} {SIZE}\"")
    if root.get("width", str(SIZE)) != str(SIZE) or root.get("height", str(SIZE)) != str(SIZE):
        raise IconError(f"<svg>: width and height, when given, are {SIZE}")

    gradients: dict[str, Gradient] = {}
    used_gradients: set[str] = set()
    shapes: list[Shape] = []

    def paint(value: str | None, what: str, allow_none: bool):
        if value is None:
            raise IconError(f"{what} must be given explicitly")
        if value == "none":
            if not allow_none:
                raise IconError(f"{what} cannot be 'none'")
            return None
        reference = re.fullmatch(r"url\(#([A-Za-z][A-Za-z0-9_-]*)\)", value)
        if reference:
            if reference.group(1) not in gradients:
                raise IconError(f"{what}: gradient '{reference.group(1)}' is not defined in <defs>")
            used_gradients.add(reference.group(1))
            return gradients[reference.group(1)]
        return color(value, what)

    for position, child in enumerate(root):
        tag = local(child)
        if tag == "defs":
            if position != 0:
                raise IconError("<defs> must be the first child of <svg> (and appear once)")
            only(child, set(), "defs")
            for definition in child:
                if local(definition) != "linearGradient":
                    raise IconError(f"<defs> may only hold <linearGradient>, not <{local(definition)}>")
                only(definition, {"id", "gradientUnits", "x1", "y1", "x2", "y2"}, "linearGradient")
                identifier = definition.get("id", "")
                if not re.fullmatch(r"[A-Za-z][A-Za-z0-9_-]*", identifier) or identifier in gradients:
                    raise IconError(f"<linearGradient id=\"{identifier}\">: the id is missing, malformed or used twice")
                if definition.get("gradientUnits") != "userSpaceOnUse":
                    raise IconError(f"<linearGradient id=\"{identifier}\"> needs gradientUnits=\"userSpaceOnUse\"")
                stops = []
                for stop in definition:
                    if local(stop) != "stop":
                        raise IconError(f"<linearGradient> may only hold <stop>, not <{local(stop)}>")
                    only(stop, {"offset", "stop-color"}, "stop")
                    offset = number(stop, "offset", "stop")
                    if not 0 <= offset <= 1 or (stops and offset < stops[-1][0]):
                        raise IconError("<stop>: offsets are numbers from 0 to 1 in ascending order")
                    stops.append((offset, color(stop.get("stop-color", ""), "<stop stop-color>")))
                if len(stops) < 2:
                    raise IconError(f"<linearGradient id=\"{identifier}\"> needs at least two stops")
                x1, y1, x2, y2 = (number(definition, a, "linearGradient") for a in ("x1", "y1", "x2", "y2"))
                if (x1, y1) == (x2, y2):
                    raise IconError(f"<linearGradient id=\"{identifier}\">: start and end are the same point")
                gradients[identifier] = Gradient(x1, y1, x2, y2, tuple(stops))
            continue

        common = {"fill", "opacity", "stroke", "stroke-width", "stroke-linecap", "stroke-linejoin"}
        if tag == "path":
            only(child, common | {"d", "fill-rule"}, "path")
            if child.get("fill-rule") not in ("nonzero", "evenodd"):
                raise IconError("<path> needs an explicit fill-rule (nonzero or evenodd)")
            if child.get("d") is None:
                raise IconError("<path> needs the attribute d")
            data: tuple = (child.get("fill-rule"), parse_path(child.get("d", "")))
        elif tag == "rect":
            only(child, common | {"x", "y", "width", "height", "rx"}, "rect")
            x, y, w, h, rx = (number(child, a, "rect") for a in ("x", "y", "width", "height", "rx"))
            if w < MIN_DETAIL or h < MIN_DETAIL:
                raise IconError(f"<rect> of {fmt(w)} x {fmt(h)} is thinner than {MIN_DETAIL} px")
            if rx > min(w, h) / 2 or rx < min(MIN_RADIUS, min(w, h) / 2):
                raise IconError(f"<rect rx=\"{fmt(rx)}\">: the corner radius must be at least {MIN_RADIUS} "
                                "(or half of the shorter side) and at most half of the shorter side")
            data = (x, y, w, h, rx)
        elif tag == "circle":
            only(child, common | {"cx", "cy", "r"}, "circle")
            data = tuple(number(child, a, "circle") for a in ("cx", "cy", "r"))
            if data[2] < MIN_DETAIL / 2:
                raise IconError(f"<circle r=\"{fmt(data[2])}\"> is smaller than {MIN_DETAIL} px")
        elif tag == "ellipse":
            only(child, common | {"cx", "cy", "rx", "ry"}, "ellipse")
            data = tuple(number(child, a, "ellipse") for a in ("cx", "cy", "rx", "ry"))
            if min(data[2], data[3]) < MIN_DETAIL / 2:
                raise IconError(f"<ellipse> is thinner than {MIN_DETAIL} px")
        else:
            raise IconError(f"element <{tag}> is not part of the subset")
        if len(child):
            raise IconError(f"<{tag}> cannot have child elements")

        fill = paint(child.get("fill"), f"<{tag} fill>", allow_none=True)
        stroke = None
        if child.get("stroke") is not None:
            for attribute in ("stroke-width", "stroke-linecap", "stroke-linejoin"):
                if child.get(attribute) is None:
                    raise IconError(f"<{tag}> with a stroke needs an explicit {attribute}")
            width = number(child, "stroke-width", tag)
            cap, join = child.get("stroke-linecap"), child.get("stroke-linejoin")
            if cap not in ("butt", "round", "square"):
                raise IconError(f"<{tag}>: stroke-linecap must be butt, round or square")
            if join not in ("round", "bevel"):
                raise IconError(f"<{tag}>: stroke-linejoin must be round or bevel (miter joins differ between SVG and WPF)")
            stroke_paint = paint(child.get("stroke"), f"<{tag} stroke>", allow_none=False)
            outline = fill is not None and fill == stroke_paint  # a stroke that only rounds the corners of its fill
            if width < (1 if outline else MIN_DETAIL):
                raise IconError(f"<{tag}>: a stroke of {fmt(width)} px is thinner than {MIN_DETAIL} px")
            stroke = Stroke(stroke_paint, width, cap, join)
        elif any(child.get(a) is not None for a in ("stroke-width", "stroke-linecap", "stroke-linejoin")):
            raise IconError(f"<{tag}> has stroke attributes but no stroke")
        if fill is None and stroke is None:
            raise IconError(f"<{tag}> paints nothing (no fill and no stroke)")
        opacity = 1.0
        if child.get("opacity") is not None:
            opacity = number(child, "opacity", tag)
            if not 0 < opacity <= 1:
                raise IconError(f"<{tag}>: opacity must be above 0 and at most 1")
        shapes.append(Shape(tag, data, fill, stroke, opacity))

    if not shapes:
        raise IconError("the icon is empty")
    unused = sorted(set(gradients) - used_gradients)
    if unused:
        raise IconError(f"gradient '{unused[0]}' is defined but not used")
    return tuple(shapes)


def luminance(color: str) -> float:
    """Relative luminance (WCAG) of #RRGGBB."""
    channels = [int(color[i:i + 2], 16) / 255 for i in (1, 3, 5)]
    red, green, blue = (c / 12.92 if c <= 0.03928 else ((c + 0.055) / 1.055) ** 2.4 for c in channels)
    return 0.2126 * red + 0.7152 * green + 0.0722 * blue


def contrast(first: str, second: str) -> float:
    lighter, darker = sorted((luminance(first), luminance(second)), reverse=True)
    return (lighter + 0.05) / (darker + 0.05)


def needs_base(color: str) -> bool:
    """White, the pale tints and the darkest tones vanish on one of the two surfaces when they stand alone."""
    return contrast(color, LIGHT_SURFACE) < MIN_SURFACE_CONTRAST_LIGHT or contrast(color, DARK_SURFACE) < MIN_SURFACE_CONTRAST_DARK


def validate_drawing(icon: Icon, palette: dict) -> None:
    """Rules that need the geometry: safe margin, pale and dark colours only on a base, number of hues."""
    covered: tuple[float, float, float, float] | None = None
    for shape in icon.shapes:
        left, top, right, bottom = shape_bounds(shape)
        if min(left, top) < MARGIN - 0.01 or max(right, bottom) > SIZE - MARGIN + 0.01:
            raise IconError(f"a <{shape.kind}> reaches {fmt(left)},{fmt(top)} .. {fmt(right)},{fmt(bottom)}: "
                            f"outside the safe margin ({MARGIN} .. {SIZE - MARGIN})")
        paints = [p for p in (shape.fill, shape.stroke.paint if shape.stroke else None) if p is not None]
        colors = [c for p in paints for c in ([s[1] for s in p.stops] if isinstance(p, Gradient) else [p])]
        if all(needs_base(c) for c in colors):
            if covered is None or left < covered[0] - 0.5 or top < covered[1] - 0.5 \
                    or right > covered[2] + 0.5 or bottom > covered[3] + 0.5:
                token = palette[colors[0]][0]
                raise IconError(f"a <{shape.kind}> in {token} is not on a coloured base: white, the pale tints and the "
                                "darkest tones must not form the outer silhouette (they vanish on one of the surfaces)")
        else:
            covered = (left, top, right, bottom) if covered is None else \
                (min(covered[0], left), min(covered[1], top), max(covered[2], right), max(covered[3], bottom))
    families = {palette[c][1] for c in icon_colors(icon)} - {"white"}
    limit = MAX_HUES + (1 if icon.base else 0)
    if len(families) > limit:
        raise IconError(f"uses {len(families)} hues ({', '.join(sorted(families))}); at most {limit}")


def icon_colors(icon: Icon) -> set[str]:
    colors: set[str] = set()
    for shape in icon.shapes:
        for p in (shape.fill, shape.stroke.paint if shape.stroke else None):
            if isinstance(p, Gradient):
                colors.update(s[1] for s in p.stops)
            elif p is not None:
                colors.add(p)
    return colors


# ----------------------------------------------------------------------------------------------------------------------
# SVG writer (state variants)
# ----------------------------------------------------------------------------------------------------------------------

def path_data_svg(segments: tuple) -> str:
    return " ".join(command if not a else command + " " + " ".join(fmt(v) for v in a) for command, a in segments)


def write_svg(shapes: tuple, comment: str | None = None) -> str:
    """The canonical form of an icon in the subset (gradients named a, b, ... in order of use)."""
    ids: dict[Gradient, str] = {}

    def reference(p) -> str:
        if isinstance(p, Gradient):
            if p not in ids:
                ids[p] = "abcdefghijklmnopqrstuvwxyz"[len(ids)]
            return f"url(#{ids[p]})"
        return "none" if p is None else p

    body = []
    for shape in shapes:
        if shape.kind == "path":
            attributes = f'fill-rule="{shape.data[0]}" d="{path_data_svg(shape.data[1])}"'
        elif shape.kind == "rect":
            x, y, w, h, rx = (fmt(v) for v in shape.data)
            attributes = f'x="{x}" y="{y}" width="{w}" height="{h}" rx="{rx}"'
        elif shape.kind == "circle":
            cx, cy, r = (fmt(v) for v in shape.data)
            attributes = f'cx="{cx}" cy="{cy}" r="{r}"'
        else:
            cx, cy, rx, ry = (fmt(v) for v in shape.data)
            attributes = f'cx="{cx}" cy="{cy}" rx="{rx}" ry="{ry}"'
        attributes += f' fill="{reference(shape.fill)}"'
        if shape.stroke is not None:
            attributes += (f' stroke="{reference(shape.stroke.paint)}" stroke-width="{fmt(shape.stroke.width)}"'
                           f' stroke-linecap="{shape.stroke.cap}" stroke-linejoin="{shape.stroke.join}"')
        if shape.opacity != 1:
            attributes += f' opacity="{fmt(shape.opacity)}"'
        body.append(f"  <{shape.kind} {attributes}/>")
    lines = [f'<svg xmlns="{SVG_NS}" viewBox="0 0 {SIZE} {SIZE}" width="{SIZE}" height="{SIZE}">']
    if comment:
        lines.append(f"  <!-- {comment} -->")
    if ids:
        lines.append("  <defs>")
        for gradient, identifier in ids.items():
            lines.append(f'    <linearGradient id="{identifier}" gradientUnits="userSpaceOnUse" x1="{fmt(gradient.x1)}" '
                         f'y1="{fmt(gradient.y1)}" x2="{fmt(gradient.x2)}" y2="{fmt(gradient.y2)}">')
            lines.extend(f'      <stop offset="{fmt(offset)}" stop-color="{color}"/>' for offset, color in gradient.stops)
            lines.append("    </linearGradient>")
        lines.append("  </defs>")
    return "\n".join(lines + body + ["</svg>"]) + "\n"


# ----------------------------------------------------------------------------------------------------------------------
# Loading the set
# ----------------------------------------------------------------------------------------------------------------------

@dataclass
class IconSet:
    palette: dict
    groups: list          # [(id, title, [Icon, ...]), ...]
    variants: dict        # path -> expected content of the generated state variant files

    @property
    def icons(self) -> list[Icon]:
        return [icon for _, _, icons in self.groups for icon in icons]


def load(errors: list[str]) -> IconSet:
    """Reads palette, manifest and sources. Problems of single icons are collected in errors."""
    palette = read_palette()
    manifest = read_manifest()
    entries = {entry["name"]: (group["id"], entry) for group in manifest for entry in group["icons"]}
    parsed: dict[str, Icon] = {}
    variants: dict[Path, str] = {}

    def fail(path: Path, error: Exception) -> None:
        errors.append(f"{rel(path)}: {error}")

    def build(name: str) -> Icon | None:
        if name in parsed:
            return parsed[name]
        group, entry = entries[name]
        path = ICONS_DIR / group / (name + ".svg")
        try:
            if "base" in entry:
                parts = []
                for role in ("base", "overlay"):
                    if entry[role] not in entries or "base" in entries[entry[role]][1]:
                        raise IconError(f"{role} '{entry[role]}' is not a drawn icon of the manifest")
                    part = build(entry[role])
                    if part is None:
                        return None
                    parts.append(part)
                shapes = parts[0].shapes + parts[1].shapes
                variants[path] = write_svg(shapes, f"Generated by {SCRIPT_NAME}: {entry['base']} + {entry['overlay']}. "
                                                   "Edit those files, not this one.")
            else:
                if not path.is_file():
                    raise IconError("the source file is missing")
                shapes = parse_svg(path, palette)
            icon = Icon(name, group, entry["meaning"].strip(), shapes, entry.get("base"), entry.get("overlay"))
            validate_drawing(icon, palette)
        except IconError as error:
            fail(path, error)
            return None
        parsed[name] = icon
        return icon

    groups = []
    for group in manifest:
        icons = [icon for icon in (build(entry["name"]) for entry in group["icons"]) if icon is not None]
        groups.append((group["id"], group["title"], icons))

    listed = {ICONS_DIR / group / (name + ".svg") for name, (group, _) in entries.items()}
    for path in sorted(ICONS_DIR.rglob("*.svg")):
        if path not in listed:
            errors.append(f"{rel(path)}: not listed in {rel(MANIFEST)} (add it to a group or delete the file)")
    return IconSet(palette, groups, variants)


def object_icon_values() -> list[str]:
    """The members of the ObjectIcon enum, in declaration order."""
    text = OBJECT_ICONS_CS.read_text(encoding="utf-8")
    match = re.search(r"public enum ObjectIcon\s*\{(.*?)\n\}", text, re.S)
    if not match:
        raise IconError(f"{rel(OBJECT_ICONS_CS)}: the ObjectIcon enum was not found")
    body = re.sub(r"///[^\n]*|//[^\n]*", "", match.group(1))
    values = re.findall(r"^\s*([A-Z][A-Za-z0-9]*)\s*(?:=\s*\d+\s*)?,?\s*$", body, re.M)
    if not values:
        raise IconError(f"{rel(OBJECT_ICONS_CS)}: the ObjectIcon enum has no members")
    return values


# ----------------------------------------------------------------------------------------------------------------------
# XAML writer
# ----------------------------------------------------------------------------------------------------------------------

LINE_CAPS = {"butt": "Flat", "round": "Round", "square": "Square"}
LINE_JOINS = {"round": "Round", "bevel": "Bevel"}


def path_data_xaml(fill_rule: str, segments: tuple) -> str:
    """WPF path markup: F0 = EvenOdd, F1 = Nonzero; points as x,y."""
    parts = ["F1" if fill_rule == "nonzero" else "F0"]
    for command, a in segments:
        v = [fmt(value) for value in a]
        if command in ("M", "L", "T"):
            parts.append(f"{command} {v[0]},{v[1]}")
        elif command in ("H", "V"):
            parts.append(f"{command} {v[0]}")
        elif command == "C":
            parts.append(f"C {v[0]},{v[1]} {v[2]},{v[3]} {v[4]},{v[5]}")
        elif command in ("S", "Q"):
            parts.append(f"{command} {v[0]},{v[1]} {v[2]},{v[3]}")
        elif command == "A":
            parts.append(f"A {v[0]},{v[1]} {v[2]} {v[3]} {v[4]} {v[5]},{v[6]}")
        else:
            parts.append("Z")
    return " ".join(parts)


def xaml_color(color: str) -> str:
    return "#FF" + color[1:]


def xaml_gradient(gradient: Gradient, indent: str) -> list[str]:
    lines = [f'{indent}<LinearGradientBrush MappingMode="Absolute" StartPoint="{fmt(gradient.x1)},{fmt(gradient.y1)}" '
             f'EndPoint="{fmt(gradient.x2)},{fmt(gradient.y2)}">']
    lines.extend(f'{indent}  <GradientStop Offset="{fmt(offset)}" Color="{xaml_color(color)}" />'
                 for offset, color in gradient.stops)
    lines.append(f"{indent}</LinearGradientBrush>")
    return lines


def xaml_shape(shape: Shape, indent: str) -> list[str]:
    if shape.opacity != 1:
        # SVG opacity applies to the painted element as a whole (fill and stroke together): a group in WPF.
        return [f'{indent}<DrawingGroup Opacity="{fmt(shape.opacity)}">',
                *xaml_shape(Shape(shape.kind, shape.data, shape.fill, shape.stroke, 1.0), indent + "  "),
                f"{indent}</DrawingGroup>"]
    attributes = ""
    children: list[str] = []
    inner = indent + "    "
    if isinstance(shape.fill, Gradient):
        children += [f"{indent}  <GeometryDrawing.Brush>", *xaml_gradient(shape.fill, inner), f"{indent}  </GeometryDrawing.Brush>"]
    elif shape.fill is not None:
        attributes += f' Brush="{xaml_color(shape.fill)}"'
    if shape.stroke is not None:
        stroke = shape.stroke
        pen = (f'<Pen Thickness="{fmt(stroke.width)}" StartLineCap="{LINE_CAPS[stroke.cap]}" '
               f'EndLineCap="{LINE_CAPS[stroke.cap]}" LineJoin="{LINE_JOINS[stroke.join]}"')
        children.append(f"{indent}  <GeometryDrawing.Pen>")
        if isinstance(stroke.paint, Gradient):
            children += [f"{inner}{pen}>", f"{inner}  <Pen.Brush>", *xaml_gradient(stroke.paint, inner + "    "),
                         f"{inner}  </Pen.Brush>", f"{inner}</Pen>"]
        else:
            children.append(f'{inner}{pen} Brush="{xaml_color(stroke.paint)}" />')
        children.append(f"{indent}  </GeometryDrawing.Pen>")
    if shape.kind == "path":
        attributes += f' Geometry="{path_data_xaml(*shape.data)}"'
    else:
        if shape.kind == "rect":
            x, y, w, h, rx = (fmt(v) for v in shape.data)
            geometry = f'<RectangleGeometry Rect="{x},{y},{w},{h}" RadiusX="{rx}" RadiusY="{rx}" />'
        else:
            cx, cy, rx, ry = (fmt(v) for v in (shape.data if shape.kind == "ellipse" else (*shape.data, shape.data[2])))
            geometry = f'<EllipseGeometry Center="{cx},{cy}" RadiusX="{rx}" RadiusY="{ry}" />'
        children += [f"{indent}  <GeometryDrawing.Geometry>", inner + geometry, f"{indent}  </GeometryDrawing.Geometry>"]
    if not children:
        return [f"{indent}<GeometryDrawing{attributes} />"]
    return [f"{indent}<GeometryDrawing{attributes}>", *children, f"{indent}</GeometryDrawing>"]


def write_xaml(icons: list[Icon]) -> str:
    lines = [
        "<!--",
        f"  GENERATED FILE - do not edit. Written by {SCRIPT_NAME} from the SVG sources in",
        f"  {rel(ICONS_DIR)} (palette, style rules and gallery: {rel(README)}).",
        "  After changing or adding an icon run the script again; its check mode fails when this file is stale.",
        "",
        f"  One DrawingImage per icon, key \"Icon.<PascalCaseName>\" (constants: Controls/AppIcons.cs). Every drawing starts",
        f"  with a transparent {SIZE} x {SIZE} rectangle, so all icons have the same bounds and line up at any size. The",
        "  colours are fixed (the set reads on the light and on the dark theme surface); these are pictures, not glyphs.",
        "-->",
        f'<ResourceDictionary xmlns="{XAML_NS}"',
        f'                    xmlns:x="{XAML_X_NS}"',
        f'                    xmlns:mc="{XAML_MC_NS}"',
        f'                    xmlns:po="{XAML_OPTIONS_NS}"',
        '                    mc:Ignorable="po">',
    ]
    for icon in sorted(icons, key=lambda i: i.key):
        lines += [
            "",
            f"  <!-- {icon.group}/{icon.name}.svg -->",
            f'  <DrawingImage x:Key="{icon.key}" po:Freeze="True">',
            "    <DrawingImage.Drawing>",
            "      <DrawingGroup>",
            '        <GeometryDrawing Brush="Transparent">',
            "          <GeometryDrawing.Geometry>",
            f'            <RectangleGeometry Rect="0,0,{SIZE},{SIZE}" />',
            "          </GeometryDrawing.Geometry>",
            "        </GeometryDrawing>",
        ]
        for shape in icon.shapes:
            lines += xaml_shape(shape, "        ")
        lines += ["      </DrawingGroup>", "    </DrawingImage.Drawing>", "  </DrawingImage>"]
    lines += ["", "</ResourceDictionary>"]
    return "\n".join(lines) + "\n"


# ----------------------------------------------------------------------------------------------------------------------
# AppIcons.cs and the README gallery
# ----------------------------------------------------------------------------------------------------------------------

def write_app_icons(icon_set: IconSet) -> str:
    by_pascal = {icon.pascal: icon for icon in icon_set.icons}
    missing = [value for value in object_icon_values() if value not in by_pascal]
    if missing:
        raise IconError(f"{rel(OBJECT_ICONS_CS)}: no icon for ObjectIcon.{missing[0]} "
                        f"(expected '{kebab(missing[0])}' in {rel(MANIFEST)})")
    lines = [
        "// <auto-generated>",
        f"//   Written by {SCRIPT_NAME} from docs/assets/icons/icons.json. Do not edit: add the icon there and run",
        "//   the script again (its check mode fails when this file is stale).",
        "// </auto-generated>",
        "#nullable enable",
        "",
        "using System.Windows;",
        "using System.Windows.Media;",
        "using AdMng.Presentation.Icons;",
        "",
        "namespace AdMng.App.Controls;",
        "",
        "/// <summary>",
        "/// Resource keys of the AD Manager icon set (Themes/Icons.xaml): one <see cref=\"DrawingImage\"/> of "
        f"{SIZE} x {SIZE} units per icon,",
        "/// drawn in fixed colours that read on the light and on the dark theme surface. Sources, palette and gallery:",
        "/// docs/assets/icons.",
        "/// </summary>",
        "public static class AppIcons",
        "{",
    ]
    for _, title, icons in icon_set.groups:
        lines.append(f"    // {title}")
        for icon in icons:
            lines.append(f"    /// <summary>{xml_text(icon.meaning)}</summary>")
            lines.append(f'    public const string {icon.pascal} = "{icon.key}";')
            lines.append("")
    lines += [
        "    /// <summary>Every resource key of the set, in gallery order.</summary>",
        "    public static IReadOnlyList<string> All { get; } =",
        "    [",
    ]
    for _, _, icons in icon_set.groups:
        lines.append("        " + " ".join(icon.pascal + "," for icon in icons))
    lines += [
        "    ];",
        "",
        "    /// <summary>The resource key of an object icon (state variants have their own drawings).</summary>",
        "    public static string KeyFor(ObjectIcon icon) => icon switch",
        "    {",
    ]
    lines += [f"        ObjectIcon.{value} => {value}," for value in object_icon_values()]
    lines += [
        "        _ => Unknown,",
        "    };",
        "",
        "    /// <summary>The image of a key from the application resources; null when there is no application or no such image.</summary>",
        "    public static ImageSource? Find(string key) => Application.Current?.TryFindResource(key) as ImageSource;",
        "}",
    ]
    return "\n".join(lines) + "\n"


def kebab(pascal: str) -> str:
    return re.sub(r"(?<!^)(?=[A-Z])", "-", pascal).lower()


def xml_text(text: str) -> str:
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def write_gallery(icon_set: IconSet) -> str:
    lines = ["", f"<!-- Generated by {SCRIPT_NAME} from icons.json - edit the meanings there. -->"]
    for group, title, icons in icon_set.groups:
        lines += ["", f"### {title}", "", "| Icon | Name | Resource key | Meaning |", "|---|---|---|---|"]
        for icon in icons:
            lines.append(f'| <img src="{group}/{icon.name}.svg" width="32" height="32" alt="{icon.name}"> | '
                         f"`{icon.name}` | `{icon.key}` | {xml_text(icon.meaning)} |")
    return "\n".join(lines) + "\n\n"


def readme_with_gallery(icon_set: IconSet) -> str:
    text = README.read_text(encoding="utf-8")
    start, end = section(text, "gallery", README)
    return text[:start] + write_gallery(icon_set) + text[end:]


def outputs(icon_set: IconSet) -> dict[Path, str]:
    """Every generated file and its expected content."""
    return {
        **icon_set.variants,
        ICONS_XAML: write_xaml(icon_set.icons),
        APP_ICONS_CS: write_app_icons(icon_set),
        README: readme_with_gallery(icon_set),
    }


# ----------------------------------------------------------------------------------------------------------------------
# XAML reader (round trip): Icons.xaml -> SVG, by WPF's rules and defaults. Deliberately shares no code with the writer.
# ----------------------------------------------------------------------------------------------------------------------

class XamlReader:
    """Reads the drawing subset of WPF that Icons.xaml uses and rebuilds an SVG picture from each DrawingImage.

    WPF semantics applied here: path markup without an "F" prefix or with "F0" fills EvenOdd, "F1" Nonzero;
    Pen defaults are Thickness 1, flat caps, miter join (limit 10); LinearGradientBrush defaults are
    MappingMode RelativeToBoundingBox, StartPoint 0,0 and EndPoint 1,1; a GeometryDrawing without Brush has no fill;
    DrawingGroup.Opacity applies to the group as a whole. Anything else is rejected rather than guessed.
    """

    def __init__(self, text: str):
        self._p = "{" + XAML_NS + "}"
        self._root = ET.fromstring(text)
        if self._root.tag != self._p + "ResourceDictionary":
            raise IconError("Icons.xaml: the root is not a ResourceDictionary")

    def pictures(self) -> dict[str, str]:
        """Resource key -> SVG text."""
        result: dict[str, str] = {}
        for image in self._root:
            if image.tag != self._p + "DrawingImage":
                raise IconError(f"Icons.xaml: unexpected <{self._local(image)}> in the dictionary")
            key = image.get("{" + XAML_X_NS + "}Key")
            self._attributes(image, {"{" + XAML_X_NS + "}Key", "{" + XAML_OPTIONS_NS + "}Freeze"})
            if not key or key in result:
                raise IconError("Icons.xaml: a DrawingImage has no key or a key is used twice")
            drawing = self._single(self._single(image, "DrawingImage.Drawing"), "DrawingGroup")
            self._attributes(drawing, set())
            children = list(drawing)
            if not children or not self._is_bounds(children[0]):
                raise IconError(f"Icons.xaml: {key} does not start with the transparent {SIZE} x {SIZE} bounds rectangle")
            self._defs: list[str] = []
            body = [line for child in children[1:] for line in self._drawing(child, key)]
            defs = ["<defs>", *self._defs, "</defs>"] if self._defs else []
            result[key] = "\n".join([f'<svg xmlns="{SVG_NS}" viewBox="0 0 {SIZE} {SIZE}" width="{SIZE}" height="{SIZE}">',
                                     *defs, *body, "</svg>"])
        return result

    def _local(self, element: ET.Element) -> str:
        return element.tag[len(self._p):] if element.tag.startswith(self._p) else element.tag

    def _attributes(self, element: ET.Element, allowed: set[str]) -> None:
        extra = sorted(set(element.attrib) - allowed)
        if extra:
            raise IconError(f"Icons.xaml: <{self._local(element)}> has an attribute the reader does not know: {extra[0]}")

    def _single(self, parent: ET.Element, name: str) -> ET.Element:
        if len(parent) != 1 or parent[0].tag != self._p + name:
            raise IconError(f"Icons.xaml: <{self._local(parent)}> needs exactly one <{name}> and nothing else")
        self._attributes(parent[0], set()) if "." in name else None
        return parent[0]

    def _is_bounds(self, element: ET.Element) -> bool:
        if element.tag != self._p + "GeometryDrawing" or element.attrib != {"Brush": "Transparent"} or len(element) != 1:
            return False
        geometry = list(element[0])
        return (element[0].tag == self._p + "GeometryDrawing.Geometry" and len(geometry) == 1
                and geometry[0].tag == self._p + "RectangleGeometry" and geometry[0].attrib == {"Rect": f"0,0,{SIZE},{SIZE}"})

    def _drawing(self, element: ET.Element, key: str) -> list[str]:
        name = self._local(element)
        if name == "DrawingGroup":
            self._attributes(element, {"Opacity"})
            inner = [line for child in element for line in self._drawing(child, key)]
            return [f'<g opacity="{float(element.get("Opacity", "1"))!r}">', *inner, "</g>"]
        if name != "GeometryDrawing":
            raise IconError(f"Icons.xaml: {key}: unexpected <{name}>")
        self._attributes(element, {"Brush", "Geometry"})
        fill = self._color(element.get("Brush"), "fill") if element.get("Brush") is not None else None
        stroke = ""
        shape: str | None = None
        if element.get("Geometry") is not None:
            shape = self._path(element.get("Geometry", ""), key)
        for child in element:
            child_name = self._local(child)
            self._attributes(child, set())
            if child_name == "GeometryDrawing.Brush" and fill is None:
                fill = self._brush(self._only_child(child), "fill")
            elif child_name == "GeometryDrawing.Pen":
                stroke = self._pen(self._only_child(child))
            elif child_name == "GeometryDrawing.Geometry" and shape is None:
                shape = self._geometry(self._only_child(child))
            else:
                raise IconError(f"Icons.xaml: {key}: unexpected or repeated <{child_name}>")
        if shape is None:
            raise IconError(f"Icons.xaml: {key}: a GeometryDrawing has no geometry")
        return ["<" + shape + " " + (fill or 'fill="none"') + stroke + "/>"]

    def _only_child(self, element: ET.Element) -> ET.Element:
        if len(element) != 1:
            raise IconError(f"Icons.xaml: <{self._local(element)}> needs exactly one child")
        return element[0]

    @staticmethod
    def _color(value: str | None, attribute: str) -> str:
        """#AARRGGBB, #RRGGBB or Transparent -> SVG paint with its opacity."""
        if value == "Transparent":
            return f'{attribute}="none"'
        match = re.fullmatch(r"#([0-9A-Fa-f]{2})?([0-9A-Fa-f]{6})", value or "")
        if not match:
            raise IconError(f"Icons.xaml: colour '{value}' is not understood")
        alpha = int(match.group(1) or "FF", 16) / 255
        opacity = "" if alpha == 1 else f' {attribute}-opacity="{alpha!r}"'
        return f'{attribute}="#{match.group(2)}"{opacity}'

    def _brush(self, element: ET.Element, attribute: str) -> str:
        name = self._local(element)
        if name == "SolidColorBrush":
            self._attributes(element, {"Color"})
            return self._color(element.get("Color"), attribute)
        if name != "LinearGradientBrush":
            raise IconError(f"Icons.xaml: brush <{name}> is not understood")
        self._attributes(element, {"MappingMode", "StartPoint", "EndPoint"})
        mode = element.get("MappingMode", "RelativeToBoundingBox")
        if mode not in ("Absolute", "RelativeToBoundingBox"):
            raise IconError(f"Icons.xaml: MappingMode '{mode}' is not understood")
        x1, y1 = self._point(element.get("StartPoint", "0,0"))
        x2, y2 = self._point(element.get("EndPoint", "1,1"))
        identifier = f"g{len(self._defs)}"
        units = "userSpaceOnUse" if mode == "Absolute" else "objectBoundingBox"
        stops = []
        for stop in element:
            if self._local(stop) != "GradientStop":
                raise IconError(f"Icons.xaml: unexpected <{self._local(stop)}> in a gradient")
            self._attributes(stop, {"Offset", "Color"})
            stops.append(f'<stop offset="{float(stop.get("Offset", "0"))!r}" '
                         f'{self._color(stop.get("Color"), "stop").replace("stop=", "stop-color=")}/>')
        self._defs.append(f'<linearGradient id="{identifier}" gradientUnits="{units}" x1="{x1!r}" y1="{y1!r}" '
                          f'x2="{x2!r}" y2="{y2!r}">{"".join(stops)}</linearGradient>')
        return f'{attribute}="url(#{identifier})"'

    def _pen(self, element: ET.Element) -> str:
        if self._local(element) != "Pen":
            raise IconError(f"Icons.xaml: <{self._local(element)}> is not a Pen")
        self._attributes(element, {"Thickness", "StartLineCap", "EndLineCap", "LineJoin", "MiterLimit", "Brush"})
        start, end = element.get("StartLineCap", "Flat"), element.get("EndLineCap", "Flat")
        caps = {"Flat": "butt", "Round": "round", "Square": "square"}
        if start != end or start not in caps:
            raise IconError(f"Icons.xaml: pen caps '{start}'/'{end}' cannot be compared with SVG")
        join = element.get("LineJoin", "Miter")
        joins = {"Miter": "miter", "Round": "round", "Bevel": "bevel"}
        if join not in joins:
            raise IconError(f"Icons.xaml: LineJoin '{join}' is not understood")
        # WPF limits the miter length against half the thickness, SVG against the whole stroke width.
        miter = f' stroke-miterlimit="{float(element.get("MiterLimit", "10")) / 2!r}"' if join == "Miter" else ""
        if element.get("Brush") is not None:
            if len(element):
                raise IconError("Icons.xaml: a Pen has two brushes")
            brush = self._color(element.get("Brush"), "stroke")
        else:
            holder = self._only_child(element)
            if self._local(holder) != "Pen.Brush":
                raise IconError(f"Icons.xaml: unexpected <{self._local(holder)}> in a Pen")
            brush = self._brush(self._only_child(holder), "stroke")
        return (f' {brush} stroke-width="{float(element.get("Thickness", "1"))!r}" stroke-linecap="{caps[start]}" '
                f'stroke-linejoin="{joins[join]}"{miter}')

    def _geometry(self, element: ET.Element) -> str:
        name = self._local(element)
        if name == "RectangleGeometry":
            self._attributes(element, {"Rect", "RadiusX", "RadiusY"})
            x, y, w, h = (float(v) for v in element.get("Rect", "").split(","))
            return (f'rect x="{x!r}" y="{y!r}" width="{w!r}" height="{h!r}" rx="{float(element.get("RadiusX", "0"))!r}" '
                    f'ry="{float(element.get("RadiusY", "0"))!r}"')
        if name == "EllipseGeometry":
            self._attributes(element, {"Center", "RadiusX", "RadiusY"})
            cx, cy = self._point(element.get("Center", "0,0"))
            return (f'ellipse cx="{cx!r}" cy="{cy!r}" rx="{float(element.get("RadiusX", "0"))!r}" '
                    f'ry="{float(element.get("RadiusY", "0"))!r}"')
        raise IconError(f"Icons.xaml: geometry <{name}> is not understood")

    @staticmethod
    def _point(text: str) -> tuple[float, float]:
        x, y = text.split(",")
        return float(x), float(y)

    @staticmethod
    def _path(markup: str, key: str) -> str:
        """WPF path markup syntax (absolute commands) -> SVG path."""
        tokens = re.findall(r"F[01]|[A-Za-z]|-?\d+(?:\.\d+)?", markup)
        if "".join(tokens) != re.sub(r"[\s,]+", "", markup):
            raise IconError(f"Icons.xaml: {key}: path markup with characters the reader does not know")
        rule = "evenodd"  # WPF: FillRule.EvenOdd unless the markup starts with F1
        if tokens and tokens[0] in ("F0", "F1"):
            rule = "nonzero" if tokens[0] == "F1" else "evenodd"
            tokens = tokens[1:]
        counts = {"M": 2, "L": 2, "H": 1, "V": 1, "C": 6, "S": 4, "Q": 4, "T": 2, "A": 7, "Z": 0}
        parts: list[str] = []
        index = 0
        while index < len(tokens):
            command = tokens[index]
            if command not in counts:
                raise IconError(f"Icons.xaml: {key}: path command '{command}' is not expected in generated markup")
            arguments = tokens[index + 1:index + 1 + counts[command]]
            if len(arguments) != counts[command] or any(a[0].isalpha() for a in arguments):
                raise IconError(f"Icons.xaml: {key}: path command {command} has the wrong number of arguments")
            parts.append(" ".join([command, *arguments]))
            index += 1 + counts[command]
        return f'path fill-rule="{rule}" d="{" ".join(parts)}"'


# ----------------------------------------------------------------------------------------------------------------------
# Chromium (Node + Playwright): contact sheets and the pixel comparison
# ----------------------------------------------------------------------------------------------------------------------

NODE_SCRIPT = r"""
const { chromium } = require('playwright');
const fs = require('fs');
(async () => {
  const job = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
  const browser = await chromium.launch();
  try {
    for (const shot of job.shots || []) {
      const context = await browser.newContext({ viewport: { width: shot.width, height: 400 }, deviceScaleFactor: shot.scale });
      const page = await context.newPage();
      await page.setContent(shot.html, { waitUntil: 'load' });
      await page.screenshot({ path: shot.path, fullPage: true });
      await context.close();
    }
    if (job.pairs) {
      const page = await browser.newPage();
      await page.setContent('<html><body></body></html>');
      const results = await page.evaluate(async ({ pairs, size }) => {
        const pixels = async (svg) => {
          const image = new Image();
          image.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg);
          await image.decode();
          const canvas = document.createElement('canvas');
          canvas.width = size;
          canvas.height = size;
          const context = canvas.getContext('2d', { willReadFrequently: true });
          context.drawImage(image, 0, 0, size, size);
          return context.getImageData(0, 0, size, size).data;
        };
        const out = [];
        for (const pair of pairs) {
          const a = await pixels(pair.source);
          const b = await pixels(pair.rebuilt);
          let worst = 0, different = 0, total = 0, painted = 0;
          for (let i = 0; i < a.length; i += 4) {
            let diff = Math.abs(a[i + 3] - b[i + 3]);
            for (let c = 0; c < 3; c++) {
              diff = Math.max(diff, Math.abs(a[i + c] * a[i + 3] - b[i + c] * b[i + 3]) / 255);
            }
            worst = Math.max(worst, diff);
            total += diff;
            if (diff > 0) different++;
            if (a[i + 3] > 0) painted++;
          }
          out.push({ key: pair.key, worst, different, mean: total / (a.length / 4), coverage: painted / (a.length / 4) });
        }
        return out;
      }, { pairs: job.pairs, size: job.size });
      fs.writeFileSync(job.result, JSON.stringify(results));
    }
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exit(1); });
"""


def run_node(job: dict) -> dict | list | None:
    with tempfile.TemporaryDirectory(prefix="admng-icons-") as work:
        script, job_file, result = Path(work, "render.js"), Path(work, "job.json"), Path(work, "result.json")
        script.write_text(NODE_SCRIPT, encoding="utf-8")
        job_file.write_text(json.dumps({**job, "result": str(result)}), encoding="utf-8")
        try:
            completed = subprocess.run(["node", str(script), str(job_file)], capture_output=True, text=True, check=False)
        except FileNotFoundError as error:
            raise IconError("Node was not found; previews and the round trip need Node with Playwright (Chromium)") from error
        if completed.returncode != 0:
            raise IconError("rendering with Chromium failed:\n" + completed.stderr.strip())
        return json.loads(result.read_text(encoding="utf-8")) if result.is_file() else None


def sheet_html(icon_set: IconSet, dark: bool, columns: int, names: list[str] | None) -> tuple[str, int]:
    """One contact sheet: every icon at the preview sizes with its name, grouped."""
    surface, text, muted = (DARK_SURFACE, "#F2F2F2", "#A8A8A8") if dark else (LIGHT_SURFACE, "#1B1B1B", "#5F5F5F")
    cell = sum(PREVIEW_SIZES) + 10 * (len(PREVIEW_SIZES) - 1) + 16
    width = columns * cell + (columns - 1) * 12 + 48
    parts = [
        "<!doctype html><html><head><meta charset='utf-8'><style>",
        f"body{{margin:0;padding:24px;background:{surface};color:{text};font:12px 'Segoe UI',system-ui,sans-serif}}",
        "h1{font-size:18px;font-weight:600;margin:0 0 2px}",
        f"p{{margin:0 0 14px;color:{muted}}}",
        "h2{font-size:13px;font-weight:600;margin:22px 0 10px}",
        f".grid{{display:grid;grid-template-columns:repeat({columns},{cell}px);gap:14px 12px}}",
        ".cell{padding:8px}",
        ".row{display:flex;align-items:flex-end;gap:10px;height:48px}",
        ".row img{display:block}",
        f".name{{margin-top:6px;color:{muted};white-space:nowrap}}",
        "</style></head><body>",
        f"<h1>AD Manager icon set</h1><p>{'Dark' if dark else 'Light'} surface {surface} &middot; "
        f"{', '.join(str(s) for s in PREVIEW_SIZES)} px</p>",
    ]
    for group, title, icons in icon_set.groups:
        shown = [icon for icon in icons if names is None or icon.name in names]
        if not shown:
            continue
        parts.append(f"<h2>{xml_text(title)}</h2><div class='grid'>")
        for icon in shown:
            source = icon_set.variants.get(icon.path) or icon.path.read_text(encoding="utf-8")
            uri = "data:image/svg+xml;charset=utf-8," + percent_encode(source)
            images = "".join(f"<img src=\"{uri}\" width='{size}' height='{size}' alt=''>" for size in PREVIEW_SIZES)
            parts.append(f"<div class='cell'><div class='row'>{images}</div><div class='name'>{icon.name}</div></div>")
        parts.append("</div>")
    parts.append("</body></html>")
    return "".join(parts), width


def percent_encode(text: str) -> str:
    from urllib.parse import quote
    return quote(text, safe="")


def preview(icon_set: IconSet, out: Path, columns: int, names: list[str] | None) -> None:
    out.mkdir(parents=True, exist_ok=True)
    shots = []
    for dark in (False, True):
        html, width = sheet_html(icon_set, dark, columns, names)
        shots.append({"html": html, "width": width, "scale": 2, "path": str(out / f"preview-{'dark' if dark else 'light'}.png")})
    run_node({"shots": shots})
    for shot in shots:
        print(f"wrote {rel(Path(shot['path']))}")


def compare(icon_set: IconSet, xaml: str) -> list[dict]:
    pictures = XamlReader(xaml).pictures()
    pairs = []
    for icon in icon_set.icons:
        if icon.key not in pictures:
            raise IconError(f"Icons.xaml has no {icon.key}")
        source = icon_set.variants.get(icon.path) or icon.path.read_text(encoding="utf-8")
        pairs.append({"key": icon.key, "source": source, "rebuilt": pictures[icon.key]})
    extra = sorted(set(pictures) - {icon.key for icon in icon_set.icons})
    if extra:
        raise IconError(f"Icons.xaml has {extra[0]}, which is not an icon of the set")
    results = run_node({"pairs": pairs, "size": ROUNDTRIP_SIZE})
    assert isinstance(results, list)
    return results


def roundtrip(icon_set: IconSet) -> bool:
    """Source SVG and the SVG rebuilt from Icons.xaml must render the same pixels; canary mutations must not."""
    if not ICONS_XAML.is_file():
        raise IconError(f"{rel(ICONS_XAML)} is missing; generate it first")
    xaml = ICONS_XAML.read_text(encoding="utf-8")
    if xaml != write_xaml(icon_set.icons):
        raise IconError(f"{rel(ICONS_XAML)} is out of date; generate it first")
    results = compare(icon_set, xaml)
    worst = max(results, key=lambda r: (r["worst"], r["mean"]))
    failed = [r for r in results if r["worst"] > ROUNDTRIP_TOLERANCE]
    thinnest = min(results, key=lambda r: r["coverage"])
    fullest = max(results, key=lambda r: r["coverage"])
    print(f"round trip: {len(results)} icons at {ROUNDTRIP_SIZE} px, tolerance {ROUNDTRIP_TOLERANCE}/255 per premultiplied channel")
    print(f"  worst icon: {worst['key']} - largest difference {worst['worst']:.2f}/255, "
          f"{worst['different']} of {ROUNDTRIP_SIZE * ROUNDTRIP_SIZE} pixels differ, mean {worst['mean']:.4f}")
    print(f"  painted share of the canvas: {thinnest['coverage']:.1%} ({thinnest['key']}) .. "
          f"{fullest['coverage']:.1%} ({fullest['key']})")
    for result in failed:
        print(f"  FAILED {result['key']}: largest difference {result['worst']:.2f}/255 in {result['different']} pixels")

    # Canaries: each mutation removes something the writer must emit. The reader then falls back to WPF's default,
    # which must change pixels - otherwise the comparison would not notice a writer that forgot it.
    canaries = [
        ("gradients without MappingMode=\"Absolute\"", lambda t: t.replace(' MappingMode="Absolute"', "")),
        ("pens without line caps", lambda t: re.sub(r' StartLineCap="\w+" EndLineCap="\w+"', "", t)),
        ("pens without LineJoin", lambda t: re.sub(r' LineJoin="\w+"', "", t)),
        ("fill rules swapped", lambda t: re.sub(r'Geometry="F([01])', lambda m: 'Geometry="F' + "10"[int(m.group(1))], t)),
        ("fill rule prefix dropped", lambda t: re.sub(r'Geometry="F[01] ', 'Geometry="', t)),
    ]
    undetected = []
    for label, mutate in canaries:
        mutated = mutate(xaml)
        if mutated == xaml:
            print(f"  canary skipped ({label}): the set does not use it")
            continue
        caught = [r for r in compare(icon_set, mutated) if r["worst"] > ROUNDTRIP_TOLERANCE]
        print(f"  canary {'detected' if caught else 'NOT DETECTED'} ({label}): {len(caught)} icons differ")
        if not caught:
            undetected.append(label)
    return not failed and not undetected


# ----------------------------------------------------------------------------------------------------------------------
# Command line
# ----------------------------------------------------------------------------------------------------------------------

def main() -> int:
    parser = argparse.ArgumentParser(description="Validates the AD Manager icon set and generates its WPF resources.")
    parser.add_argument("--validate", action="store_true", help="validate the sources only")
    parser.add_argument("--check", action="store_true", help="fail when a generated file is out of date (writes nothing)")
    parser.add_argument("--preview", action="store_true", help="render the contact sheets (needs Node with Playwright)")
    parser.add_argument("--roundtrip", action="store_true", help="compare Icons.xaml with the sources by pixels (needs Node with Playwright)")
    parser.add_argument("--only", metavar="PATTERN", help="with --preview: only icons whose name matches (comma separated globs)")
    parser.add_argument("--out", metavar="DIR", type=Path, help="with --preview: write the sheets here instead of docs/assets/icons")
    parser.add_argument("--columns", type=int, default=6, help="with --preview: icons per row (default 6)")
    arguments = parser.parse_args()

    try:
        errors: list[str] = []
        icon_set = load(errors)
        if errors:
            for error in errors:
                print("error: " + error, file=sys.stderr)
            print(f"{len(errors)} problem(s) in the icon sources", file=sys.stderr)
            return 1
        count = len(icon_set.icons)
        if arguments.validate:
            print(f"{count} icons are valid")
            return 0

        if arguments.check or arguments.preview or arguments.roundtrip:
            if arguments.check:
                stale = [path for path, content in outputs(icon_set).items()
                         if not path.is_file() or path.read_text(encoding="utf-8") != content]
                if stale:
                    for path in stale:
                        print(f"error: {rel(path)} is out of date", file=sys.stderr)
                    print(f"run {SCRIPT_NAME} and commit the result", file=sys.stderr)
                    return 1
                print(f"{count} icons are valid and the generated files are up to date")
            if arguments.preview:
                names = None
                if arguments.only:
                    patterns = [p.strip() for p in arguments.only.split(",") if p.strip()]
                    names = [icon.name for icon in icon_set.icons if any(fnmatch.fnmatch(icon.name, p) for p in patterns)]
                preview(icon_set, arguments.out or ICONS_DIR, max(1, arguments.columns), names)
            if arguments.roundtrip and not roundtrip(icon_set):
                print("error: the round trip failed", file=sys.stderr)
                return 1
            return 0

        written = 0
        for path, content in outputs(icon_set).items():
            if not path.is_file() or path.read_text(encoding="utf-8") != content:
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(content, encoding="utf-8", newline="\n")
                print(f"wrote {rel(path)}")
                written += 1
        print(f"{count} icons, {written} file(s) updated")
        return 0
    except IconError as error:
        print(f"error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
