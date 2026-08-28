#!/usr/bin/env python3
"""
Validates the CSRoll custom HUD contract.

There is no runtime signal for getting this wrong. CCSCustomHudLayout.SetDialogVariableString against
a panel id that isn't in the layout does nothing at all - no exception, no log, no visual difference
from an empty value. Same for a CSS class that no stylesheet rule matches. A typo would be found by a
player weeks later, not by a compiler.

So this script is the check that matters. It diffs, in BOTH directions:

  * panel ids   src/Hud/HudPanelIds.cs  <->  hud/layout/csroll_hud.xml
  * css classes src/Hud/HudClasses.cs   <->  hud/styles/csroll_hud.css
  * accents     resources/hud/modifiers.jsonc -> hud/styles/csroll_hud.css

and enforces the CS2 custom_hud_layout element allowlist (Panel, Label, Image, Button) plus the
absence of any client-side scripting hook, which that entity does not support.

An id declared in C# but missing from the layout is a silently dead write. An id in the layout that
C# never addresses is dead weight in an addon every player has to download. Both are reported.

Runs anywhere Python 3 does, including macOS, where the rest of the HUD toolchain cannot.

Usage:  python3 tools/validate_hud_contract.py [--repo-root PATH]
Exit:   0 all good (warnings allowed), 1 on any error.
"""

from __future__ import annotations

import argparse
import json
import pathlib
import re
import sys
import xml.etree.ElementTree as ET

# CS2's custom_hud_layout supports only these panel types. Anything else silently fails to build a
# panel, taking its children with it.
ALLOWED_ELEMENTS = {"root", "styles", "include", "Panel", "Label", "Image", "Button"}

# The entity has no client-side scripting, so any of these in the layout is a mistake that will never
# fire and may stop the layout compiling.
FORBIDDEN_ATTR_PREFIXES = ("on",)
FORBIDDEN_TOKENS = ("<script", ".vjs", "javascript:")

errors: list[str] = []
warnings: list[str] = []


def error(msg: str) -> None:
    errors.append(msg)


def warn(msg: str) -> None:
    warnings.append(msg)


def read(path: pathlib.Path) -> str:
    if not path.is_file():
        error(f"missing file: {path}")
        return ""
    return path.read_text(encoding="utf-8")


# ---------------------------------------------------------------------------------------------
# C# contract extraction
#
# These mirror how HudPanelIds.cs / HudClasses.cs are written. The coupling is deliberate and narrow:
# both files are explicitly documented as the single source of every id and class, and neither is
# allowed to build a name any other way.
# ---------------------------------------------------------------------------------------------


def csharp_int_consts(source: str) -> dict[str, int]:
    return {m.group(1): int(m.group(2))
            for m in re.finditer(r"public const int (\w+)\s*=\s*(\d+);", source)}


def csharp_string_consts(source: str) -> dict[str, str]:
    return {m.group(1): m.group(2)
            for m in re.finditer(r'public const string (\w+)\s*=\s*"([^"]*)";', source)}


def expand_panel_ids(source: str) -> set[str]:
    """Every id HudPanelIds can produce: plain consts plus each Build(count, i => $"...{i}") family."""
    ids: set[str] = set()

    for name, value in csharp_string_consts(source).items():
        # Var* are dialog-variable names, not panel ids.
        if not name.startswith("Var"):
            ids.add(value)

    counts = csharp_int_consts(source)

    for m in re.finditer(r'Build\((\w+),\s*i\s*=>\s*\$"([^"]*)\{i\}([^"]*)"\)', source):
        count_name, prefix, suffix = m.group(1), m.group(2), m.group(3)
        if count_name not in counts:
            error(f"HudPanelIds.cs: Build() references unknown count constant '{count_name}'")
            continue
        for i in range(counts[count_name]):
            ids.add(f"{prefix}{i}{suffix}")

    return ids


def expand_classes(source: str) -> set[str]:
    """Every class HudClasses can emit: plain consts, the duration ladder, the width ladder, accents."""
    classes: set[str] = set()

    for name, value in csharp_string_consts(source).items():
        # Group* are lookup keys for the service's radio-group bookkeeping, never emitted as classes.
        # Glyph* is a text symbol, not a class.
        if name.startswith("Group") or name.startswith("Glyph"):
            continue
        classes.add(value)

    ladder = re.search(r"DurationLadder\s*=\s*\[([^\]]*)\]", source)
    if not ladder:
        error("HudClasses.cs: could not find DurationLadder")
    else:
        for token in re.findall(r"(\d+(?:\.\d+)?)f", ladder.group(1)):
            classes.add(f"dur-{int(float(token))}")

    step_match = re.search(r"public const int WidthStepPercent\s*=\s*(\d+);", source)
    if not step_match:
        error("HudClasses.cs: could not find WidthStepPercent")
    else:
        step = int(step_match.group(1))
        for percent in range(0, 101, step):
            classes.add(f"w{percent}")

    accents = re.search(r"Accents\s*=\s*\n?\s*\[([^\]]*)\]", source)
    if not accents:
        error("HudClasses.cs: could not find the Accents list")
    else:
        for name in re.findall(r'"([^"]+)"', accents.group(1)):
            classes.add(f"accent-{name}")

    return classes


