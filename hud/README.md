# `hud/` — Panorama sources for the CSRoll custom HUD

These files are **not** part of the plugin. They are compiled by CS2's `resourcecompiler` and shipped
to players as a Steam Workshop addon. `CSRoll.csproj` explicitly excludes this folder so it can never
end up inside `CSRoll.zip`.

Full build and publish instructions: [`../tools/HUD_SETUP.md`](../tools/HUD_SETUP.md).

```
layout/csroll_hud.xml    the static DOM
styles/csroll_hud.css    all styling, transitions and keyframes
images/                  optional textures (see below)
```

## The contract

The server can only do three things to this HUD: set a dialog-variable string, add or remove a CSS
class, and toggle input capture. It cannot create a panel, set a width, set a duration, or set an
image source.

Everything else follows from that:

- **The DOM is static.** Every panel that can ever appear is declared in the layout up front and
  simply hidden when unused. That is why there are 20 reel rows, 6 tracker rows and 3 reveal cards
  regardless of what the current plugin version uses.
- **Continuous values are class ladders.** `dur-0…dur-60` for transition durations, `w0…w100` in 5%
  steps for widths. The server picks the nearest rung.
- **Icons are glyphs, not textures.** Since an image source cannot be set from the server, a
  per-modifier texture would need one CSS class and one image file per modifier — and every one not
  yet drawn would log a warning on every client. `resources/hud/modifiers.jsonc` supplies a symbol
  instead, written straight into the panel as text.

## Ids and classes are mirrored in C#

- `src/Hud/HudPanelIds.cs` — every panel id
- `src/Hud/HudClasses.cs` — every class the server can toggle

Keep both sides in step, and check it before compiling:

```bash
python3 tools/validate_hud_contract.py
```

This is not optional diligence. A panel id that does not exist in the layout produces **no error at
all** — the write silently does nothing and looks identical to an empty value. The validator is the
only thing that catches it.

## Adding a panel

1. Add the id to `HudPanelIds.cs` (a plain `const`, or a `Build(...)` family with its count constant).
2. Add the panel to `layout/csroll_hud.xml` with the same id.
3. Style it in `styles/csroll_hud.css`.
4. Run the validator.
5. Recompile and republish the addon — **every player re-downloads it.**

Step 5 is why the layout is over-provisioned. Check whether an existing spare panel will do first;
`csr_self` and `csr_spec` are already reserved for the center-HTML gauges and spectator readout when
those migrate.

## `images/`

Currently unused — the HUD's only texture is CS2's own
`s2r://panorama/images/backgrounds/bluedots_large_png.vtex`, which is guaranteed to resolve on every
client because it ships with the game.

To add your own, drop `.png` files here (the build script stages them to
`panorama/images/custom_game/csroll/`) and reference them from the stylesheet as:

```css
background-image: url("s2r://panorama/images/custom_game/csroll/yourfile_png.vtex");
```

Make sure `panorama/images/custom_game` is in the addon's `AddonConfig` → `VpkDirectories`, or the
files are silently dropped from the VPK.
