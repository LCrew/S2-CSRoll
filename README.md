> 🤖 **This plugin was created with [Claude AI](https://claude.ai).**

<div align="center">
  <h1><strong>CSRoll</strong></h1>
  <p>A chaos-mod style plugin for CS2, built on <a href="https://swiftlys2.net">SwiftlyS2</a>.</p>
</div>

Each round all players roll a random modifiers that apply for a round.
<table align="center">
  <tr>
    <th>Non-Workshop HUD</th>
    <th>Workshop HUD</th>
  </tr>
  <tr>
    <td><img src="./demo.gif" width="400" alt="CSRoll with the classic centre-text reveal" /></td>
    <td><img src="./demo2.gif" width="400" alt="CSRoll with the custom Workshop HUD" /></td>
  </tr>
</table>

## Requirements

- [SwiftlyS2](https://swiftlys2.net) on your CS2 server.
- For the custom HUD (case-style roll reel, modifier list, gauges - on by default):
  - the **[CSRoll HUD Workshop addon](https://steamcommunity.com/sharedfiles/filedetails/?id=3791730390)** (Workshop ID `3791730390`), and
  - [AddonsManager](https://github.com/SwiftlyS2-Plugins/AddonsManager), which mounts the addon on the server and makes players download it when they connect.

  Without them, set `CustomHud.Enabled` to `false` in `config.jsonc` to keep everyone on the classic centre-text reveal (see Installation, step 5).

## Modifier List

| Modifier | Description |
| --- | --- |
| Cluster Grenades | Grenades spawn mini grenades (configurable) |
| Suicide Bomber | On death, drop grenades dealing bonus HE damage (configurable) |
| Conditional Invisibility | Invisible while silent - sound reveals you |
| Vanish | Press Inspect Weapon to vanish briefly - on a cooldown |
| Drunk | A/D movement is mirrored |
| Juggernaut | Max health set to 300 |
| Random Health | Random health |
| Flashing Bullets | Chance to blind an enemy you hit |
| Disarming Bullets | Chance to disarm an enemy you hit |
| Hard Head | Immune to headshots |
| Butterfingers | Miss a shot, drop your weapon |
| Boomerang Bullets | Missed shots damage you - bonus health |
| Steel Body | Only headshots and utility hurt you |
| More Damage | Deal 33% more damage |
| Revive | Chance to survive lethal damage |
| Small Players | 2x smaller, 50 HP |
| Poisonous Smoke | Your smokes damage enemies inside them |
| Longer Flashes | Flashes last longer (configurable) |
| Chinese Grenades | Randomized grenade fuse timers |
| Swap On Death | Swap places on kill |
| Swap On Hit | Swap places on hit |
| Master Zeus | Zeus recharges fast and hits at long range |
| Smoke Immunity | Smokes are invisible - VAC SAFE |
| Vampire | Heal for the damage you deal |
| Saint | Chance for a kill to revive a dead teammate |
| Speedhack | You are really fast |
| Teleport On Reload | Reloading teleports you to spawn |
| Teleport On Hit | Getting hit teleports you to spawn |
| One Per Reload | 1 bullet per reload |
| No Recoil | No recoil, no spread |
| Wallhack | Free cheats, for free - VAC SAFE |
| Random Loadout | Random loadout |
| Walking Grenadier | No guns - unlimited HE grenades |
| Heavy Boots | Slower - armor, helmet and bonus health |
| Human Shield | Carry a hostage - slower, but shielded from behind |
| Jetpack | Hold jump in the air to thrust |
| Bunny Hop | Hold jump to auto bunny-hop - every hop is faster |
| Infinite Ammo | All weapons go brrrrrr... |
| Atomic Explosions | HE grenades deal much more damage |
| Increased Spread | Your aim just got worse... |
| Plant Anywhere | Plant anywhere after a delay (configurable) |
| Flanker | Press Inspect Weapon to teleport behind an enemy |
| Kamikaze Chickens | Inspect releases beeping chickens that explode on enemies |
| Regeneration | Heals over time - faster standing still |
| Bounty | Damage enemies for bonus money |
| Weapon Roulette | Random weapon, re-rolled often |
| Recall | Press Inspect to rewind a few seconds |
| Butterfly Effect | A 2nd modifier, re-rolled every 20s |
| Mimic | Kills steal the victim's modifier |

Display names and descriptions are fully customizable via `resources/translations/en.jsonc`.

## Commands

All commands are chat commands (prefix with `!`).

| Command | Access | Description |
| --- | --- | --- |
| `!rolllist` | Everyone | Prints the name and description for each registered modifier. |
| `!rollactive` | Everyone | Prints the name, scope (Global or which player(s)), and description for each active modifier. |
| `!rollhelp` | Everyone | Prints every available CSRoll command. |
| `!rollmenu` | Admin | Opens the CSRoll configuration menu (random rounds, modifiers-per-player, per-modifier enable/disable). |
| `!memodifier <name>` | Admin | Apply a modifier scoped to just yourself, without affecting anyone else. |
| `!rolltoggle <name>` | Admin | Adds the modifier globally if inactive, removes it (from everyone currently assigned) if active. |
| `!removemodifier <name>` | Admin | Remove an active modifier. |
| `!removemodifiers` | Admin | Clear / Remove all active modifiers. |
| `!disablemodifier <name>` | Admin | Deactivate a modifier and remove it from the registered pool so it can't be added/rolled again until re-enabled (`!rollmenu`) or the plugin reloads. |
| `!addrandommodifier` | Admin | Add a random modifier to be activated immediately. |
| `!randomrounds` | Admin | Toggle random rounds on/off. |
| `!randomroundsreroll` | Admin | Re-roll the current random round modifiers and apply them to the current round. |
| `!rollsim [rolls]` | Admin | Simulates that many single picks (default 10000) and prints how often each rarity tier came up - a quick check of the `Rarity` weights. |
| `!rollmode [player\|team\|game]` | Admin | How random rounds hand out modifiers (also `!rollmethod`): each player their own, one set per team, or one set for everyone. Saved to `config.jsonc`; with no argument it shows the current mode. |
| `!rollreload` | Admin | Reload `config.jsonc` from disk without restarting the plugin or resetting active modifiers. |
| `!rolldebug` | Admin | Toggle whether per-player random-round assignments are reported to admins in chat. |

**Roll modes** (`RollMode` in `config.jsonc`, or `!rollmode`; takes effect from the next roll):

- `Player` (default) - every player rolls their own modifier(s).
- `Team` - each team rolls one set that every teammate shares, with one rolled chance (e.g. Revive's %) per team. The two teams never get the same modifier.
- `Game` - one set for everyone on both teams, with one rolled chance. The only mode that also rolls ConVar-driven modifiers, which change server-wide settings.

**Rarity** (`Rarity` in `config.jsonc`; on by default). Every modifier sits in a CS case tier, and each roll picks a tier by its weight, then a modifier inside it:

| Tier | Weight | Modifiers |
| --- | --- | --- |
| ★ Gold | 4 | Wallhack, Butterfly Effect, Mimic, Conditional Invisibility |
| Covert | 12 | Juggernaut, Revive, Atomic Explosions, Jetpack, Kamikaze Chickens, Smoke Immunity, Vanish |
| Classified | 20 | Speedhack, Flanker, Recall, No Recoil, Infinite Ammo, Vampire, Saint, Master Zeus, Cluster Grenades, Suicide Bomber, Bunny Hop, Small Players |
| Restricted | 30 | More Damage, Regeneration, Hard Head, Steel Body, Poisonous Smoke, Walking Grenadier, Disarming Bullets, Flashing Bullets, Bounty, Heavy Boots, Swap On Death, Longer Flashes |
| Mil-Spec | 34 | Everything else, including ConVar `.cfg` modifiers |

A weight is the tier's share of every pick, however many modifiers it holds - Gold at 4 comes up for a player about once every 24 rounds. Move a modifier with `"Overrides": { "Drunk": "Gold" }`, set a weight to `0` to stop a tier rolling, or turn it all off with `"Enabled": false`. The custom HUD colours the reel, the winner and the card by tier (needs the current Workshop addon); the classic centre text and chat use the tier colours too. Servers upgrading from an older config get these defaults automatically, so rolls are weighted from the first round after the update.

`MinRandomRounds`/`MaxRandomRounds` have no dedicated chat command - set them in `config.jsonc`, or adjust them at runtime via `!rollmenu` (menu changes are runtime-only and revert to the config file on the next full plugin reload). `Wallhack` is a regular modifier (manage it like any other via `!rolltoggle Wallhack` or `!memodifier`), not a dedicated command.

## Installation

1. Build the plugin (or grab a prebuilt release zip):
   ```bash
   dotnet publish -c Release
   ```
2. Copy the published output (the `CSRoll` folder from `build/publish`, containing the DLL and `resources/` folder) into your CS2 server's:
   ```
   game/csgo/addons/swiftlys2/plugins/CSRoll/
   ```
   (i.e. the `SwiftlyS2/Plugins` folder for your server - the exact path depends on your SwiftlyS2 installation).
3. Restart the server, or use SwiftlyS2's plugin reload command if supported.
4. Tune behavior in the generated `config.jsonc` and `resources/translations/en.jsonc` files - both support editing without a rebuild (see comments inside each file for hot-reload behavior).
5. Set up the custom HUD:
   - Install [AddonsManager](https://github.com/SwiftlyS2-Plugins/AddonsManager).
   - Add the [CSRoll HUD Workshop addon](https://steamcommunity.com/sharedfiles/filedetails/?id=3791730390) to `addons/swiftlys2/configs/plugins/AddonsManager/config.jsonc`:
     ```jsonc
     "Main": {
       "Addons": [ "3791730390" ]
     }
     ```
   - Restart the server. Players download the addon on connect, and can switch back to the centre text with `!hud`.

   The HUD is on for everyone by default (`CustomHud.Mode: "Everyone"`). A player who doesn't have the addon sees no reveal at all, so on a server without AddonsManager either set `"Mode": "OptIn"` (players with the addon opt in with `!hud`) or `"Enabled": false`. The full HUD setup and options are in [`hud/README.md`](hud/README.md).

## Credits

CSRoll is a SwiftlyS2/C# reimplementation, inspired by CounterStrikeSharp game modifiers plugin:

- [CS2-GameModifiers-Plugin](https://github.com/vinicius-trev/CS2-GameModifiers-Plugin) by vinicius-trev

Built on the [SwiftlyS2](https://swiftlys2.net) plugin framework.
