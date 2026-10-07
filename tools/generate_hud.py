#!/usr/bin/env python3
"""
Generates the CSRoll custom HUD from one spec, so the three halves can never disagree:

    hud/panorama/layout/custom_game/csroll_hud.xml     the Panorama layout
    hud/panorama/styles/custom_game/csroll_hud.css     its stylesheet
    src/Hud/HudLayout.g.cs                             the ids, classes and timings the plugin drives

    python3 tools/generate_hud.py

Edit this file, never the outputs. A panel id or class the plugin writes that the layout lacks fails
silently in game - the write is simply dropped - so the only safe contract is one written once.

Panorama is not the web (see the cs2-panorama-hud skill): no flexbox, no calc(), #rrggbbaa colours
only, gradients in the 2008 WebKit form, keyframe names QUOTED with the brace on the same line and
declared above their first use - an unparsable keyframe throws the whole stylesheet away.
"""
from __future__ import annotations

import math
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
ICON_DIR = ROOT / "hud/icons"   # SVG sources - the HUD draws the PNGs made from them (tools/rasterize_icons.py)
LAYOUT = ROOT / "hud/panorama/layout/custom_game/csroll_hud.xml"
STYLE = ROOT / "hud/panorama/styles/custom_game/csroll_hud.css"
CONTRACT = ROOT / "src/Hud/HudLayout.g.cs"

# ---------------------------------------------------------------------------------------------------
# Spec
# ---------------------------------------------------------------------------------------------------
ROWS = 6            # active-modifier rows in the left list
GAUGES = 3          # floating gauges above the bottom HUD
GAUGE_WIDTH = 420   # px
# px from the screen's bottom edge to the gauge stack's bottom: one gauge sits in the gap between CS2's
# hint line ("You picked up the bomb", ~y 881-894 @1080p) and its health/ammo row (~y 998).
GAUGE_BOTTOM = 104
FILL_STEPS = 100    # bar resolution: 1% per step, driven by clip from the server every tick (width resets when text updates)
LIST_OFFSETS = [360, 400, 440, 480, 520]   # list top margins (px @1080p) - radar scale varies per player

TILES = 60          # carousel tiles
START_TILE = 3      # tile under the marker when the spin starts
WIN_TILE = 55       # tile the spin lands on - the only per-player tile; 4 more sit right of it at rest
TILE = 64           # px
TILE_GAP = 12       # px
REEL_WIDTH = 600    # px
REEL_PAD = 8        # px of plate above and below the tiles
REEL_TOP = 572      # px @1080p - the block ends at 736, clear of CS2's status label (~762) and the gauges
BRAND_HEIGHT = 26   # px - the caption bar above the reel
BRAND_GAP = 4       # px between it and the reel
ROLL_TOP = REEL_TOP - BRAND_HEIGHT - BRAND_GAP   # the block starts with the bar's slot, so the reel stays put
CARD_GAP = 4        # px between the reel and the caption card
CARD_HEIGHT = 80    # px, fixed so the block's bottom edge is guaranteed
NOTCH = 8           # px square, rotated 45deg and half-clipped by the plate into the marker triangles
SHADE_W = 168       # px of edge fade on each side of the reel
SHINE_W = 160       # px - the light sweep's travelling panel; the band inside it is SHINE_BAND wide
SHINE_BAND = 56     # px
SPIN_SECONDS = 6.0
# Ease-out power the server moves the reel along, 1 - (1 - t)^SPIN_POWER, and times each tick sound by.
# Its top speed is SPIN_POWER x the average - about 27px a tick, well under half a tile, so the reel
# never strobes backwards at 64 updates a second. Higher powers stop it visibly before the landing.
SPIN_POWER = 2.6

# Everything that moves is moved by the server, one class per tick: a CSS animation started by a server
# class write never ran in game. The reel's position is split in two so it can step in half pixels
# without thousands of classes (the entity interns at most 1024 class names): the strip jumps whole
# tiles (c0..cN) and a track around it slides the rest (h0..h151, half a pixel each).
FINE_PER_PX = 2

# Frame timelines on csr_roll, one class per server tick (64 a second): in0..N as the reel appears,
# win0..N from the landing, out0..N as the whole reveal fades away.
FPS = 64
IN_FRAMES = 16      # 0.25s
WIN_FRAMES = 64     # 1.0s
OUT_FRAMES = 24     # 0.375s

FALLBACK_ICON = "InfiniteRoll"

# Category -> accent. Six classes tint every icon, row edge, tile edge and bar.
CATEGORIES = {
    "move":    "#4fd1c5",
    "weap":    "#f6ad55",
    "util":    "#a3e635",
    "surv":    "#f56565",
    "stealth": "#b794f4",
    "chaos":   "#ed64a6",
}

# Rarity tier -> (CS case colour, winner glow size px). With rarity on, the server adds one rar-* class
# to the reel tiles and the card, and these out-rank the category colours there: the reel reads like
# a case, while icons, list rows and gauges keep their category tint. "rar-off" has no rules at all.
RARITIES = {
    "milspec":    ("#4b69ff", 12),
    "restricted": ("#8847ff", 12),
    "classified": ("#d32ce6", 14),
    "covert":     ("#eb4b4b", 16),
    "gold":       ("#e4ae39", 20),
}

STEP = TILE + TILE_GAP
REEL_HEIGHT = TILE + 2 * REEL_PAD
STRIP_WIDTH = TILES * STEP
STRIP_LEFT = REEL_WIDTH // 2 - (START_TILE * STEP + TILE // 2)
COARSE_STEPS = WIN_TILE - START_TILE
TRAVEL = COARSE_STEPS * STEP
FINE_STEPS = STEP * FINE_PER_PX
assert WIN_TILE + 4 < TILES, "the reel needs tiles to the right of the winner at rest"

