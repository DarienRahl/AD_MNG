#!/usr/bin/env bash
# Renders the AD Manager icon from its SVG sources and packs src/AdMng.App/Assets/AdMng.ico: admng-icon-small.svg
# for 16/20/24 px (fewer details, thicker shapes), admng-icon.svg for 32–256 px; 32-bit DIB frames below 256 px,
# PNG at 256 px. Also refreshes docs/assets/admng-icon-256.png. Needs Node with Playwright (Chromium) and Python 3
# with Pillow.
#
#   tools/icon/build-icon.sh
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

cat > "$work/render.js" <<'JS'
const { chromium } = require('playwright');
const fs = require('fs');
(async () => {
  const [svgFile, outPrefix, ...sizes] = process.argv.slice(2);
  const svg = fs.readFileSync(svgFile, 'utf8');
  const browser = await chromium.launch();
  const page = await browser.newPage();
  for (const size of sizes.map(Number)) {
    await page.setViewportSize({ width: size, height: size });
    await page.setContent(`<html><body style="margin:0;background:transparent">${svg.replace('width="256" height="256"', `width="${size}" height="${size}"`)}</body></html>`);
    await page.screenshot({ path: `${outPrefix}${size}.png`, omitBackground: true, clip: { x: 0, y: 0, width: size, height: size } });
  }
  await browser.close();
})();
JS

node "$work/render.js" "$here/admng-icon-small.svg" "$work/frame-" 16 20 24
node "$work/render.js" "$here/admng-icon.svg" "$work/frame-" 32 40 48 64 96 128 256

python3 - "$work" "$root/src/AdMng.App/Assets/AdMng.ico" <<'PY'
import struct, sys
from PIL import Image

work, target = sys.argv[1], sys.argv[2]
sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]


def dib(path):
    """A classic 32-bit BGRA DIB frame (bottom-up, with an empty AND mask) – read by every icon consumer."""
    image = Image.open(path).convert("RGBA")
    size = image.width
    pixels = image.tobytes("raw", "BGRA")
    rows = [pixels[y * size * 4:(y + 1) * size * 4] for y in range(size)]
    mask_row = ((size + 31) // 32) * 4
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, size * size * 4, 0, 0, 0, 0)
    return header + b"".join(reversed(rows)) + b"\0" * (mask_row * size)


# PNG only for 256 px (as Windows itself does); the smaller frames as DIBs.
frames = [(s, open(f"{work}/frame-{s}.png", "rb").read() if s >= 256 else dib(f"{work}/frame-{s}.png")) for s in sizes]
offset = 6 + 16 * len(frames)
entries, data = b"", b""
for size, frame in frames:
    dim = 0 if size >= 256 else size  # 0 means 256 in the ICO directory
    entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(frame), offset + len(data))
    data += frame
with open(target, "wb") as ico:
    ico.write(struct.pack("<HHH", 0, 1, len(frames)) + entries + data)
PY
cp "$work/frame-256.png" "$root/docs/assets/admng-icon-256.png"
echo "Written src/AdMng.App/Assets/AdMng.ico and docs/assets/admng-icon-256.png"
