#!/usr/bin/env python3
"""
Render the CSRoll HUD to a browser-viewable HTML approximation.

    python3 tools/preview_hud.py            # writes previews/csroll_hud.preview.html
    python3 tools/preview_hud.py --open     # and opens it

WHY. The in-game loop for a padding value is: compile, publish or mount, restart, join, roll. This
gets it to about a second. It is not a renderer and it is not authoritative - Panorama's flow model
is not flexbox, `s2r://` images and the Stratum fonts do not resolve, and `clip` bars are shown as
plain widths. Judge spacing, hierarchy and colour here; judge everything else in game.

Two things it does beyond the generic previewer it wraps:

  * Unhides everything. The reveal card, the spin reel and the tracker all ship with the `hidden`
    class because the server reveals them; previewing as-authored shows an empty screen.
  * Fills the dialog variables with representative content, so rows are sized by realistic text
    rather than by the literal string "{s:name}".

Requires the cs2-panorama-hud skill's preview.py (see hud/README.md).
"""

from __future__ import annotations

import argparse
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile
import webbrowser

REPO = pathlib.Path(__file__).resolve().parent.parent
PREVIEWER = pathlib.Path.home() / ".claude/skills/cs2-panorama-hud/scripts/preview.py"

# Representative content per panel id, so the preview is sized by real text.
SAMPLE = {
    "csr_reveal_title": "MODIFIER ACTIVATED",
    "csr_card0_name": "Cluster Grenades",
    "csr_card0_desc": "Grenades spawn 1-4 mini grenades when they detonate",
    "csr_card1_name": "Vanish",
    "csr_card1_desc": "Press Inspect to vanish briefly",
    "csr_card2_name": "Jetpack",
    "csr_card2_desc": "Hold jump to fly while fuel lasts",
    "csr_card_more": "+2 more",
    "csr_track_title": "ACTIVE MODIFIERS",
    "csr_version": "csroll-hud-1",
}

# The reel shows one row through a window; give them plausible names.
REEL = ["Wallhack", "Vampire", "Bunny Hop", "Master Zeus", "Steel Body", "Drunk",
        "Infinite Ammo", "Regeneration", "Recall", "Flanker", "Poisonous Smoke",
        "Small Players", "Speedhack", "Heavy Boots", "Revive", "Saint", "Mimic",
        "Boomerang Bullets", "Hard Head", "Cluster Grenades"]

# Tracker rows: (glyph, name, readout)
ROWS = [("\u2620", "Cluster Grenades", ""),
        ("\u25cc", "Vanish", "12s"),
        ("\u25b2", "Jetpack", "64%"),
        ("\u21b6", "Recall", "READY"),
        ("\u271a", "Regeneration", "5 HP/s"),
        ("\u25c6", "+2 more", "")]


def fill(xml: str) -> str:
    """Unhide every panel and swap {s:var} placeholders for representative content."""
    # Everything is authored hidden because the server reveals it; show it all.
    xml = re.sub(r'\s+hidden(?=["\s])', "", xml)
    xml = xml.replace(' class="hidden"', ' class=""')

    def sub_text(m: re.Match) -> str:
        panel_id, cls, var = m.group(1), m.group(2), m.group(3)

        if panel_id in SAMPLE:
            value = SAMPLE[panel_id]
        elif (reel := re.fullmatch(r"csr_reel(\d+)", panel_id)):
            value = REEL[int(reel.group(1)) % len(REEL)]
        elif (row := re.fullmatch(r"csr_row(\d+)_(icon|name|time)", panel_id)):
            glyph, name, readout = ROWS[int(row.group(1)) % len(ROWS)]
            value = {"icon": glyph, "name": name, "time": readout}[row.group(2)]
        elif panel_id.startswith("csr_spec") or panel_id.startswith("csr_self"):
            value = ""  # reserved regions, nothing to show yet
        else:
            value = f"{{{var}}}"

        return f'<Label id="{panel_id}" class="{cls}" text="{value}" />'

    return re.sub(r'<Label id="([\w]+)" class="([^"]*)" text="\{s:(\w+)\}"\s*/>', sub_text, xml)


def main() -> int:
    parser = argparse.ArgumentParser(description="Preview the CSRoll HUD in a browser.")
    parser.add_argument("--open", action="store_true", help="open the result in a browser")
    parser.add_argument("--raw", action="store_true",
                        help="preview exactly as authored, without unhiding or sample text")
    args = parser.parse_args()

    if not PREVIEWER.is_file():
        print(f"previewer not found at {PREVIEWER}\n"
              "Install the cs2-panorama-hud skill - see hud/README.md.", file=sys.stderr)
        return 1

    layout = REPO / "hud/layout/csroll_hud.xml"
    style = REPO / "hud/styles/csroll_hud.css"
    out_dir = REPO / "previews"
    out_dir.mkdir(exist_ok=True)

    # The previewer resolves s2r:// stylesheet includes by walking up to a panorama/ root, which our
    # hud/ layout deliberately is not. Stage one it recognises.
    with tempfile.TemporaryDirectory() as tmp:
        root = pathlib.Path(tmp) / "panorama"
        (root / "layout/custom_game").mkdir(parents=True)
        (root / "styles/custom_game").mkdir(parents=True)

        xml = layout.read_text(encoding="utf-8")
        staged = root / "layout/custom_game/csroll_hud.xml"
        staged.write_text(xml if args.raw else fill(xml), encoding="utf-8")
        shutil.copy(style, root / "styles/custom_game/csroll_hud.css")

        result = subprocess.run([sys.executable, str(PREVIEWER), str(staged)],
                                capture_output=True, text=True)
        if result.returncode != 0:
            print(result.stdout + result.stderr, file=sys.stderr)
            return result.returncode

        produced = next(pathlib.Path(tmp).rglob("*.preview.html"), None)
        if produced is None:
            print("previewer produced no output", file=sys.stderr)
            return 1

        target = out_dir / "csroll_hud.preview.html"
        shutil.copy(produced, target)

    print(f"wrote {target.relative_to(REPO)}")
    print("Approximation only - flexbox stands in for flow-children, s2r:// images and the Stratum")
    print("fonts do not resolve, and clip-based bars render as plain blocks. Spacing and hierarchy")
    print("are meaningful here; anything else is a hypothesis for the game to confirm.")

    if args.open:
        webbrowser.open(target.as_uri())

    return 0


if __name__ == "__main__":
    sys.exit(main())
