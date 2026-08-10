> 🤖 **This plugin was created with [Claude AI](https://claude.ai).**

<div align="center">
  <h1><strong>CSRoll</strong></h1>
  <p>A chaos-mod style "game modifiers" plugin for CS2, built on <a href="https://swiftlys2.net">SwiftlyS2</a>.</p>
</div>

Players (or admins) roll random gameplay-altering modifiers - like Jetpack, Speedhack, MasterZeus, or Kamikaze - that apply for a round, a set number of rounds, or the whole match, either server-wide or scoped to individual players.

## Modifier List

| Modifier | Description |
| --- | --- |
| Cluster Grenades | Grenades spawn 1-4 mini grenades when they detonate (configurable) |
| Kamikaze | On death, drops 3 (configurable) grenades near your body that explode for 1.25x (configurable) HE damage |
| Conditional Invisibility | You are invisible while silent - any sound briefly reveals you |
| Full Invisibility | You are always invisible, knife only, can't buy or pick up weapons |
| Drunk | Left and right movement (A/D) is mirrored |
| Juggernaut | Max health is set to 300 |
| Random Health | Health is set to a random number |
| Flashing Bullets | Random chance for a bullet hit to blind the enemy |
| Disarming Bullets | Random chance to disarm an enemy hit by your bullets |
| Hard Head | Cannot be damaged by headshots - body damage only |
| Butterfingers | Weapons are dropped on missed shots |
| Boomerang Bullets | You take the damage from your missed shots - extra health to compensate |
| Steel Body | Can only be damaged by headshots or utility (HE/molotov) |
| More Damage | Damage dealt is increased by 33% |
| Revive | Random chance to survive lethal damage, shrinking with each revive |
| Small Players | You are 2x smaller, with 50 HP |
| Poisonous Smoke | Your thrown smokes deal damage to enemies standing in them, and grants a smoke grenade |
| Longer Flashes | Flash bang effect lasts 3x longer (configurable) |
| Chinese Grenades | Timers on flashes, HE's and smokes are randomized |
| Gay Smokes | Smoke colors are randomized |
| Swap On Death | You will swap places on kill |
| Swap On Hit | You will swap places on hit |
| Reset On Reload | Players are teleported to spawn on reload |
| Master Zeus | Zeus recharges much faster and hits at very long range, granted automatically on spawn |
| Smoke Immunity | Smokes are invisible |
| Vampire | You steal the damage you deal |
| Saint | Random chance for a kill to revive a dead teammate |
| Speedhack | You are really fast |
| Teleport On Reload | You are teleported to your spawn on reload |
| Teleport On Hit | You are teleported to your spawn on hit |
| One Per Reload | 1 bullet per reload |
| No Recoil | Weapons have no recoil |
| Wallhack | Free cheats, for free - VAC SAFE |
| Random Loadout | Buy menu is disabled - random main weapon, pistol and grenades (sometimes with armor) |
| Walking Grenadier | You can't shoot, but you've got UNLIMITED HE grenades |
| Heavy Boots | Movement speed is much slower, but grants armor+helmet and bonus health |
| Jetpack | Jumping is much higher, no fall damage - hold jump in the air to fire a fuel-limited jetpack thrust with boosted air-strafe |
| Bunny Hop | Hold jump to bunny-hop automatically, with no landing speed penalty |
| Infinite Ammo | All weapons go brrrrrr... |
| Atomic Explosions | HE Grenades deal much more damage |
| Increased Spread | Weapons have bad aim |
| Plant Anywhere | Bomb can be planted anywhere after a delay, bomb timer extended (both configurable) |
| Surf | Will config vars for surfing (server-wide) |
| Flanker | After a cooldown, press Inspect Weapon to teleport behind a random enemy |

Display names and descriptions are fully customizable via `resources/translations/en.jsonc`.

## Commands

All commands are chat commands (prefix with `!`).

| Command | Access | Description |
| --- | --- | --- |
| `!listmodifiers` | Everyone | Prints the name and description for each registered modifier. |
| `!listactivemodifiers` | Everyone | Prints the name and description for each active modifier. |
| `!rollhelp` | Everyone | Prints every available CSRoll command. |
| `!addmodifier <name>` | Admin | Add a modifier that will persist until the end of the game. |
| `!memodifier <name>` | Admin | Apply a modifier scoped to just yourself, without affecting anyone else. |
| `!togglemodifier <name>` | Admin | Enables/Disables a given modifier by name. |
| `!removemodifier <name>` | Admin | Remove an active modifier. |
| `!removemodifiers` | Admin | Clear / Remove all active modifiers. |
| `!disablemodifier <name>` | Admin | Deactivate a modifier and remove it from the registered pool so it can't be added/rolled again until modifiers are reloaded. |
| `!addrandommodifier` | Admin | Add a random modifier to be activated immediately. |
| `!addrandommodifiers <count>` | Admin | Add a random number of modifiers to be activated immediately. |
| `!randomrounds` | Admin | Toggle random rounds on/off. |
| `!minrandomrounds <min>` | Admin | Set the min number of random round modifiers to be active each round. |
| `!maxrandomrounds <max>` | Admin | Set the max number of random round modifiers to be active each round. |
| `!randomroundsreroll` | Admin | Re-roll the current random round modifiers and apply them to the current round. |
| `!reloadmodifiers` | Admin | Re-initialises all registered modifiers. (This will remove all active modifiers too) |
| `!reloadconfig` | Admin | Reload `config.jsonc` from disk without restarting the plugin or resetting active modifiers. |
| `!surf` | Admin | Enable/Disable the surf modifier. |
| `!wallhack` | Admin | Enable/Disable the wallhack modifier for all players. |
| `!debug` | Admin | Toggle whether per-player random-round assignments are reported to admins in chat. |

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

Requires [SwiftlyS2](https://swiftlys2.net) to be installed on your CS2 server.

## Credits

CSRoll is a from-scratch SwiftlyS2/C# reimplementation, inspired by and adapted from the original CounterStrikeSharp game modifiers concept:

- [CS2-GameModifiers-Plugin](https://github.com/vinicius-trev/CS2-GameModifiers-Plugin) by vinicius-trev

Built on the [SwiftlyS2](https://swiftlys2.net) plugin framework.