# CS2's own font family, available once the layout includes csgostyles (below). Weight is a separate
# property - "Stratum2 Bold" is not a family name, and an unknown family silently falls back.
FONT_BOLD = "Stratum2, 'Arial Unicode MS';\n\tfont-weight: bold"
FONT_BODY = "Stratum2, 'Arial Unicode MS';\n\tfont-weight: normal"
SHADE = "#0a0d11"
GOLD = "#f3c74f"


def mix(base: str, top: str, amount: float) -> str:
    """`top` mixed `amount` into `base`, as #rrggbb."""
    a = [int(top[i:i + 2], 16) for i in (1, 3, 5)]
    b = [int(base[i:i + 2], 16) for i in (1, 3, 5)]
    return "#" + "".join(f"{round(lo + (hi - lo) * amount):02x}" for hi, lo in zip(a, b))


def premix(accent: str, amount: float = 0.12) -> str:
    """The accent mixed `amount` into SHADE - a gradient can't layer a tint over a base."""
    return mix(SHADE, accent, amount)


def icons() -> list[str]:
    names = sorted(p.stem for p in ICON_DIR.glob("*.svg"))
    if FALLBACK_ICON not in names:
        sys.exit(f"missing fallback icon {FALLBACK_ICON}.svg in {ICON_DIR}")
    return names


# ---------------------------------------------------------------------------------------------------
# Layout
# ---------------------------------------------------------------------------------------------------
def layout() -> str:
    rows = "\n".join(
        f'''        <Panel id="csr_row{i}" class="CsrRow row{i}">
          <Panel class="CsrRowBody">
            <Panel class="CsrRowTile"><Panel id="csr_row{i}_ico" class="CsrIco CsrRowIco" /></Panel>
            <Panel class="CsrRowText">
              <Label id="csr_row{i}_name" class="CsrRowName" text="{{s:name}}" />
              <Label id="csr_row{i}_short" class="CsrRowShort" text="{{s:short}}" />
            </Panel>
          </Panel>
          <Panel class="CsrRowRail" />
        </Panel>''' for i in range(ROWS))

    gauges = "\n".join(
        f'''        <Panel id="csr_g{i}" class="CsrGauge">
          <Panel class="CsrGaugeTop">
            <Panel class="CsrGaugeTile"><Panel id="csr_g{i}_ico" class="CsrIco CsrGaugeIco" /></Panel>
            <Label id="csr_g{i}_label" class="CsrGaugeLabel" text="{{s:label}}" />
            <Label id="csr_g{i}_val" class="CsrGaugeVal" text="{{s:val}}" />
          </Panel>
          <Panel class="CsrBar">
            <Panel class="CsrFill" />
            <Panel class="CsrBarTip" />
          </Panel>
        </Panel>''' for i in range(GAUGES))

    # Only the winner gets the landing flash layer, painted over its icon.
    tiles = "\n".join(
        f'''              <Panel id="csr_t{i}" class="CsrTile"><Panel id="csr_t{i}_ico" class="CsrIco CsrTileIco" />'''
        + ('<Panel class="CsrTileFlash" />' if i == WIN_TILE else "") + "</Panel>"
        for i in range(TILES))

    return f'''<!--
  CSRoll custom HUD. GENERATED by tools/generate_hud.py - edit the generator, not this file.
  Driven by the CSRoll plugin through SwiftlyS2's CCSCustomHudLayout: per-player text variables and
  per-player class toggles. Everything starts hidden; the plugin turns parts on per player.
-->
<root>
  <styles>
    <include src="s2r://panorama/styles/csgostyles.vcss_c" />
    <include src="s2r://panorama/styles/custom_game/csroll_hud.vcss_c" />
  </styles>
  <Panel class="CsrScreen" hittest="false">
    <Panel id="csr_hud" class="CsrHud" hittest="false">
      <Panel id="csr_list" class="CsrList" hittest="false">
        <Panel class="CsrListHead">
          <Label id="csr_list_title" class="CsrListTitle" text="{{s:title}}" />
          <Panel class="CsrListRule" />
        </Panel>
{rows}
      </Panel>
      <Panel id="csr_gauges" class="CsrGauges" hittest="false">
{gauges}
      </Panel>
      <Panel id="csr_roll" class="CsrRoll" hittest="false">
        <Panel class="CsrBrandClip">
          <Panel class="CsrBrand">
            <Panel class="CsrBrandRule CsrBrandRuleL" />
            <Label id="csr_brand_text" class="CsrBrandText" text="{{s:brand}}" />
            <Panel class="CsrBrandRule CsrBrandRuleR" />
          </Panel>
        </Panel>
        <Panel class="CsrReel">
          <Panel class="CsrSlot" />
          <Panel id="csr_track" class="CsrTrack">
            <Panel id="csr_strip" class="CsrStrip">
{tiles}
            </Panel>
          </Panel>
          <Panel class="CsrShade CsrShadeL" />
          <Panel class="CsrShade CsrShadeR" />
          <Panel class="CsrShine CsrShineReel"><Panel class="CsrShineBand" /></Panel>
          <Panel class="CsrRail CsrRailTop" />
          <Panel class="CsrRail CsrRailBottom" />
          <Panel class="CsrNotch CsrNotchTop" />
          <Panel class="CsrNotch CsrNotchBottom" />
        </Panel>
        <Panel class="CsrCardClip">
          <Panel id="csr_card" class="CsrCard">
            <Panel id="csr_card_ico" class="CsrIco CsrCardIco" />
            <Panel class="CsrCardText">
              <Label id="csr_card_cat" class="CsrCardCat" text="{{s:cat}}" />
              <Label id="csr_card_name" class="CsrCardName" text="{{s:name}}" />
              <Label id="csr_card_desc" class="CsrCardDesc" text="{{s:desc}}" />
            </Panel>
          </Panel>
          <Panel class="CsrShine CsrShineCard"><Panel class="CsrShineBand" /></Panel>
        </Panel>
      </Panel>
    </Panel>
    <Panel id="csr_prompt" class="CsrPrompt" hittest="false">
      <Label class="CsrPromptTitle" text="NEW HUD READY" />
      <Label class="CsrPromptText" text="Type !hud in chat to switch from the centre text to this HUD." />
    </Panel>
  </Panel>
</root>
'''


