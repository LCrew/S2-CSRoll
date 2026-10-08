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
from xml.sax.saxutils import escape

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
{admin_layout(2, "  ")}
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
    w(admin_stylesheet())
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

{admin_contract()}
}}
'''


# ---------------------------------------------------------------------------------------------------
# Admin panel (!rolladmin)
#
# A clickable window the plugin shows to one admin at a time: tabs down the left, settings as segmented
# Buttons and steppers, the modifier list as 14 re-filled slot rows. It reuses what the HUD already
# interns - `on`, cat-*, rar-* / rar-off, ico-*, f0..f100, and the val / name / short variables.
#
#     admin_layout()      -> XML, the LAST child of CsrScreen (after csr_prompt), so it paints on top
#     admin_stylesheet()  -> CSS, appended after everything the HUD emits
#     admin_contract()    -> C# lines inside HudLayout
# ---------------------------------------------------------------------------------------------------
ADM_W = 1120            # px @1080p - centred, so x 400-1520
ADM_H = 704             # px - y 188-892
ADM_NAV_W = 224         # left tab column, border included
ADM_HEAD_H = 56
ADM_FOOT_H = 56
ADM_PAD_X = 28          # main area side padding
ADM_CONTENT_W = ADM_W - ADM_NAV_W - 2 * ADM_PAD_X          # 840
ADM_COLS = 2            # modifier rows: two columns...
ADM_ROWS = 7            # ...of seven - every category fits one page, "All" is 4 pages
ADM_SLOTS = ADM_COLS * ADM_ROWS
ADM_COL_GAP = 16
ADM_COL_W = (ADM_CONTENT_W - ADM_COL_GAP) // ADM_COLS       # 412
ADM_ROW_H = 54
ADM_ROW_GAP = 6
ADM_SET_H = 48          # a setting row
ADM_TIER_H = 56         # a rarity tier row
ADM_CFG_LINES = 8       # unsaved-change lines on the Config page
ERR = "#eb4b4b"         # covert red, for destructive and error states
OK = "#9ccf4f"
WARN = "#f6ad55"

ADM_PAGES = [  # (key, tab label, static sub-label - None means the server writes it)
    ("gen", "General", "Rolls & rounds"),
    ("mod", "Modifiers", None),
    ("rar", "Rarity", "Case odds"),
    ("hud", "Display", "Reveal & HUD"),
    ("cfg", "Config", None),
]
ADM_FILTERS = [("all", "All"), ("move", "Movement"), ("weap", "Weapons"), ("util", "Grenades"),
               ("surv", "Survival"), ("stealth", "Stealth"), ("chaos", "Chaos")]
ADM_TIERS = [("milspec", "Mil-Spec"), ("restricted", "Restricted"), ("classified", "Classified"),
             ("covert", "Covert"), ("gold", "★ Gold")]

BOOL = [("on", "Enabled", None), ("off", "Disabled", None)]   # v0 = enabled, v1 = disabled

# A page's rows, top to bottom. A bare string is a section heading. A row is
# (key, label, hint, control): control "step", "text" (read-only), or a list of segment options
# (id suffix, label, hint shown while that option is the selected one, or None).
ADM_SETTINGS: dict[str, list] = {
    "gen": [
        ("rr", "Random rounds", "Roll modifiers as each round starts", BOOL),
        ("mode", "Roll mode", None, [("player", "Player", "Every player rolls their own"),
                                     ("team", "Team", "Each team shares one roll"),
                                     ("game", "Game", "One roll for the whole server")]),
        ("min", "Fewest per roll", "Modifiers a roll hands out, at least", "step"),
        ("max", "Most per roll", "Modifiers a roll hands out, at most", "step"),
        ("cd", "Repeat cooldown", "Rounds before a player can roll the same modifier again", "step"),
        ("rep", "Repeat last set", "Game mode: the same set may come up two rounds running", BOOL),
        ("warm", "Roll in warmup", "Random rounds while warmup is running", BOOL),
    ],
    "hud": [
        "Reveal",
        ("reveal", "Roll reveal", "Show each roll on screen - the reel, or centre text", BOOL),
        ("spin", "Centre-text spin", "Spin through names before the result. The HUD reel always spins", BOOL),
        ("spec", "Spectator panel", "Centre-text list of the watched player's modifiers", BOOL),
        "Workshop HUD",
        ("hud", "Workshop HUD", "Reel, list and gauges for players with the addon", BOOL),
        ("hudmode", "HUD mode", None, [("optin", "Opt-in", "Players switch it on with !hud"),
                                       ("everyone", "Everyone", "On for everyone - !hud switches back")]),
        ("hudspec", "Spectator team", "Players on the spectator team get the HUD too", BOOL),
        ("listy", "List position", "0 sits highest - lower it if the radar covers the list", "step"),
        ("brand", "Brand caption", "Read-only here - edit CustomHud.BrandText in config.jsonc", "text"),
    ],
    "rar": [
        ("rar", "Rarity tiers", "Pick a tier by weight, then a modifier inside it", BOOL),
    ],
}


def attr(text: str) -> str:
    return escape(text, {'"': "&quot;"})


# ---------------------------------------------------------------------------------------------------
# Layout
# ---------------------------------------------------------------------------------------------------
class E:
    """A layout element. Children-only-leaves elements print on one line, like the generator's tiles."""

    def __init__(self, tag: str, cls: str | None = None, id: str | None = None, text: str | None = None,
                 hittest: bool | None = None, kids: list["E"] | None = None):
        self.tag, self.cls, self.id, self.text, self.hittest, self.kids = tag, cls, id, text, hittest, kids or []

    def open(self) -> str:
        a = (f' id="{self.id}"' if self.id else "") + (f' class="{self.cls}"' if self.cls else "")
        a += "" if self.hittest is None else f' hittest="{str(self.hittest).lower()}"'
        a += f' text="{attr(self.text)}"' if self.text is not None else ""
        return f"<{self.tag}{a}"

    def inline(self) -> str:
        if not self.kids:
            return self.open() + " />"
        return self.open() + ">" + "".join(k.inline() for k in self.kids) + f"</{self.tag}>"

    def lines(self, depth: int, ind: str) -> list[str]:
        flat = self.inline()
        if not self.kids or (all(not k.kids or k.tag == "Button" for k in self.kids) and len(flat) <= 210) \
                or (self.tag == "Button" and len(flat) <= 240):
            return [ind * depth + flat]
        out = [ind * depth + self.open() + ">"]
        for k in self.kids:
            out += k.lines(depth + 1, ind)
        return out + [ind * depth + f"</{self.tag}>"]


def P(cls=None, id=None, kids=None, hittest=None):
    return E("Panel", cls, id, None, hittest, kids)


def L(cls, text, id=None, btn=False):
    """A Label; inside a Button it is hittest="false", so the Button is always the hit target."""
    return E("Label", cls, id, text, False if btn else None)


def B(id, cls, kids):
    return E("Button", cls, id, None, None, kids)


def deco(cls):
    """A decorative panel inside a button."""
    return E("Panel", cls, None, None, False)


def _setting(key: str, label: str, hint: str | None, control) -> E:
    rid = f"csr_adm_s_{key}"
    text = [L("AdmSetName", label)] + ([L("AdmSetHint", hint)] if hint else [])
    if isinstance(control, list):
        text += [L(f"AdmSetHint AdmHintV AdmHintV{n}", h) for n, (_, _, h) in enumerate(control) if h]
    kids = [P("AdmSetText", kids=text)]
    if control == "step":
        kids.append(P("AdmStep", kids=[
            B(f"{rid}_dec", "AdmStepBtn AdmStepDec", [L("AdmStepGlyph", "−", btn=True)]),
            P("AdmStepVal", kids=[L("AdmStepNum", "{s:val}", f"{rid}_val")]),
            B(f"{rid}_inc", "AdmStepBtn AdmStepInc", [L("AdmStepGlyph", "+", btn=True)])]))
    elif control == "text":
        kids.append(P("AdmReadonly", kids=[L("AdmReadonlyTxt", "{s:val}", f"{rid}_val")]))
    else:
        kids.append(P("AdmSeg", kids=[B(f"{rid}_{oid}", f"AdmOpt AdmOpt{n}", [L("AdmOptTxt", t, btn=True)])
                                      for n, (oid, t, _) in enumerate(control)]))
    return P("AdmSet", None if control == "text" else rid, kids)


def _section(title: str, meta: E | None = None) -> E:
    return P("AdmSection", kids=[L("AdmKicker", title), P("AdmSectionRule")] + ([meta] if meta else []))


def _page_head(title: str, note: str | None = None, extra: list[E] | None = None) -> list[E]:
    tail = extra if extra is not None else [L("AdmPageNote", note)]
    return [P("AdmPageHead", kids=[L("AdmPageTitle", title)] + tail), P("AdmPageRule")]


def _settings(page: str) -> list[E]:
    return [_section(r) if isinstance(r, str) else _setting(*r) for r in ADM_SETTINGS[page]]


def _button(pid: str, cls: str, text: str, arm_text: str | None = None) -> E:
    kids = [L("AdmBtnTxt", text, btn=True)] + ([L("AdmBtnArm", arm_text, btn=True)] if arm_text else [])
    return B(pid, cls, kids)


def _mod_slot(i: int) -> E:
    m = f"csr_adm_m{i}"
    return P("AdmMod", m, [
        P("AdmModRail"),
        P("AdmModBody", kids=[
            P("AdmTile", kids=[P("CsrIco CsrRowIco", f"{m}_ico")]),
            P("AdmModText", kids=[
                L("AdmModName", "{s:name}", f"{m}_name"),
                P("AdmModLine", kids=[
                    B(f"{m}_tier", "AdmRar", [L("AdmRarTxt", "{s:rar}", f"{m}_rar", btn=True), deco("AdmRarOvr")]),
                    L("AdmModShort", "{s:short}", f"{m}_short")])]),
            P("AdmDirty"),
            B(f"{m}_tog", "AdmTog", [deco("AdmTogKnob")])])])


def _tier_row(t: str, label: str) -> E:
    w = f"csr_adm_w_{t}"
    return P(f"AdmTier rar-{t}", w, [
        P("AdmTierText", kids=[L("AdmTierName", label), L("AdmTierInfo", "{s:val}", f"{w}_info")]),
        P("AdmDirty"),
        P("AdmStep", kids=[
            B(f"{w}_dec5", "AdmStepBtn AdmStepDec AdmStepBig", [L("AdmStepGlyph", "−5", btn=True)]),
            B(f"{w}_dec", "AdmStepBtn AdmStepDec", [L("AdmStepGlyph", "−", btn=True)]),
            P("AdmStepVal", kids=[L("AdmStepNum", "{s:val}", f"{w}_val")]),
            B(f"{w}_inc", "AdmStepBtn AdmStepInc", [L("AdmStepGlyph", "+", btn=True)]),
            B(f"{w}_inc5", "AdmStepBtn AdmStepInc AdmStepBig", [L("AdmStepGlyph", "+5", btn=True)])]),
        P("AdmShare", kids=[P("AdmShareBar", kids=[P("AdmShareFill")]), L("AdmSharePct", "{s:val}", f"{w}_pct")])])


def admin_tree() -> E:
    tabs = []
    for key, name, sub in ADM_PAGES:
        sub_label = L("AdmTabSub", sub, btn=True) if sub else L("AdmTabSub", "{s:val}", f"csr_adm_tab_{key}_sub", btn=True)
        tabs.append(B(f"csr_adm_tab_{key}", f"AdmTab AdmTab-{key}", [
            deco("AdmTabBar"), E("Panel", "AdmTabText", None, None, False, [L("AdmTabName", name, btn=True), sub_label]),
            deco("AdmDirty AdmTabDot"), deco("AdmTabNotch")]))

    gen = _page_head("General", "How random rounds hand out modifiers") + _settings("gen")
    hud = _page_head("Display", "What players see when a roll lands") + _settings("hud")

    chips = [B(f"csr_adm_flt_{k}", f"AdmChip AdmChip-{k}", ([] if k == "all" else [deco("AdmChipDot")]) + [L("AdmChipTxt", t, btn=True)])
             for k, t in ADM_FILTERS]
    # Column-major: an alphabetical list reads down the left column, then the right.
    cols = [P(f"AdmCol AdmCol{c}" if c else "AdmCol", kids=[_mod_slot(c * ADM_ROWS + r) for r in range(ADM_ROWS)]) for c in range(ADM_COLS)]
    mod = _page_head("Modifiers", extra=[
        L("AdmPageMeta", "{s:val}", "csr_adm_mod_count"), P("AdmHeadFill"),
        _button("csr_adm_mod_allon", "AdmBtn AdmBtnSm", "All on"),
        _button("csr_adm_mod_alloff", "AdmBtn AdmBtnSm", "All off", "Again to confirm")]) + [
        P("AdmFilter", "csr_adm_flt", chips),
        P("AdmGrid", kids=cols),
        P("AdmFootRow", kids=[
            L("AdmLegend", "Lit tile = active this round  ·  Click a tier to move the modifier up a tier"),
            P("AdmPager", "csr_adm_pager", [
                B("csr_adm_prev", "AdmPageBtn AdmPrev", [L("AdmPageBtnTxt", "‹  Prev", btn=True)]),
                L("AdmPageInfo", "{s:val}", "csr_adm_page"),
                B("csr_adm_next", "AdmPageBtn AdmNext", [L("AdmPageBtnTxt", "Next  ›", btn=True)])])])]

    rar = _page_head("Rarity", "Each tier's share of every pick, like a case's odds") + _settings("rar") + [
        _section("Case odds"),
        P("AdmDist", kids=[P(f"AdmDistSeg rar-{t}", f"csr_adm_dist_{t}") for t, _ in ADM_TIERS]),
        L("AdmRarOffNote", "Rarity is off - every enabled modifier is equally likely"),
        P("AdmTierList", kids=[_tier_row(t, label) for t, label in ADM_TIERS]),
        P("AdmOvrRow", kids=[L("AdmOvrInfo", "{s:val}", "csr_adm_ovr_info"),
                             _button("csr_adm_ovr_reset", "AdmBtn AdmBtnSm", "Reset tiers", "Again to confirm")]),
        L("AdmFootnote", "Weights needn't add up to 100: a tier's share is its weight over the total of the tiers that have an enabled modifier.")]

    def action(pid, cls, title, armed, sub):
        return B(pid, cls, [L("AdmActionTitle", title, btn=True), L("AdmActionArm", armed, btn=True), L("AdmActionSub", sub, btn=True)])

    cfg = _page_head("Config", "Changes apply live - Save keeps them") + [
        P("AdmCols", kids=[
            P("AdmColBox", kids=[
                _section("Unsaved changes", L("AdmSectionMeta", "{s:val}", "csr_adm_cfg_count")),
                P("AdmList", kids=[L("AdmLine", "{s:val}", f"csr_adm_cfg_l{n}") for n in range(ADM_CFG_LINES)]),
                L("AdmFootnote", "Save or discard them with the buttons below.")]),
            P("AdmColBox AdmColBoxR", kids=[
                _section("Round actions"),
                action("csr_adm_reroll", "AdmAction", "Re-roll now", "Click again to re-roll",
                       "Strip every active modifier and roll again, with the reel"),
                action("csr_adm_clear", "AdmAction AdmActionDanger", "Remove all active", "Click again to remove",
                       "Strip every active modifier until the next roll"),
                _section("Server"),
                L("AdmInfo", "{s:val}", "csr_adm_cfg_live"),
                L("AdmInfo AdmInfoPath", "{s:val}", "csr_adm_cfg_path"),
                L("AdmInfo AdmInfoWarn", "{s:val}", "csr_adm_cfg_cvar")])])]

    pages = [P(f"AdmPage AdmPage-{k}", kids=body) for k, body in
             (("gen", gen), ("mod", mod), ("rar", rar), ("hud", hud), ("cfg", cfg))]

    return P("AdmRoot", "csr_adm", hittest=False, kids=[
        P("AdmDim"),
        P("AdmWin", kids=[
            P("AdmHead", kids=[
                P("AdmMark"), L("AdmTitle", "CSRoll"), L("AdmTitle AdmTitleGold", "Admin"),
                L("AdmVer", "{s:val}", "csr_adm_ver"), P("AdmHeadFill"),
                P("AdmUnsaved", kids=[P("AdmDirty"), L("AdmUnsavedTxt", "{s:val}", "csr_adm_unsaved")]),
                B("csr_adm_close", "AdmClose", [L("AdmCloseX", "✕", btn=True)])]),
            P("AdmRule"),
            P("AdmBody", kids=[
                P("AdmNav", kids=tabs + [P("AdmNavFill"), L("AdmNavHint", "Changes apply live."),
                                         L("AdmNavHint", "!rolladmin or F6 closes this.")]),
                P("AdmMain", kids=pages)]),
            P("AdmFoot", kids=[
                L("AdmStatus", "{s:val}", "csr_adm_status"),
                _button("csr_adm_reload", "AdmBtn", "Reload from disk", "Again to discard changes"),
                _button("csr_adm_save", "AdmBtn AdmBtnSave", "Save to config")])])])


def admin_layout(depth: int = 2, ind: str = "  ") -> str:
    """The admin panel's XML at the given depth - 2 matches csr_hud / csr_prompt inside CsrScreen."""
    return "\n".join(admin_tree().lines(depth, ind))


# ---------------------------------------------------------------------------------------------------
# Stylesheet
# ---------------------------------------------------------------------------------------------------
def _light(colour: str, amount: float = 0.3) -> str:
    """A tier/category colour lifted toward white, for small text on the dark plate (AA at 10-16px)."""
    return mix(colour, "#ffffff", amount)


def admin_stylesheet() -> str:
    out: list[str] = []
    w = out.append

    plate = fade([(0, "#11151af2"), (1, f"{SHADE}f7")], vertical=True)
    dim = fade([(0, "#05070ab3"), (1, "#05070ad9")], vertical=True)
    # The reel's rail, laid along the window: brightening to a gold glint where the nav meets the page.
    head_rail = fade([(0, "#ffffff0f"), (0.17, "#ffffff26"), (0.195, "#ffffff40"), (0.20, GOLD),
                      (0.205, "#ffffff40"), (0.23, "#ffffff26"), (1, "#ffffff0a")])
    page_rule = fade([(0, GOLD), (0.12, f"{GOLD}66"), (0.35, "#ffffff1f"), (1, "#ffffff00")])   # = CsrListRule
    section_rule = fade([(0, "#ffffff1f"), (1, "#ffffff00")])
    row_rail = fade([(0, "#ffffff1f"), (0.6, "#ffffff0a"), (1, "#ffffff00")])                   # = CsrRowRail
    row_plate = lambda base: fade([(0, f"{base}b3"), (0.55, f"{SHADE}66"), (1, f"{SHADE}00")])  # = CsrRow plate
    tile = fade([(0, "#ffffff0f"), (1, "#ffffff05")], vertical=True)
    set_plate = fade([(0, "#ffffff0d"), (0.6, "#ffffff05"), (1, "#ffffff02")])
    set_dirty = fade([(0, f"{GOLD}1f"), (0.6, "#ffffff05"), (1, "#ffffff02")])
    selected = fade([(0, f"{GOLD}14"), (1, f"{GOLD}3d")], vertical=True)
    gold_fill = fade([(0, mix(GOLD, "#ffffff", 0.22)), (1, GOLD)], vertical=True)
    track = fade([(0, "#ffffff38"), (1, "#ffffff17")], vertical=True)                              # = CsrBar
    tab_active = fade([(0, f"{GOLD}29"), (1, f"{GOLD}00")])
    tog_on = fade([(0, "#ffffff4d"), (1, "#ffffff2e")], vertical=True)

    w(f"""
/* ================================================================================================
   Admin panel (!rolladmin). Shown per player with `on` on csr_adm; one page at a time by a pg-* class
   on the same panel. Nothing here transitions or animates: server class writes don't start them, so
   every state is drawn to look right the moment it switches. :hover / :active are client-side and do
   work, so they carry the instant feedback.
   ================================================================================================ */
.AdmRoot
{{
	width: 100%;
	height: 100%;
	visibility: collapse;
}}

.AdmRoot.on
{{
	visibility: visible;
}}

/* The game behind, pushed back. Hit-testable (the default), so a stray click outside does nothing. */
.AdmDim
{{
	width: 100%;
	height: 100%;
	background-color: {dim};
}}

/* The window: the reel's plate made opaque enough to read on, with the reveal card's gold top edge. */
.AdmWin
{{
	flow-children: down;
	width: {ADM_W}px;
	height: {ADM_H}px;
	horizontal-align: center;
	vertical-align: center;
	border-top: 2px solid {GOLD};
	border-radius: 2px;
	background-color: {plate};
	box-shadow: #000000b3 0px 24px 64px 0px;
}}

/* ---------- header ---------- */
.AdmHead
{{
	flow-children: right;
	width: 100%;
	height: {ADM_HEAD_H}px;
	padding: 0px 10px 0px 22px;
}}

/* The reel's marker, standing on its point. */
.AdmMark
{{
	width: 8px;
	height: 8px;
	margin-right: 14px;
	vertical-align: center;
	background-color: {GOLD};
	transform: rotatez( 45deg );
}}

.AdmTitle
{{
	vertical-align: center;
	font-family: {FONT_BOLD};
	font-size: 18px;
	letter-spacing: 4px;
	text-transform: uppercase;
	color: #ffffff;
}}

.AdmTitleGold
{{
	margin-left: 8px;
	color: {GOLD};
}}

.AdmVer
{{
	vertical-align: center;
	margin-left: 14px;
	padding: 2px 6px;
	border: 1px solid #ffffff1f;
	border-radius: 2px;
	font-family: {FONT_BODY};
	font-size: 12px;
	letter-spacing: 1px;
	color: #ffffff80;
}}

.AdmHeadFill
{{
	width: fill-parent-flow( 1.0 );
	height: 1px;
}}

/* "3 UNSAVED" - only while the live settings differ from config.jsonc. */
.AdmUnsaved
{{
	flow-children: right;
	height: 26px;
	vertical-align: center;
	margin-right: 10px;
	padding: 0px 10px 0px 10px;
	border: 1px solid {GOLD}80;
	border-radius: 2px;
	background-color: {GOLD}1a;
	visibility: collapse;
}}

.AdmRoot.dirty .AdmUnsaved
{{
	visibility: visible;
}}

.AdmUnsavedTxt
{{
	vertical-align: center;
	margin-left: 8px;
	font-family: {FONT_BOLD};
	font-size: 12px;
	letter-spacing: 2px;
	text-transform: uppercase;
	color: {GOLD};
}}

.AdmClose
{{
	width: 36px;
	height: 36px;
	vertical-align: center;
	border-radius: 2px;
}}

.AdmClose:hover
{{
	background-color: {ERR}38;
}}

.AdmCloseX
{{
	horizontal-align: center;
	vertical-align: center;
	font-family: {FONT_BOLD};
	font-size: 18px;
	color: #ffffff8c;
}}

.AdmClose:hover .AdmCloseX
{{
	color: #ffffff;
}}

.AdmRule
{{
	width: 100%;
	height: 1px;
	background-color: {head_rail};
}}

/* The gold diamond that marks anything unsaved: a tab, a modifier row, a tier row, the header pill.
   Kept in the flow at opacity 0, so nothing shifts when it appears. */
.AdmDirty
{{
	width: 6px;
	height: 6px;
	vertical-align: center;
	background-color: {GOLD};
	transform: rotatez( 45deg );
}}

/* ---------- body: tab column + page ---------- */
.AdmBody
{{
	flow-children: right;
	width: 100%;
	height: fill-parent-flow( 1.0 );
}}

.AdmNav
{{
	flow-children: down;
	width: {ADM_NAV_W}px;
	height: 100%;
	padding: 10px 0px 14px 0px;
	border-right: 1px solid #ffffff12;
	background-color: #0000002e;
}}

/* A tab overlays its parts (no flow): a 3px bar at the left, the text, the dirty diamond, the notch. */
.AdmTab
{{
	width: 100%;
	height: 56px;
	overflow: clip clip;
}}

.AdmTab:hover
{{
	background-color: #ffffff0a;
}}

.AdmTabBar
{{
	width: 3px;
	height: 100%;
}}

.AdmTabText
{{
	flow-children: down;
	vertical-align: center;
	margin-left: 22px;
}}

.AdmTabName
{{
	font-family: {FONT_BOLD};
	font-size: 14px;
	letter-spacing: 2.5px;
	text-transform: uppercase;
	color: #ffffffa6;
}}

.AdmTab:hover .AdmTabName
{{
	color: #ffffffd9;
}}

.AdmTabSub
{{
	margin-top: 1px;
	font-family: {FONT_BODY};
	font-size: 12px;
	color: #ffffff80;
}}

.AdmTabDot
{{
	horizontal-align: right;
	margin-right: 22px;
	opacity: 0;
}}

.AdmTab.dirty .AdmTabDot
{{
	opacity: 1;
}}

/* The reel's notch on the column's edge, half-clipped by the tab into a gold triangle that points at
   the open tab. */
.AdmTabNotch
{{
	width: 10px;
	height: 10px;
	horizontal-align: right;
	vertical-align: center;
	margin-right: -5px;
	background-color: {GOLD};
	transform: rotatez( 45deg );
	opacity: 0;
}}

.AdmNavFill
{{
	width: 100%;
	height: fill-parent-flow( 1.0 );
}}

.AdmNavHint
{{
	margin: 4px 16px 0px 22px;
	font-family: {FONT_BODY};
	font-size: 12px;
	color: #ffffff80;
}}

/* Pages overlay each other (no flow); the pg-* class on csr_adm shows one and lights its tab. */
.AdmMain
{{
	width: fill-parent-flow( 1.0 );
	height: 100%;
	padding: 18px {ADM_PAD_X}px 16px {ADM_PAD_X}px;
}}

.AdmPage
{{
	flow-children: down;
	width: 100%;
	height: 100%;
	visibility: collapse;
}}
""")
    for key, _, _ in ADM_PAGES:
        w(f".AdmRoot.pg-{key} .AdmPage-{key} {{ visibility: visible; }}")
        w(f".AdmRoot.pg-{key} .AdmTab-{key} {{ background-color: {tab_active}; }}")
        w(f".AdmRoot.pg-{key} .AdmTab-{key} .AdmTabBar {{ background-color: {GOLD}; }}")
        w(f".AdmRoot.pg-{key} .AdmTab-{key} .AdmTabName {{ color: #ffffff; }}")
        w(f".AdmRoot.pg-{key} .AdmTab-{key} .AdmTabSub {{ color: #ffffffb3; }}")
        w(f".AdmRoot.pg-{key} .AdmTab-{key} .AdmTabNotch {{ opacity: 1; }}")
    w(f"""
/* ---------- page furniture ---------- */
.AdmPageHead
{{
	flow-children: right;
	width: 100%;
	height: 30px;
}}

.AdmPageTitle
{{
	vertical-align: bottom;
	font-family: {FONT_BOLD};
	font-size: 22px;
	letter-spacing: 2px;
	text-transform: uppercase;
	color: #ffffff;
}}

.AdmPageMeta
{{
	vertical-align: bottom;
	margin-left: 14px;
	margin-bottom: 3px;
	font-family: {FONT_BOLD};
	font-size: 12px;
	letter-spacing: 2px;
	text-transform: uppercase;
	color: {GOLD};
}}

.AdmPageNote
{{
	width: fill-parent-flow( 1.0 );
	vertical-align: bottom;
	margin-bottom: 3px;
	text-align: right;
	font-family: {FONT_BODY};
	font-size: 13px;
	color: #ffffff80;
}}

/* The modifier list's title rule: a gold glint at the left fading out. */
.AdmPageRule
{{
	width: 100%;
	height: 1px;
	margin-top: 8px;
	margin-bottom: 12px;
	background-color: {page_rule};
}}

.AdmSection
{{
	flow-children: right;
	width: 100%;
	margin-top: 6px;
	margin-bottom: 6px;
}}

.AdmKicker
{{
	vertical-align: center;
	font-family: {FONT_BOLD};
	font-size: 12px;
	letter-spacing: 3px;
	text-transform: uppercase;
	color: #ffffffb3;
}}

.AdmSectionRule
{{
	width: fill-parent-flow( 1.0 );
	height: 1px;
	vertical-align: center;
	margin-left: 12px;
	background-color: {section_rule};
}}

.AdmSectionMeta
{{
	vertical-align: center;
	margin-left: 12px;
	font-family: {FONT_BOLD};
	font-size: 12px;
	letter-spacing: 2px;
	text-transform: uppercase;
	color: {GOLD};
}}

.AdmFootnote
{{
	width: 100%;
	margin-top: 8px;
	font-family: {FONT_BODY};
	font-size: 13px;
	color: #ffffff80;
}}

/* ---------- setting row: the list row's language - 2px edge, plate fading right ---------- */
.AdmSet
{{
	flow-children: right;
	width: 100%;
	height: {ADM_SET_H}px;
	margin-bottom: 4px;
	padding: 0px 10px 0px 14px;
	border-left: 2px solid #ffffff1f;
	background-color: {set_plate};
}}

/* Differs from config.jsonc: the edge turns gold and the plate warms. */
.AdmSet.dirty
{{
	border-left-color: {GOLD};
	background-color: {set_dirty};
}}

/* Doesn't apply right now (e.g. the repeat cooldown in Game mode). Still clickable. */
.AdmSet.na
{{
	opacity: 0.45;
}}

.AdmSetText
{{
	flow-children: down;
	width: fill-parent-flow( 1.0 );
	vertical-align: center;
}}

.AdmSetName
{{
	font-family: {FONT_BOLD};
	font-size: 15px;
	letter-spacing: 1.5px;
	text-transform: uppercase;
	color: #ffffff;
}}

.AdmSetHint
{{
	width: 100%;
	margin-top: 1px;
	font-family: {FONT_BODY};
	font-size: 13px;
	color: #ffffff80;
	white-space: nowrap;
	text-overflow: ellipsis;
}}

/* A choice's hint follows the selected option: one static label per option, shown by v0/v1/v2. */
.AdmHintV
{{
	visibility: collapse;
}}

.AdmSet.v0 .AdmHintV0, .AdmSet.v1 .AdmHintV1, .AdmSet.v2 .AdmHintV2
{{
	visibility: visible;
}}

/* ---------- segmented choice: every option is its own button, so a click sets, never flips ---------- */
.AdmSeg
{{
	flow-children: right;
	height: 32px;
	vertical-align: center;
	border: 1px solid #ffffff1f;
	border-radius: 2px;
	background-color: #00000040;
}}

.AdmOpt
{{
	width: 100px;
	height: 100%;
	border-left: 1px solid #ffffff14;
}}

.AdmOpt0
{{
	border-left-width: 0px;
}}

.AdmOpt:hover
{{
	background-color: #ffffff0f;
}}

.AdmOpt:active
{{
	background-color: {GOLD}1f;
}}

.AdmOptTxt
{{
	horizontal-align: center;
	vertical-align: center;
	font-family: {FONT_BOLD};
	font-size: 12px;
	letter-spacing: 2px;
	text-transform: uppercase;
	color: #ffffff8c;
}}

.AdmOpt:hover .AdmOptTxt
{{
	color: #ffffffd9;
}}

/* Selected: the item card's 2px bar, in gold, over a gold wash. */
.AdmSet.v0 .AdmOpt0, .AdmSet.v1 .AdmOpt1, .AdmSet.v2 .AdmOpt2
{{
	border-bottom: 2px solid {GOLD};
	background-color: {selected};
}}

.AdmSet.v0 .AdmOpt0 .AdmOptTxt, .AdmSet.v1 .AdmOpt1 .AdmOptTxt, .AdmSet.v2 .AdmOpt2 .AdmOptTxt
{{
	color: #ffffff;
}}

/* ---------- stepper ---------- */
.AdmStep
{{
	flow-children: right;
	height: 32px;
	vertical-align: center;
}}

.AdmStepBtn
{{
	width: 32px;
	height: 32px;
	margin-left: 4px;
	border: 1px solid #ffffff26;
	border-radius: 2px;
	background-color: #ffffff0a;
}}

.AdmStepBtn:hover
{{
	border: 1px solid #ffffff59;
	background-color: #ffffff1f;
}}

.AdmStepBtn:active
{{
	background-color: {GOLD}33;
}}

.AdmStepBig
{{
	width: 40px;
}}

.AdmStepGlyph
{{
	horizontal-align: center;
	vertical-align: center;
	font-family: {FONT_BOLD};
	font-size: 18px;
	color: #ffffffb3;
}}

.AdmStepBig .AdmStepGlyph
{{
	font-size: 13px;
	letter-spacing: 1px;
}}

.AdmStepBtn:hover .AdmStepGlyph
{{
	color: #ffffff;
}}

/* The value sits on an item-card tile with a gold bar. */
.AdmStepVal
{{
	width: 64px;
	height: 32px;
	margin-left: 4px;
	border-radius: 2px;
	border-top: 1px solid #ffffff1a;
	border-bottom: 2px solid {GOLD}99;
	background-color: {tile};
}}

.AdmStepNum
{{
	horizontal-align: center;
	vertical-align: center;
	font-family: {FONT_BOLD};
	font-size: 17px;
	letter-spacing: 1px;
	color: #ffffff;
}}

/* At a bound the button stays clickable (the server ignores it) but reads as spent. */
.AdmSet.min .AdmStepDec, .AdmSet.max .AdmStepInc, .AdmTier.min .AdmStepDec, .AdmTier.max .AdmStepInc
{{
	opacity: 0.3;
}}

/* ---------- read-only value ---------- */
.AdmReadonly
{{
	width: 360px;
	height: 32px;
	vertical-align: center;
	padding: 0px 12px;
	border: 1px solid #ffffff1a;
	border-radius: 2px;
	background-color: #00000040;
}}

/* As written - no text-transform, the same as the brand bar over the reel. */
.AdmReadonlyTxt
{{
	width: 100%;
	vertical-align: center;
	font-family: {FONT_BOLD};
	font-size: 14px;
	letter-spacing: 1px;
	color: #ffffffc2;
	white-space: nowrap;
	text-overflow: ellipsis;
}}

/* ---------- buttons ---------- */
.AdmBtn
{{
	height: 36px;
	padding: 0px 18px;
	margin-left: 8px;
	vertical-align: center;
	border: 1px solid #ffffff2e;
	border-radius: 2px;
	background-color: #ffffff0a;
}}

.AdmBtn:hover
{{
	border: 1px solid #ffffff59;
	background-color: #ffffff1a;
}}

.AdmBtn:active
{{
	brightness: 0.8;
}}

.AdmBtnTxt, .AdmBtnArm
{{
	horizontal-align: center;
	vertical-align: center;
	font-family: {FONT_BOLD};
	font-size: 13px;
	letter-spacing: 2px;
	text-transform: uppercase;
	color: #ffffffcc;
}}

.AdmBtnSm
{{
	height: 28px;
	padding: 0px 12px;
	margin-left: 6px;
}}

.AdmBtnSm .AdmBtnTxt, .AdmBtnSm .AdmBtnArm
{{
	font-size: 11px;
}}

/* Destructive buttons ask twice: the first click arms (`arm`), the label swaps, a second click within
   3s does it. */
.AdmBtnArm
{{
	color: #ffb4a8;
	visibility: collapse;
}}

.AdmBtn.arm
{{
	border: 1px solid {ERR};
	background-color: {ERR}2e;
}}

.AdmBtn.arm .AdmBtnTxt
{{
	visibility: collapse;
}}

.AdmBtn.arm .AdmBtnArm
{{
	visibility: visible;
}}

/* Save is quiet while there's nothing to save, and turns gold the moment there is. */
.AdmBtnSave .AdmBtnTxt
{{
	color: #ffffff8c;
}}

.AdmRoot.dirty .AdmBtnSave
{{
	border: 1px solid {GOLD};
	background-color: {gold_fill};
	box-shadow: {GOLD}4d 0px 0px 14px 0px;
}}

.AdmRoot.dirty .AdmBtnSave .AdmBtnTxt
{{
	color: {SHADE};
}}

.AdmRoot.dirty .AdmBtnSave:hover
{{
	brightness: 1.12;
}}

/* ---------- footer: status line + Reload + Save, on every page ---------- */
.AdmFoot
{{
	flow-children: right;
	width: 100%;
	height: {ADM_FOOT_H}px;
	padding: 0px 12px 0px 22px;
	border-top: 1px solid #ffffff12;
	background-color: #00000033;
}}

.AdmStatus
{{
	width: fill-parent-flow( 1.0 );
	vertical-align: center;
	font-family: {FONT_BODY};
	font-size: 14px;
	color: #ffffff80;
	white-space: nowrap;
	text-overflow: ellipsis;
}}

.AdmRoot.dirty .AdmStatus
{{
	color: {GOLD};
}}

/* A message outranks the dirty colour: same specificity, emitted later. */
.AdmFoot .AdmStatus.ok
{{
	color: {OK};
}}

.AdmFoot .AdmStatus.warn
{{
	color: {WARN};
}}

.AdmFoot .AdmStatus.err
{{
	color: {ERR};
}}

/* ---------- Modifiers: filter chips ---------- */
.AdmFilter
{{
	flow-children: right;
	width: 100%;
	height: 30px;
	margin-bottom: 10px;
}}

.AdmChip
{{
	flow-children: right;
	height: 30px;
	padding: 0px 12px;
	margin-right: 6px;
	border: 1px solid #ffffff1f;
	border-radius: 2px;
	background-color: #ffffff05;
}}

.AdmChip:hover
{{
	border: 1px solid #ffffff40;
	background-color: #ffffff12;
}}

.AdmChipDot
{{
	width: 6px;
	height: 6px;
	vertical-align: center;
	margin-right: 8px;
	transform: rotatez( 45deg );
}}

.AdmChipTxt
{{
	vertical-align: center;
	font-family: {FONT_BOLD};
	font-size: 12px;
	letter-spacing: 2px;
	text-transform: uppercase;
	color: #ffffff8c;
}}

.AdmChip:hover .AdmChipTxt
{{
	color: #ffffffd9;
}}

.AdmFilter.cat-all .AdmChip-all
{{
	border: 1px solid {GOLD};
	border-bottom: 2px solid {GOLD};
	background-color: {selected};
}}

.AdmFilter.cat-all .AdmChip-all .AdmChipTxt
{{
	color: #ffffff;
}}
""")
    for cat, colour in CATEGORIES.items():
        wash = fade([(0, f"{colour}14"), (1, f"{colour}3d")], vertical=True)
        w(f".AdmChip-{cat} .AdmChipDot {{ background-color: {colour}; }}")
        w(f".AdmFilter.cat-{cat} .AdmChip-{cat} {{ border: 1px solid {colour}; border-bottom: 2px solid {colour}; background-color: {wash}; }}")
        w(f".AdmFilter.cat-{cat} .AdmChip-{cat} .AdmChipTxt {{ color: #ffffff; }}")
    w(f"""
/* ---------- Modifiers: the grid of slot rows ---------- */
.AdmGrid
{{
	flow-children: right;
	width: 100%;
	height: {ADM_ROWS * (ADM_ROW_H + ADM_ROW_GAP)}px;
}}

.AdmCol
{{
	flow-children: down;
	width: {ADM_COL_W}px;
	height: 100%;
}}

.AdmCol1
{{
	margin-left: {ADM_COL_GAP}px;
}}

/* A slot row is the HUD list's row: 2px category edge, a plate fading right, a hairline on top, the
   item-card tile. It overlays its hairline and body (no flow), like CsrRow. */
.AdmMod
{{
	width: 100%;
	height: {ADM_ROW_H}px;
	margin-bottom: {ADM_ROW_GAP}px;
	border-left: 2px solid #ffffff66;
	background-color: {row_plate("#11151a")};
}}

.AdmMod.empty
{{
	visibility: collapse;
}}

.AdmModRail
{{
	width: 100%;
	height: 1px;
	vertical-align: top;
	background-color: {row_rail};
}}

.AdmModBody
{{
	flow-children: right;
	width: 100%;
	height: 100%;
	padding: 0px 12px 0px 8px;
}}

.AdmTile
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

.AdmModText
{{
	flow-children: down;
	width: fill-parent-flow( 1.0 );
	vertical-align: center;
}}

.AdmModName
{{
	width: 100%;
	font-family: {FONT_BOLD};
	font-size: 15px;
	letter-spacing: 1px;
	text-transform: uppercase;
	color: #ffffff;
	white-space: nowrap;
	text-overflow: ellipsis;
}}

.AdmModLine
{{
	flow-children: right;
	width: 100%;
	margin-top: 3px;
}}

/* The tier chip is a button: each click moves the modifier one tier up, Gold wraps to Mil-Spec. */
.AdmRar
{{
	width: 88px;
	height: 18px;
	border: 1px solid #ffffff33;
	border-radius: 2px;
	background-color: #ffffff0a;
}}

.AdmRar:hover
{{
	brightness: 1.4;
}}

.AdmRar:active
{{
	brightness: 0.8;
}}

.AdmRarTxt
{{
	horizontal-align: center;
	vertical-align: center;
	font-family: {FONT_BOLD};
	font-size: 10px;
	letter-spacing: 1.5px;
	text-transform: uppercase;
	color: #ffffffb3;
}}

/* Moved off its built-in tier (Rarity.Overrides): a small white diamond inside the chip. */
.AdmRarOvr
{{
	width: 4px;
	height: 4px;
	horizontal-align: right;
	vertical-align: center;
	margin-right: 5px;
	background-color: #ffffff;
	transform: rotatez( 45deg );
	visibility: collapse;
}}

.AdmMod.ovr .AdmRarOvr
{{
	visibility: visible;
}}

.AdmModShort
{{
	width: fill-parent-flow( 1.0 );
	vertical-align: center;
	margin-left: 8px;
	font-family: {FONT_BODY};
	font-size: 13px;
	color: #ffffffa6;
	white-space: nowrap;
	text-overflow: ellipsis;
}}

.AdmMod .AdmDirty, .AdmTier .AdmDirty
{{
	margin-left: 8px;
	opacity: 0;
}}

.AdmMod.dirty .AdmDirty, .AdmTier.dirty .AdmDirty
{{
	opacity: 1;
}}

/* The switch: knob right on a lit track = enabled. Deliberately not gold - forty gold switches would drown
   the gold that means "selected" and "unsaved". Base state is enabled; `off` on the row flips it. */
.AdmTog
{{
	width: 44px;
	height: 22px;
	vertical-align: center;
	margin-left: 10px;
	border: 1px solid #ffffff59;
	border-radius: 11px;
	background-color: {tog_on};
}}

.AdmTog:hover
{{
	brightness: 1.3;
}}

.AdmTogKnob
{{
	width: 16px;
	height: 16px;
	margin: 0px 2px;
	horizontal-align: right;
	vertical-align: center;
	border-radius: 8px;
	background-color: #ffffff;
	box-shadow: #00000080 0px 1px 3px 0px;
}}
""")
    for cat, colour in CATEGORIES.items():
        wash = fade([(0, "#ffffff0d"), (0.5, "#ffffff08"), (1, f"{colour}38")], vertical=True)
        won = fade([(0, f"{colour}33"), (1, f"{colour}73")], vertical=True)
        w(f".AdmMod.cat-{cat} {{ border-left-color: {colour}; background-color: {row_plate(premix(colour))}; }}")
        w(f".AdmMod.cat-{cat} .AdmTile {{ border-bottom-color: {colour}; background-color: {wash}; }}")
        # Active this round: the tile lights up like a ready gauge's tile.
        w(f".AdmMod.live.cat-{cat} .AdmTile {{ border: 1px solid {colour}; border-bottom: 2px solid {colour}; background-color: {won}; box-shadow: {colour}80 0px 0px 8px 0px; }}")
    w("")
    for rar, (colour, _) in RARITIES.items():
        wash = fade([(0, f"{colour}1a"), (1, f"{colour}47")], vertical=True)
        w(f".AdmMod.rar-{rar} .AdmRar {{ border: 1px solid {colour}b3; background-color: {wash}; }}")
        w(f".AdmMod.rar-{rar} .AdmRarTxt {{ color: {_light(colour)}; }}")
    w(f"""
/* Four classes: beats the two-class .cat-* .CsrIco tint, like the reel's winner. */
.AdmMod.live .AdmTile .CsrIco
{{
	wash-color: #ffffff;
}}

/* Disabled: struck out and greyed. Emitted after the per-category rules, which have the same
   specificity, so these win. */
.AdmMod.off
{{
	border-left-color: #ffffff1f;
	background-color: {row_plate("#11151a")};
}}

.AdmMod.off .AdmTile
{{
	border-bottom-color: #ffffff1f;
	background-color: {tile};
	opacity: 0.55;
}}

.AdmMod.off .CsrIco
{{
	wash-color: #ffffff59;
}}

.AdmMod.off .AdmModName
{{
	color: #ffffff66;
	text-decoration: line-through;
}}

.AdmMod.off .AdmModShort
{{
	color: #ffffff47;
}}

.AdmMod.off .AdmRar
{{
	opacity: 0.45;
	saturation: 0.2;
}}

.AdmMod.off .AdmTog
{{
	border: 1px solid #ffffff1f;
	background-color: #00000059;
}}

.AdmMod.off .AdmTogKnob
{{
	horizontal-align: left;
	background-color: #ffffff4d;
	box-shadow: #00000000 0px 0px 0px 0px;
}}

/* Rarity off: tiers still edit, but read as dormant everywhere. */
.AdmRoot.rar-off .AdmRar
{{
	saturation: 0;
	opacity: 0.55;
}}

/* ---------- Modifiers: legend + pager ---------- */
.AdmFootRow
{{
	flow-children: right;
	width: 100%;
	height: 30px;
	margin-top: 10px;
}}

.AdmLegend
{{
	width: fill-parent-flow( 1.0 );
	vertical-align: center;
	font-family: {FONT_BODY};
	font-size: 13px;
	color: #ffffff80;
}}

.AdmPager
{{
	flow-children: right;
	height: 30px;
}}

.AdmPageBtn
{{
	height: 30px;
	padding: 0px 14px;
	border: 1px solid #ffffff26;
	border-radius: 2px;
	background-color: #ffffff0a;
}}

.AdmPageBtn:hover
{{
	border: 1px solid #ffffff59;
	background-color: #ffffff1f;
}}

.AdmPageBtnTxt
{{
	vertical-align: center;
	font-family: {FONT_BOLD};
	font-size: 12px;
	letter-spacing: 2px;
	text-transform: uppercase;
	color: #ffffffb3;
}}

.AdmPageInfo
{{
	width: 150px;
	vertical-align: center;
	text-align: center;
	font-family: {FONT_BOLD};
	font-size: 12px;
	letter-spacing: 2px;
	text-transform: uppercase;
	color: #ffffff8c;
}}

.AdmPager.min .AdmPrev, .AdmPager.max .AdmNext
{{
	opacity: 0.3;
}}

/* ---------- Rarity ---------- */
/* Case odds: one strip, five segments sized by f0..f100 (width; nothing transitions here). */
.AdmDist
{{
	flow-children: right;
	width: 100%;
	height: 8px;
	margin-bottom: 14px;
	border-radius: 1px;
	overflow: clip clip;
	background-color: #ffffff12;
}}

.AdmDistSeg
{{
	width: 0%;
	height: 100%;
}}

.AdmRarOffNote
{{
	margin-bottom: 14px;
	font-family: {FONT_BOLD};
	font-size: 13px;
	letter-spacing: 1px;
	text-transform: uppercase;
	color: #ffffff8c;
	visibility: collapse;
}}

.AdmRoot.rar-off .AdmRarOffNote
{{
	visibility: visible;
}}

.AdmRoot.rar-off .AdmDist
{{
	visibility: collapse;
}}

.AdmRoot.rar-off .AdmTierList
{{
	opacity: 0.4;
	saturation: 0.2;
}}

.AdmTierList
{{
	flow-children: down;
	width: 100%;
}}

/* A tier row is a list row in the tier's colour: 3px edge, a plate tinted by the tier. */
.AdmTier
{{
	flow-children: right;
	width: 100%;
	height: {ADM_TIER_H}px;
	margin-bottom: 4px;
	padding: 0px 14px;
	border-left: 3px solid #ffffff66;
}}

/* No enabled modifier in this tier - it never rolls. */
.AdmTier.na
{{
	opacity: 0.45;
}}

.AdmTierText
{{
	flow-children: down;
	width: fill-parent-flow( 1.0 );
	vertical-align: center;
}}

.AdmTierName
{{
	font-family: {FONT_BOLD};
	font-size: 16px;
	letter-spacing: 2px;
	text-transform: uppercase;
	color: #ffffff;
}}

.AdmTierInfo
{{
	margin-top: 2px;
	font-family: {FONT_BODY};
	font-size: 13px;
	color: #ffffff80;
}}

.AdmShare
{{
	flow-children: right;
	width: 284px;
	height: 32px;
	vertical-align: center;
	margin-left: 24px;
}}

/* The gauge's track and clipped fill (clip survives text updates; width doesn't). */
.AdmShareBar
{{
	width: 200px;
	height: 4px;
	vertical-align: center;
	border-radius: 1px;
	background-color: {track};
	box-shadow: #000000a6 0px 0px 4px 0px;
}}

.AdmShareFill
{{
	width: 100%;
	height: 100%;
	border-radius: 1px;
	background-color: #ffffff;
	clip: rect( 0%, 0%, 100%, 0% );
}}

.AdmSharePct
{{
	width: 72px;
	vertical-align: center;
	text-align: right;
	font-family: {FONT_BOLD};
	font-size: 18px;
	letter-spacing: 1px;
	color: #ffffff;
}}

.AdmOvrRow
{{
	flow-children: right;
	width: 100%;
	height: 30px;
	margin-top: 6px;
}}

.AdmOvrInfo
{{
	width: fill-parent-flow( 1.0 );
	vertical-align: center;
	font-family: {FONT_BODY};
	font-size: 13px;
	color: #ffffff8c;
}}
""")
    for rar, (colour, _) in RARITIES.items():
        bar = fade([(0, mix(colour, "#ffffff", 0.35)), (1, colour)], vertical=True)
        w(f".AdmTier.rar-{rar} {{ border-left-color: {colour}; background-color: {row_plate(premix(colour))}; }}")
        w(f".AdmTier.rar-{rar} .AdmTierName {{ color: {_light(colour)}; }}")
        w(f".AdmTier.rar-{rar} .AdmShareFill {{ background-color: {bar}; }}")
        w(f".AdmDistSeg.rar-{rar} {{ background-color: {bar}; }}")
    w("")
    for step in range(FILL_STEPS + 1):
        pct = step * 100 // FILL_STEPS
        w(f".AdmTier.f{step} .AdmShareFill {{ clip: rect( 0%, {pct}%, 100%, 0% ); }}")
        w(f".AdmDistSeg.f{step} {{ width: {pct}%; }}")
    w(f"""
/* ---------- Config ---------- */
.AdmCols
{{
	flow-children: right;
	width: 100%;
	height: fill-parent-flow( 1.0 );
}}

.AdmColBox
{{
	flow-children: down;
	width: {ADM_COL_W}px;
	height: 100%;
}}

.AdmColBoxR
{{
	margin-left: {ADM_COL_GAP}px;
}}

.AdmList
{{
	flow-children: down;
	width: 100%;
	margin-top: 4px;
}}

/* One unsaved change per line, on a gold hairline edge. */
.AdmLine
{{
	width: 100%;
	height: 26px;
	margin-bottom: 4px;
	padding: 4px 0px 0px 12px;
	border-left: 2px solid {GOLD};
	background-color: {set_dirty};
	font-family: {FONT_BODY};
	font-size: 14px;
	color: #ffffffd9;
	white-space: nowrap;
	text-overflow: ellipsis;
}}

.AdmLine.empty
{{
	visibility: collapse;
}}

/* "Nothing to save" - the line, without the gold. */
.AdmLine.na
{{
	border-left-color: #ffffff1f;
	background-color: {set_plate};
	color: #ffffff80;
}}

/* A big action button: title, the armed title, and a sub-line. */
.AdmAction
{{
	flow-children: down;
	width: 100%;
	height: 64px;
	margin-bottom: 8px;
	padding: 0px 16px;
	border: 1px solid #ffffff1f;
	border-left: 3px solid {GOLD};
	border-radius: 2px;
	background-color: {set_plate};
}}

.AdmAction:hover
{{
	background-color: #ffffff14;
}}

.AdmAction:active
{{
	brightness: 0.8;
}}

.AdmActionDanger
{{
	border-left-color: {ERR};
}}

.AdmActionTitle, .AdmActionArm
{{
	margin-top: 13px;
	font-family: {FONT_BOLD};
	font-size: 15px;
	letter-spacing: 2px;
	text-transform: uppercase;
	color: #ffffff;
}}

.AdmActionArm
{{
	color: #ffb4a8;
	visibility: collapse;
}}

.AdmActionSub
{{
	margin-top: 2px;
	font-family: {FONT_BODY};
	font-size: 13px;
	color: #ffffff8c;
}}

.AdmAction.arm
{{
	border: 1px solid {ERR};
	border-left: 3px solid {ERR};
	background-color: {ERR}26;
}}

.AdmAction.arm .AdmActionTitle
{{
	visibility: collapse;
}}

.AdmAction.arm .AdmActionArm
{{
	visibility: visible;
}}

.AdmInfo
{{
	width: 100%;
	margin-top: 4px;
	font-family: {FONT_BODY};
	font-size: 13px;
	color: #ffffffa6;
	white-space: nowrap;
	text-overflow: ellipsis;
}}

.AdmInfoPath
{{
	color: #ffffff80;
}}

.AdmInfoWarn
{{
	color: {WARN};
}}

.AdmInfo.empty
{{
	visibility: collapse;
}}
""")
    return "\n".join(out)


# ---------------------------------------------------------------------------------------------------
# C# contract
# ---------------------------------------------------------------------------------------------------
def setting_rows() -> list[tuple[str, str, object]]:
    rows = []
    for page in ("gen", "hud", "rar"):
        for row in ADM_SETTINGS[page]:
            if not isinstance(row, str):
                rows.append((page, row[0], row[3]))
    return rows


def admin_contract() -> str:
    def pascal(s: str) -> str:
        return "".join(p[:1].upper() + p[1:] for p in s.replace("-", "_").split("_"))

    lines = [
        "    // ---------- admin panel (!rolladmin) ----------",
        '    public const string AdminPanel = "csr_adm";',
        '    public const string AdminVersion = "csr_adm_ver";',
        '    public const string AdminUnsaved = "csr_adm_unsaved";',
        '    public const string AdminClose = "csr_adm_close";',
        '    public const string AdminStatus = "csr_adm_status";',
        '    public const string AdminReload = "csr_adm_reload";',
        '    public const string AdminSave = "csr_adm_save";',
        '    public const string AdminFilter = "csr_adm_flt";',
        '    public const string AdminModCount = "csr_adm_mod_count";',
        '    public const string AdminAllOn = "csr_adm_mod_allon";',
        '    public const string AdminAllOff = "csr_adm_mod_alloff";',
        '    public const string AdminPager = "csr_adm_pager";',
        '    public const string AdminPrev = "csr_adm_prev";',
        '    public const string AdminNext = "csr_adm_next";',
        '    public const string AdminPageInfo = "csr_adm_page";',
        '    public const string AdminOverrideInfo = "csr_adm_ovr_info";',
        '    public const string AdminOverrideReset = "csr_adm_ovr_reset";',
        '    public const string AdminConfigCount = "csr_adm_cfg_count";',
        '    public const string AdminConfigLive = "csr_adm_cfg_live";',
        '    public const string AdminConfigPath = "csr_adm_cfg_path";',
        '    public const string AdminConfigConVar = "csr_adm_cfg_cvar";',
        '    public const string AdminReroll = "csr_adm_reroll";',
        '    public const string AdminClear = "csr_adm_clear";',
        "",
        f"    public const int AdminSlots = {ADM_SLOTS};",
        f"    public const int AdminConfigLines = {ADM_CFG_LINES};",
        "",
        "    public static readonly string[] AdminPages = [" + ", ".join(f'"{k}"' for k, _, _ in ADM_PAGES) + "];",
        "    public static readonly string[] AdminFilters = [" + ", ".join(f'"{k}"' for k, _ in ADM_FILTERS) + "];",
        "    public static readonly string[] AdminTiers = [" + ", ".join(f'"{k}"' for k, _ in ADM_TIERS) + "];",
        "",
        "    /// <summary>Setting rows: the row panel (classes v0/v1/v2, min/max, dirty, na); option buttons are Row + \"_\" + option.</summary>",
    ]
    for page, key, control in setting_rows():
        kind = "step" if control == "step" else "text" if control == "text" else "choice"
        if kind == "choice":
            opts = ", ".join(f'"{o[0]}"' for o in control)
            lines.append(f'    public const string Set{pascal(key)} = "csr_adm_s_{key}";   // {page}, choice: {opts}')
        elif kind == "step":
            lines.append(f'    public const string Set{pascal(key)} = "csr_adm_s_{key}";   // {page}, stepper: _dec, _inc, _val')
        else:
            lines.append(f'    public const string Set{pascal(key)}Value = "csr_adm_s_{key}_val";   // {page}, read-only text')
    lines += [
        "",
        '    public static string AdminTab(string page) => $"csr_adm_tab_{page}";',
        '    public static string AdminTabSub(string page) => $"csr_adm_tab_{page}_sub";   // "mod" and "cfg" only',
        '    public static string AdminFilterChip(string filter) => $"csr_adm_flt_{filter}";',
        '    public static string AdminMod(int i) => $"csr_adm_m{i}";',
        '    public static string AdminModIcon(int i) => $"csr_adm_m{i}_ico";',
        '    public static string AdminModName(int i) => $"csr_adm_m{i}_name";',
        '    public static string AdminModTier(int i) => $"csr_adm_m{i}_tier";      // button',
        '    public static string AdminModTierText(int i) => $"csr_adm_m{i}_rar";',
        '    public static string AdminModShort(int i) => $"csr_adm_m{i}_short";',
        '    public static string AdminModToggle(int i) => $"csr_adm_m{i}_tog";     // button',
        '    public static string AdminDist(string tier) => $"csr_adm_dist_{tier}";',
        '    public static string AdminWeight(string tier) => $"csr_adm_w_{tier}";   // + _dec5 _dec _inc _inc5 (buttons), _val _info _pct (text)',
        '    public static string AdminConfigLine(int i) => $"csr_adm_cfg_l{i}";',
        "",
        '    public static string AdminPageClass(string page) => $"pg-{page}";',
        '    public static string ChoiceClass(int option) => $"v{option}";',
        "",
        '    public const string VarRarity = "rar";',
        "",
        '    public const string Dirty = "dirty";',
        '    public const string AtMin = "min";',
        '    public const string AtMax = "max";',
        '    public const string NotApplicable = "na";',
        '    public const string Off = "off";',
        '    public const string Empty = "empty";',
        '    public const string Live = "live";',
        '    public const string Overridden = "ovr";',
        '    public const string Armed = "arm";',
        '    public const string StatusOk = "ok";',
        '    public const string StatusWarn = "warn";',
        '    public const string StatusError = "err";',
        '    public const string FilterAll = "cat-all";',
    ]
    return "\n".join(lines)


def main() -> None:
    names = icons()
    LAYOUT.write_text(layout(), encoding="utf-8")
    STYLE.write_text(stylesheet(names), encoding="utf-8")
    CONTRACT.write_text(contract(names), encoding="utf-8")
    print(f"wrote {LAYOUT.relative_to(ROOT)}, {STYLE.relative_to(ROOT)}, {CONTRACT.relative_to(ROOT)} ({len(names)} icons)")


if __name__ == "__main__":
    main()
