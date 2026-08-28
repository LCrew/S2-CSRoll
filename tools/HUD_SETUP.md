# CSRoll Custom HUD — setup

The custom HUD is a **second deliverable** alongside the plugin, and it is worth understanding why
before you start, because every awkward step below follows from it.

CS2's `custom_hud_layout` entity takes a Panorama layout *path*. The client resolves that path against
its own game files. So the compiled `.vxml_c` / `.vcss_c` must already be on the player's disk before
the server ever spawns the entity — there is no mechanism for a server plugin to push them. That means:

| Artifact | Built by | Installed on | Contains |
| --- | --- | --- | --- |
| `CSRoll.zip` | `dotnet publish -c Release` | the **server** | the plugin |
| `csroll_hud.vpk` | this document | the **server** *and* every **player** | the HUD |

Publishing the Workshop item does not deploy the plugin, and deploying the plugin does not give
anyone the HUD. You need both.

**`CustomHud.Enabled` defaults to `false`.** Until you complete this document, CSRoll behaves exactly
as it did before the HUD existed, and that is the intended state for any server that does not want to
take this on.

---

## Prerequisites

- **Windows.** `resourcecompiler.exe` is part of the CS2 Workshop Tools and has no macOS/Linux build.
- **CS2 + "Counter-Strike 2 Workshop Tools"** — Steam → Library → Tools.
- **[VPKEdit CLI](https://github.com/craftablescience/VPKEdit)** (`vpkeditcli.exe`) for packing.
- **Python 3** for `-Action Validate`.
- **SwiftlyS2 ≥ 1.4.6** on the server. `CCSCustomHudLayout` does not exist before that.

Authoring and validating work on any OS. Only compiling, packing and publishing need Windows.

---

## 1. Validate before you compile

```bash
python3 tools/validate_hud_contract.py
```

Do this first, every time, and do not skip it because the change "was only a rename".

A wrong panel id is completely silent: `SetDialogVariableString` against an id the layout does not
contain throws nothing, logs nothing, and looks exactly like the value being empty. Same for a CSS
class no rule matches. This script is the only thing between a typo and a player finding it. It diffs,
in both directions:

- `src/Hud/HudPanelIds.cs` ↔ `hud/layout/csroll_hud.xml`
- `src/Hud/HudClasses.cs` ↔ `hud/styles/csroll_hud.css`
- `resources/hud/modifiers.jsonc` → the accent classes in the stylesheet

and enforces the `Panel` / `Label` / `Image` / `Button` allowlist.

---

## 2. Create the Workshop addon

1. Launch **Counter-Strike 2 Workshop Tools** → **Create New Addon** → name it `csroll_hud`.
2. Note the two trees it creates. They are easy to confuse and the distinction matters:
   - `content/csgo_addons/csroll_hud/` — **sources** (`.xml`, `.css`, `.png`)
   - `game/csgo_addons/csroll_hud/` — **compiled output** (`.vxml_c`, `.vcss_c`, `.vtex_c`)

### 2a. VpkDirectories — read this one

Open the addon's `AddonConfig` in Workshop Tools and make sure `VpkDirectories` includes:

```
"include"   "panorama/layout/custom_game"
"include"   "panorama/styles/custom_game"
"include"   "panorama/images/custom_game"
```

> **This is the single most common way to lose an afternoon.** Without these entries the VPK builds
> successfully and simply contains none of the Panorama files. Every step downstream keeps reporting
> success, the addon publishes fine, clients download it, and the HUD never appears — with no error
> anywhere to explain why.

`VpkDirectories` controls what Workshop Tools *collects*. It is unrelated to `FileSystem/SearchPaths`,
which controls what a local install *mounts*. You need both, for different reasons.

---

## 3. Compile and pack

Run these from an **elevated PowerShell** prompt, in the repo root.

> Commands below are deliberately single-line. Line continuations differ between shells - `^` in
> cmd.exe, a backtick in PowerShell - and pasting the wrong one makes the continuation character
> itself get parsed as an argument value.

Allow the unsigned script for this session:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
```

Then, one step at a time - each has different prerequisites, so running them separately tells you
exactly which one you are missing:

```powershell
.\tools\build_hud_resources.ps1 -Action Compile -CS2Root "C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive"
```

```powershell
.\tools\build_hud_resources.ps1 -Action Pack -CS2Root "C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive" -VpkEditCli "C:\path\to\vpkeditcli.exe"
```

`-Action Build` chains Validate + Compile + Pack in one go, but it needs Python and VPKEdit present
up front; the separate steps above need only `resourcecompiler.exe` for Compile and VPKEdit for Pack.

From `cmd.exe` instead of PowerShell, prefix with
`powershell -ExecutionPolicy Bypass -File ` and keep it on one line.

> **Run elevated.** CS2 lives under `C:\Program Files (x86)`, and `Compile`, `Pack` and `Install` all
> write there - staging sources into `content\csgo_addons\`, copying the VPK into `game\csgo\overrides\`,
> and editing `gameinfo.gi`. Without an Administrator prompt these fail with access-denied part-way
> through, which is messier than failing up front.

> **For a local test you can ignore step 2a entirely.** VpkDirectories governs Workshop Tools' own
> publish flow; the Pack action above builds the VPK directly from the compiled folder with VPKEdit,
> so it does not care. That section only matters when you publish (step 5).

`Build` = `Validate` → `Compile` → `Pack`, and produces `build/hud/csroll_hud.vpk`.

The script fails loudly if `resourcecompiler` reports success but emits no `.vxml_c` / `.vcss_c` —
that is the VpkDirectories problem above, caught early.

---

## 4. Test locally before publishing

```powershell
.\tools\build_hud_resources.ps1 -Action Install -CS2Root "C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive"
```

This copies the VPK into `game\csgo\overrides\` and prints the `gameinfo.gi` line to add under
`FileSystem` → `SearchPaths`, **above** the existing `Game csgo` entry:

```
Game    csgo/overrides/csroll_hud.vpk
```

Back `gameinfo.gi` up first — a CS2 update can overwrite it. Restart CS2 afterwards.

This route is for iterating on your own machine only. It changes nothing for anyone else.

---

## 5. Publish to the Workshop

1. Workshop Tools → **Publish** the addon.
2. Before publishing, confirm the preview lists at least `[vxml_c]: 1 Files` and `[vcss_c]: 1 Files`.
   If either is zero, go back to step 2a.
3. Set visibility to **Public**. Friends-only and Hidden items cannot be downloaded by your players.
4. Record the Workshop ID from the item's URL.

---

## 6. Deliver it to players

Install [**AddonsManager**](https://github.com/SwiftlyS2-Plugins/AddonsManager) on the server and add
your Workshop ID to its config:

```jsonc
{
  "Main": {
    "Addons": [
      "YOUR_WORKSHOP_ID_HERE"
    ]
  }
}
```

It downloads and mounts the addon server-side and makes connecting clients fetch it. Expect a short
one-time download for each player on their first connect after you publish.

---

## 7. Enable the HUD

In the plugin's `config.jsonc`, under `Main`:

```jsonc
"CustomHud": {
  "Enabled": true,
  "LayoutPath": "panorama/layout/custom_game/csroll_hud.xml",
  "ReplaceCenterHtml": false,   // leave false until step 8 passes
  "ShowRevealCard": true,
  "ShowTracker": true,
  "TrackerRowCount": 6,
  "VersionStamp": "csroll-hud-1"
}
```

Then `sw plugins reload CSRoll` (or change the map).

---

## 8. Verify — in this order

Each step isolates one link in the chain, so a failure tells you *where* the problem is.

| # | Where | Command | Confirms |
| --- | --- | --- | --- |
| 1 | server console | `sw_searchpath` | the addon is mounted server-side |
| 2 | server log | — | `[CSRoll][HUD] Spawned custom_hud_layout #N` |
| 3 | in game | `!hudstatus` | the plugin has a live entity, and draws a 10s test bar |
| 4 | client console | `dev_report_info_hud_layout` | the client resolved the layout; ids match |
| 5 | in game | `!randomroundsreroll` | the reel spins, lands, and the card animates in |

**Step 3 is the one that matters.** A live entity only proves the *server* side works — whether a
given player has the addon is invisible from the server. `!hudstatus` draws a test bar on your own
HUD precisely so you can answer that with your eyes.

### If step 3 prints in chat but nothing appears on screen

Your client does not have the addon. Do **not** enable `ReplaceCenterHtml` — with it on, players in
that state get no reveal at all. Work backwards: step 5 (Public?), step 6 (AddonsManager config?),
step 2a (VpkDirectories?).

---

## 9. Only now, switch the reveal over

```jsonc
"ReplaceCenterHtml": true
```

This is deliberately last and deliberately separate. It stops the center-HTML reveal for **everyone**
on the server — and since the server cannot tell which clients have the addon, it cannot be decided
per player. Flip it only once you have seen the HUD render with your own eyes.

Everything else stays untouched either way: chat summaries, the nine modifiers' center-HTML gauges,
and the spectator HUD are all unaffected by this setting.

---

## Iterating on the design

Any edit to `hud/layout/*.xml` or `hud/styles/*.css` means recompile → republish → every player
re-downloads. Two consequences worth planning around:

- **Do CSS work through the local override** (step 4) until you are happy, then publish once.
- **The DOM is over-provisioned on purpose.** 20 reel rows, 6 tracker rows, 3 reveal cards, two fill
  elements per bar, and the unused `csr_self` / `csr_spec` regions all exist in version 1 precisely
  because adding a panel later is expensive. Prefer using what is already there.

`resources/hud/modifiers.jsonc` is the exception — it is server-side data, so changing a glyph or an
accent needs only a plugin reload. No recompile, no republish.

---

## Rolling back

Set `"Enabled": false` and reload. The plugin returns to pure center-HTML output; nothing else in the
config or on the server needs touching. That is also the automatic behaviour if the layout entity ever
fails to spawn — the HUD degrades to the old path rather than to a broken screen.