# ---------------------------------------------------------------------------------------------
# Layout / stylesheet extraction
# ---------------------------------------------------------------------------------------------


def layout_ids(xml_text: str, path: pathlib.Path) -> set[str]:
    try:
        root = ET.fromstring(xml_text)
    except ET.ParseError as exc:
        error(f"{path.name}: not well-formed XML - {exc}")
        return set()

    ids: set[str] = set()
    seen: set[str] = set()

    # resourcecompiler rejects the whole layout with "Found root panel with 'id' attribute, which is
    # not permitted" if the outermost panel is named. Caught here because that is a hard compile
    # failure discovered only on a Windows box with Workshop Tools - a long way from where the layout
    # is written. Wrap the panel in an unnamed one and move the id inward.
    for child in root:
        if child.tag != "styles" and child.attrib.get("id"):
            error(f"{path.name}: the outermost panel <{child.tag} id=\"{child.attrib['id']}\"> may not "
                  "have an id - Panorama rejects the layout. Wrap it in an unnamed panel and put the "
                  "id on the inner one.")

    for element in root.iter():
        if element.tag not in ALLOWED_ELEMENTS:
            error(f"{path.name}: <{element.tag}> is not one of the supported panel types "
                  f"({', '.join(sorted(t for t in ALLOWED_ELEMENTS if t[0].isupper()))})")

        for attr in element.attrib:
            # 'onclick', 'onactivate', ... - the entity dispatches none of them.
            if attr.startswith(FORBIDDEN_ATTR_PREFIXES) and attr not in ("overflow",):
                error(f"{path.name}: <{element.tag}> has event attribute '{attr}'; "
                      "custom_hud_layout has no client-side scripting")

        panel_id = element.attrib.get("id")
        if panel_id:
            if panel_id in seen:
                error(f"{path.name}: duplicate id '{panel_id}'")
            seen.add(panel_id)
            ids.add(panel_id)

    lowered = xml_text.lower()
    for token in FORBIDDEN_TOKENS:
        if token in lowered:
            error(f"{path.name}: contains '{token}', which custom_hud_layout does not support")

    return ids


def stylesheet_classes(css_text: str) -> set[str]:
    # Strip comments first so a class named only inside prose doesn't count as defined.
    stripped = re.sub(r"/\*.*?\*/", "", css_text, flags=re.S)
    # Selector position only - ignore anything inside a declaration block.
    selectors = re.sub(r"\{[^}]*\}", " ", stripped)
    return set(re.findall(r"\.([A-Za-z_][\w-]*)", selectors))


def check_stylesheet_syntax(css_text: str, path: pathlib.Path) -> None:
    """
    Panorama-specific CSS rules that resourcecompiler does NOT enforce.

    Worth being strict here. The compiler happily accepts this file and only the client's runtime
    parser rejects it - and when it does, the failure is spectacular: the stylesheet fails to parse,
    so the layout's <include> fails, so no panel is built at all, and the log fills with "Unable to
    find panel with id ..." for every id in the layout. Nothing in that output points at the CSS.
    """
    stripped = re.sub(r"/\*.*?\*/", "", css_text, flags=re.S)

    # Panorama requires the keyframe name to be quoted: @keyframes 'name'. Unquoted gives
    # "Invalid @keyframe name (missing quotes or empty)" and takes the whole stylesheet with it.
    for m in re.finditer(r"@keyframes\s+([^\s{'\"]\S*)", stripped):
        error(f"{path.name}: @keyframes name {m.group(1)!r} must be quoted, e.g. @keyframes 'name' - "
              "unquoted names fail to parse and the entire stylesheet is discarded")

    for m in re.finditer(r"animation-name:\s*([^\s;'\"]+)\s*;", stripped):
        error(f"{path.name}: animation-name {m.group(1)!r} must be quoted, e.g. animation-name: 'name';")

    # 'noclip' means do NOT clip. A fixed-size viewport meant to mask an overflowing child needs
    # 'clip clip'. It IS correct on the outermost panel, which has to let children draw past it.
    for m in re.finditer(r"([.#][\w-]+)[^{}]*\{[^}]*overflow:\s*noclip", stripped):
        if "root" not in m.group(1):
            warn(f"{path.name}: {m.group(1)} sets 'overflow: noclip', which disables clipping - "
                 "use 'clip clip' if this panel is meant to be a viewport")

    # Transitioning `width` is a trap here. Writing ANY dialog variable in the same subtree re-measures
    # the label, re-lays-out the row, re-resolves `width: 100%` and restarts the transition - so a bar
    # sitting next to its own countdown text resets itself every time that text ticks. `clip` is
    # documented as having no impact on layout and being supported for transitions, so it is immune.
    for m in re.finditer(r"transition-property:\s*([^;]*width[^;]*);", stripped):
        error(f"{path.name}: transition-property includes 'width' ({m.group(1).strip()!r}). A dialog-"
              "variable write anywhere in the same subtree restarts a width transition - use "
              "clip: rect(...) for bars and gauges instead")

    # z-index only orders siblings within one parent, so it has to be on the outermost panel to lift
    # the layout above the built-in HUD. On an inner panel it does nothing at any value.
    if "z-index" not in stripped:
        warn(f"{path.name}: no z-index anywhere. Without a large z-index on the OUTERMOST panel the "
             "layout can paint underneath the built-in CS2 HUD")


