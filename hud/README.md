# CSRoll custom HUD

The Panorama HUD: an icon carousel for the roll, the active-modifier list under the radar, and floating
gauges above the bottom HUD. It's a **second deliverable** next to the plugin:

| Piece | Built by | Installed on |
| --- | --- | --- |
| `CSRoll.zip` | `dotnet publish -c Release` | the server |
| the `csroll_hud` Workshop addon | `tools\build_csroll_hud.ps1` + Workshop Manager | every player, via AddonsManager |

A HUD layout is a client-side file: the server only names it and then sends text and CSS classes.
Players without the addon simply can't draw it, which is why the plugin keeps them on the classic
centre text.

## What's in here

```
hud/icons/*.svg                                            icon sources, one per modifier (white, 64x64) + InfiniteRoll
hud/panorama/images/custom_game/csroll_png/x32|x64|x160/   GENERATED - the PNGs the HUD draws (tools/rasterize_icons.py)
hud/panorama/layout/custom_game/csroll_hud.xml             GENERATED - the layout
hud/panorama/styles/custom_game/csroll_hud.css             GENERATED - its stylesheet
src/Hud/HudLayout.g.cs                                     GENERATED - the ids and classes the plugin drives
tools/generate_hud.py                                      writes the layout, stylesheet and C# contract from one spec
```

Never edit the generated files. Change `tools/generate_hud.py` (or an SVG in `hud/icons`) and run:

```bash
python3 tools/rasterize_icons.py   # only after icon changes
python3 tools/generate_hud.py
```

The HUD draws PNGs, not the SVGs: Panorama rasterizes an SVG background at the size written inside the
file, and these carry none, so in game they came out as stretched blobs.

A wrong panel id or CSS class fails silently in game, so the layout, stylesheet and C# contract are
written by one script that can't disagree with itself.

## One-time setup (Windows)

1. **Install the Counter-Strike 2 Workshop Tools** (Steam > Library > Tools).
2. **Create the addon.** Launch the Workshop Tools > *Create New Addon* > name it `csroll_hud`.
3. **Tell the publisher to include Panorama files.** Back up
   `...\Counter-Strike Global Offensive\game\csgo\gameinfo.gi`, then add these lines inside its
   `AddonConfig` > `VpkDirectories` block:

   ```
   "include"       "panorama/layout/custom_game"
   "include"       "panorama/styles/custom_game"
   "include"       "panorama/images/custom_game"
   ```

   Without them the Workshop item publishes fine and contains none of the HUD files. This is the
   `AddonConfig` block, which only affects what gets published; it does not mark your client as
   modified. (The `FileSystem` > `SearchPaths` block is a different thing - leave it alone.)

## Build and publish (every time the design or icons change)

1. From the repo root, in PowerShell:

   ```powershell
   Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
   .\tools\build_csroll_hud.ps1
   ```

   Pass `-Cs2Root "D:\...\Counter-Strike Global Offensive"` if CS2 isn't in the default Steam folder.
   The script copies the sources into the addon, compiles them, and stops with a list if any compiled
   file is missing.
2. Open the **Counter-Strike 2 Workshop Manager** > *New* (or your existing item) > select `csroll_hud`.
3. **Check the contents preview before submitting**: it must list `vxml_c`, `vcss_c` and `vtex_c`
   files. If any is 0, step 3 of the setup didn't take.
4. Title, description, preview image, visibility **Public**, submit. Note the Workshop ID from the
   item's URL.

Every republish makes every player download the addon again, so batch design changes.

## Server

1. Install [AddonsManager](https://github.com/SwiftlyS2-Plugins/AddonsManager) and add the Workshop ID
   to its config. It mounts the addon on the server and makes connecting players download it.
2. The HUD is on by default. Its settings are in CSRoll's `config.jsonc`, under `Main`:

   ```jsonc
   "CustomHud": {
     "Enabled": true,
     "Mode": "Everyone",         // "OptIn" while AddonsManager isn't delivering the addon yet
     "PromptSeconds": 15,
     "RevealHoldSeconds": 4,
     "BrandText": "Powered by CSRoll", // the bar above the reel after a roll; "" hides it
     "ShowToSpectatorTeam": true,
     "ListOffset": 0             // 0-4: move the list down if the radar/money overlaps it
   }
   ```

   A config written by CSRoll 1.38 or older has no `CustomHud` block, so these defaults apply; add the
   block to change them, then `!rollreload` (or change the map).

## Who sees what

- **Everyone** (default, opt-out): everyone is on the custom HUD unless they switch back with `!hud`;
  the choice is remembered per SteamID (`hud-players.json` in the plugin's data folder). The server
  can't tell who has the addon, and a player without it would see no reveal at all, so everyone who
  hasn't chosen gets a one-time chat line per map: *"Can't see it? Type !hud for the classic display."*
- **OptIn**: everyone starts on the centre text. For 15s after each spawn a *NEW HUD READY* prompt
  appears - but only on screens that have the addon, since nobody else can draw the layout. Typing
  `!hud` switches that player over; `!hud` again switches back. Use it until AddonsManager is
  delivering the addon.
- Anyone watching another player - dead on T/CT, or on the **spectator team** - sees that player's
  list as *"Name's Modifiers"*. If spectator-team players see nothing at all, set
  `"ShowToSpectatorTeam": false` to put them back on the centre-text spectator panel.

## Checking it works

| Where | What | Tells you |
| --- | --- | --- |
| server log | `[CSRoll][HUD] Spawned custom_hud_layout #N` | the plugin created the HUD |
| in game | `!hudstatus` (admin) | entity live, mode, how many players are on it |
| in game | the *NEW HUD READY* prompt after spawning | your client has the addon |
| in game | `!hud`, then `!randomroundsreroll` | carousel spins and lands, list and gauges appear |
| client console | `Failed to load layout '...'` | your client doesn't have the addon (mount/download problem, not the layout) |

To turn it all off, set `"Enabled": false` and reload - everyone is back on the centre text.