# ---------------------------------------------------------------------------------------------------
# Stylesheet
# ---------------------------------------------------------------------------------------------------
def fade(stops: list[tuple[float, str]], vertical: bool = False) -> str:
    """WebKit-form gradient from a list of (position, colour), left to right or top to bottom."""
    first, *middle, last = stops
    inner = ", ".join(f"color-stop( {p:.2f}, {c} )" for p, c in middle)
    inner = f", {inner}" if inner else ""
    end = "0% 100%" if vertical else "100% 0%"
    return f"gradient( linear, 0% 0%, {end}, from( {first[1]} ){inner}, to( {last[1]} ) )"


def num(value: float) -> str:
    """A CSS number with no trailing zeros: 0.5, 12, -37.25."""
    text = f"{value:.3f}".rstrip("0").rstrip(".")
    return "0" if text in ("-0", "") else text


def ease_out(t: float) -> float:
    return 1 - (1 - t) ** 3


def ease_in_out(t: float) -> float:
    return (1 - math.cos(math.pi * t)) / 2


def span(n: int, start: int, frames: int) -> float | None:
    """Where frame n sits (0-1) in a stretch of `frames` frames starting at `start`; None outside it."""
    return (n - start) / frames if start <= n < start + frames else None


def roll_frames() -> list[str]:
    """
    The reveal's three timelines, one class per server tick on csr_roll: in{n} as the reel appears,
    win{n} from the landing, out{n} as the whole reveal fades. The win frames also go to csr_list, for
    the list's entry. A frame only carries the properties
    that are moving in it - outside a stretch the element's resting rule applies, so the last frame of
    every timeline is also its resting state.
    """
    lines = ["/* ---------- reveal timelines (one class per tick on csr_roll) ---------- */"]
    rule = lambda sel, decls: lines.append(f"{sel} {{ {' '.join(decls)} }}")

    for n in range(IN_FRAMES):
        k = ease_out(n / IN_FRAMES)
        rule(f".CsrRoll.on.in{n}", [f"opacity: {num(k)};", f"transform: translatey( {num((1 - k) * 14)}px );"])

    drop = CARD_GAP + CARD_HEIGHT
    for n in range(WIN_FRAMES):
        f = f".CsrRoll.win{n}"
        # The winner stamps in: a white flash over its icon and a quick swell, settling in ~0.3s.
        if (t := span(n, 0, 18)) is not None:
            rule(f"{f} .CsrTileFlash", [f"opacity: {num(0.65 * (1 - ease_out(t)))};"])
        if (t := span(n, 0, 4)) is not None:
            s = 1 + 0.16 * ease_out(t)
            rule(f"{f} .CsrTile.won", [f"transform: scale3d( {num(s)}, {num(s)}, 1 );"])
        if (t := span(n, 4, 14)) is not None:
            s = 1 + 0.16 * (1 - ease_in_out(t))
            rule(f"{f} .CsrTile.won", [f"transform: scale3d( {num(s)}, {num(s)}, 1 );"])
        # The rest of the reel steps back to the `landed` look.
        if (t := span(n, 0, 20)) is not None:
            k = ease_out(t)
            rule(f".CsrRoll.landed.win{n} .CsrTile", [f"opacity: {num(1 - 0.65 * k)};", f"saturation: {num(1 - 0.7 * k)};"])
            rule(f".CsrRoll.landed.win{n} .CsrSlot", [f"opacity: {num(1 - k)};"])
        # The card slides down out of the reel's bottom edge, then its text rises in.
        if (t := span(n, 0, 22)) is not None:
            k = ease_out(t)
            rule(f"{f} .CsrCard.on", [f"transform: translatey( -{num(drop * (1 - k))}px );", f"opacity: {num(min(1.0, t * 1.8))};"])
        if (t := span(n, 6, 22)) is not None:
            k = ease_out(t)
            rule(f"{f} .CsrCardText", [f"opacity: {num(k)};", f"transform: translatey( {num((1 - k) * 8)}px );"])
        # The brand bar rises out of the reel's top edge, a beat after the card starts down.
        if (t := span(n, 6, 18)) is not None:
            k = ease_out(t)
            rule(f".CsrRoll.landed.brand.win{n} .CsrBrand", [f"transform: translatey( {num((BRAND_HEIGHT + BRAND_GAP) * (1 - k))}px );", f"opacity: {num(k)};"])

        # Light sweeps left to right, over the reel first, then the card.
        for panel, start, frames in (("CsrShineReel", 2, 36), ("CsrShineCard", 20, 38)):
            if (t := span(n, start, frames)) is not None:
                x = -SHINE_W + ease_in_out(t) * (REEL_WIDTH + SHINE_W)
                rule(f"{f} .{panel}", [f"transform: translatex( {num(x)}px );"])

    # After the card lands, the list (hidden during your own spin) comes in: the title first, then each
    # row slides in from the left, a few frames apart. Before its start a row is held invisible - one
    # grouped rule per row rather than one per frame.
    lines.append(", ".join(f".CsrRoll.landed.brand.win{n} .CsrBrand" for n in range(6)) + " { opacity: 0; }")

    list_parts = [("CsrListHead", 20, 14, 0)] + [(f"CsrRow.row{i}", 24 + 4 * i, 16, 20) for i in range(ROWS)]
    for panel, start, frames, slide in list_parts:
        lines.append(", ".join(f".CsrList.win{n} .{panel}" for n in range(start)) + " { opacity: 0; }")
        for n in range(start, start + frames):
            k = ease_out(span(n, start, frames))
            decls = [f"opacity: {num(k)};"]
            if slide:
                decls.append(f"transform: translatex( -{num(slide * (1 - k))}px );")
            rule(f".CsrList.win{n} .{panel}", decls)

    for n in range(OUT_FRAMES + 1):
        k = ease_in_out(n / OUT_FRAMES)
        rule(f".CsrRoll.on.out{n}", [f"opacity: {num(1 - k)};", f"transform: translatey( -{num(10 * k)}px );"])
    return lines


