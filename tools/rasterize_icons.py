#!/usr/bin/env python3
"""
Renders every modifier SVG to a 128x128 white-on-transparent PNG, for the custom HUD's PNG icon layer.

    python3 tools/rasterize_icons.py

hud/panorama/images/custom_game/csroll/*.svg  ->  hud/panorama/images/custom_game/csroll_png/*.png

The HUD draws each icon twice, stacked: once from the SVG, once from this PNG. Whichever of the two CS2
loads as a CSS background shows; if both do, they overlap exactly. Once it's known which one works in
game, the other can go.

The icons are plain polygons (absolute M/L/Z path commands, fill-rule evenodd), so this rasterizes
them exactly with Pillow - no SVG library needed. Each subpath is XORed into the mask, which is what
evenodd means, and the shape is drawn 8x oversize and scaled down for clean edges.
"""
from __future__ import annotations

import pathlib
import re
import sys

from PIL import Image, ImageChops, ImageDraw

ROOT = pathlib.Path(__file__).resolve().parent.parent
SRC = ROOT / "hud/panorama/images/custom_game/csroll"
DST = ROOT / "hud/panorama/images/custom_game/csroll_png"
SIZE = 128
OVERSAMPLE = 8
VIEWBOX = 64.0


def subpaths(d: str) -> list[list[tuple[float, float]]]:
    if re.search(r"[^MLZmlz0-9.,\s-]", d) or re.search(r"[mlz]", d):
        raise ValueError("only absolute M/L/Z path commands are supported")
    shapes: list[list[tuple[float, float]]] = []
    for chunk in re.split(r"Z", d):
        nums = [float(n) for n in re.findall(r"-?\d+(?:\.\d+)?", chunk)]
        if len(nums) >= 6:
            shapes.append(list(zip(nums[0::2], nums[1::2])))
    return shapes


def render(svg: pathlib.Path) -> Image.Image:
    text = svg.read_text(encoding="utf-8")
    d = re.search(r' d="([^"]+)"', text)
    if not d:
        raise ValueError("no path data")

    big = SIZE * OVERSAMPLE
    scale = big / VIEWBOX
    mask = Image.new("L", (big, big), 0)
    for shape in subpaths(d.group(1)):
        layer = Image.new("L", (big, big), 0)
        ImageDraw.Draw(layer).polygon([(x * scale, y * scale) for x, y in shape], fill=255)
        mask = ImageChops.logical_xor(mask.convert("1"), layer.convert("1")).convert("L")

    alpha = mask.resize((SIZE, SIZE), Image.LANCZOS)
    icon = Image.new("RGBA", (SIZE, SIZE), (255, 255, 255, 0))
    icon.putalpha(alpha)
    return icon


def main() -> None:
    DST.mkdir(parents=True, exist_ok=True)
    count = 0
    for svg in sorted(SRC.glob("*.svg")):
        try:
            render(svg).save(DST / f"{svg.stem}.png", optimize=True)
            count += 1
        except Exception as ex:  # noqa: BLE001 - report every bad file, not just the first
            sys.exit(f"{svg.name}: {ex}")
    print(f"wrote {count} PNGs to {DST.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