def stylesheet_style_includes(xml_text: str) -> list[str]:
    return re.findall(r'<include\s+src="s2r://panorama/styles/custom_game/([^"]+)\.vcss_c"', xml_text)


# ---------------------------------------------------------------------------------------------


def main() -> int:
    parser = argparse.ArgumentParser(description="Validate the CSRoll custom HUD contract.")
    parser.add_argument("--repo-root", default=None, help="Repository root (default: parent of tools/)")
    args = parser.parse_args()

    root = pathlib.Path(args.repo_root) if args.repo_root else pathlib.Path(__file__).resolve().parent.parent

    panel_ids_cs = read(root / "src" / "Hud" / "HudPanelIds.cs")
    classes_cs = read(root / "src" / "Hud" / "HudClasses.cs")
    layout_path = root / "hud" / "layout" / "csroll_hud.xml"
    style_path = root / "hud" / "styles" / "csroll_hud.css"
    layout_xml = read(layout_path)
    style_css = read(style_path)
    presentation_raw = read(root / "resources" / "hud" / "modifiers.jsonc")

    if errors:
        return report()

    declared_ids = expand_panel_ids(panel_ids_cs)
    declared_classes = expand_classes(classes_cs)
    xml_ids = layout_ids(layout_xml, layout_path)
    css_classes = stylesheet_classes(style_css)
    check_stylesheet_syntax(style_css, style_path)

    # --- panel ids, both directions ---
    for missing in sorted(declared_ids - xml_ids):
        error(f"panel id '{missing}' is declared in HudPanelIds.cs but absent from the layout - "
              "every write to it would silently do nothing")
    for unused in sorted(xml_ids - declared_ids):
        error(f"panel id '{unused}' exists in the layout but is not in HudPanelIds.cs - "
              "the server can never address it")

    # --- css classes ---
    for missing in sorted(declared_classes - css_classes):
        error(f"class '{missing}' can be emitted by HudClasses.cs but no rule in the stylesheet "
              "matches it - it would have no visible effect")

    # --- stylesheet include actually points at a real stylesheet ---
    includes = stylesheet_style_includes(layout_xml)
    if not includes:
        error("the layout includes no custom_game stylesheet")
    for included in includes:
        if not (root / "hud" / "styles" / f"{included}.css").is_file():
            error(f"the layout includes '{included}.vcss_c' but hud/styles/{included}.css does not exist")

    # --- presentation data ---
    if presentation_raw:
        try:
            # .jsonc - strip line comments before parsing.
            entries = json.loads(re.sub(r"//[^\n]*", "", presentation_raw))
        except json.JSONDecodeError as exc:
            error(f"resources/hud/modifiers.jsonc is not valid JSON once comments are stripped - {exc}")
            entries = {}

        if "*" not in entries:
            error("resources/hud/modifiers.jsonc has no '*' fallback entry; any unlisted modifier "
                  "(including every ConVarModifiers/*.cfg one) would fall back to a hardcoded default")

        for name, entry in entries.items():
            accent = entry.get("accent")
            if not accent:
                error(f"modifiers.jsonc: '{name}' has no accent")
            elif f"accent-{accent}" not in css_classes:
                error(f"modifiers.jsonc: '{name}' uses accent '{accent}', "
                      f"but .accent-{accent} is not in the stylesheet")
            if not entry.get("glyph"):
                error(f"modifiers.jsonc: '{name}' has no glyph")

        # Not an error: a modifier with no entry falls back cleanly by design.
        registry = read(root / "src" / "CSRoll.Registry.cs")
        modifier_dir = root / "src" / "Modifiers"
        known: set[str] = set()
        if modifier_dir.is_dir():
            for source in modifier_dir.glob("*.cs"):
                known.update(re.findall(r'\bName\s*=\s*"([A-Za-z]+)"', source.read_text(encoding="utf-8")))
        for name in sorted(known - set(entries)):
            warn(f"modifier '{name}' has no entry in modifiers.jsonc - it will use the '*' fallback badge")

    return report()


def report() -> int:
    for message in warnings:
        print(f"  warning: {message}")
    for message in errors:
        print(f"  ERROR:   {message}")

    if errors:
        print(f"\nHUD contract INVALID - {len(errors)} error(s), {len(warnings)} warning(s).")
        return 1

    print(f"\nHUD contract OK - 0 errors, {len(warnings)} warning(s).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