def stylesheet(icon_names: list[str]) -> str:
    out: list[str] = []
    w = out.append

    w("/* CSRoll custom HUD. GENERATED by tools/generate_hud.py - edit the generator, not this file. */")
    w("")
    w("")
    w(f""".CsrScreen
{{
	width: 100%;
	height: 100%;
	overflow: noclip;
	z-index: 99999;
}}

.CsrHud
{{
	width: 100%;
	height: 100%;
	opacity: 0;
	transition-property: opacity;
	transition-duration: 0.25s;
	transition-timing-function: ease-out;
}}

.CsrHud.on
{{
	opacity: 1;
}}

/* ---------- icons: a picture class plus a category tint ---------- */
/* PNG icons, stretched to the panel. Three sizes, each about twice the size it's drawn at. */
.CsrIco
{{
	background-size: 100% 100%;
	background-repeat: no-repeat;
	background-position: 0% 0%;
}}
""")
    # Source-file references, Panorama's documented form: compiling the stylesheet compiles each
    # referenced PNG as a child resource and rewrites the path - resourcecompiler can't compile a
    # PNG on its own ("Failed to find compiler").
    # The generic icon under every slot, so a modifier newer than the addon a player has installed
    # shows it instead of an empty tile. The per-icon rules below have one class more and win.
    w(f'.CsrGaugeIco {{ background-image: url("file://{{images}}/custom_game/csroll_png/x32/{FALLBACK_ICON}.png"); }}')
    w(f'.CsrRowIco, .CsrTileIco {{ background-image: url("file://{{images}}/custom_game/csroll_png/x64/{FALLBACK_ICON}.png"); }}')
    w(f'.CsrCardIco {{ background-image: url("file://{{images}}/custom_game/csroll_png/x160/{FALLBACK_ICON}.png"); }}')
    for name in icon_names:
        w(f'.CsrGaugeIco.ico-{name} {{ background-image: url("file://{{images}}/custom_game/csroll_png/x32/{name}.png"); }}')
        w(f'.CsrRowIco.ico-{name}, .CsrTileIco.ico-{name} {{ background-image: url("file://{{images}}/custom_game/csroll_png/x64/{name}.png"); }}')
        w(f'.CsrCardIco.ico-{name} {{ background-image: url("file://{{images}}/custom_game/csroll_png/x160/{name}.png"); }}')
    w("")
    for cat, colour in CATEGORIES.items():
        w(f".cat-{cat} .CsrIco {{ wash-color: {colour}; }}")
    w("")

    # ---------- left list ----------
    # The reel's language, turned down: the same item-card tile, a 2px category edge, and a plate that
    # is lighter than the reel's and fades out to the right, so it never boxes in the game.
    row_plate = lambda base: fade([(0, f"{base}b3"), (0.55, f"{SHADE}66"), (1, f"{SHADE}00")])
    rule_line = fade([(0, GOLD), (0.12, f"{GOLD}66"), (0.35, "#ffffff1f"), (1, "#ffffff00")])
    row_rail = fade([(0, "#ffffff1f"), (0.6, "#ffffff0a"), (1, "#ffffff00")])
    tile = fade([(0, "#ffffff0f"), (1, "#ffffff05")], vertical=True)
    w(f""".CsrList
{{
	flow-children: down;
	width: 320px;
	horizontal-align: left;
	vertical-align: top;
	margin-left: 16px;
	margin-top: {LIST_OFFSETS[0]}px;
	visibility: collapse;
}}

.CsrList.on
{{
	visibility: visible;
}}
""")
    for i, top in enumerate(LIST_OFFSETS[1:], start=1):
        w(f".CsrList.y{i} {{ margin-top: {top}px; }}")
    w(f"""
.CsrListHead
{{
	flow-children: down;
	width: 100%;
	margin-bottom: 6px;
}}

.CsrListTitle
{{
	margin-left: 2px;
	font-family: {FONT_BOLD};
	font-size: 12px;
	letter-spacing: 3px;
	text-transform: uppercase;
	color: #ffffffb3;
	text-shadow: 0px 1px 3px 1.0 #000000cc;
}}

/* The reel's rail, laid flat: a gold glint at the left fading out. */
.CsrListRule
{{
	width: 100%;
	height: 1px;
	margin-top: 4px;
	background-color: {rule_line};
}}

/* A row overlays its body and a top hairline (no flow), so the hairline can fade like the plate. */
.CsrRow
{{
	width: 100%;
	margin-bottom: 4px;
	border-left: 2px solid #ffffff66;
	background-color: {row_plate("#11151a")};
	visibility: collapse;
}}

.CsrRow.on
{{
	visibility: visible;
}}

.CsrRowRail
{{
	width: 100%;
	height: 1px;
	vertical-align: top;
	background-color: {row_rail};
}}

.CsrRowBody
{{
	flow-children: right;
	width: 100%;
	padding: 6px 12px 6px 8px;
}}

/* The reel tile, smaller: neutral top, category wash and 2px category bar (per category below). */
.CsrRowTile
{{
	width: 40px;
	height: 40px;
	vertical-align: center;
	margin-right: 10px;
	border-radius: 2px;
	border-top: 1px solid #ffffff1a;
	border-bottom: 2px solid #ffffff40;
	background-color: {tile};
}}

.CsrRowIco
{{
	width: 26px;
	height: 26px;
	horizontal-align: center;
	vertical-align: center;
}}

.CsrRowText
{{
	flow-children: down;
	width: fill-parent-flow( 1.0 );
	vertical-align: center;
}}

.CsrRowName
{{
	width: 100%;
	font-family: {FONT_BOLD};
	font-size: 17px;
	letter-spacing: 1px;
	text-transform: uppercase;
	color: #ffffff;
	white-space: nowrap;
	text-overflow: ellipsis;
	text-shadow: 0px 1px 2px 1.0 #000000b3;
}}

.CsrRowShort
{{
	width: 100%;
	font-family: {FONT_BODY};
	font-size: 14px;
	color: #ffffffa6;
	white-space: nowrap;
	text-overflow: ellipsis;
	text-shadow: 0px 1px 2px 1.0 #000000b3;
}}
""")
    for cat, colour in CATEGORIES.items():
        wash = fade([(0, "#ffffff0d"), (0.5, "#ffffff08"), (1, f"{colour}38")], vertical=True)
        w(f".CsrRow.cat-{cat} {{ border-left-color: {colour}; background-color: {row_plate(premix(colour))}; }}")
        w(f".CsrRow.cat-{cat} .CsrRowTile {{ border-bottom-color: {colour}; background-color: {wash}; }}")
    w("")

    # ---------- gauges ----------
    # No plate here - they float over the game. The reel's language is in the details: the item-card
    # tile, a hairline rail for the track, a category fill with a glowing tip, and a "won" tile when a
    # gauge is ready. The fill step class sits on the gauge itself so one write moves the fill and tip.
    track = fade([(0, "#ffffff38"), (1, "#ffffff17")], vertical=True)
    tile = fade([(0, "#ffffff0f"), (1, "#ffffff05")], vertical=True)
    w(f""".CsrGauges
{{
	flow-children: down;
	width: {GAUGE_WIDTH}px;
	horizontal-align: center;
	vertical-align: bottom;
	margin-bottom: {GAUGE_BOTTOM}px;
}}

.CsrGauge
{{
	flow-children: down;
	width: 100%;
	margin-top: 12px;
	visibility: collapse;
}}

.CsrGauge.on
{{
	visibility: visible;
}}

.CsrGaugeTop
{{
	flow-children: right;
	width: 100%;
}}

/* The list's tile, smaller. */
.CsrGaugeTile
{{
	width: 28px;
	height: 28px;
	vertical-align: center;
	margin-right: 9px;
	border-radius: 2px;
	border-top: 1px solid #ffffff1a;
	border-bottom: 2px solid #ffffff40;
	background-color: {tile};
	box-shadow: #00000066 0px 1px 4px 0px;
}}

.CsrGaugeIco
{{
	width: 18px;
	height: 18px;
	horizontal-align: center;
	vertical-align: center;
}}

/* Ready: the tile lights up like the reel's winner, its icon turns white. */
.CsrGauge.ready .CsrGaugeTile .CsrIco
{{
	wash-color: #ffffff;
}}

.CsrGaugeLabel
{{
	width: fill-parent-flow( 1.0 );
	vertical-align: center;
	font-family: {FONT_BOLD};
	font-size: 15px;
	letter-spacing: 2px;
	text-transform: uppercase;
	color: #ffffffe0;
	white-space: nowrap;
	text-overflow: ellipsis;
	text-shadow: 0px 1px 4px 1.5 #000000e6;
}}

.CsrGaugeVal
{{
	vertical-align: center;
	font-family: {FONT_BOLD};
	font-size: 15px;
	letter-spacing: 1px;
	color: #ffffff;
	white-space: nowrap;
	text-shadow: 0px 1px 4px 1.5 #000000e6;
}}

/* The track: a hairline-lit rail. noclip, so the taller tip can stand proud of it. */
.CsrBar
{{
	width: 100%;
	height: 4px;
	margin-top: 6px;
	overflow: noclip;
	border-radius: 1px;
	background-color: {track};
	box-shadow: #000000a6 0px 0px 4px 0px;
}}

.CsrFill
{{
	width: 100%;
	height: 100%;
	border-radius: 1px;
	background-color: #ffffff;
	clip: rect( 0%, 0%, 100%, 0% );
}}

/* A bright tick riding the fill's edge, glowing in the category colour. */
.CsrBarTip
{{
	width: 2px;
	height: 10px;
	vertical-align: center;
	background-color: #ffffff;
	box-shadow: #ffffffb3 0px 0px 6px 0px;
}}

.CsrGauge.f0 .CsrBarTip
{{
	opacity: 0;
}}
""")
    for step in range(FILL_STEPS + 1):
        pct = step * 100 // FILL_STEPS
        tip = max(0, round(GAUGE_WIDTH * pct / 100) - 2)
        w(f".CsrGauge.f{step} .CsrFill {{ clip: rect( 0%, {pct}%, 100%, 0% ); }}")
        w(f".CsrGauge.f{step} .CsrBarTip {{ transform: translatex( {tip}px ); }}")
    w("")
    for cat, colour in CATEGORIES.items():
        fill = fade([(0, mix(colour, "#ffffff", 0.35)), (1, colour)], vertical=True)
        won = fade([(0, f"{colour}33"), (1, f"{colour}73")], vertical=True)
        w(f".CsrGauge.cat-{cat} .CsrFill {{ background-color: {fill}; }}")
        w(f".CsrGauge.cat-{cat} .CsrBarTip {{ box-shadow: {colour} 0px 0px 6px 1px; }}")
        w(f".CsrGauge.cat-{cat} .CsrGaugeTile {{ border-bottom-color: {colour}; }}")
        w(f".CsrGauge.ready.cat-{cat} .CsrGaugeTile {{ border: 1px solid {colour}; border-bottom: 2px solid {colour}; background-color: {won}; box-shadow: {colour}80 0px 0px 8px 0px; }}")
        w(f".CsrGauge.ready.cat-{cat} .CsrGaugeVal {{ color: {colour}; }}")
    w("")

    # ---------- roll ----------
    # No transitions: server class writes don't start them here, so every state is drawn to look right
    # the moment it switches.
    plate = fade([(0, f"#11151ad1"), (1, f"{SHADE}d9")], vertical=True)
    slot = fade([(0, f"{GOLD}29"), (0.22, f"{GOLD}0a"), (0.78, f"{GOLD}0a"), (1, f"{GOLD}29")], vertical=True)
    shade_l = fade([(0, f"{SHADE}f5"), (0.45, f"{SHADE}99"), (1, f"{SHADE}00")])
    shade_r = fade([(0, f"{SHADE}00"), (0.55, f"{SHADE}99"), (1, f"{SHADE}f5")])
    rail = fade([(0, "#ffffff14"), (0.40, "#ffffff33"), (0.47, "#ffffff40"), (0.50, GOLD),
                 (0.53, "#ffffff40"), (0.60, "#ffffff33"), (1, "#ffffff14")])
    tile = fade([(0, "#ffffff0f"), (1, "#ffffff05")], vertical=True)
    shine = fade([(0, "#ffffff00"), (0.35, "#ffffff14"), (0.5, "#ffffff40"), (0.65, "#ffffff14"), (1, "#ffffff00")])
    w(f""".CsrRoll
{{
	flow-children: down;
	width: {REEL_WIDTH}px;
	horizontal-align: center;
	vertical-align: top;
	margin-top: {ROLL_TOP}px;
	opacity: 0;
}}

.CsrRoll.on
{{
	opacity: 1;
}}

/* The plate: a crisp dark translucent rectangle, like CS2's own status boxes. Its children overlay in
   paint order - slot, track, shades, shine, rails, notches. */
.CsrReel
{{
	width: {REEL_WIDTH}px;
	height: {REEL_HEIGHT}px;
	overflow: clip clip;
	background-color: {plate};
	box-shadow: #00000066 0px 6px 18px 0px;
}}

/* Lit centre slot, painted BEHIND the strip: it tints the centre tile but never covers its icon. */
.CsrSlot
{{
	width: {STEP}px;
	height: 100%;
	horizontal-align: center;
	background-color: {slot};
}}

/* The reel's position in two parts: the track slides up to one tile in half pixels (h0..hN), the
   strip inside it jumps whole tiles (c0..cN). */
.CsrTrack
{{
	width: {STRIP_WIDTH}px;
	height: {TILE}px;
	margin-left: {STRIP_LEFT}px;
	margin-top: {REEL_PAD}px;
}}

.CsrStrip
{{
	flow-children: right;
	width: {STRIP_WIDTH}px;
	height: {TILE}px;
}}

/* An item card: neutral top, category wash at the bottom, 1px top highlight, 2px category bar.
   Panorama draws borders inside the box, so they never change STEP. */
.CsrTile
{{
	width: {TILE}px;
	height: {TILE}px;
	margin-right: {TILE_GAP}px;
	border-radius: 2px;
	border-top: 1px solid #ffffff1a;
	border-bottom: 2px solid #ffffff40;
	background-color: {tile};
}}

.CsrTileIco
{{
	width: 40px;
	height: 40px;
	horizontal-align: center;
	vertical-align: center;
}}

/* Winner: full ring + glow + solid fill (category values below). Its landing pop is in the win frames. */
.CsrTile.won
{{
	border: 2px solid #ffffff;
	background-color: #ffffff26;
	box-shadow: #ffffff40 0px 0px 12px 0px;
}}

/* Three classes, so it beats the two-class .cat-* .CsrIco tint above. */
.CsrTile.won .CsrIco
{{
	wash-color: #ffffff;
}}

/* Only the winning tile has one: a white flash over the icon at the landing, faded by the win frames. */
.CsrTileFlash
{{
	width: 100%;
	height: 100%;
	border-radius: 2px;
	background-color: #ffffff;
	opacity: 0;
}}

/* Edge vignette, IN FRONT of the strip: outer tiles fade into the plate. */
.CsrShade
{{
	width: {SHADE_W}px;
	height: 100%;
}}

.CsrShadeL
{{
	horizontal-align: left;
	background-color: {shade_l};
}}

.CsrShadeR
{{
	horizontal-align: right;
	background-color: {shade_r};
}}

/* The light sweep, one over the reel and one over the card: a travelling panel (parked off to the left
   until a win frame moves it) clipping a tilted soft band. */
.CsrShine
{{
	width: {SHINE_W}px;
	overflow: clip clip;
	transform: translatex( -{SHINE_W}px );
}}

.CsrShineReel
{{
	height: {REEL_HEIGHT}px;
}}

.CsrShineCard
{{
	height: {CARD_HEIGHT}px;
	margin-top: {CARD_GAP}px;
}}

.CsrShineBand
{{
	width: {SHINE_BAND}px;
	height: {2 * max(REEL_HEIGHT, CARD_HEIGHT)}px;
	horizontal-align: center;
	vertical-align: center;
	transform: rotatez( 20deg );
	background-color: {shine};
}}

/* Hairline rails, brightening toward the centre with a gold glint at the notch. */
.CsrRail
{{
	width: 100%;
	height: 1px;
	background-color: {rail};
}}

.CsrRailTop
{{
	vertical-align: top;
}}

.CsrRailBottom
{{
	vertical-align: bottom;
}}

/* The marker: a square rotated 45deg and centred on the plate's top / bottom edge. The reel's clip
   halves it into a gold triangle pointing at the slot, stopping short of the tile. */
.CsrNotch
{{
	width: {NOTCH}px;
	height: {NOTCH}px;
	horizontal-align: center;
	vertical-align: top;
	background-color: {GOLD};
	transform: rotatez( 45deg );
}}

.CsrNotchTop
{{
	margin-top: -{NOTCH // 2}px;
}}

.CsrNotchBottom
{{
	margin-top: {REEL_HEIGHT - NOTCH // 2}px;
}}

/* `landed` on csr_roll: the rest of the reel steps back. */
.CsrRoll.landed .CsrTile
{{
	opacity: 0.35;
	saturation: 0.3;
}}

/* Five classes, so it outranks the four-class dimming steps in the win frames too. */
.CsrRoll.landed .CsrStrip .CsrTile.won
{{
	opacity: 1;
	saturation: 1;
}}

.CsrRoll.landed .CsrSlot
{{
	opacity: 0;
}}

/* ---------- brand bar: a caption that slides up out of the reel's top edge on landing ---------- */
.CsrBrandClip
{{
	width: 100%;
	height: {BRAND_HEIGHT + BRAND_GAP}px;
	overflow: clip clip;
}}

/* Hidden until the roll lands, and only when the server has a caption for it (`brand`). */
.CsrBrand
{{
	flow-children: right;
	width: 100%;
	height: {BRAND_HEIGHT}px;
	padding: 0px 16px;
	border-top: 1px solid #ffffff14;
	background-color: {plate};
	opacity: 0;
}}

.CsrRoll.landed.brand .CsrBrand
{{
	opacity: 1;
}}

/* Gold hairlines either side of the text, fading outward - they share the space, centring it. */
.CsrBrandRule
{{
	width: fill-parent-flow( 1.0 );
	height: 1px;
	vertical-align: center;
}}

.CsrBrandRuleL
{{
	margin-right: 12px;
	background-color: {fade([(0, f"{GOLD}00"), (1, f"{GOLD}99")])};
}}

.CsrBrandRuleR
{{
	margin-left: 12px;
	background-color: {fade([(0, f"{GOLD}99"), (1, f"{GOLD}00")])};
}}

/* As written - no text-transform, so a case-sensitive invite code survives. */
.CsrBrandText
{{
	vertical-align: center;
	font-family: {FONT_BOLD};
	font-size: 14px;
	letter-spacing: 2px;
	color: #ffffffc2;
	white-space: nowrap;
	text-overflow: ellipsis;
}}

/* ---------- reveal card: compact centred caption under the winner ---------- */
/* Clips the card as it slides down out of the reel's bottom edge. Taller than the card so its shadow
   keeps most of its fall-off. */
.CsrCardClip
{{
	width: 100%;
	height: {CARD_GAP + CARD_HEIGHT + 24}px;
	overflow: clip clip;
}}

.CsrCard
{{
	flow-children: right;
	width: 100%;
	height: {CARD_HEIGHT}px;
	margin-top: {CARD_GAP}px;
	padding: 0px 20px;
	border-top: 2px solid {GOLD};
	background-color: {plate};
	box-shadow: #00000066 0px 6px 18px 0px;
	opacity: 0;
}}

.CsrCard.on
{{
	opacity: 1;
}}

/* The winner tile above already shows the icon. The panel stays so the server's ico-* write still
   lands somewhere; it just takes no space. */
.CsrCardIco
{{
	visibility: collapse;
}}

.CsrCardText
{{
	flow-children: down;
	width: fill-parent-flow( 1.0 );
	vertical-align: center;
}}

.CsrCardCat
{{
	width: 100%;
	text-align: center;
	font-family: {FONT_BOLD};
	font-size: 12px;
	line-height: 14px;
	letter-spacing: 3px;
	text-transform: uppercase;
	color: {GOLD};
	white-space: nowrap;
	text-overflow: ellipsis;
}}

.CsrCardName
{{
	width: 100%;
	margin-top: 2px;
	text-align: center;
	font-family: {FONT_BOLD};
	font-size: 28px;
	line-height: 30px;
	letter-spacing: 1px;
	text-transform: uppercase;
	color: #ffffff;
	white-space: nowrap;
	text-overflow: ellipsis;
}}

.CsrCardDesc
{{
	width: 100%;
	margin-top: 2px;
	text-align: center;
	font-family: {FONT_BODY};
	font-size: 16px;
	line-height: 19px;
	color: #ffffffc2;
	white-space: nowrap;
	text-overflow: ellipsis;
}}
""")
    for n in range(COARSE_STEPS + 1):
        w(f".CsrStrip.c{n} {{ transform: translate3d( -{n * STEP}px, 0px, 0px ); }}")
    w("")
    for n in range(FINE_STEPS):
        w(f".CsrTrack.h{n} {{ transform: translate3d( -{num(n / FINE_PER_PX)}px, 0px, 0px ); }}")
    w("")
    for cat, colour in CATEGORIES.items():
        wash = fade([(0, "#ffffff0d"), (0.5, "#ffffff08"), (1, f"{colour}38")], vertical=True)
        won = fade([(0, f"{colour}33"), (1, f"{colour}73")], vertical=True)
        card = fade([(0, f"{premix(colour)}d1"), (0.6, "#0d1015d5"), (1, f"{SHADE}d9")], vertical=True)
        w(f".CsrTile.cat-{cat} {{ border-bottom-color: {colour}; background-color: {wash}; }}")
        w(f".CsrTile.won.cat-{cat} {{ border-color: {colour}; background-color: {won}; box-shadow: {colour}80 0px 0px 12px 0px; }}")
        w(f".CsrCard.cat-{cat} {{ border-top-color: {colour}; background-color: {card}; }}")
        w(f".CsrCard.cat-{cat} .CsrCardCat {{ color: {colour}; }}")
    w("")
    # One ancestor class more than the category rules above, so a tier wins wherever both are set.
    for rar, (colour, glow) in RARITIES.items():
        wash = fade([(0, "#ffffff0d"), (0.5, "#ffffff08"), (1, f"{colour}38")], vertical=True)
        won = fade([(0, f"{colour}33"), (1, f"{colour}73")], vertical=True)
        card = fade([(0, f"{premix(colour)}d1"), (0.6, "#0d1015d5"), (1, f"{SHADE}d9")], vertical=True)
        w(f".CsrStrip .CsrTile.rar-{rar} {{ border-bottom-color: {colour}; background-color: {wash}; }}")
        w(f".CsrStrip .CsrTile.won.rar-{rar} {{ border-color: {colour}; background-color: {won}; box-shadow: {colour}80 0px 0px {glow}px 0px; }}")
        w(f".CsrCardClip .CsrCard.rar-{rar} {{ border-top-color: {colour}; background-color: {card}; }}")
        w(f".CsrCardClip .CsrCard.rar-{rar} .CsrCardCat {{ color: {colour}; }}")
    w("")
    out.extend(roll_frames())
    w("")

    # ---------- prompt ----------
    prompt_shade = fade([(0, f"{SHADE}00"), (0.2, f"{SHADE}c7"), (0.8, f"{SHADE}c7"), (1, f"{SHADE}00")])
    w(f""".CsrPrompt
{{
	flow-children: down;
	width: 620px;
	horizontal-align: center;
	vertical-align: top;
	margin-top: 92px;
	padding: 10px 40px;
	background-color: {prompt_shade};
	opacity: 0;
	transition-property: opacity;
	transition-duration: 0.3s;
	transition-timing-function: ease-out;
}}

.CsrPrompt.on
{{
	opacity: 1;
}}

.CsrPromptTitle
{{
	horizontal-align: center;
	font-family: {FONT_BOLD};
	font-size: 17px;
	letter-spacing: 3px;
	color: #f3c74f;
}}

.CsrPromptText
{{
	horizontal-align: center;
	font-family: {FONT_BODY};
	font-size: 17px;
	color: #ffffffd9;
}}
""")
    return "\n".join(out)


# ---------------------------------------------------------------------------------------------------
# C# contract
# ---------------------------------------------------------------------------------------------------
def contract(icon_names: list[str]) -> str:
    icon_list = ",\n        ".join(f'"{n}"' for n in icon_names)
    cats = ", ".join(f'"{c}"' for c in CATEGORIES)
    rars = ", ".join(f'"{r}"' for r in RARITIES)
    return f'''// <auto-generated>
// GENERATED by tools/generate_hud.py - edit the generator, not this file. It writes the Panorama
// layout and stylesheet from the same spec, so every id and class below exists in the layout.
// </auto-generated>

namespace CSRoll.Hud;

public static partial class HudLayout
{{
    public const string HudPanel = "csr_hud";
    public const string PromptPanel = "csr_prompt";
    public const string ListPanel = "csr_list";
    public const string ListTitle = "csr_list_title";
    public const string RollPanel = "csr_roll";
    public const string Track = "csr_track";
    public const string Strip = "csr_strip";
    public const string Card = "csr_card";
    public const string CardIcon = "csr_card_ico";
    public const string CardCategory = "csr_card_cat";
    public const string CardName = "csr_card_name";
    public const string CardDescription = "csr_card_desc";
    public const string BrandText = "csr_brand_text";

    public const int Rows = {ROWS};
    public const int Gauges = {GAUGES};
    public const int FillSteps = {FILL_STEPS};
    public const int ListOffsets = {len(LIST_OFFSETS)};

    public const int Tiles = {TILES};
    public const int StartTile = {START_TILE};
    public const int WinTile = {WIN_TILE};
    public const float SpinSeconds = {SPIN_SECONDS}f;
    public const float SpinPower = {SPIN_POWER}f;
    public const int Step = {STEP};
    public const int CoarseSteps = {COARSE_STEPS};
    public const int FinePerPixel = {FINE_PER_PX};
    public const int FineSteps = {FINE_STEPS};

    public const int Fps = {FPS};
    public const int InFrames = {IN_FRAMES};
    public const int WinFrames = {WIN_FRAMES};
    public const int OutFrames = {OUT_FRAMES};

    public const string FallbackIcon = "{FALLBACK_ICON}";

    public static readonly string[] Icons =
    [
        {icon_list},
    ];

    public static readonly string[] Categories = [{cats}];

    /// <summary>Rarity keys with stylesheet rules; RarityClass("off") is deliberately unstyled.</summary>
    public static readonly string[] Rarities = [{rars}];

    public static string Row(int i) => $"csr_row{{i}}";
    public static string RowIcon(int i) => $"csr_row{{i}}_ico";
    public static string RowName(int i) => $"csr_row{{i}}_name";
    public static string RowShort(int i) => $"csr_row{{i}}_short";
    public static string Gauge(int i) => $"csr_g{{i}}";
    public static string GaugeIcon(int i) => $"csr_g{{i}}_ico";
    public static string GaugeLabel(int i) => $"csr_g{{i}}_label";
    public static string GaugeValue(int i) => $"csr_g{{i}}_val";
    public static string Tile(int i) => $"csr_t{{i}}";
    public static string TileIcon(int i) => $"csr_t{{i}}_ico";

    public static string IconClass(string icon) => $"ico-{{icon}}";
    public static string CategoryClass(string category) => $"cat-{{category}}";
    public static string RarityClass(string rarity) => $"rar-{{rarity}}";
    public static string FillClass(int step) => $"f{{step}}";
    public static string CoarseClass(int step) => $"c{{step}}";
    public static string FineClass(int step) => $"h{{step}}";
    public static string InClass(int frame) => $"in{{frame}}";
    public static string WinClass(int frame) => $"win{{frame}}";
    public static string OutClass(int frame) => $"out{{frame}}";
    public static string ListOffsetClass(int offset) => $"y{{offset}}";

    /// <summary>Text variable names, as written in the layout's {{s:...}} slots.</summary>
    public const string VarTitle = "title";
    public const string VarName = "name";
    public const string VarShort = "short";
    public const string VarLabel = "label";
    public const string VarValue = "val";
    public const string VarCategory = "cat";
    public const string VarDescription = "desc";
    public const string VarBrand = "brand";

    public const string On = "on";
    public const string Ready = "ready";
    public const string Won = "won";
    public const string Landed = "landed";
    public const string Brand = "brand";
}}
'''


def main() -> None:
    names = icons()
    LAYOUT.write_text(layout(), encoding="utf-8")
    STYLE.write_text(stylesheet(names), encoding="utf-8")
    CONTRACT.write_text(contract(names), encoding="utf-8")
    print(f"wrote {LAYOUT.relative_to(ROOT)}, {STYLE.relative_to(ROOT)}, {CONTRACT.relative_to(ROOT)} ({len(names)} icons)")


if __name__ == "__main__":
    main()
